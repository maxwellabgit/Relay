using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Judgments;

public static class JudgmentRetryStatus
{
    public const string Pending = "pending";
    public const string BackingOff = "backing_off";
    public const string InFlight = "in_flight";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>Persisted retry / circuit state. Retries must not hold the runtime lock or inference lease.</summary>
public sealed class JudgmentRetryState
{
    [JsonPropertyName("retryId")] public required string RetryId { get; init; }
    [JsonPropertyName("requestHash")] public required string RequestHash { get; init; }
    [JsonPropertyName("attempt")] public int Attempt { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = JudgmentRetryStatus.Pending;
    [JsonPropertyName("nextAttemptAt")] public DateTimeOffset? NextAttemptAt { get; set; }
    [JsonPropertyName("lastErrorCode")] public string? LastErrorCode { get; set; }
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
    [JsonPropertyName("lastHttpStatus")] public int? LastHttpStatus { get; set; }
    [JsonPropertyName("circuitOpenUntil")] public DateTimeOffset? CircuitOpenUntil { get; set; }
    [JsonPropertyName("consecutiveFailures")] public int ConsecutiveFailures { get; set; }
    [JsonPropertyName("probeIntervalSeconds")] public int ProbeIntervalSeconds { get; set; } = 30;
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Transport retry + circuit policy for hosted judgment calls.</summary>
public sealed class JudgmentTransportPolicy
{
    public static readonly TimeSpan[] DefaultBackoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    public static readonly TimeSpan InitialCircuitOpen = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxProbeInterval = TimeSpan.FromMinutes(5);
    public const int BurstFailureThreshold = 3;

    private readonly DataRoot? _root;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private JudgmentRetryState? _circuit;

    public JudgmentTransportPolicy(IClock clock, DataRoot? root = null)
    {
        _clock = clock;
        _root = root;
    }

    public string? RetryDirectory => _root is null ? null : Path.Combine(_root.Path, "judgments", "retries");

    public bool IsRetryableStatus(int status) => status is 429 or 529 || status >= 500;
    public bool IsAuthStatus(int status) => status is 401 or 403;
    public bool IsInvalidContractStatus(int status) => status == 422;

    public TimeSpan ComputeBackoff(int attemptZeroBased, TimeSpan? retryAfter, Random? rng = null)
    {
        if (retryAfter is not null && retryAfter.Value > TimeSpan.Zero)
            return retryAfter.Value;
        var baseDelay = attemptZeroBased < DefaultBackoff.Length
            ? DefaultBackoff[attemptZeroBased]
            : DefaultBackoff[^1];
        var jitterMs = (rng ?? Random.Shared).Next(0, 250);
        return baseDelay + TimeSpan.FromMilliseconds(jitterMs);
    }

    public JudgmentRetryState SaveRetry(JudgmentRetryState state)
    {
        lock (_gate)
        {
            state.UpdatedAt = _clock.UtcNow;
            if (RetryDirectory is not null)
            {
                Directory.CreateDirectory(RetryDirectory);
                AtomicFile.WriteAllText(
                    Path.Combine(RetryDirectory, state.RetryId + ".json"),
                    JsonSerializer.Serialize(state, JudgmentJson.Options));
            }
            return state;
        }
    }

    public JudgmentRetryState? TryLoad(string retryId)
    {
        if (RetryDirectory is null) return null;
        var text = AtomicFile.ReadAllTextIfExists(Path.Combine(RetryDirectory, retryId + ".json"));
        return text is null ? null : JsonSerializer.Deserialize<JudgmentRetryState>(text, JudgmentJson.Options);
    }

    public JudgmentRetryState Begin(string requestHash)
    {
        var state = new JudgmentRetryState
        {
            RetryId = Ulid.NewUlid(_clock.UtcNow),
            RequestHash = requestHash,
            Attempt = 0,
            Status = JudgmentRetryStatus.Pending,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        };
        return SaveRetry(state);
    }

    public bool IsCircuitOpen(out TimeSpan wait)
    {
        lock (_gate)
        {
            wait = TimeSpan.Zero;
            if (_circuit?.CircuitOpenUntil is { } until && until > _clock.UtcNow)
            {
                wait = until - _clock.UtcNow;
                return true;
            }
            return false;
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _circuit = null;
        }
    }

    public void RecordFailure(bool retryable)
    {
        lock (_gate)
        {
            _circuit ??= new JudgmentRetryState
            {
                RetryId = "circuit",
                RequestHash = "circuit",
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
            };
            if (!retryable) return;
            _circuit.ConsecutiveFailures++;
            if (_circuit.ConsecutiveFailures >= BurstFailureThreshold)
            {
                var interval = Math.Min(
                    MaxProbeInterval.TotalSeconds,
                    Math.Max(InitialCircuitOpen.TotalSeconds, _circuit.ProbeIntervalSeconds));
                if (_circuit.CircuitOpenUntil is not null)
                    interval = Math.Min(MaxProbeInterval.TotalSeconds, interval * 2);
                _circuit.ProbeIntervalSeconds = (int)interval;
                _circuit.CircuitOpenUntil = _clock.UtcNow + TimeSpan.FromSeconds(interval);
                _circuit.UpdatedAt = _clock.UtcNow;
            }
        }
    }

    /// <summary>Allows a single probe when the circuit open window has elapsed.</summary>
    public bool TryBeginProbe()
    {
        lock (_gate)
        {
            if (_circuit?.CircuitOpenUntil is { } until && until > _clock.UtcNow)
                return false;
            return true;
        }
    }
}
