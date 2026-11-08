using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Inventory;

namespace Amwag.PrinterSetup.Testing;

/// <summary>In-memory Plug and Play printer devices.</summary>
public sealed class FakeDeviceInventory : IDeviceInventory
{
    private readonly object _gate = new();
    private readonly List<PnpPrinterDevice> _devices = [];

    public IReadOnlyList<PnpPrinterDevice> Devices
    {
        get { lock (_gate) return [.. _devices]; }
    }

    /// <summary>When set, <see cref="GetPrinterDevices"/> throws it.</summary>
    public Exception? Failure { get; set; }

    public void Add(PnpPrinterDevice device)
    {
        lock (_gate)
        {
            _devices.RemoveAll(d => SameInstance(d, device.InstanceId));
            _devices.Add(device);
        }
    }

    public void Update(string instanceId, Func<PnpPrinterDevice, PnpPrinterDevice> change)
    {
        lock (_gate)
        {
            int index = _devices.FindIndex(d => SameInstance(d, instanceId));
            if (index < 0)
                throw new InvalidOperationException($"No fake device '{instanceId}'.");
            _devices[index] = change(_devices[index]);
        }
    }

    public void Remove(string instanceId)
    {
        lock (_gate)
            _devices.RemoveAll(d => SameInstance(d, instanceId));
    }

    public PnpPrinterDevice? Find(string instanceId)
    {
        lock (_gate)
            return _devices.FirstOrDefault(d => SameInstance(d, instanceId));
    }

    public IReadOnlyList<PnpPrinterDevice> GetPrinterDevices()
    {
        if (Failure is not null)
            throw Failure;
        return Devices;
    }

    private static bool SameInstance(PnpPrinterDevice device, string instanceId) =>
        string.Equals(device.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase);
}
