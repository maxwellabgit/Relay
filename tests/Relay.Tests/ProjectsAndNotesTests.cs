using Relay.Core.Backup;
using Relay.Core.Ids;
using Relay.Core.Notes;
using Relay.Core.Projects;
using Relay.Core.Workspaces;
using Relay.Tests.Support;

namespace Relay.Tests;

public class PathGuardTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    [Fact]
    public void TraversalOutOfTheRootIsRejected()
    {
        var root = Path.Combine(_tmp.Root.Path, "ws");
        Directory.CreateDirectory(root);
        var check = PathGuard.Check(Path.Combine(root, "..", "elsewhere", "x.md"), [PathGuard.Canonicalize(root)]);
        Assert.False(check.Ok);
        Assert.Contains("outside", check.Reason);
    }

    [Fact]
    public void NestedPathsAreAcceptedRegardlessOfCase()
    {
        var root = Path.Combine(_tmp.Root.Path, "WorkSpace");
        Directory.CreateDirectory(root);
        var check = PathGuard.Check(Path.Combine(root.ToLowerInvariant(), "Projects", "new-one"), [PathGuard.Canonicalize(root)]);
        Assert.True(check.Ok);
        Assert.Equal(PathGuard.Canonicalize(root), check.Root);
    }

    [Fact]
    public void PrefixCollisionIsNotContainment()
    {
        var root = Path.Combine(_tmp.Root.Path, "ws");
        Directory.CreateDirectory(root);
        Assert.False(PathGuard.Check(root + "2", [PathGuard.Canonicalize(root)]).Ok);
    }

    [Fact]
    public void DevicePathsAndStreamsAreRejected()
    {
        var root = PathGuard.Canonicalize(_tmp.Root.Path);
        Assert.False(PathGuard.Check(@"\\?\" + _tmp.Root.Path, [root]).Ok);
        Assert.False(PathGuard.Check(Path.Combine(_tmp.Root.Path, "a.md:hidden"), [root]).Ok);
    }

    [Fact]
    public void SymlinkEscapeIsResolvedWhenLinksCanBeCreated()
    {
        var root = Path.Combine(_tmp.Root.Path, "ws");
        var outside = Path.Combine(_tmp.Root.Path, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(root, "link");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception) { return; } // symlink creation needs Developer Mode or elevation; nothing to verify here
        var check = PathGuard.Check(Path.Combine(link, "escaped.md"), [PathGuard.Canonicalize(root)]);
        Assert.False(check.Ok);
    }

    public void Dispose() => _tmp.Dispose();
}

public class ProjectRegistryTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    [Theory]
    [InlineData("Market Study!", "market-study")]
    [InlineData("  Atlas  ", "atlas")]
    [InlineData("A--B", "a-b")]
    public void SlugsAreDerivedDeterministically(string name, string slug) => Assert.Equal(slug, Slug.From(name));

    [Theory]
    [InlineData("ok-slug", true)]
    [InlineData("-bad", false)]
    [InlineData("Bad", false)]
    [InlineData("", false)]
    public void SlugValidation(string slug, bool valid) => Assert.Equal(valid, Slug.IsValid(slug));

    [Fact]
    public void AddFindPersistAndReload()
    {
        _tmp.Root.EnsureLayout(new FixedClock(Harness.T0));
        var registry = new ProjectRegistry(_tmp.Root);
        var record = new ProjectRecord { Id = Ulid.NewUlid(Harness.T0), Slug = "atlas", Name = "Atlas", Aliases = ["the atlas project"], RootPath = Path.Combine(_tmp.Root.Path, "p", "atlas"), CreatedAt = Harness.T0 };
        registry.Add(record);

        Assert.Same(record, registry.Find("Atlas"));
        Assert.Same(record, registry.Find("the atlas project"));
        Assert.Same(record, registry.Find("ATLAS"));
        Assert.Null(registry.Find("nope"));
        Assert.Throws<InvalidOperationException>(() => registry.Add(new ProjectRecord { Id = "other", Slug = "atlas", Name = "x", RootPath = "y", CreatedAt = Harness.T0 }));

        var reloaded = new ProjectRegistry(_tmp.Root);
        Assert.Single(reloaded.All);
        Assert.Equal("atlas", reloaded.All[0].Slug);
    }

    [Fact]
    public void LayoutIsCreatedAndVerified()
    {
        var record = new ProjectRecord { Id = Ulid.NewUlid(Harness.T0), Slug = "atlas", Name = "Atlas", RootPath = Path.Combine(_tmp.Root.Path, "atlas"), CreatedAt = Harness.T0 };
        ProjectLayout.Create(record, Harness.T0);
        Assert.Empty(ProjectLayout.Verify(record.RootPath));
        Assert.Contains("id = \"" + record.Id + "\"", File.ReadAllText(ProjectLayout.ProjectToml(record.RootPath)));
        Directory.Delete(Path.Combine(record.RootPath, "decisions"));
        Assert.Equal(["decisions"], ProjectLayout.Verify(record.RootPath));
        Assert.Throws<IOException>(() => ProjectLayout.Create(record, Harness.T0));
    }

    public void Dispose() => _tmp.Dispose();
}

