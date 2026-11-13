using Spc.Application;
using Spc.Domain;
using SqlSugar;
using System.Text.Json;

namespace Spc.Infrastructure;

/// <summary>
/// 仓储实现。M5 引入第三个后台 Worker 后发现：多个 Worker 并发访问同一个 SqlSugarScope
/// 会让 SQLite 的 DataReader / 实体映射缓存串扰（表现为 "SpcChart UpdatedAt bind error"），
/// 并可能触发 Microsoft.Data.Sqlite 的 prepared statements 并发修改异常。
/// 因此所有 DB 操作统一经过进程级闸门串行化；生产切 Oracle 后可改为按连接隔离以恢复并行度。
/// </summary>
public sealed class SpcRepository(SqlSugarScope db) : ISpcRepository
{
    private readonly SemaphoreSlim _dbGate = new(1, 1);
    /// <summary>发布事务额外闸门：避免事务上下文串扰（M2 已验证的并发缺陷）。</summary>
    private readonly SemaphoreSlim _publishGate = new(1, 1);

    private async Task<T> Gate<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _dbGate.WaitAsync(ct);
        try { return await action(); }
        finally { _dbGate.Release(); }
    }

    private async Task Gate(Func<Task> action, CancellationToken ct)
    {
        await _dbGate.WaitAsync(ct);
        try { await action(); }
        finally { _dbGate.Release(); }
    }

    public Task<IReadOnlyList<SpcChart>> GetChartsAsync(CancellationToken ct)
        => Gate<IReadOnlyList<SpcChart>>(async () => await db.Queryable<SpcChart>().ToListAsync(), ct);

    public Task<SpcChart?> GetChartAsync(long chartId, CancellationToken ct)
        => Gate<SpcChart?>(async () => await db.Queryable<SpcChart>().FirstAsync(c => c.Id == chartId), ct);

    public Task<SpcChart?> GetChartByCodeAsync(string code, CancellationToken ct)
        => Gate<SpcChart?>(async () => await db.Queryable<SpcChart>().FirstAsync(c => c.Code == code), ct);

    public async Task<long> InsertChartAsync(SpcChart chart, CancellationToken ct)
        => await Gate<long>(async () =>
        {
            chart.Id = await db.Insertable(chart).ExecuteReturnBigIdentityAsync();
            return chart.Id;
        }, ct);

    public Task UpdateChartAsync(SpcChart chart, CancellationToken ct)
        => Gate(async () => { await db.Updateable(chart).ExecuteCommandAsync(); }, ct);

    public Task<bool> DedupKeyExistsAsync(string dedupKey, CancellationToken ct)
        => Gate<bool>(async () => await db.Queryable<SpcMeasure>().AnyAsync(m => m.DedupKey == dedupKey), ct);

    public async Task<long> InsertMeasureAsync(SpcMeasure measure, CancellationToken ct)
        => await Gate<long>(async () =>
        {
            measure.Id = await db.Insertable(measure).ExecuteReturnBigIdentityAsync();
            return measure.Id;
        }, ct);

    public Task<IReadOnlyList<SpcMeasure>> GetUnassignedMeasuresAsync(
        long chartId, DateTime watermark, CancellationToken ct)
        => Gate<IReadOnlyList<SpcMeasure>>(async () => await db.Queryable<SpcMeasure>()
            .Where(m => m.ChartId == chartId && m.SubgroupId == null && m.EventTime <= watermark)
            .ToListAsync(), ct);

    public Task AssignSubgroupAsync(IReadOnlyList<long> measureIds, long subgroupId, CancellationToken ct)
        => Gate(async () =>
        {
            await db.Updateable<SpcMeasure>()
                .SetColumns(m => m.SubgroupId == subgroupId)
                .Where(m => measureIds.Contains(m.Id))
                .ExecuteCommandAsync();
        }, ct);

    public async Task<SpcSubgroup?> GetLastSubgroupAsync(long chartId, CancellationToken ct)
        => await Gate<SpcSubgroup?>(async () =>
        {
            var all = await db.Queryable<SpcSubgroup>().Where(s => s.ChartId == chartId).ToListAsync();
            return all.OrderByDescending(s => s.Seq).FirstOrDefault();
        }, ct);

    public async Task<long> InsertSubgroupAsync(SpcSubgroup subgroup, CancellationToken ct)
        => await Gate<long>(async () =>
        {
            subgroup.Id = await db.Insertable(subgroup).ExecuteReturnBigIdentityAsync();
            return subgroup.Id;
        }, ct);

    public async Task<IReadOnlyList<SpcSubgroup>> GetSubgroupsAsync(long chartId, int take, CancellationToken ct)
        => await Gate<IReadOnlyList<SpcSubgroup>>(async () =>
        {
            var all = await db.Queryable<SpcSubgroup>().Where(s => s.ChartId == chartId).ToListAsync();
            return all.OrderBy(s => s.Seq).TakeLast(take).ToArray();
        }, ct);

    public async Task<IReadOnlyList<SpcMeasure>> GetMeasuresAsync(long chartId, int take, CancellationToken ct)
        => await Gate<IReadOnlyList<SpcMeasure>>(async () =>
        {
            var all = await db.Queryable<SpcMeasure>().Where(m => m.ChartId == chartId).ToListAsync();
            return all.OrderByDescending(m => m.EventTime).ThenByDescending(m => m.Id).Take(take).ToArray();
        }, ct);

    public Task<long> CountMeasuresAsync(long chartId, CancellationToken ct)
        => Gate<long>(async () => await db.Queryable<SpcMeasure>().CountAsync(m => m.ChartId == chartId), ct);

    public Task<long> CountSubgroupsAsync(long chartId, CancellationToken ct)
        => Gate<long>(async () => await db.Queryable<SpcSubgroup>().CountAsync(s => s.ChartId == chartId), ct);

    public async Task<long> InsertAuditAsync(SpcAuditLog log, CancellationToken ct)
        => await Gate<long>(async () =>
        {
            log.Id = await db.Insertable(log).ExecuteReturnBigIdentityAsync();
            return log.Id;
        }, ct);

    // ---------------- M2：Phase I 基线冻结 ----------------

    public Task<IReadOnlyList<SpcSubgroup>> GetClosedSubgroupsInWindowAsync(
        long chartId, DateTime from, DateTime to, CancellationToken ct)
        => Gate<IReadOnlyList<SpcSubgroup>>(async () => await db.Queryable<SpcSubgroup>()
            .Where(s => s.ChartId == chartId && s.Status >= (int)SubgroupStatus.Closed &&
                        s.EventTimeTo >= from && s.EventTimeTo <= to)
            .ToListAsync(), ct);

    public async Task<IReadOnlyList<SpcMeasure>> GetMeasuresBySubgroupIdsAsync(
        long chartId, IReadOnlyList<long> subgroupIds, CancellationToken ct)
    {
        if (subgroupIds.Count == 0) return [];
        return await Gate<IReadOnlyList<SpcMeasure>>(async () =>
        {
            var set = subgroupIds.ToHashSet();
            var all = await db.Queryable<SpcMeasure>()
                .Where(m => m.ChartId == chartId && m.SubgroupId != null)
                .ToListAsync();
            return all.Where(m => m.SubgroupId.HasValue && set.Contains(m.SubgroupId.Value)).ToArray();
        }, ct);
    }

    public async Task<long> GetMaxVersionNoAsync(long chartId, CancellationToken ct)
        => await Gate<long>(async () =>
        {
            var all = await db.Queryable<SpcLimitVersion>().Where(v => v.ChartId == chartId).ToListAsync();
            return all.Count == 0 ? 0L : all.Max(v => v.VersionNo);
        }, ct);

    public async Task<long> InsertLimitVersionAsync(SpcLimitVersion version, CancellationToken ct)
        => await Gate<long>(async () =>
        {
            version.Id = await db.Insertable(version).ExecuteReturnBigIdentityAsync();
            return version.Id;
        }, ct);

    public async Task InsertBaselineExcludesAsync(IReadOnlyList<SpcBaselineExclude> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        await Gate(async () => { await db.Insertable(rows.ToList()).ExecuteCommandAsync(); }, ct);
    }

    public Task<SpcLimitVersion?> GetLimitVersionAsync(long versionId, CancellationToken ct)
        => Gate<SpcLimitVersion?>(async () => await db.Queryable<SpcLimitVersion>().FirstAsync(v => v.Id == versionId), ct);

    public Task<SpcLimitVersion?> GetActiveLimitAsync(long chartId, CancellationToken ct)
        => Gate<SpcLimitVersion?>(async () => await db.Queryable<SpcLimitVersion>()
            .FirstAsync(v => v.ChartId == chartId && v.Status == (int)LimitVersionStatus.Active), ct);

    /// <summary>历史回放：按 EFFECTIVE_FROM / EXPIRED_TIME 定位事件当时生效的版本（方案 5.7）。</summary>
    public async Task<SpcLimitVersion?> GetEffectiveLimitAsync(long chartId, DateTime eventTime, CancellationToken ct)
        => await Gate<SpcLimitVersion?>(async () =>
        {
            var all = await db.Queryable<SpcLimitVersion>()
                .Where(v => v.ChartId == chartId && (v.Status == (int)LimitVersionStatus.Active ||
                                                     v.Status == (int)LimitVersionStatus.Expired))
                .ToListAsync();
            return all
                .Where(v => v.EffectiveFrom.HasValue && v.EffectiveFrom.Value <= eventTime &&
                            (!v.ExpiredTime.HasValue || v.ExpiredTime.Value > eventTime))
                .OrderByDescending(v => v.EffectiveFrom)
                .FirstOrDefault();
        }, ct);

    public Task<IReadOnlyList<SpcLimitVersion>> GetLimitVersionsAsync(long chartId, CancellationToken ct)
        => Gate<IReadOnlyList<SpcLimitVersion>>(async () =>
            await db.Queryable<SpcLimitVersion>().Where(v => v.ChartId == chartId).ToListAsync(), ct);

    /// <summary>
    /// 审批发布事务（方案 5.6 / N-05）。仅更新生命周期字段，CL/UCL/LCL 等计算字段永不修改。
    /// 事务期间独占 DB 闸门；唯一 ACTIVE 索引兜底；任何异常都回滚。
    /// </summary>
    public async Task<LimitPublishOutcome> PublishVersionAsync(
        long chartId, long versionId, string approvedBy,
        DateTime effectiveFrom, string changeReason, CancellationToken ct)
    {
        await _publishGate.WaitAsync(ct);
        await _dbGate.WaitAsync(ct);
        try
        {
            db.Ado.BeginTran();

            var version = await db.Queryable<SpcLimitVersion>().FirstAsync(v => v.Id == versionId);
            if (version is null || version.ChartId != chartId ||
                version.Status != (int)LimitVersionStatus.Pending)
            {
                db.Ado.RollbackTran();
                return new LimitPublishOutcome(false,
                    "版本不是可发布的 PENDING 状态（可能已被其它审批抢先发布）。", versionId, null, string.Empty);
            }

            var actives = await db.Queryable<SpcLimitVersion>()
                .Where(v => v.ChartId == chartId && v.Status == (int)LimitVersionStatus.Active)
                .ToListAsync();

            long? expiredId = null;
            foreach (var old in actives)
            {
                await db.Updateable<SpcLimitVersion>()
                    .SetColumns(v => new SpcLimitVersion
                    {
                        Status = (int)LimitVersionStatus.Expired,
                        ExpiredTime = effectiveFrom
                    })
                    .Where(v => v.Id == old.Id)
                    .ExecuteCommandAsync();
                expiredId = old.Id;

                await db.Insertable(new SpcAuditLog
                {
                    EventTime = effectiveFrom,
                    EventType = "BASELINE_EXPIRED",
                    EntityType = nameof(SpcLimitVersion),
                    EntityId = old.Id,
                    ChartId = chartId,
                    Payload = JsonSerializer.Serialize(new { old.VersionNo, ExpiredTime = effectiveFrom, NewVersionId = versionId }),
                    Actor = approvedBy,
                    CreatedAt = DateTime.UtcNow
                }).ExecuteCommandAsync();
            }

            await db.Updateable<SpcLimitVersion>()
                .SetColumns(v => new SpcLimitVersion
                {
                    Status = (int)LimitVersionStatus.Active,
                    ApprovedBy = approvedBy,
                    EffectiveFrom = effectiveFrom
                })
                .Where(v => v.Id == versionId)
                .ExecuteCommandAsync();

            await db.Insertable(new SpcAuditLog
            {
                EventTime = effectiveFrom,
                EventType = "BASELINE_PUBLISHED",
                EntityType = nameof(SpcLimitVersion),
                EntityId = versionId,
                ChartId = chartId,
                Payload = JsonSerializer.Serialize(new
                {
                    version.VersionNo, version.DataHash, version.Cl, version.Ucl, version.Lcl,
                    version.SigmaHat, approvedBy, effectiveFrom, changeReason, ExpiredVersionId = expiredId
                }),
                Actor = approvedBy,
                CreatedAt = DateTime.UtcNow
            }).ExecuteCommandAsync();

            db.Ado.CommitTran();
            return new LimitPublishOutcome(true, string.Empty, versionId, expiredId, version.DataHash);
        }
        catch (Exception ex)
        {
            try { db.Ado.RollbackTran(); } catch { /* 事务已失效 */ }
            return new LimitPublishOutcome(false,
                $"发布事务失败并已回滚（并发冲突或唯一 ACTIVE 索引拦截）：{ex.Message}",
                versionId, null, string.Empty);
        }
        finally
        {
            _dbGate.Release();
            _publishGate.Release();
        }
    }

    // ---------------- M3：Phase II 实时判异 ----------------

    public Task<SpcSubgroup?> GetSubgroupAsync(long subgroupId, CancellationToken ct)
        => Gate<SpcSubgroup?>(async () => await db.Queryable<SpcSubgroup>().FirstAsync(s => s.Id == subgroupId), ct);

    /// <summary>规则窗口只取同一冻结版本生效区间内的子组，换版后窗口重置（方案 5.7）。</summary>
    public async Task<IReadOnlyList<SpcSubgroup>> GetSubgroupsInVersionWindowAsync(
        long chartId, DateTime from, DateTime? to, CancellationToken ct)
        => await Gate<IReadOnlyList<SpcSubgroup>>(async () =>
        {
            var query = db.Queryable<SpcSubgroup>()
                .Where(s => s.ChartId == chartId && s.EventTimeTo >= from);
            if (to.HasValue) query = query.Where(s => s.EventTimeTo < to.Value);
            var all = await query.ToListAsync();
            return all.OrderBy(s => s.Seq).ThenBy(s => s.EventTimeTo).ToArray();
        }, ct);

    public async Task<IReadOnlyList<SpcSubgroup>> GetUnevaluatedSubgroupsAsync(
        long chartId, int take, CancellationToken ct)
        => await Gate<IReadOnlyList<SpcSubgroup>>(async () =>
        {
            var all = await db.Queryable<SpcSubgroup>()
                .Where(s => s.ChartId == chartId && s.Status == (int)SubgroupStatus.Closed)
                .ToListAsync();
            return all.OrderBy(s => s.Seq).Take(take).ToArray();
        }, ct);

    public Task MarkSubgroupEvaluatedAsync(long subgroupId, long limitVerId, CancellationToken ct)
        => Gate(async () =>
        {
            await db.Updateable<SpcSubgroup>()
                .SetColumns(s => new SpcSubgroup
                {
                    Status = (int)SubgroupStatus.Evaluated,
                    LimitVerId = limitVerId
                })
                .Where(s => s.Id == subgroupId)
                .ExecuteCommandAsync();
        }, ct);

    public async Task<long> InsertRuleOutAsync(SpcRuleOut ruleOut, CancellationToken ct)
        => await Gate<long>(async () =>
        {
            ruleOut.Id = await db.Insertable(ruleOut).ExecuteReturnBigIdentityAsync();
            return ruleOut.Id;
        }, ct);

    public Task<SpcRuleOut?> GetRuleOutAsync(long ruleOutId, CancellationToken ct)
        => Gate<SpcRuleOut?>(async () => await db.Queryable<SpcRuleOut>().FirstAsync(r => r.Id == ruleOutId), ct);

    public async Task<IReadOnlyList<SpcRuleOut>> GetRuleOutsAsync(long chartId, int take, CancellationToken ct)
        => await Gate<IReadOnlyList<SpcRuleOut>>(async () =>
        {
            var all = await db.Queryable<SpcRuleOut>().Where(r => r.ChartId == chartId).ToListAsync();
            return all.OrderByDescending(r => r.EventTime).ThenByDescending(r => r.Id).Take(take).ToArray();
        }, ct);

    public Task<IReadOnlyList<SpcRuleOut>> GetRuleOutsBySubgroupAsync(long subgroupId, CancellationToken ct)
        => Gate<IReadOnlyList<SpcRuleOut>>(async () =>
            await db.Queryable<SpcRuleOut>().Where(r => r.SubgroupId == subgroupId).ToListAsync(), ct);

    // ---------------- M4：OCAP + MES Hold 处置闭环 ----------------

    public async Task<long> InsertOcapAsync(SpcOcap ocap, CancellationToken ct)
        => await Gate<long>(async () =>
        {
            ocap.Id = await db.Insertable(ocap).ExecuteReturnBigIdentityAsync();
            return ocap.Id;
        }, ct);

    public Task UpdateOcapAsync(SpcOcap ocap, CancellationToken ct)
        => Gate(async () => { await db.Updateable(ocap).ExecuteCommandAsync(); }, ct);

    public Task<SpcOcap?> GetOcapAsync(long ocapId, CancellationToken ct)
        => Gate<SpcOcap?>(async () => await db.Queryable<SpcOcap>().FirstAsync(o => o.Id == ocapId), ct);

    public Task<SpcOcap?> GetOcapByRuleOutAsync(long ruleOutId, CancellationToken ct)
        => Gate<SpcOcap?>(async () => await db.Queryable<SpcOcap>().FirstAsync(o => o.RuleOutId == ruleOutId), ct);

    public async Task<IReadOnlyList<SpcOcap>> GetOcapsAsync(long chartId, int take, CancellationToken ct)
        => await Gate<IReadOnlyList<SpcOcap>>(async () =>
        {
            var ids = (await db.Queryable<SpcRuleOut>().Where(r => r.ChartId == chartId).ToListAsync())
                .Select(r => r.Id).ToHashSet();
            var all = await db.Queryable<SpcOcap>().ToListAsync();
            return all.Where(o => ids.Contains(o.RuleOutId))
                .OrderByDescending(o => o.Id).Take(take).ToArray();
        }, ct);

    public async Task<long> InsertMesActionAsync(SpcMesAction action, CancellationToken ct)
        => await Gate<long>(async () =>
        {
            action.Id = await db.Insertable(action).ExecuteReturnBigIdentityAsync();
            return action.Id;
        }, ct);

    public Task UpdateMesActionAsync(SpcMesAction action, CancellationToken ct)
        => Gate(async () => { await db.Updateable(action).ExecuteCommandAsync(); }, ct);

    public Task<SpcMesAction?> GetMesActionByRefAsync(string mesRef, CancellationToken ct)
        => Gate<SpcMesAction?>(async () => await db.Queryable<SpcMesAction>().FirstAsync(a => a.MesRef == mesRef), ct);

    public Task<IReadOnlyList<SpcMesAction>> GetMesActionsByRuleOutAsync(long ruleOutId, CancellationToken ct)
        => Gate<IReadOnlyList<SpcMesAction>>(async () =>
            await db.Queryable<SpcMesAction>().Where(a => a.RuleOutId == ruleOutId).ToListAsync(), ct);

    public async Task<IReadOnlyList<SpcMesAction>> GetMesActionsAsync(long chartId, int take, CancellationToken ct)
        => await Gate<IReadOnlyList<SpcMesAction>>(async () =>
        {
            var ids = (await db.Queryable<SpcRuleOut>().Where(r => r.ChartId == chartId).ToListAsync())
                .Select(r => r.Id).ToHashSet();
            var all = await db.Queryable<SpcMesAction>().ToListAsync();
            return all.Where(a => ids.Contains(a.RuleOutId))
                .OrderByDescending(a => a.Id).Take(take).ToArray();
        }, ct);

    public Task UpdateRuleOutOcapStatusAsync(long ruleOutId, int status, CancellationToken ct)
        => Gate(async () =>
        {
            await db.Updateable<SpcRuleOut>()
                .SetColumns(r => r.OcapStatus == status)
                .Where(r => r.Id == ruleOutId)
                .ExecuteCommandAsync();
        }, ct);

    public Task<int> CountRecentRuleOutsAsync(long chartId, DateTime since, CancellationToken ct)
        => Gate<int>(async () => await db.Queryable<SpcRuleOut>()
            .CountAsync(r => r.ChartId == chartId && r.EventTime >= since), ct);

    /// <summary>A-08 审计追溯：取该图审计流，可附加实体 ID 过滤（含 payload 中的 RuleOutId）。</summary>
    public async Task<IReadOnlyList<SpcAuditLog>> GetAuditLogsAsync(
        long chartId, long? entityId, int take, CancellationToken ct)
        => await Gate<IReadOnlyList<SpcAuditLog>>(async () =>
        {
            var all = await db.Queryable<SpcAuditLog>()
                .Where(a => a.ChartId == chartId).ToListAsync();
            return all
                .Where(a => !entityId.HasValue ||
                            a.EntityId == entityId.Value ||
                            a.Payload.Contains($"\"RuleOutId\":{entityId.Value}", StringComparison.Ordinal))
                .OrderBy(a => a.EventTime).ThenBy(a => a.Id)
                .TakeLast(take).ToArray();
        }, ct);

    // ---------------- M5 报表中心与合规取证 ----------------

    public Task<IReadOnlyList<SpcMeasure>> GetMeasuresInWindowAsync(
        long chartId, DateTime from, DateTime to, CancellationToken ct)
        => Gate<IReadOnlyList<SpcMeasure>>(async () => await db.Queryable<SpcMeasure>()
            .Where(m => m.ChartId == chartId && m.EventTime >= from && m.EventTime <= to)
            .ToListAsync(), ct);

    public Task<IReadOnlyList<SpcRuleOut>> GetRuleOutsInWindowAsync(
        long chartId, DateTime from, DateTime to, CancellationToken ct)
        => Gate<IReadOnlyList<SpcRuleOut>>(async () => await db.Queryable<SpcRuleOut>()
            .Where(r => r.ChartId == chartId && r.EventTime >= from && r.EventTime <= to)
            .ToListAsync(), ct);

    public Task<IReadOnlyList<SpcBaselineExclude>> GetBaselineExcludesAsync(long chartId, CancellationToken ct)
        => Gate<IReadOnlyList<SpcBaselineExclude>>(async () =>
            await db.Queryable<SpcBaselineExclude>().Where(e => e.ChartId == chartId).ToListAsync(), ct);

    public async Task<IReadOnlyList<SpcAuditLog>> GetAuditLogsInWindowAsync(
        long chartId, DateTime from, DateTime to, int take, CancellationToken ct)
        => await Gate<IReadOnlyList<SpcAuditLog>>(async () =>
        {
            var all = await db.Queryable<SpcAuditLog>()
                .Where(a => a.ChartId == chartId && a.EventTime >= from && a.EventTime <= to)
                .ToListAsync();
            return all.OrderBy(a => a.EventTime).ThenBy(a => a.Id).TakeLast(take).ToArray();
        }, ct);

    public async Task<long> InsertReportSnapshotAsync(SpcReportSnapshot snapshot, CancellationToken ct)
        => await Gate<long>(async () =>
        {
            snapshot.Id = await db.Insertable(snapshot).ExecuteReturnBigIdentityAsync();
            return snapshot.Id;
        }, ct);

    public async Task<IReadOnlyList<SpcReportSnapshot>> GetReportSnapshotsAsync(
        long chartId, int take, CancellationToken ct)
        => await Gate<IReadOnlyList<SpcReportSnapshot>>(async () =>
        {
            var all = await db.Queryable<SpcReportSnapshot>()
                .Where(s => s.ChartId == chartId).ToListAsync();
            return all.OrderByDescending(s => s.GeneratedAt).ThenByDescending(s => s.Id)
                .Take(take).ToArray();
        }, ct);
}
