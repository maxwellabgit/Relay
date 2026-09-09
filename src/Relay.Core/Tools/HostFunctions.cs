using System.Globalization;
using System.Text.Json;
using Relay.Core.Storage;

namespace Relay.Core.Tools;

/// <summary>One thing the machine will do for a built tool: its broker name, the name the script calls it by on <c>relay</c>, and what it takes and returns (for the build prompt).</summary>
public sealed record HostFunction(string Name, string JsName, string Signature, string Description);

/// <summary>
/// The closed set of things a built tool may ask the machine for. A tool's source runs in the worker
/// sandbox and can compute anything, but everything about the present — the clock, a time zone — comes
/// through here: one broker call per question, allowed only when the tool's manifest declares the
/// function and recorded like any other worker tool call. Adding a function is a code change, reviewed
/// here; it is never something a build can do.
/// </summary>
public sealed class HostFunctions
{
    public const string TimeNow = "time.now";
    public const string TimeZone = "time.zone";

    public static readonly IReadOnlyDictionary<string, HostFunction> Catalog = new Dictionary<string, HostFunction>(StringComparer.Ordinal)
    {
        [TimeNow] = new(TimeNow, "now", "relay.now(): string", "The current instant as an ISO 8601 UTC string, e.g. \"2026-09-09T04:59:00.000Z\"."),
        [TimeZone] = new(TimeZone, "zone", "relay.zone(zoneId: string): object",
            "The current local time in an IANA time zone (\"Europe/London\", \"Asia/Tokyo\", \"America/New_York\"). Returns " +
            "{zone, offsetMinutes, offset: \"+09:00\", abbreviation, isDst, utc: iso, local: {iso, date: \"2026-09-09\", time: \"13:59\", hour, minute, second, weekday: \"Wednesday\"}}. " +
            "Throws when the zone id is unknown; pass the id through from the arguments, do not guess offsets yourself."),
    };

    private readonly Func<DateTimeOffset> _clock;

    public HostFunctions(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Runs one host function. Returns ok=false with a message for an unknown function or bad argument; never throws on tool input.</summary>
    public (bool Ok, string Result) Invoke(string function, string argument)
    {
        try
        {
            switch (function)
            {
                case TimeNow:
                    return (true, _clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                case TimeZone:
                {
                    var id = (argument ?? "").Trim();
                    if (id.Length is 0 or > 64) return (false, "time.zone needs an IANA zone id such as \"Europe/London\"");
                    if (!TryFindZone(id, out var zone)) return (false, $"unknown time zone '{id}'; use an IANA id such as \"Europe/London\" or \"Asia/Tokyo\"");
                    var now = _clock();
                    var offset = zone.GetUtcOffset(now);
                    var local = now.ToOffset(offset);
                    var payload = new
                    {
                        zone = id,
                        offsetMinutes = (int)offset.TotalMinutes,
                        offset = (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                        abbreviation = zone.IsDaylightSavingTime(now) ? zone.DaylightName : zone.StandardName,
                        isDst = zone.IsDaylightSavingTime(now),
                        utc = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                        local = new
                        {
                            iso = local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                            date = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            time = local.ToString("HH:mm", CultureInfo.InvariantCulture),
                            hour = local.Hour,
                            minute = local.Minute,
                            second = local.Second,
                            weekday = local.DayOfWeek.ToString(),
                        },
                    };
                    return (true, JsonSerializer.Serialize(payload, RelayJson.Compact));
                }
                default:
                    return (false, $"no host function '{function}'; available: {string.Join(", ", Catalog.Keys)}");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidTimeZoneException or FormatException)
        {
            return (false, $"{function} failed: {ex.Message}");
        }
    }

    private static bool TryFindZone(string id, out TimeZoneInfo zone)
    {
        // IANA ids first (Windows maps them through ICU); Windows ids are accepted too so a tool that received one still works.
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found) && found is not null) { zone = found; return true; }
        if (string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase) || string.Equals(id, "Z", StringComparison.OrdinalIgnoreCase)) { zone = TimeZoneInfo.Utc; return true; }
        zone = TimeZoneInfo.Utc;
        return false;
    }

    /// <summary>The functions named, as the build prompt lists them.</summary>
    public static string Describe(IEnumerable<string> names)
        => string.Join("\n", names.Where(Catalog.ContainsKey).Select(n => $"- {Catalog[n].Signature} — {Catalog[n].Description}"));
}
