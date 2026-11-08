using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;

namespace Amwag.PrinterSetup.Core.Setup;

/// <summary>Queues, ports, drivers and forms read together for one assessment; <see cref="Known"/> is false when they could not be read.</summary>
internal sealed record SpoolerState(
    bool Known,
    bool Unavailable,
    string? Error,
    IReadOnlyList<PrintQueueInfo> Queues,
    IReadOnlyList<PrinterPortInfo> Ports,
    IReadOnlyList<PrinterDriverInfo> Drivers,
    IReadOnlyList<FormInfo> Forms)
{
    public static SpoolerState Read(IPrintSpooler spooler, SpoolerServiceStatus status)
    {
        if (status.State is not (ServiceRunState.Running or ServiceRunState.StartPending or ServiceRunState.Unknown))
            return Unread(false, $"The Print Spooler service is {status.State}.");

        try
        {
            return new SpoolerState(true, false, null, spooler.GetQueues(), spooler.GetPorts(), spooler.GetDrivers(), spooler.GetForms());
        }
        catch (SpoolerException ex) when (ex.IsSpoolerUnavailable)
        {
            return Unread(true, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unread(false, ex.Message);
        }
    }

    private static SpoolerState Unread(bool unavailable, string error) => new(false, unavailable, error, [], [], [], []);
}
