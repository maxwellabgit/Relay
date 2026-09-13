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

    [Fact]
    public void RecoveryMovesStartingToReadyOrLocked()
    {
        Assert.Equal(RelayState.Ready, TransitionTable.Next(RelayState.Starting, Trigger.RecoveryCompleted).To);
        Assert.Equal(RelayState.Locked, TransitionTable.Next(RelayState.Starting, Trigger.RecoveryLocked).To);
        Assert.False(TransitionTable.Next(RelayState.Starting, Trigger.Cancel).Accepted);
        Assert.Equal(TransitionTable.StartingMessage, TransitionTable.Next(RelayState.Starting, Trigger.Dismiss).Message);
    }

    [Fact]
    public void ReadyAcceptsDismissAndCancelWithoutLeaving()
    {
        Assert.Equal(RelayState.Ready, TransitionTable.Next(RelayState.Ready, Trigger.Dismiss).To);
        Assert.Equal(RelayState.Ready, TransitionTable.Next(RelayState.Ready, Trigger.Cancel).To);
        Assert.False(TransitionTable.Next(RelayState.Ready, Trigger.Retry).Accepted);
        Assert.False(TransitionTable.Next(RelayState.Ready, Trigger.Unlock).Accepted);
    }

    [Fact]
    public void FailedOnlyLeavesThroughDismissOrRetry()
    {
        Assert.Equal(RelayState.Ready, TransitionTable.Next(RelayState.Failed, Trigger.Dismiss).To);
        Assert.Equal(RelayState.Ready, TransitionTable.Next(RelayState.Failed, Trigger.Retry).To);
        Assert.False(TransitionTable.Next(RelayState.Failed, Trigger.Cancel).Accepted);
        Assert.Equal(TransitionTable.InspectFailureFirst, TransitionTable.Next(RelayState.Failed, Trigger.Unlock).Message);
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
    public void FailMovesToFailedExceptFromLockedOrFailed()
    {
        Assert.Equal(RelayState.Failed, TransitionTable.Next(RelayState.Ready, Trigger.Fail).To);
        Assert.Equal(RelayState.Failed, TransitionTable.Next(RelayState.Starting, Trigger.Fail).To);
        Assert.False(TransitionTable.Next(RelayState.Failed, Trigger.Fail).Accepted);
        Assert.False(TransitionTable.Next(RelayState.Locked, Trigger.Fail).Accepted);
    }

    [Fact]
    public void LockedOnlyLeavesThroughExplicitUnlock()
    {
        foreach (var trigger in Enum.GetValues<Trigger>())
        {
            var t = TransitionTable.Next(RelayState.Locked, trigger);
            if (trigger == Trigger.Unlock) Assert.Equal(RelayState.Ready, t.To);
            else if (trigger == Trigger.Lock) Assert.False(t.Accepted);
            else Assert.False(t.Accepted);
        }
    }
}
