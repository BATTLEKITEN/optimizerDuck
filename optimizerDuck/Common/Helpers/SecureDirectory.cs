using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace optimizerDuck.Common.Helpers;

/// <summary>
///     A directory only Administrators and SYSTEM can change. Files the elevated app runs or
///     imports go here, never into the per-user data folder, which any process of the user can
///     rewrite between the moment the app writes a file and the moment it runs it.
/// </summary>
public static class SecureDirectory
{
    private const FileSystemRights OwnerRights =
        FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

    private static readonly SecurityIdentifier Administrators = new(
        WellKnownSidType.BuiltinAdministratorsSid,
        null
    );

    private static readonly SecurityIdentifier LocalSystem = new(
        WellKnownSidType.LocalSystemSid,
        null
    );

    // OWNER RIGHTS replaces the implicit WRITE_DAC an owner always has, so whoever ends up
    // owning an entry still cannot widen its access.
    private static readonly SecurityIdentifier OwnerRightsSid = new("S-1-3-4");

    /// <summary>
    ///     Makes sure <paramref name="path" /> exists with an administrators-only descriptor.
    ///     A directory that exists with any other descriptor was created by someone else and is
    ///     moved aside, never trusted or reused.
    /// </summary>
    /// <returns><see langword="true" /> when the directory is ready.</returns>
    public static bool EnsureAdminOnly(string path, ILogger? logger = null)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (directory.Exists)
            {
                if (IsAdminOnly(directory))
                    return true;

                var aside = $"{path}.untrusted-{DateTime.UtcNow:yyyyMMddHHmmss}";
                logger?.LogWarning(
                    "{Path} is not restricted to administrators; moved aside to {Aside}",
                    path,
                    aside
                );
                Directory.Move(path, aside);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            directory.Create(BuildSecurity());
            return IsAdminOnly(new DirectoryInfo(path));
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Could not prepare the protected directory {Path}", path);
            return false;
        }
    }

    /// <summary>
    ///     Whether the directory is not a link, is owned by Administrators or SYSTEM, does not
    ///     inherit, and grants anything beyond read only to Administrators and SYSTEM.
    /// </summary>
    public static bool IsAdminOnly(DirectoryInfo directory)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;

        var security = directory.GetAccessControl();
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (owner != Administrators && owner != LocalSystem))
            return false;

        if (!security.AreAccessRulesProtected)
            return false;

        foreach (
            FileSystemAccessRule rule in security.GetAccessRules(
                true,
                true,
                typeof(SecurityIdentifier)
            )
        )
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (sid == Administrators || sid == LocalSystem)
                continue;
            if ((rule.FileSystemRights & ~(OwnerRights | FileSystemRights.ReadPermissions)) != 0)
                return false;
        }

        return true;
    }

    private static DirectorySecurity BuildSecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(
            new FileSystemAccessRule(
                Administrators,
                FileSystemRights.FullControl,
                inherit,
                PropagationFlags.None,
                AccessControlType.Allow
            )
        );
        security.AddAccessRule(
            new FileSystemAccessRule(
                LocalSystem,
                FileSystemRights.FullControl,
                inherit,
                PropagationFlags.None,
                AccessControlType.Allow
            )
        );
        security.AddAccessRule(
            new FileSystemAccessRule(
                OwnerRightsSid,
                OwnerRights,
                inherit,
                PropagationFlags.None,
                AccessControlType.Allow
            )
        );
        return security;
    }
}
