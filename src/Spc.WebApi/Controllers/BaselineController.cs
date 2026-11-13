using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Spc.Algorithm;
using Spc.Application;
using Spc.Domain;

namespace Spc.WebApi.Controllers;

/// <summary>方案 8.2 的 Phase I 基线接口：预检 / 冻结 / 审批发布 / 版本回放 / 能力分析。</summary>
[ApiController]
[Route("api/spc/baseline")]
public sealed class BaselineController(
    BaselineFreezeService freeze,
    BaselinePublishService publish,
    CapabilityService capability,
    ISpcRepository repository,
    ILimitReader limitReader) : ControllerBase
{
    /// <summary>六步流程第 2~3 步：试算试验控制限，列出 R1~R4 命中点供质量工程师登记特殊原因。</summary>
    [HttpPost("precheck")]
    public async Task<object> Precheck([FromBody] BaselineFreezeRequest request, CancellationToken ct)
        => await freeze.PrecheckAsync(request, ct);

    /// <summary>六步流程第 4~5 步：迭代收敛 + SHA256 指纹 + 生成 PENDING 版本（绝不自动激活）。</summary>
    [HttpPost("freeze")]
    public async Task<object> Freeze([FromBody] BaselineFreezeRequest request, CancellationToken ct)
        => await freeze.FreezeAsync(request, ct);

    /// <summary>六步流程第 6 步：人工审批发布，事务内切换 ACTIVE 版本。</summary>
    [HttpPost("publish")]
    public async Task<object> Publish([FromBody] BaselinePublishRequest request, CancellationToken ct)
        => await publish.PublishAsync(request, ct);

    [HttpGet("versions")]
    public async Task<object> Versions([FromQuery] long chartId, CancellationToken ct)
    {
        var versions = await repository.GetLimitVersionsAsync(chartId, ct);
        return new
        {
            items = versions.OrderBy(v => v.VersionNo),
            active = versions.FirstOrDefault(v => v.Status == (int)LimitVersionStatus.Active),
            stateMachine = "1 DRAFT -> 2 PENDING -> 3 ACTIVE -> 4 EXPIRED；旧版本只置 EXPIRED，永不删除"
        };
    }

    [HttpGet("excludes")]
    public object Excludes() => new
    {
        note = "剔除台账 SPC_BASELINE_EXCLUDE 随每次建限写入，字段含子组 ID、规则号、已确认特殊原因、确认人与时间"
    };

    /// <summary>按事件时间定位当时生效的版本 —— 历史图回放必须使用这一版，不能用当前 ACTIVE（方案 5.7）。</summary>
    [HttpGet("resolve")]
    public async Task<object> Resolve(
        [FromQuery] long chartId, [FromQuery] DateTime? eventTime, CancellationToken ct)
    {
        var at = eventTime ?? DateTime.UtcNow;
        var version = await repository.GetEffectiveLimitAsync(chartId, at, ct);
        return new
        {
            chartId,
            eventTime = at,
            version,
            resolved = version is not null,
            note = version is null
                ? "该时刻无生效版本：判异/回放应被拒绝，不得用近期数据现算控制限。"
                : "回放使用当时生效版本，即使该版本此后已 EXPIRED。"
        };
    }

    /// <summary>A-02 服务级可复现校验：用版本表记录的子组清单与原始值重算 SHA256 指纹。</summary>
    [HttpPost("verify-hash")]
    public async Task<object> VerifyHash([FromQuery] long versionId, CancellationToken ct)
    {
        var v = await repository.GetLimitVersionAsync(versionId, ct);
        if (v is null) return NotFound(new { error = "版本不存在" });

        var ids = JsonSerializer.Deserialize<long[]>(v.SubgroupIds) ?? [];
        var all = await repository.GetClosedSubgroupsInWindowAsync(
            v.ChartId, DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
            DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), ct);
        var selected = all.Where(s => ids.Contains(s.Id))
            .OrderBy(s => s.EventTimeTo).ThenBy(s => s.Seq).ToArray();
        var measures = await repository.GetMeasuresBySubgroupIdsAsync(
            v.ChartId, selected.Select(s => s.Id).ToArray(), ct);
        var byGroup = measures.Where(m => m.SubgroupId.HasValue)
            .GroupBy(m => m.SubgroupId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.EventTime).ThenBy(m => m.Id)
                .Select(m => m.MeasureValue).ToArray());

        var candidates = selected.Select(s => new BaselineCandidate(
            s.Id, s.Seq, DateTime.SpecifyKind(s.EventTimeTo, DateTimeKind.Utc),
            byGroup.TryGetValue(s.Id, out var values) ? values : [],
            null, null)).ToArray();

        var req = new BaselineRequest(
            (ChartType)v.ChartType, v.SubgroupSize, candidates,
            v.RuleSetVer, v.CalcParams, v.CreatedBy,
            DateTime.SpecifyKind(v.DataFrom, DateTimeKind.Utc),
            DateTime.SpecifyKind(v.DataTo, DateTimeKind.Utc));
        string recomputed = BaselineHasher.Compute(req, candidates);

        return new
        {
            versionId = v.Id,
            versionNo = v.VersionNo,
            storedHash = v.DataHash,
            recomputedHash = recomputed,
            reproducible = v.DataHash == recomputed,
            subgroupCount = candidates.Length,
            note = "指纹输入 = 子组 ID + 每个原始量测值 + 计算参数 + 数据窗口；一致即证明控制限可复现。"
        };
    }

    /// <summary>能力指数：σ̂ 来自冻结版本，过程未受控时拒绝输出 Cpk。</summary>
    [HttpGet("capability")]
    public async Task<object> Capability(
        [FromQuery] long chartId, [FromQuery] DateTime? eventTime, CancellationToken ct)
        => await capability.ComputeAsync(chartId, eventTime, ct);

    [HttpGet("contract")]
    public async Task<object> Contract([FromQuery] long chartId, CancellationToken ct) => new
    {
        description = await limitReader.DescribeAsync(chartId, ct),
        rule = "GetLimitsForEvaluation(chartId, eventTime) 只查 SPC_LIMIT_VERSION；任何判异路径不得调用 LimitCalculator"
    };
}