public class NoteDocumentTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    private static NoteDocument Sample(string id = "01M1NOTE000000000000000001") => new()
    {
        Id = id,
        ProjectId = "01M1PROJ000000000000000001",
        Type = NoteTypes.Decision,
        Status = NoteStatus.Active,
        Created = Harness.T0,
        Confidence = 0.825,
        CaptureId = "01M1CAPT000000000000000001",
        Topic = "pricing: tiers, annual",
        Spans = [new SourceSpan("01M1EVNT000000000000000001", 12, 98)],
        Supersedes = ["01M1NOTE000000000000000000"],
        Body = "We will price in three tiers.\n\nAnnual billing gets 15% off.",
    };

    [Fact]
    public void FrontMatterRoundTrips()
    {
        var note = Sample();
        var parsed = NoteDocument.Parse(note.ToMarkdown());
        Assert.Equal(note.Id, parsed.Id);
        Assert.Equal(note.ProjectId, parsed.ProjectId);
        Assert.Equal(note.Type, parsed.Type);
        Assert.Equal(note.Created, parsed.Created);
        Assert.Equal(0.825, parsed.Confidence);
        Assert.Equal(note.CaptureId, parsed.CaptureId);
        Assert.Equal(note.Topic, parsed.Topic);
        Assert.Equal(note.Spans, parsed.Spans);
        Assert.Equal(note.Supersedes, parsed.Supersedes);
        Assert.Equal(note.Body, parsed.Body);
        Assert.Equal(note.ToMarkdown(), parsed.ToMarkdown());
    }

    [Fact]
    public void NullsAndEmptyListsRoundTrip()
    {
        var note = new NoteDocument { Id = "a", ProjectId = "p", Type = NoteTypes.Idea, Created = Harness.T0, Body = "x" };
        var parsed = NoteDocument.Parse(note.ToMarkdown());
        Assert.Null(parsed.Confidence);
        Assert.Null(parsed.Topic);
        Assert.Empty(parsed.Spans);
        Assert.Empty(parsed.Supersedes);
    }

    [Fact]
    public void ForeignFrontMatterIsRejectedNotGuessed()
    {
        Assert.Throws<FormatException>(() => NoteDocument.Parse("# just markdown"));
        Assert.Throws<FormatException>(() => NoteDocument.Parse("---\nid: x\n---\nbody"));
    }

    [Fact]
    public void StoreWritesVersionsAndNeverOverwrites()
    {
        var root = Path.Combine(_tmp.Root.Path, "proj");
        var record = new ProjectRecord { Id = "p", Slug = "proj", Name = "Proj", RootPath = root, CreatedAt = Harness.T0 };
        ProjectLayout.Create(record, Harness.T0);

        var note = Sample();
        var first = ProjectNoteStore.WriteNew(root, note);
        Assert.Equal(Path.Combine(root, "decisions", note.Id + ".md"), first.Path);
        Assert.Throws<IOException>(() => ProjectNoteStore.WriteNew(root, note));

        note.Body = "We will price in four tiers.";
        var second = ProjectNoteStore.WriteVersion(root, note);
        Assert.Equal(2, second.Version);
        Assert.NotNull(second.PreviousVersionPath);
        Assert.Contains("three tiers", File.ReadAllText(second.PreviousVersionPath!));
        Assert.Contains("four tiers", File.ReadAllText(second.Path));
        Assert.NotEqual(first.Sha256, second.Sha256);

        var (all, problems) = ProjectNoteStore.ReadAll(root);
        Assert.Single(all);
        Assert.Empty(problems);

        File.WriteAllText(Path.Combine(root, "notes", "stray.md"), "# hand written");
        var (_, problems2) = ProjectNoteStore.ReadAll(root);
        Assert.Single(problems2);
    }

    public void Dispose() => _tmp.Dispose();
}

