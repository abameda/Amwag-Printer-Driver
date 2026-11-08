using System.Net;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Detection;
using Amwag.PrinterSetup.Core.Inventory;

namespace Amwag.PrinterSetup.Core.Setup;

/// <summary>Finds queues and ports of a printer in freshly read spooler state, and names new ones without collisions.</summary>
internal static class QueueLocator
{
    private static readonly string[] LocalPortNames = ["FILE:", "NUL:", "PORTPROMPT:"];

    public static PrinterPortInfo? FindPort(IReadOnlyList<PrinterPortInfo> ports, string? name) =>
        name is null ? null : ports.FirstOrDefault(p => Same(p.Name, name));

    /// <summary>
    /// The port's kind from the port list, or inferred from its name when the port is not listed (an Internet Port is named by
    /// its URL). A pooled queue's ports (<see cref="PortNames"/>) are USB when one of them is; otherwise they are no single
    /// printer's port (Other).
    /// </summary>
    public static PortKind KindOf(string portName, IReadOnlyList<PrinterPortInfo> ports)
    {
        var names = PortNames(portName);
        if (names.Count > 1)
            return names.Any(p => KindOf(p, ports) == PortKind.Usb) ? PortKind.Usb : PortKind.Other;
        string name = names.Count == 1 ? names[0] : portName;
        if (FindPort(ports, name) is { } port)
            return port.Kind;
        if (IsLocalPortName(name))
            return PortKind.Local;
        if (name.Length > 3 && name.StartsWith("USB", StringComparison.OrdinalIgnoreCase) && name[3..].All(char.IsAsciiDigit))
            return PortKind.Usb;
        if (IsUrl(name))
            return PortKind.Internet;
        return name.StartsWith("WSD", StringComparison.OrdinalIgnoreCase) ? PortKind.Wsd : PortKind.Other;
    }

    public static bool IsLocalPortName(string name) =>
        LocalPortNames.Contains(name, StringComparer.OrdinalIgnoreCase) || IsNumberedPort(name, "LPT") || IsNumberedPort(name, "COM");

    /// <summary>
    /// The Standard TCP/IP host of the queue's port, when it has one; only such a queue is a network queue the setup can
    /// configure. A pooled queue has no single host, and an LPR or Internet Port is no Standard TCP/IP port (see <see cref="HostsOf"/>).
    /// </summary>
    public static string? HostOf(PrintQueueInfo queue, IReadOnlyList<PrinterPortInfo> ports) =>
        PortNames(queue.PortName) is [var name] && FindPort(ports, name) is { Kind: PortKind.StandardTcpIp, HostAddress: { Length: > 0 } host } ? host : null;

    /// <summary>
    /// Every host the queue prints to, one for each of its ports that names one (several for a pooled queue): a Standard TCP/IP
    /// port's host address, the server of an LPR Port (<see cref="LprServer"/>) and the host of an Internet Port's URL
    /// (<see cref="UrlHost"/>). A port name that does not give its host reliably gives none.
    /// </summary>
    public static IReadOnlyList<string> HostsOf(PrintQueueInfo queue, IReadOnlyList<PrinterPortInfo> ports) =>
        PortNames(queue.PortName)
            .Select(name => KindOf(name, ports) switch
            {
                PortKind.StandardTcpIp => FindPort(ports, name)?.HostAddress is { Length: > 0 } host ? host : null,
                PortKind.Lpr => LprServer(name),
                PortKind.Internet => UrlHost(name),
                _ => null,
            })
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The LPD server an LPR Port names: the LPR Port monitor names its ports "&lt;host&gt;:&lt;queue&gt;". Null unless the
    /// name has exactly one colon, with an IPv4 address or host name before it and a queue name after it; the colons of an IPv6
    /// server would make the name ambiguous.
    /// </summary>
    public static string? LprServer(string portName)
    {
        int colon = portName.IndexOf(':');
        if (colon <= 0 || colon == portName.Length - 1 || portName.IndexOf(':', colon + 1) >= 0)
            return null;
        string host = portName[..colon];
        return PrinterAddress.IsValid(host) ? host : null;
    }

    /// <summary>
    /// The host of an Internet Port's URL ("http(s)://&lt;host&gt;[:&lt;port&gt;]/...", as for IPP), an IPv6 address without its
    /// brackets; null for a name that is no such URL.
    /// </summary>
    public static string? UrlHost(string portName) =>
        IsUrl(portName) && Uri.TryCreate(portName, UriKind.Absolute, out var uri) && uri.IdnHost is { Length: > 0 } host ? host : null;

