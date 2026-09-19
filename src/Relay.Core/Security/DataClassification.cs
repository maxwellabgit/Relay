namespace Relay.Core.Security;

/// <summary>
/// Classification of stored or disclosed content. Derived content inherits the most restrictive source.
/// </summary>
public enum DataClassification
{
    LocalOnly = 0,
    HostedAllowedSession = 1,
    HostedAllowedProject = 2,
    Public = 3,
    Financial = 4,
}

public static class DataClassificationRules
{
    /// <summary>Lower rank = more restrictive.</summary>
    public static int Rank(DataClassification classification) => classification switch
    {
        DataClassification.LocalOnly => 0,
        DataClassification.Financial => 0,
        DataClassification.HostedAllowedSession => 1,
        DataClassification.HostedAllowedProject => 2,
        DataClassification.Public => 3,
        _ => 0,
    };

    public static DataClassification MostRestrictive(IEnumerable<DataClassification> classifications)
    {
        var list = classifications.ToList();
        if (list.Count == 0)
            return DataClassification.LocalOnly;
        return list.OrderBy(Rank).First();
    }

    public static bool IsHostedEligible(DataClassification classification) =>
        classification is DataClassification.HostedAllowedSession
            or DataClassification.HostedAllowedProject
            or DataClassification.Public;
}
