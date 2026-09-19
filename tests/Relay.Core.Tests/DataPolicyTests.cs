using Relay.Core.Security;

namespace Relay.Core.Tests;

public sealed class DataPolicyTests
{
    [Fact]
    public void Combine_takes_strictest_disclosure_and_unions_sensitivity()
    {
        var left = new DataPolicy(DisclosureClass.HostedSession, DataSensitivity.Email);
        var right = new DataPolicy(DisclosureClass.LocalOnly, DataSensitivity.Financial);
        var combined = DataPolicy.Combine(left, right);

        Assert.Equal(DisclosureClass.LocalOnly, combined.Disclosure);
        Assert.Equal(DataSensitivity.Email | DataSensitivity.Financial, combined.Sensitivity);
    }

    [Fact]
    public void Combine_is_commutative()
    {
        var a = new DataPolicy(DisclosureClass.HostedProject, DataSensitivity.Calendar);
        var b = new DataPolicy(DisclosureClass.HostedSession, DataSensitivity.Personal | DataSensitivity.Email);

        Assert.Equal(DataPolicy.Combine(a, b), DataPolicy.Combine(b, a));
    }

    [Fact]
    public void Combine_is_associative()
    {
        var a = new DataPolicy(DisclosureClass.Public, DataSensitivity.SourceCode);
        var b = new DataPolicy(DisclosureClass.HostedSession, DataSensitivity.Email);
        var c = new DataPolicy(DisclosureClass.LocalOnly, DataSensitivity.Financial);

        var left = DataPolicy.Combine(DataPolicy.Combine(a, b), c);
        var right = DataPolicy.Combine(a, DataPolicy.Combine(b, c));
        Assert.Equal(left, right);
    }

    [Fact]
    public void Combine_is_idempotent()
    {
        var policy = DataPolicy.Observed(DataSensitivity.Personal | DataSensitivity.Financial);
        Assert.Equal(policy, DataPolicy.Combine(policy, policy));
    }

    [Fact]
    public void Financial_sensitivity_does_not_collapse_with_local_only_disclosure()
    {
        var financial = DataPolicy.Observed(DataSensitivity.Financial);
        var personal = DataPolicy.Observed(DataSensitivity.Personal);
        var combined = DataPolicy.Combine(financial, personal);

        Assert.Equal(DisclosureClass.LocalOnly, combined.Disclosure);
        Assert.Equal(DataSensitivity.Financial | DataSensitivity.Personal, combined.Sensitivity);
    }

    [Fact]
    public void Observed_defaults_to_local_only_disclosure()
    {
        var observed = DataPolicy.Observed(DataSensitivity.Email | DataSensitivity.Calendar);
        Assert.Equal(DisclosureClass.LocalOnly, observed.Disclosure);
        Assert.False(observed.IsHostedEligible);
    }
}
