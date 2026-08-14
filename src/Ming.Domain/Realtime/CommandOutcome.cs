using System.Collections.ObjectModel;
using MingSim.Domain.Common;

namespace MingSim.Domain.Realtime;

/// <summary>单写者接纳一条命令后留下的不可变终态记录。</summary>
public sealed record CommandOutcome
{
    public CommandOutcome(
        string commandId,
        string fingerprint,
        bool accepted,
        IEnumerable<string> errorCodes,
        long ingressSequence,
        GameTime acceptedGameTime,
        long expectedWorldVersion,
        long resultingWorldVersion,
        string? commitId,
        int schemaVersion = 1)
    {
        CommandId = commandId;
        Fingerprint = fingerprint;
        Accepted = accepted;
        ErrorCodes = new ReadOnlyCollection<string>(errorCodes.ToArray());
        IngressSequence = ingressSequence;
        AcceptedGameTime = acceptedGameTime;
        ExpectedWorldVersion = expectedWorldVersion;
        ResultingWorldVersion = resultingWorldVersion;
        CommitId = commitId;
        SchemaVersion = schemaVersion;
    }

    public string CommandId { get; }

    public string Fingerprint { get; }

    public bool Accepted { get; }

    public IReadOnlyList<string> ErrorCodes { get; }

    public long IngressSequence { get; }

    public GameTime AcceptedGameTime { get; }

    public long ExpectedWorldVersion { get; }

    public long ResultingWorldVersion { get; }

    public string? CommitId { get; }

    public int SchemaVersion { get; }
}
