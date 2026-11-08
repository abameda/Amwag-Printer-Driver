using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;

namespace Amwag.PrinterSetup.Core.Detection;

/// <summary>Captures a read-only snapshot; each source is read on its own so one failure never hides the others.</summary>
internal sealed class SnapshotReader
{
    private readonly SetupServices _services;
    private readonly List<string> _errors = [];

    public SnapshotReader(SetupServices services) => _services = services;

    /// <summary>A spooler call failed with an RPC error (the spooler is stopped or crashed).</summary>
    public bool SpoolerUnavailable { get; private set; }

    public SystemSnapshot Capture()
    {
        var platform = Read("platform", _services.Platform.Inspect) ?? new PlatformInfo();
        var spooler = Read("spooler service", _services.Spooler.GetServiceStatus)
            ?? new SpoolerServiceStatus(ServiceRunState.Unknown, ServiceStartMode.Unknown);

        IReadOnlyList<PrintQueueInfo> queues = [];
        IReadOnlyList<PrinterPortInfo> ports = [];
        IReadOnlyList<PrinterDriverInfo> drivers = [];
        IReadOnlyList<FormInfo> forms = [];
        if (spooler.State is ServiceRunState.Running or ServiceRunState.StartPending or ServiceRunState.Unknown)
        {
            queues = Read("queues", _services.Spooler.GetQueues) ?? [];
            ports = Read("ports", _services.Spooler.GetPorts) ?? [];
            drivers = Read("drivers", _services.Spooler.GetDrivers) ?? [];
            forms = Read("forms", _services.Spooler.GetForms) ?? [];
        }
        else
        {
            _errors.Add($"queues, ports, drivers, forms: not read because the Print Spooler service is {spooler.State}.");
        }

        var devices = Read("devices", _services.Devices.GetPrinterDevices) ?? [];

        return new SystemSnapshot
        {
            CapturedAt = _services.Clock.Now,
            Platform = platform,
            Spooler = spooler,
            Devices = devices,
            Queues = queues,
            Ports = ports,
            Drivers = drivers,
            Forms = forms,
            CaptureErrors = _errors,
        };
    }

    private T? Read<T>(string source, Func<T> read) where T : class
    {
        try
        {
            return read();
        }
        catch (SpoolerException ex) when (ex.IsSpoolerUnavailable)
        {
            SpoolerUnavailable = true;
            Failed(source, ex);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed(source, ex);
            return null;
        }
    }

    private void Failed(string source, Exception ex)
    {
        _errors.Add($"{source}: {ex.Message}");
        _services.Log.Warn("detect.capture.failed", $"Could not read {source}.", new Dictionary<string, object?> { ["source"] = source }, ex);
    }
}
