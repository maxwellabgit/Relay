using System.Text.Json;
using Relay.Core.Notes;

namespace Relay.Core.Cases;

/// <summary>
/// Slice 2 scripted mind: local_search → read_note → say with citations from the stored note.
/// Never invents a date; answers only from the read note body.
/// </summary>
public sealed class AtlasRecallMind : ICaseMind
{
    public const string Question = "What did we decide about the Atlas beta date?";

    public string Name => "scripted-atlas-recall";

    public Task<CaseMindStep> StepAsync(CaseMindRequest request, CancellationToken cancellationToken)
    {
        var search = FindToolResult(request.RecentEvents, CaseTools.LocalSearch);
        var read = FindToolResult(request.RecentEvents, CaseTools.ReadNote);

        if (search is null)
        {
            return Task.FromResult(new CaseMindStep(
                Read("search local notes for Atlas beta date"),
                new CaseMove
                {
                    Type = CaseMove.UseTool,
                    Name = CaseTools.LocalSearch,
                    Args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["query"] = JsonSerializer.SerializeToElement(request.Objective ?? Question),
                    },
                },
                "Searching local notes."));
        }

        if (read is null)
        {
            if (!TryBestHit(search.Value, out var projectId, out var noteId))
            {
                return Task.FromResult(new CaseMindStep(
                    Read("no matching note"),
                    new CaseMove { Type = CaseMove.Say, Text = "I could not find a stored decision about the Atlas beta date.", Done = true },
                    "No Atlas beta decision found."));
            }

            return Task.FromResult(new CaseMindStep(
                Read("read the Atlas decision note"),
                new CaseMove
                {
                    Type = CaseMove.UseTool,
                    Name = CaseTools.ReadNote,
                    Args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["projectId"] = JsonSerializer.SerializeToElement(projectId),
                        ["noteId"] = JsonSerializer.SerializeToElement(noteId),
                    },
                },
                "Reading the decision note."));
        }

        var body = read.Value.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
            ? bodyEl.GetString() ?? ""
            : "";
        var noteIdAns = read.Value.TryGetProperty("noteId", out var idEl) ? idEl.GetString() : null;
        var projectIdAns = read.Value.TryGetProperty("projectId", out var pidEl) ? pidEl.GetString() : null;
        var start = read.Value.TryGetProperty("spanStart", out var sEl) && sEl.TryGetInt32(out var s) ? s : 0;
        var end = read.Value.TryGetProperty("spanEnd", out var eEl) && eEl.TryGetInt32(out var e) ? e : body.Length;
        var eventId = read.Value.TryGetProperty("sourceEventId", out var evEl) ? evEl.GetString() : null;

        // Answer only from the stored body — do not invent a date.
        var answer = string.IsNullOrWhiteSpace(body)
            ? "The stored note is empty."
            : body.Trim();

        var citations = new[]
        {
            new Dictionary<string, object?>
            {
                ["noteId"] = noteIdAns,
                ["projectId"] = projectIdAns,
                ["eventId"] = eventId,
                ["start"] = start,
                ["end"] = end,
            },
        };

        return Task.FromResult(new CaseMindStep(
            Read("answer from cited note"),
            new CaseMove
            {
                Type = CaseMove.Say,
                Text = answer,
                Done = true,
                Args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["citations"] = JsonSerializer.SerializeToElement(citations),
                },
            },
            "Answering with citations."));
    }

    private static CaseMindRead Read(string intent) => new(
        intent, 0.8, 0.6, 0.1, new CaseMindConfidence(0.9, 0.95, 0.9), Needs: ["local_notes"]);

    private static JsonElement? FindToolResult(IReadOnlyList<CaseEvent> events, string tool)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var evt = events[i];
            if (evt.Type != CaseEventTypes.ToolResult) continue;
            if (!evt.Payload.TryGetProperty("tool", out var t) || t.GetString() != tool) continue;
            if (evt.Payload.TryGetProperty("result", out var result)) return result.Clone();
        }
        return null;
    }

    private static bool TryBestHit(JsonElement searchResult, out string projectId, out string noteId)
    {
        projectId = "";
        noteId = "";
        if (!searchResult.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            return false;

        JsonElement? best = null;
        var bestScore = double.MinValue;
        foreach (var hit in hits.EnumerateArray())
        {
            var score = hit.TryGetProperty("score", out var sc) && sc.TryGetDouble(out var d) ? d : 0;
            var type = hit.TryGetProperty("type", out var ty) ? ty.GetString() : null;
            if (string.Equals(type, NoteTypes.Decision, StringComparison.Ordinal)) score += 0.25;
            if (score >= bestScore)
            {
                bestScore = score;
                best = hit.Clone();
            }
        }

        if (best is null) return false;
        projectId = best.Value.TryGetProperty("projectId", out var p) ? p.GetString() ?? "" : "";
        noteId = best.Value.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
        return projectId.Length > 0 && noteId.Length > 0;
    }
}

