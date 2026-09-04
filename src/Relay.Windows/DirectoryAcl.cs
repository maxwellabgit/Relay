using System.Security.AccessControl;
using System.Security.Principal;

namespace Relay.Windows;

/// <summary>
/// Restricts the data root to the current user and SYSTEM with inheritance disabled
/// (contract §14). Other local accounts, including other users on a shared machine, cannot read
/// the ledger, drafts, or settings.
/// </summary>
public static class DirectoryAcl
{
    public static (bool Applied, string? Error) Harden(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("Current user SID unavailable.");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                security.RemoveAccessRule(rule);
            }
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
            return (true, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException)
        {
            return (false, ex.Message);
        }
    }
}
