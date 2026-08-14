using System.Diagnostics;
using MingSim.Agents.Audit;
using MingSim.Agents.Decision;
using MingSim.Agents.Providers;
using MingSim.Agents.Realtime;
using MingSim.Application.Host;
using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Intents;
using MingSim.Domain.Map;
using MingSim.Domain.Realtime;
using MingSim.Simulation.Realtime;

namespace MingSim.Agents.ContractTests;

/// <summary>
/// Wave 5A/5B 审计 P1-AGENT-04 + P1-AGENT-05 的契约测试：
/// - 预算原子预留（reserve/settle）：单次调用在发起前原子预留上限，不足即拒绝；
///   并发预留恰在上限时只有一个成功且总额精确；结算返还未用额度。
/// - AgentDecisionHost 世界版本语义：每角色提交成功后从权威状态重取版本再规划下一角色，
///   多角色不再因共享同一快照版本被 STATE_VERSION_CONFLICT 拒绝。
/// - 预留失败时世界不阻塞且不发起模型调用。
/// </summary>
internal static partial class Program
{
    /// <summary>
    /// P1-AGENT-04 原子性：两个并发预留恰好都申请等于上限的额度时，必须只有一个成功，
    /// 且记账总额精确等于上限——修复前 CanAfford+RecordUsage 检查与提交分离，
    /// 两个并发调用可能同时通过闸门造成总额超限。
    /// </summary>
    private static void ShouldAllowOnlyOneConcurrentReservationAtTheCap()
    {
        const int calls = 2;
        var budget = new ModelBudgetTracker(
            new ModelBudget(MaxTokens: 100, MaxCostMillis: long.MaxValue, CostPerTokenMillis: 0));
        var reservations = new ModelBudgetReservation[calls];
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, calls)
            .Select(index => Task.Run(() =>
            {
                gate.Wait();
                return budget.TryReserve(100, out reservations[index]);
            }))
            .ToArray();
        gate.Set();
        var results = Task.WhenAll(tasks).GetAwaiter().GetResult();

