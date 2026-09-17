namespace Relay.Core.Evidence;

/// <summary>
/// Provenance graph over content artifacts. Walks input lineage and computes
/// effective export restriction and complete source closure.
/// </summary>
public sealed class ProvenanceGraph
{
    private readonly EvidenceStore _store;
    private readonly object _gate = new();
    private readonly List<ProvenanceEdge> _edges = [];

    public ProvenanceGraph(EvidenceStore store) => _store = store;

    public void Link(string fromArtifactId, string toArtifactId, string relation = "derived_from")
    {
        lock (_gate)
        {
            _edges.Add(new ProvenanceEdge
            {
                FromArtifactId = fromArtifactId,
                ToArtifactId = toArtifactId,
                Relation = relation,
            });
        }
    }

    public IReadOnlyList<ProvenanceEdge> Edges
    {
        get { lock (_gate) return _edges.ToList(); }
    }

    /// <summary>Transitive closure of input artifact ids (complete lineage).</summary>
    public IReadOnlyList<string> LineageClosure(string artifactId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(artifactId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id)) continue;
            var artifact = _store.TryLoad(id);
            if (artifact is null) continue;
            foreach (var input in artifact.InputArtifactIds)
                stack.Push(input);
            lock (_gate)
            {
                foreach (var edge in _edges.Where(e => e.ToArtifactId == id))
                    stack.Push(edge.FromArtifactId);
            }
        }
        return seen.ToList();
    }

    /// <summary>All sourceRefs across the lineage closure.</summary>
    public IReadOnlyList<string> SourceClosure(string artifactId)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in LineageClosure(artifactId))
        {
            var a = _store.TryLoad(id);
            if (a is null) continue;
            foreach (var s in a.SourceRefs) sources.Add(s);
        }
        return sources.ToList();
    }

    /// <summary>Effective restriction across lineage — local_only if any ancestor is local_only.</summary>
    public string EffectiveRestriction(string artifactId)
    {
        var restrictions = LineageClosure(artifactId)
            .Select(_store.TryLoad)
            .Where(a => a is not null)
            .Select(a => a!.Restriction);
        return ContentRestriction.Max(restrictions.DefaultIfEmpty(ContentRestriction.LocalOnly));
    }

    public string EffectiveRestriction(IEnumerable<string> artifactIds)
        => ContentRestriction.Max(artifactIds.Select(EffectiveRestriction));

    /// <summary>Project ids required for a package covering these artifacts.</summary>
    public IReadOnlyList<string> RequiredProjectIds(IEnumerable<string> artifactIds)
    {
        var projects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in artifactIds.SelectMany(LineageClosure).Distinct(StringComparer.Ordinal))
        {
            var a = _store.TryLoad(id);
            if (a is null) continue;
            foreach (var p in a.ProjectIds) projects.Add(p);
        }
        return projects.ToList();
    }

    /// <summary>Session ids required for a package covering these artifacts.</summary>
    public IReadOnlyList<string> RequiredSessionIds(IEnumerable<string> artifactIds)
    {
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in artifactIds.SelectMany(LineageClosure).Distinct(StringComparer.Ordinal))
        {
            var a = _store.TryLoad(id);
            if (a is null) continue;
            foreach (var s in a.SessionIds) sessions.Add(s);
        }
        return sessions.ToList();
    }
}
