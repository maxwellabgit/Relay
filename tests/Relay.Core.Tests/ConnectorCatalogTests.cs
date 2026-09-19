using Relay.Connectors;
using Relay.Core.Connectors;
using Relay.Core.Security;

namespace Relay.Core.Tests;

public sealed class ConnectorCatalogTests
{
    [Fact]
    public void Alpha_connector_ids_and_versions_are_unique()
    {
        var keys = ConnectorCatalog.AlphaConnectors.Select(c => $"{c.Id}@{c.Version}").ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(9, keys.Count);
    }

    [Fact]
    public void Alpha_action_refs_are_unique()
    {
        var actions = ConnectorCatalog.AlphaConnectors
            .SelectMany(c => c.Operations.Select(o => c.ActionRef(o.ActionId, o.ActionVersion).Display))
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
    }

    [Fact]
    public void Observed_content_defaults_to_local_only_disclosure()
    {
        foreach (var connector in ConnectorCatalog.AlphaConnectors.Where(c => c.Id is not "public-web" and not "wikipedia" and not "memory"))
        {
            foreach (var op in connector.Operations.Where(o => o.Kind is ConnectorOperationKind.Observe or ConnectorOperationKind.Read))
            {
                Assert.Equal(DisclosureClass.LocalOnly, op.ReturnedPolicy.Disclosure);
            }
        }
    }

    [Fact]
    public void GitHub_read_scopes_are_fine_grained_not_classic_repo()
    {
        var github = ConnectorCatalog.Require("github");
        var readOps = github.Operations.Where(o => o.Kind is ConnectorOperationKind.Observe or ConnectorOperationKind.Read);
        Assert.All(readOps, o =>
        {
            Assert.DoesNotContain("repo", o.RequiredOAuthScopes);
            Assert.Contains("permissions:contents:read", o.RequiredOAuthScopes);
            Assert.Contains("permissions:issues:read", o.RequiredOAuthScopes);
        });

        Assert.Contains(github.Operations, o => o.ActionId == "github.issue-draft-local");
        Assert.Contains(github.Operations, o => o.ActionId == "github.issue-create");
        Assert.DoesNotContain(github.Operations, o => o.ActionId is "github.issue-draft" or "github.comment-draft");
    }

    [Fact]
    public void Schemas_resolve_to_versioned_documents()
    {
        foreach (var connector in ConnectorCatalog.AlphaConnectors)
        {
            foreach (var op in connector.Operations)
            {
                Assert.True(ConnectorSchemas.All.ContainsKey(op.InputSchema.SchemaId), op.InputSchema.Display);
                Assert.True(ConnectorSchemas.All.ContainsKey(op.OutputSchema.SchemaId), op.OutputSchema.Display);
                Assert.Contains("\"$schema\"", ConnectorSchemas.All[op.InputSchema.SchemaId].JsonSchema, StringComparison.Ordinal);
            }
        }
    }
}
