using Relay.Core.Telemetry;

namespace Relay.Core.Tests;

public sealed class TelemetryRedactorTests
{
    [Fact]
    public void Allow_keeps_only_event_allowlisted_keys()
    {
        var allowed = TelemetryRedactor.Allow(
            ProductEventNames.CaseCreated,
            new Dictionary<string, string>
            {
                ["origin"] = "direct",
                ["charCount"] = "12",
            });

        Assert.Equal("direct", allowed["origin"]);
        Assert.Equal("12", allowed["charCount"]);
    }

    [Theory]
    [InlineData("expected")]
    [InlineData("actual")]
    [InlineData("query")]
    [InlineData("subject")]
    [InlineData("message")]
    [InlineData("providerError")]
    [InlineData("person")]
    [InlineData("transcript")]
    public void Allow_rejects_unknown_keys(string key)
    {
        var ex = Assert.Throws<TelemetryRedactionException>(() =>
            TelemetryRedactor.Allow(
                ProductEventNames.ProblemReported,
                new Dictionary<string, string> { [key] = "leak", ["severity"] = "error" }));
        Assert.Equal(key, ex.Key, ignoreCase: false);
    }

    [Fact]
    public void Problem_reported_allows_only_severity()
    {
        var allowed = TelemetryRedactor.Allow(
            ProductEventNames.ProblemReported,
            new Dictionary<string, string> { ["severity"] = "error" });
        Assert.Equal(new[] { "severity" }, allowed.Keys.ToArray());
    }

    [Fact]
    public void Legacy_redact_without_event_name_drops_all_properties()
    {
        var redacted = TelemetryRedactor.Redact(new Dictionary<string, string>
        {
            ["phase"] = "needs_decision",
            ["transcript"] = "secret",
        });
        Assert.Empty(redacted);
    }

    [Fact]
    public void Reject_mode_throws_when_legacy_redact_sees_properties()
    {
        Assert.Throws<TelemetryRedactionException>(() =>
            TelemetryRedactor.Redact(new Dictionary<string, string> { ["phase"] = "x" }, rejectSensitive: true));
    }
}

public sealed class JsonlRelayTelemetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-telemetry-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Writes_one_json_object_per_line_with_bounded_schema()
    {
        string path;
        ProductEvent first;
        using (var telemetry = new JsonlRelayTelemetry(_dir, "run-1", appVersion: "0.1.0-test"))
        {
            path = telemetry.EventsPath;
            first = telemetry.Emit(new ProductEventDraft
            {
                EventName = ProductEventNames.AppStarted,
                Properties = new Dictionary<string, string> { ["appVersion"] = "0.1.0-test" },
            });
            telemetry.Emit(new ProductEventDraft
            {
                EventName = ProductEventNames.OperationApproved,
                OperationId = "op-1",
                CaseId = "case-1",
            });
        }

        Assert.Equal(1, first.Sequence);
        Assert.Equal("0.1.0-test", first.Properties["appVersion"]);

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"eventName\":\"app.started\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"eventName\":\"operation.approved\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\":1", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_unknown_property_keys_for_event()
    {
        using var telemetry = new JsonlRelayTelemetry(_dir, "run-2");
        Assert.Throws<TelemetryRedactionException>(() =>
            telemetry.Emit(new ProductEventDraft
            {
                EventName = ProductEventNames.ProblemReported,
                PayloadRef = "obj-1",
                Properties = new Dictionary<string, string>
                {
                    ["severity"] = "error",
                    ["expected"] = "should not leak",
                },
            }));
    }
}
