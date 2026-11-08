using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

/// <summary>
/// Creates directory junctions (mount points) for tests. Standard users may create them, unlike symbolic links. Delete them
/// with <see cref="Directory.Delete(string)"/> before the folder around them: a recursive delete that meets a junction also
/// asks the mount manager to forget it, which a standard user may not.
/// </summary>
internal static class Junction
{
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    /// <summary>Creates <paramref name="link"/> as a junction to the existing folder <paramref name="target"/>.</summary>
    public static void Create(string link, string target)
    {
        Directory.CreateDirectory(link);
        string fullTarget = Path.GetFullPath(target);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + fullTarget);
        byte[] print = Encoding.Unicode.GetBytes(fullTarget);

        // REPARSE_DATA_BUFFER: tag, data length, reserved; then MountPointReparseBuffer: substitute and print name offsets and
        // lengths, and both names, each terminated by a NUL character.
        int names = substitute.Length + 2 + print.Length + 2;
        var buffer = new byte[16 + names];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), IoReparseTagMountPoint);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)(8 + names));
        BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)substitute.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)(substitute.Length + 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)print.Length);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);

        using SafeFileHandle handle = CreateFileW(link, GenericWrite, 0, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception();
        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, byte[] inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
