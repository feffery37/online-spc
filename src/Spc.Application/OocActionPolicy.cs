using Spc.Domain;

namespace Spc.Application;

public sealed record ActionDecision(
    IReadOnlyList<MesActionType> Actions,
    string Reason,
    int RecentCount);

/// <summary>
/// OOC Action 策略（方案 6.3）：按命中规则、严重级别与同机台近期判异次数决定 MES 侧动作。
/// 策略与判异解耦，可按图/按规则集替换，不影响控制限与规则引擎。
/// </summary>
public static class OocActionPolicy
{
    public const int RecentWindowMinutes = 30;
    public const int EqpHoldThreshold = 3;

    public static ActionDecision Decide(
        RuleCode rule, Severity severity, int recentRuleOutCount, bool hasLot)
    {
        var actions = new List<MesActionType>();
        var reasons = new List<string>();

        // R1 或高等级连续规则命中 -> LOT Hold（冻结当前批次，禁止流转）
        bool highGrade = rule is RuleCode.R1 or RuleCode.R2 or RuleCode.R3 or RuleCode.R8;
        if (hasLot && (highGrade || severity >= Severity.Critical))
        {
            actions.Add(MesActionType.LotHold);
            reasons.Add($"命中 {rule.Code()} / {RuleSetCatalog.SeverityLabel(severity)}，冻结批次");
        }

        // 同一机台连续多次判异 -> EQP Hold（锁定设备，禁止继续投料）
        if (recentRuleOutCount >= EqpHoldThreshold)
        {
            actions.Add(MesActionType.EqpHold);
            reasons.Add($"近 {RecentWindowMinutes} 分钟内同一控制图已累计 {recentRuleOutCount} 次判异");
        }

        // 严重超限 -> 停机
        if (rule == RuleCode.R1 && severity >= Severity.Critical)
        {
            actions.Add(MesActionType.StopLine);
            reasons.Add("严重超限，停线并通知班组长与质量工程师");
        }

        // 所有 Rule Out 都要有通知记录（签收留痕）
        actions.Add(MesActionType.Notify);
        reasons.Add("分级通知质量工程师");

        return new ActionDecision(actions, string.Join("；", reasons), recentRuleOutCount);
    }
}
