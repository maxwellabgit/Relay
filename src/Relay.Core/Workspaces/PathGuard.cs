namespace Relay.Core.Workspaces;

public sealed record PathCheck(bool Ok, string Canonical, string? Root, string? Reason);

/// <summary>
/// The only way a path reaches the filesystem on behalf of a proposal. Canonicalizes the path,
/// resolves reparse points on the deepest existing ancestor, and requires the result to sit
/// under one registered root. Symlink, junction, traversal, device-path, alternate-stream and
/// case-folding escapes all fail here rather than in the executor.
/// </summary>
public static class PathGuard
{
    public static string Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is empty.", nameof(path));
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("Device and extended-length paths are not allowed.", nameof(path));

        var full = Path.GetFullPath(path);
        var withoutRoot = full.Length > 2 && full[1] == ':' ? full[2..] : full;
        if (withoutRoot.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("Alternate data streams are not allowed.", nameof(path));

        // Resolve reparse points on the deepest ancestor that exists, then re-append the rest.
        var existing = full;
        var remainder = new Stack<string>();
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null) break;
            remainder.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = existing;
        if (Directory.Exists(existing) || File.Exists(existing))
        {
            resolved = ResolveChain(existing);
        }
        foreach (var part in remainder) resolved = Path.Combine(resolved, part);
        return TrimSeparators(Path.GetFullPath(resolved));
    }

    private static string ResolveChain(string path)
    {
        // Every ancestor may itself be a reparse point; resolve from the drive root downward.
        var root = Path.GetPathRoot(path) ?? path;
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        if (relative == ".") return current;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null) current = target.FullName;
            }
            else if (File.Exists(current))
            {
                var file = new FileInfo(current);
                if (file.LinkTarget is not null)
                {
                    var target = file.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is not null) current = target.FullName;
                }
            }
        }
        return current;
    }

    private static string TrimSeparators(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length >= 2 && trimmed[1] == ':' && trimmed.Length == 2 ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }

    public static bool IsWithin(string canonicalCandidate, string canonicalRoot)
    {
        var root = TrimSeparators(canonicalRoot);
        if (string.Equals(canonicalCandidate, root, StringComparison.OrdinalIgnoreCase)) return true;
        return canonicalCandidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canonicalizes and checks containment against the registered roots (which must already be canonical).</summary>
    public static PathCheck Check(string path, IEnumerable<string> canonicalRoots)
    {
        string canonical;
        try { canonical = Canonicalize(path); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return new PathCheck(false, path, null, ex.Message);
        }
        foreach (var root in canonicalRoots)
        {
            if (IsWithin(canonical, root)) return new PathCheck(true, canonical, root, null);
        }
        return new PathCheck(false, canonical, null, "Path is outside every registered workspace root.");
    }
}
