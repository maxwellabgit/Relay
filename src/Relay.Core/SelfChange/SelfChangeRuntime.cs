using Relay.Core.Execution;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Policy;
using Relay.Core.Preferences;
using Relay.Core.Storage;

namespace Relay.Core.SelfChange;

/// <summary>
/// Executes approved changes to Relay itself: typed preference edits and prompt fragments. Every
/// change is a change set (before/after, hashed, revertible) recorded in the ledger; nothing else
/// about Relay is writable this way. Keys are a closed list so a proposal cannot reach arbitrary state.
/// </summary>
public sealed class SelfChangeRuntime : ISelfChangeOperations
{
    public static readonly IReadOnlyList<string> PreferenceKeys =
    [
        "response.verbosity", "response.promptLine", "display.alwaysShow", "display.stopShowing",
        "display.maxAlertsPer10Minutes", "display.maxResultsPer5Minutes", "display.cooldownSeconds",
        "filing.grant", "filing.revoke", "retention.bufferSeconds", "retention.excerptMaxSeconds", "sources.allowOnlineSearch",
    ];

    /// <summary>
    /// The prompt fragments an approved change set may add to: the mind's own prompt and the two utility
    /// prompts (docs/09), the fixed tool-build prompt and the digest of a delegate's reply. A fragment is
    /// added to a prompt, never the whole of one — <see cref="Mind.MindPrompt.ConstitutionName"/> is
    /// deliberately absent, because the constitution is what judges these proposals in the first place.
    /// </summary>
    public static readonly IReadOnlyList<string> PromptNames = [Mind.MindPrompt.PromptName, Tools.BuildPrompt.PromptName, External.Digest.PromptName];

    private readonly DataRoot _root;
    private readonly PreferenceStore _preferences;
    private readonly ChangeSetStore _changes;
    private readonly Func<DateTimeOffset> _clock;

