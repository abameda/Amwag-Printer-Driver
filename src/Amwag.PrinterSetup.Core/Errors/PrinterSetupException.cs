using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.Core.Errors;

/// <summary>A failure with a finding code the UI can explain, and the Win32 error when one is involved.</summary>
public class PrinterSetupException : Exception
{
    public PrinterSetupException(FindingCode code, string message, int? win32Error = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Win32Error = win32Error;
    }

    public FindingCode Code { get; }
    public int? Win32Error { get; }
}

/// <summary>A failed spooler API call: <see cref="Operation"/> names the API (e.g. "AddForm").</summary>
public sealed class SpoolerException : PrinterSetupException
{
    public SpoolerException(string operation, int win32Error, string message, FindingCode code = FindingCode.OperationFailed)
        : base(code, $"{operation} failed (Win32 error {win32Error}): {message}", win32Error)
    {
        Operation = operation;
    }

    public string Operation { get; }

    /// <summary>RPC errors that mean the spooler is not reachable (stopped or crashed).</summary>
    public bool IsSpoolerUnavailable => Win32Error is 1722 or 1726 or 1727 or 1753;
}
