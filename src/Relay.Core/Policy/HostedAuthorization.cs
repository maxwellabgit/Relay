using Relay.Core.Evidence;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Policy;

/// <summary>Composable hosted-authorization stack for provenance gates and outbound packages.</summary>
public sealed class HostedAuthorization
{
    public HostedAuthorization(DataRoot root, IClock clock, bool hostedEnabled = false)
    {
        Evidence = new EvidenceStore(root, clock);
        Provenance = new ProvenanceGraph(Evidence);
        Grants = new HostedGrantStore(root, clock);
        Budgets = new BudgetReservations(root, clock, Grants);
        Outbound = new OutboundPackagePolicy(Evidence, Provenance, Grants, Budgets, root, clock, hostedEnabled);
    }

    public EvidenceStore Evidence { get; }
    public ProvenanceGraph Provenance { get; }
    public HostedGrantStore Grants { get; }
    public BudgetReservations Budgets { get; }
    public OutboundPackagePolicy Outbound { get; }

    public bool HostedEnabled
    {
        get => Outbound.HostedEnabled;
        set => Outbound.HostedEnabled = value;
    }
}
