using System.Text.Json;
using Relay.Core.Capabilities;
using Relay.Core.Cases;
using Relay.Core.Decisions;
using Relay.Core.Judgments;
using Relay.Core.Memory;
using Relay.Core.Policy;
using Relay.Core.Privacy;
using Relay.Core.Storage;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>
/// Production-path invariants that must hold before alpha is complete.
/// These are expected to fail against the 8b65b2d scaffold until the finish branch lands.
/// </summary>
public sealed class ProductionCompositionTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 15, 0, 0, TimeSpan.Zero);
    private const string SessionId = "sess-prod-1";

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task Hosted_off_observed_input_causes_zero_provider_calls()
    {
        // Production composition: lifecycle + disclosure, no session grant ⇒ zero provider calls.
        var fake = ScriptScreen(term: 0.95);
        var (runtime, _) = OpenIntendedComposition(fake);
        using (runtime)
        {
            var listening = runtime.StartListening();
            runtime.IngestSegment("What does BESS mean in the Atlas plan?", _clock.UtcNow);
            await runtime.StepCaseAsync(listening.Id);

            Assert.Equal(0, fake.CallCount);
        }
    }

    [Fact]
    public async Task Valid_session_grant_permits_exactly_one_provider_call()
    {
        var fake = ScriptScreen(term: 0.95);
        var (runtime, grants) = OpenIntendedComposition(fake);
        using (runtime)
        {
            grants.CreateSessionGrant(
                SessionId,
                [HostedPurposes.ConversationScreen, HostedPurposes.DirectRouting, HostedPurposes.AcronymDisambiguation],
                [SourceClassification.HostedAllowedSession, SourceClassification.Public],
                10_000);

            var listening = runtime.StartListening();
            runtime.IngestSegment("What does BESS mean in the Atlas plan?", _clock.UtcNow);
            await runtime.StepCaseAsync(listening.Id);

            Assert.Equal(1, fake.CallCount);
            Assert.Equal(1, fake.CallsFor(QuestionSets.ConversationScreenId));
        }
    }

    [Fact]
    public async Task Direct_input_is_persisted_as_object_and_routed_through_lifecycle()
    {
        var fake = ScriptDirect("answer");
        var (runtime, grants) = OpenIntendedComposition(fake, registerDirect: true);
        using (runtime)
        {
            grants.CreateSessionGrant(
                SessionId,
                HostedPurposes.All,
                [SourceClassification.HostedAllowedSession, SourceClassification.Public],
                10_000);

            var started = runtime.StartDirectCase("When does the Atlas beta ship?");
            var events = runtime.Cases.LoadEvents(started.Id);
            var input = Assert.Single(events, e => e.Type == CaseEventTypes.UserInput);
            Assert.True(input.Payload.TryGetProperty("objectId", out var objectIdEl));
            Assert.True(input.Payload.TryGetProperty("sha256", out var shaEl));
            Assert.True(input.Payload.TryGetProperty("classification", out var classEl));
            Assert.True(input.Payload.TryGetProperty("characterCount", out var countEl));
            Assert.False(input.Payload.TryGetProperty("text", out _), "user.input must not store raw text");

            var objectId = objectIdEl.GetString();
            Assert.False(string.IsNullOrWhiteSpace(objectId));
            Assert.Equal(SourceClassification.HostedAllowedSession, classEl.GetString());
            Assert.True(countEl.GetInt32() > 0);
            Assert.False(string.IsNullOrWhiteSpace(shaEl.GetString()));
            Assert.NotNull(runtime.Objects.TryReadTextById(objectId!));
            Assert.Contains(started.SourceRefs, r => r.Contains(objectId!, StringComparison.Ordinal));

            await runtime.StepCaseAsync(started.Id);
            Assert.Equal(1, fake.CallCount);
            Assert.Equal(1, fake.CallsFor(QuestionSets.DirectRouteId));
        }
    }

    [Fact]
    public async Task Observed_input_preserves_exact_span_and_source_object_into_child()
    {
        var fake = ScriptScreen(term: 0.95);
        var (runtime, grants) = OpenIntendedComposition(fake, registerAcronym: true);
        using (runtime)
        {
            grants.CreateSessionGrant(
                SessionId,
                HostedPurposes.All,
                [SourceClassification.HostedAllowedSession, SourceClassification.Public],
                10_000);

            var listening = runtime.StartListening();
            var segment = runtime.IngestSegment("What does BESS mean in the Atlas plan?", _clock.UtcNow);
            var after = await runtime.StepCaseAsync(listening.Id);
            Assert.NotEmpty(after.ChildCaseIds);

            var child = runtime.GetCase(after.ChildCaseIds[0])!;
            Assert.Equal(CaseOrigin.Observed, child.Origin);
            Assert.Contains(segment.ObjectId, string.Join(',', child.SourceRefs));
            Assert.Contains("BESS", child.ApprovedObjective ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("Unresolved term noted", child.ApprovedObjective ?? "", StringComparison.Ordinal);

            var childEvents = runtime.Cases.LoadEvents(child.Id);
            var childInput = Assert.Single(childEvents, e => e.Type == CaseEventTypes.UserInput);
            Assert.True(
                childInput.Payload.TryGetProperty("span", out var spanEl) ||
                childInput.Payload.TryGetProperty("acronym", out spanEl));
            var spanText = spanEl.GetString() ?? "";
            Assert.Contains("BESS", spanText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Acronym_resolution_receives_BESS_not_generic_feed_copy()
    {
        var fake = ScriptScreen(term: 0.95);
        var (runtime, grants) = OpenIntendedComposition(fake, registerAcronym: true);
        using (runtime)
        {
            grants.CreateSessionGrant(
                SessionId,
                HostedPurposes.All,
                [SourceClassification.HostedAllowedSession, SourceClassification.Public],
                10_000);

            var listening = runtime.StartListening();
            runtime.IngestSegment("What does BESS mean in the Atlas plan?", _clock.UtcNow);
            var after = await runtime.StepCaseAsync(listening.Id);
            var childId = Assert.Single(after.ChildCaseIds);
            var child = runtime.GetCase(childId)!;

            Assert.Contains(AcronymResolveCapability.AtVersion, child.AllowedCapabilities);
            Assert.Equal("BESS", ExtractAcronymArgument(runtime, childId));
            Assert.DoesNotContain("Unresolved term noted", child.ApprovedObjective ?? "", StringComparison.Ordinal);

            await runtime.StepCaseAsync(childId);
            var feed = runtime.Projections.ListFeedItems().Select(f => f.Text).ToList();
            Assert.Contains(feed, t => t.Contains("BESS", StringComparison.Ordinal));
            Assert.DoesNotContain(feed, t => t.Equals("Unresolved term noted.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Task_capture_creates_an_approval_envelope()
    {
        var fake = ScriptScreen(commitment: 0.95, term: 0.1, includeUnresolvedTerm: false);
        var (runtime, grants) = OpenIntendedComposition(fake, registerTask: true);
        using (runtime)
        {
            grants.CreateSessionGrant(
                SessionId,
                HostedPurposes.All,
                [SourceClassification.HostedAllowedSession, SourceClassification.Public],
                10_000);

            var listening = runtime.StartListening();
            runtime.IngestSegment("Max will send the draft Friday for Atlas.", _clock.UtcNow);
            var afterScreen = await runtime.StepCaseAsync(listening.Id);
            Assert.NotEmpty(afterScreen.ChildCaseIds);

            var childId = afterScreen.ChildCaseIds
                .Select(id => runtime.GetCase(id)!)
                .First(c => c.AllowedCapabilities.Any(a =>
                    a.StartsWith(TaskCaptureCapability.Id, StringComparison.Ordinal))).Id;

            await runtime.StepCaseAsync(childId);
            var pending = runtime.GetPendingApproval(childId);
            Assert.NotNull(pending);
            Assert.Equal(OperationStatus.AwaitingApproval, pending!.Status);
            Assert.Equal("task.create", pending.Capability);
            Assert.True(pending.Arguments.TryGetValue("owner", out var ownerEl));
            Assert.Contains("Max", ownerEl.GetString() ?? "", StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Approving_an_envelope_executes_it_exactly_once()
    {
        _tmp.Root.EnsureLayout(_clock);
        var sideEffects = 0;
        using var diagnostics = Diag("prod-approve");
        using var runtime = CaseRuntime.Open(
            _tmp.Root, _clock, new ScriptedCaseMind(), diagnostics, () => sideEffects++);

        var started = runtime.StartDirectCase("approve-executes-once");
        var stepped = await runtime.StepNextAsync();
        Assert.NotNull(stepped);
        var pending = runtime.GetPendingApproval(started.Id);
        Assert.NotNull(pending);

        var approved = runtime.ApproveOperation(
            pending!.OperationId,
            pending.CanonicalHash(),
            stepped!.Version);
        Assert.Equal(OperationStatus.Approved, approved.Status);

        // Production contract: approval wakes the executor — no separate ExecuteOperation call.
        for (var i = 0; i < 8; i++)
        {
            var next = await runtime.StepNextAsync();
            if (next is null) break;
        }

        var op = runtime.GetOperation(pending.OperationId)!;
        Assert.Equal(OperationStatus.Completed, op.Status);
        Assert.Equal(1, op.SideEffectCount);
        Assert.Equal(1, sideEffects);

        var again = runtime.ExecuteOperation(pending.OperationId);
        Assert.Equal(OperationStatus.Completed, again.Status);
        Assert.Equal(1, sideEffects);
    }

    [Fact]
    public async Task Jev_timeout_leaves_original_work_retryable()
    {
        var fake = new FakeJudgmentClient
        {
            ProviderName = HostedProviders.TypeSafe,
            Behavior = FakeJudgmentBehavior.Timeout,
        };
        var (runtime, grants) = OpenIntendedComposition(fake);
        using (runtime)
        {
            grants.CreateSessionGrant(
                SessionId,
                HostedPurposes.All,
                [SourceClassification.HostedAllowedSession, SourceClassification.Public],
                10_000);

            var listening = runtime.StartListening();
            runtime.IngestSegment("What does BESS mean in the Atlas plan?", _clock.UtcNow);
            var after = await runtime.StepCaseAsync(listening.Id);

            Assert.Empty(after.ChildCaseIds);
            Assert.NotEmpty(runtime.Intake.LoadState().PendingMindSegmentIds);

            fake.Behavior = FakeJudgmentBehavior.Success;
            fake.Script(QuestionSets.ConversationScreenId, JudgmentResponse.FromSuccess(ScreenAnswers(0.1, 0.1, 0.1, 0.95, "persistent", 0.85)));

            // No new ingest — original segment must still be runnable.
            var recovered = await runtime.StepCaseAsync(listening.Id);
            Assert.True(fake.CallCount >= 1);
            Assert.NotEmpty(recovered.ChildCaseIds);
        }
    }

    [Fact]
    public async Task Restart_retries_without_new_input()
    {
        string listeningId;
        var fake = new FakeJudgmentClient
        {
            ProviderName = HostedProviders.TypeSafe,
            Behavior = FakeJudgmentBehavior.Timeout,
        };

        {
            var (runtime, grants) = OpenIntendedComposition(fake);
            using (runtime)
            {
                grants.CreateSessionGrant(
                    SessionId,
                    HostedPurposes.All,
                    [SourceClassification.HostedAllowedSession, SourceClassification.Public],
                    10_000);
                var listening = runtime.StartListening();
                listeningId = listening.Id;
                runtime.IngestSegment("What does BESS mean in the Atlas plan?", _clock.UtcNow);
                await runtime.StepCaseAsync(listeningId);
            }
        }

        fake.Behavior = FakeJudgmentBehavior.Success;
        fake.Script(QuestionSets.ConversationScreenId, JudgmentResponse.FromSuccess(ScreenAnswers(0.1, 0.1, 0.1, 0.95, "persistent", 0.85)));
        _clock.Advance(TimeSpan.FromSeconds(30));

        {
            var (runtime, _) = OpenIntendedComposition(fake, reuseRoot: true);
            using (runtime)
            {
                // Fresh process on same data root — no new IngestSegment.
                var recovered = await runtime.StepCaseAsync(listeningId);
                Assert.NotEmpty(recovered.ChildCaseIds);
            }
        }
    }

    [Fact]
    public async Task Step_budget_stops_a_loop()
    {
        _tmp.Root.EnsureLayout(_clock);
        var mind = new LoopingDecisionMind();
        using var diagnostics = Diag("prod-budget");
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics);

        var started = runtime.StartDirectCase("budget-loop");
        var record = runtime.GetCase(started.Id)!;
        record.Budgets.MaxSteps = 3;
        runtime.Cases.SaveRecord(record);

        CaseRecord? last = null;
        for (var i = 0; i < 16; i++)
        {
            last = await runtime.StepCaseAsync(started.Id);
            if (last.Status is CaseStatus.Waiting or CaseStatus.Completed or CaseStatus.Cancelled)
                break;
            if (string.Equals(last.Status, "failed", StringComparison.Ordinal))
                break;
        }

        Assert.NotNull(last);
        Assert.True(last!.Budgets.StepsUsed <= last.Budgets.MaxSteps);
        Assert.True(
            last.Status is CaseStatus.Waiting or CaseStatus.Cancelled ||
            string.Equals(last.Status, "failed", StringComparison.Ordinal),
            $"expected budget stop, got {last.Status} after {last.Budgets.StepsUsed} steps");
        Assert.Equal(3, last.Budgets.MaxSteps);
        Assert.Equal(3, last.Budgets.StepsUsed);
    }

    [Fact]
    public void Project_grants_cannot_authorize_session_classified_data()
    {
        _tmp.Root.EnsureLayout(_clock);
        var objects = new ObjectStore(_tmp.Root, _clock);
        var grants = new HostedGrantStore(_tmp.Root, _clock);
        var policy = new DisclosurePolicy(grants, objects, () => _clock.UtcNow);

        // Creating a project grant that lists session classification must be rejected.
        var createdWithSession = false;
        try
        {
            grants.CreateProjectGrant(
                "proj-1",
                [HostedPurposes.AcronymDisambiguation],
                [SourceClassification.HostedAllowedProject, SourceClassification.HostedAllowedSession, SourceClassification.Public],
                5_000);
            createdWithSession = true;
        }
        catch (Exception ex)
        {
            Assert.Contains("session", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.False(createdWithSession, "project grants must not accept hosted_allowed_session");

        var projectOnly = grants.CreateProjectGrant(
            "proj-1",
            [HostedPurposes.AcronymDisambiguation],
            [SourceClassification.HostedAllowedProject, SourceClassification.Public],
            5_000);
        Assert.DoesNotContain(SourceClassification.HostedAllowedSession, projectOnly.AllowedSourceClassifications);

        var sessionObj = objects.PutText("session text", classification: SourceClassification.HostedAllowedSession);
        var request = new JudgmentRequest
        {
            QuestionSetId = QuestionSets.AcronymSelectId,
            QuestionSetVersion = QuestionSets.AcronymSelectVersion,
            Model = "jev-1.13.0",
            State = JudgmentState.Parse("""{"acronym":"BESS"}"""),
            SourceObjectRefs =
            [
                new JudgmentSourceRef(sessionObj.ObjectId, sessionObj.Sha256, SourceClassification.HostedAllowedSession),
            ],
            Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
            {
                ["best_match"] = new ChoiceQuestion
                {
                    Instructions = "pick",
                    Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["a"] = "option a",
                        [ChoiceQuestion.NoMatch] = "none",
                    },
                },
            },
        };

        var authEx = Assert.Throws<DisclosureException>(() =>
            policy.Authorize(
                request,
                HostedPurposes.AcronymDisambiguation,
                HostedProviders.TypeSafe,
                sessionId: null,
                projectId: "proj-1",
                conservativeInputTokenEstimate: 40));
        Assert.Equal("not_authorized", authEx.Category);
    }

    private (CaseRuntime Runtime, HostedGrantStore Grants) OpenIntendedComposition(
        FakeJudgmentClient fake,
        bool registerAcronym = false,
        bool registerTask = false,
        bool registerDirect = false,
        bool reuseRoot = false)
    {
        if (!reuseRoot)
            _tmp.Root.EnsureLayout(_clock);
        else
            _tmp.Root.EnsureLayout(_clock);

        fake.ProviderName = HostedProviders.TypeSafe;
        var objects = new ObjectStore(_tmp.Root, _clock);
        var judgmentStore = new JudgmentStore(_tmp.Root, objects, _clock);
        var grants = new HostedGrantStore(_tmp.Root, _clock);
        var disclosure = new DisclosurePolicy(grants, objects, () => _clock.UtcNow);
        var lifecycle = new JudgmentLifecycle(
            judgmentStore,
            new JudgmentCache(judgmentStore),
            fake,
            _clock,
            cases: null,
            disclosure: disclosure,
            grants: grants);
        var engine = new RelayDecisionEngine(lifecycle: lifecycle, model: "jev-1.13.0");
        var mind = new SessionAwareDecisionAdapter(engine, SessionId);

        var registry = new CapabilityRegistry();
        if (registerAcronym)
        {
            registry.Register(AcronymResolveCapability.Definition,
                new AcronymResolveCapability(new GlossaryStore(_tmp.Root), _ => null, lifecycle: lifecycle));
        }

        if (registerTask)
            registry.Register(TaskCaptureCapability.Definition, new TaskCaptureCapability());

        if (registerDirect)
            registry.Register(DirectAnswerCapability.Definition, new DirectAnswerCapability());

        var diagnostics = Diag("prod-" + UlidLike());
        var runtime = CaseRuntime.Open(
            _tmp.Root, _clock, mind, diagnostics, capabilities: registry.Enabled.Count > 0 ? registry : null);
        return (runtime, grants);
    }

    /// <summary>Mirrors CaseRelayHost: engine gets the raw client, not the lifecycle.</summary>
    private CaseRuntime OpenBrokenHostComposition(FakeJudgmentClient fake)
    {
        _tmp.Root.EnsureLayout(_clock);
        fake.ProviderName = HostedProviders.TypeSafe;
        var engine = new RelayDecisionEngine(client: fake, model: "jev-1.13.0");
        var mind = new CaseMindDecisionAdapter(engine);
        var diagnostics = Diag("prod-broken");
        return CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics);
    }

    private FakeJudgmentClient ScriptScreen(
        double decision = 0.1,
        double commitment = 0.1,
        double correction = 0.1,
        double term = 0.1,
        bool includeUnresolvedTerm = true)
    {
        return new FakeJudgmentClient { ProviderName = HostedProviders.TypeSafe }
            .Script(QuestionSets.ConversationScreenId, JudgmentResponse.FromSuccess(
                ScreenAnswers(decision, commitment, correction, term, "persistent", 0.85, includeUnresolvedTerm)));
    }

    private FakeJudgmentClient ScriptDirect(string route) =>
        new FakeJudgmentClient { ProviderName = HostedProviders.TypeSafe }
            .Script(QuestionSets.DirectRouteId, JudgmentResponse.FromSuccess(DirectRoute(route, 0.9)));

    private RuntimeDiagnostics Diag(string runId)
        => new(Path.Combine(_tmp.Root.DevRunsDirectory, runId, "runtime.jsonl"), runId);

    private static string UlidLike() => Guid.NewGuid().ToString("N")[..12];

    private static string ExtractAcronymArgument(CaseRuntime runtime, string caseId)
    {
        var record = runtime.GetCase(caseId)!;
        if (string.Equals(record.ApprovedObjective, "BESS", StringComparison.Ordinal))
            return "BESS";

        foreach (var evt in runtime.Cases.LoadEvents(caseId))
        {
            if (evt.Type != CaseEventTypes.UserInput) continue;
            if (evt.Payload.TryGetProperty("acronym", out var a) && a.ValueKind == JsonValueKind.String)
                return a.GetString() ?? "";
            if (evt.Payload.TryGetProperty("span", out var s) && s.ValueKind == JsonValueKind.String)
            {
                var span = s.GetString() ?? "";
                var match = System.Text.RegularExpressions.Regex.Match(span, @"\b[A-Z]{2,6}\b");
                if (match.Success) return match.Value;
            }
        }

        return record.ApprovedObjective;
    }

    private static JudgmentSuccess ScreenAnswers(
        double decision,
        double commitment,
        double correction,
        double term,
        string attention,
        double attentionConfidence,
        bool includeUnresolvedTerm = true)
    {
        var answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
        {
            ["contains_durable_decision"] = new NoulAnswer { ProbabilityYes = decision },
            ["contains_actionable_commitment"] = new NoulAnswer { ProbabilityYes = commitment },
            ["contains_correction"] = new NoulAnswer { ProbabilityYes = correction },
            ["attention"] = new ChoiceAnswer
            {
                Choice = attention,
                Confidence = attentionConfidence,
                Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["ambient"] = attention == "ambient" ? 0.8 : 0.1,
                    ["persistent"] = attention == "persistent" ? 0.8 : 0.1,
                    ["alert"] = attention == "alert" ? 0.8 : 0.1,
                },
            },
        };
        if (includeUnresolvedTerm)
            answers["contains_unresolved_term_request"] = new NoulAnswer { ProbabilityYes = term };
        return new() { Model = "jev-1.13.0", Answers = answers };
    }

    private static JudgmentSuccess DirectRoute(string choice, double confidence) => new()
    {
        Model = "jev-1.13.0",
        Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
        {
            ["route"] = new ChoiceAnswer
            {
                Choice = choice,
                Confidence = confidence,
                Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["answer"] = choice == "answer" ? confidence : 0.05,
                    ["remember"] = choice == "remember" ? confidence : 0.05,
                    ["organize"] = choice == "organize" ? confidence : 0.05,
                    ["clarify"] = choice == "clarify" ? confidence : 0.05,
                    ["no_match"] = 0.05,
                },
            },
        },
    };

    /// <summary>Passes session id into the decision engine (production must do this).</summary>
    private sealed class SessionAwareDecisionAdapter : ICaseMind
    {
        private readonly ICaseDecisionEngine _engine;
        private readonly string _sessionId;

        public SessionAwareDecisionAdapter(ICaseDecisionEngine engine, string sessionId)
        {
            _engine = engine;
            _sessionId = sessionId;
        }

        public string Name => "session-aware-decision-engine";

        public async Task<CaseMindStep> StepAsync(CaseMindRequest request, CancellationToken cancellationToken)
        {
            var decisionRequest = new CaseDecisionRequest
            {
                CaseId = request.CaseId,
                Origin = request.Origin,
                Kind = request.Kind,
                Objective = request.Objective,
                Version = request.Version,
                Status = request.Status,
                RecentEvents = request.RecentEvents,
                RecentSegments = request.RecentSegments,
                PendingOperations = request.PendingOperations,
                AvailableCapabilities = request.AvailableTools ?? [],
                ParentCaseId = request.ParentCaseId,
                PresentationPolicy = request.PresentationPolicy,
                At = request.At,
                StepIndex = request.StepIndex,
                SessionId = _sessionId,
            };

            var decision = await _engine.DecideAsync(decisionRequest, cancellationToken).ConfigureAwait(false);
            return CaseMindDecisionAdapter.ToMindStep(decision);
        }
    }

    /// <summary>Never completes — used to prove MaxSteps enforcement.</summary>
    private sealed class LoopingDecisionMind : ICaseMind
    {
        public string Name => "looping";

        public Task<CaseMindStep> StepAsync(CaseMindRequest request, CancellationToken cancellationToken)
        {
            var step = new CaseMindStep(
                new CaseMindRead("loop", 0.5, 0.5, 0.2, new CaseMindConfidence(0.5, 0.5, 0.5), []),
                new CaseMove { Type = CaseMove.Say, Text = "still working", Done = false },
                "still working");
            return Task.FromResult(step);
        }
    }
}
