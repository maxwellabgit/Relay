using Relay.Core.Cases;
using Relay.Core.Telemetry;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>
/// Plaintext object-store boundary characterization of the frozen prototype.
/// This is not an alpha encryption gate: it does not scan SQLite, raw files, or
/// encrypted storage, and the object store remains plaintext.
/// </summary>
public sealed class PlaintextBoundaryCharacterizationTests : IDisposable
{
    private const string Sentinel = "PRIVACY_SENTINEL_94465cf_do_not_log";
    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 18, 16, 0, 0, TimeSpan.Zero));

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Direct_input_sentinel_stays_in_the_plaintext_object_store()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = new RuntimeDiagnostics(
            Path.Combine(_tmp.Root.DevRunsDirectory, "sentinel", "runtime.jsonl"), "sentinel");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics);

        var started = runtime.StartDirectCase(Sentinel);
        var events = runtime.Cases.LoadEvents(started.Id);
        var input = Assert.Single(events, e => e.Type == CaseEventTypes.UserInput);
        var payload = input.Payload.GetRawText();

        Assert.DoesNotContain(Sentinel, payload, StringComparison.Ordinal);
        Assert.Contains(started.SourceRefs, r => r.Contains("object:", StringComparison.Ordinal) || r.Length > 0);

        var objectId = input.Payload.GetProperty("objectId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(objectId));
        Assert.Equal(Sentinel, runtime.Objects.TryReadTextById(objectId!));
    }

    [Fact]
    public void Telemetry_redactor_does_not_keep_the_sentinel_in_known_keys()
    {
        var redacted = TelemetryRedactor.Redact(new Dictionary<string, string>
        {
            ["transcript"] = Sentinel,
            ["expected"] = Sentinel,
            ["actual"] = Sentinel,
            ["summary"] = Sentinel,
            ["detail"] = Sentinel,
            ["phase"] = "needs_decision",
        });

        Assert.DoesNotContain(redacted.Values, v => v.Contains(Sentinel, StringComparison.Ordinal));
        Assert.Equal(Sentinel.Length.ToString(), redacted["transcript.charCount"]);
    }
}
