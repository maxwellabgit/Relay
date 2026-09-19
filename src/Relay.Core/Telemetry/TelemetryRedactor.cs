using System.Collections.ObjectModel;

namespace Relay.Core.Telemetry;

/// <summary>
/// Allowlisted telemetry properties only. Unknown keys are rejected.
/// Counts and opaque object references are permitted; content hashes are not.
/// </summary>
public static class TelemetryRedactor
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedKeysByEvent =
        new ReadOnlyDictionary<string, IReadOnlySet<string>>(new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [ProductEventNames.AppStarted] = Set("appVersion"),
            [ProductEventNames.AppStopped] = Set("reason"),
            [ProductEventNames.AppCrashed] = Set("exceptionType"),
            [ProductEventNames.UiCommandStarted] = Set("commandId", "commandName"),
            [ProductEventNames.UiCommandCompleted] = Set("commandId", "commandName", "ok"),
            [ProductEventNames.UiCommandFailed] = Set("commandId", "commandName", "errorCode"),
            [ProductEventNames.TranscriptChanged] = Set("charCount", "isFinal"),
            [ProductEventNames.TranscriptWindowPersisted] = Set("objectId", "charCount"),
            [ProductEventNames.QueueEnqueued] = Set("readyCount"),
            [ProductEventNames.QueueDequeued] = Set("readyCount"),
            [ProductEventNames.QueueIdle] = Set("readyCount"),
            [ProductEventNames.CaseCreated] = Set("derived", "hasSourceObject", "origin", "kind", "charCount"),
            [ProductEventNames.CasePhaseChanged] = Set("decisionSignature", "stepsUsed", "maxSteps"),
            [ProductEventNames.CaseCompleted] = Set("stepsUsed", "maxSteps"),
            [ProductEventNames.CaseFailed] = Set("errorCode", "stepsUsed", "maxSteps"),
            [ProductEventNames.JudgmentAuthorized] = Set("hadGrant", "grantId", "purpose"),
            [ProductEventNames.JudgmentBlocked] = Set("hadGrant", "reasonCode"),
            [ProductEventNames.JudgmentDispatched] = Set("judgmentDefinitionId", "tokenBudget"),
            [ProductEventNames.JudgmentCompleted] = Set("judgmentDefinitionId", "durationMs"),
            [ProductEventNames.JudgmentDeferred] = Set("reasonCode"),
            [ProductEventNames.CapabilityStarted] = Set("capabilityId"),
            [ProductEventNames.CapabilityCompleted] = Set("capabilityId"),
            [ProductEventNames.CapabilityFailed] = Set("capabilityId", "errorCode"),
            [ProductEventNames.OperationProposed] = Set("actionId", "actionVersion", "connectionId", "idempotencyKey"),
            [ProductEventNames.OperationApproved] = Set("actionId", "caseVersion"),
            [ProductEventNames.OperationExecuting] = Set("actionId", "attemptId"),
            [ProductEventNames.OperationCompleted] = Set("actionId", "idempotencyKey", "duplicate"),
            [ProductEventNames.OperationFailed] = Set("actionId", "errorCode"),
            [ProductEventNames.FeedPublished] = Set("itemKind", "attention", "charCount"),
            [ProductEventNames.ProblemReported] = Set("severity"),
            [ProductEventNames.RuntimeHeartbeat] = Set("queueDepth", "listening"),
        });

    public static IReadOnlySet<string>? AllowedKeys(string eventName) =>
        AllowedKeysByEvent.TryGetValue(eventName, out var keys) ? keys : null;

    /// <summary>
    /// Returns only allowlisted keys for the event. Throws when an unknown key is present
    /// and <paramref name="rejectUnknown"/> is true; otherwise drops unknown keys.
    /// </summary>
    public static Dictionary<string, string> Allow(
        string eventName,
        IReadOnlyDictionary<string, string>? properties,
        bool rejectUnknown = true)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (properties is null || properties.Count == 0)
            return result;

        if (!AllowedKeysByEvent.TryGetValue(eventName, out var allowed))
        {
            if (rejectUnknown && properties.Count > 0)
                throw new TelemetryRedactionException(properties.Keys.First(), eventName);
            return result;
        }

        foreach (var (key, value) in properties)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!allowed.Contains(key))
            {
                if (rejectUnknown)
                    throw new TelemetryRedactionException(key, eventName);
                continue;
            }

            result[key] = value ?? "";
        }

        return result;
    }

    /// <summary>Legacy entry point used by existing emitters that omit event name until emit.</summary>
    public static Dictionary<string, string> Redact(
        IReadOnlyDictionary<string, string>? properties,
        bool rejectSensitive = false)
    {
        // Without an event name the only safe behavior is to accept an empty map
        // or reject any properties when rejectSensitive/rejectUnknown semantics are requested.
        if (properties is null || properties.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        if (rejectSensitive)
            throw new TelemetryRedactionException(properties.Keys.First());

        // Drop everything — denylists are insufficient; callers must use Allow(eventName, ...).
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static HashSet<string> Set(params string[] keys) =>
        new(keys, StringComparer.Ordinal);
}

public sealed class TelemetryRedactionException : Exception
{
    public TelemetryRedactionException(string key)
        : base($"Telemetry property '{key}' is not allowlisted.")
    {
        Key = key;
    }

    public TelemetryRedactionException(string key, string eventName)
        : base($"Telemetry property '{key}' is not allowlisted for event '{eventName}'.")
    {
        Key = key;
        EventName = eventName;
    }

    public string Key { get; }
    public string? EventName { get; }
}
