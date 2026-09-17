using System.Text.Json;

namespace Relay.Core.Cases;

/// <summary>Applies <see cref="CaseDomainEvent"/>s to a <see cref="CaseSnapshot"/> purely in memory.</summary>
public static class CaseReducer
{
    public static CaseSnapshot Apply(CaseSnapshot snapshot, IEnumerable<CaseDomainEvent> events)
    {
        var state = CloneMutable(snapshot);
        foreach (var evt in events)
            ApplyOne(state, evt);
        return state.ToSnapshot(snapshot);
    }

    public static void ApplyToRecord(CaseRecord record, CaseDomainEvent evt)
    {
        switch (evt.Type)
        {
            case CaseDomainEventTypes.StatusChanged:
                if (TryGetString(evt.Payload, "status", out var status) && status is not null)
                {
                    record.Status = status;
                    if (TryGetString(evt.Payload, "reason", out var reason))
                        record.WaitingReason = status == CaseStatus.Waiting ? reason : null;
                }
                break;

            case CaseDomainEventTypes.WaitEntered:
                if (TryGetString(evt.Payload, "reason", out var waitReason) && waitReason is not null)
                {
                    if (!record.PendingWaits.Contains(waitReason))
                        record.PendingWaits.Add(waitReason);
                    record.WaitingReason = waitReason;
                }
                if (TryGetBool(evt.Payload, "keepListening", out var keep) && keep)
                    record.Status = CaseStatus.Active;
                else
                    record.Status = CaseStatus.Waiting;
                break;

            case CaseDomainEventTypes.CaseCompleted:
                record.Status = CaseStatus.Completed;
                if (TryGetString(evt.Payload, "result", out var result))
                    record.Result = result;
                record.WaitingReason = null;
                record.PendingWaits.Clear();
                break;

            case CaseDomainEventTypes.CaseCancelled:
                record.Status = CaseStatus.Cancelled;
                if (TryGetString(evt.Payload, "reason", out var cancelReason))
                    record.Result = cancelReason;
                record.WaitingReason = null;
                break;

            case CaseDomainEventTypes.ObjectiveRevised:
                record.ObjectiveRevision++;
                if (TryGetString(evt.Payload, "objective", out var objective) && objective is not null)
                    record.ApprovedObjective = objective;
                break;

            case CaseDomainEventTypes.DecisionDepsSet:
                record.DecisionDependencyRefs = ReadStringArray(evt.Payload, "refs");
                break;

            case CaseDomainEventTypes.CitationsApplied:
                ApplyCitations(record, evt.Payload);
                break;

            case CaseDomainEventTypes.MoveRejected:
                if (record.Origin == CaseOrigin.Observed)
                    record.Status = CaseStatus.Active;
                break;

            case CaseDomainEventTypes.MindStepped:
                record.Budgets.StepsUsed++;
                break;
        }
    }

    private static void ApplyOne(MutableState state, CaseDomainEvent evt)
    {
        switch (evt.Type)
        {
            case CaseDomainEventTypes.StatusChanged:
                if (TryGetString(evt.Payload, "status", out var status) && status is not null)
                {
                    state.Status = status;
                    state.WaitingReason = status == CaseStatus.Waiting && TryGetString(evt.Payload, "reason", out var r)
                        ? r
                        : null;
                }
                break;
            case CaseDomainEventTypes.WaitEntered:
                if (TryGetString(evt.Payload, "reason", out var waitReason) && waitReason is not null
                    && !state.PendingWaits.Contains(waitReason))
                    state.PendingWaits.Add(waitReason);
                state.WaitingReason = waitReason;
                state.Status = TryGetBool(evt.Payload, "keepListening", out var keep) && keep
                    ? CaseStatus.Active
                    : CaseStatus.Waiting;
                break;
            case CaseDomainEventTypes.CaseCompleted:
                state.Status = CaseStatus.Completed;
                if (TryGetString(evt.Payload, "result", out var result))
                    state.Result = result;
                state.WaitingReason = null;
                state.PendingWaits.Clear();
                break;
            case CaseDomainEventTypes.CaseCancelled:
                state.Status = CaseStatus.Cancelled;
                state.WaitingReason = null;
                break;
            case CaseDomainEventTypes.ObjectiveRevised:
                state.ObjectiveRevision++;
                if (TryGetString(evt.Payload, "objective", out var objective) && objective is not null)
                    state.ApprovedObjective = objective;
                break;
            case CaseDomainEventTypes.DecisionDepsSet:
                state.DecisionDependencyRefs = ReadStringArray(evt.Payload, "refs");
                break;
            case CaseDomainEventTypes.MindStepped:
                state.StepsUsed++;
                break;
            case CaseDomainEventTypes.MoveRejected:
                if (state.Origin == CaseOrigin.Observed)
                    state.Status = CaseStatus.Active;
                break;
        }
    }

