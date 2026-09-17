using System.Text.Json;
using System.Text.Json.Serialization;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Policy;

public static class BudgetReservationStatus
{
    public const string Reserved = "reserved";
    public const string Committed = "committed";
    public const string Released = "released";
    /// <summary>Ambiguous timeout — retain conservative charge until reconciled.</summary>
    public const string HeldConservative = "held_conservative";
}

/// <summary>One transport attempt's budget reservation against a hosted grant.</summary>
public sealed class BudgetReservation
{
    [JsonPropertyName("reservationId")] public required string ReservationId { get; init; }
    [JsonPropertyName("grantId")] public required string GrantId { get; init; }
    [JsonPropertyName("grantRevocationVersion")] public long GrantRevocationVersion { get; init; }
    [JsonPropertyName("purpose")] public required string Purpose { get; init; }
    [JsonPropertyName("estimatedInputTokens")] public long EstimatedInputTokens { get; init; }
    [JsonPropertyName("estimatedSpendingMicros")] public long EstimatedSpendingMicros { get; init; }
    [JsonPropertyName("reportedInputTokens")] public long? ReportedInputTokens { get; set; }
    [JsonPropertyName("reportedSpendingMicros")] public long? ReportedSpendingMicros { get; set; }
    [JsonPropertyName("pricingAssumptionVersion")] public string PricingAssumptionVersion { get; init; } = "pricing-v1";
    [JsonPropertyName("status")] public string Status { get; set; } = BudgetReservationStatus.Reserved;
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("packageHash")] public string? PackageHash { get; set; }
    /// <summary>True when displayed figures are estimates, not exact billed amounts.</summary>
    [JsonPropertyName("isEstimate")] public bool IsEstimate { get; set; } = true;
}

/// <summary>
/// Atomic budget reservations. Concurrent reservations cannot exceed the grant;
/// each transport attempt has its own reservation.
/// </summary>
public sealed class BudgetReservations
{
    private readonly DataRoot _root;
    private readonly IClock _clock;
    private readonly HostedGrantStore _grants;
    private readonly object _gate = new();

    public BudgetReservations(DataRoot root, IClock clock, HostedGrantStore grants)
    {
        _root = root;
        _clock = clock;
        _grants = grants;
    }

    public string ReservationsDirectory => Path.Combine(_root.GrantsDirectory, "reservations");
    public string ReservationPath(string id) => Path.Combine(ReservationsDirectory, id + ".json");

    public BudgetReservation? TryLoad(string reservationId)
    {
        var text = AtomicFile.ReadAllTextIfExists(ReservationPath(reservationId));
        return text is null ? null : JsonSerializer.Deserialize<BudgetReservation>(text, RelayJson.Indented);
    }

    private void Save(BudgetReservation reservation)
    {
        Directory.CreateDirectory(ReservationsDirectory);
        AtomicFile.WriteAllText(ReservationPath(reservation.ReservationId),
            JsonSerializer.Serialize(reservation, RelayJson.Indented));
    }

    /// <summary>
    /// Atomically reserves against the grant. Returns null when the grant cannot cover the estimate.
    /// </summary>
    public BudgetReservation? TryReserve(
        string grantId,
        string purpose,
        long estimatedInputTokens,
        long estimatedSpendingMicros,
        string? packageHash = null)
    {
        lock (_gate)
        {
            var grant = _grants.TryLoad(grantId);
            if (grant is null || grant.Revoked || grant.ExpiresAt <= _clock.UtcNow)
                return null;

            var outstanding = Outstanding(grantId);
            var reservedTokens = outstanding.Sum(r => r.EstimatedInputTokens);
            var reservedSpend = outstanding.Sum(r => r.EstimatedSpendingMicros);
            var reservedRequests = outstanding.Count;

            if (grant.RequestsUsed + reservedRequests + 1 > grant.MaxRequests)
                return null;
            if (grant.InputTokensUsed + reservedTokens + estimatedInputTokens > grant.MaxInputTokens)
                return null;
            if (grant.SpendingMicrosUsed + reservedSpend + estimatedSpendingMicros > grant.MaxSpendingMicros)
                return null;

            var reservation = new BudgetReservation
            {
                ReservationId = Ulid.NewUlid(_clock.UtcNow),
                GrantId = grantId,
                GrantRevocationVersion = grant.RevocationVersion,
                Purpose = purpose,
                EstimatedInputTokens = estimatedInputTokens,
                EstimatedSpendingMicros = estimatedSpendingMicros,
                PricingAssumptionVersion = grant.PricingAssumptionVersion,
                Status = BudgetReservationStatus.Reserved,
                CreatedAt = _clock.UtcNow,
                PackageHash = packageHash,
                IsEstimate = true,
            };
            Save(reservation);
            return reservation;
        }
    }

    /// <summary>Ambiguous timeout: keep conservative charge until reconciled.</summary>
    public BudgetReservation HoldConservative(string reservationId)
    {
        lock (_gate)
        {
            var r = TryLoad(reservationId) ?? throw new InvalidOperationException("Reservation not found.");
            if (r.Status is BudgetReservationStatus.Committed or BudgetReservationStatus.Released)
                return r;
            r.Status = BudgetReservationStatus.HeldConservative;
            Save(r);
            return r;
        }
    }

    /// <summary>Reconcile with reported usage; commits charge against the grant.</summary>
    public BudgetReservation Reconcile(string reservationId, long reportedInputTokens, long reportedSpendingMicros)
    {
        lock (_gate)
        {
            var r = TryLoad(reservationId) ?? throw new InvalidOperationException("Reservation not found.");
            if (r.Status == BudgetReservationStatus.Committed)
                return r;

            var grant = _grants.TryLoad(r.GrantId)
                ?? throw new InvalidOperationException("Grant not found.");

            r.ReportedInputTokens = reportedInputTokens;
            r.ReportedSpendingMicros = reportedSpendingMicros;
            r.IsEstimate = false;
            r.Status = BudgetReservationStatus.Committed;
            Save(r);

            grant.RequestsUsed++;
            grant.InputTokensUsed += reportedInputTokens;
            grant.SpendingMicrosUsed += reportedSpendingMicros;
            _grants.Save(grant);
            return r;
        }
    }

    public BudgetReservation Release(string reservationId)
    {
        lock (_gate)
        {
            var r = TryLoad(reservationId) ?? throw new InvalidOperationException("Reservation not found.");
            if (r.Status == BudgetReservationStatus.Committed)
                return r;
            r.Status = BudgetReservationStatus.Released;
            Save(r);
            return r;
        }
    }

    public IReadOnlyList<BudgetReservation> Outstanding(string grantId)
    {
        if (!Directory.Exists(ReservationsDirectory)) return [];
        var list = new List<BudgetReservation>();
        foreach (var file in Directory.GetFiles(ReservationsDirectory, "*.json"))
        {
            var text = AtomicFile.ReadAllTextIfExists(file);
            if (text is null) continue;
            var r = JsonSerializer.Deserialize<BudgetReservation>(text, RelayJson.Indented);
            if (r is null || r.GrantId != grantId) continue;
            if (r.Status is BudgetReservationStatus.Reserved or BudgetReservationStatus.HeldConservative)
                list.Add(r);
        }
        return list;
    }

    /// <summary>Display helper: never present estimates as exact billed amounts.</summary>
    public static string FormatForDisplay(BudgetReservation reservation)
    {
        if (reservation.IsEstimate || reservation.ReportedSpendingMicros is null)
            return $"estimated {reservation.EstimatedSpendingMicros} µ$ (not exact billed)";
        return $"billed {reservation.ReportedSpendingMicros.Value} µ$";
    }
}
