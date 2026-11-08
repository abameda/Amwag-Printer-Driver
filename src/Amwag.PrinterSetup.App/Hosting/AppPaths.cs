using System.Security.AccessControl;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Windows.Drivers;

namespace Amwag.PrinterSetup.App.Hosting;

/// <summary>
/// Data folders. Elevated runs (worker, silent IT deployment) write under %ProgramData%\AMWAG\PrinterSetup, which they secure
/// level by level so only administrators control it. Unelevated runs never create anything there: an employee-owned folder
/// under %ProgramData% would let a same-user process redirect later elevated writes. They use %LOCALAPPDATA%\AMWAG\PrinterSetup.
/// </summary>
internal static class AppPaths
{
    public static string MachineBase { get; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static string UserBase { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DataRoot { get; } = Path.Combine(MachineBase, "AMWAG", "PrinterSetup");

    public static string LogsDirectory => Path.Combine(DataRoot, "Logs");

    public static string ReportsDirectory => Path.Combine(DataRoot, "Reports");

    public static string UserLogsDirectory => Path.Combine(UserBase, "AMWAG", "PrinterSetup", "Logs");

    public static string UserReportsDirectory => Path.Combine(UserBase, "AMWAG", "PrinterSetup", "Reports");

    /// <summary>Where this process may write logs, in order of preference.</summary>
    public static (string Base, string Directory)[] LogLocations =>
        Environment.IsPrivilegedProcess
            ? [(MachineBase, LogsDirectory), (UserBase, UserLogsDirectory)]
            : [(UserBase, UserLogsDirectory)];

    /// <summary>Where this process may write reports, in order of preference.</summary>
    public static (string Base, string Directory)[] ReportLocations =>
        Environment.IsPrivilegedProcess
            ? [(MachineBase, ReportsDirectory), (UserBase, UserReportsDirectory)]
            : [(UserBase, UserReportsDirectory)];

    /// <summary>
    /// Prepares a log or report folder: below %ProgramData% every level is secured for administrators
    /// (<see cref="ProtectedDirectory.EnsureChain"/>); elsewhere it is created without following links.
    /// </summary>
    public static bool TryPrepareDirectory(string baseDirectory, string directory)
    {
        if (!string.Equals(Path.GetFullPath(baseDirectory), Path.GetFullPath(MachineBase), StringComparison.OrdinalIgnoreCase))
            return TryCreateDirectoryWithoutLinks(baseDirectory, directory);

        try
        {
            ProtectedDirectory.EnsureChain(baseDirectory, directory, harden: Environment.IsPrivilegedProcess, NullLog.Instance);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or System.Security.Principal.IdentityNotMappedException or PrivilegeNotHeldException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates <paramref name="directory"/> one level at a time below <paramref name="baseDirectory"/> and returns false when that
    /// fails or when an existing level is a junction or symbolic link. ProgramData subfolders are writable by standard users, so an
    /// elevated process must not follow a link planted there into a system folder.
    /// </summary>
    public static bool TryCreateDirectoryWithoutLinks(string baseDirectory, string directory)
    {
        try
        {
            string root = Path.GetFullPath(baseDirectory);
            string relative = Path.GetRelativePath(root, Path.GetFullPath(directory));
            if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return false;

            string current = root;
            foreach (string part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var info = new DirectoryInfo(current);
                if (!info.Exists)
                    info.Create();

                info.Refresh();
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
