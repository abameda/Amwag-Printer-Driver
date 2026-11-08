using System.Security.AccessControl;
using System.Security.Principal;
using Amwag.PrinterSetup.Windows.Drivers;
using Amwag.PrinterSetup.Windows.Security;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

/// <summary>
/// What another account can put in place of a folder the hardening creates or deletes: a junction or folder created between the
/// existence check and the create, or a parent that lets others rename or replace what is inside. Unelevated, inside the test's
/// own temporary folder (hardened folders stay empty, so the test can delete them again).
/// </summary>
[Trait("Category", "WindowsIntegration")]
public sealed class ProtectedDirectoryChainTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly RecordingLog _log = new();
    private readonly List<string> _junctions = [];

    public void Dispose()
    {
        foreach (string junction in _junctions)
            Directory.Delete(junction);
        _folders.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_junction_that_appears_where_a_folder_is_created_is_refused(bool harden)
    {
        // DirectoryInfo.Create succeeds on a path that already exists, so a junction planted between the existence check and
        // the create would otherwise be used as the new folder (and hardening would follow it to the target).
        string target = Directory.CreateDirectory(Path.Combine(_folders.Root, "target")).FullName;
        string path = Path.Combine(_folders.Root, "AMWAG");
        CreateJunction(path, target);

        Assert.Throws<IOException>(() => ProtectedDirectory.CreateAndVerify(new DirectoryInfo(path), harden));

        Assert.False(new DirectoryInfo(target).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected);
    }

    [Fact]
    public void A_folder_that_appears_where_a_folder_is_created_is_secured_like_a_new_one()
    {
        // Created in between by an account the hardening trusts (unelevated: this user), with the inherited DACL.
        string path = Directory.CreateDirectory(Path.Combine(_folders.Root, "AMWAG")).FullName;
        Assert.False(ProtectedDirectory.IsSecured(new DirectoryInfo(path)));

        ProtectedDirectory.CreateAndVerify(new DirectoryInfo(path), harden: true);

        Assert.True(ProtectedDirectory.IsSecured(new DirectoryInfo(path)));
    }

    [Fact]
    public void A_missing_root_is_created_secured()
    {
        string path = Path.Combine(_folders.Root, "AMWAG");

        ProtectedDirectory.EnsureRoot(path, harden: true, _log);

        Assert.True(ProtectedDirectory.IsSecured(new DirectoryInfo(path)));
    }

    [Fact]
    public void An_existing_junction_is_never_used_as_a_root()
    {
        string target = Directory.CreateDirectory(Path.Combine(_folders.Root, "target")).FullName;
        string path = Path.Combine(_folders.Root, "AMWAG");
        CreateJunction(path, target);

        Assert.Throws<IOException>(() => ProtectedDirectory.EnsureRoot(path, harden: true, _log));
    }

    [Fact]
    public void A_secured_folder_reached_through_a_junction_is_not_safe_to_delete()
    {
        string real = Directory.CreateDirectory(Path.Combine(_folders.Root, "real")).FullName;
        ProtectedDirectory.CreateNew(Path.Combine(real, "old"), harden: true);
        string link = Path.Combine(_folders.Root, "link");
        CreateJunction(link, real);
        string throughLink = Path.Combine(link, "old");

        // The folder itself looks secured through the link; the link above it is what redirects the delete.
        Assert.True(ProtectedDirectory.IsSecured(new DirectoryInfo(throughLink)));
        Assert.False(ProtectedDirectory.IsSafeToDelete(throughLink, harden: true));
    }

    [Fact]
    public void A_secured_folder_is_not_safe_to_delete_when_its_parent_lets_others_delete_entries()
    {
        var parent = Directory.CreateDirectory(Path.Combine(_folders.Root, "Packages"));
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        DirectorySecurity parentSecurity = parent.GetAccessControl();
        parentSecurity.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
        parent.SetAccessControl(parentSecurity);
        string child = Path.Combine(parent.FullName, "old");
        ProtectedDirectory.CreateNew(child, harden: true);

        Assert.True(ProtectedDirectory.IsSecured(new DirectoryInfo(child)));
        Assert.False(ProtectedDirectory.IsSafeToDelete(child, harden: true));
        string? problem = FileSystemAccess.SecurityProblem(parent.FullName, parent.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access),
            FileSystemAccess.Replace, TrustedWhenUnelevated);
        Assert.Contains(everyone.Translate(typeof(NTAccount)).Value, problem);
    }

    [Fact]
    public void Without_hardening_only_the_folder_itself_must_not_be_a_link()
    {
        string real = Directory.CreateDirectory(Path.Combine(_folders.Root, "real", "old")).FullName;
        string link = Path.Combine(_folders.Root, "link");
        CreateJunction(link, Path.GetDirectoryName(real)!);

        Assert.True(ProtectedDirectory.IsSafeToDelete(real, harden: false));
        Assert.False(ProtectedDirectory.IsSafeToDelete(link, harden: false));
    }

    private void CreateJunction(string link, string target)
    {
        Junction.Create(link, target);
        _junctions.Add(link);
    }

    // What ProtectedDirectory trusts in an unelevated run: SYSTEM, Administrators, TrustedInstaller and the current user.
    private static bool TrustedWhenUnelevated(SecurityIdentifier sid)
    {
        using var current = WindowsIdentity.GetCurrent();
        return sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
            sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" || sid == current.User;
    }
}
