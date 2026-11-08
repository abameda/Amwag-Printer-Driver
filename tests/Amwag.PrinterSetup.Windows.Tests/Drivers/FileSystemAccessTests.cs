using System.Security.AccessControl;
using System.Security.Principal;
using Amwag.PrinterSetup.Windows.Security;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

/// <summary>
/// The rules of the "only administrators can change it" check, on security descriptors written as SDDL (no file system).
/// Rights in hexadecimal: 0x2 WriteData (create file), 0x4 AppendData (create folder), 0x40 DeleteChild, 0x10000 Delete,
/// 0x40000 WriteDAC, 0x80000 WriteOwner, 0x1200a9 ReadAndExecute, GA/GW generic all/write.
/// </summary>
public sealed class FileSystemAccessTests
{
    private const string Admins = "(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";
    private const string Domain = "S-1-5-21-1004336348-1177238915-682003330";

    public static TheoryData<string, bool, bool> Descriptors => new()
    {
        // SDDL, may change (file and its folder), may replace (folder above)
        { $"O:BAD:P{Admins}", false, false },
        { $"O:SYD:P{Admins}", false, false },
        { $"O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464D:P{Admins}", false, false },
        { $"O:{Domain}-512D:P{Admins}(A;OICI;FA;;;{Domain}-512)", false, false },
        { $"O:BAD:P{Admins}(A;;FA;;;{Domain}-519)", false, false },
        // An owner may always change the permissions.
        { $"O:{Domain}-1105D:P{Admins}", true, true },
        { $"O:{Domain}-500D:P{Admins}", true, true },
        // Creating files lets a user add a file that is not there yet; creating folders is what C:\ allows everyone.
        { $"O:BAD:P{Admins}(A;;0x2;;;BU)", true, false },
        { $"O:BAD:P{Admins}(A;;0x4;;;AU)", true, false },
        { $"O:BAD:P{Admins}(A;;0x10000;;;BU)", true, true },
        { $"O:BAD:P{Admins}(A;;0x40;;;BU)", true, true },
        { $"O:BAD:P{Admins}(A;;0x40000;;;IU)", true, true },
        { $"O:BAD:P{Admins}(A;;0x80000;;;WD)", true, true },
        { $"O:BAD:P{Admins}(A;;GA;;;BU)", true, true },
        { $"O:BAD:P{Admins}(A;;GW;;;BU)", true, false },
        { $"O:BAD:P{Admins}(A;;FA;;;{Domain}-513)", true, true },
        // Entries that only apply to what is created inside (CREATOR OWNER and the like) grant nothing on the object itself.
        { $"O:BAD:P{Admins}(A;OICIIO;GA;;;CO)(A;OICIIO;FA;;;BU)", false, false },
        // Deny entries only take rights away.
        { $"O:BAD:P{Admins}(D;;FA;;;BU)", false, false },
        // Reading and executing is fine for everyone.
        { $"O:BAD:P{Admins}(A;;0x1200a9;;;WD)(A;;0x1200a9;;;AN)", false, false },
    };

    [Theory]
    [MemberData(nameof(Descriptors))]
    public void Only_administrator_accounts_may_change_or_replace(string sddl, bool mayChange, bool mayReplace)
    {
        DirectorySecurity security = FromSddl(sddl);

        string? change = FileSystemAccess.SecurityProblem(@"C:\X", security, FileSystemAccess.Change, AdministratorControlledFile.IsAdministratorAccount);
        string? replace = FileSystemAccess.SecurityProblem(@"C:\X", security, FileSystemAccess.Replace, AdministratorControlledFile.IsAdministratorAccount);

        Assert.Equal(mayChange, change is not null);
        Assert.Equal(mayReplace, replace is not null);
    }

    [Fact]
    public void The_problem_names_the_object_and_the_account()
    {
        DirectorySecurity security = FromSddl($"O:BAD:P{Admins}(A;;0x10000;;;BU)");

        string? problem = FileSystemAccess.SecurityProblem(@"C:\Branch\Setup", security, FileSystemAccess.Change, AdministratorControlledFile.IsAdministratorAccount);

        Assert.NotNull(problem);
        Assert.Contains(@"C:\Branch\Setup", problem);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Translate(typeof(NTAccount)).Value, problem);
    }

    [Fact]
    public void The_current_user_is_never_an_administrator_account_elevated_or_not()
    {
        using var identity = WindowsIdentity.GetCurrent();
        DirectorySecurity security = FromSddl($"O:{identity.User!.Value}D:P{Admins}");

        Assert.False(AdministratorControlledFile.IsAdministratorAccount(identity.User));
        Assert.NotNull(FileSystemAccess.SecurityProblem(@"C:\X", security, FileSystemAccess.Change, AdministratorControlledFile.IsAdministratorAccount));
    }

    [Theory]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-32-544", true)]
    [InlineData("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", true)]
    [InlineData(Domain + "-512", true)]
    [InlineData(Domain + "-519", true)]
    [InlineData(Domain + "-500", false)]
    [InlineData(Domain + "-513", false)]
    [InlineData(Domain + "-1512", false)]
    [InlineData("S-1-5-32-545", false)]
    [InlineData("S-1-5-11", false)]
    [InlineData("S-1-1-0", false)]
    [InlineData("S-1-3-0", false)]
    [InlineData("S-1-3-4", false)]
    public void Administrator_accounts(string sid, bool expected)
    {
        Assert.Equal(expected, AdministratorControlledFile.IsAdministratorAccount(new SecurityIdentifier(sid)));
    }

    private static DirectorySecurity FromSddl(string sddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return security;
    }
}
