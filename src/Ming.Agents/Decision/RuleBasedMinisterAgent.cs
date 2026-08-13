using MingSim.Agents.Runtime;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Intents;

namespace MingSim.Agents.Decision;

/// <summary>
/// 一个“规则式大臣”。
/// 这里不接入大模型（LLM），也不学习历史数据；
/// 它只按写死的规则，在每回合给出要做的动作意图（WorldIntent）。
/// </summary>
/// <remarks>
/// 这个实现适合新手理解流程：先把基础规则跑通，再考虑引入 AI 决策。
/// </remarks>
public sealed class RuleBasedMinisterAgent : IAgentDecisionSource
{
    /// <summary>大臣当前专注方向：工业 or 军事。</summary>
    private readonly MinisterFocus _focus;

    /// <summary>
    /// 创建一个大臣代理，指定它专注的方向。
    /// </summary>
    /// <param name="focus">行业型/军事实验型策略开关。</param>
    public RuleBasedMinisterAgent(MinisterFocus focus)
    {
        _focus = focus;
    }

    /// <summary>
    /// 根据上下文决定本回合要提交的动作。
    /// 返回的是“意图列表”，真正执行权限和资源扣减会在仿真层校验。
    /// </summary>
    public IReadOnlyList<WorldIntent> Decide(AgentContext context)
    {
        return _focus switch
        {
            MinisterFocus.Industry => DecideIndustry(context),
            MinisterFocus.Military => DecideMilitary(context),
            _ => [],
        };
    }

    /// <summary>
    /// 工业导向策略：
    /// 条件满足时，尝试建一座火铳工坊（仅一座）。
    /// </summary>
    private static IReadOnlyList<WorldIntent> DecideIndustry(AgentContext context)
    {
        // 1) 没有建厂能力 -> 不下令
        // 2) 已经已有设施 -> 保持谨慎，不重复建设
        // 3) 银两不足 50_000 -> 不下不能成功的指令
        if (!context.Capabilities.Contains(GameCapability.BuildIndustry) ||
            context.FacilityCount > 0 ||
            context.TreasurySilver < 50_000)
        {
            return [];
        }

        // 返回一个“建工厂”意图：对象、位置、花费、产能和工人数量都写死为示例参数。
        // 注意：这是规则代理，目的是示范流程，不是最优决策。
        return
        [
            new BuildFacilityIntent(
                "agent-build-first-flintlock-workshop", // 意图ID（本回合动作唯一标识）
                context.ActorId,                        // 当前决策的大臣角色
                context.TurnNumber,                    // 目标回合
                "turn-1-build-first-flintlock-workshop", // 幂等键，避免重复提交同一动作
                new FacilityId("factory-capital-flintlock-01"), // 设施ID
                new ProvinceId("capital"),              // 建设地点
                FacilityType.FlintlockWorkshop,         // 设施类型
                Budget: 50_000,                        // 预算
                BaseCapacity: 800,                     // 基础产能
                Workforce: 80),                        // 配备工人
        ];
    }

    /// <summary>
    /// 军事导向策略：
    /// 找到一支辅助兵至少 1000 的军队，把 1000 人转为正兵。
    /// </summary>
    private static IReadOnlyList<WorldIntent> DecideMilitary(AgentContext context)
    {
        // 没有“转换军种”能力，直接不做动作。
        if (!context.Capabilities.Contains(GameCapability.ConvertArmy))
        {
            return [];
        }

        // 找第一支可转化军队（辅助兵 >= 1000）
        var army = context.Armies.FirstOrDefault(candidate => candidate.Auxiliaries >= 1_000);
        if (army is null)
        {
            return [];
        }

        // 返回“转化军队”意图：把该军队的 1000 辅助兵转为线列步兵。
        return
        [
            new ConvertArmyIntent(
                "agent-convert-frontier-1000",  // 意图ID
                context.ActorId,                // 当前决策角色
                context.TurnNumber,            // 目标回合
                "turn-1-convert-frontier-1000",// 幂等键
                army.ArmyId,                   // 目标军队
                Count: 1_000),                 // 转换数量
        ];
    }
}

