namespace Relay.Core.State;

public sealed record Transition(bool Accepted, RelayState From, RelayState To, Trigger Trigger, string? Message)
{
    public bool Changed => Accepted && From != To;

    public static Transition Accept(RelayState from, RelayState to, Trigger trigger, string? message = null) => new(true, from, to, trigger, message);
    public static Transition Reject(RelayState from, Trigger trigger, string message) => new(false, from, from, trigger, message);
}

/// <summary>
/// The complete, side-effect-free transition table for the v0.1 slice. Every (state, trigger)
/// pair resolves to exactly one outcome, and every rejection carries the message the UI shows.
/// The coordinator performs side effects only after the table accepts a transition.
/// </summary>
public static class TransitionTable
{
    public const string FinishOrCancelFirst = "Finish or cancel the active capture first.";
    public const string WaitingForTranscript = "Waiting for the transcript. Use Submit now or Cancel.";
    public const string StillOrganizing = "Still organizing the previous capture.";
    public const string InspectFailureFirst = "Inspect the failure first: Retry or Return to Idle.";
    public const string LockedMessage = "Relay is locked. Inspect the incident, then unlock explicitly.";
    public const string StartingMessage = "Relay is still starting.";
    public const string NotInThisBuild = "Not available in this build.";

    public static Transition Next(RelayState state, Trigger trigger)
    {
        // Lock wins from every state; it is how integrity and ledger failures stop the system.
        if (trigger == Trigger.Lock)
        {
            return state == RelayState.Locked
                ? Transition.Reject(state, trigger, "Already locked.")
                : Transition.Accept(state, RelayState.Locked, trigger);
        }

        // A reported failure stops work from any running state, but never overrides a lock.
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
                Trigger.RecoveryCompleted => Transition.Accept(state, RelayState.Idle, trigger),
                Trigger.RecoveryLocked => Transition.Accept(state, RelayState.Locked, trigger),
                _ => Transition.Reject(state, trigger, StartingMessage),
            },

            RelayState.Idle or RelayState.Completed => trigger switch
            {
                Trigger.NoteKey => Transition.Accept(state, RelayState.NoteCapture, trigger),
                Trigger.CommandKey => Transition.Accept(state, RelayState.CommandCapture, trigger),
                Trigger.Dismiss when state == RelayState.Completed => Transition.Accept(state, RelayState.Idle, trigger),
                Trigger.CommitInterrupted or Trigger.RecoverDraft when state == RelayState.Idle => Transition.Accept(state, RelayState.Organizing, trigger),
                _ => Transition.Reject(state, trigger, $"Nothing to {Describe(trigger)} while {state.Label()}."),
            },

            RelayState.NoteCapture => trigger switch
            {
                Trigger.NoteKey => Transition.Accept(state, RelayState.AwaitingTranscript, trigger),
                Trigger.CommandKey => Transition.Reject(state, trigger, FinishOrCancelFirst),
                Trigger.Cancel => Transition.Accept(state, RelayState.Idle, trigger),
                _ => Transition.Reject(state, trigger, $"Cannot {Describe(trigger)} during note capture."),
            },

            RelayState.CommandCapture => trigger switch
            {
                Trigger.CommandKey => Transition.Accept(state, RelayState.AwaitingTranscript, trigger),
                Trigger.NoteKey => Transition.Reject(state, trigger, FinishOrCancelFirst),
                Trigger.Cancel => Transition.Accept(state, RelayState.Idle, trigger),
                _ => Transition.Reject(state, trigger, $"Cannot {Describe(trigger)} during command capture."),
            },

            RelayState.AwaitingTranscript => trigger switch
            {
                Trigger.TranscriptStable => Transition.Accept(state, RelayState.Organizing, trigger),
                Trigger.SubmitNow => Transition.Accept(state, RelayState.Organizing, trigger),
                Trigger.TranscriptTimeout => Transition.Accept(state, RelayState.AwaitingTranscript, trigger, "No transcript arrived."),
                Trigger.RetryWait => Transition.Accept(state, RelayState.AwaitingTranscript, trigger),
                Trigger.Cancel => Transition.Accept(state, RelayState.Idle, trigger),
                Trigger.NoteKey or Trigger.CommandKey => Transition.Reject(state, trigger, WaitingForTranscript),
                _ => Transition.Reject(state, trigger, $"Cannot {Describe(trigger)} while awaiting the transcript."),
            },

            RelayState.Organizing => trigger switch
            {
                Trigger.OrganizeSucceeded => Transition.Accept(state, RelayState.Completed, trigger),
                Trigger.OrganizeFailed => Transition.Accept(state, RelayState.Failed, trigger),
                Trigger.Cancel => Transition.Reject(state, trigger, "Organizing only stores what was already captured and cannot be cancelled."),
                _ => Transition.Reject(state, trigger, StillOrganizing),
            },

            RelayState.Failed => trigger switch
            {
                Trigger.Dismiss => Transition.Accept(state, RelayState.Idle, trigger),
                Trigger.Retry => Transition.Accept(state, RelayState.Organizing, trigger),
                _ => Transition.Reject(state, trigger, InspectFailureFirst),
            },

            RelayState.Locked => trigger switch
            {
                Trigger.Unlock => Transition.Accept(state, RelayState.Idle, trigger),
                _ => Transition.Reject(state, trigger, LockedMessage),
            },

            RelayState.Planning or RelayState.AwaitingApproval or RelayState.Executing
                => Transition.Reject(state, trigger, NotInThisBuild),

            _ => Transition.Reject(state, trigger, $"Unhandled state {state}."),
        };
    }

    private static string Describe(Trigger trigger) => trigger switch
    {
        Trigger.Cancel => "cancel",
        Trigger.SubmitNow => "submit",
        Trigger.RetryWait => "retry waiting",
        Trigger.Dismiss => "dismiss",
        Trigger.Retry => "retry",
        Trigger.Unlock => "unlock",
        Trigger.TranscriptStable => "complete a transcript",
        Trigger.TranscriptTimeout => "time out",
        Trigger.OrganizeSucceeded or Trigger.OrganizeFailed => "finish organizing",
        Trigger.CommitInterrupted => "commit an interrupted capture",
        Trigger.RecoverDraft => "recover a cancelled draft",
        Trigger.RecoveryCompleted or Trigger.RecoveryLocked => "finish recovery",
        _ => trigger.ToString().ToLowerInvariant(),
    };
}
