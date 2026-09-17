using System.Text;
using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Evidence;
using Relay.Core.Policy;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>§5 provenance and hosted authorization gates.</summary>
public class ProvenanceGrantTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 18, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    public void Dispose() => _tmp.Dispose();

    private HostedAuthorization Auth(bool hostedEnabled = true)
    {
        _tmp.Root.EnsureLayout(_clock);
        return new HostedAuthorization(_tmp.Root, _clock, hostedEnabled);
    }

    private RuntimeDiagnostics Diag(string name) => new(
        Path.Combine(_tmp.Root.DevRunsDirectory, name, "runtime.jsonl"), name);

    [Fact]
    public void Listening_enabled_hosted_disabled_persists_transcript_zero_hosted()
    {
        var auth = Auth(hostedEnabled: false);
        using var diagnostics = Diag("s5-listen");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics, hosted: auth);

        runtime.StartListening();
        var seg = runtime.IngestSegment("private hallway talk");
        Assert.False(string.IsNullOrEmpty(seg.SegmentId));

        var artifact = auth.Evidence.TryLoad("seg:" + seg.SegmentId);
        Assert.NotNull(artifact);
        Assert.Equal(ContentRestriction.LocalOnly, artifact!.Restriction);
        Assert.Equal(0, auth.Outbound.HostedRequestCount);

        var blocked = auth.Outbound.PrepareAndDispatch(
            "jev",
            HostedPurposes.Judgment,
            [new OutboundContentItem { ArtifactId = artifact.ArtifactId, Role = "state", Required = true }]);
        Assert.False(blocked.Ok);
        Assert.Equal(OutboundBlockReasons.HostedDisabled, blocked.BlockReason);
        Assert.Equal(0, auth.Outbound.HostedRequestCount);
    }

    [Fact]
    public void Standing_grant_permits_repeated_in_scope_calls()
    {
        var auth = Auth(hostedEnabled: true);
        var exportable = auth.Evidence.PutText(
            "public project note",
            projectIds: ["proj-a"],
            restriction: ContentRestriction.HostedEligible,
            label: "note");

        var grant = auth.Grants.Create(
            provider: "jev",
            projectIds: ["proj-a"],
            allowedPurposes: [HostedPurposes.Judgment],
            maxRequests: 10,
            maxInputTokens: 100_000,
            maxSpendingMicros: 1_000_000);

        for (var i = 0; i < 3; i++)
        {
            var result = auth.Outbound.PrepareAndDispatch(
                "jev",
                HostedPurposes.Judgment,
                [new OutboundContentItem { ArtifactId = exportable.ArtifactId, Role = "state", Required = true }],
                grantId: grant.GrantId,
                estimatedInputTokens: 10,
                estimatedSpendingMicros: 10);
            Assert.True(result.Ok, result.BlockReason);
            auth.Budgets.Reconcile(result.Reservation!.ReservationId, 10, 10);
        }

        Assert.Equal(3, auth.Outbound.HostedRequestCount);
        var reloaded = auth.Grants.TryLoad(grant.GrantId)!;
        Assert.Equal(3, reloaded.RequestsUsed);
        Assert.False(reloaded.Revoked);
    }

    [Fact]
    public void Mixed_local_only_and_exportable_never_exports_restricted()
    {
        var auth = Auth(hostedEnabled: true);
        var local = auth.Evidence.PutText("secret", projectIds: ["proj-a"], restriction: ContentRestriction.LocalOnly);
        var open = auth.Evidence.PutText("public", projectIds: ["proj-a"], restriction: ContentRestriction.HostedEligible);
        auth.Grants.Create("jev", projectIds: ["proj-a"], allowedPurposes: [HostedPurposes.Judgment]);

        // Required local-only → blocked; must not send to obtain permission.
        var blocked = auth.Outbound.PrepareAndDispatch(
            "jev",
            HostedPurposes.Judgment,
            [
                new OutboundContentItem { ArtifactId = local.ArtifactId, Role = "state", Required = true },
                new OutboundContentItem { ArtifactId = open.ArtifactId, Role = "state", Required = false },
            ]);
        Assert.False(blocked.Ok);
        Assert.Equal(OutboundBlockReasons.BlockedContextRestriction, blocked.BlockReason);
        Assert.Equal(0, auth.Outbound.HostedRequestCount);

        // Optional local-only excluded; exportable alone succeeds.
        var ok = auth.Outbound.PrepareAndDispatch(
            "jev",
            HostedPurposes.Judgment,
            [
                new OutboundContentItem { ArtifactId = local.ArtifactId, Role = "state", Required = false },
                new OutboundContentItem { ArtifactId = open.ArtifactId, Role = "state", Required = true },
            ],
            estimatedInputTokens: 5,
            estimatedSpendingMicros: 5);
        Assert.True(ok.Ok, ok.BlockReason);
        Assert.DoesNotContain(local.ArtifactId, ok.Intent!.IncludedArtifactIds);
        Assert.Contains(local.ArtifactId, ok.Intent.ExcludedArtifactIds);
        Assert.Contains(open.ArtifactId, ok.Intent.IncludedArtifactIds);
        var payload = Encoding.UTF8.GetString(ok.ApprovedBytes!);
        Assert.DoesNotContain("secret", payload, StringComparison.Ordinal);
        Assert.Contains("public", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_then_query_from_local_only_remain_restricted()
    {
        var auth = Auth(hostedEnabled: true);
        var transcript = auth.Evidence.PutText(
            "hallway: launch delayed",
            restriction: ContentRestriction.LocalOnly,
            kind: "transcript");

        var summary = auth.Evidence.Derive("Summary: launch delayed", [transcript.ArtifactId], label: "summary");
        Assert.Equal(ContentRestriction.LocalOnly, summary.Restriction);

        var query = auth.Evidence.Derive("query: when is launch?", [summary.ArtifactId], label: "query", kind: "query");
        Assert.Equal(ContentRestriction.LocalOnly, query.Restriction);
        Assert.Equal(ContentRestriction.LocalOnly, auth.Provenance.EffectiveRestriction(query.ArtifactId));

        auth.Grants.Create("jev", allowedPurposes: [HostedPurposes.Search, HostedPurposes.Judgment]);
        var blocked = auth.Outbound.PrepareAndDispatch(
            "jev",
            HostedPurposes.Search,
            [new OutboundContentItem { ArtifactId = query.ArtifactId, Role = "query", Required = true }]);
        Assert.False(blocked.Ok);
        Assert.Equal(OutboundBlockReasons.BlockedContextRestriction, blocked.BlockReason);
        Assert.Equal(0, auth.Outbound.HostedRequestCount);
    }

    [Fact]
    public void Cross_project_package_requires_coverage_for_every_source()
    {
        var auth = Auth(hostedEnabled: true);
        var a = auth.Evidence.PutText("a", projectIds: ["proj-a"], restriction: ContentRestriction.HostedEligible);
        var b = auth.Evidence.PutText("b", projectIds: ["proj-b"], restriction: ContentRestriction.HostedEligible);

        var grantAOnly = auth.Grants.Create("jev", projectIds: ["proj-a"], allowedPurposes: [HostedPurposes.Judgment]);
        var missing = auth.Outbound.PrepareAndDispatch(
            "jev",
            HostedPurposes.Judgment,
            [
                new OutboundContentItem { ArtifactId = a.ArtifactId, Role = "state", Required = true },
                new OutboundContentItem { ArtifactId = b.ArtifactId, Role = "state", Required = true },
            ],
            grantId: grantAOnly.GrantId);
        Assert.False(missing.Ok);
        Assert.Equal(OutboundBlockReasons.ProjectScopeMismatch, missing.BlockReason);

        var grantBoth = auth.Grants.Create("jev", projectIds: ["proj-a", "proj-b"], allowedPurposes: [HostedPurposes.Judgment]);
        var ok = auth.Outbound.PrepareAndDispatch(
            "jev",
            HostedPurposes.Judgment,
            [
                new OutboundContentItem { ArtifactId = a.ArtifactId, Role = "state", Required = true },
                new OutboundContentItem { ArtifactId = b.ArtifactId, Role = "state", Required = true },
            ],
            grantId: grantBoth.GrantId,
            estimatedInputTokens: 8,
            estimatedSpendingMicros: 8);
        Assert.True(ok.Ok, ok.BlockReason);
    }

    [Fact]
    public void Revoking_grant_blocks_queued_requests()
    {
        var auth = Auth(hostedEnabled: true);
        var open = auth.Evidence.PutText("ok", projectIds: ["p1"], restriction: ContentRestriction.HostedEligible);
        var grant = auth.Grants.Create("jev", projectIds: ["p1"], allowedPurposes: [HostedPurposes.Judgment]);

        var prepared = auth.Outbound.Prepare(
            "jev",
            HostedPurposes.Judgment,
            [new OutboundContentItem { ArtifactId = open.ArtifactId, Role = "state", Required = true }],
            grantId: grant.GrantId,
            estimatedInputTokens: 5,
            estimatedSpendingMicros: 5);
        Assert.True(prepared.Ok, prepared.BlockReason);
        Assert.Equal("queued", prepared.Intent!.Status);

        auth.Grants.Revoke(grant.GrantId);
        var dispatched = auth.Outbound.Dispatch(prepared.Intent.IntentId);
        Assert.False(dispatched.Ok);
        Assert.Equal(OutboundBlockReasons.RevokedBeforeDispatch, dispatched.BlockReason);
        Assert.Equal(0, auth.Outbound.HostedRequestCount);
    }

    [Fact]
    public void Expiry_while_queued_blocks_dispatch()
    {
        var auth = Auth(hostedEnabled: true);
        var open = auth.Evidence.PutText("ok", projectIds: ["p1"], restriction: ContentRestriction.HostedEligible);
        var grant = auth.Grants.Create(
            "jev",
            projectIds: ["p1"],
            allowedPurposes: [HostedPurposes.Judgment],
            expiresAt: _clock.UtcNow.AddMinutes(5));

        var prepared = auth.Outbound.Prepare(
            "jev",
            HostedPurposes.Judgment,
            [new OutboundContentItem { ArtifactId = open.ArtifactId, Role = "state", Required = true }],
            grantId: grant.GrantId,
            estimatedInputTokens: 5,
            estimatedSpendingMicros: 5);
        Assert.True(prepared.Ok);

        _clock.Advance(TimeSpan.FromMinutes(10));
        var dispatched = auth.Outbound.Dispatch(prepared.Intent!.IntentId);
        Assert.False(dispatched.Ok);
        Assert.Equal(OutboundBlockReasons.GrantExpired, dispatched.BlockReason);
        Assert.Equal(0, auth.Outbound.HostedRequestCount);
    }

    [Fact]
    public void Concurrent_requests_cannot_overspend_same_grant()
    {
        var auth = Auth(hostedEnabled: true);
        var open = auth.Evidence.PutText("ok", projectIds: ["p1"], restriction: ContentRestriction.HostedEligible);
        var grant = auth.Grants.Create(
            "jev",
            projectIds: ["p1"],
            allowedPurposes: [HostedPurposes.Judgment],
            maxRequests: 100,
            maxInputTokens: 100,
            maxSpendingMicros: 100);

        var items = new[] { new OutboundContentItem { ArtifactId = open.ArtifactId, Role = "state", Required = true } };
        var first = auth.Outbound.Prepare("jev", HostedPurposes.Judgment, items, grant.GrantId, 60, 60);
        Assert.True(first.Ok, first.BlockReason);

        var second = auth.Outbound.Prepare("jev", HostedPurposes.Judgment, items, grant.GrantId, 60, 60);
        Assert.False(second.Ok);
        Assert.Equal(OutboundBlockReasons.BudgetExceeded, second.BlockReason);

        // Direct concurrent reserve also cannot exceed remaining headroom (40 left).
        Assert.Null(auth.Budgets.TryReserve(grant.GrantId, HostedPurposes.Judgment, 50, 50));
        Assert.NotNull(auth.Budgets.TryReserve(grant.GrantId, HostedPurposes.Judgment, 40, 40));
    }

    [Fact]
    public async Task Insufficient_permitted_context_blocks_judgment_not_masquerade()
    {
        var auth = Auth(hostedEnabled: true);
        var local = auth.Evidence.PutText("only local evidence", restriction: ContentRestriction.LocalOnly);
        auth.Grants.Create("jev", allowedPurposes: [HostedPurposes.Judgment, HostedPurposes.DecisionQuestion]);

        var question = auth.Evidence.Derive(
            "Is the launch delayed?",
            [local.ArtifactId],
            label: "decision_question",
            kind: "decision_question");
        Assert.Equal(ContentRestriction.LocalOnly, question.Restriction);

        var blocked = auth.Outbound.PrepareAndDispatch(
            "jev",
            HostedPurposes.DecisionQuestion,
            [new OutboundContentItem { ArtifactId = question.ArtifactId, Role = "question", Required = true }]);
        Assert.False(blocked.Ok);
        Assert.Equal(OutboundBlockReasons.BlockedContextRestriction, blocked.BlockReason);
        Assert.Equal(0, auth.Outbound.HostedRequestCount);

        // Controller may still run local jobs; must not treat this as a complete hosted judgment.
        using var diagnostics = Diag("s5-block-judgment");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics, hosted: auth);
        var started = runtime.StartDirectCase("needs judgment");
        runtime.EnqueueCommand(new RuntimeCommand
        {
            CommandId = "judge-1",
            CaseId = started.Id,
            Kind = RuntimeCommandKinds.RequestJudgments,
            CreatedAt = _clock.UtcNow,
            Payload = new Dictionary<string, JsonElement>
            {
                ["artifactIds"] = JsonSerializer.SerializeToElement(new[] { local.ArtifactId }),
                ["provider"] = JsonSerializer.SerializeToElement("jev"),
            },
        });
        await runtime.DispatchPendingCommandsAsync(started.Id);
        var events = runtime.Cases.LoadEvents(started.Id);
        Assert.Contains(events, e => e.Type == CaseDomainEventTypes.CommandFailed
            && e.Payload.TryGetProperty("judgmentComplete", out var jc)
            && jc.ValueKind == JsonValueKind.False);
        Assert.Equal(0, auth.Outbound.HostedRequestCount);
    }

    [Fact]
    public void Hosted_eligible_does_not_grant_permission()
    {
        var auth = Auth(hostedEnabled: true);
        var eligible = auth.Evidence.PutText("eligible but no grant", restriction: ContentRestriction.HostedEligible);
        Assert.Equal(ContentRestriction.HostedEligible, eligible.Restriction);

        var blocked = auth.Outbound.PrepareAndDispatch(
            "jev",
            HostedPurposes.Judgment,
            [new OutboundContentItem { ArtifactId = eligible.ArtifactId, Role = "state", Required = true }]);
        Assert.False(blocked.Ok);
        Assert.Equal(OutboundBlockReasons.NoGrant, blocked.BlockReason);
    }

    [Fact]
    public void Budget_display_marks_estimates_not_exact_billed()
    {
        var auth = Auth(hostedEnabled: true);
        var open = auth.Evidence.PutText("ok", restriction: ContentRestriction.HostedEligible);
        var grant = auth.Grants.Create("jev", maxSpendingMicros: 10_000, maxInputTokens: 10_000);
        var prepared = auth.Outbound.Prepare(
            "jev", HostedPurposes.Judgment,
            [new OutboundContentItem { ArtifactId = open.ArtifactId, Role = "state", Required = true }],
            grant.GrantId, 100, 100);
        Assert.True(prepared.Ok);
        var display = BudgetReservations.FormatForDisplay(prepared.Reservation!);
        Assert.Contains("estimated", display, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not exact billed", display, StringComparison.OrdinalIgnoreCase);

        var held = auth.Budgets.HoldConservative(prepared.Reservation!.ReservationId);
        Assert.Equal(BudgetReservationStatus.HeldConservative, held.Status);

        var billed = auth.Budgets.Reconcile(held.ReservationId, 80, 90);
        Assert.False(billed.IsEstimate);
        Assert.Contains("billed", BudgetReservations.FormatForDisplay(billed), StringComparison.OrdinalIgnoreCase);
    }
}
