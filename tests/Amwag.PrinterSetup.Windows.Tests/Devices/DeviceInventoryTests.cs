using System.Collections.Concurrent;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Windows.Devices;
using Microsoft.Win32;

namespace Amwag.PrinterSetup.Windows.Tests.Devices;

public sealed class DeviceInventoryTests
{
    [Theory]
    [InlineData("USB", 1, "USB001")]
    [InlineData(null, 3, "USB003")]
    [InlineData("", 12, "USB012")]
    [InlineData(" USB ", 7, "USB007")]
    [InlineData("USB", 1000, "USB1000")]
    [InlineData("DOT4_", 1, "DOT4_001")]
    public void Formats_usb_monitor_port_names(string? baseName, int portNumber, string expected)
    {
        Assert.Equal(expected, WindowsDeviceInventory.FormatUsbPortName(baseName, portNumber));
    }

    [Theory]
    [InlineData("a\0b\0\0", new[] { "a", "b" })]
    [InlineData("USBPRINT\\Kyocera\\1&2\0\0", new[] { "USBPRINT\\Kyocera\\1&2" })]
    [InlineData("\0", new string[0])]
    [InlineData("", new string[0])]
    [InlineData("a", new[] { "a" })]
    [InlineData("a\0\0b\0\0", new[] { "a" })]
    public void Splits_multi_strings(string buffer, string[] expected)
    {
        Assert.Equal(expected, DeviceTree.SplitMultiSz(buffer));
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void Enumerates_printer_devices_without_throwing()
    {
        var log = new RecordingLog();

        IReadOnlyList<PnpPrinterDevice> devices = new WindowsDeviceInventory(log).GetPrinterDevices();

        foreach (PnpPrinterDevice device in devices)
        {
            PnpBus expectedBus = device.InstanceId.Split('\\')[0].ToUpperInvariant() switch
            {
                "USBPRINT" => PnpBus.Usb,
                "WSDPRINT" => PnpBus.Wsd,
                "LPTENUM" => PnpBus.Lpt,
                _ => throw new Xunit.Sdk.XunitException($"Unexpected enumerator in {device.InstanceId}"),
            };
            Assert.Equal(expectedBus, device.Bus);
            if (!device.IsPresent)
                Assert.Null(device.ProblemCode);
        }

        Assert.Contains(log.Entries, e => e.Event == "devices.enumerate.completed");
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void Reads_properties_of_present_root_devices()
    {
        var log = new RecordingLog();

        IReadOnlyList<PnpPrinterDevice> devices = new WindowsDeviceInventory(log).GetDevices("ROOT", PnpBus.Other);

        Assert.NotEmpty(devices);
        Assert.Contains(devices, d => d.IsPresent && d.HardwareIds.Count > 0 && d.DeviceClass is not null && d.Description is not null);
        Assert.All(devices, d => Assert.StartsWith("ROOT\\", d.InstanceId, StringComparison.OrdinalIgnoreCase));
        Assert.All(devices, d => Assert.True(d.ProblemCode is null or > 0));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void Reads_present_and_non_present_usb_devices_and_looks_up_their_interfaces()
    {
        var log = new RecordingLog();

        // Bus Usb makes the inventory look for a USBPRINT interface on each device's parent (a hub here: none).
        IReadOnlyList<PnpPrinterDevice> devices = new WindowsDeviceInventory(log).GetDevices("USB", PnpBus.Usb);

        Assert.All(devices, d => Assert.NotNull(d.ParentInstanceId));
        Assert.All(devices.Where(d => !d.IsPresent), d => Assert.Null(d.ProblemCode));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void Opens_the_interface_device_parameters_key_that_the_port_mapping_reads()
    {
        // Compares CM_Open_Device_Interface_KeyW with the registry layout
        // DeviceClasses\{class}\{interface}\#\Device Parameters for interfaces that have such values (read-only).
        int compared = 0;
        using RegistryKey deviceClasses = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceClasses")!;
        foreach (string classKeyName in deviceClasses.GetSubKeyNames())
        {
            if (!Guid.TryParse(classKeyName, out Guid interfaceClass))
                continue;

            using RegistryKey? classKey = TryOpen(deviceClasses, classKeyName);
            foreach (string interfaceKeyName in classKey?.GetSubKeyNames() ?? [])
            {
                using RegistryKey? interfaceKey = TryOpen(classKey!, interfaceKeyName);
                using RegistryKey? parameters = interfaceKey is null ? null : TryOpen(interfaceKey, @"#\Device Parameters");
                if (interfaceKey?.GetValue("DeviceInstance") is not string deviceInstance || parameters is null || parameters.ValueCount == 0)
                    continue;

                string? path = DeviceTree.GetInterfaces(interfaceClass, deviceInstance)
                    .FirstOrDefault(p => string.Equals(p.Replace('\\', '#'), interfaceKeyName, StringComparison.OrdinalIgnoreCase));
                if (path is null)
                    continue;

                using RegistryKey? opened = DeviceTree.OpenInterfaceKey(path);
                Assert.NotNull(opened);
                Assert.Equal(parameters.GetValueNames().Order(), opened.GetValueNames().Order());
                if (++compared == 5)
                    return;
            }
        }

        Assert.True(compared > 0, "No device interface with Device Parameters values was found to compare against.");
    }

    // Some interface keys are readable only by administrators or SYSTEM.
    private static RegistryKey? TryOpen(RegistryKey parent, string name)
    {
        try
        {
            return parent.OpenSubKey(name);
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }

    private sealed class RecordingLog : ILog
    {
        public ConcurrentQueue<(LogLevel Level, string Event, string Message)> Entries { get; } = new();

        public string SessionId => "test";

        public string? FilePath => null;

        public void Write(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null) =>
            Entries.Enqueue((level, eventName, message));
    }
}
