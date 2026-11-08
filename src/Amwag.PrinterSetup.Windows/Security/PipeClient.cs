using Amwag.PrinterSetup.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Amwag.PrinterSetup.Windows.Security;

/// <summary>Who is at the other end of a named pipe.</summary>
public static class PipeClient
{
    /// <summary>The process id of the client connected to the server end <paramref name="serverPipe"/>, or null when Windows cannot tell.</summary>
    public static int? ProcessId(SafePipeHandle serverPipe) =>
        Kernel32.GetNamedPipeClientProcessId(serverPipe, out uint processId) ? (int)processId : null;

    /// <summary>The process id of the server whose pipe the client end <paramref name="clientPipe"/> is connected to, or null when Windows cannot tell.</summary>
    public static int? ServerProcessId(SafePipeHandle clientPipe) =>
        Kernel32.GetNamedPipeServerProcessId(clientPipe, out uint processId) ? (int)processId : null;
}
