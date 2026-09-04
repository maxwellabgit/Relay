using System.Text;

namespace Relay.Core.Storage;

/// <summary>
/// Crash-safe file replacement: write to a sibling temp file, flush to disk, then atomically
/// move it over the destination. A reader never observes a half-written file.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));
    }

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    public static string? ReadAllTextIfExists(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    }
}
