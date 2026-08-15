using System.Diagnostics;
using MingSim.Agents.Audit;
using MingSim.Agents.Decision;
using MingSim.Agents.Providers;
using MingSim.Agents.Realtime;
using MingSim.Agents.Runtime;
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
/// M6 组合根接线 + #27 审查 P2 修复的契约测试：
/// - AgentDecisionHost 端到端（fake Provider 产出 Intent → 入口提交 → 内核生效）；
/// - 预算耗尽 / Provider 超时经宿主回退 Utility 仍产出并提交意图；
/// - ModelBudgetTracker / ModelAuditLog 并发护栏（P2-2）与审计容量截断（P2-4）；
/// - MaxCostMillis / 饱和 / 边界测试（P2-6）；解析器 try 回退（P2-1）。
/// </summary>
internal static partial class Program
{
    /// <summary>
    /// 组合根接线端到端：三名关键人物（zhu-youjian、hubu-slot、duliaoxiang-slot）全部经
    /// AgentDecisionHost 走"模型增强→结构化 Intent→入口提交"管线；fake Provider 为所有人
    /// 产出白名单粮运意图，只有持 PlanLogistics 授权的 duliaoxiang-slot 提交生效，
    /// 其余两人在入口被 TOOL_SCOPE_DENIED 结构化拒绝（模型伪造越权不能绕过权限红线）。
    /// </summary>
    private static void ShouldRunHostEndToEndThroughKernel()
    {
        var world = CreateHostScenarioWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var modelJson = """{"schema_version":1,"intent_type":"logistics.request_shipment","parameters":{"route_id":"capital-ningyuan-grain","grain_quantity":200}}""";
        var host = new AgentDecisionHost(runtime,
        [
            new HostedAgent(new CharacterId("zhu-youjian"),
                new DecisionPlanner(new UtilityMinisterAgent(MinisterFocus.Logistics), new FakeModelProvider(modelJson))),
            new HostedAgent(new CharacterId("hubu-slot"),
                new DecisionPlanner(new UtilityMinisterAgent(MinisterFocus.Logistics), new FakeModelProvider(modelJson))),
            new HostedAgent(new CharacterId("duliaoxiang-slot"),
                new DecisionPlanner(new UtilityMinisterAgent(MinisterFocus.Logistics), new FakeModelProvider(modelJson))),
        ]);
        var before = runtime.ReadModel;

        var batch = host.DecideAndSubmitAsync(world, world.GameTime).GetAwaiter().GetResult();

        Require(batch.Decisions.Count == 3, "宿主必须为全部三名托管角色完成一轮决策");
        Require(batch.AcceptedGameTime == world.GameTime, "批次必须记录决策被接受时的权威游戏时刻");

        var duliaoxiang = batch.Decisions.Single(decision => decision.ActorId.Value == "duliaoxiang-slot");
        Require(duliaoxiang.Source == DecisionSource.Model, "持授权角色的模型意图必须被采用");
        var duliaoxiangSubmit = duliaoxiang.Submissions.Single();
        Require(duliaoxiangSubmit.Accepted, "持 PlanLogistics 授权的意图必须通过入口预检");
        Require(duliaoxiangSubmit.CommandId == $"{duliaoxiang.DecisionId}-1",
            "入口必须把意图幂等键（DecisionId-1）作为稳定命令编号");

        // 模型伪造越权：hubu-slot（只持财粮能力）与 zhu-youjian（无能力）也被模型"建议"调粮。
        // 新契约（候选集按 Actor 实际授权过滤）：无授权角色的上下文不含可行动路线，
        // 模型输出过不了候选校验而回退规则路径，且规则路径同样无候选 → 零意图、零提交。
        // 权限红线保持不变（模型文本不能改变状态），只是拦截点前移到候选编译层。
        foreach (var actor in new[] { "hubu-slot", "zhu-youjian" })
        {
            var denied = batch.Decisions.Single(decision => decision.ActorId.Value == actor);
            Require(denied.Source == DecisionSource.Rules && denied.Intents.Count == 0 && denied.Submissions.Count == 0,
                $"无调粮授权的 {actor} 不得产生任何意图或提交（候选集按授权过滤，红线前移）");
        }

        // P1-AGENT-05 契约变化：宿主在首位角色提交成功后即把收件箱在当前安全点真实推进
        // （下一角色必须从权威状态重取版本），因此批次返回时 duliaoxiang 的命令已经生效——
        // 组合根接线的模型意图必须真正生效（创建运输单），权威版本已推进 +2。
        Require(runtime.ReadModel.Shipments.Count == 1 &&
                runtime.ReadModel.Shipments.Single().Id.Value == $"shipment-{duliaoxiang.DecisionId}-1",
            "组合根接线的模型意图必须真正生效（创建运输单）");
        Require(runtime.ReadModel.WorldVersion == before.WorldVersion + 2,
            "受理命令 +1 与出发事件 +1 各一次原子提交");
        var outcome = runtime.CommandOutcomes.Single();
        Require(outcome.Accepted && outcome.CommandId == $"{duliaoxiang.DecisionId}-1",
            "内核必须受理宿主派生命令编号并记录 Accepted Outcome");
        Require(outcome.ExpectedWorldVersion == before.WorldVersion,
            "首位角色命令必须携带批次起始的权威版本");

        // 宿主已逐角色推进收件箱，调用方后续安全点不再有未受理命令（幂等空转）。
        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.CommandResults.Count == 0 && advanced.ReadModel.WorldVersion == before.WorldVersion + 2,
            "宿主推进后调用方安全点不得重复受理已生效的命令");
    }

    /// <summary>
    /// 预算耗尽经宿主回退：配置了 Provider 但预算已耗尽时，宿主管线 0 次调用模型，
    /// 回退 Utility 仍产出结构化意图并提交生效，世界不阻塞；审计记录 BudgetExceeded。
    /// </summary>
    private static void ShouldFallBackToRulesThroughHostWhenBudgetExhausted()
    {
        var world = CreateHostScenarioWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var budget = new ModelBudgetTracker(
            new ModelBudget(MaxTokens: 100, MaxCostMillis: long.MaxValue, CostPerTokenMillis: 0));
        budget.RecordUsage(100); // 预算已耗尽：任何新调用都被闸门拦截
        var audit = new ModelAuditLog();
        var provider = new FakeModelProvider(
            """{"schema_version":1,"intent_type":"logistics.request_shipment","parameters":{"route_id":"capital-ningyuan-grain","grain_quantity":200}}""");
        var host = new AgentDecisionHost(runtime,
        [
            new HostedAgent(new CharacterId("duliaoxiang-slot"),
                new DecisionPlanner(new UtilityMinisterAgent(MinisterFocus.Logistics), provider, budget, audit)),
        ]);
        var before = runtime.ReadModel;

        var batch = host.DecideAndSubmitAsync(world, world.GameTime).GetAwaiter().GetResult();
        var decision = batch.Decisions.Single();

        Require(decision.Source == DecisionSource.Rules && decision.FallbackReason == ModelFallbackReason.BudgetExceeded,
            "预算耗尽时必须回退规则路径并明确 BudgetExceeded 原因");
        Require(provider.CallCount == 0, "预算耗尽后宿主不得发起任何模型调用");
        var ruleIntent = decision.Intents.Single();
        Require(ruleIntent is PlanLogisticsIntent && ruleIntent.IdempotencyKey == $"logistics-capital-ningyuan-grain-300-{world.WorldVersion}",
            "回退必须产出 Utility 的结构化意图");
        Require(decision.Submissions.Single().Accepted, "回退意图必须经入口提交");
        Require(audit.Entries.Single().Outcome == ModelCallOutcome.BudgetExceeded, "审计必须记录一次 BudgetExceeded");

        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.ReadModel.Shipments.Count == 1 &&
                advanced.ReadModel.Shipments.Single().Id.Value == $"shipment-logistics-capital-ningyuan-grain-300-{world.WorldVersion}",
            "预算耗尽后宿主回退的规则意图必须真正生效，世界继续推进");
    }

    /// <summary>
    /// Provider 超时经宿主回退：真实 OpenAiCompatibleModelProvider 头部停滞触发总超时硬边界，
    /// 宿主决策回退 Utility 仍产出并提交意图，且受硬边界约束不阻塞世界。
    /// </summary>
    private static void ShouldFallBackToRulesThroughHostWhenProviderTimesOut()
    {
        var world = CreateHostScenarioWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
            new TaskCompletionSource<HttpResponseMessage>().Task));
        var provider = new OpenAiCompatibleModelProvider(
            client, "test-model", totalTimeout: TimeSpan.FromMilliseconds(80));
        var audit = new ModelAuditLog();
        var host = new AgentDecisionHost(runtime,
        [
            new HostedAgent(new CharacterId("duliaoxiang-slot"),
                new DecisionPlanner(new UtilityMinisterAgent(MinisterFocus.Logistics), provider, auditLog: audit)),
        ]);
        var before = runtime.ReadModel;
        var stopwatch = Stopwatch.StartNew();

        var batch = host.DecideAndSubmitAsync(world, world.GameTime).GetAwaiter().GetResult();
        stopwatch.Stop();

        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Provider 超时必须受硬边界约束（实际耗时 {stopwatch.Elapsed}）");
        var decision = batch.Decisions.Single();
        Require(decision.Source == DecisionSource.Rules && decision.FallbackReason == ModelFallbackReason.ProviderFailed,
            "Provider 超时经宿主必须回退 Utility 并明确失败原因");
        Require(decision.Intents.Single() is PlanLogisticsIntent, "回退必须产出结构化意图");
        Require(decision.Submissions.Single().Accepted, "超时回退意图必须可提交");
        Require(audit.Entries.Single().Outcome == ModelCallOutcome.ProviderFailed, "审计必须记录 ProviderFailed");

        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.ReadModel.Shipments.Count == 1, "Provider 超时后宿主回退意图生效，世界必须继续推进");
    }

    /// <summary>
    /// 并发护栏（P2-2）：多线程 100 次 RecordUsage 总额精确（不丢更新）；
    /// 并发审计追加同样不丢条目。
    /// </summary>
    private static void ShouldNotLoseConcurrentUsageAccounting()
    {
        const int calls = 100;
        var tracker = new ModelBudgetTracker(
            new ModelBudget(MaxTokens: long.MaxValue, MaxCostMillis: long.MaxValue, CostPerTokenMillis: 3));
        Parallel.For(0, calls, _ => tracker.RecordUsage(1));

        Require(tracker.SpentTokens == calls,
            $"并发 {calls} 次 RecordUsage(1) 后 token 总额必须精确等于 {calls}（实际 {tracker.SpentTokens}）");
        Require(tracker.SpentCostMillis == calls * 3,
            $"并发记账金额必须精确等于 {calls} × 3 毫厘（实际 {tracker.SpentCostMillis}）");
        Require(!tracker.IsExhausted, "小额并发记账不得误判预算耗尽");

        var audit = new ModelAuditLog();
        Parallel.For(0, calls, index => audit.Append(new ModelAuditEntry(
            $"decision-{index}", "concurrent", ModelCallOutcome.Accepted, 1, 1, 1, TimeSpan.Zero, DateTimeOffset.UtcNow)));
        Require(audit.Count == calls, $"并发追加 {calls} 条审计后 Count 必须精确等于 {calls}（实际 {audit.Count}）");
        Require(audit.Entries.Count == calls, "并发追加后 Entries 快照必须包含全部条目，不丢更新");
    }

    /// <summary>
    /// 审计容量上限（P2-4）：超出容量后截断最旧条目，剩余条目保持追加顺序。
    /// </summary>
    private static void ShouldTruncateAuditLogToCapacityPreservingOrder()
    {
        var log = new ModelAuditLog(capacity: 5);
        for (var index = 0; index < 10; index++)
        {
            log.Append(new ModelAuditEntry($"decision-{index}", "provider", ModelCallOutcome.Accepted,
                index, index, index, TimeSpan.FromSeconds(index), DateTimeOffset.UtcNow));
        }

        Require(log.Count == 5, "容量 5 的审计日志追加 10 条后只能保留 5 条");
        var entries = log.Entries;
        Require(entries.Count == 5, "Entries 快照必须与 Count 一致");
        Require(entries[0].DecisionId == "decision-5" && entries[1].DecisionId == "decision-6" &&
                entries[2].DecisionId == "decision-7" && entries[3].DecisionId == "decision-8" &&
                entries[4].DecisionId == "decision-9",
            "截断必须丢弃最旧条目并保持剩余条目的追加顺序");
        Require(log.Count == 5, "读取快照不得改变日志长度");

        // 边界：容量必须为正；空条目拒绝。
        RequireThrows<ArgumentOutOfRangeException>(() => new ModelAuditLog(0), "审计日志容量 0 必须拒绝");
        RequireThrows<ArgumentOutOfRangeException>(() => new ModelAuditLog(-1), "审计日志负容量必须拒绝");
        RequireThrows<ArgumentNullException>(() => log.Append(null!), "空审计条目必须拒绝");
    }

    /// <summary>
    /// 预算边界（P2-6）：MaxCostMillis 金额上限先耗尽即拦截；long 溢出按饱和处理绝不回绕；
    /// 0/负数边界与构造参数校验。
    /// </summary>
    private static void ShouldEnforceMaxCostMillisAndSaturatingBoundaries()
    {
        // 金额双上限：MaxCostMillis 先于 token 上限耗尽。
        var costBudget = new ModelBudgetTracker(
            new ModelBudget(MaxTokens: long.MaxValue, MaxCostMillis: 100, CostPerTokenMillis: 1));
        Require(costBudget.CanAfford(50), "预估 50 token（50 毫厘）未超金额上限必须允许");
        Require(costBudget.CanAfford(100), "预估 100 token（恰好等于金额上限）必须允许");
        Require(!costBudget.CanAfford(101), "预估 101 token 超金额上限必须拦截");
        costBudget.RecordUsage(50);
        Require(costBudget.SpentCostMillis == 50 && costBudget.SpentTokens == 50, "记账必须同时累计金额与 token");
        Require(costBudget.CanAfford(49), "已记 50 毫厘后预估 49 仍在上限内");
        Require(!costBudget.CanAfford(51), "已记 50 毫厘后预估 51 必然超金额上限");
        costBudget.RecordUsage(50);
        Require(costBudget.IsExhausted, "金额累计到上限即视为预算耗尽");

        // 饱和：long.MaxValue 记账与换算不得回绕成负数/小值。
        var saturating = new ModelBudgetTracker(
            new ModelBudget(MaxTokens: long.MaxValue, MaxCostMillis: long.MaxValue, CostPerTokenMillis: 1));
        Require(saturating.CostFor(long.MaxValue) == long.MaxValue, "CostFor(long.MaxValue) 必须饱和到 long.MaxValue 而非回绕");
        saturating.RecordUsage(long.MaxValue);
        Require(saturating.SpentTokens == long.MaxValue && saturating.SpentCostMillis == long.MaxValue,
            "饱和记账必须停在 long.MaxValue");
        saturating.RecordUsage(1);
        Require(saturating.SpentTokens == long.MaxValue && saturating.SpentCostMillis == long.MaxValue,
            "饱和后继续记账不得回绕成小值");
        Require(!saturating.CanAfford(0), "饱和到上限后即使 0 token 预估也必须视为预算耗尽");
        Require(!saturating.CanAfford(1), "饱和到上限后任何新调用都必须被闸门拦截");

        // 0 与负数边界。
        var zero = new ModelBudgetTracker(
            new ModelBudget(MaxTokens: 100, MaxCostMillis: 100, CostPerTokenMillis: 0));
        Require(zero.CanAfford(0), "预算未耗尽时预估 0 token 必须允许");
        zero.RecordUsage(0);
        Require(zero.SpentTokens == 0 && zero.SpentCostMillis == 0, "RecordUsage(0) 必须是无操作");
        Require(zero.CostFor(0) == 0, "CostFor(0) 必须为 0");
        RequireThrows<ArgumentOutOfRangeException>(() => zero.RecordUsage(-1), "负数记账必须拒绝");
        RequireThrows<ArgumentOutOfRangeException>(() => zero.CanAfford(-1), "负数预估必须拒绝");
        RequireThrows<ArgumentOutOfRangeException>(() => zero.CostFor(-1), "负数换算必须拒绝");
        RequireThrows<ArgumentOutOfRangeException>(
            () => new ModelBudgetTracker(new ModelBudget(0, 100, 1)), "MaxTokens<=0 必须拒绝");
        RequireThrows<ArgumentOutOfRangeException>(
            () => new ModelBudgetTracker(new ModelBudget(100, 0, 1)), "MaxCostMillis<=0 必须拒绝");
        RequireThrows<ArgumentOutOfRangeException>(
            () => new ModelBudgetTracker(new ModelBudget(100, 100, -1)), "负单价必须拒绝");
    }

    /// <summary>
    /// 解析器 try 回退（P2-1）：解析器抛出未预期异常时，决策回退 Utility 并记 ParseFailed 审计，
    /// 异常绝不外泄阻塞世界。
    /// </summary>
    private static void ShouldFallBackToRulesWhenParserThrows()
    {
        var world = CreateEntryLogisticsWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var entry = new AgentRealtimeEntry(runtime);
        var context = new AgentContextCompiler().Compile(world, new CharacterId("works"));
        var now = runtime.ReadModel.GameTime;
        var request = new DecisionRequest(
            "decision-parser-throws", new CharacterId("works"), world.WorldVersion, now,
            now.Add(TimeSpan.FromHours(1)));
        var audit = new ModelAuditLog();
        var planner = new DecisionPlanner(
            new UtilityMinisterAgent(MinisterFocus.Logistics),
            new FakeModelProvider(
                """{"schema_version":1,"intent_type":"logistics.request_shipment","parameters":{"route_id":"capital-ningyuan-grain","grain_quantity":200}}"""),
            auditLog: audit,
            parser: new ThrowingModelDecisionParser());

        var result = planner.PlanAsync(request, context, now.Add(TimeSpan.FromMinutes(30))).GetAwaiter().GetResult();

        Require(result.Source == DecisionSource.Rules && result.FallbackReason == ModelFallbackReason.ParseFailed,
            "解析器未预期异常必须回退规则路径并明确 ParseFailed 原因");
        var ruleIntent = result.Intents.Single();
        Require(ruleIntent is PlanLogisticsIntent && ruleIntent.IdempotencyKey == $"logistics-capital-ningyuan-grain-300-{world.WorldVersion}",
            "回退必须产出 Utility 的结构化意图");
        Require(audit.Entries.Single().Outcome == ModelCallOutcome.ParseFailed, "解析器异常必须记录 ParseFailed 审计");

        var submit = entry.Submit(world, result.Intents).Single();
        Require(submit.Accepted, "回退意图必须可提交");
        var advanced = runtime.AdvanceTo(now);
        Require(advanced.ReadModel.Shipments.Count == 1, "解析器异常后世界必须继续推进（回退意图生效）");
    }

    /// <summary>宿主测试用的 1629 场景世界：三名关键人物按场景能力授权（DESIGN 最小映射）。</summary>
    private static WorldState CreateHostScenarioWorld()
    {
        var map = new MapDefinition(
            "host-wiring-map",
            [
                new ProvinceDefinition(new ProvinceId("capital"), "京师", [new ProvinceId("liaodong")]),
                new ProvinceDefinition(new ProvinceId("liaodong"), "辽东", [new ProvinceId("capital")]),
            ]);
        return WorldState.CreateInitial(
            new WorldId("host-wiring"),
            1,
            200_000,
            map,
            characters:
            [
                new CharacterState(new CharacterId("zhu-youjian"), "朱由检（崇祯帝）",
                    new CharacterAttributes(50, 50, 50, 50, 50),
                    new CharacterPersonality(true, false, true, false)),
                new CharacterState(new CharacterId("hubu-slot"), "户部尚书（职位槽位）",
                    new CharacterAttributes(50, 50, 50, 50, 50),
                    new CharacterPersonality(true, false, true, false)),
                new CharacterState(new CharacterId("duliaoxiang-slot"), "督辽饷承办人（职位槽位）",
                    new CharacterAttributes(50, 50, 50, 50, 50),
                    new CharacterPersonality(true, false, true, false)),
            ],
            capabilityGrants:
            [
                new CapabilityGrant(new CharacterId("hubu-slot"), GameCapability.ReadFinance),
                new CapabilityGrant(new CharacterId("hubu-slot"), GameCapability.AllocateFinance),
                new CapabilityGrant(new CharacterId("duliaoxiang-slot"), GameCapability.PlanLogistics, "capital-ningyuan-grain"),
            ],
            stockpiles:
            [
                new StockpileState(new StockpileId("capital-granary"), new ProvinceId("capital"), 2_000, 1_000),
                new StockpileState(new StockpileId("ningyuan-granary"), new ProvinceId("liaodong"), 1_000, 0),
            ],
            routes:
            [
                new RouteState(new RouteId("capital-ningyuan-grain"),
                    new StockpileId("capital-granary"), new StockpileId("ningyuan-granary"),
                    500, 2, 100),
            ]);
    }

    /// <summary>
    /// 幂等键随世界版本派生（修复 #32 审查 P2-2）：同一版本重复决策同键（可被内核去重）；
    /// 世界版本推进后的新决策必须派生新键，否则规则回退自第二回合起被 IDEMPOTENCY_CONFLICT 永远拒绝。
    /// </summary>
    private static void ShouldDeriveFreshIdempotencyKeyPerWorldVersion()
    {
        var world = CreateHostScenarioWorld();
        var agent = new UtilityMinisterAgent(MinisterFocus.Logistics);
        var context = new AgentContextCompiler().Compile(world, new CharacterId("duliaoxiang-slot"));

        var firstKey = agent.Decide(context).Single().IdempotencyKey;
        Require(firstKey == $"logistics-capital-ningyuan-grain-300-{world.WorldVersion}",
            "幂等键必须由当前世界版本派生");
        Require(agent.Decide(context).Single().IdempotencyKey == firstKey,
            "同一版本重复决策必须得到同一幂等键（幂等）");

        var nextContext = context with { WorldVersion = world.WorldVersion + 1 };
        var secondKey = agent.Decide(nextContext).Single().IdempotencyKey;
        Require(secondKey != firstKey, "世界版本推进后必须派生出新的幂等键");
        Require(secondKey == $"logistics-capital-ningyuan-grain-300-{world.WorldVersion + 1}",
            "新键必须对应推进后的世界版本");
    }

    /// <summary>契约测试用的"必然抛异常"解析器：证明 DecisionPlanner 的解析 try 回退防线真实生效。</summary>
    private sealed class ThrowingModelDecisionParser : ModelDecisionParser
    {
        public override ModelParseResult Parse(
            DecisionRequest request,
            AgentContext context,
            string modelJson,
            GameTime acceptedGameTime) =>
            throw new InvalidOperationException("parser boom（模拟解析器未预期缺陷）");
    }
}
