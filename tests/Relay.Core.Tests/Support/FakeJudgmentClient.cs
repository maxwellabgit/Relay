using System.Collections.Concurrent;
using Relay.Core.Judgments;

namespace Relay.Core.Tests.Support;

/// <summary>
/// Scripted judgment provider for deterministic tests. Never calls TypeSafe.
/// Matches by question-set id and optionally state hash.
/// <see cref="OperationCanceledException"/> is the only exception that leaves <see cref="JudgeAsync"/>.
/// </summary>
public sealed class FakeJudgmentClient : IJudgmentClient
{
    private readonly List<ScriptedJudgment> _scripts = [];
    private readonly ConcurrentQueue<FakeJudgmentCall> _calls = new();
    private int _callCount;

    public string ProviderName => "fake";
    public int CallCount => Volatile.Read(ref _callCount);
    public IReadOnlyList<FakeJudgmentCall> Calls => _calls.ToArray();

    public TimeSpan? SimulatedDelay { get; set; }
    public FakeJudgmentBehavior Behavior { get; set; } = FakeJudgmentBehavior.Success;
    public JudgmentResponse? DefaultResponse { get; set; }

    public FakeJudgmentClient Script(string questionSetId, JudgmentResponse response, string? stateHash = null)
    {
        _scripts.Add(new ScriptedJudgment(questionSetId, stateHash, response, Validated: true));
        return this;
    }

    /// <summary>Script a response without validating it first (parser / engine negative fixtures).</summary>
    public FakeJudgmentClient ScriptUnvalidated(string questionSetId, JudgmentResponse response, string? stateHash = null)
    {
        _scripts.Add(new ScriptedJudgment(questionSetId, stateHash, response, Validated: false));
        return this;
    }

    public int CallsFor(string questionSetId) =>
        Calls.Count(c => string.Equals(c.QuestionSetId, questionSetId, StringComparison.Ordinal));

    public async Task<JudgmentResponse> JudgeAsync(JudgmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            request.Validate();
        }
        catch (JudgmentValidationException ex)
        {
            return JudgmentResponse.FromFailure(
                JudgmentFailure.Create(JudgmentFailureCategories.Validation, ex.Message));
        }

        Interlocked.Increment(ref _callCount);
        var stateHash = JudgmentRequestHasher.HashJsonElement(request.State);
        _calls.Enqueue(new FakeJudgmentCall(request.QuestionSetId, request.QuestionSetVersion, stateHash, request.Model));

        if (SimulatedDelay is { } delay && delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return Behavior switch
        {
            FakeJudgmentBehavior.Timeout => JudgmentResponse.FromFailure(
                JudgmentFailure.Create(JudgmentFailureCategories.Timeout, "Fake provider timed out.")),
            FakeJudgmentBehavior.RateLimited => JudgmentResponse.FromFailure(
                JudgmentFailure.Create(JudgmentFailureCategories.RateLimited, "Fake provider rate limited.", httpStatus: 429)),
            FakeJudgmentBehavior.Overloaded => JudgmentResponse.FromFailure(
                JudgmentFailure.Create(JudgmentFailureCategories.Overloaded, "Fake provider overloaded.", httpStatus: 529)),
            FakeJudgmentBehavior.Malformed => JudgmentResponse.FromFailure(
                JudgmentFailure.Create(JudgmentFailureCategories.InvalidResponse, "Fake provider returned a malformed payload.")),
            FakeJudgmentBehavior.Cancel => throw new OperationCanceledException(cancellationToken),
            _ => ResolveSuccess(request, stateHash),
        };
    }

    private JudgmentResponse ResolveSuccess(JudgmentRequest request, string stateHash)
    {
        foreach (var script in _scripts)
        {
            if (!string.Equals(script.QuestionSetId, request.QuestionSetId, StringComparison.Ordinal))
                continue;
            if (script.StateHash is not null &&
                !string.Equals(script.StateHash, stateHash, StringComparison.Ordinal))
                continue;

            if (script.Validated)
            {
                try
                {
                    script.Response.ValidateAgainst(request);
                }
                catch (JudgmentValidationException ex)
                {
                    return JudgmentResponse.FromFailure(
                        JudgmentFailure.Create(JudgmentFailureCategories.InvalidResponse, ex.Message));
                }
            }

            return script.Response;
        }

        if (DefaultResponse is not null)
        {
            try
            {
                DefaultResponse.ValidateAgainst(request);
            }
            catch (JudgmentValidationException ex)
            {
                return JudgmentResponse.FromFailure(
                    JudgmentFailure.Create(JudgmentFailureCategories.InvalidResponse, ex.Message));
            }
            return DefaultResponse;
        }

        return JudgmentResponse.FromFailure(
            JudgmentFailure.Create(
                JudgmentFailureCategories.Validation,
                $"No scripted response for question set '{request.QuestionSetId}'."));
    }

    private sealed record ScriptedJudgment(
        string QuestionSetId,
        string? StateHash,
        JudgmentResponse Response,
        bool Validated);
}

public enum FakeJudgmentBehavior
{
    Success,
    Timeout,
    RateLimited,
    Overloaded,
    Malformed,
    Cancel,
}

public sealed record FakeJudgmentCall(
    string QuestionSetId,
    string QuestionSetVersion,
    string StateHash,
    string Model);
