using Relay.Core.Captures;
using Relay.Core.Execution;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Memory;
using Relay.Core.Notes;
using Relay.Core.Orchestration;
using Relay.Core.Policy;
using Relay.Core.Projects;
using Relay.Core.Stream;
using Relay.Core.Tasks;

namespace Relay.Core.Session;

/// <summary>
/// Remembering. Two paths feed one filing step: a dictated note capture (listening off) is cut into
/// atomic notes with exact spans; a note the mind raised while listening arrives already written, with
/// the excerpt as its source. Each note is routed by confidence (file / Review / leave in the inbox),
/// checked for conflicting decisions, and every uncertain call is left to the user in Review.
/// </summary>
public sealed partial class SessionCoordinator
{
    private ReviewStore ReviewStore => _reviewStore ??= new ReviewStore(_root);
    private ReviewStore? _reviewStore;

    private sealed record FilingOutcome(bool Filed, bool Pending, bool Unrouted, int Disputes, string? ProjectSlug, string NoteId, string? Error);

    private string OrganizeNoteMemory(CaptureDraft draft, string sourceEventId)
    {
        var extracted = NoteExtractor.Extract(draft.Text);
        if (extracted.Count == 0) extracted = [new ExtractedNote(NoteTypes.Idea, draft.Text, 0, draft.Text.Length, NoteExtractor.Topic(draft.Text))];
        if (Append(EventTypes.NoteExtracted, new { captureId = draft.CaptureId, sourceEventId, count = extracted.Count, types = extracted.Select(e => e.Type) }) is null) return "";

        int filed = 0, pending = 0, unrouted = 0, disputed = 0;
        foreach (var x in extracted)
        {
            var outcome = FileNote(x.Type, x.Text, x.Topic, draft.CaptureId, sourceEventId, new SourceSpan(sourceEventId, x.Start, x.End), null, "note:" + draft.CaptureId, Producers.Router);
            if (outcome is null) return "";
            if (outcome.Filed) filed++;
            if (outcome.Pending) pending++;
            if (outcome.Unrouted) unrouted++;
            disputed += outcome.Disputes;
        }

        var parts = new List<string>();
        if (filed > 0) parts.Add($"{filed} filed");
        if (pending > 0) parts.Add($"{pending} need your routing decision");
        if (unrouted > 0) parts.Add($"{unrouted} unrouted in staging");
        if (disputed > 0) parts.Add($"{disputed} disputed");
        return $"Saved {extracted.Count} note{(extracted.Count == 1 ? "" : "s")} · {string.Join(" · ", parts)}";
    }

    /// <summary>
    /// The whole of a piece of work is a note worth keeping: file it and finish, without a planning turn. The mind
    /// wrote the text on the pass that heard it; the excerpt is its source and the project hint raises routing confidence.
    /// </summary>
    private void Remember(TaskState task, string noteText, string? noteType, string? topic, string? projectHint, Excerpt? excerpt, string producer)
    {
        var type = noteType is not null && NoteTypes.All.Contains(noteType) ? noteType : NoteExtractor.Classify(noteText);
        var span = excerpt is not null ? new SourceSpan(excerpt.ExcerptId, 0, excerpt.Text.Length) : new SourceSpan(task.SourceEventId, 0, noteText.Length);
        var outcome = FileNote(type, noteText, topic, task.CaptureId, task.SourceEventId, span, projectHint, task.TaskId, producer);
        if (outcome is null) return; // locked
        var steps = new List<string> { $"Heard ({task.Why}) and kept a {type} note", outcome.Filed ? $"Filed under {outcome.ProjectSlug}" : outcome.Pending ? "Routing needs your decision (Review)" : "No project matched; kept in the inbox" };
        if (outcome.Disputes > 0) steps.Add($"{outcome.Disputes} earlier decision(s) may conflict (Review)");
        if (outcome.Error is not null) steps.Add("Filing refused: " + outcome.Error);
        task.Plan = new TurnPlan(true, outcome.Filed ? $"Filed {type} note under {outcome.ProjectSlug}" : $"Kept {type} note in the inbox", steps, null,
            excerpt is null ? [] : [new Citation(Search.SearchIndex.ExcerptKind, excerpt.ExcerptId, null, null, Truncate(excerpt.Text, 120), span)], [], producer);
        // The filing already ran through proposal → policy → executor; the task records the outcome without re-proposing.
        task.Proposals.Add(new ProposalState
        {
            Proposal = new Proposal(outcome.NoteId, Actions.RouteNote, "recorded by the remember lane", new Dictionary<string, string> { ["noteId"] = outcome.NoteId }, [task.SourceEventId], [], Risks.StagingWrite, false, producer),
            Decision = new Decision(DecisionOutcome.Allow, Tier.Automatic, [outcome.Filed ? "filed automatically" : "left in the inbox"], new Dictionary<string, string>()),
            Status = outcome.Filed ? "executed" : outcome.Error is not null ? "denied" : "skipped",
            Result = outcome.Filed ? ExecutionResult.Ok($"Note {outcome.NoteId} filed under {outcome.ProjectSlug}") : null,
        });
        FinishTask(task);
    }

