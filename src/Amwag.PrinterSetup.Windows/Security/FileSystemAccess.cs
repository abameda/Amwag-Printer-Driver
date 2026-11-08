using System.Security.AccessControl;
using System.Security.Principal;

namespace Amwag.PrinterSetup.Windows.Security;

/// <summary>
/// Who may change or replace a file-system object, read from its security descriptor. Only Allow entries count (Deny entries
/// can only take rights away), and only for accounts the caller does not trust. An owner may always change the permissions,
/// so an untrusted owner counts as someone who may change the object.
/// </summary>
internal static class FileSystemAccess
{
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;

    /// <summary>
    /// Rights that change a file (content, attributes, permissions, owner) or delete it, and for a folder also add, delete or
    /// rename its entries.
    /// </summary>
    public const FileSystemRights Change =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericWrite | GenericAll;

    /// <summary>
    /// Rights that let an account rename or replace a folder or one of its entries: delete it, delete its entries, or change its
    /// permissions or owner. Creating new entries is not among them, because C:\ lets every user create folders.
    /// </summary>
    public const FileSystemRights Replace =
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership | GenericAll;

    /// <summary>
    /// Why an account outside <paramref name="trusted"/> may use <paramref name="rights"/> on <paramref name="path"/> (or is its
    /// owner), or null when none may. Entries that only apply to children (inherit-only, such as CREATOR OWNER) are skipped:
    /// they do not grant anything on the object itself.
    /// </summary>
    public static string? SecurityProblem(string path, FileSystemSecurity security, FileSystemRights rights, Func<SecurityIdentifier, bool> trusted)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !trusted(owner))
            return $"'{path}' is owned by {Describe(owner)}, not by an administrator account";

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & rights) == 0 ||
                rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
                continue;
            if (rule.IdentityReference is not SecurityIdentifier grantee || !trusted(grantee))
                return $"{Describe(rule.IdentityReference as SecurityIdentifier)} may change '{path}'";
        }

        return null;
    }

    /// <summary>
    /// The first junction or symbolic link among <paramref name="directory"/> and the folders above it, or null. Path lookups
    /// follow links, so a link anywhere above an object would let its owner redirect the path.
    /// </summary>
    public static string? LinkProblem(DirectoryInfo? directory)
    {
        for (DirectoryInfo? level = directory; level is not null; level = level.Parent)
        {
            // Attributes of a missing level read as all flags set, which also refuses it.
            if (level.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return $"'{level.FullName}' is a junction or symbolic link";
        }

        return null;
    }

    /// <summary>
    /// Why an account outside <paramref name="trusted"/> could rename or replace <paramref name="directory"/> or a folder above it
    /// up to the volume or share root (<see cref="Replace"/> rights, or ownership), or null when none could. Links are checked
    /// separately by <see cref="LinkProblem"/>.
    /// </summary>
    public static string? ReplaceProblem(DirectoryInfo? directory, Func<SecurityIdentifier, bool> trusted)
    {
        for (DirectoryInfo? level = directory; level is not null; level = level.Parent)
        {
            DirectorySecurity security = level.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            if (SecurityProblem(level.FullName, security, Replace, trusted) is { } problem)
                return problem;
        }

        return null;
    }

    /// <summary>The account name when Windows can resolve it, else the SID.</summary>
    public static string Describe(SecurityIdentifier? sid)
    {
        if (sid is null)
            return "no account";
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (SystemException)
        {
            // IdentityNotMappedException for an unknown account, or a failed lookup.
            return sid.Value;
        }
    }
}
