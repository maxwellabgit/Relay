using System.Globalization;
using System.Text.RegularExpressions;

namespace Relay.Core.Capabilities;

/// <summary>`conversation.task.capture@1` — explicit owner/date only; code owns date parsing.</summary>
public sealed class TaskCaptureCapability : ICapabilityHandler
{
    public const string Id = "conversation.task.capture";
    public const int CapabilityVersion = 1;
    public static string AtVersion => $"{Id}@{CapabilityVersion}";

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
                Kind = "unresolved",
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
        var title = owner is null
            ? Clip(text, 80)
            : $"{owner}: {Clip(text, 60)}";

        if (vague && owner is null)
        {
            return Task.FromResult(new CapabilityResult
            {
                Kind = "ownerless_or_clarify",
                FeedText = "Commitment noted without an explicit owner — clarify who owns it.",
                PresentationLevel = "persistent",
                Done = true,
                Reason = "no_inferred_owner",
                Artifacts =
                {
                    ["title"] = title,
                    ["owner"] = "",
                    ["dueDate"] = due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                },
                SourceRefs = request.SourceRefs.ToList(),
            });
        }

        return Task.FromResult(new CapabilityResult
        {
            Kind = "task_proposal",
            FeedText = owner is null
                ? $"Task proposal: {title}"
                : $"Task proposal for {owner}: {title}",
            PresentationLevel = "persistent",
            Done = true,
            Reason = "explicit_commitment",
            Artifacts =
            {
                ["title"] = title,
                ["owner"] = owner ?? "",
                ["dueDate"] = due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            },
            SourceRefs = request.SourceRefs.ToList(),
        });
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text.Trim() : text.Trim()[..max].Trim() + "…";
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
