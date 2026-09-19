namespace Relay.Core.Reflexes;

public sealed record ReflexDefinition(
    string Id,
    int Version,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> PermittedSources,
    IReadOnlyList<string> WriteActionIds);

public sealed record ReflexContext(string CaseId, string ReflexId, int ReflexVersion);

public enum ReflexResultKind
{
    Finding,
    Evidence,
    Read,
    ProposeOperation,
    Clarification,
    NoAction,
}

public sealed record ReflexResult(ReflexResultKind Kind, string Summary, string? OperationActionId);

public interface IReflexHandler
{
    ReflexDefinition Definition { get; }
    Task<ReflexResult> EvaluateAsync(ReflexContext context, CancellationToken cancellationToken);
}
