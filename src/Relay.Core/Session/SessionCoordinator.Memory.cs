using Relay.Core.Notes;

namespace Relay.Core.Session;

/// <summary>Note-mode organizing beyond the verbatim draft: extraction, routing, disputes (phase 6).</summary>
public sealed partial class SessionCoordinator
{
    /// <summary>Runs after the verbatim draft note is stored. Returns the receipt text.</summary>
    private string OrganizeNoteMemory(DraftNote note)
    {
        return OrchestratorEnabled
            ? "Saved 1 draft note · routing pending"
            : "Saved 1 draft note · orchestrator is off, routing deferred";
    }

    private IEnumerable<ReviewItem> MemoryReviewItems() => [];
}
