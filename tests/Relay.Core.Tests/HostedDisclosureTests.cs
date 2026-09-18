using Relay.Core.Cases;
using Relay.Core.Judgments;
using Relay.Core.Privacy;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

public sealed class HostedDisclosureTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Listening_can_be_on_while_hosted_judgments_are_off()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("disc-listen");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics);
        var grants = new HostedGrantStore(_tmp.Root, _clock);
        IRelaySurface surface = new CaseRuntimeSurface(runtime, _clock, grants: grants);

        var on = surface.ToggleListening();
        Assert.True(on.Ok, on.Error);
        var snap = surface.Snapshot();
        Assert.True(snap.Listening);
        Assert.NotNull(snap.HostedJudgments);
        Assert.False(snap.HostedJudgments!.HasActiveGrant);
        Assert.Equal(HostedJudgmentView.WaitingGrant, snap.HostedJudgments.JevStatus);
        Assert.True(snap.HostedJudgments.ListeningIndependent);
    }

    [Fact]
    public void Local_only_source_blocks_the_complete_request()
    {
        var (policy, objects, _) = CreatePolicy();
        var local = objects.PutText("private words", classification: SourceClassification.LocalOnly);
        var request = BuildRequest([
            new JudgmentSourceRef(local.ObjectId, local.Sha256, SourceClassification.LocalOnly),
        ]);

        var ex = Assert.Throws<DisclosureException>(() =>
            policy.Authorize(request, HostedPurposes.ConversationScreen, HostedProviders.TypeSafe, "sess-1", null, 100));
        Assert.Equal("not_authorized", ex.Category);
        Assert.Contains("local_only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_derived_from_local_only_source_remains_blocked()
    {
        var derived = DisclosurePolicy.ClassifyDerived([
            SourceClassification.Public,
            SourceClassification.LocalOnly,
        ]);
        Assert.Equal(SourceClassification.LocalOnly, derived);

        var (policy, objects, grants) = CreatePolicy();
        grants.CreateSessionGrant(
            "sess-1",
            [HostedPurposes.ConversationScreen],
            [SourceClassification.HostedAllowedSession, SourceClassification.Public],
            10_000);

        var summary = objects.PutText("derived summary", classification: derived);
        var request = BuildRequest([
            new JudgmentSourceRef(summary.ObjectId, summary.Sha256, derived),
        ]);

        Assert.Throws<DisclosureException>(() =>
            policy.Authorize(request, HostedPurposes.ConversationScreen, HostedProviders.TypeSafe, "sess-1", null, 50));
    }

    [Fact]
    public void Session_grant_cannot_authorize_another_session()
    {
        var (policy, objects, grants) = CreatePolicy();
        grants.CreateSessionGrant(
            "sess-A",
            [HostedPurposes.DirectRouting],
            [SourceClassification.HostedAllowedSession],
            5_000);
        var src = objects.PutText("ok", classification: SourceClassification.HostedAllowedSession);
        var request = BuildRequest([
            new JudgmentSourceRef(src.ObjectId, src.Sha256, SourceClassification.HostedAllowedSession),
        ]);

        var ex = Assert.Throws<DisclosureException>(() =>
            policy.Authorize(request, HostedPurposes.DirectRouting, HostedProviders.TypeSafe, "sess-B", null, 40));
        Assert.Contains("Session grant", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_grant_cannot_authorize_another_project()
    {
        var (policy, objects, grants) = CreatePolicy();
        grants.CreateProjectGrant(
            "proj-A",
            [HostedPurposes.AcronymDisambiguation],
            [SourceClassification.HostedAllowedProject],
            5_000);
        var src = objects.PutText("BESS", classification: SourceClassification.HostedAllowedProject);
        var request = BuildRequest([
            new JudgmentSourceRef(src.ObjectId, src.Sha256, SourceClassification.HostedAllowedProject),
        ]);

        var ex = Assert.Throws<DisclosureException>(() =>
            policy.Authorize(request, HostedPurposes.AcronymDisambiguation, HostedProviders.TypeSafe, null, "proj-B", 40));
        Assert.Contains("Project grant", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Expired_revoked_or_exhausted_grant_blocks_dispatch()
    {
        var (policy, objects, grants) = CreatePolicy();
        var src = objects.PutText("ok", classification: SourceClassification.HostedAllowedSession);
        var request = BuildRequest([
            new JudgmentSourceRef(src.ObjectId, src.Sha256, SourceClassification.HostedAllowedSession),
        ]);

        var expired = grants.CreateSessionGrant(
            "sess-1",
            [HostedPurposes.ConversationScreen],
            [SourceClassification.HostedAllowedSession],
            1_000,
            expiresAt: T0.AddMinutes(-1));
        Assert.Throws<DisclosureException>(() =>
            policy.Authorize(request, HostedPurposes.ConversationScreen, HostedProviders.TypeSafe, "sess-1", null, 10));

        var revoked = grants.CreateSessionGrant(
            "sess-1",
            [HostedPurposes.ConversationScreen],
            [SourceClassification.HostedAllowedSession],
            1_000,
            grantId: "grant-revoked");
        grants.Revoke(revoked.GrantId);
        Assert.Throws<DisclosureException>(() =>
            policy.Authorize(request, HostedPurposes.ConversationScreen, HostedProviders.TypeSafe, "sess-1", null, 10));

        var tiny = grants.CreateSessionGrant(
            "sess-1",
            [HostedPurposes.ConversationScreen],
            [SourceClassification.HostedAllowedSession],
            maximumInputTokenBudget: 10,
            grantId: "grant-tiny");
        grants.RecordTokenUse(tiny.GrantId, 10);
        var ex = Assert.Throws<DisclosureException>(() =>
            policy.Authorize(request, HostedPurposes.ConversationScreen, HostedProviders.TypeSafe, "sess-1", null, 1));
        Assert.Contains("token budget", ex.Message, StringComparison.OrdinalIgnoreCase);
        _ = expired;
    }

    [Fact]
    public void Permitted_request_includes_only_approved_source_objects()
    {
        var (policy, objects, grants) = CreatePolicy();
        grants.CreateSessionGrant(
            "sess-1",
            [HostedPurposes.ConversationScreen],
            [SourceClassification.HostedAllowedSession],
            10_000);
        var a = objects.PutText("seg-a", classification: SourceClassification.HostedAllowedSession);
        var b = objects.PutText("seg-b", classification: SourceClassification.HostedAllowedSession);
        var request = BuildRequest([
            new JudgmentSourceRef(a.ObjectId, a.Sha256, SourceClassification.HostedAllowedSession),
            new JudgmentSourceRef(b.ObjectId, b.Sha256, SourceClassification.HostedAllowedSession),
        ]);

        var decision = policy.Authorize(
            request,
            HostedPurposes.ConversationScreen,
            HostedProviders.TypeSafe,
            "sess-1",
            null,
            120);

        Assert.Equal(2, decision.Sources.Count);
        Assert.All(decision.Sources, s => Assert.Equal(SourceClassification.HostedAllowedSession, s.Classification));
        Assert.Equal(decision.Grant.GrantId, decision.Audit.GrantId);
        Assert.Equal(2, decision.Audit.SourceHashes.Count);
        Assert.DoesNotContain("seg-a", string.Join(',', decision.Audit.SourceHashes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_with_disclosure_blocks_provider_when_unauthorized()
    {
        var objects = new ObjectStore(_tmp.Root, _clock);
        var store = new JudgmentStore(_tmp.Root, objects, _clock);
        var cache = new JudgmentCache(store);
        var grants = new HostedGrantStore(_tmp.Root, _clock);
        var policy = new DisclosurePolicy(grants, objects, () => _clock.UtcNow);
        var fake = new FakeJudgmentClient { ProviderName = HostedProviders.TypeSafe }
            .Script("conversation.screen.v1", JudgmentResponse.FromSuccess(new JudgmentSuccess
        {
            Model = "jev-1.13.0",
            Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
            {
                ["contains_correction"] = new NoulAnswer { ProbabilityYes = 0.9 },
            },
        }));
        var life = new JudgmentLifecycle(store, cache, fake, _clock, disclosure: policy, grants: grants);

        var local = objects.PutText("secret", classification: SourceClassification.LocalOnly);
        var request = BuildRequest([
            new JudgmentSourceRef(local.ObjectId, local.Sha256, SourceClassification.LocalOnly),
        ]);

        var result = await life.ExecuteAsync(
            request,
            CancellationToken.None,
            appendCaseEvent: false,
            purpose: HostedPurposes.ConversationScreen,
            sessionId: "sess-1");

        Assert.False(result.Response.Ok);
        Assert.Equal(JudgmentFailureCategories.NotAuthorized, result.Response.Failure!.Category);
        Assert.False(result.ProviderCalled);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task Lifecycle_with_grant_dispatches_and_records_tokens()
    {
        var objects = new ObjectStore(_tmp.Root, _clock);
        var store = new JudgmentStore(_tmp.Root, objects, _clock);
        var cache = new JudgmentCache(store);
        var grants = new HostedGrantStore(_tmp.Root, _clock);
        var policy = new DisclosurePolicy(grants, objects, () => _clock.UtcNow);
        var grant = grants.CreateSessionGrant(
            "sess-1",
            [HostedPurposes.ConversationScreen],
            [SourceClassification.HostedAllowedSession],
            10_000);
        var fake = new FakeJudgmentClient { ProviderName = HostedProviders.TypeSafe }
            .Script("conversation.screen.v1", JudgmentResponse.FromSuccess(new JudgmentSuccess
        {
            Model = "jev-1.13.0",
            InputTokens = 77,
            Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
            {
                ["contains_correction"] = new NoulAnswer { ProbabilityYes = 0.9 },
            },
        }));
        var life = new JudgmentLifecycle(store, cache, fake, _clock, disclosure: policy, grants: grants);
        var src = objects.PutText("hello", classification: SourceClassification.HostedAllowedSession);
        var request = BuildRequest([
            new JudgmentSourceRef(src.ObjectId, src.Sha256, SourceClassification.HostedAllowedSession),
        ]);

        var result = await life.ExecuteAsync(
            request,
            CancellationToken.None,
            appendCaseEvent: false,
            purpose: HostedPurposes.ConversationScreen,
            sessionId: "sess-1",
            conservativeInputTokenEstimate: 100);

        Assert.True(result.Response.Ok);
        Assert.True(result.ProviderCalled);
        Assert.Equal(1, fake.CallCount);
        Assert.Equal(77, grants.TryGet(grant.GrantId)!.TokensUsed);
    }

    [Fact]
    public void Surface_grant_and_revoke_commands()
    {
        _tmp.Root.EnsureLayout(_clock);
        using var diagnostics = Diag("disc-surface");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, new ScriptedCaseMind(), diagnostics);
        var grants = new HostedGrantStore(_tmp.Root, _clock);
        IRelaySurface surface = new CaseRuntimeSurface(runtime, _clock, grants: grants);

        var created = surface.GrantHostedSession(
            "sess-ui",
            [HostedPurposes.ConversationScreen, HostedPurposes.DirectRouting],
            8_000);
        Assert.True(created.Ok, created.Error);
        Assert.NotNull(created.GrantId);
        Assert.True(surface.Snapshot().HostedJudgments!.HasActiveGrant);

        var revoked = surface.RevokeHostedGrant(created.GrantId!);
        Assert.True(revoked.Ok, revoked.Error);
        Assert.False(surface.Snapshot().HostedJudgments!.HasActiveGrant);
    }

    private (DisclosurePolicy Policy, ObjectStore Objects, HostedGrantStore Grants) CreatePolicy()
    {
        var objects = new ObjectStore(_tmp.Root, _clock);
        var grants = new HostedGrantStore(_tmp.Root, _clock);
        var policy = new DisclosurePolicy(grants, objects, () => _clock.UtcNow);
        return (policy, objects, grants);
    }

    private static JudgmentRequest BuildRequest(IReadOnlyList<JudgmentSourceRef> sources) => new()
    {
        QuestionSetId = "conversation.screen.v1",
        QuestionSetVersion = "1",
        Model = "jev-1.13.0",
        State = JudgmentState.Parse("""{"segments":["x"]}"""),
        SourceObjectRefs = sources,
        Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
        {
            ["contains_correction"] = new NoulQuestion
            {
                Instructions = "Does the unread transcript contain a correction of a prior claim?",
            },
        },
    };

    private RuntimeDiagnostics Diag(string runId)
    {
        var path = System.IO.Path.Combine(_tmp.Path, "diag-" + runId + ".jsonl");
        return new RuntimeDiagnostics(path, runId);
    }
}
