using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Amwag.PrinterSetup.Windows.Interop;

/// <summary>Volume and named-pipe queries (kernel32.dll; values from the Windows SDK 10.0.26100 winnt.h).</summary>
internal static class Kernel32
{
    private const string Library = "kernel32.dll";

    /// <summary>The file system keeps and enforces access control lists (NTFS, ReFS; not FAT or exFAT).</summary>
    internal const uint FILE_PERSISTENT_ACLS = 0x00000008;

    /// <summary>
    /// Volume information for the volume an open file is on (also for a file on a share). Buffers that are not needed are passed
    /// as null with size 0.
    /// </summary>
    [DllImport(Library, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeInformationByHandleW(
        SafeFileHandle file,
        [Out] char[]? volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        [Out] char[]? fileSystemNameBuffer,
        uint fileSystemNameSize);

    /// <summary>The process id of the client connected to a named pipe server handle.</summary>
    [DllImport(Library, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    /// <summary>The process id of the server that created the named pipe a client handle is connected to.</summary>
    [DllImport(Library, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