public class ArchiveAndBackupTests : IDisposable
{
    private readonly TempRoot _tmp = new();

    [Fact]
    public void ArchiveMovesEverythingWithAManifestAndRestoreVerifiesIt()
    {
        var clock = new FixedClock(Harness.T0);
        _tmp.Root.EnsureLayout(clock);
        var record = new ProjectRecord { Id = Ulid.NewUlid(Harness.T0), Slug = "atlas", Name = "Atlas", RootPath = Path.Combine(_tmp.Root.Path, "ws", "atlas"), CreatedAt = Harness.T0 };
        ProjectLayout.Create(record, Harness.T0);
        File.WriteAllText(Path.Combine(record.RootPath, "notes", "a.md"), "note a");

        var result = ProjectArchiver.Archive(record, _tmp.Root, Harness.T0);
        Assert.False(Directory.Exists(record.RootPath));
        Assert.True(Directory.Exists(result.NewPath));
        Assert.True(File.Exists(result.ManifestPath));
        Assert.Equal(5, result.Files); // project.toml, overview.md, artifacts.jsonl, sources.jsonl, notes/a.md
        Assert.Equal(Harness.T0 + ProjectArchiver.RecoveryWindow, result.RecoverUntil);

        record.Status = ProjectRecord.ArchivedStatus;
        record.ArchivedPath = result.NewPath;
        File.AppendAllText(Path.Combine(result.NewPath, "notes", "a.md"), " tampered");

        var (restored, problems) = ProjectArchiver.Restore(record, Harness.T0);
        Assert.Equal(record.RootPath, restored);
        Assert.True(Directory.Exists(restored));
        Assert.Contains(problems, p => p.Contains("Modified while archived"));
    }

    [Fact]
    public void BackupExportsAndVerifies()
    {
        var clock = new FixedClock(Harness.T0);
        _tmp.Root.EnsureLayout(clock);
        var record = new ProjectRecord { Id = Ulid.NewUlid(Harness.T0), Slug = "atlas", Name = "Atlas", RootPath = Path.Combine(_tmp.Root.Path, "ws", "atlas"), CreatedAt = Harness.T0 };
        ProjectLayout.Create(record, Harness.T0);

        var zip = Path.Combine(_tmp.Root.BackupsDirectory, "b.zip");
        var result = BackupService.Export(_tmp.Root, [record], zip, Harness.T0);
        Assert.True(File.Exists(zip));
        Assert.True(result.Files >= 5);
        var verification = BackupService.Verify(zip);
        Assert.True(verification.Ok, string.Join("; ", verification.Problems));

        // Corrupt one entry and expect the verifier to say so.
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = archive.Entries.First(e => e.FullName.EndsWith("overview.md", StringComparison.Ordinal));
            using var writer = new StreamWriter(entry.Open());
            writer.Write("tampered");
        }
        var broken = BackupService.Verify(zip);
        Assert.False(broken.Ok);
        Assert.Contains(broken.Problems, p => p.Contains("Hash mismatch"));
    }

    public void Dispose() => _tmp.Dispose();
}