    /// <summary>
    /// One note through the filing step: draft in staging, route by confidence, file when confident (Tier A
    /// proposal through policy and the executor), otherwise leave it for Review. Returns null when the ledger locked.
    /// </summary>
    private FilingOutcome? FileNote(string type, string text, string? topic, string captureId, string sourceEventId, SourceSpan span, string? projectHint, string taskId, string producer)
    {
        var now = _clock.UtcNow;
        // The topic rides on the draft, not in the ledger: a note heard in the room must leave no words there.
        var note = new DraftNote(Ulid.NewUlid(now), captureId, sourceEventId, now, type, DraftNote.DraftStatus, null, DraftNote.UnroutedRouting, null, text, [span], topic);
        var path = _notes.Write(note);
        if (Append(EventTypes.NoteDraftCreated, new { noteId = note.NoteId, captureId, sourceEventId, chars = text.Length, type, spanSource = span.EventId, spanStart = span.Start, spanEnd = span.End, path, by = producer }) is null) return null;
        _services.Index.IndexDraft(note);

        var profiles = _services.Registry.Active.Select(ProjectProfile.Build).ToList();
        var routing = NoteRouter.Route(text, profiles);
        // A project the mind heard named is strong evidence; it lifts that candidate rather than replacing the router.
        var best = routing.Best;
        if (projectHint is not null && _services.Registry.FindActive(projectHint) is { } hinted)
        {
            var hintedCandidate = routing.Candidates.FirstOrDefault(c => c.ProjectId == hinted.Id);
            var boosted = Math.Min(1.0, Math.Max(hintedCandidate?.Confidence ?? 0, 0.6) + 0.25);
            if (best is null || best.ProjectId == hinted.Id || boosted > best.Confidence)
                best = new RoutingCandidate(hinted.Id, hinted.Slug, hinted.Name, boosted, [.. hintedCandidate?.Reasons ?? [], $"named in the conversation ({projectHint})"]);
        }

        var auto = _settings.Orchestrator.AutoRouteThreshold;
        var review = _settings.Orchestrator.ReviewThreshold;
        var projectAuto = best is { } b && _services.Registry.ById(b.ProjectId) is { } p ? p.Policy.AutoRouteThreshold ?? auto : auto;
        // A standing grant ("file Atlas decisions without asking") files at the Review threshold instead of waiting for the user.
        var grant = best is null ? null : Preferences.Grants.FirstOrDefault(g => g.Covers(Actions.RouteNote, new Dictionary<string, string> { ["projectId"] = best.ProjectId, ["type"] = type }));
        var threshold = grant is null ? projectAuto : Math.Min(projectAuto, review);
        if (best is not null && best.Confidence >= threshold)
        {
            var project = _services.Registry.ById(best.ProjectId)!;
            if (grant is not null && best.Confidence < projectAuto)
            {
                Append(EventTypes.GrantApplied, new { taskId, noteId = note.NoteId, action = Actions.RouteNote, grantId = grant.GrantId, projectId = project.Id, noteType = grant.NoteType, confidence = best.Confidence });
                best = best with { Reasons = [.. best.Reasons, $"standing grant {grant.GrantId} files {project.Slug} {(grant.NoteType is null ? "notes" : grant.NoteType + "s")} without asking"] };
            }
            var existing = Directory.Exists(project.RootPath) ? ProjectNoteStore.ReadAll(project.RootPath).Notes.Select(n => n.Note).ToList() : [];
            var disputes = DisputeDetector.Find(type, text, existing);
            var target = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["noteId"] = note.NoteId,
                ["projectId"] = project.Id,
                ["confidence"] = best.Confidence.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                ["type"] = type,
            };
            if (disputes.Count > 0) target["disputedWith"] = string.Join(",", disputes.Select(d => d.Existing.Id));
            var proposal = new Proposal(Ulid.NewUlid(now), Actions.RouteNote, best.Reasons.Count > 0 ? string.Join("; ", best.Reasons) : routing.Summary, target, [sourceEventId],
                [$"New {type} note in {project.Slug}"], Risks.ControlledWrite, false, producer);
            var (ok, why) = ExecuteAutomatic(proposal, taskId);
            if (ok)
            {
                foreach (var d in disputes)
                {
                    Append(EventTypes.NoteDisputed, new { noteId = note.NoteId, otherNoteId = d.Existing.Id, projectId = project.Id, reason = d.Reason, similarity = d.Similarity });
                    ReviewStore.SaveDispute(new DisputeRecord(project.Id, project.Slug, note.NoteId, d.Existing.Id, text, d.Existing.Body, d.Reason, now));
                }
                return new FilingOutcome(true, false, false, disputes.Count, project.Slug, note.NoteId, null);
            }
            Append(EventTypes.NoteRoutingDeferred, new { noteId = note.NoteId, reason = "automatic filing was refused: " + why, candidates = routing.Candidates });
            ReviewStore.SaveRouting(new RoutingDecisionRecord(note.NoteId, captureId, type, text, now, routing.Summary + " — " + why, routing.Candidates));
            return new FilingOutcome(false, true, false, 0, null, note.NoteId, why);
        }
        if (best is not null && best.Confidence >= review)
        {
            var candidates = routing.Candidates.Any(c => c.ProjectId == best.ProjectId) ? routing.Candidates : [best, .. routing.Candidates];
            Append(EventTypes.NoteRoutingDeferred, new { noteId = note.NoteId, reason = routing.Summary, confidence = best.Confidence, candidates });
            ReviewStore.SaveRouting(new RoutingDecisionRecord(note.NoteId, captureId, type, text, now, routing.Summary, candidates));
            return new FilingOutcome(false, true, false, 0, null, note.NoteId, null);
        }
        Append(EventTypes.NoteRoutingDeferred, new { noteId = note.NoteId, reason = routing.Summary, confidence = routing.Confidence, candidates = routing.Candidates });
        return new FilingOutcome(false, false, true, 0, null, note.NoteId, null);
    }

    /// <summary>Runs a Tier A proposal outside a planned task (note routing). Denied or failed operations return the reason and change nothing.</summary>
    private (bool Ok, string? Reason) ExecuteAutomatic(Proposal proposal, string taskId)
    {
        PersistProposal(proposal);
        Append(EventTypes.ProposalReceived, new { taskId, proposalId = proposal.ProposalId, action = proposal.Action, reason = proposal.Reason, target = proposal.Target, sourceEventIds = proposal.SourceEventIds, proposedBy = proposal.ProposedBy, hash = proposal.Hash() });
        var decision = PolicyEngine.Decide(proposal, World);
        Append(EventTypes.ProposalDecided, new { taskId, proposalId = proposal.ProposalId, action = proposal.Action, outcome = decision.Outcome.ToString(), tier = decision.Tier.ToString(), reasons = decision.Reasons, target = decision.NormalizedTarget });
        if (decision.Outcome != DecisionOutcome.Allow) return (false, string.Join(" ", decision.Reasons));
        var capability = _capabilities.Issue(proposal, _clock.UtcNow);
        var result = _executor.Execute(proposal, capability, World, taskId, this);
        var state = new ProposalState { Proposal = proposal, Decision = decision, Status = result.Status == ExecutionStatus.Completed ? "executed" : "failed", Result = result };
        RefreshIndexAfter(state);
        return result.Status == ExecutionStatus.Completed ? (true, null) : (false, result.Error);
    }

    // ----------------------------------------------------------------------------------------
    // Review resolutions
    // ----------------------------------------------------------------------------------------

    /// <summary>The user filed an inbox note under a project. Any pending routing suggestion for it is retired as resolved.</summary>
    public bool RouteDraftNote(string noteId, string projectId)
    {
        var ok = PromoteDraftNote(noteId, projectId);
        if (ok) ReviewStore.ResolveRouting(noteId, "routed");
        Notify();
        return ok;
    }

    /// <summary>The user chose to leave a note in the inbox; its routing suggestions are retired (file kept under resolved) and the choice recorded.</summary>
    public void KeepUnrouted(string noteId)
    {
        if (ReviewStore.ResolveRouting(noteId, "kept-unrouted"))
            Append(EventTypes.NoteRoutingDeferred, new { noteId, reason = "kept unrouted by user" });
        Notify();
    }

    /// <summary>Resolves a dispute: either the new decision supersedes the old one, or both stay active side by side.</summary>
    public bool ResolveDispute(string newNoteId, bool newSupersedesExisting)
    {
        var dispute = ReviewStore.Dispute(newNoteId);
        if (dispute is null) { _notice = "That dispute is no longer pending."; Notify(); return false; }
        bool ok;
        if (newSupersedesExisting)
        {
            ok = RunUserOperation(Actions.SupersedeNote, "Supersede the earlier decision",
                Targets(("projectId", dispute.ProjectId), ("noteId", dispute.ExistingNoteId), ("supersededBy", dispute.NewNoteId)), "Resolved from Review: the newer decision replaces the earlier one.");
        }
        else
        {
            ok = RunUserOperation(Actions.ModifyNote, "Keep both decisions",
                Targets(("projectId", dispute.ProjectId), ("noteId", dispute.NewNoteId), ("status", NoteStatus.Active)), "Resolved from Review: both decisions stand.");
        }
        if (ok) ReviewStore.ResolveDispute(newNoteId, newSupersedesExisting ? "superseded" : "kept-both");
        Notify();
        return ok;
    }

    private IEnumerable<ReviewItem> MemoryReviewItems()
    {
        foreach (var d in ReviewStore.PendingDisputes())
        {
            yield return new ReviewItem(ReviewItemKind.DisputedNotes, $"Two decisions in '{d.ProjectSlug}' may conflict",
                $"New: {d.NewText}\n\nExisting: {d.ExistingText}\n\n{d.Reason}. Both are kept; choose whether the new one supersedes the old.", d.NewNoteId);
        }
    }

    /// <summary>
    /// The inbox: every unrouted note in staging exactly once, newest first, carrying the router's
    /// candidates when it asked for a decision. A routing decision whose note is no longer in staging
    /// is stale and is not shown.
    /// </summary>
    private IReadOnlyList<InboxItem> InboxViews()
    {
        var pending = ReviewStore.PendingRouting().ToDictionary(r => r.NoteId, StringComparer.Ordinal);
        return _notes.Unrouted()
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => pending.TryGetValue(d.NoteId, out var r)
                ? new InboxItem(d.NoteId, d.Type, d.CreatedAt, d.Text, r.Summary, r.Candidates)
                : new InboxItem(d.NoteId, d.Type, d.CreatedAt, d.Text, null, []))
            .ToList();
    }

    public IReadOnlyList<RoutingDecisionRecord> PendingRoutingDecisions => ReviewStore.PendingRouting();
    public IReadOnlyList<DisputeRecord> PendingDisputes => ReviewStore.PendingDisputes();
}
