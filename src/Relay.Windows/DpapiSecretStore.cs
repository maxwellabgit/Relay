using System.Security.Cryptography;
using System.Text;
using Relay.Core.Model;
using Relay.Core.Storage;

namespace Relay.Windows;

/// <summary>
/// Secrets encrypted with the Windows Data Protection API in the current user's scope and an
/// installation-specific entropy value, stored one file per secret under <c>config\secrets</c>.
/// Only this Windows account on this machine can decrypt them; a copied file is useless elsewhere.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Relay.SecretStore.v1");
    private readonly string _directory;

    public DpapiSecretStore(DataRoot root)
    {
        _directory = root.SecretsDirectory;
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Secret names must be plain file-safe names.", nameof(name));
        return Path.Combine(_directory, name + ".bin");
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    public string? Get(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null; // another user or machine: unreadable by design
        }
    }

    public void Set(string name, string value)
    {
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        var path = PathFor(name);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Move(tmp, path, overwrite: true);
    }

    public bool Remove(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}
