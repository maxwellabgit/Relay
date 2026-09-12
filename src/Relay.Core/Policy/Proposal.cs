using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core.Policy;

/// <summary>Every action a proposal may name. Anything else is denied as unknown.</summary>
public static class Actions
{
    public const string CreateDraftNote = "create_draft_note";   // Tier A: staging only
    public const string RouteNote = "route_note";                // Tier A: adds a new note file to a project (never modifies)
    public const string CreateProject = "create_project";        // Tier B
    public const string ModifyNote = "modify_note";              // Tier B
    public const string SupersedeNote = "supersede_note";        // Tier B
    public const string MoveNote = "move_note";                  // Tier B: a note changes project; versions travel with it
    public const string RenameProject = "rename_project";        // Tier B
    public const string ArchiveProject = "archive_project";      // Tier B
    public const string RestoreProject = "restore_project";      // Tier B
    public const string DeleteProject = "delete_project";        // Tier B: permanent, only ever from a direct request, never covered by a standing grant
    public const string LaunchWorker = "launch_worker";          // Tier B
    public const string ApplyPatch = "apply_patch";              // Tier B
    public const string ExportBackup = "export_backup";          // Tier B
    public const string ModelRequest = "model.request";          // Tier B: an exact package leaves the machine for a named external model
    public const string UpdatePreference = "update_preference";  // Tier B: a change set to preferences
    public const string UpdatePrompt = "update_prompt";          // Tier B: a change set to a prompt fragment
    public const string AddTool = "add_tool";                    // Tier B: promote a tested draft tool Relay built (docs/09, slice 6); one approval, one change set
    public const string RunShell = "run_shell";                  // Prohibited
    public const string SendMessage = "send_message";            // Prohibited

    public static readonly string[] Prohibited = [RunShell, SendMessage];

    /// <summary>Actions a standing grant may never cover: each needs a fresh approval every time.</summary>
    public static readonly string[] NeverGranted = [DeleteProject, ModelRequest, UpdatePreference, UpdatePrompt, LaunchWorker, ApplyPatch, AddTool];

    /// <summary>Actions an observed task may not propose: a destructive or self-modifying step needs the user's own words.</summary>
    public static readonly string[] DirectOnly = [DeleteProject, UpdatePreference, UpdatePrompt, ModelRequest];
}

public static class Producers
{
    public const string Rules = "rules";
    public const string Model = "model";
    public const string User = "user";
    public const string Router = "router";
    public const string Canned = "canned";
    public const string Judge = "judge";
    public const string Engine = "engine";
    /// <summary>The mind, whether it was working a task or listening to a conversation.</summary>
    public const string Mind = "mind";
}

public static class Risks
{
    public const string ReadOnly = "read_only";
    public const string StagingWrite = "staging_write";
    public const string ControlledWrite = "controlled_write";
    public const string External = "external";
    public const string Prohibited = "prohibited";
}

/// <summary>
/// A structured request for an action (contract §8). Proposals are data: the target is a flat
/// string map so it can be shown verbatim, hashed canonically, and edited by the user without
/// the model in the loop. <see cref="RequiresApproval"/> is what the proposer believes; the
/// policy engine recomputes it and the engine's answer is the one that counts.
/// <see cref="DependsOn"/> names proposals in the same task that must have executed first; a
/// dependent whose prerequisite was rejected cannot be approved on its own.
/// </summary>
public sealed record Proposal(
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("target")] IReadOnlyDictionary<string, string> Target,
    [property: JsonPropertyName("sourceEventIds")] IReadOnlyList<string> SourceEventIds,
    [property: JsonPropertyName("expectedEffects")] IReadOnlyList<string> ExpectedEffects,
    [property: JsonPropertyName("risk")] string Risk,
    [property: JsonPropertyName("requiresApproval")] bool RequiresApproval,
    [property: JsonPropertyName("proposedBy")] string ProposedBy,
    [property: JsonPropertyName("dependsOn")] IReadOnlyList<string>? DependsOn = null)
{
    public string? TargetOrNull(string key) => Target.TryGetValue(key, out var v) ? v : null;

    [JsonIgnore] public IReadOnlyList<string> Dependencies => DependsOn ?? [];

    /// <summary>SHA-256 over the canonical form: action, sorted target pairs, sorted source ids. Reason and effects are not part of what is approved.</summary>
    public string Hash()
    {
        var sb = new StringBuilder();
        sb.Append(Action).Append('\n');
        foreach (var kv in Target.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
        foreach (var id in SourceEventIds.OrderBy(s => s, StringComparer.Ordinal)) sb.Append("src:").Append(id).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public Proposal WithTarget(string newProposalId, IReadOnlyDictionary<string, string> newTarget)
        => this with { ProposalId = newProposalId, Target = newTarget, ProposedBy = Producers.User };

    public string ToJson() => JsonSerializer.Serialize(this, Storage.RelayJson.Indented);
}

public enum Tier { Automatic, RequiresApproval, Prohibited }

public enum DecisionOutcome { Allow, NeedsApproval, Deny }

public sealed record Decision(DecisionOutcome Outcome, Tier Tier, IReadOnlyList<string> Reasons, IReadOnlyDictionary<string, string> NormalizedTarget)
{
    public static Decision Deny(Tier tier, params string[] reasons) => new(DecisionOutcome.Deny, tier, reasons, new Dictionary<string, string>());
}

public sealed record Approval(
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("proposalHash")] string ProposalHash,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("by")] string By);