    /// <summary>A local (not server-connection) queue by name.</summary>
    public static PrintQueueInfo? ByName(IReadOnlyList<PrintQueueInfo> queues, string? name) =>
        name is null ? null : queues.FirstOrDefault(q => !q.IsServerConnection && Same(q.Name, name));

    /// <summary>
    /// The ports a queue prints to: PRINTER_INFO_2.pPortName lists the ports of a pooled queue separated by commas
    /// ("USB001,USB002"), and a queue prints to any of them.
    /// </summary>
    public static IReadOnlyList<string> PortNames(string portName) =>
        portName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Whether the queue prints to <paramref name="portName"/>: its port, or one of its pooled ports.</summary>
    public static bool UsesPort(PrintQueueInfo queue, string? portName) =>
        portName is not null && PortNames(queue.PortName).Contains(portName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Local queues that print to the port, also as one of their pooled ports.</summary>
    public static IReadOnlyList<PrintQueueInfo> OnPort(IReadOnlyList<PrintQueueInfo> queues, string? portName) =>
        portName is null ? [] : queues.Where(q => !q.IsServerConnection && UsesPort(q, portName)).ToList();

    /// <summary>
    /// The local queues of a USB printer: those that print to its port, also as one of their pooled ports. While the printer is
    /// not connected, another USB printer on one of a queue's ports (also on the printer's own port, which the USB monitor may
    /// have given a successor) is that queue's printer: a connected one whose node has hardware IDs, whatever its model (the
    /// port now leads to it), and one of the model the queue's driver names, connected or not, when this printer is of another
    /// model. So a unit known only from an earlier connection (a replaced unit, possibly of another model) never claims the
    /// queue another printer uses, and its driver is never changed for it; a queue it gives way to is judged by the printers on
    /// its ports. A node without IDs tells nothing.
    /// </summary>
    public static IReadOnlyList<PrintQueueInfo> ForUsbDevice(
        IReadOnlyList<PrintQueueInfo> queues,
        PnpPrinterDevice device,
        IReadOnlyList<PnpPrinterDevice> devices,
        ModelMatcher matcher)
    {
        var onPort = OnPort(queues, device.UsbPortName);
        if (device.IsPresent)
            return onPort;
        string? ownModel = matcher.MatchDevice(device).Model?.Id;
        var others = devices.Where(d => d.Bus == PnpBus.Usb && d.UsbPortName is not null && !Same(d.InstanceId, device.InstanceId)).ToList();
        return onPort.Where(queue => !others.Any(other => UsesPort(queue, other.UsbPortName) && IsPrinterOf(queue, other))).ToList();

        bool IsPrinterOf(PrintQueueInfo queue, PnpPrinterDevice other) =>
            other is { IsPresent: true, HardwareIds.Count: > 0 }
            || matcher.ModelForDriver(queue.DriverName) is { } driverModel && !Same(ownModel, driverModel.Id)
                && Same(matcher.MatchDevice(other).Model?.Id, driverModel.Id);
    }

    /// <summary>Whether detection gave the target <paramref name="queue"/>: its queue, or one of its printer's other queues (by name).</summary>
    public static bool IsDetectedQueue(PrinterCandidate target, PrintQueueInfo queue) =>
        Same(target.Queue?.Name, queue.Name) || target.AdditionalQueues.Any(q => Same(q.Name, queue.Name));

    public static IReadOnlyList<PrintQueueInfo> ForHost(IReadOnlyList<PrintQueueInfo> queues, IReadOnlyList<PrinterPortInfo> ports, string host) =>
        queues.Where(q => !q.IsServerConnection && Same(HostOf(q, ports), host)).ToList();

    /// <summary>Local queues whose Standard TCP/IP port sends to any of <paramref name="hosts"/>.</summary>
    public static IReadOnlyList<PrintQueueInfo> ForHosts(IReadOnlyList<PrintQueueInfo> queues, IReadOnlyList<PrinterPortInfo> ports, IReadOnlyCollection<string> hosts) =>
        queues.Where(q => !q.IsServerConnection && HostOf(q, ports) is { } host && hosts.Contains(host, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Every spelling that reaches a network target: its address first, then the aliases detection merged into it.</summary>
    public static IReadOnlyList<string> NetworkHosts(PrinterCandidate target) =>
        target.NetworkAddress is { } address ? [address, .. target.NetworkAliases] : target.NetworkAliases;

    /// <summary>The queue this tool configures among one printer's queues: the preferred driver's, else an accepted driver's, else the first by name.</summary>
    public static PrintQueueInfo? Primary(IEnumerable<PrintQueueInfo> queues, ModelDefinition? model)
    {
        var ordered = queues.OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (model is not null)
        {
            var accepted = ordered.FirstOrDefault(q => Same(q.DriverName, model.PreferredDriverName))
                ?? ordered.FirstOrDefault(q => DriverRules.IsAccepted(model, q.DriverName));
            if (accepted is not null)
                return accepted;
        }
        return ordered.FirstOrDefault();
    }

    /// <summary><paramref name="baseName"/> when free, else "base (model)", then "base (model) 2", "base (model) 3", ...</summary>
    public static string NewQueueName(string baseName, string modelId, IReadOnlyList<PrintQueueInfo> queues)
    {
        if (!IsQueueNameTaken(queues, baseName))
            return baseName;

        string withModel = $"{baseName} ({modelId})";
        if (!IsQueueNameTaken(queues, withModel))
            return withModel;

        for (int n = 2; ; n++)
        {
            string candidate = FormattableString.Invariant($"{withModel} {n}");
            if (!IsQueueNameTaken(queues, candidate))
                return candidate;
        }
    }

    /// <summary>An existing RAW Standard TCP/IP port for the address and port number.</summary>
    public static PrinterPortInfo? ReusableTcpPort(IReadOnlyList<PrinterPortInfo> ports, string address, int portNumber) =>
        ports.FirstOrDefault(p => IsRawTcpPort(p, portNumber) && Same(p.HostAddress, address));

    /// <summary>
    /// An existing RAW Standard TCP/IP port that already sends to the network target, so that the printer gets no second port:
    /// one on its address; else one on an IP address the printer is known by (an IP literal among its spellings, or the
    /// address its address resolved to); else, when its address is itself a host name, one on another of its host names that
    /// <paramref name="probedAddresses"/> names. A queue on that port is keyed by the port's host when the printer is detected
    /// again, so that host must merge with the target then: an IP address always does, a host name only when it is probed
    /// again (configured addresses always are) and resolves. A target keyed by an IP address therefore never moves its queue
    /// to a host name, which a later run without DNS could not link to that address (it would add a second queue).
    /// </summary>
    public static PrinterPortInfo? ReusableTcpPort(IReadOnlyList<PrinterPortInfo> ports, PrinterCandidate target, int portNumber, IReadOnlyCollection<string?> probedAddresses)
    {
        var raw = ports.Where(p => IsRawTcpPort(p, portNumber)).ToList();
        var hosts = NetworkHosts(target);
        var ipAddresses = hosts.Append(target.NetworkIdentity?.ResolvedAddress).Select(IpAddressOf).OfType<IPAddress>().ToList();
        bool keyedByName = target.NetworkAddress is { } address && IpAddressOf(address) is null;
        return raw.FirstOrDefault(p => Same(p.HostAddress, target.NetworkAddress))
            ?? raw.FirstOrDefault(p => IpAddressOf(p.HostAddress) is { } ip && ipAddresses.Contains(ip))
            ?? (keyedByName
                ? raw.FirstOrDefault(p => hosts.Contains(p.HostAddress, StringComparer.OrdinalIgnoreCase) && probedAddresses.Contains(p.HostAddress, StringComparer.OrdinalIgnoreCase))
                : null);
    }

    /// <summary>"IP_&lt;address&gt;", or "IP_&lt;address&gt;_2", "_3", ... when the name belongs to another port.</summary>
    public static string NewTcpPortName(string address, IReadOnlyList<PrinterPortInfo> ports)
    {
        string baseName = "IP_" + address;
        if (FindPort(ports, baseName) is null)
            return baseName;

        for (int n = 2; ; n++)
        {
            string candidate = FormattableString.Invariant($"{baseName}_{n}");
            if (FindPort(ports, candidate) is null)
                return candidate;
        }
    }

    private static bool IsQueueNameTaken(IReadOnlyList<PrintQueueInfo> queues, string name) => queues.Any(q => Same(q.Name, name));

    /// <summary>A port name that is an http or https URL, as Internet Ports are named.</summary>
    private static bool IsUrl(string name) =>
        name.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || name.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool IsRawTcpPort(PrinterPortInfo port, int portNumber) =>
        port.Kind == PortKind.StandardTcpIp && port.PortNumber == portNumber && port.Protocol != TcpPortProtocol.Lpr;

    /// <summary>The IP address an IP literal stands for (one address has several IPv6 spellings); null for a host name.</summary>
    private static IPAddress? IpAddressOf(string? address) =>
        PrinterAddress.TryParse(address, out _, out var ip, out _) ? ip : null;

    private static bool IsNumberedPort(string name, string prefix) =>
        name.Length > prefix.Length + 1 &&
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(':') &&
        name[prefix.Length..^1].All(char.IsAsciiDigit);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
