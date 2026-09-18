using System.Text.Json;
using Relay.Core.Cases;
using Relay.Core.Judgments;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

public sealed class JudgmentLifecycleTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 18, 0, 0, TimeSpan.Zero);
    private const string Secret = "Atlas beta moves to October 21; Max will email the draft Friday";

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task Crash_after_request_before_response_leaves_one_resumable_request()
    {
        var objects = new ObjectStore(_tmp.Root, _clock);
        var store = new JudgmentStore(_tmp.Root, objects, _clock);
        var request = BuildRequest(Secret);

        var handle = store.BeginRequest(request, provider: "fake");
        Assert.False(handle.AlreadyComplete);
        Assert.Equal(JudgmentStatuses.Requested, handle.Record.Status);

        // Simulate process death before provider returns.
        var unresolved = store.ListUnresolved();
        Assert.Single(unresolved);
        Assert.Equal(handle.Record.JudgmentId, unresolved[0].JudgmentId);

        // Resume: still one unresolved; completing later must not create a second request for the same hash
        // until this one finishes. A new BeginRequest while unresolved creates a second requested record
        // only if hash index is not completed — verify unfinished hash is not treated as cache hit.
        var again = store.BeginRequest(request, provider: "fake");
        Assert.False(again.AlreadyComplete);
        Assert.Equal(2, store.ListUnresolved().Count);

        // Finish the first; cache then serves the completed body.
        var success = SampleSuccess();
        store.CompleteSuccess(handle.Record.JudgmentId, success);
        var cached = store.BeginRequest(request, provider: "fake");
        Assert.True(cached.AlreadyComplete);
        Assert.Equal(handle.Record.JudgmentId, cached.Record.JudgmentId);
    }

    [Fact]
    public async Task Crash_after_response_before_decision_does_not_call_provider_again()
    {
        var objects = new ObjectStore(_tmp.Root, _clock);
        var store = new JudgmentStore(_tmp.Root, objects, _clock);
        var cache = new JudgmentCache(store);
        var fake = new FakeJudgmentClient().Script("conversation.screen.v1", JudgmentResponse.FromSuccess(SampleSuccess()));
        var life = new JudgmentLifecycle(store, cache, fake, _clock);

        var first = await life.ExecuteAsync(BuildRequest(Secret), CancellationToken.None, appendCaseEvent: false);
        Assert.True(first.ProviderCalled);
        Assert.Equal(1, fake.CallCount);

        // Restart facade: resume completed record without provider call.
        var resumed = life.ResumeCompleted(first.Record.JudgmentId);
        Assert.False(resumed.ProviderCalled);
        Assert.Equal(1, fake.CallCount);
        Assert.True(resumed.Response.Ok);

        // Same request hash: BeginRequest returns cache hit; ExecuteAsync must not call provider.
        var second = await life.ExecuteAsync(BuildRequest(Secret), CancellationToken.None, appendCaseEvent: false);
        Assert.False(second.ProviderCalled);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Duplicate_completion_keeps_one_decision_payload()
    {
        var objects = new ObjectStore(_tmp.Root, _clock);
        var store = new JudgmentStore(_tmp.Root, objects, _clock);
        var handle = store.BeginRequest(BuildRequest(Secret), "fake");
        var first = store.CompleteSuccess(handle.Record.JudgmentId, SampleSuccess());
        var second = store.CompleteSuccess(handle.Record.JudgmentId, new JudgmentSuccess
        {
            Model = "jev-1.13.0",
            InputTokens = 999,
            OutputTokens = 0,
            ElapsedMs = 18,
            Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
            {
                ["contains_correction"] = new NoulAnswer { ProbabilityYes = 0.93 },
            },
        });

        Assert.Equal(first.ResponseObjectId, second.ResponseObjectId);
        Assert.Equal(first.InputTokens, second.InputTokens);
        Assert.Equal(1, store.ListAll().Count(r => r.Status == JudgmentStatuses.Completed));
        await Task.CompletedTask;
    }

    [Fact]
    public void Projection_delete_and_rebuild_recreates_judgment_metadata()
    {
        var objects = new ObjectStore(_tmp.Root, _clock);
        var store = new JudgmentStore(_tmp.Root, objects, _clock);
        var cases = new CaseStore(_tmp.Root);
        var operations = new OperationStore(_tmp.Root);

        var handle = store.BeginRequest(BuildRequest(Secret), "fake");
        var completed = store.CompleteSuccess(handle.Record.JudgmentId, SampleSuccess());

        using var db = ProjectionDatabase.Open(_tmp.Root);
        db.UpsertJudgment(completed);
        Assert.Single(db.ListJudgments());

        // Clear projection rows and rebuild from the judgment store.
        db.RebuildFromStores(cases, operations, store);
        var rows = db.ListJudgments();
        Assert.Single(rows);
        Assert.Equal(completed.JudgmentId, rows[0].JudgmentId);
        Assert.Equal(JudgmentStatuses.Completed, rows[0].Status);
        Assert.Equal(completed.RequestHash, rows[0].RequestHash);
        Assert.Null(rows[0].FailureCategory);
    }

    [Fact]
    public async Task Audit_payload_and_case_event_omit_raw_transcript()
    {
        var objects = new ObjectStore(_tmp.Root, _clock);
        var store = new JudgmentStore(_tmp.Root, objects, _clock);
        var cases = new CaseStore(_tmp.Root);
        var cache = new JudgmentCache(store);
        var fake = new FakeJudgmentClient().Script("conversation.screen.v1", JudgmentResponse.FromSuccess(SampleSuccess()));

        var caseId = "case_judgment_audit";
        cases.SaveRecord(new CaseRecord
        {
            Id = caseId,
            Version = 1,
            Origin = CaseOrigin.Observed,
            Kind = CaseKind.Check,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        });

        var life = new JudgmentLifecycle(store, cache, fake, _clock, cases);
        var request = BuildRequest(Secret);
        request = new JudgmentRequest
        {
            QuestionSetId = request.QuestionSetId,
            QuestionSetVersion = request.QuestionSetVersion,
            Model = request.Model,
            State = request.State,
            Questions = request.Questions,
            SourceObjectRefs = request.SourceObjectRefs,
            CaseId = caseId,
            CaseVersion = 1,
            DisclosureGrantId = request.DisclosureGrantId,
            RequestHash = request.RequestHash,
        };
        var result = await life.ExecuteAsync(request, CancellationToken.None);

        var auditJson = JsonSerializer.Serialize(JudgmentStore.ToAuditPayload(result.Record));
        Assert.DoesNotContain(Secret, auditJson, StringComparison.Ordinal);

        var events = cases.LoadEvents(caseId);
        Assert.Contains(events, e => e.Type == CaseEventTypes.JudgmentRequested);
        Assert.Contains(events, e => e.Type == CaseEventTypes.JudgmentCompleted);
        foreach (var evt in events)
        {
            var raw = evt.Payload.GetRawText();
            Assert.DoesNotContain(Secret, raw, StringComparison.Ordinal);
        }

        // Object store may hold the request body; SQLite projection must not.
        using var db = ProjectionDatabase.Open(_tmp.Root);
        db.UpsertJudgment(result.Record);
        foreach (var row in db.ListJudgments())
        {
            var rowJson = JsonSerializer.Serialize(row);
            Assert.DoesNotContain(Secret, rowJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Failed_judgment_is_not_cached_as_success()
    {
        var objects = new ObjectStore(_tmp.Root, _clock);
        var store = new JudgmentStore(_tmp.Root, objects, _clock);
        var handle = store.BeginRequest(BuildRequest(Secret), "fake");
        store.CompleteFailure(handle.Record.JudgmentId, JudgmentFailure.Create(
            JudgmentFailureCategories.Timeout, "provider timed out", retryable: true));

        var again = store.BeginRequest(BuildRequest(Secret), "fake");
        Assert.False(again.AlreadyComplete);
    }

    private static JudgmentRequest BuildRequest(string segmentText) => new()
    {
        QuestionSetId = "conversation.screen.v1",
        QuestionSetVersion = "1",
        Model = "jev-1.13.0",
        State = JudgmentState.FromObject(new { segments = new[] { segmentText } }),
        CaseId = "case_test",
        CaseVersion = 1,
        Questions = new Dictionary<string, JudgmentQuestion>(StringComparer.Ordinal)
        {
            ["contains_correction"] = new NoulQuestion
            {
                Instructions = "Does the unread transcript contain a correction of a prior claim?",
            },
        },
    };

    private static JudgmentSuccess SampleSuccess() => new()
    {
        Model = "jev-1.13.0",
        InputTokens = 55,
        OutputTokens = 0,
        ElapsedMs = 18,
        Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
        {
            ["contains_correction"] = new NoulAnswer { ProbabilityYes = 0.93 },
        },
    };
}
