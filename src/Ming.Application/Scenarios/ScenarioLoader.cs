using System.Text.Json;
using System.Text.Json.Serialization;
using MingSim.Domain;
using MingSim.Domain.Authorization;
using MingSim.Domain.Characters;
using MingSim.Domain.Common;
using MingSim.Domain.Economy;
using MingSim.Domain.Institutions;
using MingSim.Domain.Map;
using MingSim.Domain.Military;

namespace MingSim.Application.Scenarios;

/// <summary>
/// 从 JSON 内容文件创建初始世界。
/// </summary>
/// <remarks>
/// 这样历史人物和制度内容可以独立迭代，C# 只负责理解“角色、机构、能力、军队”等通用结构，
/// 不需要把某个具体人物写死在程序里。
/// </remarks>
public sealed class ScenarioLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    public WorldState Load(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var definition = JsonSerializer.Deserialize<ScenarioDefinition>(json, JsonOptions)
            ?? throw new InvalidDataException($"无法读取剧本文件 {filePath}。 ");

        var map = new MapDefinition(
            string.IsNullOrWhiteSpace(definition.Map.Id) ? $"{definition.Id}-map" : definition.Map.Id,
            definition.Map.Provinces.Select(province => new ProvinceDefinition(
                new ProvinceId(province.Id),
                province.Name,
                (province.AdjacentTo ?? []).Select(id => new ProvinceId(id)))));

        var characters = definition.Characters.Select(character =>
        {
            return new CharacterState(new CharacterId(character.Id), character.Name,
                character.Attributes.Normalize(), character.Personality,
                new ProvinceId(character.LocationId), character.OfficeId);
        }).ToArray();

        var institutions = definition.Institutions.Select(institution =>
        {
            return new InstitutionState(new InstitutionId(institution.Id), institution.Name,
                institution.Capabilities, institution.Members.Select(member => new CharacterId(member)));
        }).ToArray();

        var grants = definition.CapabilityGrants.Select(grant => new CapabilityGrant(
            new CharacterId(grant.ActorId), grant.Capability, grant.ResourceId, grant.ExpiresAtTurn)).ToArray();
        var inventory = definition.Inventory.Select(item => (item.ResourceType, item.Quantity)).ToArray();
        var armies = definition.Armies.Select(army => new ArmyState(new ArmyId(army.Id), army.Name,
            new ProvinceId(army.LocationId), army.Auxiliaries, army.LineInfantry)).ToArray();

        var world = WorldState.CreateInitial(new WorldId(definition.Id), definition.StartTurn,
            definition.TreasurySilver, map, currentTime: null, characters, institutions, grants, inventory, armies);

        return world;
    }
}

/// <summary>JSON 的根对象。它是内容格式，不是运行时领域对象。</summary>
public sealed class ScenarioDefinition
{
    public string Id { get; set; } = string.Empty;

    public int StartTurn { get; set; }

    public long TreasurySilver { get; set; }

    /// <summary>
    /// 地图是静态内容，独立于角色、库存和军队等动态状态。
    /// </summary>
    public ScenarioMapDefinition Map { get; set; } = new();

    public List<CharacterDefinition> Characters { get; set; } = [];

    public List<InstitutionDefinition> Institutions { get; set; } = [];

    public List<CapabilityGrantDefinition> CapabilityGrants { get; set; } = [];

    public List<InventoryDefinition> Inventory { get; set; } = [];

    public List<ArmyDefinition> Armies { get; set; } = [];
}

/// <summary>剧本 JSON 中的地图内容格式。</summary>
public sealed class ScenarioMapDefinition
{
    public string Id { get; set; } = string.Empty;

    public List<ScenarioProvinceDefinition> Provinces { get; set; } = [];
}

/// <summary>剧本 JSON 中的单个省份格式。</summary>
public sealed class ScenarioProvinceDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<string> AdjacentTo { get; set; } = [];
}

public sealed class CharacterDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string LocationId { get; set; } = "capital";

    public string? OfficeId { get; set; }

    public CharacterAttributes Attributes { get; set; } = new(50, 50, 50, 50, 50);

    public CharacterPersonality Personality { get; set; } = new(true, false, true, true);
}

public sealed class InstitutionDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<GameCapability> Capabilities { get; set; } = [];

    public List<string> Members { get; set; } = [];
}

public sealed class CapabilityGrantDefinition
{
    public string ActorId { get; set; } = string.Empty;

    public GameCapability Capability { get; set; }

    public string? ResourceId { get; set; }

    public int? ExpiresAtTurn { get; set; }
}

public sealed class InventoryDefinition
{
    public string ResourceType { get; set; } = string.Empty;

    public long Quantity { get; set; }
}

public sealed class ArmyDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string LocationId { get; set; } = "capital";

    public long Auxiliaries { get; set; }

    public long LineInfantry { get; set; }
}
