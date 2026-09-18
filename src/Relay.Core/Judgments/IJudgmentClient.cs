namespace Relay.Core.Judgments;

/// <summary>
/// Hosted judgment provider. Implementations must never put request state or raw transcript
/// text into error strings.
/// </summary>
public interface IJudgmentClient
{
    string ProviderName { get; }

    Task<JudgmentResponse> JudgeAsync(JudgmentRequest request, CancellationToken cancellationToken);
}
