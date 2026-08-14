using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using MingSim.Agents.Audit;
using MingSim.Agents.Decision;
using MingSim.Agents.Providers;
using MingSim.Agents.Realtime;
using MingSim.Agents.Runtime;
using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Intents;
using MingSim.Domain.Map;
using MingSim.Domain.Military;
using MingSim.Domain.Realtime;
using MingSim.Simulation.Realtime;

namespace MingSim.Agents.ContractTests;

/// <summary>
/// Agent → 实时内核入口的契约测试。
/// 验证红线：Agent 只能提交结构化意图，经权限预检后以 RealtimeCommand 进入唯一实时管线；
/// 未授权/不支持的意图在进入内核前就被结构化拒绝，且不产生任何副作用。
/// </summary>
internal static partial class Program
{
    /// <summary>
    /// 规则路径端到端：不配置任何模型 Provider，规则大臣产出粮运意图，
    /// 经 AgentRuntime → AgentRealtimeEntry → 实时内核受理并递增 WorldVersion。
    /// </summary>
    private static void ShouldSubmitAuthorizedRulesLogisticsIntentThroughKernel()
    {
        var world = CreateEntryLogisticsWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var entry = new AgentRealtimeEntry(runtime);
        var intents = new AgentRuntime().CollectDecisions(
            world,
            [new AgentRegistration(new CharacterId("works"), new UtilityMinisterAgent(MinisterFocus.Logistics))]);
        var before = runtime.ReadModel;

        var results = entry.Submit(world, intents);
        Require(results.Count == 1 && results[0].Accepted,
            "授权规则代理的粮运意图必须通过入口预检并进入收件箱");
        Require(results[0].CommandId == $"logistics-ningyuan-300-{world.WorldVersion}",
            "入口必须把意图的幂等键作为稳定命令编号");

        var advanced = runtime.AdvanceTo(before.GameTime);
        var commandResult = advanced.CommandResults.Single();
        Require(commandResult.Accepted, "内核必须在安全点受理授权粮运命令");
        Require(commandResult.ResultingWorldVersion == before.WorldVersion + 1,
            "内核受理命令本身必须恰好 +1 WorldVersion");
        // 受理命令 +1（CommandAccepted 提交），同一安全点内 ShipmentDeparture 事件再 +1。
        Require(advanced.ReadModel.WorldVersion == before.WorldVersion + 2,
            "受理粮运必须产生命令与出发两个原子提交");
        Require(advanced.ReadModel.Shipments.Any(shipment => shipment.Id.Value == $"shipment-logistics-ningyuan-300-{world.WorldVersion}"),
            "内核必须创建对应运输单");
    }