    public SelfChangeRuntime(DataRoot root, PreferenceStore preferences, ChangeSetStore changes, Func<DateTimeOffset>? clock = null)
    {
        _root = root;
        _preferences = preferences;
        _changes = changes;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string? PromptFragment(string name)
    {
        if (!PromptNames.Contains(name)) return null;
        var text = AtomicFile.ReadAllTextIfExists(Path.Combine(_root.PromptsDirectory, name + ".md"));
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public ExecutionResult UpdatePreference(Proposal proposal, Decision decision, string taskId, IExecutionSink sink)
    {
        var key = proposal.Target.GetValueOrDefault("key") ?? "";
        var value = (proposal.Target.GetValueOrDefault("value") ?? "").Trim();
        if (!PreferenceKeys.Contains(key)) return ExecutionResult.Fail($"'{key}' is not a preference Relay can change. Keys: {string.Join(", ", PreferenceKeys)}");
        var now = _clock();
        string summary;
        ChangeSetResult result;
        switch (key)
        {
            case "response.verbosity":
                if (!ResponsePreferences.Verbosities.Contains(value)) return ExecutionResult.Fail("response.verbosity must be minimalist, concise, or normal.");
                result = _preferences.Update(p => p.Response.Verbosity = value, proposal.Reason, now, taskId, proposal.ProposalId);
                summary = $"Responses are now {value}";
                break;
            case "response.promptLine":
                if (value.Length is 0 or > 240) return ExecutionResult.Fail("response.promptLine must be 1–240 characters.");
                result = _preferences.Update(p => { if (!p.Response.PromptLines.Contains(value)) p.Response.PromptLines.Add(value); }, proposal.Reason, now, taskId, proposal.ProposalId);
                summary = $"Added response instruction: \"{value}\"";
                break;
            case "display.alwaysShow":
                if (value.Length is 0 or > 80) return ExecutionResult.Fail("display.alwaysShow needs a term.");
                result = _preferences.Update(p => { if (!p.Display.AlwaysShowTerms.Contains(value, StringComparer.OrdinalIgnoreCase)) p.Display.AlwaysShowTerms.Add(value); }, proposal.Reason, now, taskId, proposal.ProposalId);
                summary = $"'{value}' is now always shown when it comes up";
                break;
            case "display.stopShowing":
                result = _preferences.Update(p => p.Display.AlwaysShowTerms.RemoveAll(t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase)), proposal.Reason, now, taskId, proposal.ProposalId);
                summary = $"'{value}' is no longer pinned";
                break;
            case "display.maxAlertsPer10Minutes":
            case "display.maxResultsPer5Minutes":
            case "display.cooldownSeconds":
            case "retention.bufferSeconds":
            case "retention.excerptMaxSeconds":
                if (!int.TryParse(value, out var n)) return ExecutionResult.Fail($"{key} must be an integer.");
                result = _preferences.Update(p =>
                {
                    switch (key)
                    {
                        case "display.maxAlertsPer10Minutes": p.Display.MaxAlertsPer10Minutes = n; break;
                        case "display.maxResultsPer5Minutes": p.Display.MaxResultsPer5Minutes = n; break;
                        case "display.cooldownSeconds": p.Display.CooldownSeconds = n; break;
                        case "retention.bufferSeconds": p.Retention.BufferSeconds = n; break;
                        case "retention.excerptMaxSeconds": p.Retention.ExcerptMaxSeconds = n; break;
                    }
                }, proposal.Reason, now, taskId, proposal.ProposalId);
                summary = $"{key} = {n}";
                break;
            case "filing.grant":
                {
                    var action = proposal.Target.GetValueOrDefault("action") ?? Actions.RouteNote;
                    if (Actions.NeverGranted.Contains(action)) return ExecutionResult.Fail($"{action} can never be a standing grant.");
                    var grant = new StandingGrant
                    {
                        GrantId = Ulid.NewUlid(now),
                        Action = action,
                        ProjectId = proposal.Target.GetValueOrDefault("projectId"),
                        NoteType = proposal.Target.GetValueOrDefault("noteType"),
                        GrantedAt = now,
                        Reason = proposal.Reason,
                    };
                    if (grant.ProjectId is null) return ExecutionResult.Fail("A standing grant must name a project.");
                    result = _preferences.Update(p => p.Filing.Grants.Add(grant), proposal.Reason, now, taskId, proposal.ProposalId);
                    summary = $"Standing grant {grant.GrantId}: {action} for project {grant.ProjectId}" + (grant.NoteType is null ? "" : $" ({grant.NoteType} notes)");
                    if (result.Ok) sink.Record(EventTypes.GrantApplied, new { taskId, proposalId = proposal.ProposalId, grantId = grant.GrantId, action, projectId = grant.ProjectId, noteType = grant.NoteType });
                    break;
                }
            case "filing.revoke":
                result = _preferences.Update(p => p.Filing.Grants.RemoveAll(g => g.GrantId == value), proposal.Reason, now, taskId, proposal.ProposalId);
                summary = $"Revoked standing grant {value}";
                break;
            case "sources.allowOnlineSearch":
                if (!bool.TryParse(value, out var allow)) return ExecutionResult.Fail("sources.allowOnlineSearch must be true or false.");
                result = _preferences.Update(p => p.Sources.AllowOnlineSearch = allow, proposal.Reason, now, taskId, proposal.ProposalId);
                summary = allow ? "External profiles may search online when a task is approved with search" : "Online search is off";
                break;
            default:
                return ExecutionResult.Fail($"Unhandled key {key}.");
        }
        if (!result.Ok) return ExecutionResult.Fail(result.Error ?? "change set failed");
        sink.Record(EventTypes.ChangeSetApplied, new { taskId, proposalId = proposal.ProposalId, changeSetId = result.ChangeSet!.ChangeSetId, kind = result.ChangeSet.Kind, path = result.ChangeSet.Path, key, afterSha256 = result.ChangeSet.AfterSha256, acceptance = proposal.Target.GetValueOrDefault("acceptance") });
        return ExecutionResult.Ok(summary, new Dictionary<string, string> { ["changeSetId"] = result.ChangeSet.ChangeSetId, ["key"] = key });
    }

    public ExecutionResult UpdatePrompt(Proposal proposal, Decision decision, string taskId, IExecutionSink sink)
    {
        var name = proposal.Target.GetValueOrDefault("name") ?? "";
        var content = (proposal.Target.GetValueOrDefault("content") ?? "").Trim();
        if (!PromptNames.Contains(name)) return ExecutionResult.Fail($"Prompt must be one of {string.Join(", ", PromptNames)}.");
        if (content.Length > 2000) return ExecutionResult.Fail("A prompt fragment is at most 2000 characters.");
        Directory.CreateDirectory(_root.PromptsDirectory);
        var path = Path.Combine(_root.PromptsDirectory, name + ".md");
        var result = _changes.Apply(ChangeKinds.Prompt, path, content + Environment.NewLine, proposal.Reason, _clock(), taskId, proposal.ProposalId);
        if (!result.Ok) return ExecutionResult.Fail(result.Error ?? "change set failed");
        sink.Record(EventTypes.ChangeSetApplied, new { taskId, proposalId = proposal.ProposalId, changeSetId = result.ChangeSet!.ChangeSetId, kind = result.ChangeSet.Kind, path, afterSha256 = result.ChangeSet.AfterSha256, acceptance = proposal.Target.GetValueOrDefault("acceptance") });
        return ExecutionResult.Ok($"Prompt fragment '{name}' updated ({content.Length} chars)", new Dictionary<string, string> { ["changeSetId"] = result.ChangeSet.ChangeSetId, ["name"] = name });
    }

    public ExecutionResult Revert(string changeSetId, string reason, IExecutionSink sink)
    {
        var result = _changes.Revert(changeSetId, reason, _clock());
        if (!result.Ok) return ExecutionResult.Fail(result.Error ?? "revert failed");
        sink.Record(EventTypes.ChangeSetReverted, new { changeSetId, kind = result.ChangeSet!.Kind, path = result.ChangeSet.Path, reason });
        return ExecutionResult.Ok($"Reverted change set {changeSetId} ({result.ChangeSet.Kind})", new Dictionary<string, string> { ["changeSetId"] = changeSetId });
    }
}
