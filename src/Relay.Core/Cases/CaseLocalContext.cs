using Relay.Core.Ids;
using Relay.Core.Notes;
using Relay.Core.Projects;
using Relay.Core.Search;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Cases;

/// <summary>
/// Local notes/projects context for the case runtime: registry + rebuildable <see cref="SearchIndex"/>.
/// </summary>
public sealed class CaseLocalContext
{
    public const string AtlasBetaBody = "The Atlas beta ships on October 14.";
    public const string AtlasBetaTopic = "atlas-beta-date";
    public const string AtlasSourceEventId = "evt-atlas-beta-source";

    public CaseLocalContext(DataRoot root, IClock clock)
    {
        Root = root;
        Clock = clock;
        Registry = new ProjectRegistry(root);
        Index = new SearchIndex();
        RebuildIndex();
    }

    public DataRoot Root { get; }
    public IClock Clock { get; }
    public ProjectRegistry Registry { get; }
    public SearchIndex Index { get; private set; }

    public string ProjectsHome => Root.Combine("projects");

    public void RebuildIndex()
    {
        var index = new SearchIndex();
        foreach (var project in Registry.Active)
        {
            if (!Directory.Exists(project.RootPath)) continue;
            var (notes, _) = ProjectNoteStore.ReadAll(project.RootPath);
            foreach (var (note, _) in notes) index.IndexNote(note, project);
        }
        Index = index;
    }

    /// <summary>
    /// Seeds an Atlas project with a stored decision about the beta date (source span attached).
    /// </summary>
    public (ProjectRecord Project, NoteDocument Note) SeedAtlasBetaDecision(
        string? projectId = null,
        string? noteId = null,
        DateTimeOffset? at = null)
    {
        Directory.CreateDirectory(ProjectsHome);
        var now = at ?? Clock.UtcNow;
        var project = new ProjectRecord
        {
            Id = projectId ?? Ulid.NewUlid(now),
            Slug = "atlas",
            Name = "Atlas",
            RootPath = Path.Combine(ProjectsHome, "atlas"),
            CreatedAt = now,
        };
        if (Registry.ById(project.Id) is null && !Registry.SlugInUse(project.Slug))
        {
            ProjectLayout.Create(project, now);
            Registry.Add(project);
        }
        else
        {
            project = Registry.FindActive("atlas") ?? Registry.ById(project.Id)
                ?? throw new InvalidOperationException("Atlas project could not be resolved.");
        }

        var body = AtlasBetaBody;
        var note = new NoteDocument
        {
            Id = noteId ?? Ulid.NewUlid(now),
            ProjectId = project.Id,
            Type = NoteTypes.Decision,
            Status = NoteStatus.Active,
            Created = now,
            Confidence = 0.95,
            Topic = AtlasBetaTopic,
            Spans =
            [
                new SourceSpan(AtlasSourceEventId, 0, body.Length),
            ],
            Body = body,
        };

        if (ProjectNoteStore.Find(project.RootPath, note.Id) is null)
            ProjectNoteStore.WriteNew(project.RootPath, note);

        RebuildIndex();
        return (project, note);
    }

    /// <summary>Seeds a Lightshift project with a project-scoped BESS glossary entry.</summary>
    public (ProjectRecord Project, Memory.GlossaryEntry Entry) SeedLightshiftBess(
        string expansion = "Battery Energy Storage System",
        string? projectId = null,
        DateTimeOffset? at = null)
    {
        Directory.CreateDirectory(ProjectsHome);
        var now = at ?? Clock.UtcNow;
        var project = new ProjectRecord
        {
            Id = projectId ?? Ulid.NewUlid(now),
            Slug = "lightshift",
            Name = "Lightshift",
            RootPath = Path.Combine(ProjectsHome, "lightshift"),
            CreatedAt = now,
        };
        if (Registry.ById(project.Id) is null && !Registry.SlugInUse(project.Slug))
        {
            ProjectLayout.Create(project, now);
            Registry.Add(project);
        }
        else
        {
            project = Registry.FindActive("lightshift") ?? Registry.ById(project.Id)
                ?? throw new InvalidOperationException("Lightshift project could not be resolved.");
        }

        var store = new Memory.GlossaryStore(Root);
        var entry = new Memory.GlossaryEntry
        {
            Id = "bess-lightshift",
            Acronym = "BESS",
            Expansion = expansion,
            Scope = Memory.GlossaryScopes.Project,
            ProjectId = project.Id,
            SourceRefs = ["seed:lightshift-bess"],
        };
        store.SaveProject(project.RootPath, [entry]);
        return (project, entry);
    }

    public (NoteDocument Note, string Path, ProjectRecord Project)? FindNote(string projectId, string noteId)
    {
        var project = Registry.ById(projectId);
        if (project is null || !Directory.Exists(project.RootPath)) return null;
        var found = ProjectNoteStore.Find(project.RootPath, noteId);
        return found is null ? null : (found.Value.Note, found.Value.Path, project);
    }
}
