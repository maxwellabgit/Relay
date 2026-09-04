using Relay.Core.State;

namespace Relay.Tests;

public class TransitionTableTests
{
    public static IEnumerable<object[]> AllPairs()
    {
        foreach (var state in Enum.GetValues<RelayState>())
            foreach (var trigger in Enum.GetValues<Trigger>())
                yield return new object[] { state, trigger };
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void EveryPairResolvesWithoutThrowing(RelayState state, Trigger trigger)
    {
        var t = TransitionTable.Next(state, trigger);
        Assert.Equal(state, t.From);
        if (!t.Accepted)
        {
            Assert.Equal(state, t.To);
            Assert.False(string.IsNullOrWhiteSpace(t.Message));
        }
    }

    [Theory]
    [InlineData(RelayState.NoteCapture, Trigger.CommandKey)]
    [InlineData(RelayState.CommandCapture, Trigger.NoteKey)]
    public void TheOtherKeyDuringCaptureNeitherSwitchesNorSubmits(RelayState state, Trigger otherKey)
    {
        var t = TransitionTable.Next(state, otherKey);
        Assert.False(t.Accepted);
        Assert.Equal(state, t.To);
        Assert.Equal(TransitionTable.FinishOrCancelFirst, t.Message);
    }

    [Fact]
    public void OnePressStartsAndTheSamePressStops()
    {
        Assert.Equal(RelayState.NoteCapture, TransitionTable.Next(RelayState.Idle, Trigger.NoteKey).To);
        Assert.Equal(RelayState.AwaitingTranscript, TransitionTable.Next(RelayState.NoteCapture, Trigger.NoteKey).To);
        Assert.Equal(RelayState.CommandCapture, TransitionTable.Next(RelayState.Idle, Trigger.CommandKey).To);
        Assert.Equal(RelayState.AwaitingTranscript, TransitionTable.Next(RelayState.CommandCapture, Trigger.CommandKey).To);
    }

    [Theory]
    [InlineData(RelayState.NoteCapture)]
    [InlineData(RelayState.CommandCapture)]
    [InlineData(RelayState.AwaitingTranscript)]
    public void CancelReturnsToIdleFromEveryCaptureState(RelayState state)
    {
        var t = TransitionTable.Next(state, Trigger.Cancel);
        Assert.True(t.Accepted);
        Assert.Equal(RelayState.Idle, t.To);
    }

    [Fact]
    public void OrganizingCannotBeCancelled()
    {
        Assert.False(TransitionTable.Next(RelayState.Organizing, Trigger.Cancel).Accepted);
    }

    [Fact]
    public void LockWinsFromEveryStateExceptLocked()
    {
        foreach (var state in Enum.GetValues<RelayState>())
        {
            var t = TransitionTable.Next(state, Trigger.Lock);
            if (state == RelayState.Locked) Assert.False(t.Accepted);
            else Assert.Equal(RelayState.Locked, t.To);
        }
    }

    [Fact]
    public void LockedOnlyLeavesThroughExplicitUnlock()
    {
        foreach (var trigger in Enum.GetValues<Trigger>())
        {
            var t = TransitionTable.Next(RelayState.Locked, trigger);
            if (trigger == Trigger.Unlock) Assert.Equal(RelayState.Idle, t.To);
            else Assert.False(t.Accepted);
        }
    }

    [Fact]
    public void TurnStatesAreReachedOnlyThroughTheirDefinedPaths()
    {
        Assert.Equal(RelayState.Planning, TransitionTable.Next(RelayState.Organizing, Trigger.BeginPlanning).To);
        Assert.Equal(RelayState.AwaitingApproval, TransitionTable.Next(RelayState.Planning, Trigger.ApprovalRequired).To);
        Assert.Equal(RelayState.Executing, TransitionTable.Next(RelayState.Planning, Trigger.BeginExecution).To);
        Assert.Equal(RelayState.Executing, TransitionTable.Next(RelayState.AwaitingApproval, Trigger.BeginExecution).To);
        Assert.Equal(RelayState.Executing, TransitionTable.Next(RelayState.Idle, Trigger.BeginExecution).To);
        Assert.Equal(RelayState.Completed, TransitionTable.Next(RelayState.Planning, Trigger.PlanReady).To);
        Assert.Equal(RelayState.Completed, TransitionTable.Next(RelayState.AwaitingApproval, Trigger.AllRejected).To);
        Assert.Equal(RelayState.Completed, TransitionTable.Next(RelayState.Executing, Trigger.ExecutionSucceeded).To);
        Assert.Equal(RelayState.Failed, TransitionTable.Next(RelayState.Executing, Trigger.ExecutionFailed).To);
        Assert.Equal(RelayState.Failed, TransitionTable.Next(RelayState.Planning, Trigger.PlanFailed).To);

        // No capture state can jump straight into a turn state.
        foreach (var state in new[] { RelayState.NoteCapture, RelayState.CommandCapture, RelayState.AwaitingTranscript })
            foreach (var trigger in Enum.GetValues<Trigger>())
            {
                var t = TransitionTable.Next(state, trigger);
                if (t.Accepted) Assert.False(t.To.IsTurnActive());
            }
    }

    [Fact]
    public void CancelDuringATurnStopsSafely()
    {
        Assert.Equal(RelayState.Idle, TransitionTable.Next(RelayState.Planning, Trigger.Cancel).To);
        Assert.Equal(RelayState.Idle, TransitionTable.Next(RelayState.AwaitingApproval, Trigger.Cancel).To);
        var executing = TransitionTable.Next(RelayState.Executing, Trigger.Cancel);
        Assert.True(executing.Accepted);
        Assert.Equal(RelayState.Executing, executing.To);
        Assert.Equal(TransitionTable.StopRequested, executing.Message);
    }

    [Theory]
    [InlineData(RelayState.Planning)]
    [InlineData(RelayState.AwaitingApproval)]
    [InlineData(RelayState.Executing)]
    public void HotkeysAreRejectedDuringATurn(RelayState state)
    {
        Assert.False(TransitionTable.Next(state, Trigger.NoteKey).Accepted);
        Assert.False(TransitionTable.Next(state, Trigger.CommandKey).Accepted);
    }

    [Fact]
    public void KeysDuringAwaitingTranscriptAreRejectedWithGuidance()
    {
        Assert.Equal(TransitionTable.WaitingForTranscript, TransitionTable.Next(RelayState.AwaitingTranscript, Trigger.NoteKey).Message);
        Assert.Equal(TransitionTable.WaitingForTranscript, TransitionTable.Next(RelayState.AwaitingTranscript, Trigger.CommandKey).Message);
    }
}
