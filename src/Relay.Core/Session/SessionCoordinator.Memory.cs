using Relay.Core.Captures;
using Relay.Core.Execution;
using Relay.Core.Ids;
using Relay.Core.Ledger;
using Relay.Core.Memory;
using Relay.Core.Notes;
using Relay.Core.Policy;
using Relay.Core.Projects;

namespace Relay.Core.Session;

/// <summary>
/// Note-mode organizing (phase 6): the capture is already durable in the ledger; this cuts it into
/// atomic notes with exact spans, routes each by confidence (file / Review / leave unrouted),
/// detects conflicting decisions, and leaves every uncertain call to the user in Review.
/// </summary>
public sealed partial class SessionCoordinator
{
    private ReviewStore ReviewStore => _reviewStore ??= new ReviewStore(_root);
    private ReviewStore? _reviewStore;

    private string OrganizeNoteMemory(CaptureDraft draft, string sourceEventId)
    {
        var extracted = NoteExtractor.Extract(draft.Text);
        if (extracted.Count == 0) extracted = [new ExtractedNote(NoteTypes.Idea, draft.Text, 0, draft.Text.Length, NoteExtractor.Topic(draft.Text))];
        if (Append(EventTypes.NoteExtracted, new { captureId = draft.CaptureId, sourceEventId, count = extracted.Count, types = extracted.Select(e => e.Type) }) is null) return "";

        var profiles = _services.Registry.Active.Select(ProjectProfile.Build).ToList();
        var auto = _settings.Orchestrator.AutoRouteThreshold;
        var review = _settings.Orchestrator.ReviewThreshold;
        int filed = 0, pending = 0, unrouted = 0, disputed = 0;

        foreach (var x in extracted)
        {
            var now = _clock.UtcNow;
            var note = new DraftNote(Ulid.NewUlid(now), draft.CaptureId, sourceEventId, now, x.Type, DraftNote.DraftStatus, null, DraftNote.UnroutedRouting, null,
                x.Text, [new SourceSpan(sourceEventId, x.Start, x.End)]);
            var path = _notes.Write(note);
            if (Append(EventTypes.NoteDraftCreated, new { noteId = note.NoteId, captureId = draft.CaptureId, sourceEventId, chars = x.Text.Length, type = x.Type, topic = x.Topic, spanStart = x.Start, spanEnd = x.End, path }) is null) return "";
            _services.Index.IndexDraft(note);

            var routing = NoteRouter.Route(x.Text, profiles);
            var projectAuto = routing.Best is { } b && _services.Registry.ById(b.ProjectId) is { } p ? p.Policy.AutoRouteThreshold ?? auto : auto;
            if (routing.Best is { } best && best.Confidence >= projectAuto)
            {
                var project = _services.Registry.ById(best.ProjectId)!;
                var existing = Directory.Exists(project.RootPath) ? ProjectNoteStore.ReadAll(project.RootPath).Notes.Select(n => n.Note).ToList() : [];
                var disputes = DisputeDetector.Find(x.Type, x.Text, existing);
                var target = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["noteId"] = note.NoteId,
                    ["projectId"] = project.Id,
                    ["confidence"] = best.Confidence.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["type"] = x.Type,
                };
                if (x.Topic is not null) target["topic"] = x.Topic;
                if (disputes.Count > 0) target["disputedWith"] = string.Join(",", disputes.Select(d => d.Existing.Id));
                var proposal = new Proposal(Ulid.NewUlid(now), Actions.RouteNote, routing.Summary, target, [sourceEventId],
                    [$"New {x.Type} note in {project.Slug}"], Risks.ControlledWrite, false, Producers.Router);
                var (ok, why) = ExecuteAutomatic(proposal, "note:" + draft.CaptureId);
                if (ok)
                {
                    filed++;
                    foreach (var d in disputes)
                    {
                        disputed++;
                        Append(EventTypes.NoteDisputed, new { noteId = note.NoteId, otherNoteId = d.Existing.Id, projectId = project.Id, reason = d.Reason, similarity = d.Similarity });
                        ReviewStore.SaveDispute(new DisputeRecord(project.Id, project.Slug, note.NoteId, d.Existing.Id, x.Text, d.Existing.Body, d.Reason, now));
                    }
                    continue;
                }
                Append(EventTypes.NoteRoutingDeferred, new { noteId = note.NoteId, reason = "automatic filing was refused: " + why, candidates = routing.Candidates });
                ReviewStore.SaveRouting(new RoutingDecisionRecord(note.NoteId, draft.CaptureId, x.Type, x.Text, now, routing.Summary + " — " + why, routing.Candidates));
                pending++;
            }
            else if (routing.Best is { } candidate && candidate.Confidence >= review)
            {
                Append(EventTypes.NoteRoutingDeferred, new { noteId = note.NoteId, reason = routing.Summary, confidence = candidate.Confidence, candidates = routing.Candidates });
                ReviewStore.SaveRouting(new RoutingDecisionRecord(note.NoteId, draft.CaptureId, x.Type, x.Text, now, routing.Summary, routing.Candidates));
                pending++;
            }
            else
            {
                Append(EventTypes.NoteRoutingDeferred, new { noteId = note.NoteId, reason = routing.Summary, confidence = routing.Confidence, candidates = routing.Candidates });
                unrouted++;
            }
        }

