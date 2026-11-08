using System.Globalization;
using System.Security;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Microsoft.Win32;
using static Amwag.PrinterSetup.Windows.Interop.CfgMgr32;

namespace Amwag.PrinterSetup.Windows.Devices;

/// <summary>Plug and Play printer devices via CfgMgr32, with the USB monitor port mapping.</summary>
public sealed class WindowsDeviceInventory : IDeviceInventory
{
    // The enumerators that create printer device nodes: usbprint.sys, the WSD print enumerator and the parallel port.
    private static readonly (string Enumerator, PnpBus Bus)[] PrinterEnumerators =
    [
        ("USBPRINT", PnpBus.Usb),
        ("WSDPRINT", PnpBus.Wsd),
        ("LPTENUM", PnpBus.Lpt),
    ];

    private readonly ILog _log;

    public WindowsDeviceInventory(ILog log) => _log = log;

    /// <summary>Throws <see cref="PrinterSetupException"/> when an enumerator cannot be listed; single unreadable devices are skipped.</summary>
    public IReadOnlyList<PnpPrinterDevice> GetPrinterDevices()
    {
        var devices = new List<PnpPrinterDevice>();
        foreach (var (enumerator, bus) in PrinterEnumerators)
            devices.AddRange(GetDevices(enumerator, bus));

        int present = devices.Count(d => d.IsPresent);
        _log.Info("devices.enumerate.completed", $"Found {devices.Count} printer device node(s), {present} present.", new Dictionary<string, object?>
        {
            ["count"] = devices.Count,
            ["present"] = present,
            ["devices"] = devices.Select(Summarize).ToArray(),
        });
        return devices;
    }

    /// <summary>Every device node of one enumerator; a device that cannot be read is logged and left out.</summary>
    internal IReadOnlyList<PnpPrinterDevice> GetDevices(string enumerator, PnpBus bus)
    {
        var devices = new List<PnpPrinterDevice>();
        foreach (string instanceId in DeviceTree.GetDeviceIds(enumerator))
        {
            try
            {
                devices.Add(ReadDevice(instanceId, bus));
            }
            catch (Exception ex)
            {
                _log.Warn("devices.device.skipped", $"Device {instanceId} could not be read and is left out of the inventory.",
                    new Dictionary<string, object?> { ["instanceId"] = instanceId }, ex);
            }
        }

        return devices;
    }

    private static string Summarize(PnpPrinterDevice device) =>
        $"{device.InstanceId} present={device.IsPresent} problem={device.ProblemCode?.ToString(CultureInfo.InvariantCulture) ?? "none"} port={device.UsbPortName ?? "-"}";

    /// <summary>usbmon names a port Base Name + three-digit Port Number ("USB" + 1 = "USB001"); Base Name is not always written.</summary>
    internal static string FormatUsbPortName(string? baseName, int portNumber) =>
        (string.IsNullOrWhiteSpace(baseName) ? "USB" : baseName.Trim()) + portNumber.ToString("000", CultureInfo.InvariantCulture);

    private PnpPrinterDevice ReadDevice(string instanceId, PnpBus bus)
    {
        var (devInst, isPresent) = DeviceTree.Locate(instanceId);
        string? parent = DeviceTree.GetString(devInst, DevPropKeys.Parent);

        return new PnpPrinterDevice
        {
            InstanceId = instanceId,
            Bus = bus,
            HardwareIds = DeviceTree.GetStringList(devInst, DevPropKeys.HardwareIds),
            CompatibleIds = DeviceTree.GetStringList(devInst, DevPropKeys.CompatibleIds),
            FriendlyName = DeviceTree.GetString(devInst, DevPropKeys.FriendlyName),
            Description = DeviceTree.GetString(devInst, DevPropKeys.DeviceDesc),
            Manufacturer = DeviceTree.GetString(devInst, DevPropKeys.Manufacturer),
            DeviceClass = DeviceTree.GetString(devInst, DevPropKeys.Class),
            IsPresent = isPresent,
            ProblemCode = isPresent ? DeviceTree.GetProblemCode(devInst) : null,
            DriverDescription = DeviceTree.GetString(devInst, DevPropKeys.DriverDesc),
            DriverVersion = DeviceTree.GetString(devInst, DevPropKeys.DriverVersion),
            ParentInstanceId = parent,
            UsbPortName = bus == PnpBus.Usb && parent is not null ? FindUsbPortName(instanceId, parent) : null,
        };
    }

    /// <summary>
    /// usbprint.sys registers GUID_DEVINTERFACE_USBPRINT on the USB device that is the parent of the USBPRINT\ node and
    /// keeps the usbmon port assignment in that interface's Device Parameters key ("Port Number", "Base Name"). The
    /// assignment survives unplugging, so non-present devices still map to their old port.
    /// </summary>
    private string? FindUsbPortName(string instanceId, string usbDeviceId)
    {
        try
        {
            foreach (string interfacePath in DeviceTree.GetInterfaces(GUID_DEVINTERFACE_USBPRINT, usbDeviceId))
            {
                using RegistryKey? key = DeviceTree.OpenInterfaceKey(interfacePath);
                if (key?.GetValue("Port Number") is not int portNumber || portNumber < 0)
                    continue;

                string portName = FormatUsbPortName(key.GetValue("Base Name") as string, portNumber);
                _log.Debug("devices.usbport.resolved", $"Device {instanceId} uses port {portName}.", new Dictionary<string, object?>
                {
                    ["instanceId"] = instanceId,
                    ["usbDevice"] = usbDeviceId,
                    ["interface"] = interfacePath,
                    ["port"] = portName,
                });
                return portName;
            }

            _log.Debug("devices.usbport.unknown", $"No USB monitor port is recorded for device {instanceId}.", new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId,
                ["usbDevice"] = usbDeviceId,
            });
            return null;
        }
        catch (Exception ex) when (ex is PrinterSetupException or IOException or UnauthorizedAccessException or SecurityException)
        {
            _log.Warn("devices.usbport.failed", $"The USB port of device {instanceId} could not be read.", new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId,
                ["usbDevice"] = usbDeviceId,
            }, ex);
            return null;
        }
    }
}