/// <summary>Scripted mind that proposes a project/note write (for approve / edit / reject coverage).</summary>
public sealed class ScriptedProposeMind : ICaseMind
{
    private readonly string _capability;
    private readonly Dictionary<string, JsonElement> _args;
    private readonly string _idempotencyKey;
    private bool _proposed;

    public ScriptedProposeMind(string capability, Dictionary<string, JsonElement> args, string? idempotencyKey = null)
    {
        _capability = capability;
        _args = args;
        _idempotencyKey = idempotencyKey ?? capability + "-v1";
    }

    public string Name => "scripted-propose";

    public Task<CaseMindStep> StepAsync(CaseMindRequest request, CancellationToken cancellationToken)
    {
        var denied = request.PendingOperations.Any(o => o.Status == OperationStatus.Denied);
        var completed = request.PendingOperations.Any(o => o.Status == OperationStatus.Completed);
        var awaiting = request.PendingOperations.FirstOrDefault(o =>
            o.Status is OperationStatus.AwaitingApproval or OperationStatus.Approved or OperationStatus.Executing);

        if (completed)
        {
            return Task.FromResult(new CaseMindStep(
                new CaseMindRead("done", 0.5, 0.5, 0, new CaseMindConfidence(1, 1, 1), []),
                new CaseMove { Type = CaseMove.Stop, Text = "done", Done = true },
                "Operation completed."));
        }

        if (denied)
        {
            return Task.FromResult(new CaseMindStep(
                new CaseMindRead("rejected", 0.5, 0.5, 0, new CaseMindConfidence(1, 1, 1), []),
                new CaseMove { Type = CaseMove.Say, Text = "Understood — I will not make that change.", Done = true },
                "Stopped after rejection."));
        }

        if (awaiting is not null)
        {
            return Task.FromResult(new CaseMindStep(
                new CaseMindRead("waiting", 0.5, 0.5, 0, new CaseMindConfidence(1, 1, 1), []),
                new CaseMove { Type = CaseMove.Wait, Text = "awaiting " + awaiting.OperationId },
                "Waiting on approval."));
        }

        if (_proposed)
        {
            return Task.FromResult(new CaseMindStep(
                new CaseMindRead("idle", 0.2, 0.2, 0, new CaseMindConfidence(1, 1, 1), []),
                new CaseMove { Type = CaseMove.Wait, Text = "nothing pending" },
                "Nothing to do."));
        }

        _proposed = true;
        var args = new Dictionary<string, JsonElement>(_args, StringComparer.Ordinal)
        {
            ["capability"] = JsonSerializer.SerializeToElement(_capability),
            ["idempotencyKey"] = JsonSerializer.SerializeToElement(_idempotencyKey),
        };
        return Task.FromResult(new CaseMindStep(
            new CaseMindRead("propose", 0.7, 0.5, 0.2, new CaseMindConfidence(0.9, 0.9, 0.9), []),
            new CaseMove
            {
                Type = CaseMove.Propose,
                Name = _capability,
                Text = "Propose " + _capability,
                Args = args,
            },
            "Proposing " + _capability + "."));
    }
}
