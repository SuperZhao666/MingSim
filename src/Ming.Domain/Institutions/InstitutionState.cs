using MingSim.Domain.Common;

namespace MingSim.Domain.Institutions;

/// <summary>
/// 一个政府机构或社会组织。
/// </summary>
/// <remarks>
/// 机构不是一个大模型 Agent，而是一个受保护的能力边界。
/// 例如工部可以暴露“建造工坊”的接口，但只有拥有相应授权的角色才能调用。
/// </remarks>
public sealed class InstitutionState
{
    private readonly HashSet<GameCapability> _capabilities = [];
    private readonly HashSet<CharacterId> _members = [];

    public InstitutionState(InstitutionId id, string name)
    {
        Id = id;
        Name = name;
    }

    public InstitutionId Id { get; }

    public string Name { get; }

    public IReadOnlySet<GameCapability> Capabilities => _capabilities;

    public IReadOnlySet<CharacterId> Members => _members;

    public void ExposeCapability(GameCapability capability) => _capabilities.Add(capability);

    public void AddMember(CharacterId characterId) => _members.Add(characterId);

    public InstitutionState Clone()
    {
        var clone = new InstitutionState(Id, Name);
        foreach (var capability in _capabilities)
        {
            clone._capabilities.Add(capability);
        }

        foreach (var member in _members)
        {
            clone._members.Add(member);
        }

        return clone;
    }
}
