namespace Relay.Core.Security;

/// <summary>Who may receive content for hosted processing. Independent of content sensitivity.</summary>
public enum DisclosureClass
{
    LocalOnly = 0,
    HostedSession = 1,
    HostedProject = 2,
    Public = 3,
}

/// <summary>Content-kind flags. Orthogonal to disclosure class.</summary>
[Flags]
public enum DataSensitivity
{
    None = 0,
    Personal = 1,
    Email = 2,
    Calendar = 4,
    SourceCode = 8,
    Financial = 16,
}

/// <summary>
/// Combined storage/disclosure policy. Combining policies takes the strictest disclosure
/// and unions every sensitivity flag.
/// </summary>
public sealed record DataPolicy(DisclosureClass Disclosure, DataSensitivity Sensitivity)
{
    public static DataPolicy LocalOnly { get; } = new(DisclosureClass.LocalOnly, DataSensitivity.None);
    public static DataPolicy Public { get; } = new(DisclosureClass.Public, DataSensitivity.None);

    public static DataPolicy Observed(DataSensitivity sensitivity) =>
        new(DisclosureClass.LocalOnly, sensitivity);

    public static DataPolicy Combine(DataPolicy left, DataPolicy right) =>
        new(StrictestDisclosure(left.Disclosure, right.Disclosure), left.Sensitivity | right.Sensitivity);

    public static DataPolicy Combine(IEnumerable<DataPolicy> policies)
    {
        DataPolicy? acc = null;
        foreach (var policy in policies)
            acc = acc is null ? policy : Combine(acc, policy);
        return acc ?? LocalOnly;
    }

    public static DisclosureClass StrictestDisclosure(DisclosureClass a, DisclosureClass b) =>
        Rank(a) <= Rank(b) ? a : b;

    /// <summary>Lower rank = more restrictive.</summary>
    public static int Rank(DisclosureClass disclosure) => disclosure switch
    {
        DisclosureClass.LocalOnly => 0,
        DisclosureClass.HostedSession => 1,
        DisclosureClass.HostedProject => 2,
        DisclosureClass.Public => 3,
        _ => 0,
    };

    public bool IsHostedEligible =>
        Disclosure is DisclosureClass.HostedSession
            or DisclosureClass.HostedProject
            or DisclosureClass.Public;
}
