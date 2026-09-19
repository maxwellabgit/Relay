using Relay.Connectors;
using Relay.Core.Connectors;
using Relay.Core.Reflexes;

namespace Relay.Core.Tests;

public sealed class CrossCatalogTests
{
    [Fact]
    public void Every_reflex_source_resolves_to_an_exact_connector_version()
    {
        foreach (var reflex in ReflexCatalog.AlphaReflexes)
        {
            foreach (var source in reflex.PermittedSources)
            {
                var connector = Assert.Single(
                    ConnectorCatalog.AlphaConnectors,
                    c => c.Id == source.Id && c.Version == source.Version);
                Assert.Equal(source, connector.Ref);
            }
        }
    }

    [Fact]
    public void Every_reflex_read_resolves_to_a_read_operation()
    {
        foreach (var reflex in ReflexCatalog.AlphaReflexes)
        {
            foreach (var action in reflex.ReadPlan)
            {
                Assert.True(
                    ConnectorCatalog.TryGetAction(action.ConnectorId, action.ConnectorVersion, action.ActionId, action.ActionVersion, out var op),
                    action.Display);
                Assert.Equal(ConnectorOperationKind.Read, op!.Kind);
            }
        }
    }

    [Fact]
    public void Every_reflex_write_resolves_to_a_write_operation()
    {
        foreach (var reflex in ReflexCatalog.AlphaReflexes)
        {
            foreach (var action in reflex.PermittedWriteActions)
            {
                Assert.True(
                    ConnectorCatalog.TryGetAction(action.ConnectorId, action.ConnectorVersion, action.ActionId, action.ActionVersion, out var op),
                    action.Display);
                Assert.Equal(ConnectorOperationKind.Write, op!.Kind);
            }

            if (reflex.Rollback.CompensatingAction is { } compensating)
            {
                Assert.True(
                    ConnectorCatalog.TryGetAction(
                        compensating.ConnectorId,
                        compensating.ConnectorVersion,
                        compensating.ActionId,
                        compensating.ActionVersion,
                        out var op),
                    compensating.Display);
                Assert.Equal(ConnectorOperationKind.Write, op!.Kind);
            }
        }
    }

    [Fact]
    public void Judgment_refs_use_explicit_versions_not_at_suffix_parsing()
    {
        foreach (var reflex in ReflexCatalog.AlphaReflexes)
        {
            foreach (var judgment in reflex.Judgments)
            {
                Assert.False(judgment.Id.Contains('@', StringComparison.Ordinal));
                Assert.Equal(1, judgment.Version);
                Assert.Equal($"{judgment.Id}@{judgment.Version}", judgment.Display);
            }
        }
    }
}
