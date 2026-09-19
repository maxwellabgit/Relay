using Relay.Connectors;
using Relay.Core.Connectors;

namespace Relay.Core.Tests;

public sealed class ConnectorCatalogTests
{
    [Fact]
    public void Alpha_connector_ids_are_unique()
    {
        var ids = ConnectorCatalog.AlphaConnectors.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(9, ids.Count);
    }

    [Fact]
    public void Alpha_action_ids_are_unique_across_connectors()
    {
        var actions = ConnectorCatalog.AlphaConnectors
            .SelectMany(c => c.Operations.Select(o => o.ActionId))
            .ToList();
        Assert.Equal(actions.Count, actions.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_write_operation_defaults_off()
    {
        var writes = ConnectorCatalog.AlphaConnectors
            .SelectMany(c => c.Operations)
            .Where(o => o.Kind == ConnectorOperationKind.Write)
            .ToList();

        Assert.NotEmpty(writes);
        Assert.All(writes, w => Assert.False(w.DefaultEnabled));
    }

    [Fact]
    public void Plaid_exposes_no_writes()
    {
        var plaid = ConnectorCatalog.Require("plaid");
        Assert.DoesNotContain(plaid.Operations, o => o.Kind == ConnectorOperationKind.Write);
        Assert.Contains(plaid.Operations, o => o.Kind == ConnectorOperationKind.Read);
        Assert.Contains(plaid.Operations, o => o.Kind == ConnectorOperationKind.Observe);
    }

    [Fact]
    public void Public_connectors_expose_no_writes()
    {
        foreach (var id in new[] { "public-web", "wikipedia" })
        {
            var connector = ConnectorCatalog.Require(id);
            Assert.DoesNotContain(connector.Operations, o => o.Kind == ConnectorOperationKind.Write);
        }
    }

    [Fact]
    public void Write_operations_declare_idempotency_and_reconciliation_support()
    {
        var writes = ConnectorCatalog.AlphaConnectors
            .SelectMany(c => c.Operations)
            .Where(o => o.Kind == ConnectorOperationKind.Write);

        Assert.All(writes, w =>
        {
            Assert.True(w.SupportsIdempotency);
            Assert.False(string.IsNullOrWhiteSpace(w.InputSchema));
            Assert.False(string.IsNullOrWhiteSpace(w.OutputSchema));
        });
    }
}
