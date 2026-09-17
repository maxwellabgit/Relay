using System.Text.Json;

namespace Relay.Core.Cases;

/// <summary>
/// Slice 4 scripted mind for the Lightshift research path:
/// propose search → (after artifacts) propose delegate → say with citations.
/// When search yields no evidence, says insufficient evidence without inventing claims.
/// </summary>
public sealed class LightshiftResearchMind : ICaseMind
{
    public const string Question = "Verify whether Lightshift operates 20 battery sites";
    public const string SearchQuery = "Lightshift battery energy storage sites count";

    public string Name => "scripted-lightshift-research";

    public Task<CaseMindStep> StepAsync(CaseMindRequest request, CancellationToken cancellationToken)
    {
        var ops = request.PendingOperations;
        var awaiting = ops.FirstOrDefault(o =>
            o.Status is OperationStatus.AwaitingApproval or OperationStatus.Approved or OperationStatus.Executing);
        if (awaiting is not null)
        {
            return Task.FromResult(Step(
                "waiting research approval",
                new CaseMove { Type = CaseMove.Wait, Text = "awaiting " + awaiting.OperationId },
                "Waiting on research approval."));
        }

        var searchDone = FindCompletedCapability(request, ResearchCapabilities.Search);
        var delegateDone = FindCompletedCapability(request, ResearchCapabilities.Delegate)
            ?? FindCompletedCapability(request, ResearchCapabilities.ModelRequest);

        if (delegateDone is { } del)
        {
            return Task.FromResult(InterpretDelegate(del, request));
        }

        if (searchDone is { } search)
        {
            if (!TryGetArtifactIds(search, out var artifactIds) || artifactIds.Count == 0)
            {
                return Task.FromResult(Step(
                    "insufficient evidence",
                    new CaseMove
                    {
                        Type = CaseMove.Say,
                        Text = "Insufficient evidence: the approved search returned no stored source artifacts about Lightshift battery sites.",
                        Done = true,
                    },
                    "Insufficient evidence."));
            }

            var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["capability"] = JsonSerializer.SerializeToElement(ResearchCapabilities.Delegate),
            ["idempotencyKey"] = JsonSerializer.SerializeToElement("lightshift-delegate-" + request.CaseId),
            ["objective"] = JsonSerializer.SerializeToElement(request.Objective ?? Question),
            ["profile"] = JsonSerializer.SerializeToElement("research-delegate"),
            ["artifactObjectIds"] = JsonSerializer.SerializeToElement(artifactIds),
        };
        return Task.FromResult(Step(
            "delegate with exact artifacts",
            new CaseMove
            {
                Type = CaseMove.Propose,
                Name = ResearchCapabilities.Delegate,
                Text = "Ask the delegate using only the stored research artifacts.",
                Args = args,
            },
            "Proposing bounded delegate package."));
        }

        // First step: propose online research (search scope).
        var searchArgs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["capability"] = JsonSerializer.SerializeToElement(ResearchCapabilities.Search),
            ["idempotencyKey"] = JsonSerializer.SerializeToElement("lightshift-search-" + request.CaseId),
            ["query"] = JsonSerializer.SerializeToElement(SearchQuery),
            ["limit"] = JsonSerializer.SerializeToElement(5),
            ["allowedHosts"] = JsonSerializer.SerializeToElement(new[] { "lightshift.example", "news.example" }),
        };
        return Task.FromResult(Step(
            "propose online research",
            new CaseMove
            {
                Type = CaseMove.Propose,
                Name = ResearchCapabilities.Search,
                Text = "Search for evidence about Lightshift battery sites.",
                Args = searchArgs,
            },
            "Proposing online research."));
    }

    private static CaseMindStep InterpretDelegate(JsonElement responseRoot, CaseMindRequest request)
    {
        var text = responseRoot.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? ""
            : "";
        var cited = new List<Dictionary<string, object?>>();
        if (responseRoot.TryGetProperty("citedArtifactObjectIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind != JsonValueKind.String) continue;
                cited.Add(new Dictionary<string, object?>
                {
                    ["objectId"] = id.GetString(),
                    ["kind"] = "research.artifact",
                });
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Step(
                "insufficient evidence",
                new CaseMove
                {
                    Type = CaseMove.Say,
                    Text = "Insufficient evidence: the delegate returned no usable conclusion.",
                    Done = true,
                },
                "Insufficient evidence.");
        }

        // Final claims cite stored source artifacts (object ids), not only the delegate prose.
        return Step(
            "answer with citations",
            new CaseMove
            {
                Type = CaseMove.Say,
                Text = text.Trim(),
                Done = true,
                Args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["citations"] = JsonSerializer.SerializeToElement(cited),
                },
            },
            "Answering with cited research artifacts.");
    }

    private static JsonElement? FindCompletedCapability(CaseMindRequest request, string capability)
    {
        for (var i = request.RecentEvents.Count - 1; i >= 0; i--)
        {
            var evt = request.RecentEvents[i];
            if (evt.Type != CaseEventTypes.OperationExecuted) continue;
            if (!evt.Payload.TryGetProperty("operationId", out var opIdEl)) continue;
            var opId = opIdEl.GetString();
            var op = request.PendingOperations.FirstOrDefault(o => o.OperationId == opId)
                ?? request.PendingOperations.FirstOrDefault(o => o.Capability == capability && o.Status == OperationStatus.Completed);
            // Prefer matching capability via result payload embedded by runtime... look at summary path.
        }

        // Scan executed ops listed on the case (completed ops may have been removed from pending).
        // Fall back: look for operation.executed payloads that include result snapshots in recent tool-like events.
        // The runtime stores resultRef; mind request includes pending ops only. So we encode result into
        // OperationExecuted payload — Lightshift mind reads from events that carry resultBlob when present.
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

    private static bool TryGetArtifactIds(JsonElement searchResult, out List<string> ids)
    {
        ids = [];
        if (searchResult.TryGetProperty("artifactObjectIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
                    ids.Add(s);
        }
        return ids.Count > 0;
    }

    private static CaseMindStep Step(string intent, CaseMove move, string feed) => new(
        new CaseMindRead(intent, 0.7, 0.6, 0.2, new CaseMindConfidence(0.85, 0.8, 0.85), ["world_knowledge"]),
        move,
        feed);
}
