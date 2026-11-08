using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using Amwag.PrinterSetup.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Amwag.PrinterSetup.Windows.Security;

/// <summary>
/// Whether only administrators can change a file. An elevated run may only use a configuration file next to the program when
/// this holds: the UAC prompt vouches for the program, not for files a standard user could have edited. The answer is the
/// same in every process: the current user is never trusted, elevated or not, so the window and the elevated worker always
/// load the same files.
/// </summary>
public static class AdministratorControlledFile
{
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>
    /// Null when only administrator accounts can change or replace the open <paramref name="file"/>; otherwise why not. All of
    /// this must hold: the volume keeps permissions (not a FAT or exFAT stick); no level of the path is a junction or link; the
    /// file and its folder are owned by an administrator account; no other account may write to the file or add, delete or
    /// rename entries in its folder; and no other account owns, deletes, re-permissions or renames a folder above it, up to the
    /// volume or share root. Administrator accounts are SYSTEM, BUILTIN\Administrators, TrustedInstaller and a domain's Domain
    /// Admins or Enterprise Admins. Fails closed: any error counts as "not protected". The caller reads the file through the
    /// same handle, opened sharing only reads, so it cannot change between this check and that read.
    /// </summary>
    public static string? FindProblem(FileStream file)
    {
        try
        {
            return Check(file);
        }
        catch (SystemException ex)
        {
            // Fail closed: permissions that cannot be read do not prove anything.
            return $"its permissions could not be checked ({ex.Message})";
        }
    }

    /// <summary>SYSTEM, BUILTIN\Administrators, TrustedInstaller, or a domain's Domain Admins (RID 512) or Enterprise Admins (RID 519).</summary>
    internal static bool IsAdministratorAccount(SecurityIdentifier sid)
    {
        if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid == TrustedInstaller)
            return true;

        SecurityIdentifier? domain = sid.AccountDomainSid;
        return domain is not null &&
            (sid == new SecurityIdentifier(WellKnownSidType.AccountDomainAdminsSid, domain) ||
             sid == new SecurityIdentifier(WellKnownSidType.AccountEnterpriseAdminsSid, domain));
    }

    private static string? Check(FileStream file)
    {
        string path = Path.GetFullPath(file.Name);
        string fileSystem = FileSystemName(file.SafeFileHandle, out bool persistentAcls);
        if (!persistentAcls)
            return $"'{path}' is on a drive that does not keep file permissions ({fileSystem})";

        DirectoryInfo folder = new FileInfo(path).Directory ?? throw new IOException($"'{path}' has no folder.");
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            return $"'{path}' is a symbolic link";
        if (FileSystemAccess.LinkProblem(folder) is { } link)
            return link;

        FileSecurity security = file.GetAccessControl();
        if (FileSystemAccess.SecurityProblem(path, security, FileSystemAccess.Change, IsAdministratorAccount) is { } fileProblem)
            return fileProblem;

        DirectorySecurity folderSecurity = folder.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        return FileSystemAccess.SecurityProblem(folder.FullName, folderSecurity, FileSystemAccess.Change, IsAdministratorAccount)
            ?? FileSystemAccess.ReplaceProblem(folder.Parent, IsAdministratorAccount);
    }

    /// <summary>The file system's name (NTFS, exFAT, ...), and whether it keeps permissions.</summary>
    private static string FileSystemName(SafeFileHandle file, out bool persistentAcls)
    {
        var name = new char[64];
        if (!Kernel32.GetVolumeInformationByHandleW(file, null, 0, out _, out _, out uint flags, name, (uint)name.Length))
            throw new Win32Exception();

        persistentAcls = (flags & Kernel32.FILE_PERSISTENT_ACLS) != 0;
        int length = Array.IndexOf(name, '\0');
        return new string(name, 0, length < 0 ? name.Length : length);
    }
}
