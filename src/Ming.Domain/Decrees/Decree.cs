using MingSim.Domain.Common;

namespace MingSim.Domain.Decrees;

/// <summary>政令的生命周期。</summary>
public enum DecreeStatus
{
    Draft,
    Submitted,
    Executing,
    Completed,
    Rejected,
}

/// <summary>
/// 玩家或皇帝发出的正式政令。
/// </summary>
/// <remarks>
/// 自然语言只是政令的输入方式之一。进入世界后，它应当先变成这个结构化对象，
/// 再由相关机构拆分成一个或多个 WorldIntent。
/// </remarks>
public sealed class Decree
{
    private readonly List<string> _restrictions = [];
    private readonly List<ProvinceId> _regionScope = [];

    public Decree(
        string id,
        CharacterId issuerId,
        string domain,
        string objective,
        int issuedTurn,
        int deadlineTurn)
    {
        Id = id;
        IssuerId = issuerId;
        Domain = domain;
        Objective = objective;
        IssuedTurn = issuedTurn;
        DeadlineTurn = deadlineTurn;
        Status = DecreeStatus.Draft;
    }

    public string Id { get; }

    public CharacterId IssuerId { get; }

    public string Domain { get; }

    public string Objective { get; }

    public int IssuedTurn { get; }

    public int DeadlineTurn { get; }

    public DecreeStatus Status { get; private set; }

    public IReadOnlyList<ProvinceId> RegionScope => _regionScope;

    public IReadOnlyList<string> Restrictions => _restrictions;

    public void AddRegion(ProvinceId provinceId) => _regionScope.Add(provinceId);

    public void AddRestriction(string restriction) => _restrictions.Add(restriction);

    public void Submit() => Status = DecreeStatus.Submitted;

    public void StartExecution() => Status = DecreeStatus.Executing;

    public void Complete() => Status = DecreeStatus.Completed;

    public void Reject() => Status = DecreeStatus.Rejected;
}
