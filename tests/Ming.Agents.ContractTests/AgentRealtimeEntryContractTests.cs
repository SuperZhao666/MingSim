using System.Text.RegularExpressions;
using MingSim.Agents.Decision;
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
            [new AgentRegistration(new CharacterId("works"), new RuleBasedMinisterAgent(MinisterFocus.Logistics))]);
        var before = runtime.ReadModel;

        var results = entry.Submit(world, intents);
        Require(results.Count == 1 && results[0].Accepted,
            "授权规则代理的粮运意图必须通过入口预检并进入收件箱");
        Require(results[0].CommandId == "turn-1-logistics-ningyuan-300",
            "入口必须把意图的幂等键作为稳定命令编号");

        var advanced = runtime.AdvanceTo(before.GameTime);
        var commandResult = advanced.CommandResults.Single();
        Require(commandResult.Accepted, "内核必须在安全点受理授权粮运命令");
        Require(commandResult.ResultingWorldVersion == before.WorldVersion + 1,
            "内核受理命令本身必须恰好 +1 WorldVersion");
        // 受理命令 +1（CommandAccepted 提交），同一安全点内 ShipmentDeparture 事件再 +1。
        Require(advanced.ReadModel.WorldVersion == before.WorldVersion + 2,
            "受理粮运必须产生命令与出发两个原子提交");
        Require(advanced.ReadModel.Shipments.Any(shipment => shipment.Id.Value == "shipment-turn-1-logistics-ningyuan-300"),
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
}
