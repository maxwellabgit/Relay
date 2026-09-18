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

        if (string.Equals(result.Kind, CapabilityResultKinds.Wait, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.Kind, "waiting", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.Reason, "waiting_for_judgment", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.Reason, "generator_unavailable", StringComparison.OrdinalIgnoreCase))
        {
            var waitArgs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["reason"] = JsonSerializer.SerializeToElement(result.Reason ?? "waiting_for_judgment"),
            };
            return new CaseMindStep(
                new CaseMindRead(result.Reason ?? result.Kind, 0.5, 0.3, 0.2, new CaseMindConfidence(0.7, 0.7, 0.7), []),
                new CaseMove
                {
                    Type = CaseMove.Wait,
                    Text = result.Reason ?? "waiting_for_judgment",
                    Done = false,
                    Args = waitArgs,
                },
                result.FeedText);
        }

        if (IsProposeOperation(result))
        {
            var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (k, v) in result.Artifacts)
                args[k] = JsonSerializer.SerializeToElement(v);

            var capability = result.Artifacts.TryGetValue("capability", out var cap)
                ? cap
                : Actions.ModifyNote;
            args["capability"] = JsonSerializer.SerializeToElement(capability);
            if (!args.ContainsKey("idempotencyKey"))
                args["idempotencyKey"] = JsonSerializer.SerializeToElement(
                    "cap-" + StableHash(result.FeedText + "|" + capability));

            return new CaseMindStep(
                new CaseMindRead(result.Reason ?? result.Kind, 0.6, 0.5, 0.2, new CaseMindConfidence(0.7, 0.7, 0.7), []),
                new CaseMove
                {
                    Type = CaseMove.Propose,
                    Name = capability,
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
                args["idempotencyKey"] = JsonSerializer.SerializeToElement(
                    "draft-" + StableHash(body ?? result.FeedText));
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

        if (string.Equals(result.Kind, CapabilityResultKinds.Clarification, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.Kind, CapabilityResultKinds.OwnerlessOrClarify, StringComparison.OrdinalIgnoreCase))
        {
            return Step(
                CaseMove.Say,
                result.FeedText,
                result.FeedText,
                done: result.Done,
                result,
                attention: result.PresentationLevel ?? "persistent");
        }

        if (string.Equals(result.Kind, CapabilityResultKinds.Complete, StringComparison.OrdinalIgnoreCase))
        {
            return Step(CaseMove.Stop, result.FeedText, result.FeedText, done: true, result);
        }

        return Step(
            CaseMove.Say,
            result.FeedText,
            result.FeedText,
            done: result.Done,
            result,
            attention: result.PresentationLevel);
    }

    private static bool IsProposeOperation(CapabilityResult result)
    {
        if (string.Equals(result.Kind, CapabilityResultKinds.ProposeOperation, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(result.Kind, CapabilityResultKinds.TaskProposal, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(result.Kind, "propose_modify", StringComparison.OrdinalIgnoreCase))
            return true;
        return result.Artifacts.TryGetValue("capability", out var cap) &&
               (string.Equals(cap, Actions.ModifyNote, StringComparison.Ordinal) ||
                string.Equals(cap, TaskCaptureCapability.CreateTaskCapability, StringComparison.Ordinal));
    }

    private static string StableHash(string material)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(bytes)[..16];
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
