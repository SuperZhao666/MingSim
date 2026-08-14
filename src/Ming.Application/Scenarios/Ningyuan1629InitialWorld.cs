using System.Text.Json;
using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Institutions;
using MingSim.Domain.Map;
using MingSim.Domain.Scenario;

namespace MingSim.Application.Scenarios;

/// <summary>
/// 宁远急饷 1629 垂直切片（I2）的受控剧本装配：读 world.json 并构造 CreateInitial 的
/// 全部初始对象。所有玩法数值来自 content 文件中的 DESIGN 条目（doc 03 §7.1），
/// 不在这里硬编码任何史实断言；加载失败一律抛异常（fail-closed），绝不静默空世界。
/// </summary>
public static class Ningyuan1629InitialWorld
{
    /// <summary>剧本起点：崇祯二年正月初一（doc 03 §2 DESIGN 开局日）。</summary>
    private static readonly DateTimeOffset ScenarioStart = new(1629, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static WorldState Load(string worldJsonPath = "content/scenarios/ming_1629/world.json")
    {
        if (!File.Exists(worldJsonPath))
        {
            throw new FileNotFoundException($"宁远 1629 剧本文件不存在：{worldJsonPath}", worldJsonPath);
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(worldJsonPath));
        var root = document.RootElement;
        var id = new WorldId(GetString(root, "id"));
        var treasurySilver = root.GetProperty("treasurySilver").GetInt64();

        var provinces = new List<ProvinceDefinition>();
        foreach (var province in root.GetProperty("map").GetProperty("provinces").EnumerateArray())
        {
            provinces.Add(new ProvinceDefinition(
                new ProvinceId(GetString(province, "id")),
                GetString(province, "name"),
                province.GetProperty("adjacentTo").EnumerateArray().Select(item => new ProvinceId(item.GetString()!))));
        }

        var characters = new List<CharacterState>();
        foreach (var character in root.GetProperty("characters").EnumerateArray())
        {
            // 史实人物不写能力/人格数值（证据生产规范禁止未证实数值）；
            // 用中性 DESIGN 占位，规则不依赖它们做裁决。
            characters.Add(new CharacterState(
                new CharacterId(GetString(character, "id")),
                GetString(character, "name"),
                new CharacterAttributes(50, 50, 50, 50, 50),
                new CharacterPersonality(Honest: true, Bold: false, LoyalToRuler: true, Compassionate: false),
                character.TryGetProperty("locationId", out var location) && location.ValueKind == JsonValueKind.String
                    ? new ProvinceId(location.GetString()!)
                    : new ProvinceId("capital"),
                character.TryGetProperty("officeId", out var office) && office.ValueKind == JsonValueKind.String
                    ? office.GetString()
                    : null));
        }

        var institutions = new List<InstitutionState>();
        foreach (var institution in root.GetProperty("institutions").EnumerateArray())
        {
            institutions.Add(new InstitutionState(
                new InstitutionId(GetString(institution, "id")),
                GetString(institution, "name"),
                institution.TryGetProperty("capabilities", out var capabilities)
                    ? capabilities.EnumerateArray().Select(item => ParseCapability(item.GetString()!)).ToArray()
                    : null,
                institution.TryGetProperty("members", out var members)
                    ? members.EnumerateArray().Select(item => new CharacterId(item.GetString()!)).ToArray()
                    : null));
        }

        var grants = new List<CapabilityGrant>();
        foreach (var grant in root.GetProperty("capabilityGrants").EnumerateArray())
        {
            grants.Add(new CapabilityGrant(
                new CharacterId(GetString(grant, "actorId")),
                ParseCapability(GetString(grant, "capability")),
                grant.TryGetProperty("resourceId", out var resource) && resource.ValueKind == JsonValueKind.String
                    ? resource.GetString()
                    : null));
        }

        var stockpiles = new List<StockpileState>();
        foreach (var stockpile in root.GetProperty("stockpiles").EnumerateArray())
        {
            stockpiles.Add(new StockpileState(
                new StockpileId(GetString(stockpile, "id")),
                new ProvinceId(GetString(stockpile, "locationId")),
                stockpile.GetProperty("capacity").GetInt64(),
                stockpile.GetProperty("grainQuantity").GetInt64()));
        }

        var routes = new List<RouteState>();
        foreach (var route in root.GetProperty("routes").EnumerateArray())
        {
            routes.Add(new RouteState(
                new RouteId(GetString(route, "id")),
                new StockpileId(GetString(route, "fromStockpileId")),
                new StockpileId(GetString(route, "toStockpileId")),
                route.GetProperty("capacity").GetInt64(),
                route.GetProperty("travelHours").GetInt32(),
                route.GetProperty("lossPerThousand").GetInt32()));
        }

        // 场景状态：前线粮仓宁远启用日耗/战备规则（DESIGN 数值用 ScenarioState 常量，
        // 与 world.json 的 design.numbers 一致——doc 03 §7.1 / 17 号账本 NUM-001~007）。
        var scenario = new ScenarioState(frontStockpileId: new StockpileId("sp-ningyuan"));

        return WorldState.CreateInitial(
            id,
            turnNumber: 1,
            treasurySilver,
            new MapDefinition(GetString(root.GetProperty("map"), "id"), provinces),
            ScenarioStart,
            characters,
            institutions,
            grants,
            stockpiles: stockpiles,
            routes: routes,
            scenario: scenario);
    }

    private static string GetString(JsonElement element, string property) =>
        element.GetProperty(property).GetString()
        ?? throw new InvalidDataException($"剧本字段 {property} 不能为空。");

    private static GameCapability ParseCapability(string value) =>
        Enum.TryParse<GameCapability>(value, out var capability)
            ? capability
            : throw new InvalidDataException($"剧本中出现未知能力：{value}");
}
