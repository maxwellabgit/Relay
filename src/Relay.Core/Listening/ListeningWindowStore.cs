using System.Text.Json;
using Relay.Core.Ids;
using Relay.Core.Storage;
using Relay.Core.Time;

namespace Relay.Core.Listening;

/// <summary>Persists durable listening windows under <c>listening/windows/</c>.</summary>
public sealed class ListeningWindowStore
{
    private readonly DataRoot _root;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public ListeningWindowStore(DataRoot root, IClock clock)
    {
        _root = root;
        _clock = clock;
    }

    public string WindowsDirectory => Path.Combine(_root.Path, "listening", "windows");

    public string WindowPath(string windowId) => Path.Combine(WindowsDirectory, windowId + ".json");

    public ListeningWindow Save(ListeningWindow window)
    {
        lock (_gate)
        {
            window.UpdatedAt = _clock.UtcNow;
            Directory.CreateDirectory(WindowsDirectory);
            AtomicFile.WriteAllText(WindowPath(window.WindowId), JsonSerializer.Serialize(window, RelayJson.Indented));
            return window;
        }
    }

    public ListeningWindow? TryLoad(string windowId)
    {
        var text = AtomicFile.ReadAllTextIfExists(WindowPath(windowId));
        return text is null ? null : JsonSerializer.Deserialize<ListeningWindow>(text, RelayJson.Indented);
    }

    public IReadOnlyList<ListeningWindow> ListBySession(string sessionId)
    {
        if (!Directory.Exists(WindowsDirectory)) return [];
        var list = new List<ListeningWindow>();
        foreach (var file in Directory.GetFiles(WindowsDirectory, "*.json"))
        {
            var text = AtomicFile.ReadAllTextIfExists(file);
            if (text is null) continue;
            var w = JsonSerializer.Deserialize<ListeningWindow>(text, RelayJson.Indented);
            if (w is not null && w.SessionId == sessionId)
                list.Add(w);
        }
        return list.OrderBy(w => w.CreatedAt).ToList();
    }

    public IReadOnlyList<ListeningWindow> ListPending(string sessionId)
        => ListBySession(sessionId)
            .Where(w => w.Status is ListeningWindowStatus.Pending or ListeningWindowStatus.Deferred or ListeningWindowStatus.Processing)
            .OrderBy(w => w.CreatedAt)
            .ToList();

    public ListeningWindow Create(
        string sessionId,
        string? caseId,
        ListeningCoverage primary,
        ListeningCoverage? context = null,
        string text = "",
        IReadOnlyDictionary<string, string>? decisionVersions = null)
    {
        var window = new ListeningWindow
        {
            WindowId = Ulid.NewUlid(_clock.UtcNow),
            SessionId = sessionId,
            CaseId = caseId,
            Primary = primary,
            Context = context ?? new ListeningCoverage { Role = CoverageRoles.Context },
            Text = text,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
            DecisionDefinitionVersions = decisionVersions is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(decisionVersions, StringComparer.Ordinal),
        };
        return Save(window);
    }
}
