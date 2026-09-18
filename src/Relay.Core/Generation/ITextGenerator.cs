namespace Relay.Core.Generation;

/// <summary>Local text generator — drafting only after code selects the generation task.</summary>
public interface ITextGenerator
{
    string ProviderName { get; }
    Task<TextGenerationResult> GenerateAsync(TextGenerationRequest request, CancellationToken cancellationToken);
}

public sealed class TextGenerationRequest
{
    public required string TaskKind { get; init; }
    public required string Prompt { get; init; }
    public IReadOnlyList<string> EvidenceExcerpts { get; init; } = [];
    public string? CaseId { get; init; }
}

public sealed class TextGenerationResult
{
    public required bool Ok { get; init; }
    public string Text { get; init; } = "";
    public string? FailureReason { get; init; }

    public static TextGenerationResult Success(string text) => new() { Ok = true, Text = text };
    public static TextGenerationResult Fail(string reason) => new() { Ok = false, FailureReason = reason };
}

/// <summary>Deterministic test double. Queue responses with <see cref="Enqueue"/>.</summary>
public sealed class ScriptedTextGenerator : ITextGenerator
{
    private readonly Queue<TextGenerationResult> _results = new();
    private int _callCount;

    public string ProviderName => "scripted";
    public int CallCount => Volatile.Read(ref _callCount);

    public ScriptedTextGenerator Enqueue(TextGenerationResult result)
    {
        _results.Enqueue(result);
        return this;
    }

    public ScriptedTextGenerator EnqueueText(string text) => Enqueue(TextGenerationResult.Success(text));

    public ScriptedTextGenerator EnqueueUnavailable(string reason = "generator_unavailable") =>
        Enqueue(TextGenerationResult.Fail(reason));

    public Task<TextGenerationResult> GenerateAsync(TextGenerationRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        cancellationToken.ThrowIfCancellationRequested();
        if (_results.Count == 0)
            return Task.FromResult(TextGenerationResult.Fail("no_scripted_response"));
        return Task.FromResult(_results.Dequeue());
    }
}
