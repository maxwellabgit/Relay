using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Relay.Core.Capabilities;

/// <summary>`conversation.task.capture@1` — explicit owner/date only; code owns date parsing.</summary>
public sealed class TaskCaptureCapability : ICapabilityHandler
{
    public const string Id = "conversation.task.capture";
    public const int CapabilityVersion = 1;
    public static string AtVersion => $"{Id}@{CapabilityVersion}";
    public const string CreateTaskCapability = "task.create";

    private static readonly Regex ExplicitOwner = new(
        @"\b([A-Z][a-z]+)\s+will\b",
        RegexOptions.Compiled);
    private static readonly Regex VagueOwner = new(
        @"\b(someone|somebody|anybody|anyone)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string CapabilityId => Id;
    public int Version => CapabilityVersion;

    public static CapabilityDefinition Definition { get; } = new()
    {
        Id = Id,
        Version = CapabilityVersion,
        AllowedOrigins = ["observed", "direct"],
        SideEffectClass = CapabilitySideEffects.LocalWrite,
        EvaluationFixtures =
        [
            "TaskCapture_ExplicitOwnerAndDate_ProposesTaskWithCandidates",
            "TaskCapture_SomeoneShould_NoInferredOwner_OwnerlessOrClarify",
            "TaskCapture_DateComparison_IsPerformedInCode",
        ],
        HandlerKey = Id,
    };

    public Task<CapabilityResult> HandleAsync(CapabilityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = request.Arguments.TryGetValue("span", out var span) ? span
            : request.Objective ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(new CapabilityResult
            {
                Kind = CapabilityResultKinds.Clarification,
                FeedText = "No commitment text to capture.",
                Reason = "no_text",
                Done = true,
            });
        }

        var asOf = request.At == default ? DateTimeOffset.UtcNow : request.At;
        string? owner = null;
        var vague = VagueOwner.IsMatch(text);
        var ownerMatch = ExplicitOwner.Match(text);
        if (ownerMatch.Success && !vague)
            owner = ownerMatch.Groups[1].Value;

        var due = TaskDateParsing.TryParseDueDate(text, asOf);
        var dueText = due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        var title = owner is null
            ? Clip(text, 80)
            : $"{owner}: {Clip(text, 60)}";

        if (vague && owner is null)
        {
            return Task.FromResult(new CapabilityResult
            {
                Kind = CapabilityResultKinds.Clarification,
                FeedText = "Commitment noted without an explicit owner — clarify who owns it.",
                PresentationLevel = "persistent",
                Done = true,
                Reason = "no_inferred_owner",
                Artifacts =
                {
                    ["title"] = title,
                    ["owner"] = "",
                    ["dueDate"] = dueText,
                },
                SourceRefs = request.SourceRefs.ToList(),
            });
        }

        var idempotencyKey = DeterministicIdempotencyKey(request.CaseId, title, owner ?? "", dueText);
        return Task.FromResult(new CapabilityResult
        {
            Kind = CapabilityResultKinds.ProposeOperation,
            FeedText = owner is null
                ? $"Task proposal: {title}"
                : $"Task proposal for {owner}: {title}",
            PresentationLevel = "persistent",
            Done = true,
            Reason = "explicit_commitment",
            Artifacts =
            {
                ["capability"] = CreateTaskCapability,
                ["title"] = title,
                ["commitment"] = text.Trim(),
                ["owner"] = owner ?? "",
                ["dueDate"] = dueText,
                ["idempotencyKey"] = idempotencyKey,
            },
            SourceRefs = request.SourceRefs.ToList(),
        });
    }

    private static string DeterministicIdempotencyKey(string caseId, string title, string owner, string due)
    {
        var material = string.Join('\n', "task.create", caseId, title, owner, due);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return "task.create:" + hash[..24];
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text.Trim() : text.Trim()[..max].Trim() + "…";
}

/// <summary>Bounded capability result kinds for the v0.1 production path.</summary>
public static class CapabilityResultKinds
{
    public const string Feed = "Feed";
    public const string ProposeOperation = "ProposeOperation";
    public const string Clarification = "Clarification";
    public const string Wait = "Wait";
    public const string Complete = "Complete";

    /// <summary>Legacy alias retained for characterization fixtures.</summary>
    public const string TaskProposal = "task_proposal";
    public const string OwnerlessOrClarify = "ownerless_or_clarify";
}

/// <summary>Deterministic weekday/date extraction — never delegated to a model.</summary>
public static class TaskDateParsing
{
    private static readonly string[] Weekdays =
        ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    public static DateOnly? TryParseDueDate(string text, DateTimeOffset asOf)
    {
        // Explicit ISO-like or Month day
        var iso = Regex.Match(text, @"\b(20\d{2}-\d{2}-\d{2})\b");
        if (iso.Success && DateOnly.TryParse(iso.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;

        var monthDay = Regex.Match(text, @"\b(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})\b",
            RegexOptions.IgnoreCase);
        if (monthDay.Success &&
            DateTime.TryParse($"{monthDay.Groups[1].Value} {monthDay.Groups[2].Value} {asOf.Year}",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var md))
        {
            var date = DateOnly.FromDateTime(md);
            if (date < DateOnly.FromDateTime(asOf.DateTime))
                date = date.AddYears(1);
            return date;
        }

        foreach (var (name, index) in Weekdays.Select((n, i) => (n, i)))
        {
            if (!Regex.IsMatch(text, $@"\b{name}\b", RegexOptions.IgnoreCase))
                continue;
            var today = (int)asOf.DayOfWeek;
            var delta = (index - today + 7) % 7;
            if (delta == 0) delta = 7; // next occurrence
            return DateOnly.FromDateTime(asOf.Date.AddDays(delta));
        }

        return null;
    }

    /// <summary>Returns negative if a is before b, zero if equal, positive if after.</summary>
    public static int Compare(DateOnly a, DateOnly b) => a.CompareTo(b);
}
