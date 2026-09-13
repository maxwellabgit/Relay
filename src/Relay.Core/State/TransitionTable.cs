namespace Relay.Core.State;

public sealed record Transition(bool Accepted, RelayState From, RelayState To, Trigger Trigger, string? Message)
{
    public bool Changed => Accepted && From != To;

    public static Transition Accept(RelayState from, RelayState to, Trigger trigger, string? message = null) => new(true, from, to, trigger, message);
    public static Transition Reject(RelayState from, Trigger trigger, string message) => new(false, from, from, trigger, message);
}

/// <summary>
/// The session transition table. Capture, listening and turns are not states here: they are surface
/// flags and per-task status, so the only thing this table decides is whether Relay is starting,
/// ready, failed or locked.
/// </summary>
public static class TransitionTable
{
    public const string InspectFailureFirst = "Inspect the failure first: Retry or Return to Ready.";
    public const string LockedMessage = "Relay is locked. Inspect the incident, then unlock explicitly.";
    public const string StartingMessage = "Relay is still starting.";
    public const string FinishInstructionFirst = "Finish or cancel the active instruction first.";

    public static Transition Next(RelayState state, Trigger trigger)
    {
        if (trigger == Trigger.Lock)
        {
            return state == RelayState.Locked
                ? Transition.Reject(state, trigger, "Already locked.")
                : Transition.Accept(state, RelayState.Locked, trigger);
        }

        if (trigger == Trigger.Fail)
        {
            return state is RelayState.Locked or RelayState.Failed
                ? Transition.Reject(state, trigger, state == RelayState.Locked ? LockedMessage : "Already failed.")
                : Transition.Accept(state, RelayState.Failed, trigger);
        }

        return state switch
        {
            RelayState.Starting => trigger switch
            {
                Trigger.RecoveryCompleted => Transition.Accept(state, RelayState.Ready, trigger),
                Trigger.RecoveryLocked => Transition.Accept(state, RelayState.Locked, trigger),
                _ => Transition.Reject(state, trigger, StartingMessage),
            },

            RelayState.Ready => trigger switch
            {
                Trigger.Dismiss => Transition.Accept(state, RelayState.Ready, trigger),
                Trigger.Cancel => Transition.Accept(state, RelayState.Ready, trigger),
                _ => Transition.Reject(state, trigger, $"Nothing to {Describe(trigger)} while {state.Label()}."),
            },

            RelayState.Failed => trigger switch
            {
                Trigger.Dismiss => Transition.Accept(state, RelayState.Ready, trigger),
                Trigger.Retry => Transition.Accept(state, RelayState.Ready, trigger),
                _ => Transition.Reject(state, trigger, InspectFailureFirst),
            },

            RelayState.Locked => trigger switch
            {
                Trigger.Unlock => Transition.Accept(state, RelayState.Ready, trigger),
                _ => Transition.Reject(state, trigger, LockedMessage),
            },

            _ => Transition.Reject(state, trigger, $"Unhandled state {state}."),
        };
    }

    private static string Describe(Trigger trigger) => trigger switch
    {
        Trigger.Cancel => "cancel",
        Trigger.Dismiss => "dismiss",
        Trigger.Retry => "retry",
        Trigger.Unlock => "unlock",
        Trigger.RecoveryCompleted or Trigger.RecoveryLocked => "finish recovery",
        _ => trigger.ToString().ToLowerInvariant(),
    };
}
