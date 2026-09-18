namespace Relay.Core.Judgments;

/// <summary>
/// Hosted judgment provider. Implementations must never put request state or raw transcript
/// text into error strings. Invalid requests return <see cref="JudgmentFailureCategories.Validation"/>;
/// the only exception that leaves <see cref="JudgeAsync"/> is <see cref="OperationCanceledException"/>.
/// </summary>
public interface IJudgmentClient
{
    string ProviderName { get; }

    Task<JudgmentResponse> JudgeAsync(JudgmentRequest request, CancellationToken cancellationToken);
}
