using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Windows.Security;

namespace Amwag.PrinterSetup.Windows.Drivers;

/// <summary>
/// Folders an elevated process writes into or deletes from (driver cache and extraction, logs, reports). When hardened
/// (elevated runs), they carry an explicit DACL without inheritance: SYSTEM and Administrators full control, Users read and
/// execute, owned by Administrators. That keeps standard users from swapping extracted files before installation, and from
/// planting links that would redirect a privileged write or recursive delete. Without hardening (unelevated runs and tests)
/// plain folders are used.
/// </summary>
public static class ProtectedDirectory
{
    private const FileSystemRights WriteRights = FileSystemAccess.Change;

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    // Under UAC consent the elevated token carries the employee's own user SID, so ownership by "the current user" proves
    // nothing about who can change a folder. Only unelevated runs (tests) accept their own folders.
    private static readonly bool ProcessIsElevated = Environment.IsPrivilegedProcess;

    /// <summary>
    /// <see cref="EnsureRoot"/> for every level from <paramref name="baseDirectory"/> (exclusive, e.g. %ProgramData%) down to
    /// <paramref name="path"/>, so a standard user cannot rename or replace any parent of a folder an elevated process uses.
    /// </summary>
    public static void EnsureChain(string baseDirectory, string path, bool harden, ILog log)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        string relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException($"'{path}' is not below '{baseDirectory}'.", nameof(path));

        string current = root;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            EnsureRoot(current, harden, log);
        }
    }

    /// <summary>
    /// Makes sure a tool-owned folder exists. Junctions and symbolic links are refused. When hardened, a folder that is not
    /// owned by an administrator is moved aside (never deleted) and recreated, and a DACL that is inherited or lets anyone else
    /// write is replaced. A folder this call creates is checked the same way afterwards, because another account may have
    /// created it (or a link in its place) in between.
    /// </summary>
    public static void EnsureRoot(string path, bool harden, ILog log)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(directory.FullName)!);
            CreateAndVerify(directory, harden);
            return;
        }

        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"The folder '{path}' is a junction or symbolic link and is not used.");

        if (!harden)
            return;

        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!IsTrustedOwner(security.GetOwner(typeof(SecurityIdentifier))))
        {
            string aside = $"{directory.FullName}.untrusted-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
            Directory.Move(directory.FullName, aside);
            log.Warn("driver.package.folder.replaced", "A tool folder not owned by an administrator was moved aside and recreated.",
                new Dictionary<string, object?> { ["path"] = directory.FullName, ["movedTo"] = aside });
            CreateAndVerify(new DirectoryInfo(directory.FullName), harden: true);
            return;
        }

        if (!security.AreAccessRulesProtected || GrantsWriteToOthers(security))
            directory.SetAccessControl(CreateSecurity());
    }

    /// <summary>Creates a folder that must not exist yet; when hardened, verifies afterwards that nobody else created it first.</summary>
    public static void CreateNew(string path, bool harden)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists || File.Exists(path))
            throw new IOException($"'{path}' already exists.");

        CreateAndVerify(directory, harden);
    }

    /// <summary>
    /// Whether this process may delete the folder recursively. A hardened process only deletes folders that standard users
    /// cannot change, so nothing inside can be swapped for a link while the delete runs, and only when no folder above it is a
    /// link or can be renamed or replaced by them either: that would redirect the delete (callers secure that chain with
    /// <see cref="EnsureChain"/>).
    /// </summary>
    public static bool IsSafeToDelete(string path, bool harden)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;

        if (!harden)
            return true;

        return IsSecured(directory) &&
            FileSystemAccess.LinkProblem(directory.Parent) is null &&
            FileSystemAccess.ReplaceProblem(directory.Parent, IsTrusted) is null;
    }

    /// <summary>The folder itself is hardened: owned by an administrator, with a protected DACL that lets nobody else write.</summary>
    internal static bool IsSecured(DirectoryInfo directory)
    {
        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        return security.AreAccessRulesProtected && IsTrustedOwner(security.GetOwner(typeof(SecurityIdentifier))) && !GrantsWriteToOthers(security);
    }

    /// <summary>
    /// Creates the folder, then checks what is actually there: <see cref="DirectoryInfo.Create()"/> succeeds without applying
    /// the security when the folder already exists, so a folder, junction or link another account put there in between is
    /// refused, and a DACL that is not ours is replaced.
    /// </summary>
    internal static void CreateAndVerify(DirectoryInfo directory, bool harden)
    {
        Create(directory, harden);
        directory.Refresh();
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"The folder '{directory.FullName}' is a junction or symbolic link and is not used.");

        if (!harden)
            return;

        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!IsTrustedOwner(security.GetOwner(typeof(SecurityIdentifier))))
            throw new IOException($"'{directory.FullName}' was created by another account and is not used.");
        if (!security.AreAccessRulesProtected || GrantsWriteToOthers(security))
            directory.SetAccessControl(CreateSecurity());
    }

    // Inherit-only entries count here: they decide who may write to what is created inside the folder later.
    private static bool GrantsWriteToOthers(DirectorySecurity security)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & WriteRights) != 0 &&
                !IsTrustedOwner(rule.IdentityReference))
                return true;
        }

        return false;
    }

    private static void Create(DirectoryInfo directory, bool harden)
    {
        if (harden)
            directory.Create(CreateSecurity());
        else
            directory.Create();
    }

    private static DirectorySecurity CreateSecurity()
    {
        var security = new DirectorySecurity();
        if (ProcessIsElevated)
            security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static bool IsTrustedOwner(IdentityReference? identity) => identity is SecurityIdentifier sid && IsTrusted(sid);

    private static bool IsTrusted(SecurityIdentifier sid)
    {
        if (sid == LocalSystem || sid == Administrators || sid == TrustedInstaller)
            return true;
        if (ProcessIsElevated)
            return false;

        using var current = WindowsIdentity.GetCurrent();
        return sid == current.User;
    }
}
