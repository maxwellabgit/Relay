using System.Text.Json;
using Relay.Core.Capabilities;
using Relay.Core.Cases;
using Relay.Core.Decisions;
using Relay.Core.Generation;
using Relay.Core.Judgments;
using Relay.Core.Policy;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

public sealed class NoteCaptureTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 15, 0, 0, TimeSpan.Zero);

    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    [Fact]
    public async Task NoteCapture_AtlasCorrection_ProposesModifyNote_WithPriorAndTranscriptSourceRefs()
    {
        _tmp.Root.EnsureLayout(_clock);
        var local = new CaseLocalContext(_tmp.Root, _clock);
        var (project, note) = local.SeedAtlasBetaDecision();
        var transcriptRef = "segment:corr-1";

        var capability = new NoteCaptureCapability();
        var result = await capability.HandleAsync(new CapabilityRequest
        {
            CapabilityId = NoteCaptureCapability.Id,
            CapabilityVersion = 1,
            CaseId = "note-corr",
            Origin = CaseOrigin.Observed,
            ProjectId = project.Id,
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = "correction",
                ["projectId"] = project.Id,
                ["noteId"] = note.Id,
                ["body"] = "The Atlas beta ships on October 21.",
                ["priorSourceRef"] = CaseLocalContext.AtlasSourceEventId,
                ["transcriptSourceRef"] = transcriptRef,
            },
            SourceRefs = [transcriptRef],
            At = _clock.UtcNow,
        }, CancellationToken.None);

        Assert.Equal("propose_modify", result.Kind);
        Assert.Equal(Actions.ModifyNote, result.Artifacts["capability"]);
        Assert.Equal(note.Id, result.Artifacts["noteId"]);
        Assert.Equal(project.Id, result.Artifacts["projectId"]);
        Assert.Contains(CaseLocalContext.AtlasSourceEventId, result.SourceRefs);
        Assert.Contains(transcriptRef, result.SourceRefs);
        Assert.Contains("October 21", result.Artifacts["body"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoteCapture_NoteSupportFail_RetainsVerbatimDraft_DoesNotAcceptGeneratedSentence()
    {
        var verbatim = "The Atlas beta ships on October 14.";
        var generated = "The Atlas beta ships on October 14 and will delight every customer worldwide.";
        var fake = new FakeJudgmentClient().Script(
            QuestionSets.NoteSupportId,
            JudgmentResponse.FromSuccess(new JudgmentSuccess
            {
                Model = "jev-1.13.0",
                Answers = new Dictionary<string, JudgmentAnswer>(StringComparer.Ordinal)
                {
                    ["supported"] = new NoulAnswer { ProbabilityYes = 0.4 },
                    ["unsupported_claim"] = new NoulAnswer { ProbabilityYes = 0.85 },
                },
            }));
        var generator = new ScriptedTextGenerator().EnqueueText(generated);
        var capability = new NoteCaptureCapability(generator: generator, client: fake);

        var result = await capability.HandleAsync(new CapabilityRequest
        {
            CapabilityId = NoteCaptureCapability.Id,
            CapabilityVersion = 1,
            CaseId = "note-fail",
            Origin = CaseOrigin.Direct,
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = "new",
                ["verbatim"] = verbatim,
            },
            At = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.Equal("draft_verbatim", result.Kind);
        Assert.Equal("note_support_fail", result.Reason);
        Assert.Equal(verbatim, result.Artifacts["body"]);
        Assert.DoesNotContain("delight", result.Artifacts["body"], StringComparison.Ordinal);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task NoteCapture_EditApprovalArguments_InvalidatesPriorEnvelopeHash()
    {
        _tmp.Root.EnsureLayout(_clock);
        var local = new CaseLocalContext(_tmp.Root, _clock);
        var (project, note) = local.SeedAtlasBetaDecision();
        using var diagnostics = new RuntimeDiagnostics(
            Path.Combine(_tmp.Root.DevRunsDirectory, "note-edit", "runtime.jsonl"), "note-edit");
        var mind = new ScriptedProposeMind(Actions.ModifyNote, new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["capability"] = JsonSerializer.SerializeToElement(Actions.ModifyNote),
            ["idempotencyKey"] = JsonSerializer.SerializeToElement("note-edit-1"),
            ["projectId"] = JsonSerializer.SerializeToElement(project.Id),
            ["noteId"] = JsonSerializer.SerializeToElement(note.Id),
            ["body"] = JsonSerializer.SerializeToElement("The Atlas beta ships on October 21."),
        });
        using var runtime = CaseRuntime.Open(_tmp.Root, _clock, mind, diagnostics, local: local);

        var direct = runtime.StartDirectCase("Correct the Atlas date");
        await runtime.StepCaseAsync(direct.Id);
        var pending = runtime.GetPendingApproval(direct.Id);
        Assert.NotNull(pending);
        var oldHash = pending!.CanonicalHashValue ?? pending.CanonicalHash();

        var edited = runtime.EditOperation(pending.OperationId, new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["capability"] = JsonSerializer.SerializeToElement(Actions.ModifyNote),
            ["projectId"] = JsonSerializer.SerializeToElement(project.Id),
            ["noteId"] = JsonSerializer.SerializeToElement(note.Id),
            ["body"] = JsonSerializer.SerializeToElement("The Atlas beta ships on October 28."),
        });

        Assert.NotEqual(oldHash, edited.CanonicalHashValue);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            runtime.ApproveOperation(edited.OperationId, oldHash, runtime.GetCase(direct.Id)!.Version));
        Assert.Contains("hash", ex.Message, StringComparison.OrdinalIgnoreCase);

        runtime.ApproveOperation(edited.OperationId, edited.CanonicalHashValue!, runtime.GetCase(direct.Id)!.Version);
        Assert.Equal(OperationStatus.Approved, runtime.GetOperation(edited.OperationId)!.Status);
    }

    public void Dispose() => _tmp.Dispose();
}
