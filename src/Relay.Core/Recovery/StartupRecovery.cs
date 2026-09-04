using Relay.Core.Captures;
using Relay.Core.Ledger;
using Relay.Core.Sessions;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Recovery;

public sealed record RecoveryReport(
    LedgerVerification Verification,
    string? QuarantinePath,
    long QuarantinedBytes,
    IReadOnlyList<SessionRecord> CrashedSessions,
    CaptureDraft? InterruptedDraft,
    /// <summary>True when the ledger already holds a committed or cancelled record for the draft's capture id: the staging copy is redundant.</summary>
    bool InterruptedDraftAlreadyResolved,
    string? InterruptedDraftResolution);

/// <summary>
/// Runs before the UI is shown. Verifies the ledger, repairs a torn tail without discarding bytes,
/// finds sessions that never shut down cleanly, and surfaces an interrupted capture. It does not
/// write to the ledger itself; the coordinator records the findings once the ledger is open.
/// </summary>
public static class StartupRecovery
{
    public static RecoveryReport Run(DataRoot root, IDraftStore drafts, SessionStore sessions, IClock clock)
    {
        var verification = LedgerVerifier.Verify(root.LedgerPath);
        string? quarantinePath = null;
        long quarantinedBytes = 0;
        if (verification.Health == LedgerHealth.TornTail)
        {
            quarantinedBytes = verification.FileLength - verification.GoodByteLength;
            quarantinePath = LedgerVerifier.RepairTornTail(root.LedgerPath, root.LedgerQuarantineDirectory, verification, clock);
            verification = LedgerVerifier.Verify(root.LedgerPath);
        }

        var crashed = sessions.FindUnclean();

        var draft = drafts.ReadCurrent();
        var resolved = false;
        string? resolution = null;
        if (draft is not null)
        {
            foreach (var record in verification.Records)
            {
                if (record.Type is EventTypes.CaptureCommitted or EventTypes.CaptureCancelled or EventTypes.CaptureInterruptedDiscarded
                    && record.DataString("captureId") == draft.CaptureId)
                {
                    resolved = true;
                    resolution = record.Type;
                    break;
                }
            }
        }

        return new RecoveryReport(verification, quarantinePath, quarantinedBytes, crashed, draft, resolved, resolution);
    }
}
