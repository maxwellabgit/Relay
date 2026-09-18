using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Policy;

namespace Relay.Core.Capabilities;

/// <summary>Maps capability results onto case moves the runtime already understands.</summary>
public static class CapabilityResultMapper
{
    public static CaseMindStep ToMindStep(CapabilityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.Equals(result.Kind, "waiting", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.Reason, "waiting_for_judgment", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.Reason, "generator_unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return Step(CaseMove.Wait, result.FeedText, result.FeedText, done: false, result);
        }

        if (string.Equals(result.Kind, "propose_modify", StringComparison.OrdinalIgnoreCase) ||
            (result.Artifacts.TryGetValue("capability", out var cap) &&
             string.Equals(cap, Actions.ModifyNote, StringComparison.Ordinal)))
        {
            var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (k, v) in result.Artifacts)
                args[k] = JsonSerializer.SerializeToElement(v);
            if (!args.ContainsKey("capability"))
                args["capability"] = JsonSerializer.SerializeToElement(Actions.ModifyNote);
            if (!args.ContainsKey("idempotencyKey"))
                args["idempotencyKey"] = JsonSerializer.SerializeToElement("cap-" + Guid.NewGuid().ToString("N"));
            return new CaseMindStep(
                new CaseMindRead(result.Reason ?? result.Kind, 0.6, 0.5, 0.2, new CaseMindConfidence(0.7, 0.7, 0.7), []),
                new CaseMove
                {
                    Type = CaseMove.Propose,
                    Name = Actions.ModifyNote,
                    Text = result.FeedText,
                    Done = false,
                    Args = args,
                },
                result.FeedText);
        }

        if (string.Equals(result.Kind, "draft_verified", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.Kind, "draft_verbatim", StringComparison.OrdinalIgnoreCase))
        {
            var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (k, v) in result.Artifacts)
                args[k] = JsonSerializer.SerializeToElement(v);
            args["capability"] = JsonSerializer.SerializeToElement(Actions.CreateDraftNote);
            if (result.Artifacts.TryGetValue("body", out var body))
                args["text"] = JsonSerializer.SerializeToElement(body);
            if (!args.ContainsKey("idempotencyKey"))
                args["idempotencyKey"] = JsonSerializer.SerializeToElement("draft-" + Guid.NewGuid().ToString("N"));
            return new CaseMindStep(
                new CaseMindRead(result.Reason ?? result.Kind, 0.5, 0.4, 0.2, new CaseMindConfidence(0.7, 0.7, 0.7), []),
                new CaseMove
                {
                    Type = CaseMove.Propose,
                    Name = Actions.CreateDraftNote,
                    Text = result.FeedText,
                    Done = false,
                    Args = args,
                },
                result.FeedText);
        }

        return Step(
            CaseMove.Say,
            result.FeedText,
            result.FeedText,
            done: result.Done,
            result,
            attention: result.PresentationLevel);
    }

    private static CaseMindStep Step(
        string moveType,
        string moveText,
        string feed,
        bool done,
        CapabilityResult result,
        string? attention = null)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(attention ?? result.PresentationLevel))
            args["attention"] = JsonSerializer.SerializeToElement(attention ?? result.PresentationLevel!);
        foreach (var src in result.SourceRefs)
            args["sourceRef:" + src] = JsonSerializer.SerializeToElement(src);

        return new CaseMindStep(
            new CaseMindRead(result.Reason ?? result.Kind, 0.5, 0.3, 0.2, new CaseMindConfidence(0.7, 0.7, 0.7), []),
            new CaseMove { Type = moveType, Text = moveText, Done = done, Args = args },
            feed);
    }
}