    private static void ApplyCitations(CaseRecord record, JsonElement payload)
    {
        if (!payload.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object)
            return;
        if (!args.TryGetProperty("citations", out var cites) || cites.ValueKind != JsonValueKind.Array)
            return;
        foreach (var c in cites.EnumerateArray())
        {
            if (c.TryGetProperty("objectId", out var oid) && oid.ValueKind == JsonValueKind.String)
            {
                var refText = "artifact:" + oid.GetString();
                if (!record.SourceRefs.Contains(refText))
                    record.SourceRefs.Add(refText);
                continue;
            }
            var noteId = c.TryGetProperty("noteId", out var n) ? n.GetString() : null;
            var projectId = c.TryGetProperty("projectId", out var p) ? p.GetString() : null;
            var eventId = c.TryGetProperty("eventId", out var e) ? e.GetString() : null;
            var start = c.TryGetProperty("start", out var s) && s.TryGetInt32(out var si) ? si : 0;
            var end = c.TryGetProperty("end", out var en) && en.TryGetInt32(out var ei) ? ei : 0;
            var refText2 = $"note:{projectId}/{noteId}#{eventId}:{start}-{end}";
            if (!string.IsNullOrEmpty(noteId) && !record.SourceRefs.Contains(refText2))
                record.SourceRefs.Add(refText2);
        }
    }

    private static bool TryGetString(JsonElement el, string name, out string? value)
    {
        value = null;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.String) { value = p.GetString(); return true; }
        return false;
    }

    private static bool TryGetBool(JsonElement el, string name, out bool value)
    {
        value = false;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind is JsonValueKind.True or JsonValueKind.False) { value = p.GetBoolean(); return true; }
        return false;
    }

    private static List<string> ReadStringArray(JsonElement el, string name)
    {
        var list = new List<string>();
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in p.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                list.Add(s);
        return list;
    }

    private static MutableState CloneMutable(CaseSnapshot s) => new()
    {
        CaseId = s.CaseId,
        Version = s.Version,
        Origin = s.Origin,
        Kind = s.Kind,
        Purpose = s.Purpose,
        AuthorizationStatus = s.AuthorizationStatus,
        ObjectiveRevision = s.ObjectiveRevision,
        ApprovedObjective = s.ApprovedObjective,
        Status = s.Status,
        Stage = s.Stage,
        WaitingReason = s.WaitingReason,
        Result = s.Result,
        StepsUsed = s.StepsUsed,
        PendingWaits = s.PendingWaits.ToList(),
        DecisionDependencyRefs = s.DecisionDependencyRefs.ToList(),
        PresentationPolicy = s.PresentationPolicy,
    };

    private sealed class MutableState
    {
        public required string CaseId { get; init; }
        public long Version { get; set; }
        public required string Origin { get; init; }
        public required string Kind { get; init; }
        public string Purpose { get; set; } = CasePurpose.Objective;
        public string AuthorizationStatus { get; set; } = CaseAuthorizationStatus.None;
        public long ObjectiveRevision { get; set; }
        public string? ApprovedObjective { get; set; }
        public required string Status { get; set; }
        public string? Stage { get; set; }
        public string? WaitingReason { get; set; }
        public string? Result { get; set; }
        public int StepsUsed { get; set; }
        public List<string> PendingWaits { get; set; } = [];
        public List<string> DecisionDependencyRefs { get; set; } = [];
        public string? PresentationPolicy { get; set; }

        public CaseSnapshot ToSnapshot(CaseSnapshot original) => new()
        {
            CaseId = CaseId,
            Version = Version,
            SchemaVersion = original.SchemaVersion,
            Origin = Origin,
            Kind = Kind,
            Purpose = Purpose,
            AuthorizationStatus = AuthorizationStatus,
            ObjectiveRevision = ObjectiveRevision,
            ApprovedObjective = ApprovedObjective,
            Status = Status,
            Stage = Stage,
            WaitingReason = WaitingReason,
            ControllerId = original.ControllerId,
            ControllerVersion = original.ControllerVersion,
            PendingCommandIds = original.PendingCommandIds,
            PendingOperationIds = original.PendingOperationIds,
            PendingWaits = PendingWaits,
            ProcessedEventIds = original.ProcessedEventIds,
            DecisionDependencyRefs = DecisionDependencyRefs,
            CompletionCriteria = original.CompletionCriteria,
            UnresolvedConflictIds = original.UnresolvedConflictIds,
            SourceRefs = original.SourceRefs,
            AllowedCapabilities = original.AllowedCapabilities,
            Budgets = original.Budgets,
            ParentCaseId = original.ParentCaseId,
            ChildCaseIds = original.ChildCaseIds,
            PresentationPolicy = PresentationPolicy,
            Result = Result,
            RecentEvents = original.RecentEvents,
            PendingOperations = original.PendingOperations,
            RecentSegments = original.RecentSegments,
            AvailableTools = original.AvailableTools,
            At = original.At,
            StepsUsed = StepsUsed,
        };
    }
}
