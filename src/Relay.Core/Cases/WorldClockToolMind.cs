using System.Text.Json;

namespace Relay.Core.Cases;

/// <summary>
/// Slice 5 scripted mind: Tokyo ask → generalize/build world_clock → promote → answer;
/// later Kathmandu/London reuse the promoted tool without rebuilding.
/// </summary>
public sealed class WorldClockToolMind : ICaseMind
{
    public const string TokyoAsk = "What time is it in Tokyo right now?";
    public const string ToolName = "world_clock";

    public string Name => "scripted-world-clock";

    /// <summary>Zone to answer for this case (derived from objective).</summary>
    public static string ZoneFor(string? objective)
    {
        var text = objective ?? "";
        if (text.Contains("Kathmandu", StringComparison.OrdinalIgnoreCase)) return "Asia/Kathmandu";
        if (text.Contains("London", StringComparison.OrdinalIgnoreCase)) return "Europe/London";
        return "Asia/Tokyo";
    }

    public Task<CaseMindStep> StepAsync(CaseMindRequest request, CancellationToken cancellationToken)
    {
        var awaiting = request.PendingOperations.FirstOrDefault(o =>
            o.Status is OperationStatus.AwaitingApproval or OperationStatus.Approved or OperationStatus.Executing);
        if (awaiting is not null)
        {
            return Task.FromResult(Step(
                "waiting tool approval",
                new CaseMove { Type = CaseMove.Wait, Text = "awaiting " + awaiting.OperationId },
                "Waiting on tool promote approval."));
        }

        var promoteDone = FindExecuted(request, ToolCapabilities.PromoteTool);
        var toolResult = FindToolResult(request, ToolName);
        var zone = ZoneFor(request.Objective);

        if (toolResult is { } tr && tr.Ok)
        {
            var time = tr.Time ?? "?";
            return Task.FromResult(Step(
                "answer with tool",
                new CaseMove
                {
                    Type = CaseMove.Say,
                    Text = $"It is {time} in {zone}.",
                    Done = true,
                },
                "Answered with world_clock."));
        }

        if (toolResult is { Ok: false } fail)
        {
            return Task.FromResult(Step(
                "tool failed",
                new CaseMove { Type = CaseMove.Say, Text = "The world clock failed: " + (fail.Error ?? "unknown"), Done = true },
                "Tool failed."));
        }

        // After promote (or when the tool is already promoted), use it — never rebuild.
        if (promoteDone is not null || (request.AvailableTools?.Contains(ToolName, StringComparer.Ordinal) == true))
        {
            return Task.FromResult(Step(
                "use world_clock",
                new CaseMove
                {
                    Type = CaseMove.UseTool,
                    Name = ToolName,
                    Args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["zone"] = JsonSerializer.SerializeToElement(zone),
                    },
                },
                $"Asking world_clock for {zone}."));
        }

        // First path: build a generalized tool (Tokyo creates it; tests use other cities).
        return Task.FromResult(Step(
            "build world_clock",
            new CaseMove
            {
                Type = CaseMove.Build,
                Name = ToolName,
                Text = "The user asks for the time in other cities and no tool can tell it.",
                Args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["inputs"] = JsonSerializer.SerializeToElement("zone"),
                    ["outputs"] = JsonSerializer.SerializeToElement("time, date, weekday, offset"),
                    ["justification"] = JsonSerializer.SerializeToElement(
                        "Return the current local time, date and weekday for any IANA time zone."),
                },
            },
            "Building a reusable world_clock tool."));
    }

    private static JsonElement? FindExecuted(CaseMindRequest request, string capability)
    {
        for (var i = request.RecentEvents.Count - 1; i >= 0; i--)
        {
            var evt = request.RecentEvents[i];
            if (evt.Type != CaseEventTypes.OperationExecuted) continue;
            if (evt.Payload.TryGetProperty("capability", out var cap) && cap.GetString() == capability
                && evt.Payload.TryGetProperty("result", out var result))
                return result.Clone();
        }
        return null;
    }

    private static (bool Ok, string? Time, string? Error)? FindToolResult(CaseMindRequest request, string tool)
    {
        for (var i = request.RecentEvents.Count - 1; i >= 0; i--)
        {
            var evt = request.RecentEvents[i];
            if (evt.Type != CaseEventTypes.ToolResult) continue;
            if (!evt.Payload.TryGetProperty("tool", out var t) || t.GetString() != tool) continue;
            var ok = evt.Payload.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            string? time = null;
            if (evt.Payload.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("time", out var timeEl))
                time = timeEl.GetString();
            var error = evt.Payload.TryGetProperty("error", out var err) ? err.GetString() : null;
            return (ok, time, error);
        }
        return null;
    }

    private static CaseMindStep Step(string intent, CaseMove move, string feed) => new(
        new CaseMindRead(intent, 0.7, 0.5, 0.1, new CaseMindConfidence(0.9, 0.85, 0.9), ["need_new_tool"]),
        move,
        feed);
}
