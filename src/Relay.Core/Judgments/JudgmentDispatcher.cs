using System.Text.Json;
using Relay.Core.Time;

namespace Relay.Core.Judgments;

/// <summary>
/// Dispatches judgment requests through an <see cref="IJudgmentClient"/> with persisted retry state.
/// Retries never occupy the case runtime lock or the local inference lease — callers schedule work outside those gates.
/// </summary>
public sealed class JudgmentDispatcher
{
    private readonly IJudgmentClient _client;
    private readonly JudgmentTransportPolicy _policy;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;

    public JudgmentDispatcher(
        IJudgmentClient client,
        JudgmentTransportPolicy policy,
        IClock clock,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _client = client;
        _policy = policy;
        _clock = clock;
        _delay = delay;
    }

    public IJudgmentClient Client => _client;
    public JudgmentTransportPolicy Policy => _policy;

    /// <summary>
    /// Runs a judgment with transport policy. Backoff waits use <paramref name="delay"/> (tests inject a fake clock advance).
    /// </summary>
    public async Task<JudgmentResponse> DispatchAsync(
        JudgmentRequest request,
        CancellationToken cancellationToken = default,
        int maxAttempts = 4)
    {
        if (_policy.IsCircuitOpen(out var wait) && !_policy.TryBeginProbe())
        {
            return JudgmentResponse.Fail(JudgmentErrorCodes.CircuitOpen,
                $"Circuit open; next probe in {wait.TotalSeconds:0}s.", retryable: true);
        }

        var hash = FixtureJudgmentClient.CanonicalRequestHash(request);
        var retry = _policy.Begin(hash);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            retry.Attempt = attempt + 1;
            retry.Status = JudgmentRetryStatus.InFlight;
            _policy.SaveRetry(retry);

            JudgmentResponse response;
            try
            {
                response = await _client.JudgeAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                retry.Status = JudgmentRetryStatus.Cancelled;
                retry.LastErrorCode = JudgmentErrorCodes.Cancelled;
                retry.LastError = "cancelled";
                _policy.SaveRetry(retry);
                return JudgmentResponse.Fail(JudgmentErrorCodes.Cancelled, "Judgment cancelled.", retryable: false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
            {
                response = JudgmentResponse.Fail(JudgmentErrorCodes.Transient, ex.Message, retryable: true);
            }

            if (response.Ok)
            {
                retry.Status = JudgmentRetryStatus.Succeeded;
                _policy.SaveRetry(retry);
                _policy.RecordSuccess();
                return response;
            }

            retry.LastErrorCode = response.ErrorCode;
            retry.LastError = response.Error;
            retry.LastHttpStatus = response.HttpStatus;

            var status = response.HttpStatus ?? 0;
            if (_policy.IsAuthStatus(status) || response.ErrorCode == JudgmentErrorCodes.AuthBlocked
                || response.ErrorCode == JudgmentErrorCodes.MissingApiKey)
            {
                retry.Status = JudgmentRetryStatus.Failed;
                _policy.SaveRetry(retry);
                _policy.RecordFailure(retryable: false);
                return response;
            }

            if (_policy.IsInvalidContractStatus(status)
                || response.ErrorCode is JudgmentErrorCodes.InvalidContract or JudgmentErrorCodes.ProviderContractError)
            {
                // Contract errors: no auto retry.
                retry.Status = JudgmentRetryStatus.Failed;
                _policy.SaveRetry(retry);
                _policy.RecordFailure(retryable: false);
                return response;
            }

            var retryable = response.Retryable
                || _policy.IsRetryableStatus(status)
                || response.ErrorCode is JudgmentErrorCodes.Throttled or JudgmentErrorCodes.Overload or JudgmentErrorCodes.Transient;

            if (!retryable || attempt + 1 >= maxAttempts)
            {
                retry.Status = JudgmentRetryStatus.Failed;
                _policy.SaveRetry(retry);
                _policy.RecordFailure(retryable);
                return response;
            }

            _policy.RecordFailure(retryable: true);
            var delay = _policy.ComputeBackoff(attempt, retryAfter: null);
            retry.Status = JudgmentRetryStatus.BackingOff;
            retry.NextAttemptAt = _clock.UtcNow + delay;
            _policy.SaveRetry(retry);

            // Delay outside any case lock / inference lease (caller's responsibility).
            if (_delay is not null)
                await _delay(delay, cancellationToken).ConfigureAwait(false);
            else
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        retry.Status = JudgmentRetryStatus.Failed;
        _policy.SaveRetry(retry);
        return JudgmentResponse.Fail(JudgmentErrorCodes.Transient, "Exhausted judgment retry attempts.", retryable: true);
    }

    /// <summary>Resumes a persisted backoff after process restart.</summary>
    public async Task<JudgmentResponse> ResumeAsync(
        string retryId,
        JudgmentRequest request,
        CancellationToken cancellationToken = default)
    {
        var state = _policy.TryLoad(retryId)
            ?? throw new InvalidOperationException($"Retry state '{retryId}' not found.");

        if (state.Status == JudgmentRetryStatus.Succeeded)
            throw new InvalidOperationException("Retry already succeeded.");
        if (state.Status == JudgmentRetryStatus.Cancelled)
            return JudgmentResponse.Fail(JudgmentErrorCodes.Cancelled, "Retry was cancelled.");

        if (state.NextAttemptAt is { } next && next > _clock.UtcNow)
        {
            var wait = next - _clock.UtcNow;
            if (_delay is not null)
                await _delay(wait, cancellationToken).ConfigureAwait(false);
            else
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        return await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
