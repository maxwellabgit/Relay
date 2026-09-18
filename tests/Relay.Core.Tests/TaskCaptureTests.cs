using Relay.Core.Capabilities;
using Relay.Core.Cases;

namespace Relay.Core.Tests;

public sealed class TaskCaptureTests
{
    private static readonly DateTimeOffset FridayAsOf = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero); // Thursday → next Friday = 2026-09-18

    [Fact]
    public async Task TaskCapture_ExplicitOwnerAndDate_ProposesTaskWithCandidates()
    {
        var capability = new TaskCaptureCapability();
        var result = await capability.HandleAsync(new CapabilityRequest
        {
            CapabilityId = TaskCaptureCapability.Id,
            CapabilityVersion = 1,
            CaseId = "task-1",
            Origin = CaseOrigin.Observed,
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["span"] = "Max will send the draft Friday",
            },
            At = FridayAsOf,
        }, CancellationToken.None);

        Assert.Equal(CapabilityResultKinds.ProposeOperation, result.Kind);
        Assert.Equal("Max", result.Artifacts["owner"]);
        Assert.Equal("2026-09-18", result.Artifacts["dueDate"]); // next Friday from Thursday Sep 17
        Assert.Equal(TaskCaptureCapability.CreateTaskCapability, result.Artifacts["capability"]);
        Assert.Contains("Max", result.FeedText, StringComparison.Ordinal);
        Assert.StartsWith("task.create:", result.Artifacts["idempotencyKey"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskCapture_SomeoneShould_NoInferredOwner_OwnerlessOrClarify()
    {
        var capability = new TaskCaptureCapability();
        var result = await capability.HandleAsync(new CapabilityRequest
        {
            CapabilityId = TaskCaptureCapability.Id,
            CapabilityVersion = 1,
            CaseId = "task-2",
            Origin = CaseOrigin.Observed,
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["span"] = "Someone should look at this",
            },
            At = FridayAsOf,
        }, CancellationToken.None);

        Assert.Equal(CapabilityResultKinds.Clarification, result.Kind);
        Assert.Equal("no_inferred_owner", result.Reason);
        Assert.Equal("", result.Artifacts["owner"]);
        Assert.DoesNotContain("will", result.Artifacts["owner"], StringComparison.Ordinal);
    }

    [Fact]
    public void TaskCapture_DateComparison_IsPerformedInCode()
    {
        var asOf = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero); // Thursday
        var friday = TaskDateParsing.TryParseDueDate("due Friday", asOf);
        var monday = TaskDateParsing.TryParseDueDate("due Monday", asOf);
        Assert.NotNull(friday);
        Assert.NotNull(monday);
        // From Thursday: next Friday is sooner than next Monday.
        Assert.True(TaskDateParsing.Compare(friday!.Value, monday!.Value) < 0);
        Assert.Equal(0, TaskDateParsing.Compare(friday.Value, friday.Value));
        Assert.True(TaskDateParsing.Compare(monday.Value, friday.Value) > 0);
    }
}