        var successCount = results.Count(success => success);
        Require(successCount == 1,
            $"两个并发预留恰在上限时必须恰好一个成功（实际 {successCount} 个）");
        Require(budget.SpentTokens == 100,
            $"并发预留后记账总额必须精确等于上限 100（实际 {budget.SpentTokens}）");
        Require(budget.SpentCostMillis == 0, "单价为 0 时金额记账必须为 0");
        Require(budget.IsExhausted, "预留成功后预算必须处于耗尽状态");
        var winner = reservations.FirstOrDefault(reservation => reservation.ReservedTokens > 0);
        Require(winner.ReservedTokens == 100, "成功预留必须登记完整的预留额度");
    }

    /// <summary>
    /// P1-AGENT-04 reserve/settle 语义：预留即原子提交；结算按实际用量修正，
    /// 未用额度返还、超额补记、全额返还（取消/未发起调用路径）都不回绕。
    /// </summary>
    private static void ShouldSettleReservationWithRefundOfUnusedQuota()
    {
        var budget = new ModelBudgetTracker(
            new ModelBudget(MaxTokens: 100, MaxCostMillis: long.MaxValue, CostPerTokenMillis: 1));

        Require(budget.TryReserve(100, out var reservation), "空闲预算预留 100 必须成功");
        Require(budget.SpentTokens == 100 && budget.SpentCostMillis == 100,
            "预留必须立即原子计入 token 与金额（检查与提交在同一临界区）");

        budget.Settle(reservation, 40);
        Require(budget.SpentTokens == 40 && budget.SpentCostMillis == 40,
            "结算实际 40 时未用 60 必须返还");

        // 全额返还（取消/未发起调用路径）：单独预留单独全额返还，不留残留。
        Require(budget.TryReserve(40, out var cancelled), "返还后剩余额度必须可再次预留");
        Require(budget.SpentTokens == 80 && budget.SpentCostMillis == 80,
            "再次预留必须立即原子计入");
        budget.Settle(cancelled, 0);
        Require(budget.SpentTokens == 40 && budget.SpentCostMillis == 40,
            "实际 0（取消/未发起调用）时预留必须全额返还，不留残留");

        // 超额补记（响应 token 多于预留）。
        Require(budget.TryReserve(30, out var overrun), "再次预留 30 必须成功");
        Require(budget.SpentTokens == 70, "预留 30 后总额必须为 70");
        budget.Settle(overrun, 50);
        Require(budget.SpentTokens == 90 && budget.SpentCostMillis == 90,
            "实际 50 超过预留 30 时必须补记差额 20，不得把总额算成 30");
    }

    /// <summary>
    /// P1-AGENT-05 世界版本语义：两位托管角色（都持粮运授权）同批决策时，
    /// 首位提交成功后宿主必须从权威状态重取版本再规划下一位——修复前两者共享同一
    /// 快照版本，内核受理首位后下一位必然 STATE_VERSION_CONFLICT。
    /// </summary>
    private static void ShouldSubmitTwoHostedAgentsWithoutVersionConflict()
    {
        var world = CreateTwoAgentWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var host = new AgentDecisionHost(runtime,
        [
            new HostedAgent(new CharacterId("duliaoxiang-slot"),
                new DecisionPlanner(new UtilityMinisterAgent(MinisterFocus.Logistics))),
            new HostedAgent(new CharacterId("hubu-slot"),
                new DecisionPlanner(new UtilityMinisterAgent(MinisterFocus.Logistics))),
        ]);
        var before = runtime.ReadModel;

        var batch = host.DecideAndSubmitAsync(world, world.GameTime).GetAwaiter().GetResult();

        Require(batch.Decisions.Count == 2, "两位托管角色都必须完成一轮决策");
        foreach (var decision in batch.Decisions)
        {
            Require(decision.Submissions.Single().Accepted, "每位角色的意图都必须通过入口预检进入收件箱");
            Require(decision.Submissions.Single().ErrorCode is null, "入口阶段不得出现任何错误码");
        }

        // 每角色提交成功后从权威状态重取版本：两位角色的幂等键必须按各自观察到的
        // 权威版本派生（修复前两者都基于同一快照版本，必然相同）。
        // 幂等键格式为 logistics-{route}-{quantity}-{version}（P1-AGENT-01 后由候选集派生）。
        var keys = batch.Decisions
            .Select(decision => ((PlanLogisticsIntent)decision.Intents.Single()).IdempotencyKey)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        Require(keys.Distinct(StringComparer.Ordinal).Count() == 2,
            "两个角色的幂等键必须按各自观察到的权威世界版本派生");
        Require(keys[0] == $"logistics-capital-ningyuan-grain-300-{before.WorldVersion}",
            "首位角色必须基于批次起始的权威版本");
        Require(keys[1] == $"logistics-capital-ningyuan-grain-300-{before.WorldVersion + 2}",
            "后位角色必须基于首位命令生效后的权威版本（受理+出发事件 = +2）");

        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.CommandResults.Count == 1,
            "宿主已逐角色生效首位命令，调用方安全点只需受理末位命令");
        Require(advanced.CommandResults.Single().Accepted, "末位角色的命令必须被内核受理");
        Require(advanced.CommandResults.SelectMany(result => result.Errors).All(error => error.Code != "STATE_VERSION_CONFLICT"),
            "任何命令都不得出现 STATE_VERSION_CONFLICT");
        Require(runtime.ReadModel.Shipments.Count == 2, "两位角色的粮运都必须真正生效");
        Require(runtime.ReadModel.WorldVersion == before.WorldVersion + 4,
            "两位角色各产生受理+出发两次原子提交（合计 +4）");
    }

    /// <summary>
    /// P1-AGENT-04 预留失败不阻塞世界：共享预算已耗尽时，两位托管角色的模型预留
    /// 都被原子拒绝——0 次模型调用、回退规则路径、意图仍提交且世界继续推进。
    /// </summary>
    private static void ShouldNotCallModelOrBlockWorldWhenReservationFails()
    {
        var world = CreateTwoAgentWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var budget = new ModelBudgetTracker(
            new ModelBudget(MaxTokens: 100, MaxCostMillis: long.MaxValue, CostPerTokenMillis: 0));
        budget.RecordUsage(100); // 预算已耗尽：任何新预留都被原子拒绝
        var audit = new ModelAuditLog();
        var modelJson = """{"schema_version":1,"intent_type":"logistics.request_shipment","parameters":{"route_id":"capital-ningyuan-grain","grain_quantity":200}}""";
        var providerA = new FakeModelProvider(modelJson);
        var providerB = new FakeModelProvider(modelJson);
        var host = new AgentDecisionHost(runtime,
        [
            new HostedAgent(new CharacterId("duliaoxiang-slot"),
                new DecisionPlanner(new UtilityMinisterAgent(MinisterFocus.Logistics), providerA, budget, audit)),
            new HostedAgent(new CharacterId("hubu-slot"),
                new DecisionPlanner(new UtilityMinisterAgent(MinisterFocus.Logistics), providerB, budget, audit)),
        ]);
        var before = runtime.ReadModel;

        var batch = host.DecideAndSubmitAsync(world, world.GameTime).GetAwaiter().GetResult();

        Require(providerA.CallCount == 0 && providerB.CallCount == 0,
            "预留失败时两位角色都不得发起任何模型调用");
        foreach (var decision in batch.Decisions)
        {
            Require(decision.Source == DecisionSource.Rules && decision.FallbackReason == ModelFallbackReason.BudgetExceeded,
                "预留失败必须回退规则路径并明确 BudgetExceeded 原因");
            Require(decision.Submissions.Single().Accepted, "规则回退意图必须经入口提交，世界不阻塞");
        }

        Require(audit.Entries.Count == 2 && audit.Entries.All(entry => entry.Outcome == ModelCallOutcome.BudgetExceeded),
            "每位角色必须记录一次 BudgetExceeded 审计");

        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.ReadModel.Shipments.Count == 2, "预留失败后世界必须继续推进（规则意图生效）");
    }

    /// <summary>
    /// 双托管角色测试世界：两位角色都持有 capital-ningyuan-grain 的粮运授权，
    /// 路线容量 1000（可容纳 2×300 石在途），源库存与目的地容量充足。
    /// </summary>
    private static WorldState CreateTwoAgentWorld()
    {
        var map = new MapDefinition(
            "two-agent-version-map",
            [
                new ProvinceDefinition(new ProvinceId("capital"), "京师", [new ProvinceId("liaodong")]),
                new ProvinceDefinition(new ProvinceId("liaodong"), "辽东", [new ProvinceId("capital")]),
            ]);
        return WorldState.CreateInitial(
            new WorldId("two-agent-version"),
            1,
            200_000,
            map,
            characters:
            [
                new CharacterState(new CharacterId("hubu-slot"), "户部尚书（职位槽位）",
                    new CharacterAttributes(50, 50, 50, 50, 50),
                    new CharacterPersonality(true, false, true, false)),
                new CharacterState(new CharacterId("duliaoxiang-slot"), "督辽饷承办人（职位槽位）",
                    new CharacterAttributes(50, 50, 50, 50, 50),
                    new CharacterPersonality(true, false, true, false)),
            ],
            capabilityGrants:
            [
                new CapabilityGrant(new CharacterId("hubu-slot"), GameCapability.PlanLogistics, "capital-ningyuan-grain"),
                new CapabilityGrant(new CharacterId("duliaoxiang-slot"), GameCapability.PlanLogistics, "capital-ningyuan-grain"),
            ],
            stockpiles:
            [
                new StockpileState(new StockpileId("capital-granary"), new ProvinceId("capital"), 2_000, 2_000),
                new StockpileState(new StockpileId("ningyuan-granary"), new ProvinceId("liaodong"), 1_000, 0),
            ],
            routes:
            [
                new RouteState(new RouteId("capital-ningyuan-grain"),
                    new StockpileId("capital-granary"), new StockpileId("ningyuan-granary"),
                    1_000, 2, 100),
            ]);
    }
}
