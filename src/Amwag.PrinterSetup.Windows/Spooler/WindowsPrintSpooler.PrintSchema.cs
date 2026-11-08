using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Xml;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Printing;
using static Amwag.PrinterSetup.Windows.Interop.PrintSchemaNative;

namespace Amwag.PrinterSetup.Windows.Spooler;

// Print Schema cross-checks through prntvpt.dll. Driver problems never throw: they become Available = false with Error.
public sealed unsafe partial class WindowsPrintSpooler
{
    /// <summary>Generous bound for drivers that hang inside their PrintTicket provider.</summary>
    private static readonly TimeSpan PrintSchemaTimeout = TimeSpan.FromSeconds(60);

    public PrintCapabilitiesSummary GetPrintCapabilities(string queueName, DevMode? devMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        byte[]? devModeBytes = devMode?.ToArray();

        PrintCapabilitiesSummary summary = RunPrintSchema<PrintCapabilitiesSummary>(
            queueName,
            "PTGetPrintCapabilities",
            Unavailable,
            provider =>
            {
                string? conversionError = null;
                using HGlobalStream? ticket = devModeBytes is null ? null : ConvertToTicket(provider, devModeBytes, out conversionError);
                if (devModeBytes is not null && ticket is null)
                    return Unavailable(conversionError);
                // PTGetPrintCapabilities reads the ticket from the current position, which the conversion left at the end.
                ticket?.Rewind();

                using var capabilities = HGlobalStream.Create();
                int hr = PTGetPrintCapabilities(provider, ticket?.Pointer ?? IntPtr.Zero, capabilities.Pointer, out IntPtr errorMessage);
                string? driverMessage = TakeBstr(errorMessage);
                if (hr < 0)
                    return Unavailable(HResultText("PTGetPrintCapabilities", hr, driverMessage));

                return Parse(() => PrintSchemaReader.ReadCapabilities(capabilities.ToArray()), Unavailable);
            });

        _log.Write(summary.Available ? LogLevel.Debug : LogLevel.Warning, "spooler.printschema.capabilities", $"PrintCapabilities of '{queueName}' read.", Data(
            ("queue", queueName), ("available", summary.Available), ("error", summary.Error), ("mediaSizes", summary.MediaSizes.Count),
            ("customRange", summary.CustomSizeRange?.ToString()), ("inputBins", string.Join(", ", summary.InputBins))));
        return summary;

        static PrintCapabilitiesSummary Unavailable(string? error) => new() { Available = false, Error = error };
    }

    public PrintTicketMediaInfo DescribePrintTicket(string queueName, DevMode devMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentNullException.ThrowIfNull(devMode);
        byte[] devModeBytes = devMode.ToArray();

        PrintTicketMediaInfo info = RunPrintSchema<PrintTicketMediaInfo>(
            queueName,
            "PTConvertDevModeToPrintTicket",
            Unavailable,
            provider =>
            {
                using var ticket = ConvertToTicket(provider, devModeBytes, out string? conversionError);
                if (ticket is null)
                    return Unavailable(conversionError);
                return Parse(() => PrintSchemaReader.ReadTicket(ticket.ToArray()), Unavailable);
            });

        _log.Write(info.Available ? LogLevel.Debug : LogLevel.Warning, "spooler.printschema.ticket", $"PrintTicket of a DEVMODE for '{queueName}' read.", Data(
            ("queue", queueName), ("available", info.Available), ("error", info.Error), ("mediaSize", info.MediaSizeName),
            ("width", info.WidthMicrometers), ("height", info.HeightMicrometers), ("inputBin", info.InputBin),
            ("orientation", info.Orientation), ("duplex", info.Duplex)));
        return info;

        static PrintTicketMediaInfo Unavailable(string? error) => new() { Available = false, Error = error };
    }

    /// <summary>
    /// Runs the work on a dedicated thread: a provider handle is only valid on the thread that opened it
    /// (https://learn.microsoft.com/windows/win32/api/prntvpt/nf-prntvpt-ptgetprintcapabilities), and COM must be
    /// initialized there. The thread joins the MTA, which PTOpenProvider itself requests (noted in Chromium's
    /// printing/backend/win_helper.cc), and the extra CoInitializeEx reference keeps the apartment alive should a driver
    /// uninitialize COM it did not initialize.
    /// </summary>
    private T RunPrintSchema<T>(string queueName, string operation, Func<string, T> unavailable, Func<SafePrintTicketProvider, T> work)
    {
        T? result = default;
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            int init = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            try
            {
                int hr = PTOpenProvider(queueName, PrintSchemaVersion, out SafePrintTicketProvider provider);
                using (provider)
                    result = hr < 0 ? unavailable(HResultText("PTOpenProvider", hr, driverMessage: null)) : work(provider);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                if (init >= 0)
                    CoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "AMWAG Print Schema",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        if (!thread.Join(PrintSchemaTimeout))
        {
            _log.Warn("spooler.printschema.timeout", $"{operation} for '{queueName}' did not finish within {PrintSchemaTimeout.TotalSeconds:0} s.", Data(("queue", queueName)));
            return unavailable($"{operation} did not finish within {PrintSchemaTimeout.TotalSeconds:0} seconds.");
        }

        failure?.Throw();
        return result!;
    }

    /// <summary>Converts a DEVMODE to a job-scope PrintTicket; null with the error text when the driver fails.</summary>
    private static HGlobalStream? ConvertToTicket(SafePrintTicketProvider provider, byte[] devMode, out string? error)
    {
        var ticket = HGlobalStream.Create();
        int hr;
        fixed (byte* pDevMode = devMode)
            hr = PTConvertDevModeToPrintTicket(provider, (uint)devMode.Length, pDevMode, kPTJobScope, ticket.Pointer);

        if (hr >= 0)
        {
            error = null;
            return ticket;
        }

        ticket.Dispose();
        error = HResultText("PTConvertDevModeToPrintTicket", hr, driverMessage: null);
        return null;
    }

    private static T Parse<T>(Func<T> read, Func<string, T> unavailable)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException)
        {
            return unavailable($"The driver returned an unreadable Print Schema document: {ex.Message}");
        }
    }

    private static string? TakeBstr(IntPtr bstr)
    {
        if (bstr == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringBSTR(bstr);
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    private static string HResultText(string api, int hr, string? driverMessage)
    {
        string meaning = hr switch
        {
            E_PRINTTICKET_FORMAT => "the PrintTicket does not follow the Print Schema",
            E_PRINTCAPABILITIES_FORMAT => "the driver's PrintCapabilities document does not follow the Print Schema",
            _ => Marshal.GetPInvokeErrorMessage(hr),
        };
        string text = $"{api} failed with HRESULT 0x{hr:X8} ({meaning})";
        return string.IsNullOrWhiteSpace(driverMessage) ? text : $"{text}: {driverMessage.Trim()}";
    }
}
