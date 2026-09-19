using Relay.Core.Telemetry;

namespace Relay.Core.Tests;

public sealed class TelemetryRedactorTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("body")]
    [InlineData("prompt")]
    [InlineData("content")]
    [InlineData("transcript")]
    [InlineData("answer")]
    [InlineData("expected")]
    [InlineData("actual")]
    [InlineData("summary")]
    [InlineData("detail")]
    [InlineData("apiKey")]
    [InlineData("secret")]
    [InlineData("authorization")]
    [InlineData("TEXT")]
    [InlineData("ApiKey")]
    [InlineData("Summary")]
    public void Reject_mode_throws_for_sensitive_keys(string key)
    {
        var ex = Assert.Throws<TelemetryRedactionException>(() =>
            TelemetryRedactor.Redact(new Dictionary<string, string> { [key] = "secret-value" }, rejectSensitive: true));
        Assert.Equal(key, ex.Key, ignoreCase: true);
    }

    [Fact]
    public void Hash_mode_replaces_sensitive_values_with_count_and_sha256()
    {
        var input = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["text"] = "hello",
            ["casePhase"] = "needs_decision",
            ["prompt"] = "do not leak",
        };

        var redacted = TelemetryRedactor.Redact(input, rejectSensitive: false);

        Assert.False(redacted.ContainsKey("text"));
        Assert.False(redacted.ContainsKey("prompt"));
        Assert.Equal("5", redacted["text.charCount"]);
        Assert.Equal(TelemetryRedactor.Sha256Hex("hello"), redacted["text.sha256"]);
        Assert.Equal("11", redacted["prompt.charCount"]);
        Assert.Equal(TelemetryRedactor.Sha256Hex("do not leak"), redacted["prompt.sha256"]);
        Assert.Equal("needs_decision", redacted["casePhase"]);
        Assert.DoesNotContain(redacted.Values, v => v.Contains("hello", StringComparison.Ordinal));
        Assert.DoesNotContain(redacted.Values, v => v.Contains("do not leak", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_sensitive_keys_pass_through()
    {
        var redacted = TelemetryRedactor.Redact(new Dictionary<string, string>
        {
            ["capabilityId"] = "glossary.acronym.resolve@1",
            ["spanStart"] = "12",
        }, rejectSensitive: true);

        Assert.Equal("glossary.acronym.resolve@1", redacted["capabilityId"]);
        Assert.Equal("12", redacted["spanStart"]);
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
                Level = ProductEventLevels.Info,
                Properties = new Dictionary<string, string> { ["text"] = "should-hash" },
            });
            telemetry.Emit(new ProductEventDraft
            {
                EventName = ProductEventNames.OperationApproved,
                OperationId = "op-1",
                CaseId = "case-1",
            });
        }

        Assert.Equal(1, first.Sequence);
        Assert.False(first.Properties.ContainsKey("text"));
        Assert.Equal("11", first.Properties["text.charCount"]);

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"eventName\":\"app.started\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"eventName\":\"operation.approved\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\":1", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("should-hash", lines[0], StringComparison.Ordinal);
    }
}
