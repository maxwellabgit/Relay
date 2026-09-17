using System.Text;
using System.Text.Json;
using Relay.Core.Capabilities;
using Relay.Core.Cases;
using Relay.Core.Evidence;
using Relay.Core.Tests.Support;

namespace Relay.Core.Tests;

/// <summary>§11 retention and capability growth.</summary>
public class RetentionCapabilityTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 23, 0, 0, TimeSpan.Zero);
    private readonly TempDataRoot _tmp = new();
    private readonly FixedClock _clock = new(T0);

    public void Dispose() => _tmp.Dispose();

    private RetentionService Retention()
    {
        _tmp.Root.EnsureLayout(_clock);
        var evidence = new EvidenceStore(_tmp.Root, _clock);
        var objects = new ObjectStore(_tmp.Root, _clock);
        return new RetentionService(_tmp.Root, evidence, objects, _clock);
    }

    [Fact]
    public void Default_transcript_ttl_is_30_days_with_session_override()
    {
        var retention = Retention();
        var (ttl, _) = retention.GetSessionPolicy("sess-default");
        Assert.Equal(RetentionService.DefaultTranscriptTtl, ttl);

        retention.SetSessionPolicy("sess-short", TimeSpan.FromDays(7), deleteExcerptsWithSession: true);
        var (shortTtl, del) = retention.GetSessionPolicy("sess-short");
        Assert.Equal(TimeSpan.FromDays(7), shortTtl);
        Assert.True(del);
    }

    [Fact]
    public void Immediate_deletion_covers_artifacts_and_audit_has_ids_not_bodies()
    {
        var retention = Retention();
        var evidence = new EvidenceStore(_tmp.Root, _clock);
        var artifact = evidence.PutText(
            "private hallway talk that must not appear in audit",
            sessionIds: ["sess-1"],
            kind: "transcript",
            artifactId: "seg:1");

        var excerptId = retention.RetainExcerpt("sess-1", "approved excerpt", artifact.ArtifactId, artifact.ContentHash);
        Assert.True(File.Exists(Path.Combine(_tmp.Root.ExcerptsDirectory, excerptId + ".json")));

        var summary = evidence.PutText("summary of talk", sessionIds: ["sess-1"], kind: "summary", artifactId: "sum-1");
        var audit = retention.DeleteSession("sess-1", immediate: true, deleteExcerpts: false,
            survivingSummaryArtifactIds: [summary.ArtifactId]);

        Assert.Null(evidence.TryLoad(artifact.ArtifactId));
        Assert.True(File.Exists(Path.Combine(_tmp.Root.ExcerptsDirectory, excerptId + ".json")), "excerpts retained");
        Assert.DoesNotContain("hallway", JsonSerializer.Serialize(audit), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(artifact.ArtifactId, audit.ObjectIds);
        Assert.Contains(artifact.ContentHash, audit.ContentHashes);
        Assert.True(retention.IsOriginalEvidenceUnavailable(summary.ArtifactId));
    }

    [Fact]
    public void Option_deletes_excerpts_with_session()
    {
        var retention = Retention();
        var evidence = new EvidenceStore(_tmp.Root, _clock);
        var a = evidence.PutText("talk", sessionIds: ["sess-2"], kind: "transcript");
        var excerptId = retention.RetainExcerpt("sess-2", "keep?", a.ArtifactId, a.ContentHash);
        retention.DeleteSession("sess-2", deleteExcerpts: true);
        Assert.False(File.Exists(Path.Combine(_tmp.Root.ExcerptsDirectory, excerptId + ".json")));
    }

    [Fact]
    public void Content_addressed_blob_survives_while_another_owner_exists()
    {
        var retention = Retention();
        var evidence = new EvidenceStore(_tmp.Root, _clock);
        const string body = "shared bytes";
        var a = evidence.PutText(body, sessionIds: ["sess-a"], kind: "transcript", artifactId: "a1");
        var b = evidence.PutText(body, sessionIds: ["sess-b"], kind: "transcript", artifactId: "b1");
        Assert.Equal(a.ContentHash, b.ContentHash);

        retention.DeleteSession("sess-a");
        Assert.Null(evidence.TryLoad("a1"));
        Assert.NotNull(evidence.TryLoad("b1"));
        Assert.True(File.Exists(Path.Combine(_tmp.Root.EvidenceDirectory, "blobs", a.ContentHash + ".bin")));
    }

    [Fact]
    public void Capability_activation_binds_hash_and_eval_report()
    {
        _tmp.Root.EnsureLayout(_clock);
        var store = new CapabilityBundleStore(_tmp.Root, _clock);
        var activation = new CapabilityActivation(store, _clock);

        var eval = new CapabilityEvalResult
        {
            EvalId = "e1",
            Passed = true,
            ReportHash = "report-abc",
            At = T0,
        };
        var bundle = store.Create("weather-tool", "1.0.0", tools: ["weather"], permissions: ["net:weather"], evals: [eval]);
        Assert.False(string.IsNullOrEmpty(bundle.ContentHash));

        var ok = activation.Activate(bundle, "report-abc");
        Assert.NotNull(ok);
        Assert.True(activation.IsApprovalValid(bundle, ok!));

        // Changing permissions invalidates approval.
        bundle.Permissions.Add("net:admin");
        Assert.False(activation.IsApprovalValid(bundle, ok));
    }

    [Fact]
    public void Permission_expansion_requires_separate_approval()
    {
        _tmp.Root.EnsureLayout(_clock);
        var store = new CapabilityBundleStore(_tmp.Root, _clock);
        var activation = new CapabilityActivation(store, _clock);
        var a = store.Create("t", "1", permissions: ["read"]);
        var b = store.Create("t", "2", permissions: ["read", "write"]);
        Assert.True(activation.RequiresSeparatePermissionApproval(a, b));
        Assert.False(activation.RequiresSeparatePermissionApproval(b, a));
    }

    [Fact]
    public void Case_pins_bundle_version_and_rollback_records_target()
    {
        _tmp.Root.EnsureLayout(_clock);
        var store = new CapabilityBundleStore(_tmp.Root, _clock);
        var activation = new CapabilityActivation(store, _clock);
        var eval = new CapabilityEvalResult { EvalId = "e", Passed = true, ReportHash = "r1", At = T0 };
        var v1 = store.Create("cap", "1.0.0", evals: [eval]);
        var v2 = store.Create("cap", "1.1.0", rollbackTarget: v1.BundleId, evals: [eval]);
        store.PinCase("case-1", v2.BundleId, v2.ContentHash);
        var pin = store.TryGetCasePin("case-1");
        Assert.NotNull(pin);
        Assert.Equal(v2.BundleId, pin!.Value.BundleId);

        var act = activation.Activate(v2, "r1")!;
        var rolled = activation.Rollback(act, v1.BundleId)!;
        Assert.False(rolled.Active);
        Assert.Equal(v1.BundleId, rolled.RolledBackTo);
    }

    [Fact]
    public void Jint_documented_as_test_helper_only()
    {
        Assert.Contains("test helper", CapabilityActivation.JintIsTestHelperOnly, StringComparison.OrdinalIgnoreCase);
    }
}
