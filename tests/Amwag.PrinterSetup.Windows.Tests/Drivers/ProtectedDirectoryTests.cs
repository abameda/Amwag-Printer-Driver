using System.Security.AccessControl;
using System.Security.Principal;
using Amwag.PrinterSetup.Windows.Drivers;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

/// <summary>
/// Hardened folders created without elevation inside the test's own temporary folder (the current user owns them, which
/// the hardening accepts). Nothing outside that folder is touched.
/// </summary>
[Trait("Category", "WindowsIntegration")]
public sealed class ProtectedDirectoryTests : IDisposable
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private readonly TempFolders _folders = new();
    private readonly RecordingLog _log = new();

    public void Dispose() => _folders.Dispose();

    [Fact]
    public void HardenedFolderHasAnExplicitAdministratorsOnlyDacl()
    {
        string path = Path.Combine(_folders.Root, "hardened");

        ProtectedDirectory.CreateNew(path, harden: true);

        DirectorySecurity security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToDictionary(r => (SecurityIdentifier)r.IdentityReference, r => r);
        Assert.Equal(3, rules.Count);
        Assert.Equal(FileSystemRights.FullControl, rules[LocalSystem].FileSystemRights);
        Assert.Equal(FileSystemRights.FullControl, rules[Administrators].FileSystemRights);
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, rules[Users].FileSystemRights);
        Assert.All(rules.Values, r => Assert.False(r.IsInherited));
        Assert.All(rules.Values, r => Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, r.InheritanceFlags));
        Assert.True(ProtectedDirectory.IsSecured(new DirectoryInfo(path)));
    }

    [Fact]
    public void ExistingFolderIsNeverReusedAsANewOne()
    {
        string path = Directory.CreateDirectory(Path.Combine(_folders.Root, "existing")).FullName;

        Assert.Throws<IOException>(() => ProtectedDirectory.CreateNew(path, harden: false));
    }

    [Fact]
    public void InheritedDaclOfAnOwnedRootIsReplaced()
    {
        string path = Directory.CreateDirectory(Path.Combine(_folders.Root, "root")).FullName;
        Assert.False(ProtectedDirectory.IsSecured(new DirectoryInfo(path)));

        ProtectedDirectory.EnsureRoot(path, harden: true, _log);

        Assert.True(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected);
        Assert.True(ProtectedDirectory.IsSecured(new DirectoryInfo(path)));
    }

    [Fact]
    public void ChainSecuresEveryLevelBelowTheBaseNotJustTheLeaf()
    {
        // Pre-existing levels with inherited DACLs. (Unelevated, a hardened parent no longer lets this test create children;
        // elevated runs keep full control through Administrators.)
        string path = Directory.CreateDirectory(Path.Combine(_folders.Root, "AMWAG", "PrinterSetup", "Logs")).FullName;
        string amwag = Path.Combine(_folders.Root, "AMWAG");

        ProtectedDirectory.EnsureChain(_folders.Root, path, harden: true, _log);

        Assert.All(new[] { amwag, Path.Combine(amwag, "PrinterSetup"), path }, level => Assert.True(ProtectedDirectory.IsSecured(new DirectoryInfo(level)), level));
    }

    [Fact]
    public void ChainRefusesAPathOutsideItsBase()
    {
        Assert.Throws<ArgumentException>(() =>
            ProtectedDirectory.EnsureChain(Path.Combine(_folders.Root, "a"), Path.Combine(_folders.Root, "b"), harden: false, _log));
    }

    [Fact]
    public void MissingRootIsCreatedTogetherWithItsParents()
    {
        string path = Path.Combine(_folders.Root, "a", "b", "root");

        ProtectedDirectory.EnsureRoot(path, harden: false, _log);

        Assert.True(Directory.Exists(path));
    }
}