        var parts = new List<string>();
        if (filed > 0) parts.Add($"{filed} filed");
        if (pending > 0) parts.Add($"{pending} need your routing decision");
        if (unrouted > 0) parts.Add($"{unrouted} unrouted in staging");
        if (disputed > 0) parts.Add($"{disputed} disputed");
        return $"Saved {extracted.Count} note{(extracted.Count == 1 ? "" : "s")} · {string.Join(" · ", parts)}";
    }

    /// <summary>Runs a Tier A proposal outside a command turn (note routing). Denied or failed operations return the reason and change nothing.</summary>
    private (bool Ok, string? Reason) ExecuteAutomatic(Proposal proposal, string turnId)
    {
        PersistProposal(proposal);
        Append(EventTypes.ProposalReceived, new { turnId, proposalId = proposal.ProposalId, action = proposal.Action, reason = proposal.Reason, target = proposal.Target, sourceEventIds = proposal.SourceEventIds, proposedBy = proposal.ProposedBy, hash = proposal.Hash() });
        var decision = PolicyEngine.Decide(proposal, World);
        Append(EventTypes.ProposalDecided, new { turnId, proposalId = proposal.ProposalId, action = proposal.Action, outcome = decision.Outcome.ToString(), tier = decision.Tier.ToString(), reasons = decision.Reasons, target = decision.NormalizedTarget });
        if (decision.Outcome != DecisionOutcome.Allow) return (false, string.Join(" ", decision.Reasons));
        var capability = _capabilities.Issue(proposal, _clock.UtcNow);
        var result = _executor.Execute(proposal, capability, World, turnId, this);
        var state = new ProposalState { Proposal = proposal, Decision = decision, Status = result.Status == ExecutionStatus.Completed ? "executed" : "failed", Result = result };
        RefreshIndexAfter(state);
        return result.Status == ExecutionStatus.Completed ? (true, null) : (false, result.Error);
    }

    // ----------------------------------------------------------------------------------------
    // Review resolutions
    // ----------------------------------------------------------------------------------------

    /// <summary>The user picked a project for a note whose routing was uncertain.</summary>
    public bool RouteDraftNote(string noteId, string projectId)
    {
        var ok = PromoteDraftNote(noteId, projectId);
        if (ok) ReviewStore.ResolveRouting(noteId, "routed");
        Notify();
        return ok;
    }

    /// <summary>The user chose to leave a note in staging; the decision file is retired and the choice recorded.</summary>
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
        foreach (var r in ReviewStore.PendingRouting())
        {
            var candidates = r.Candidates.Count == 0 ? "No project matched." : string.Join("\n", r.Candidates.Select(c => $"• {c.Name} ({c.Slug}) — {c.Confidence:0.00}: {string.Join(", ", c.Reasons)}"));
            yield return new ReviewItem(ReviewItemKind.RoutingDecision, $"Where does this {r.Type} belong?", $"{r.Text}\n\n{candidates}", r.NoteId);
        }
        foreach (var d in ReviewStore.PendingDisputes())
        {
            yield return new ReviewItem(ReviewItemKind.DisputedNotes, $"Two decisions in '{d.ProjectSlug}' may conflict",
                $"New: {d.NewText}\n\nExisting: {d.ExistingText}\n\n{d.Reason}. Both are kept; choose whether the new one supersedes the old.", d.NewNoteId);
        }
    }

    public IReadOnlyList<RoutingDecisionRecord> PendingRoutingDecisions => ReviewStore.PendingRouting();
    public IReadOnlyList<DisputeRecord> PendingDisputes => ReviewStore.PendingDisputes();
}
