using System.Text.Json;
using MingSim.Agents.Decision;
using MingSim.Agents.Realtime;
using MingSim.Agents.Runtime;
using MingSim.Application.Scenarios;
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
/// 权威行动候选集（P1-AGENT-01/02）契约测试：
/// - AgentContext 只含最小、已过滤的可行动候选：路线候选（含起终点/余量/容量/在途/损耗）
///   与军队观察（含 LocationId 与邻接合法目的地），绝不把完整 WorldState 塞给模型；
/// - Utility 规则回退的粮运意图必须从候选集选择真实存在的路线（1629 真实场景验证）；
/// - ModelDecisionParser 校验模型返回的 route_id/army_id/destination_id 必须属于
///   上下文候选集，否则 ParseFailed 回退规则路径，模型文本不能伪造世界对象。
/// </summary>
internal static partial class Program
{
    /// <summary>
    /// P1-AGENT-01 回归：在真实 1629 场景（world.json 不含 capital-ningyuan-grain 等虚构路线）
    /// 中，Utility 规则回退产出的粮运意图必须选择候选集中真实存在的路线：
    /// 路线必须同时存在于 world.json 路线集（直接从内容文件读取的权威集合）与上下文候选集，
    /// 并经入口提交、内核受理，证明该意图真实可行动。
    /// </summary>
    private static void ShouldUseRealRouteFromWorldJsonWhenRulesFallbackIn1629Scenario()
    {
        var worldJsonPath = Path.Combine(FindRepositoryRoot(), "content", "scenarios", "ming_1629", "world.json");
        Require(File.Exists(worldJsonPath), "1629 场景 world.json 必须存在");

        var authoritativeRouteIds = ReadWorldJsonRouteIds(worldJsonPath);
        Require(authoritativeRouteIds.Count > 0, "world.json 必须声明至少一条路线");
        Require(!authoritativeRouteIds.Contains("capital-ningyuan-grain"),
            "world.json 不得包含审计中虚构的 capital-ningyuan-grain 路线（测试前置不变式）");

        var world = Ningyuan1629InitialWorld.Load(worldJsonPath);
        var actorId = new CharacterId("duliaoxiang-slot");
        var context = new AgentContextCompiler().Compile(world, actorId);
        Require(context.Routes.Count > 0, "1629 场景初始上下文必须包含可行动路线候选");

        var intents = new UtilityMinisterAgent(MinisterFocus.Logistics).Decide(context);
        Require(intents.Count == 1, "物流专注大臣在 1629 场景必须有可用粮运意图");
        var intent = intents.Single() as PlanLogisticsIntent;
        Require(intent is not null, "规则回退必须产出粮运意图");

        var chosenRouteId = intent!.RouteId.Value;
        Require(authoritativeRouteIds.Contains(chosenRouteId),
            $"回退意图路线 {chosenRouteId} 必须存在于 world.json 路线集");
        Require(context.Routes.Any(route => route.RouteId.Value == chosenRouteId),
            $"回退意图路线 {chosenRouteId} 必须在上下文候选集中");

        var runtime = new RealtimeSimulationRuntime(world);
        var entry = new AgentRealtimeEntry(runtime);
        var before = runtime.ReadModel;
        var results = entry.Submit(world, intents);
        Require(results.Count == 1 && results[0].Accepted,
            "候选集中的真实路线意图必须通过入口预检");
        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.CommandResults.Single().Accepted,
            "内核必须受理候选集真实路线的粮运命令");
        Require(advanced.ReadModel.Shipments.Count == 1 &&
                advanced.ReadModel.Shipments.Single().RouteId.Value == chosenRouteId,
            "内核必须按候选集真实路线创建运输单");
    }

    /// <summary>
    /// P1-AGENT-02：模型返回不在上下文候选集的 route_id 时，解析必须 ParseFailed 回退规则路径，
    /// 绝不把虚构路线意图放行给内核。
    /// </summary>
    private static void ShouldRejectModelRouteIdOutsideCandidateSetWithParseFailedFallback()
    {
        var world = CreateEntryLogisticsWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var context = new AgentContextCompiler().Compile(world, new CharacterId("works"));
        var now = runtime.ReadModel.GameTime;
        var request = new DecisionRequest(
            "decision-route-not-candidate", new CharacterId("works"), world.WorldVersion, now,
            now.Add(TimeSpan.FromHours(1)));
        var planner = new DecisionPlanner(
            new UtilityMinisterAgent(MinisterFocus.Logistics),
            new FakeModelProvider(
                """{"schema_version":1,"intent_type":"logistics.request_shipment","parameters":{"route_id":"route-does-not-exist","grain_quantity":200}}"""));

        var result = planner.PlanAsync(request, context, now.Add(TimeSpan.FromMinutes(30))).GetAwaiter().GetResult();

        Require(result.Source == DecisionSource.Rules && result.FallbackReason == ModelFallbackReason.ParseFailed,
            "模型返回候选集之外的 route_id 必须 ParseFailed 回退");
        var ruleIntent = result.Intents.Single();
        Require(ruleIntent is PlanLogisticsIntent,
            "ParseFailed 回退必须产出规则路径（Utility）的结构化意图");

        // 路线 ID 合法也不能让模型自行填写超出候选快照可执行上限的数量；
        // 否则会把明显过期/幻觉动作一直推到内核才拒绝，失去 candidate 协议的意义。
        var route = context.Routes.Single();
        var tooMuch = Math.Min(long.MaxValue, route.SourceGrain + 1);
        var oversizedPlanner = new DecisionPlanner(
            new UtilityMinisterAgent(MinisterFocus.Logistics),
            new FakeModelProvider(
                $"{{\"schema_version\":1,\"intent_type\":\"logistics.request_shipment\",\"parameters\":{{\"route_id\":\"{route.RouteId.Value}\",\"grain_quantity\":{tooMuch}}}}}"));
        var oversized = oversizedPlanner.PlanAsync(request, context, now.Add(TimeSpan.FromMinutes(30))).GetAwaiter().GetResult();
        Require(oversized.Source == DecisionSource.Rules && oversized.FallbackReason == ModelFallbackReason.ParseFailed,
            "模型数量超过候选路线可执行上限必须 ParseFailed 回退");
    }

    /// <summary>
    /// P1-AGENT-02：模型返回不在候选集的 army_id 或非邻接 destination_id 时，
    /// 解析必须 ParseFailed 回退；候选集内的合法行军意图仍被采用（正向对照）。
    /// </summary>
    private static void ShouldRejectModelArmyOrDestinationOutsideCandidateSetWithParseFailedFallback()
    {
        var world = CreateEntryMoveWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var context = new AgentContextCompiler().Compile(world, new CharacterId("war"));
        var now = runtime.ReadModel.GameTime;
        var request = new DecisionRequest(
            "decision-move-not-candidate", new CharacterId("war"), world.WorldVersion, now,
            now.Add(TimeSpan.FromHours(1)));
        var acceptedGameTime = now.Add(TimeSpan.FromMinutes(30));

        // 非邻接目的地：army-1 在 frontier，邻接只有 capital，beijing 不在邻接集合。
        var notAdjacent = new DecisionPlanner(
            new UtilityMinisterAgent(MinisterFocus.Logistics),
            new FakeModelProvider(
                """{"schema_version":1,"intent_type":"military.move_army","parameters":{"army_id":"army-1","destination_id":"beijing"}}"""));
        var notAdjacentResult = notAdjacent.PlanAsync(request, context, acceptedGameTime).GetAwaiter().GetResult();
        Require(notAdjacentResult.Source == DecisionSource.Rules &&
                notAdjacentResult.FallbackReason == ModelFallbackReason.ParseFailed,
            "非邻接 destination_id 必须 ParseFailed 回退");

        // 不存在的军队：army_id 不在上下文军队候选集。
        var unknownArmy = new DecisionPlanner(
            new UtilityMinisterAgent(MinisterFocus.Logistics),
            new FakeModelProvider(
                """{"schema_version":1,"intent_type":"military.move_army","parameters":{"army_id":"army-ghost","destination_id":"capital"}}"""));
        var unknownArmyResult = unknownArmy.PlanAsync(request, context, acceptedGameTime).GetAwaiter().GetResult();
        Require(unknownArmyResult.Source == DecisionSource.Rules &&
                unknownArmyResult.FallbackReason == ModelFallbackReason.ParseFailed,
            "候选集之外的 army_id 必须 ParseFailed 回退");

        // 正向对照：候选集内的合法行军意图必须被解析采用（不允许一刀切拒绝）。
        var valid = new DecisionPlanner(
            new UtilityMinisterAgent(MinisterFocus.Logistics),
            new FakeModelProvider(
                """{"schema_version":1,"intent_type":"military.move_army","parameters":{"army_id":"army-1","destination_id":"capital"}}"""));
        var validResult = valid.PlanAsync(request, context, acceptedGameTime).GetAwaiter().GetResult();
        Require(validResult.Source == DecisionSource.Model, "候选集内的合法行军意图必须被采用");
        var validIntent = validResult.Intents.Single() as MoveArmyIntent;
        Require(validIntent is not null && validIntent.ArmyId.Value == "army-1" &&
                validIntent.DestinationId.Value == "capital",
            "采用的行军意图必须携带模型声明的候选集内军队与目的地");
    }

    /// <summary>
    /// P1-AGENT-01/02：AgentContext 必须包含最小、已过滤的可行动路线候选与军队邻接合法目的地；
    /// 起点无粮的路线不得进入候选集；在途粮食实时反映世界状态。
    /// </summary>
    private static void ShouldExposeRouteCandidatesAndArmyAdjacentDestinationsInAgentContext()
    {
        var world = CreateAuthoritativeCandidatesWorld();
        var runtime = new RealtimeSimulationRuntime(world);
        var entry = new AgentRealtimeEntry(runtime);
        var actorId = new CharacterId("works");
        var context = new AgentContextCompiler().Compile(world, actorId);

        // 路线候选：只有可行动路线进入候选集，且携带全部最小观察字段。
        var candidate = context.Routes.SingleOrDefault(route => route.RouteId.Value == "route-capital-tongzhou");
        Require(candidate is not null, "可行动路线必须进入候选集");
        Require(candidate!.From.Value == "capital" && candidate.To.Value == "tongzhou",
            "路线候选必须携带起点/终点省份");
        Require(candidate.SourceGrain == 1_000, "路线候选必须携带起点库存");
        Require(candidate.DestinationHeadroom == 500, "路线候选必须携带目的地余量（容量-存量-在途预留）");
        Require(candidate.RouteCapacity == 500, "路线候选必须携带路线容量");
        Require(candidate.InTransitGrain == 0, "初始路线候选在途粮食必须为 0");
        Require(candidate.TravelHours == 48 && candidate.LossPerThousand == 80,
            "路线候选必须携带行程时间与损耗率");
        Require(context.Routes.Any(route => route.RouteId.Value == "route-tongzhou-ningyuan"),
            "第二条约定的可行动路线必须进入候选集");
        Require(!context.Routes.Any(route => route.RouteId.Value == "route-shanhaiguan-ningyuan"),
            "起点无粮的路线不得进入可行动候选集");

        // 军队观察：必须携带 LocationId 与邻接合法目的地。
        var army = context.Armies.SingleOrDefault(observation => observation.ArmyId.Value == "army-1");
        Require(army is not null, "军队必须进入上下文观察");
        Require(army!.LocationId.Value == "capital", "军队观察必须携带当前位置");
        Require(army.AdjacentDestinations.Select(province => province.Value).SequenceEqual(["liaodong", "tongzhou"]),
            "军队观察必须携带排序后的邻接合法目的地");

        // 在途状态反映权威世界：候选路线上的运输单被内核受理后进入在途
        // （ReadModel 是内核状态的唯一只读视图；编译器的 InTransitGrain/余量
        // 来自同一权威 WorldState 的 Logistics 域方法，已被 SmokeTests 覆盖）。
        var logisticsIntent = new PlanLogisticsIntent(
            "decision-in-transit", actorId, world.TurnNumber,
            "in-transit-1", runtime.ReadModel.WorldVersion,
            new RouteId("route-capital-tongzhou"), 300, runtime.ReadModel.GameTime.Value);
        var before = runtime.ReadModel;
        var submit = entry.Submit(world, [logisticsIntent]).Single();
        Require(submit.Accepted, "候选路线的粮运意图必须通过入口预检");
        var advanced = runtime.AdvanceTo(before.GameTime);
        Require(advanced.CommandResults.Single().Accepted, "粮运命令必须被内核受理");
        var shipment = advanced.ReadModel.Shipments.Single();
        Require(shipment.RouteId.Value == "route-capital-tongzhou" &&
                shipment.Status == ShipmentStatus.InTransit,
            "内核必须把候选路线运输单置于在途状态（InTransitGrain 将按此状态累计）");
    }

    /// <summary>直接从 world.json 内容文件读取路线集合：它就是"权威路线集"本身。</summary>
    private static IReadOnlyList<string> ReadWorldJsonRouteIds(string worldJsonPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(worldJsonPath));
        var routes = document.RootElement.GetProperty("routes");
        var ids = new List<string>();
        foreach (var route in routes.EnumerateArray())
        {
            ids.Add(route.GetProperty("id").GetString()!);
        }

        return ids;
    }

    /// <summary>权威候选测试世界：三条路线（一条起点无粮被过滤）+ 一支有邻接目的地的军队。</summary>
    private static WorldState CreateAuthoritativeCandidatesWorld()
    {
        var map = new MapDefinition(
            "authoritative-candidates-map",
            [
                new ProvinceDefinition(new ProvinceId("capital"), "京师",
                    [new ProvinceId("tongzhou"), new ProvinceId("liaodong")]),
                new ProvinceDefinition(new ProvinceId("tongzhou"), "通州",
                    [new ProvinceId("capital"), new ProvinceId("shanhaiguan")]),
                new ProvinceDefinition(new ProvinceId("shanhaiguan"), "山海关",
                    [new ProvinceId("tongzhou"), new ProvinceId("ningyuan")]),
                new ProvinceDefinition(new ProvinceId("ningyuan"), "宁远",
                    [new ProvinceId("shanhaiguan")]),
                new ProvinceDefinition(new ProvinceId("liaodong"), "辽东",
                    [new ProvinceId("capital")]),
            ]);
        return WorldState.CreateInitial(
            new WorldId("authoritative-candidates"),
            1,
            200_000,
            map,
            characters:
            [
                new CharacterState(new CharacterId("works"), "户部运粮官",
                    new CharacterAttributes(80, 60, 30, 40, 70),
                    new CharacterPersonality(true, false, true, true)),
                new CharacterState(new CharacterId("war"), "兵部角色",
                    new CharacterAttributes(60, 40, 80, 50, 60),
                    new CharacterPersonality(true, true, true, false)),
            ],
            capabilityGrants:
            [
                new CapabilityGrant(new CharacterId("works"), GameCapability.PlanLogistics),
                new CapabilityGrant(new CharacterId("works"), GameCapability.MoveArmy, "army-1"),
            ],
            stockpiles:
            [
                new StockpileState(new StockpileId("capital-granary"), new ProvinceId("capital"), 2_000, 1_000),
                new StockpileState(new StockpileId("tongzhou-granary"), new ProvinceId("tongzhou"), 1_000, 500),
                new StockpileState(new StockpileId("ningyuan-granary"), new ProvinceId("ningyuan"), 1_000, 0),
                new StockpileState(new StockpileId("shanhaiguan-granary"), new ProvinceId("shanhaiguan"), 1_000, 0),
            ],
            routes:
            [
                new RouteState(new RouteId("route-capital-tongzhou"),
                    new StockpileId("capital-granary"), new StockpileId("tongzhou-granary"),
                    500, 48, 80),
                new RouteState(new RouteId("route-tongzhou-ningyuan"),
                    new StockpileId("tongzhou-granary"), new StockpileId("ningyuan-granary"),
                    500, 120, 80),
                // 起点库存为 0：不可行动，必须被过滤出候选集。
                new RouteState(new RouteId("route-shanhaiguan-ningyuan"),
                    new StockpileId("shanhaiguan-granary"), new StockpileId("ningyuan-granary"),
                    500, 120, 80),
            ],
            armies:
            [
                new ArmyState(new ArmyId("army-1"), "边军", new ProvinceId("capital"), 10_000, 3_000),
            ]);
    }
}