    /// <summary>
    /// 未授权 Actor：入口直接结构化拒绝（TOOL_SCOPE_DENIED），命令不进入内核，零副作用。
    /// </summary>
    private static void ShouldRejectUnauthorizedLogisticsIntentWithoutSideEffects()
    {
        var world = CreateEntryLogisticsWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var entry = new AgentRealtimeEntry(runtime);
        var intent = new PlanLogisticsIntent(
            "decision-unauthorized",
            new CharacterId("war"),   // 存在但没有 PlanLogistics 授权
            1,
            "unauthorized-logistics-1",
            runtime.ReadModel.WorldVersion,
            new RouteId("capital-ningyuan-grain"),
            300,
            runtime.ReadModel.GameTime.Value);
        var before = runtime.ReadModel;

        var result = entry.Submit(world, [intent]).Single();
        Require(!result.Accepted && result.ErrorCode == "TOOL_SCOPE_DENIED",
            "无粮运权限的角色必须在入口被结构化拒绝");
        Require(result.CommandId is null, "被拒绝的意图不能产生命令编号");

        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.CommandResults.Count == 0, "未授权意图不能进入内核收件箱");
        Require(advanced.ReadModel.WorldVersion == before.WorldVersion, "未授权意图不能产生任何提交");
        Require(advanced.ReadModel.Shipments.Count == 0, "未授权意图不能创建运输单");
    }

    /// <summary>
    /// 内核不支持的意图（如旧回合路径的建厂意图）必须明确拒绝，而不是静默丢弃。
    /// </summary>
    private static void ShouldRejectUnsupportedIntentExplicitly()
    {
        var world = CreateEntryLogisticsWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var entry = new AgentRealtimeEntry(runtime);
        var intent = new BuildFacilityIntent(
            "decision-unsupported",
            new CharacterId("works"),
            1,
            "unsupported-1",
            new FacilityId("factory-1"),
            new ProvinceId("capital"),
            FacilityType.FlintlockWorkshop,
            50_000,
            800,
            80);
        var before = runtime.ReadModel;

        var result = entry.Submit(world, [intent]).Single();
        Require(!result.Accepted && result.ErrorCode == "UNSUPPORTED_INTENT",
            "内核不支持的意图必须结构化拒绝而非静默丢弃");

        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.CommandResults.Count == 0 && advanced.ReadModel.WorldVersion == before.WorldVersion,
            "不支持的意图不能进入内核或改变世界");
    }

    /// <summary>
    /// 行军意图同样走入口：授权受理恰好 +1 WorldVersion；未授权入口拒绝且零副作用。
    /// </summary>
    private static void ShouldSubmitAuthorizedMoveArmyIntentThroughKernel()
    {
        var world = CreateEntryMoveWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var entry = new AgentRealtimeEntry(runtime);
        var intent = new MoveArmyIntent(
            "decision-move-1",
            new CharacterId("war"),
            1,
            "move-frontier-1",
            runtime.ReadModel.WorldVersion,
            new ArmyId("army-1"),
            new ProvinceId("capital"),
            runtime.ReadModel.GameTime.Value,
            TravelHours: 24);
        var before = runtime.ReadModel;

        var result = entry.Submit(world, [intent]).Single();
        Require(result.Accepted, "有行军权限的角色必须通过入口预检");

        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.CommandResults.Single().Accepted, "内核必须受理行军命令");
        Require(advanced.ReadModel.WorldVersion == before.WorldVersion + 1, "行军受理必须恰好 +1 WorldVersion");
        Require(advanced.ReadModel.Movements.Count == 1, "内核必须建立唯一行军状态");

        var denied = new MoveArmyIntent(
            "decision-move-denied",
            new CharacterId("works"),   // 存在但没有 MoveArmy 授权
            1,
            "move-denied-1",
            runtime.ReadModel.WorldVersion,
            new ArmyId("army-1"),
            new ProvinceId("capital"),
            runtime.ReadModel.GameTime.Value,
            TravelHours: 24);
        var deniedBefore = runtime.ReadModel;
        var deniedResult = entry.Submit(world, [denied]).Single();
        Require(!deniedResult.Accepted && deniedResult.ErrorCode == "TOOL_SCOPE_DENIED",
            "无行军权限的角色必须被入口拒绝");
        var deniedAdvanced = runtime.AdvanceTo(deniedBefore.GameTime);
        Require(deniedAdvanced.CommandResults.Count == 0 && deniedAdvanced.ReadModel.WorldVersion == deniedBefore.WorldVersion,
            "被拒绝的行军意图不能进入内核");
    }

    /// <summary>
    /// 模型路径保持可选：入口构造与提交不接收、不引用任何 IModelProvider；
    /// 规则路径（默认）无需任何 Provider 配置即可完整走到内核。
    /// </summary>
    private static void ShouldRequireNoModelProviderForRulesPath()
    {
        var constructorTypes = typeof(AgentRealtimeEntry).GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Require(constructorTypes.SequenceEqual([typeof(RealtimeSimulationRuntime)]),
            "AgentRealtimeEntry 只能依赖实时内核，不能接收模型 Provider");
        Require(typeof(AgentRealtimeEntry).GetMembers().All(member => !IsSecretMemberName(member.Name)),
            "AgentRealtimeEntry 的公开面不能出现密钥/令牌形态的成员");
    }

    /// <summary>
    /// 无密钥泄露断言：Agent 实时入口源码与契约测试不得包含任何凭据形态，
    /// 也不得出现本机绝对路径（防止把开发机路径带进仓库）。
    /// </summary>
    private static void ShouldNotLeakSecretsInAgentEntrySources()
    {
        var root = FindRepositoryRoot();
        var scanRoots = new[]
        {
            Path.Combine(root, "src", "Ming.Agents"),
            Path.Combine(root, "tests", "Ming.Agents.ContractTests"),
        };
        var secretPatterns = new[]
        {
            new Regex(@"sk-[A-Za-z0-9]{16,}", RegexOptions.Compiled),
            new Regex(@"(?i)(api[_-]?key|apikey|secret)\s*[:=]\s*[""'][^""']{8,}[""']", RegexOptions.Compiled),
            new Regex(@"(?i)bearer\s+[A-Za-z0-9._~+/=-]{16,}", RegexOptions.Compiled),
            // 盘符路径扫描：排除 n、t、r 等 C# 转义字母，避免把字符串里的
            // 转义序列（反斜杠加 n 之类）误报成绝对路径。
            new Regex(@"[A-Za-z]:\\[^nrtabfv0xu]", RegexOptions.Compiled), // Windows 绝对路径盘符
        };
        var hits = new List<string>();
        foreach (var scanRoot in scanRoots)
        {
            if (!Directory.Exists(scanRoot))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    foreach (var pattern in secretPatterns)
                    {
                        if (pattern.IsMatch(lines[index]))
                        {
                            hits.Add($"{Path.GetFileName(file)}:{index + 1}: {lines[index].Trim()}");
                        }
                    }
                }
            }
        }

        Require(hits.Count == 0,
            $"Agent 入口源码/测试出现秘密或绝对路径：{Environment.NewLine}{string.Join(Environment.NewLine, hits)}");
    }

    /// <summary>
    /// 效用打分确定性：同一上下文重复决策必须得到同一意图；
    /// 三个白名单意图都可用时，分数 = 权重 × 条件，取分数最高的那个（专注方向决定权重）。
    /// </summary>
    private static void ShouldChooseDeterministicallyByUtilityScoring()
    {
        var world = CreateUtilityScoringWorld();
        var context = new AgentContextCompiler().Compile(world, new CharacterId("works"));

        var industry = new UtilityMinisterAgent(MinisterFocus.Industry).Decide(context);
        var military = new UtilityMinisterAgent(MinisterFocus.Military).Decide(context);
        var logistics = new UtilityMinisterAgent(MinisterFocus.Logistics).Decide(context);
        Require(industry.Single() is BuildFacilityIntent, "工业专注在条件齐备时必须选建厂意图");
        Require(military.Single() is ConvertArmyIntent, "军事专注在条件齐备时必须选改编意图");
        Require(logistics.Single() is PlanLogisticsIntent, "物流专注在条件齐备时必须选粮运意图");

        var industryAgain = new UtilityMinisterAgent(MinisterFocus.Industry).Decide(context);
        Require(industry.SequenceEqual(industryAgain), "同状态同选择：重复决策必须返回相同意图");

        // 白名单之外的能力缺失时条件为 0：没有建厂/改编授权的大臣只剩粮运可用。
        var limitedWorld = CreateEntryLogisticsWorld();
        var limitedContext = new AgentContextCompiler().Compile(limitedWorld, new CharacterId("works"));
        var limited = new UtilityMinisterAgent(MinisterFocus.Industry).Decide(limitedContext);
        Require(limited.Single() is PlanLogisticsIntent,
            "条件不成立的高权重意图不得被选，只能选条件成立的低分白名单意图");
    }

    /// <summary>
    /// 模型路径 happy path：fake Provider 产出白名单粮运 JSON，解析成功且未过期时
    /// 采用模型意图，并经 AgentRealtimeEntry 提交到内核。
    /// </summary>
    private static void ShouldSubmitFreshModelDecisionThroughKernel()
    {
        var world = CreateEntryLogisticsWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var entry = new AgentRealtimeEntry(runtime);
        var context = new AgentContextCompiler().Compile(world, new CharacterId("works"));
        var now = runtime.ReadModel.GameTime;
        var request = new DecisionRequest(
            "decision-model-fresh", new CharacterId("works"), world.WorldVersion, now,
            now.Add(TimeSpan.FromHours(1)));
        var provider = new FakeModelProvider(
            """{"schema_version":1,"intent_type":"logistics.request_shipment","parameters":{"route_id":"capital-ningyuan-grain","grain_quantity":200}}""");
        var planner = new DecisionPlanner(new UtilityMinisterAgent(MinisterFocus.Logistics), provider);
        var before = runtime.ReadModel;

        var result = planner.PlanAsync(request, context, now.Add(TimeSpan.FromMinutes(30))).GetAwaiter().GetResult();

        Require(result.Source == DecisionSource.Model, "未过期且解析成功的模型结果必须被采用");
        var modelIntent = result.Intents.Single();
        Require(modelIntent is PlanLogisticsIntent && modelIntent.IdempotencyKey == "decision-model-fresh-1",
            "模型意图的幂等键必须由 DecisionId + 序号派生");

        var submit = entry.Submit(world, result.Intents).Single();
        Require(submit.Accepted, "模型意图必须经入口预检进入收件箱");
        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.CommandResults.Single().Accepted, "内核必须在安全点受理模型意图");
        Require(advanced.CommandResults.Single().CommandId == "decision-model-fresh-1",
            "模型意图的命令编号必须稳定且幂等");
        Require(advanced.ReadModel.Shipments.Any(item => item.Id.Value == "shipment-decision-model-fresh-1"),
            "内核必须创建模型意图对应的运输单");
    }

    /// <summary>
    /// 过期模型结果必须被丢弃：即使模型返回了合法意图，只要 AcceptedGameTime 达到截止时刻
    /// （半开区间，AcceptedGameTime &gt;= Deadline 即过期），也一律回退规则路径，且不产生任何副作用。
    /// </summary>
    private static void ShouldDiscardExpiredModelResultAndFallBackToRules()
    {
        var world = CreateEntryLogisticsWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var entry = new AgentRealtimeEntry(runtime);
        var context = new AgentContextCompiler().Compile(world, new CharacterId("works"));
        var now = runtime.ReadModel.GameTime;
        var request = new DecisionRequest(
            "decision-expired", new CharacterId("works"), world.WorldVersion, now,
            now.Add(TimeSpan.FromHours(1)));
        var provider = new FakeModelProvider(
            """{"schema_version":1,"intent_type":"logistics.request_shipment","parameters":{"route_id":"capital-ningyuan-grain","grain_quantity":500}}""");
        var planner = new DecisionPlanner(new UtilityMinisterAgent(MinisterFocus.Logistics), provider);
        var before = runtime.ReadModel;

        // 模型返回时世界已推进到截止时刻之后 1 小时 → 半开区间语义下已过期。
        var result = planner.PlanAsync(request, context, now.Add(TimeSpan.FromHours(2))).GetAwaiter().GetResult();

        Require(!request.IsExpired(now.Add(TimeSpan.FromMinutes(30))), "截止之前到达必须有效（半开区间左端开）");
        Require(request.IsExpired(request.Deadline), "半开区间：AcceptedGameTime 恰好等于截止也必须过期");
        Require(request.IsExpired(now.Add(TimeSpan.FromHours(2))), "截止之后到达必须过期");
        Require(result.Source == DecisionSource.Rules, "过期模型结果必须被丢弃并回退规则路径");
        var ruleIntent = result.Intents.Single();
        Require(ruleIntent is PlanLogisticsIntent && ruleIntent.IdempotencyKey == $"logistics-ningyuan-300-{world.WorldVersion}",
            "回退必须采用规则（Utility AI）路径的意图");

        var submit = entry.Submit(world, result.Intents).Single();
        Require(submit.Accepted && submit.CommandId == $"logistics-ningyuan-300-{world.WorldVersion}",
            "规则回退意图必须经入口提交");
        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.CommandResults.Single().CommandId == $"logistics-ningyuan-300-{world.WorldVersion}" &&
                advanced.CommandResults.Single().Accepted,
            "被采纳的命令只能是规则回退意图，过期模型意图不得出现");
        Require(advanced.ReadModel.Shipments.Count == 1 &&
                advanced.ReadModel.Shipments.Single().Id.Value == $"shipment-logistics-ningyuan-300-{world.WorldVersion}",
            "过期模型结果不能产生任何运输单副作用");
    }

    /// <summary>
    /// 模型文本不改状态：模型输出非法 JSON、未知意图类型或模型失败时自动回退规则路径；
    /// 模型文本本身不产生任何提交或世界变化。
    /// </summary>
    private static void ShouldFallBackToRulesWhenModelOutputIsInvalid()
    {
        var world = CreateEntryLogisticsWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var entry = new AgentRealtimeEntry(runtime);
        var context = new AgentContextCompiler().Compile(world, new CharacterId("works"));
        var now = runtime.ReadModel.GameTime;
        var request = new DecisionRequest(
            "decision-invalid", new CharacterId("works"), world.WorldVersion, now,
            now.Add(TimeSpan.FromHours(1)));

        var invalidJson = plannerResult(new FakeModelProvider("这不是 JSON"), request, context, now);
        Require(invalidJson.Source == DecisionSource.Rules, "非法 JSON 必须回退规则路径");
        Require(invalidJson.Intents.Single().IdempotencyKey == $"logistics-ningyuan-300-{world.WorldVersion}",
            "非法 JSON 回退必须采用规则意图");
        var invalidSubmit = entry.Submit(world, invalidJson.Intents).Single();
        Require(invalidSubmit.Accepted, "规则回退意图必须可提交");
        var invalidAdvanced = runtime.AdvanceTo(now);
        Require(invalidAdvanced.ReadModel.Shipments.Count == 1 &&
                invalidAdvanced.ReadModel.Shipments.Single().Id.Value == $"shipment-logistics-ningyuan-300-{world.WorldVersion}",
            "模型文本不能改变世界，只有规则回退意图生效");

        var unknownIntent = plannerResult(
            new FakeModelProvider("""{"schema_version":1,"intent_type":"world.modify_state","parameters":{}}"""),
            request, context, now);
        Require(unknownIntent.Source == DecisionSource.Rules, "未知意图类型必须被拒绝并回退规则路径");

        var failedProvider = plannerResult(new FakeModelProvider("", succeeded: false), request, context, now);
        Require(failedProvider.Source == DecisionSource.Rules, "模型失败/超时必须回退规则路径");

        static DecisionResult plannerResult(FakeModelProvider provider, DecisionRequest request, AgentContext context, GameTime now)
        {
            var planner = new DecisionPlanner(new UtilityMinisterAgent(MinisterFocus.Logistics), provider);
            return planner.PlanAsync(request, context, now.Add(TimeSpan.FromMinutes(30))).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// 0 次模型调用完整可玩：不配置 Provider 时，决策始终走规则路径。
    /// </summary>
    private static void ShouldRunRulesPathWithoutModelCalls()
    {
        var world = CreateEntryLogisticsWorld();
        var context = new AgentContextCompiler().Compile(world, new CharacterId("works"));
        var now = world.GameTime;
        var request = new DecisionRequest(
            "decision-no-provider", new CharacterId("works"), world.WorldVersion, now,
            now.Add(TimeSpan.FromHours(1)));
        var planner = new DecisionPlanner(new UtilityMinisterAgent(MinisterFocus.Logistics));

        var result = planner.PlanAsync(request, context, now.Add(TimeSpan.FromMinutes(30))).GetAwaiter().GetResult();

        Require(result.Source == DecisionSource.Rules, "无 Provider 时必须走规则路径");
        Require(result.Intents.Single() is PlanLogisticsIntent, "规则路径必须产出粮运意图");
    }

    /// <summary>
    /// 预算预检（M6）：预算已耗尽时，即使配置了 Provider，也必须在调用前停止模型请求（0 次调用），
    /// 回退 Utility 产出结构化意图；审计记录一次 BudgetExceeded，世界不阻塞。
    /// </summary>
    private static void ShouldStopModelCallsWhenBudgetExceededBeforeCall()
    {
        var world = CreateEntryLogisticsWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var entry = new AgentRealtimeEntry(runtime);
        var context = new AgentContextCompiler().Compile(world, new CharacterId("works"));
        var now = runtime.ReadModel.GameTime;
        var request = new DecisionRequest(
            "decision-budget-precheck", new CharacterId("works"), world.WorldVersion, now,
            now.Add(TimeSpan.FromHours(1)));
        var provider = new FakeModelProvider(
            """{"schema_version":1,"intent_type":"logistics.request_shipment","parameters":{"route_id":"capital-ningyuan-grain","grain_quantity":200}}""");
        var budget = new ModelBudgetTracker(
            new ModelBudget(MaxTokens: 100, MaxCostMillis: long.MaxValue, CostPerTokenMillis: 0));
        budget.RecordUsage(100); // 预算已耗尽：任何一次新调用都会被闸门拦截
        var audit = new ModelAuditLog();
        var planner = new DecisionPlanner(
            new UtilityMinisterAgent(MinisterFocus.Logistics), provider, budget, audit);
        var before = runtime.ReadModel;

        var result = planner.PlanAsync(request, context, now.Add(TimeSpan.FromMinutes(30))).GetAwaiter().GetResult();

        Require(result.Source == DecisionSource.Rules, "预算耗尽时必须回退规则路径");
        Require(result.FallbackReason == ModelFallbackReason.BudgetExceeded,
            "回退原因必须明确为 BudgetExceeded，不能静默假装模型决策成功");
        Require(result.Intents.Single() is PlanLogisticsIntent, "回退必须产出 Utility 的结构化意图");
        Require(provider.CallCount == 0, "预算耗尽后不得发起任何模型调用");

        var auditEntries = audit.Entries;
        Require(auditEntries.Count == 1 && auditEntries[0].Outcome == ModelCallOutcome.BudgetExceeded,
            "审计必须记录一次 BudgetExceeded");
        Require(auditEntries[0].DecisionId == request.DecisionId, "审计必须关联决策 ID");

        var submit = entry.Submit(world, result.Intents).Single();
        Require(submit.Accepted, "预算回退意图必须经入口提交");
        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.ReadModel.Shipments.Count == 1, "预算耗尽后世界必须继续推进（回退意图生效）");
    }

    /// <summary>
    /// 预算记账（M6）：成功调用按 请求+响应 token 估算记账（长整型，避免浮点）；超大响应耗尽预算后，
    /// 下一次决策在调用前被闸门拦截并回退 Utility。
    /// </summary>
    private static void ShouldFallBackToUtilityAfterBudgetExhaustedByUsage()
    {
        var world = CreateEntryLogisticsWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var context = new AgentContextCompiler().Compile(world, new CharacterId("works"));
        var now = runtime.ReadModel.GameTime;
        var request = new DecisionRequest(
            "decision-budget-usage", new CharacterId("works"), world.WorldVersion, now,
            now.Add(TimeSpan.FromHours(1)));
        var budget = new ModelBudgetTracker(
            new ModelBudget(MaxTokens: 100_000, MaxCostMillis: long.MaxValue, CostPerTokenMillis: 0));
        var audit = new ModelAuditLog();
        // 超大响应：padding 字段被解析器固定忽略，但响应 token 估算会计入预算。
        var padding = new string('x', 1_000_000);
        var hugeJson = "{\"schema_version\":1,\"intent_type\":\"logistics.request_shipment\",\"parameters\":{\"route_id\":\"capital-ningyuan-grain\",\"grain_quantity\":1},\"padding\":\"" + padding + "\"}";
        var provider = new SequencedFakeModelProvider(
            """{"schema_version":1,"intent_type":"logistics.request_shipment","parameters":{"route_id":"capital-ningyuan-grain","grain_quantity":200}}""",
            hugeJson);
        var planner = new DecisionPlanner(
            new UtilityMinisterAgent(MinisterFocus.Logistics), provider, budget, audit);
        var acceptedGameTime = now.Add(TimeSpan.FromMinutes(30));

        var first = planner.PlanAsync(request, context, acceptedGameTime).GetAwaiter().GetResult();
        Require(first.Source == DecisionSource.Model, "预算充足时第一次调用必须走模型路径");
        Require(budget.SpentTokens > 0, "成功调用后必须按请求+响应 token 记账");

        var second = planner.PlanAsync(request, context, acceptedGameTime).GetAwaiter().GetResult();
        Require(second.Source == DecisionSource.Model, "超大响应本身仍可解析（多余字段固定忽略）");
        Require(budget.SpentTokens >= 100_000, "超大响应的记账必须耗尽预算");
        Require(budget.IsExhausted, "记账后预算必须处于耗尽状态");

        var third = planner.PlanAsync(request, context, acceptedGameTime).GetAwaiter().GetResult();
        Require(third.Source == DecisionSource.Rules && third.FallbackReason == ModelFallbackReason.BudgetExceeded,
            "预算耗尽后的下一次决策必须回退 Utility 并明确原因");
        Require(provider.CallCount == 2, "只有前两次真正调用模型，第三次被预算闸门拦截");
    }

    /// <summary>
    /// Provider 抛异常（M6）：异常文本即使包含密钥形态，也绝不能进入审计或异常路径；
    /// 决策回退 Utility，世界照常推进（不阻塞）。
    /// </summary>
    private static void ShouldFallBackToUtilityWhenProviderThrowsWithoutBlockingWorld()
    {
        var world = CreateEntryLogisticsWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var entry = new AgentRealtimeEntry(runtime);
        var context = new AgentContextCompiler().Compile(world, new CharacterId("works"));
        var now = runtime.ReadModel.GameTime;
        var request = new DecisionRequest(
            "decision-provider-throws", new CharacterId("works"), world.WorldVersion, now,
            now.Add(TimeSpan.FromHours(1)));
        var key = "sk-demo-secret-" + Guid.NewGuid().ToString("N");
        var audit = new ModelAuditLog();
        var provider = new ThrowingModelProvider(new InvalidOperationException("provider boom " + key));
        var planner = new DecisionPlanner(
            new UtilityMinisterAgent(MinisterFocus.Logistics), provider, auditLog: audit);
        var before = runtime.ReadModel;

        var result = planner.PlanAsync(request, context, now.Add(TimeSpan.FromMinutes(30))).GetAwaiter().GetResult();

        Require(result.Source == DecisionSource.Rules && result.FallbackReason == ModelFallbackReason.ProviderFailed,
            "Provider 抛异常必须回退 Utility 并明确失败原因");
        var ruleIntent = result.Intents.Single();
        Require(ruleIntent is PlanLogisticsIntent && ruleIntent.IdempotencyKey == $"logistics-ningyuan-300-{world.WorldVersion}",
            "回退必须产出 Utility 的结构化意图");

        var submit = entry.Submit(world, result.Intents).Single();
        Require(submit.Accepted, "回退意图必须可提交，世界不阻塞");
        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.ReadModel.Shipments.Count == 1, "Provider 抛异常后世界必须继续推进");

        Require(provider.CallCount == 1, "抛异常的 Provider 必须被调用一次");
        Require(audit.Entries.Single().Outcome == ModelCallOutcome.ProviderFailed, "审计必须记录 ProviderFailed");
        Require(!FlattenAudit(audit.Entries).Contains(key, StringComparison.Ordinal),
            "异常文本中的密钥绝不能进入审计");
    }

    /// <summary>
    /// Provider 超时（M6）：真实 OpenAiCompatibleModelProvider 头部停滞触发总超时硬边界，
    /// 决策回退 Utility，审计记录 ProviderFailed，世界照常推进。
    /// </summary>
    private static void ShouldFallBackToUtilityWhenProviderTimesOutWithoutBlockingWorld()
    {
        var world = CreateEntryLogisticsWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var entry = new AgentRealtimeEntry(runtime);
        var context = new AgentContextCompiler().Compile(world, new CharacterId("works"));
        var now = runtime.ReadModel.GameTime;
        var request = new DecisionRequest(
            "decision-provider-timeout", new CharacterId("works"), world.WorldVersion, now,
            now.Add(TimeSpan.FromHours(1)));
        using var client = CreateClient(new FakeHttpMessageHandler((_, _) =>
            new TaskCompletionSource<HttpResponseMessage>().Task));
        var provider = new OpenAiCompatibleModelProvider(
            client, "test-model", totalTimeout: TimeSpan.FromMilliseconds(80));
        var audit = new ModelAuditLog();
        var planner = new DecisionPlanner(
            new UtilityMinisterAgent(MinisterFocus.Logistics), provider, auditLog: audit);
        var before = runtime.ReadModel;
        var stopwatch = Stopwatch.StartNew();

        var result = planner.PlanAsync(request, context, now.Add(TimeSpan.FromMinutes(30))).GetAwaiter().GetResult();

        stopwatch.Stop();
        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Provider 超时必须受硬边界约束（实际耗时 {stopwatch.Elapsed}）");
        Require(result.Source == DecisionSource.Rules && result.FallbackReason == ModelFallbackReason.ProviderFailed,
            "Provider 超时必须回退 Utility");
        Require(audit.Entries.Single().Outcome == ModelCallOutcome.ProviderFailed, "审计必须记录 ProviderFailed");

        var submit = entry.Submit(world, result.Intents).Single();
        Require(submit.Accepted, "超时回退意图必须可提交");
        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.ReadModel.Shipments.Count == 1, "Provider 超时后世界必须继续推进");
    }

    /// <summary>
    /// 密钥安全（M6）：密钥只从环境变量读取一次并写入 Bearer 认证头；
    /// 解析器不保留密钥字段、不提供任何读回途径；环境变量未配置时明确失败。
    /// </summary>
    private static void ShouldReadApiKeyOnlyFromEnvironmentVariable()
    {
        var key = "sk-demo-secret-" + Guid.NewGuid().ToString("N");
        var resolver = new ModelKeySource(() => key);
        using var client = resolver.CreateKeyedHttpClient("https://provider.test/v1/");

        Require(client.BaseAddress == new Uri("https://provider.test/v1/"), "HttpClient 必须使用配置的 BaseAddress");
        Require(client.DefaultRequestHeaders.Authorization is { Scheme: "Bearer" } &&
                client.DefaultRequestHeaders.Authorization.Parameter == key,
            "密钥必须只进入 Bearer 认证头");

        // 解析器任何字段都不得留存密钥：密钥只在本地变量中短暂存在，设置完认证头即被丢弃。
        var retained = typeof(ModelKeySource)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => field.GetValue(resolver) as string)
            .Where(value => value == key)
            .ToArray();
        Require(retained.Length == 0, "ModelKeySource 不得在任何字段中保留密钥");

        var missing = new ModelKeySource(() => null);
        RequireThrows<InvalidOperationException>(
            () => missing.CreateKeyedHttpClient("https://provider.test/v1/"),
            "环境变量未配置时必须明确失败，而不是生成空密钥客户端");
    }

    /// <summary>
    /// 审计不泄露（M6）：无论模型成功、失败，审计都只记录固定摘要（决策 ID、provider 名称、
    /// 结果、请求/响应 token、金额、耗时），绝不回显模型文本或异常细节；
    /// 密钥字符串不得出现于审计文本、DecisionResult 或任何抛出的异常消息。
    /// </summary>
    private static void ShouldKeepKeyOutOfAuditAndExceptions()
    {
        var world = CreateEntryLogisticsWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var context = new AgentContextCompiler().Compile(world, new CharacterId("works"));
        var now = runtime.ReadModel.GameTime;
        var request = new DecisionRequest(
            "decision-key-audit", new CharacterId("works"), world.WorldVersion, now,
            now.Add(TimeSpan.FromHours(1)));
        var key = "sk-demo-secret-" + Guid.NewGuid().ToString("N");
        // 模型输出不可信：即使模型把密钥形态文本塞进参数（多余字段固定忽略），审计也不得回显。
        var taintedModelJson = "{\"schema_version\":1,\"intent_type\":\"logistics.request_shipment\"," +
                               "\"parameters\":{\"route_id\":\"capital-ningyuan-grain\",\"grain_quantity\":200,\"note\":\"" +
                               key + "\"}}";
        var audit = new ModelAuditLog();
        var planner = new DecisionPlanner(
            new UtilityMinisterAgent(MinisterFocus.Logistics),
            new FakeModelProvider(taintedModelJson),
            auditLog: audit);
        var acceptedGameTime = now.Add(TimeSpan.FromMinutes(30));

        var result = planner.PlanAsync(request, context, acceptedGameTime).GetAwaiter().GetResult();
        Require(result.Source == DecisionSource.Model, "带多余字段的模型输出应被采用（多余字段固定忽略）");
        Require(!result.ToString().Contains(key, StringComparison.Ordinal), "DecisionResult 文本不得包含密钥");

        var acceptedEntry = audit.Entries.Single();
        Require(acceptedEntry.Outcome == ModelCallOutcome.Accepted, "审计必须记录 Accepted");
        Require(acceptedEntry.RequestTokens > 0 && acceptedEntry.ResponseTokens > 0,
            "审计必须记录请求/响应 token 估算");
        Require(!FlattenAudit(audit.Entries).Contains(key, StringComparison.Ordinal),
            "成功路径审计不得包含密钥");

        // 失败路径：Provider 返回失败，审计只记固定摘要，同样不得回显模型文本。
        var failingAudit = new ModelAuditLog();
        var failingPlanner = new DecisionPlanner(
            new UtilityMinisterAgent(MinisterFocus.Logistics),
            new FakeModelProvider(taintedModelJson, succeeded: false),
            auditLog: failingAudit);
        var failing = failingPlanner.PlanAsync(request, context, acceptedGameTime).GetAwaiter().GetResult();
        Require(failing.Source == DecisionSource.Rules && failing.FallbackReason == ModelFallbackReason.ProviderFailed,
            "模型失败必须回退 Utility");
        Require(failingAudit.Entries.Single().Outcome == ModelCallOutcome.ProviderFailed,
            "失败路径审计必须记录 ProviderFailed");
        Require(!FlattenAudit(failingAudit.Entries).Contains(key, StringComparison.Ordinal),
            "失败路径审计不得包含密钥");
    }

    private static bool IsSecretMemberName(string name) =>
        name.Contains("ApiKey", StringComparison.Ordinal) ||
        name.Contains("Secret", StringComparison.Ordinal) ||
        name.Contains("Token", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyGame.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到 MyGame.sln 仓库根目录。");
    }

    /// <summary>入口契约测试用的物流世界：works 有粮运授权，war 存在但无授权。</summary>
    private static WorldState CreateEntryLogisticsWorld()
    {
        var map = new MapDefinition(
            "agent-entry-logistics-map",
            [
                new ProvinceDefinition(new ProvinceId("capital"), "京师", [new ProvinceId("liaodong")]),
                new ProvinceDefinition(new ProvinceId("liaodong"), "辽东", [new ProvinceId("capital")]),
            ]);
        return WorldState.CreateInitial(
            new WorldId("agent-entry-logistics"),
            1,
            200_000,
            map,
            characters:
            [
                new CharacterState(new CharacterId("works"), "户部运粮官",
                    new CharacterAttributes(80, 60, 30, 40, 70),
                    new CharacterPersonality(true, false, true, true)),
                new CharacterState(new CharacterId("war"), "无物流权限角色",
                    new CharacterAttributes(60, 40, 80, 50, 60),
                    new CharacterPersonality(true, true, true, false)),
            ],
            capabilityGrants:
            [
                new CapabilityGrant(new CharacterId("works"), GameCapability.PlanLogistics, "capital-ningyuan-grain"),
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

    /// <summary>入口契约测试用的行军世界：war 有行军授权，works 存在但无授权。</summary>
    private static WorldState CreateEntryMoveWorld()
    {
        var map = new MapDefinition(
            "agent-entry-move-map",
            [
                new ProvinceDefinition(new ProvinceId("frontier"), "边地", [new ProvinceId("capital")]),
                new ProvinceDefinition(new ProvinceId("capital"), "京师", [new ProvinceId("frontier")]),
            ]);
        return WorldState.CreateInitial(
            new WorldId("agent-entry-move"),
            1,
            200_000,
            map,
            characters:
            [
                new CharacterState(new CharacterId("war"), "兵部角色",
                    new CharacterAttributes(60, 40, 80, 50, 60),
                    new CharacterPersonality(true, true, true, false)),
                new CharacterState(new CharacterId("works"), "无行军权限角色",
                    new CharacterAttributes(80, 60, 30, 40, 70),
                    new CharacterPersonality(true, false, true, true)),
            ],
            capabilityGrants:
            [
                new CapabilityGrant(new CharacterId("war"), GameCapability.MoveArmy, "army-1"),
            ],
            armies:
            [
                new ArmyState(new ArmyId("army-1"), "测试军", new ProvinceId("frontier"), 10_000, 3_000),
            ]);
    }

    /// <summary>效用打分测试世界：works 同时拥有建厂/改编/粮运授权，条件全部成立。</summary>
    private static WorldState CreateUtilityScoringWorld()
    {
        var map = new MapDefinition(
            "utility-scoring-map",
            [
                new ProvinceDefinition(new ProvinceId("capital"), "京师", [new ProvinceId("liaodong")]),
                new ProvinceDefinition(new ProvinceId("liaodong"), "辽东", [new ProvinceId("capital")]),
            ]);
        return WorldState.CreateInitial(
            new WorldId("utility-scoring"),
            1,
            200_000, // 银两满足建厂预算条件
            map,
            characters:
            [
                new CharacterState(new CharacterId("works"), "全能大臣",
                    new CharacterAttributes(80, 60, 30, 40, 70),
                    new CharacterPersonality(true, false, true, true)),
            ],
            capabilityGrants:
            [
                new CapabilityGrant(new CharacterId("works"), GameCapability.BuildIndustry),
                new CapabilityGrant(new CharacterId("works"), GameCapability.ConvertArmy),
                new CapabilityGrant(new CharacterId("works"), GameCapability.PlanLogistics, "capital-ningyuan-grain"),
            ],
            armies:
            [
                new ArmyState(new ArmyId("army-1"), "边军", new ProvinceId("capital"), 1_000, 0),
            ]);
    }

    /// <summary>把审计条目拍平成可检查的文本，便于断言其中绝不含密钥形态文本。</summary>
    private static string FlattenAudit(IReadOnlyList<ModelAuditEntry> entries) =>
        string.Join("\n", entries.Select(entry =>
            $"{entry.DecisionId}|{entry.ProviderName}|{entry.Outcome}|{entry.RequestTokens}|{entry.ResponseTokens}|{entry.CostMillis}|{entry.Duration}|{entry.RecordedAt:O}"));

    /// <summary>
    /// 契约测试用的假 Provider：只返回预先写好的文本，不联网、不携带任何密钥；
    /// succeeded 为 false 时模拟模型失败/超时。CallCount 记录实际发起的模型调用次数。
    /// </summary>
    private sealed class FakeModelProvider(string content, bool succeeded = true) : IModelProvider
    {
        public int CallCount { get; private set; }

        public Task<ModelResponse> GenerateAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new ModelResponse(succeeded, content));
        }
    }

    /// <summary>按调用序号依次返回内容的假 Provider；调用超过内容数量时重复最后一项。</summary>
    private sealed class SequencedFakeModelProvider(params string[] contents) : IModelProvider
    {
        public int CallCount { get; private set; }

        public Task<ModelResponse> GenerateAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var index = Math.Min(CallCount - 1, contents.Length - 1);
            return Task.FromResult(new ModelResponse(true, contents[index]));
        }
    }

    /// <summary>每次调用都以固定异常失败的假 Provider（模拟 Provider 抛异常/断网）。</summary>
    private sealed class ThrowingModelProvider(Exception exception) : IModelProvider
    {
        public int CallCount { get; private set; }

        public Task<ModelResponse> GenerateAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromException<ModelResponse>(exception);
        }
    }
}

