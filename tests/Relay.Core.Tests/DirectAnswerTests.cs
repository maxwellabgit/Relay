using Relay.Core.Capabilities;
using Relay.Core.Cases;
using Relay.Core.Generation;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

public sealed class DirectAnswerTests
{
    [Fact]
    public async Task DirectAnswer_LocalEvidence_ProducesAnswerWithCitations()
    {
        var generator = new ScriptedTextGenerator()
            .EnqueueText("The Atlas beta ships on October 14.");
        var capability = new DirectAnswerCapability(
            generator,
            _ =>
            [
                new EvidenceHit
                {
                    SourceRef = "note:atlas-beta",
                    Excerpt = "The Atlas beta ships on October 14.",
                },
            ]);

        var result = await capability.HandleAsync(new CapabilityRequest
        {
            CapabilityId = DirectAnswerCapability.Id,
            CapabilityVersion = 1,
            CaseId = "c-answer",
            Origin = CaseOrigin.Direct,
            Objective = "When does the Atlas beta ship?",
            At = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.Equal("answered", result.Kind);
        Assert.Contains("October 14", result.FeedText, StringComparison.Ordinal);
        Assert.Contains("note:atlas-beta", result.FeedText, StringComparison.Ordinal);
        Assert.Equal(1, generator.CallCount);
        Assert.True(result.Done);
    }

    [Fact]
    public async Task DirectAnswer_CurrentWorld_ReturnsLimitation_NotHiddenWebResearch()
    {
        var generator = new ScriptedTextGenerator()
            .EnqueueText("should not be used");
        var capability = new DirectAnswerCapability(
            generator,
            _ => throw new InvalidOperationException("retrieval must not run for current-world questions"));

        var result = await capability.HandleAsync(new CapabilityRequest
        {
            CapabilityId = DirectAnswerCapability.Id,
            CapabilityVersion = 1,
            CaseId = "c-world",
            Origin = CaseOrigin.Direct,
            Objective = "What is the latest stock price for Atlas today?",
            At = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.Equal("limitation", result.Kind);
        Assert.Equal("current_world_limitation", result.Reason);
        Assert.Contains("local project evidence", result.FeedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", result.FeedText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, generator.CallCount);
    }

    [Fact]
    public async Task DirectAnswer_GeneratorUnavailable_ResumableWaitingState()
    {
        var generator = new ScriptedTextGenerator()
            .EnqueueUnavailable();
        var capability = new DirectAnswerCapability(
            generator,
            _ =>
            [
                new EvidenceHit { SourceRef = "note:x", Excerpt = "evidence" },
            ]);

        var result = await capability.HandleAsync(new CapabilityRequest
        {
            CapabilityId = DirectAnswerCapability.Id,
            CapabilityVersion = 1,
            CaseId = "c-wait",
            Origin = CaseOrigin.Direct,
            Objective = "Summarize the Atlas decision.",
            At = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.Equal("waiting", result.Kind);
        Assert.False(result.Done);
        Assert.Equal("generator_unavailable", result.Reason);
        Assert.Contains("Waiting", result.FeedText, StringComparison.Ordinal);
    }
}
