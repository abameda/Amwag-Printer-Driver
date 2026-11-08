using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.Core.Detection;

/// <summary>
/// Turns a snapshot (with network identities) into printer candidates and detection findings. Queues attach to their USB
/// device or TCP/IP host, one candidate each; extra queues are reported, never removed. Network spellings that reach the
/// same printer (its host name and its IP address, for example) are recognised: a requested spelling without a queue is an
/// alias of that printer's queue hosts, never a second candidate, and queue hosts of one printer stay separate candidates
/// (each configures its own queue) that are reported as duplicates. A queue the setup cannot configure (pooled ports, an LPR
/// or Internet Port) is linked to the printers it prints to, which are then not set up automatically; one whose driver names
/// no single model on an LPR or Internet Port does so only when it is the printer's ticket queue, else it is reported.
/// </summary>
internal sealed class CandidateAssembler
{
    private readonly ModelMatcher _matcher;
    private readonly SystemSnapshot _snapshot;
    private readonly int _rawPort;
    private readonly List<PrinterCandidate> _candidates = [];
    private readonly List<Finding> _findings = [];
    private readonly HashSet<string> _claimedQueues = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>USB nodes that are not present, with the position of their DeviceNotConnected finding.</summary>
    private readonly List<(int Finding, PnpPrinterDevice Device, ModelDefinition Model)> _staleUsbDevices = [];
    /// <summary>USB queues without a connected printer on their port, with the position of their DeviceNotConnected finding.</summary>
    private readonly List<(int Finding, PrinterCandidate Candidate)> _unattachedUsbQueues = [];
    /// <summary>Keys of candidates whose configured queue, among several, is the one already set up as the ticket printer.</summary>
    private readonly HashSet<string> _knownTicketQueues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlyList<PrintQueueInfo>, PrintQueueInfo?>? _ticketQueue;
    private readonly List<string> _rejectedQueues = [];
    /// <summary>Names of queues on ports the setup cannot configure that print to known hosts (<see cref="LinkToHosts"/>).</summary>
    private readonly HashSet<string> _unconfigurableQueues = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Queues of a supported model whose port gives no printer's host (<see cref="RejectUnlinkedPort"/>).</summary>
    private readonly List<PrintQueueInfo> _unlinkedQueues = [];
    /// <summary>
    /// Queues whose driver names no single model (a class driver) on an LPR or Internet Port (also pooled over such ports) that
    /// print to known hosts, with those hosts: compared with the printers they print to (<see cref="ModelLessQueuesTo"/>,
    /// <see cref="ReportPossibleDuplicates"/>).
    /// </summary>
    private readonly List<(PrintQueueInfo Queue, IReadOnlyList<string> Hosts)> _modelLessQueuesOnOtherPorts = [];
    /// <summary>Names of the queues whose host the probe limit left out (<see cref="UncheckedQueues"/>).</summary>
    private readonly List<string> _uncheckedQueues = [];
    /// <summary>Names of the queues of devices that report another vendor or an unsupported model (<see cref="RejectDevice"/>).</summary>
    private readonly HashSet<string> _otherDeviceQueues = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="ticketQueue">
    /// Among one printer's several queues, the one already set up as the ticket printer (null when none can be told apart); it
    /// is the queue this tool configures. Reads queue defaults, so it is asked only for printers with more than one queue.
    /// </param>
    public CandidateAssembler(ModelMatcher matcher, SystemSnapshot snapshot, int rawPort, Func<IReadOnlyList<PrintQueueInfo>, PrintQueueInfo?>? ticketQueue = null)
    {
        _matcher = matcher;
        _snapshot = snapshot;
        _rawPort = rawPort;
        _ticketQueue = ticketQueue;
    }

    /// <summary>
    /// After <see cref="Assemble"/>: queues whose driver names a supported model but that were not made candidates, because their
    /// printer reports another identity (another or an unsupported model, several models, another vendor), their drivers
    /// disagree, they print to a network printer that a queue the setup cannot configure (pooled, or on an LPR or Internet Port)
    /// prints to as well, that queue included, or their port cannot be linked to a printer. Also queues whose driver names no
    /// single model (a class driver) that a printer they print to is not set up automatically for: pooled over Standard TCP/IP
    /// ports, or that printer's ticket queue on an LPR or Internet Port. A pooled queue that is also a USB printer's queue stays
    /// that printer's and is not listed. Such a queue may be the ticket printer's, so no other printer is chosen without asking.
    /// </summary>
    public IReadOnlyList<string> RejectedQueues => _rejectedQueues;

    /// <summary>
    /// After <see cref="Assemble"/>: queues that may be a TA printer's (<see cref="ModelMatcher.MayBeTaQueue"/>) and whose driver
    /// names no single model, on a single Standard TCP/IP port whose host was not probed because the probe limit was reached and
    /// that leads to no probed spelling of a printer. They built no candidate and were compared with no other printer, so one of
    /// them may be the ticket printer's queue (a NetworkIdentityUnavailable warning names each).
    /// </summary>
    public IReadOnlyList<string> UncheckedQueues => _uncheckedQueues;

    /// <param name="requestedAddresses">Addresses the operator, command line or branch file named.</param>
    /// <param name="unprobedAddresses">
    /// Addresses that were not probed because the probe limit was reached: requested addresses and hosts of existing queues.
    /// </param>
    public (IReadOnlyList<PrinterCandidate> Candidates, IReadOnlyList<Finding> Findings) Assemble(
        IReadOnlyList<string> requestedAddresses,
        IReadOnlyCollection<string> unprobedAddresses)
    {
        var usbQueues = new List<PrintQueueInfo>();
        var tcpQueues = new Dictionary<string, List<PrintQueueInfo>>(StringComparer.OrdinalIgnoreCase);
        var wsdQueues = new List<PrintQueueInfo>();
        ClassifyQueues(usbQueues, tcpQueues, wsdQueues);

        AddUsbDevices(usbQueues);
        AddUnattachedUsbQueues(usbQueues);
        foreach (var printer in NetworkPrinters(tcpQueues, requestedAddresses))
        {
            if (printer.QueueHosts.Count > 0)
                AddQueuedNetworkPrinter(printer, unprobedAddresses);
            else
                AddRequestedNetworkPrinter(printer, unprobedAddresses);
        }
        AddWsdQueues(wsdQueues);
        ReportStaleUsbDevices();
        ReportSupersededUsbQueues();
        ReportUnmatchedTicketQueues();
        ReportPossibleDuplicates(tcpQueues, wsdQueues);
        ReportUnsupportedQueues();
        ReportDuplicates();

        var ordered = _candidates
            .OrderByDescending(c => c.PhysicallyAvailable)
            .ThenByDescending(c => c.Confidence)
            .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (ordered, _findings);
    }

    private void ClassifyQueues(List<PrintQueueInfo> usbQueues, Dictionary<string, List<PrintQueueInfo>> tcpQueues, List<PrintQueueInfo> wsdQueues)
    {
        foreach (var queue in _snapshot.Queues)
        {
            if (queue.IsServerConnection)
            {
                if (_matcher.ModelsAcceptingDriver(queue.DriverName).Count > 0)
                {
                    _findings.Add(Findings.Info(FindingCode.ServerConnectionNotManaged,
                        $"Queue '{queue.Name}' is a connection to print server {queue.ServerName}; the server manages its driver and defaults.",
                        ("queue", queue.Name), ("server", queue.ServerName)));
                }
                continue;
            }

            var hosts = QueueLocator.HostsOf(queue, _snapshot.Ports);
            // A pooled queue (several ports) is a USB queue when one of its ports is.
            switch (QueueLocator.KindOf(queue.PortName, _snapshot.Ports))
            {
                case PortKind.Usb:
                    usbQueues.Add(queue);
                    break;
                case PortKind.StandardTcpIp when QueueLocator.HostOf(queue, _snapshot.Ports) is { } host:
                    AddToHost(tcpQueues, host, queue);
                    break;
                case PortKind.Wsd:
                    wsdQueues.Add(queue);
                    break;
                default:
                    if (hosts.Count == 0)
                        RejectUnlinkedPort(queue);
                    break;
            }
            // No network queue the setup can configure, but it may still print to network printers.
            if (QueueLocator.HostOf(queue, _snapshot.Ports) is null)
                LinkToHosts(queue, hosts, tcpQueues);
        }
    }

    /// <summary>
    /// A queue on ports that the setup cannot configure as a network queue (pooled ports, also the TCP/IP members of a pool with a
    /// USB port, or an LPR or Internet Port) still prints to each host they name (<see cref="QueueLocator.HostsOf"/>). When its
    /// driver names a supported model, or it is pooled over a Standard TCP/IP port and may be a TA printer's whatever its driver
    /// names (<see cref="ModelMatcher.MayBeTaQueue"/>, such as a class driver), it is listed among each of those hosts' queues, so
    /// the printer there is recognised under all its spellings and is then not set up automatically
    /// (<see cref="RejectUnconfigurableQueues"/>): a queue added for it would print through the same port. Such a queue whose
    /// driver names no single model on an LPR or Internet Port is compared with the printers it prints to instead
    /// (<see cref="ModelLessQueuesTo"/>).
    /// </summary>
    private void LinkToHosts(PrintQueueInfo queue, IReadOnlyList<string> hosts, Dictionary<string, List<PrintQueueInfo>> tcpQueues)
    {
        if (hosts.Count == 0)
            return;
        if (_matcher.ModelForDriver(queue.DriverName) is null)
        {
            if (!_matcher.MayBeTaQueue(queue))
                return;
            if (!HasStandardTcpIpPort(queue))
            {
                // A class-driver queue on an LPR or Internet Port (as Windows may add one for an IPP printer) is not configured; it
                // keeps its printer from being set up automatically only when it is that printer's ticket queue.
                _modelLessQueuesOnOtherPorts.Add((queue, hosts));
                return;
            }
        }
        _unconfigurableQueues.Add(queue.Name);
        foreach (string host in hosts)
            AddToHost(tcpQueues, host, queue);
    }

    /// <summary>Whether one of the queue's ports is a Standard TCP/IP port with a host (a pool's member, as the queue has no single host).</summary>
    private bool HasStandardTcpIpPort(PrintQueueInfo queue) =>
        QueueLocator.PortNames(queue.PortName).Any(p => QueueLocator.FindPort(_snapshot.Ports, p) is { Kind: PortKind.StandardTcpIp, HostAddress.Length: > 0 });

    private static void AddToHost(Dictionary<string, List<PrintQueueInfo>> tcpQueues, string host, PrintQueueInfo queue)
    {
        if (!tcpQueues.TryGetValue(host, out var list))
            tcpQueues[host] = list = [];
        list.Add(queue);
    }

    /// <summary>
    /// A queue whose driver names a supported model on a port that gives no printer's host (a redirected local port, a Standard
    /// TCP/IP port without a host, an LPR or Internet Port whose name does not give its host, pooled ports none of which does):
    /// it may be the ticket printer's, so it is left unchanged, no other printer is chosen without asking
    /// (<see cref="RejectedQueues"/>), and it may be a requested printer's (<see cref="UncomparedQueues"/>). Queues that print to
    /// FILE:, NUL:, PORTPROMPT:, LPT or COM ports are left out, as everywhere in detection.
    /// </summary>
    private void RejectUnlinkedPort(PrintQueueInfo queue)
    {
        if (QueueLocator.IsLocalPortName(queue.PortName) || _matcher.ModelForDriver(queue.DriverName) is not { } model)
            return;
        Claim([queue]);
        Rejected([queue]);
        _unlinkedQueues.Add(queue);
        _findings.Add(Findings.Info(FindingCode.QueueLeftUnchanged,
            $"Queue '{queue.Name}' uses driver '{queue.DriverName}' for {model.Id}, but {UnlinkedPort(queue)}, so the setup cannot link it to a printer; the queue is not configured.",
            ("queue", queue.Name), ("port", queue.PortName), ("driverModel", model.Id)));
    }

    /// <summary>Why the queue's port gives no printer's host, as a clause.</summary>
    private string UnlinkedPort(PrintQueueInfo queue)
    {
        if (QueueLocator.PortNames(queue.PortName).Count > 1)
            return $"it is pooled over ports {queue.PortName}, none of which is a USB port or names the host of a printer";
        return QueueLocator.KindOf(queue.PortName, _snapshot.Ports) switch
        {
            PortKind.StandardTcpIp => $"the host address of its Standard TCP/IP port {queue.PortName} could not be read",
            PortKind.Lpr => $"the name of its LPR Port {queue.PortName} does not give the printer's host reliably",
            PortKind.Internet => $"the URL of its Internet Port {queue.PortName} does not give the printer's host",
            PortKind.Local => $"its port {queue.PortName} is a local port (such as a redirected share), which names no printer",
            _ => $"its port {queue.PortName} belongs to a port monitor whose ports the setup cannot link to a printer",
        };
    }

    private void AddUsbDevices(List<PrintQueueInfo> usbQueues)
    {
        foreach (var device in _snapshot.Devices.Where(d => d.Bus == PnpBus.Usb))
        {
            var match = _matcher.MatchDevice(device);
            if (match.IsAmbiguous)
            {
                _findings.Add(Findings.Warning(FindingCode.UnsupportedPrinterDetected, match.Evidence, ("device", device.InstanceId)));
                continue;
            }
            if (match.Model is not { } model)
            {
                if (ModelMatcher.IsVendorDevice(device))
                {
                    _findings.Add(Findings.Info(FindingCode.UnsupportedPrinterDetected,
                        $"USB device {device.InstanceId} ({device.FriendlyName ?? device.Description ?? "unnamed"}) is a TA/UTAX/Kyocera printer that is not a supported model.",
                        ("device", device.InstanceId)));
                }
                continue;
            }

            // Also a pooled queue that lists the device's port, unless the device is not connected and another printer on the
            // queue's ports uses it: an earlier unit's node never claims (or repairs) that printer's queue, which is then judged by
            // the printers on its ports (AddUnattachedUsbQueues).
            var queues = QueueLocator.ForUsbDevice(usbQueues, device, _snapshot.Devices, _matcher);
            string key = PrinterCandidate.UsbKey(device.InstanceId);
            var primary = PrimaryQueue(key, queues, model);
            var evidence = new List<string> { match.Evidence };
            bool conflict = false;
            if (primary is not null)
            {
                evidence.Add(QueueEvidence(primary, model));
                conflict = IndicatesOtherModel(primary.DriverName, model, evidence);
            }
            evidence.Add(device.IsPresent ? "The device is connected." : "The device is not connected (known from an earlier connection).");
            if (!device.IsPresent)
            {
                _staleUsbDevices.Add((_findings.Count, device, model));
                _findings.Add(Findings.Warning(FindingCode.DeviceNotConnected,
                    $"The USB printer {model.Id} ({device.InstanceId}) is not connected.", ("device", device.InstanceId), ("model", model.Id)));
            }

            Claim(queues);
            _candidates.Add(new PrinterCandidate
            {
                Key = key,
                Kind = TargetKind.UsbDevice,
                Model = model,
                Confidence = MatchConfidence.Exact,
                Evidence = evidence,
                Connection = ConnectionType.Usb,
                Device = device,
                Queue = primary,
                AdditionalQueues = Others(queues, primary),
                IdentityConflict = conflict,
                PhysicallyAvailable = device.IsPresent,
            });
        }
    }

    /// <summary>
    /// USB queues no device candidate claimed: DriverOnly when no printer on their port says otherwise (no node, or a node
    /// without IDs). A switched-off printer still names itself, so a TA/UTAX/Kyocera node on the port that names no supported
    /// model, several or another model wins over the queue's driver whether or not it is connected, as on a TCP/IP port; so
    /// does a connected printer of another manufacturer (a moved ticket queue, or a port the USB monitor gave another
    /// printer). Such a queue is left unchanged, and it may be the ticket printer's (<see cref="RejectedQueues"/>). A pooled
    /// queue's printers are those on any of its ports.
    /// </summary>
    private void AddUnattachedUsbQueues(List<PrintQueueInfo> usbQueues)
    {
        foreach (var queue in usbQueues.Where(q => !_claimedQueues.Contains(q.Name)))
        {
            if (_matcher.ModelForDriver(queue.DriverName) is not { } model)
                continue;
            var nodes = _snapshot.Devices.Where(d => QueueLocator.UsesPort(queue, d.UsbPortName)).ToList();
            var (other, reason) = nodes.Select(d => (Device: (PnpPrinterDevice?)d, Reason: NotModelOf(d, model))).FirstOrDefault(n => n.Reason is not null);
            // A connected device whose own IDs matched no model and that is no TA/UTAX/Kyocera printer is another printer.
            if (other is null && nodes.FirstOrDefault(d => d is { IsPresent: true, HardwareIds.Count: > 0 }) is { } connected)
                (other, reason) = (connected, $"is another printer ('{connected.FriendlyName ?? connected.Description ?? "unnamed"}') whose IDs name no supported model");
            if (other is not null)
            {
                Claim([queue]);
                Rejected([queue]);
                _findings.Add(Findings.Warning(FindingCode.QueueLeftUnchanged,
                    $"Queue '{queue.Name}' uses driver '{queue.DriverName}' for {model.Id}, but the printer on its port {queue.PortName} ({other.InstanceId}{(other.IsPresent ? "" : ", not connected")}) {reason}; the queue is not configured.",
                    ("queue", queue.Name), ("port", queue.PortName), ("device", other.InstanceId), ("driverModel", model.Id)));
                continue;
            }

            Claim([queue]);
            var candidate = new PrinterCandidate
            {
                Key = PrinterCandidate.QueueKey(queue.Name),
                Kind = TargetKind.ExistingQueue,
                Model = model,
                Confidence = MatchConfidence.DriverOnly,
                Evidence =
                [
                    QueueEvidence(queue, model),
                    $"No connected USB printer is on port {queue.PortName}; the model comes from the queue's driver only.",
                ],
                Connection = ConnectionType.Usb,
                Device = nodes.FirstOrDefault(),
                Queue = queue,
                PhysicallyAvailable = false,
            };
            _candidates.Add(candidate);
            _unattachedUsbQueues.Add((_findings.Count, candidate));
            _findings.Add(Findings.Warning(FindingCode.DeviceNotConnected,
                $"Queue '{queue.Name}' uses USB port {queue.PortName}, but no connected printer is on that port.",
                ("queue", queue.Name), ("port", queue.PortName)));
        }
    }

    /// <summary>
    /// Groups the hosts of existing TCP/IP queues and the requested addresses into printers: spellings whose probes show the
    /// same device (<see cref="SameDevice"/>) are one printer, so a requested spelling never becomes a second candidate next
    /// to a queue of the same printer.
    /// </summary>
    private List<NetworkPrinter> NetworkPrinters(Dictionary<string, List<PrintQueueInfo>> tcpQueues, IReadOnlyList<string> requestedAddresses)
    {
        var spellings = tcpQueues.Keys
            .Concat(requestedAddresses.Where(a => !tcpQueues.ContainsKey(a)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var parent = Enumerable.Range(0, spellings.Count).ToArray();
        for (int j = 1; j < spellings.Count; j++)
        {
            for (int i = 0; i < j; i++)
            {
                if (Root(i) != Root(j) && SameDevice(spellings[i], spellings[j]) is not null)
                    parent[Root(j)] = Root(i);
            }
        }

        var byRoot = new Dictionary<int, NetworkPrinter>();
        var printers = new List<NetworkPrinter>();
        for (int i = 0; i < spellings.Count; i++)
        {
            if (!byRoot.TryGetValue(Root(i), out var printer))
            {
                byRoot[Root(i)] = printer = new NetworkPrinter();
                printers.Add(printer);
            }
            string spelling = spellings[i];
            printer.Spellings.Add(spelling);
            if (tcpQueues.TryGetValue(spelling, out var queues))
                printer.QueueHosts.Add((spelling, queues));
            if (IsRequested(spelling, requestedAddresses))
                printer.Requested.Add(spelling);
        }

        // The probes of one printer agree; the one that tells the most identifies it (SNMP answered, then reachable).
        foreach (var printer in printers)
            printer.Identity = BestIdentity(printer.Spellings);
        return printers;

        int Root(int index)
        {
            while (parent[index] != index)
                index = parent[index];
            return index;
        }
    }

    /// <summary>
    /// A network printer with existing TCP/IP queues. A device that names an unsupported model, several models or another
    /// vendor is never configured (<see cref="RejectDevice"/>), nor is a printer that a queue the setup cannot configure prints
    /// to (<see cref="RejectUnconfigurableQueues"/>), or whose ticket queue is such a queue that names no model
    /// (<see cref="RejectForModelLessTicketQueue"/>). Otherwise each queue host is its own candidate, keyed by that host (the
    /// host of the queue it configures): a preference naming one spelling selects that spelling's queue, and several queue
    /// hosts of one printer are the operator's choice (or Repair's ticket-form rule). Once one queue host has a candidate, a
    /// queue host whose printer and drivers name no model is another queue of that printer, possibly the ticket queue, so it is
    /// an unidentified candidate of its own, whatever the order of the hosts. Requested spellings without a queue are aliases of
    /// each of them, so Setup never adds a queue for them. A printer none of whose spellings the probe limit let through has
    /// its class-driver queues reported as not checked (<see cref="ReportUncheckedQueues"/>), and a queue that names no model on
    /// an LPR or Internet Port to a printer with candidates is reported with them (<see cref="ReportModelLessQueues"/>).
    /// </summary>
    private void AddQueuedNetworkPrinter(NetworkPrinter printer, IReadOnlyCollection<string> unprobedAddresses)
    {
        var identity = printer.Identity;
        var match = identity is null ? ModelMatch.None : _matcher.MatchNetwork(identity);
        var modelLess = ModelLessQueuesTo(printer);
        if (RejectDevice(printer, identity, match) || RejectUnconfigurableQueues(printer) || RejectForModelLessTicketQueue(printer, modelLess))
        {
            Rejected(printer.Queues);
            return;
        }

        var aliases = printer.RequestedOnly;
        var built = new List<PrinterCandidate>();
        var unidentifiedHosts = new List<(string Host, List<PrintQueueInfo> Queues)>();
        foreach (var (host, queues) in printer.QueueHosts)
        {
            if (QueueHostCandidate(printer, host, queues, aliases, identity, match, besideCandidate: false, out bool unidentified) is { } candidate)
                built.Add(candidate);
            else if (unidentified)
                unidentifiedHosts.Add((host, queues));
        }

        if (built.Count == 0)
        {
            if (unidentifiedHosts.Count > 0 && identity is not null && ModelMatcher.IsVendorIdentity(identity))
            {
                string host = printer.QueueHosts[0].Host;
                _findings.Add(Findings.Info(FindingCode.UnsupportedPrinterDetected,
                    $"The printer at {Spelled(printer, host)} ({Describe(identity)}) is a TA/UTAX/Kyocera printer that is not a supported model.", ("address", host)));
            }
            if (identity is null)
                ReportUncheckedQueues(unidentifiedHosts, unprobedAddresses);
            return;
        }

        foreach (var (host, queues) in unidentifiedHosts.Where(h => h.Queues.Any(_matcher.MayBeTaQueue)))
            built.Add(QueueHostCandidate(printer, host, queues, aliases, identity, match, besideCandidate: true, out _)!);

        string queuesOnHosts = Listed(built.Select(c => $"'{c.Queue!.Name}' on {c.NetworkAddress}"));
        (string, string?) queuesData = ("queues", string.Join(", ", built.Select(c => c.Queue!.Name)));
        (string, string?) candidatesData = ("candidates", string.Join(", ", built.Select(c => c.Key)));
        if (built.Count > 1)
        {
            // One printer, a queue on each of its spellings (its DNS name and its IP address): which one is the ticket
            // queue is not known here, so each is its own choice and none is folded into another. A queue host that names no
            // model is a choice whose model must be named, also when it does not answer (its printer may be switched off).
            string first = built[0].NetworkAddress!;
            string reasons = string.Join("; ", built.Skip(1).Select(c => SameDeviceReason(printer, c.NetworkAddress!, first)).Distinct());
            var unavailable = built.Where(c => c is { Model: null, PhysicallyAvailable: false }).ToList();
            string notAnswering = unavailable switch
            {
                [] => "",
                [var only] => $" '{only.Queue!.Name}' on {only.NetworkAddress} does not answer on the print port; its model must be named before it can be set up.",
                _ => $" {Listed(unavailable.Select(c => $"'{c.Queue!.Name}' on {c.NetworkAddress}"))} do not answer on the print port; their models must be named before they can be set up.",
            };
            _findings.Add(Findings.Warning(FindingCode.DuplicateQueues,
                $"Queues {queuesOnHosts} print to the same printer ({reasons}). They are separate choices: only the queue chosen is configured, and the others keep their settings.{notAnswering}",
                queuesData, candidatesData));
        }

        foreach (string requested in aliases)
        {
            string reason = SameDeviceReason(printer, requested, built[0].NetworkAddress!);
            _findings.Add(built is [var only]
                ? Findings.Info(FindingCode.DuplicateQueues,
                    $"{requested} reaches the printer of queue '{only.Queue!.Name}' on {only.NetworkAddress} ({reason}); that queue is used and no second queue is created for {requested}.",
                    ("address", requested), ("candidate", only.Key), ("queue", only.Queue.Name))
                : Findings.Info(FindingCode.DuplicateQueues,
                    $"{requested} reaches the printer of queues {queuesOnHosts} ({reason}); the queue chosen among them is used and no second queue is created for {requested}.",
                    ("address", requested), candidatesData, queuesData));
        }
        ReportModelLessQueues(printer, built, modelLess);
    }

    /// <summary>
    /// The queues whose driver names no single model on an LPR or Internet Port (<see cref="LinkToHosts"/>) that print to the
    /// printer, each with its host there: one of their hosts is a spelling of it or leads to the same IP address.
    /// </summary>
    private List<(PrintQueueInfo Queue, string Host)> ModelLessQueuesTo(NetworkPrinter printer)
    {
        var reaching = new List<(PrintQueueInfo Queue, string Host)>();
        foreach (var (queue, hosts) in _modelLessQueuesOnOtherPorts)
        {
            if (hosts.FirstOrDefault(h => Reaches(printer, h)) is { } host)
                reaching.Add((queue, host));
        }
        return reaching;
    }

    /// <summary>
    /// A printer whose ticket queue is one of the queues that name no model on an LPR or Internet Port and print to it
    /// (<see cref="ModelLessQueuesTo"/>), judged with the ticket-queue rule among the printer's own queues and that queue (the
    /// only one on the ticket form, else the only one with the configured queue name: <see cref="IsTicketQueueAmong"/>). That
    /// queue cannot be configured, and another queue of the printer or a queue added for it must not get the ticket settings
    /// while employees print to that one, so the printer gets no candidate, as for a queue the setup cannot configure that names
    /// a model (<see cref="RejectUnconfigurableQueues"/>), and that queue may be the ticket printer's (<see cref="RejectedQueues"/>).
    /// </summary>
    private bool RejectForModelLessTicketQueue(NetworkPrinter printer, List<(PrintQueueInfo Queue, string Host)> modelLess)
    {
        var ticketQueues = modelLess.Where(m => IsTicketQueueAmong(printer, m.Queue)).ToList();
        if (ticketQueues.Count == 0)
            return false;
        foreach (var (queue, host) in ticketQueues)
            ReportUnconfigurableQueue(printer, queue, host, ticketQueue: true);
        var queues = ticketQueues.Select(t => t.Queue).ToList();
        Claim(queues);
        Rejected(queues, whateverDriver: true);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="queue"/> is the one already set up as the ticket printer (<see cref="_ticketQueue"/>) among the
    /// printer's own queues and it.
    /// </summary>
    private bool IsTicketQueueAmong(NetworkPrinter printer, PrintQueueInfo queue) =>
        _ticketQueue?.Invoke([.. printer.Queues, queue]) is { } ticket && Same(ticket.Name, queue.Name);

    /// <summary>
    /// The queues that name no model on an LPR or Internet Port and print to a printer with candidates without being its ticket
    /// queue (<see cref="RejectForModelLessTicketQueue"/>): the setup does not configure them, so only the printer's own queue is,
    /// while employees may print to one of them. A warning, or information when the printer's only candidate configures the
    /// queue already set up as the ticket printer among its queues and that one.
    /// </summary>
    private void ReportModelLessQueues(NetworkPrinter printer, List<PrinterCandidate> built, List<(PrintQueueInfo Queue, string Host)> modelLess)
    {
        foreach (var (queue, _) in modelLess)
        {
            string prints = $"Queue '{queue.Name}' (driver '{queue.DriverName}', which names no single model) also prints to the printer of";
            string through = $"through port {queue.PortName}, which the setup does not configure";
            var data = new List<(string Key, string? Value)> { ("queue", queue.Name), ("port", queue.PortName), ("driver", queue.DriverName) };
            if (built is [var only])
            {
                string configured = only.Queue!.Name;
                data.AddRange([("address", only.NetworkAddress), ("candidate", only.Key)]);
                bool known = _ticketQueue?.Invoke([.. printer.Queues, queue]) is { } ticket && Same(ticket.Name, configured);
                _findings.Add(known
                    ? Findings.Info(FindingCode.DuplicateQueues,
                        $"{prints} queue '{configured}' on {only.NetworkAddress} {through}; '{configured}' is configured because it is already set up as the ticket printer, and '{queue.Name}' keeps its settings.",
                        [.. data])
                    : Findings.Warning(FindingCode.DuplicateQueues,
                        $"{prints} queue '{configured}' on {only.NetworkAddress} {through}; only '{configured}' is configured and '{queue.Name}' keeps its settings. Check which queue employees print to.",
                        [.. data]));
            }
            else
            {
                data.Add(("candidates", string.Join(", ", built.Select(c => c.Key))));
                _findings.Add(Findings.Warning(FindingCode.DuplicateQueues,
                    $"{prints} queues {Listed(built.Select(c => $"'{c.Queue!.Name}' on {c.NetworkAddress}"))} {through}; "
                    + $"only the queue chosen among them is configured and '{queue.Name}' keeps its settings. Check which queue employees print to.",
                    [.. data]));
            }
        }
    }

    /// <summary>
    /// The queue hosts of a printer none of whose spellings was probed: those the probe limit left out hold queues that may be a
    /// TA printer's and name no model, which built no candidate and were compared with no other printer. One of them may be the
    /// ticket printer's queue, so each is reported and counted (<see cref="UncheckedQueues"/>).
    /// </summary>
    private void ReportUncheckedQueues(List<(string Host, List<PrintQueueInfo> Queues)> unidentifiedHosts, IReadOnlyCollection<string> unprobedAddresses)
    {
        foreach (var (host, queues) in unidentifiedHosts.Where(h => unprobedAddresses.Contains(h.Host, StringComparer.OrdinalIgnoreCase)))
        {
            foreach (var queue in queues.Where(q => _matcher.MayBeTaQueue(q) && Same(QueueLocator.HostOf(q, _snapshot.Ports), host)))
            {
                _uncheckedQueues.Add(queue.Name);
                _findings.Add(Findings.Warning(FindingCode.NetworkIdentityUnavailable,
                    $"Queue '{queue.Name}' on {host} was not checked because the limit of network probes was reached; it may be the ticket printer's queue. "
                    + "Raise connection.maxNetworkProbes in the configuration, or name the ticket queue's host with --address or printerAddress.",
                    ("queue", queue.Name), ("address", host), ("reason", "probeLimit")));
            }
        }
    }

    /// <summary>
    /// Queues already set up as the ticket printer (the only one judged with <see cref="_ticketQueue"/>: on the ticket form, or
    /// with the configured queue name) that may be a TA printer's but that no candidate configures and no other rule rejected or
    /// left unchecked: a WSD, LPR or Internet Port queue whose driver names no single model and whose printer has no queue the
    /// setup can configure, or a queue to a printer that got no candidate (it does not answer, or does not report its model). The
    /// ticket printer may be its printer, so it counts as a rejected queue and no other printer is taken as the ticket printer
    /// without asking. The queue of a device that reports another vendor or an unsupported model is not one, nor is an LPR or
    /// Internet Port queue that prints to a printer with candidates (that printer's ticket-queue rule judges it:
    /// <see cref="RejectForModelLessTicketQueue"/>, <see cref="ReportModelLessQueues"/>).
    /// </summary>
    private void ReportUnmatchedTicketQueues()
    {
        var configured = ConfiguredQueueNames();
        var toCandidates = _modelLessQueuesOnOtherPorts
            .Where(o => o.Hosts.Any(h => _candidates.Any(c => c is { Kind: TargetKind.NetworkAddress, NetworkAddress: not null } && Reaches(c, h))))
            .Select(o => o.Queue.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var queue in _snapshot.Queues)
        {
            if (queue.IsServerConnection || QueueLocator.IsLocalPortName(queue.PortName) || configured.Contains(queue.Name) || _otherDeviceQueues.Contains(queue.Name)
                || toCandidates.Contains(queue.Name)
                || _rejectedQueues.Contains(queue.Name, StringComparer.OrdinalIgnoreCase) || _uncheckedQueues.Contains(queue.Name, StringComparer.OrdinalIgnoreCase)
                || !_matcher.MayBeTaQueue(queue) || _ticketQueue?.Invoke([queue]) is null)
                continue;
            var model = _matcher.ModelForDriver(queue.DriverName);
            string uses = model is null ? $"uses driver '{queue.DriverName}', which names no single model," : $"uses driver '{queue.DriverName}' for {model.Id}";
            // On a Standard TCP/IP port its host names the printer, which then only needs its model confirmed.
            string? host = QueueLocator.HostOf(queue, _snapshot.Ports);
            string detail = host is null
                ? $"Queue '{queue.Name}' {uses} is already set up as the ticket printer and prints through port {queue.PortName}, but it leads to no printer the setup "
                    + "can configure, so it is not configured, and no other printer is taken as the ticket printer without asking. If its printer is the ticket "
                    + "printer, put the queue on a Standard TCP/IP port to that printer, or delete it and name the printer with printerAddress, then run the setup again."
                : $"Queue '{queue.Name}' {uses} is already set up as the ticket printer and prints to {host}, whose printer does not report its model or does not "
                    + "answer, so it is not configured, and no other printer is taken as the ticket printer without asking. If its printer is the ticket printer, "
                    + $"name that host with printerAddress {host} (silent: --address {host} with --model and --confirm-model; switch the printer on first if it "
                    + "does not answer), then run the setup again: the queue itself is then configured.";
            _rejectedQueues.Add(queue.Name);
            _findings.Add(Findings.Warning(FindingCode.QueueLeftUnchanged, detail,
                ("queue", queue.Name), ("port", queue.PortName), ("address", host), ("reason", "unmatchedTicketQueue"),
                ("driverModel", model?.Id), ("driver", model is null ? queue.DriverName : null)));
        }
    }

    /// <summary>
    /// A device whose own identity names an unsupported model, several models or another vendor: its queues are never
    /// configured. That is a mismatch (a warning) when a queue's driver claims a supported model or one of its spellings was
    /// requested; otherwise it is an unrelated printer: information for a TA/UTAX/Kyocera device, nothing for another vendor's.
    /// A queue of it that a candidate configures (a pool that is a USB printer's queue) stays that printer's (<see cref="LeftQueues"/>).
    /// </summary>
    private bool RejectDevice(NetworkPrinter printer, NetworkIdentity? identity, ModelMatch match)
    {
        string host = printer.QueueHosts[0].Host;
        var (notConfigured, kept) = LeftQueues(printer);
        string notConfiguredClause = notConfigured.Length == 0 ? "" : $"; its queues ({notConfigured}) are not configured";
        bool claimed = DriverModels(printer.Queues).Count > 0 || printer.Requested.Count > 0;
        if (match.IsAmbiguous)
        {
            // Several models at the same level: the queues' drivers must not decide between them.
            string notConfiguredSentence = notConfigured.Length == 0 ? "" : $" Its queues ({notConfigured}) on {Spelled(printer, host)} are not configured.";
            string detail = $"{match.Evidence}{notConfiguredSentence}{kept}";
            _findings.Add(claimed
                ? Findings.Warning(FindingCode.NetworkIdentityMismatch, detail, ("address", host))
                : Findings.Info(FindingCode.UnsupportedPrinterDetected, detail, ("address", host)));
            return true;
        }
        if (identity is not null && _matcher.ReportsUnsupportedModel(identity, out string reported))
        {
            // The device itself says it is another model: it wins over the queues' drivers.
            _otherDeviceQueues.UnionWith(printer.Queues.Select(q => q.Name));
            if (claimed)
            {
                _findings.Add(Findings.Warning(FindingCode.NetworkIdentityMismatch,
                    $"The device at {Spelled(printer, host)} reports itself as '{reported}', which is not a supported model{notConfiguredClause}.{kept}",
                    ("address", host), ("reported", reported)));
            }
            else if (ModelMatcher.IsKyoceraFamily(identity))
            {
                _findings.Add(Findings.Info(FindingCode.UnsupportedPrinterDetected,
                    $"The printer at {Spelled(printer, host)} reports itself as '{reported}', a TA/UTAX/Kyocera printer that is not a supported model{notConfiguredClause}.{kept}",
                    ("address", host), ("reported", reported)));
            }
            return true;
        }
        if (identity is not null && match.Model is null && ModelMatcher.IsInformative(identity) && !ModelMatcher.IsKyoceraFamily(identity))
        {
            // Another vendor's printer: an approved driver on its queue does not make it a TA printer.
            _otherDeviceQueues.UnionWith(printer.Queues.Select(q => q.Name));
            if (claimed)
            {
                _findings.Add(Findings.Warning(FindingCode.NetworkIdentityMismatch,
                    $"The device at {Spelled(printer, host)} reports itself as {Describe(identity)}, which is not a supported TA printer{notConfiguredClause}.{kept}",
                    ("address", host)));
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// The queues of a printer that is not configured, for messages: the names of those no candidate configures (empty when there
    /// are none), and a sentence for each queue a candidate configures (a pool that is a USB printer's queue), which stays that
    /// printer's and also prints to this device.
    /// </summary>
    private (string NotConfigured, string Kept) LeftQueues(NetworkPrinter printer)
    {
        var notConfigured = new List<PrintQueueInfo>();
        var kept = new List<string>();
        foreach (var queue in printer.Queues)
        {
            if (CandidateWith(queue) is { } owner)
                kept.Add($" Queue '{queue.Name}' stays the queue of the USB printer {owner.Model?.Id} ({owner.Key}) and also prints to this device.");
            else
                notConfigured.Add(queue);
        }
        return (QueueNames(notConfigured), string.Concat(kept));
    }

    /// <summary>
    /// A printer that a queue the setup cannot configure prints to (<see cref="LinkToHosts"/>: pooled ports, or an LPR or
    /// Internet Port, when its driver names a supported model; pooled over Standard TCP/IP ports, when it names no single model).
    /// That queue may be the ticket printer's, and Setup would otherwise add a second queue next to it, even on the same port, or
    /// configure another queue of the printer, so the printer gets no candidate at all, under none of its spellings, as a device
    /// that names another model gets none. Each such queue is reported with the printer and what to do; a pool that is also a USB
    /// printer's queue stays that printer's queue. One that names no model is not among the printer's queues of a supported
    /// model, but it may be the ticket printer's as well (<see cref="RejectedQueues"/>).
    /// </summary>
    private bool RejectUnconfigurableQueues(NetworkPrinter printer)
    {
        var unconfigurable = printer.Queues.Where(q => _unconfigurableQueues.Contains(q.Name)).ToList();
        if (unconfigurable.Count == 0)
            return false;

        foreach (var queue in unconfigurable)
            ReportUnconfigurableQueue(printer, queue, printer.QueueHosts.First(h => h.Queues.Contains(queue)).Host, ticketQueue: false);
        Claim(unconfigurable);
        Rejected(unconfigurable.Where(q => _matcher.ModelForDriver(q.DriverName) is null), whateverDriver: true);
        return true;
    }

    /// <summary>
    /// Reports a queue the setup cannot configure because of which the printer it prints to through <paramref name="host"/> is not
    /// set up automatically: the queue, its driver, its ports, every spelling of the printer and what to do (one warning per
    /// printer). <paramref name="ticketQueue"/>: the queue names no model and is the printer's ticket queue. A pool that is also a
    /// USB printer's queue stays that printer's queue.
    /// </summary>
    private void ReportUnconfigurableQueue(NetworkPrinter printer, PrintQueueInfo queue, string host, bool ticketQueue)
    {
        const string reason = "because a queue added for it, or another of its queues, would be configured while employees may print to this one";
        var model = _matcher.ModelForDriver(queue.DriverName);
        bool pooled = QueueLocator.PortNames(queue.PortName).Count > 1;
        string fix = pooled ? "un-pool the queue or put it on a single Standard TCP/IP port" : "put the queue on a Standard TCP/IP port";
        string advice = $"If the printer at {host} is the ticket printer, {fix}, or delete the queue if it is not used, then run the setup again.";
        string uses = model is null ? $"uses driver '{queue.DriverName}', which names no single model," : $"uses driver '{queue.DriverName}' for {model.Id}";
        string setUp = ticketQueue ? " is already set up as the ticket printer" : "";
        string detail = CandidateWith(queue) is { } owner
            ? $"Queue '{queue.Name}' is pooled over ports {queue.PortName}: it is the queue of the USB printer {owner.Model?.Id} ({owner.Key}), and it also prints to the printer at {Spelled(printer, host)}. "
                + $"That printer is not set up automatically, {reason}. {advice}"
            : $"Queue '{queue.Name}' {uses}{setUp} and prints to the printer at {Spelled(printer, host)} on {UnconfigurablePorts(queue, pooled)}, which the setup cannot configure. "
                + $"The queue is not configured, and the printer is not set up automatically, {reason}. {advice}";
        _findings.Add(Findings.Warning(FindingCode.QueueLeftUnchanged, detail,
            ("queue", queue.Name), ("port", queue.PortName), ("address", host), ("driverModel", model?.Id), ("driver", model is null ? queue.DriverName : null)));
    }

    /// <summary>The ports of a queue linked to its hosts (<see cref="LinkToHosts"/>), for messages.</summary>
    private string UnconfigurablePorts(PrintQueueInfo queue, bool pooled) =>
        pooled ? $"pooled ports {queue.PortName}"
        : QueueLocator.KindOf(queue.PortName, _snapshot.Ports) == PortKind.Lpr ? $"LPR Port {queue.PortName}"
        : $"Internet Port {queue.PortName}";

    /// <summary>
    /// The candidate of one queue host: the printer's identity (from whichever of its spellings tells the most) and that host's
    /// queue drivers decide the model; reachability is the host's own probe. Null when its queues cannot be configured;
    /// <paramref name="unidentified"/> then tells whether neither the printer nor the host's drivers name a model (rather than
    /// drivers of different models). <paramref name="besideCandidate"/>: another queue host of the printer has a candidate, so
    /// such an unidentified host is a candidate as well, whose model must be named, and whose own probe alone tells whether it
    /// answers.
    /// </summary>
    private PrinterCandidate? QueueHostCandidate(
        NetworkPrinter printer,
        string host,
        List<PrintQueueInfo> queues,
        List<string> aliases,
        NetworkIdentity? identity,
        ModelMatch match,
        bool besideCandidate,
        out bool unidentified)
    {
        unidentified = false;
        var driverModels = DriverModels(queues);
        var evidence = new List<string>();
        ModelDefinition? model;
        MatchConfidence confidence;
        if (match.Confidence == MatchConfidence.Exact)
        {
            (model, confidence) = (match.Model, MatchConfidence.Exact);
            evidence.Add(match.Evidence);
        }
        else if (match.Model is { } probable && !(driverModels is [var only] && Same(only.Id, probable.Id)))
        {
            // A probable identity that the queues' driver does not confirm needs the operator.
            (model, confidence) = (probable, MatchConfidence.Probable);
            evidence.Add(match.Evidence);
        }
        else if (driverModels.Count == 1)
        {
            // The device names no model (or the driver's model): DriverOnly means the device did not say otherwise.
            (model, confidence) = (driverModels[0], MatchConfidence.DriverOnly);
            if (match.Model is not null)
                evidence.Add(match.Evidence);
            evidence.Add("The model comes from the queue's driver; the printer's own identity was not confirmed.");
        }
        else if (driverModels.Count > 1)
        {
            // Left unchanged: the drivers cannot decide the model, and the device did not.
            _findings.Add(Findings.Warning(FindingCode.QueueLeftUnchanged,
                $"Queues for {Spelled(host, aliases)} use drivers of different models ({string.Join(", ", driverModels.Select(m => m.Id))}); the printer is not configured automatically.",
                ("address", host)));
            Rejected(queues);
            return null;
        }
        else if ((IsRequested(host, printer.Requested) || aliases.Count > 0) && identity is { RawPortReachable: true })
        {
            // An identity that says nothing, or names TA/UTAX/Kyocera: the operator can identify the model.
            (model, confidence) = (null, MatchConfidence.None);
            evidence.Add($"The printer at {host} answers on its print port but its model could not be identified.");
        }
        else if (besideCandidate)
        {
            // Another queue of a printer that has a candidate under another spelling: it may be the ticket queue.
            (model, confidence) = (null, MatchConfidence.None);
            evidence.Add($"Neither the printer nor the drivers of its queues on {host} name a model; another spelling of the printer has queues of its own, so these queues are a separate candidate.");
        }
        else
        {
            unidentified = true;
            return null;
        }

        string key = PrinterCandidate.NetworkKey(host);
        var primary = PrimaryQueue(key, queues, model)!;
        // Another queue host's probe would make this candidate "at" that host's address, so it is never used here; beside
        // another queue host's candidate, only this host's own probe tells whether the printer answers there.
        var keyIdentity = besideCandidate ? Identity(host) : Identity(host) ?? BestIdentity(aliases);
        evidence.Add(QueueEvidence(primary, model));
        bool conflict = false;
        if (model is not null && confidence is MatchConfidence.Exact or MatchConfidence.Probable)
            conflict = IndicatesOtherModel(primary.DriverName, model, evidence);
        AddSpellingEvidence(printer, host, evidence);
        AddReachability(host, keyIdentity, evidence);

        Claim(queues);
        var candidate = new PrinterCandidate
        {
            Key = key,
            Kind = TargetKind.NetworkAddress,
            Model = model,
            Confidence = confidence,
            Evidence = evidence,
            Connection = ConnectionType.TcpIp,
            NetworkAddress = host,
            NetworkAliases = aliases,
            NetworkIdentity = keyIdentity,
            Queue = primary,
            AdditionalQueues = Others(queues, primary),
            IdentityConflict = conflict,
            PhysicallyAvailable = keyIdentity?.RawPortReachable ?? false,
        };
        _candidates.Add(candidate);
        return candidate;
    }

    /// <summary>
    /// A printer only requested by address (no queue on any of its spellings). Its key, the address a new port uses, is the
    /// first requested spelling that answers on the print port (the first one when none does).
    /// </summary>
    private void AddRequestedNetworkPrinter(NetworkPrinter printer, IReadOnlyCollection<string> unprobedAddresses)
    {
        string address = printer.Spellings.FirstOrDefault(s => Identity(s)?.RawPortReachable == true) ?? printer.Spellings[0];
        if (unprobedAddresses.Contains(address, StringComparer.OrdinalIgnoreCase))
        {
            _findings.Add(Findings.Warning(FindingCode.NetworkIdentityUnavailable,
                $"{address} was not probed because the limit of network probes was reached.", ("address", address), ("reason", "probeLimit")));
            return;
        }
        if (printer.Identity is not { } identity)
            return;

        var match = _matcher.MatchNetwork(identity);
        if (match.IsAmbiguous)
        {
            _findings.Add(Findings.Warning(FindingCode.NetworkIdentityMismatch, match.Evidence, ("address", address)));
            return;
        }
        if (_matcher.ReportsUnsupportedModel(identity, out string reported))
        {
            _findings.Add(Findings.Warning(FindingCode.NetworkIdentityMismatch,
                $"The device at {Spelled(printer, address)} reports itself as '{reported}', which is not a supported model.", ("address", address), ("reported", reported)));
            return;
        }

        var keyIdentity = Identity(address) ?? identity;
        var evidence = new List<string>();
        AddReachability(address, keyIdentity, evidence);
        if (!identity.SnmpResponded)
        {
            _findings.Add(Findings.Warning(FindingCode.NetworkIdentityUnavailable,
                $"{address} did not answer SNMP, so its model cannot be read.", ("address", address), ("error", keyIdentity.Error)));
        }

        ModelDefinition? model = match.Model;
        if (model is not null)
        {
            evidence.Insert(0, match.Evidence);
        }
        else if (ModelMatcher.IsInformative(identity) && !ModelMatcher.IsKyoceraFamily(identity))
        {
            _findings.Add(Findings.Warning(FindingCode.NetworkIdentityMismatch,
                $"The device at {Spelled(printer, address)} reports itself as {Describe(identity)}, which is not a supported TA printer.", ("address", address)));
            return;
        }
        else if (keyIdentity.RawPortReachable)
        {
            evidence.Insert(0, ModelMatcher.IsInformative(identity)
                ? $"{address} reports {Describe(identity)}, which names no configured model; the operator must choose the model."
                : $"{address} answers on its print port but did not report its model.");
        }
        else
        {
            return; // Unreachable and unidentified: only the findings remain.
        }
        // A queue that names no model on an LPR or Internet Port and is already this printer's ticket queue: the queue Setup
        // would add for it must not get the ticket settings while employees print to that one.
        if (RejectForModelLessTicketQueue(printer, ModelLessQueuesTo(printer)))
            return;
        AddSpellingEvidence(printer, address, evidence);

        _candidates.Add(new PrinterCandidate
        {
            Key = PrinterCandidate.NetworkKey(address),
            Kind = TargetKind.NetworkAddress,
            Model = model,
            Confidence = model is null ? MatchConfidence.None : match.Confidence,
            Evidence = evidence,
            Connection = ConnectionType.TcpIp,
            NetworkAddress = address,
            NetworkAliases = Aliases(printer, address),
            NetworkIdentity = keyIdentity,
            PhysicallyAvailable = keyIdentity.RawPortReachable,
        });
    }

    /// <summary>
    /// WSD queues: Exact when one connected WSD printer is the model of the queue's driver, else DriverOnly. A WSD port cannot
    /// be linked to its printer, so the device-wins rule is applied narrowly: when no connected WSD printer is the queue's
    /// model and a connected TA/UTAX/Kyocera WSD printer names an unsupported model, several models or another model, and no
    /// other WSD queue can be that printer's own, the queue may be that printer's (a successor given the old driver) and is
    /// not configured. The price: a switched-off ticket printer next to such a printer must be switched on first.
    /// </summary>
    private void AddWsdQueues(List<PrintQueueInfo> wsdQueues)
    {
        var connected = _snapshot.Devices
            .Where(d => d.Bus == PnpBus.Wsd && d.IsPresent)
            .Select(d => (Device: d, Match: _matcher.MatchDevice(d)))
            .ToList();
        foreach (var queue in wsdQueues)
        {
            if (_matcher.ModelForDriver(queue.DriverName) is not { } model)
                continue;

            var devices = connected.Where(x => x.Match.Confidence == MatchConfidence.Exact && Same(x.Match.Model?.Id, model.Id)).ToList();
            if (devices.Count == 0 && OtherWsdPrinters(queue, model, connected, wsdQueues) is { Count: > 0 } others)
            {
                Claim([queue]);
                Rejected([queue]);
                var (other, otherMatch, _) = others[0];
                string printers = Listed(others.Select(o => $"{o.Device.InstanceId} ({o.Device.FriendlyName ?? o.Device.Description ?? "unnamed"}) {o.Reason}"));
                var (those, it) = others.Count == 1 ? ("that printer", "it") : ("those printers", "one of them");
                _findings.Add(Findings.Warning(FindingCode.QueueLeftUnchanged,
                    $"WSD queue '{queue.Name}' uses driver '{queue.DriverName}' for {model.Id}, but no connected WSD printer is a {model.Id}: {printers}. "
                    + $"No other WSD queue can belong to {those} and a WSD port cannot be linked to its printer, so the queue may belong to {it} and is not configured. "
                    + $"If '{queue.Name}' is the ticket printer, switch it on and run the setup again.",
                    ("queue", queue.Name), ("port", queue.PortName), ("device", other.InstanceId), ("driverModel", model.Id), ("deviceModel", otherMatch.Model?.Id)));
                continue;
            }
            var evidence = new List<string> { QueueEvidence(queue, model) };
            PnpPrinterDevice? device = null;
            if (devices.Count == 1)
            {
                device = devices[0].Device;
                evidence.Add(devices[0].Match.Evidence);
            }
            else
            {
                evidence.Add("The model comes from the queue's driver; no single connected WSD device confirms it.");
            }

            Claim([queue]);
            _candidates.Add(new PrinterCandidate
            {
                Key = PrinterCandidate.QueueKey(queue.Name),
                Kind = TargetKind.ExistingQueue,
                Model = model,
                Confidence = device is null ? MatchConfidence.DriverOnly : MatchConfidence.Exact,
                Evidence = evidence,
                Connection = ConnectionType.Wsd,
                Device = device,
                Queue = queue,
                PhysicallyAvailable = device is not null,
            });
        }
    }

    /// <summary>
    /// Connected WSD printers that show they are not a <paramref name="model"/> (<see cref="NotModelOf"/>) and that no other WSD
    /// queue can belong to: none on another WSD port has a driver for the printer's model, or a driver that names no single
    /// supported model (a class or other vendor driver, which any printer's own queue may use).
    /// </summary>
    private List<(PnpPrinterDevice Device, ModelMatch Match, string Reason)> OtherWsdPrinters(
        PrintQueueInfo queue,
        ModelDefinition model,
        List<(PnpPrinterDevice Device, ModelMatch Match)> connected,
        List<PrintQueueInfo> wsdQueues)
    {
        var otherQueueModels = wsdQueues.Where(q => !Same(q.PortName, queue.PortName)).Select(q => _matcher.ModelForDriver(q.DriverName)).ToList();
        var others = new List<(PnpPrinterDevice Device, ModelMatch Match, string Reason)>();
        foreach (var (device, match) in connected)
        {
            if (NotModelOf(device, model, match) is not { } reason)
                continue;
            bool hasOwnQueue = otherQueueModels.Any(m => m is null
                || (match.Model is { } named ? Same(named.Id, m.Id) : match.AmbiguousModels.Any(a => Same(a.Id, m.Id))));
            if (!hasOwnQueue)
                others.Add((device, match, reason));
        }
        return others;
    }

    /// <summary>
    /// Why a printer's own device node shows it is not a <paramref name="model"/>: it matches several models or another
    /// supported model, or it is a TA/UTAX/Kyocera printer that matches none. Null when it is one, or tells nothing (no hardware
    /// IDs, or another vendor's printer).
    /// </summary>
    private string? NotModelOf(PnpPrinterDevice device, ModelDefinition model, ModelMatch? known = null)
    {
        if (device.HardwareIds.Count == 0)
            return null;
        var match = known ?? _matcher.MatchDevice(device);
        if (match.IsAmbiguous)
            return $"matches several models ({string.Join(", ", match.AmbiguousModels.Select(m => m.Id))})";
        if (match.Model is { } named)
            return Same(named.Id, model.Id) ? null : $"is a {named.Id}";
        return ModelMatcher.IsVendorDevice(device) ? "is a TA/UTAX/Kyocera printer that is not a supported model" : null;
    }

    /// <summary>
    /// Windows keeps a node for every USB printer ever connected. A node that is not present while a USB printer of its model is
    /// connected is an earlier connection (a replaced or moved unit) that selection leaves out
    /// (<see cref="PrinterCandidate.IsSupersededAmong"/>), so its warning becomes information, which names the node's own queue
    /// that is left out with it (Data "queue"). A queue on its port that is the queue of a printer selection keeps (a pool over
    /// the connected unit's port) is not left out with it, which the note says as well (Data "keptQueue"); without a queue of
    /// its own, only the node is left out. Next to any other printer it stays a warning: a switched-off ticket printer is not
    /// connected either.
    /// </summary>
    private void ReportStaleUsbDevices()
    {
        foreach (var (index, device, model) in _staleUsbDevices)
        {
            if (_candidates.FirstOrDefault(c => Same(c.Key, PrinterCandidate.UsbKey(device.InstanceId))) is not { } node || !node.IsSupersededAmong(_candidates))
                continue;
            string earlier = $"The USB printer {model.Id} ({device.InstanceId}) is known only from an earlier connection";
            var kept = KeptQueueOnPort(node, device);
            string keptSentence = kept is ({ } keptQueue, { } owner)
                ? $" Queue '{keptQueue.Name}', which also prints to its port {device.UsbPortName}, is the queue of {owner.Model?.Id} ({owner.Key}) and is not left out."
                : "";
            // The node's own queue is left out with it, unless a printer selection keeps uses it as well.
            var own = node.Queue is { } queue && !Same(queue.Name, kept?.Queue.Name) ? queue : null;
            _findings[index] = own is not null
                ? Findings.Info(FindingCode.DeviceNotConnected,
                    $"{earlier}; it and its queue '{own.Name}' are left out because a USB printer of the same model is connected.{keptSentence}",
                    ("device", device.InstanceId), ("model", model.Id), ("target", node.Key), ("queue", own.Name), ("keptQueue", kept?.Queue.Name))
                : kept is not null
                ? Findings.Info(FindingCode.DeviceNotConnected,
                    $"{earlier}; only that device node is left out, because a USB printer of the same model is connected.{keptSentence}",
                    ("device", device.InstanceId), ("model", model.Id), ("target", node.Key), ("keptQueue", kept.Value.Queue.Name))
                : Findings.Info(FindingCode.DeviceNotConnected,
                    $"{earlier} and has no queue; it is left out because a USB printer of the same model is connected.",
                    ("device", device.InstanceId), ("model", model.Id), ("target", node.Key));
        }
    }

    /// <summary>A queue that prints to the node's port and is also the queue of another candidate selection keeps, with that candidate.</summary>
    private (PrintQueueInfo Queue, PrinterCandidate Owner)? KeptQueueOnPort(PrinterCandidate node, PnpPrinterDevice device)
    {
        foreach (var queue in QueueLocator.OnPort(_snapshot.Queues, device.UsbPortName))
        {
            if (_candidates.FirstOrDefault(c => !ReferenceEquals(c, node) && HasQueue(c, queue) && !c.IsSupersededAmong(_candidates)) is { } owner)
                return (queue, owner);
        }
        return null;
    }

    /// <summary>
    /// A USB queue without a connected printer on its port next to a connected USB printer of its model is an earlier unit's
    /// queue (its node removed, or its port unknown) that selection leaves out, as <see cref="ReportStaleUsbDevices"/> does for
    /// such nodes; its warning becomes information.
    /// </summary>
    private void ReportSupersededUsbQueues()
    {
        foreach (var (index, candidate) in _unattachedUsbQueues)
        {
            if (!candidate.IsSupersededAmong(_candidates))
                continue;
            var queue = candidate.Queue!;
            _findings[index] = Findings.Info(FindingCode.DeviceNotConnected,
                $"Queue '{queue.Name}' uses USB port {queue.PortName}, where no printer is connected; it is left out because a USB printer of the same model ({candidate.Model!.Id}) is connected.",
                ("queue", queue.Name), ("port", queue.PortName), ("target", candidate.Key), ("model", candidate.Model.Id));
        }
    }

    /// <summary>
    /// A requested address without a queue may be the printer of a queue whose address cannot be compared with it: a WSD queue
    /// (its network address is not known), a TCP/IP queue whose host name was not probed or did not resolve, or a queue whose
    /// port cannot be linked to a printer at all. Setup would then add a second queue for it: for a printer of that queue's
    /// model, and for one that does not report its model (whatever model the operator then names), whatever the queue's model.
    /// A queue there that is no candidate counts as well when it may be a TA printer's (<see cref="UncomparedQueues"/>); one
    /// whose driver names no single model (such as the class driver Windows gives a network printer) may be the requested
    /// printer's whatever its model. Such a queue on an LPR or Internet Port (also pooled over such ports) whose host is the
    /// requested printer (one of its spellings, or the same IP address) does print to it: Setup would add a second queue for it.
    /// </summary>
    private void ReportPossibleDuplicates(Dictionary<string, List<PrintQueueInfo>> tcpQueues, List<PrintQueueInfo> wsdQueues)
    {
        var requested = _candidates.Where(c => c is { Kind: TargetKind.NetworkAddress, Queue: null }).ToList();
        if (requested.Count == 0)
            return;
        var uncompared = UncomparedQueues(tcpQueues, wsdQueues);
        var configured = ConfiguredQueueNames();
        foreach (var candidate in requested)
        {
            string address = candidate.NetworkAddress!;
            var model = candidate.Model;
            string printer = model is null ? $"The printer at {address} has no queue and does not report its model" : $"The printer at {address} has no queue";
            string named = model is null ? " once its model is named" : "";
            foreach (var (queue, queueModel, kind, unknown) in uncompared.Where(u => model is null || u.Model is null || Same(u.Model.Id, model.Id)))
            {
                string isFor = queueModel is null ? $"may be for any model (its driver '{queue.DriverName}' names no single model)"
                    : model is null ? $"is for a {queueModel.Id}"
                    : $"is also for a {model.Id}";
                _findings.Add(Findings.Warning(FindingCode.DuplicateQueues,
                    $"{printer}, but {kind} '{queue.Name}' {isFor} and {unknown}, so they may be one printer, and Setup would add a second queue for {address}{named}.",
                    ("address", address), ("queue", queue.Name), ("model", queueModel?.Id), ("driver", queueModel is null ? queue.DriverName : null)));
            }

            string withoutConfigurableQueue = model is null
                ? $"The printer at {address} does not report its model and has no queue the setup can configure"
                : $"The printer at {address} has no queue the setup can configure";
            foreach (var (queue, hosts) in _modelLessQueuesOnOtherPorts.Where(o => !configured.Contains(o.Queue.Name) && o.Hosts.Any(h => Reaches(candidate, h))))
            {
                // Data port: a queue that does print to this printer (the window names it first).
                _findings.Add(Findings.Warning(FindingCode.DuplicateQueues,
                    $"{withoutConfigurableQueue}, but queue '{queue.Name}' (driver '{queue.DriverName}', which names no single model) prints to it "
                    + $"through port {queue.PortName}, which the setup does not configure, so Setup would add a second queue for {address}{named}.",
                    ("address", address), ("queue", queue.Name), ("driver", queue.DriverName), ("port", queue.PortName)));
            }
        }
    }

    /// <summary>Whether <paramref name="host"/> is the printer of <paramref name="candidate"/>: one of its spellings, or it leads to the same IP address.</summary>
    private bool Reaches(PrinterCandidate candidate, string host) =>
        candidate.IsAtAddress(host)
        || ResolvedOf(host) is { } resolved && (Same(resolved, ResolvedOf(candidate.NetworkAddress!)) || Same(resolved, candidate.NetworkIdentity?.ResolvedAddress));

    /// <summary>Whether <paramref name="host"/> is <paramref name="printer"/>: one of its spellings, or it leads to the same IP address as one of them.</summary>
    private bool Reaches(NetworkPrinter printer, string host) =>
        printer.Spellings.Any(s => Same(s, host))
        || ResolvedOf(host) is { } resolved && printer.Spellings.Any(s => Same(ResolvedOf(s), resolved));

    /// <summary>The names of the queues candidates configure (their own and their printer's other queues).</summary>
    private HashSet<string> ConfiguredQueueNames() =>
        _candidates.SelectMany(c => c.AdditionalQueues.Prepend(c.Queue)).OfType<PrintQueueInfo>()
            .Select(q => q.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Queues whose printer cannot be compared with a requested address, with the model their candidate (or driver) names, what
    /// kind of queue they are and why their printer is not known, as clauses: the queues candidates configure on a WSD port or on
    /// a host name that was not probed or did not resolve, the queues there that no candidate configures but that may be a TA
    /// printer's (<see cref="ModelMatcher.MayBeTaQueue"/>, as for the queue hosts detection probes; a class-driver queue, or one
    /// left unchanged, yields no candidate but may be the printer's own), and the queues whose port gives no printer's host at
    /// all (<see cref="RejectUnlinkedPort"/>). A queue pooled over several such hosts is listed once.
    /// </summary>
    private List<(PrintQueueInfo Queue, ModelDefinition? Model, string Kind, string Unknown)> UncomparedQueues(Dictionary<string, List<PrintQueueInfo>> tcpQueues, List<PrintQueueInfo> wsdQueues)
    {
        const string wsdUnknown = "the WSD printer's network address is not known";
        static string Unresolved(string host) => $"it prints to {host}, a name that was not probed or did not resolve";

        var uncompared = new List<(PrintQueueInfo Queue, ModelDefinition? Model, string Kind, string Unknown)>();
        foreach (var candidate in _candidates.Where(c => c.Queue is not null))
        {
            if (candidate.Connection == ConnectionType.Wsd)
                uncompared.Add((candidate.Queue!, candidate.Model, "WSD queue", wsdUnknown));
            else if (candidate is { Kind: TargetKind.NetworkAddress, NetworkAddress: { } host } && ResolvedOf(host) is null)
                uncompared.Add((candidate.Queue!, candidate.Model, "queue", Unresolved(host)));
        }

        var configured = ConfiguredQueueNames();
        bool MayBeOwnQueue(PrintQueueInfo queue) => !configured.Contains(queue.Name) && _matcher.MayBeTaQueue(queue);

        uncompared.AddRange(wsdQueues.Where(MayBeOwnQueue).Select(q => (q, _matcher.ModelForDriver(q.DriverName), "WSD queue", wsdUnknown)));
        foreach (var (host, queues) in tcpQueues.Where(h => ResolvedOf(h.Key) is null))
            uncompared.AddRange(queues.Where(MayBeOwnQueue).Select(q => (q, _matcher.ModelForDriver(q.DriverName), "queue", Unresolved(host))));
        uncompared.AddRange(_unlinkedQueues.Select(q => (q, _matcher.ModelForDriver(q.DriverName), "queue", $"its port {q.PortName} cannot be linked to a printer")));
        // A queue that names no model on an LPR or Internet Port none of whose hosts leads to a known IP address cannot be compared either.
        uncompared.AddRange(_modelLessQueuesOnOtherPorts.Where(o => !configured.Contains(o.Queue.Name) && o.Hosts.Count > 0 && o.Hosts.All(h => ResolvedOf(h) is null))
            .Select(o => (o.Queue, _matcher.ModelForDriver(o.Queue.DriverName), "queue", Unresolved(o.Hosts[0]))));
        return uncompared.DistinctBy(u => u.Queue.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ReportUnsupportedQueues()
    {
        foreach (var queue in _snapshot.Queues)
        {
            if (queue.IsServerConnection || _claimedQueues.Contains(queue.Name) || QueueLocator.IsLocalPortName(queue.PortName))
                continue;
            if (!DriverRules.IsVendorName(queue.DriverName) || _matcher.ModelsAcceptingDriver(queue.DriverName).Count > 0)
                continue;
            _findings.Add(Findings.Info(FindingCode.UnsupportedPrinterDetected,
                $"Queue '{queue.Name}' uses driver '{queue.DriverName}', which is not an approved driver of a supported model.",
                ("queue", queue.Name), ("driver", queue.DriverName)));
        }
    }

    /// <summary>
    /// Several queues on one printer's USB port or TCP/IP host: only the configured one is changed and the others keep their
    /// settings. Information when the configured one is the queue already set up as the ticket printer; a warning when none
    /// could be told apart (it is then chosen by driver and name), because employees may print to another of them.
    /// </summary>
    private void ReportDuplicates()
    {
        foreach (var candidate in _candidates.Where(c => c.AdditionalQueues.Count > 0))
        {
            string printer = $"{candidate.Model?.Id ?? "The printer"} ({candidate.Key}) has {candidate.AdditionalQueues.Count + 1} queues";
            string configured = candidate.Queue!.Name;
            string others = QueueNames(candidate.AdditionalQueues);
            _findings.Add(_knownTicketQueues.Contains(candidate.Key)
                ? Findings.Info(FindingCode.DuplicateQueues,
                    $"{printer}; '{configured}' is configured because it is already set up as the ticket printer, and the others keep their settings: {others}.",
                    ("candidate", candidate.Key), ("queue", configured))
                : Findings.Warning(FindingCode.DuplicateQueues,
                    $"{printer} and none of them can be told apart as the ticket printer (no single one already uses the ticket form or has the configured queue name); "
                    + $"'{configured}' is configured, chosen by its driver and name, and the others keep their settings: {others}. Check which queue employees print to.",
                    ("candidate", candidate.Key), ("queue", configured)));
        }
    }

    private bool IndicatesOtherModel(string driverName, ModelDefinition model, List<string> evidence)
    {
        if (_matcher.ModelForDriver(driverName) is not { } other || Same(other.Id, model.Id))
            return false;
        evidence.Add($"Identity conflict: driver '{driverName}' is for {other.Id}, but the device is a {model.Id}; the device identity wins.");
        _findings.Add(Findings.Warning(FindingCode.IdentityConflict,
            $"The printer is a {model.Id}, but its queue uses driver '{driverName}' for {other.Id}.",
            ("model", model.Id), ("driver", driverName), ("driverModel", other.Id)));
        return true;
    }

    private void AddReachability(string address, NetworkIdentity? identity, List<string> evidence)
    {
        if (identity is null)
        {
            evidence.Add($"{address} was not probed.");
            return;
        }

        evidence.Add(identity.RawPortReachable
            ? FormattableString.Invariant($"Raw print port {_rawPort} at {address} is reachable.")
            : FormattableString.Invariant($"Raw print port {_rawPort} at {address} is not reachable."));
        if (!identity.RawPortReachable)
        {
            _findings.Add(Findings.Warning(FindingCode.NetworkPrinterUnreachable,
                FormattableString.Invariant($"The printer at {address} does not answer on port {_rawPort}") + (identity.Error is { Length: > 0 } error ? $": {error}" : "."),
                ("address", address)));
        }
    }

    /// <summary>
    /// Why two probes reached one printer, or null: both contacted the same IP address, or both devices answered SNMP with
    /// the same serial number and are exactly identified as the same model. An empty or placeholder serial number, or
    /// different models, never make two addresses one printer.
    /// </summary>
    private string? SameDevice(string spellingA, string spellingB)
    {
        if (ResolvedOf(spellingA) is { } resolved && Same(resolved, ResolvedOf(spellingB)))
            return $"{spellingA} and {spellingB} both lead to {resolved}";
        if (Identity(spellingA) is not { } a || Identity(spellingB) is not { } b)
            return null;
        if (a.SnmpResponded && b.SnmpResponded && IsDistinctiveSerial(a.SerialNumber) && Same(a.SerialNumber, b.SerialNumber)
            && _matcher.MatchNetwork(a) is { Confidence: MatchConfidence.Exact, Model: { } model }
            && _matcher.MatchNetwork(b) is { Confidence: MatchConfidence.Exact, Model: { } other }
            && Same(model.Id, other.Id))
        {
            return $"{a.Address} and {b.Address} report the same serial number {a.SerialNumber} and model {model.Id}";
        }
        return null;
    }

    // Placeholders such as "0", "N/A" or "0000000" are not unique to one device.
    private static bool IsDistinctiveSerial(string? serial) =>
        serial is { Length: >= 5 } && serial.Any(char.IsAsciiDigit) && serial.Distinct().Skip(1).Any();

    /// <summary>Why <paramref name="spelling"/> belongs to the printer: its link to the key, or to another spelling of the group.</summary>
    private string SameDeviceReason(NetworkPrinter printer, string spelling, string key) =>
        new[] { key }.Concat(printer.Spellings)
            .Where(s => !Same(s, spelling))
            .Select(s => SameDevice(s, spelling))
            .FirstOrDefault(reason => reason is not null) ?? "same printer";

    /// <summary>
    /// The IP address a spelling leads to: what its probe contacted, or the address itself when it is an IP address (which needs
    /// no probe); null for a host name that was not probed or did not resolve.
    /// </summary>
    private string? ResolvedOf(string spelling) =>
        Identity(spelling)?.ResolvedAddress is { Length: > 0 } resolved ? resolved
        : PrinterAddress.TryParse(spelling, out _, out var literal, out _) && literal is not null ? literal.ToString()
        : null;

    private void AddSpellingEvidence(NetworkPrinter printer, string key, List<string> evidence)
    {
        foreach (string spelling in printer.Spellings.Where(s => !Same(s, key)))
        {
            string own = printer.QueuesOf(spelling) is { Count: > 0 } queues ? $"; its own queues ({QueueNames(queues)}) are a separate choice" : "";
            evidence.Add($"{spelling} reaches the same printer as {key}: {SameDeviceReason(printer, spelling, key)}{own}.");
        }
    }

    /// <summary>The requested spellings without a queue other than <paramref name="key"/>: aliases of the key's candidate.</summary>
    private static List<string> Aliases(NetworkPrinter printer, string key) => printer.RequestedOnly.Where(s => !Same(s, key)).ToList();

    /// <summary>The address for messages, followed by the printer's other spellings so that every requested one is named.</summary>
    private static string Spelled(NetworkPrinter printer, string address) =>
        Spelled(address, printer.Spellings.Where(s => !Same(s, address)).ToList());

    private static string Spelled(string address, IReadOnlyCollection<string> others) =>
        others.Count > 0 ? $"{address} (also reached as {string.Join(", ", others)})" : address;

    private NetworkIdentity? Identity(string address) => _snapshot.NetworkIdentities.FirstOrDefault(i => Same(i.Address, address));

    /// <summary>The probe of these spellings that tells the most (SNMP answered, then reachable); null when none was probed.</summary>
    private NetworkIdentity? BestIdentity(IEnumerable<string> spellings) =>
        spellings.Select(Identity).OfType<NetworkIdentity>()
            .OrderByDescending(i => i.SnmpResponded)
            .ThenByDescending(i => i.RawPortReachable)
            .FirstOrDefault();

    /// <summary>
    /// The queue this tool configures among one printer's queues: the one already set up as the ticket printer, else
    /// <see cref="QueueLocator.Primary"/>. Remembers, for <see cref="ReportDuplicates"/>, when the first rule told it apart.
    /// </summary>
    private PrintQueueInfo? PrimaryQueue(string key, IReadOnlyList<PrintQueueInfo> queues, ModelDefinition? model)
    {
        if (queues.Count > 1 && _ticketQueue?.Invoke(queues) is { } ticket)
        {
            _knownTicketQueues.Add(key);
            return ticket;
        }
        return QueueLocator.Primary(queues, model);
    }

    /// <summary>The single models the queues' drivers indicate, without repeats.</summary>
    private List<ModelDefinition> DriverModels(IEnumerable<PrintQueueInfo> queues) =>
        queues
            .Select(q => _matcher.ModelForDriver(q.DriverName))
            .OfType<ModelDefinition>()
            .DistinctBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string QueueNames(IEnumerable<PrintQueueInfo> queues) => string.Join(", ", queues.Select(q => $"'{q.Name}'"));

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string Listed(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}";
    }

    /// <summary>
    /// Remembers the queues among <paramref name="queues"/> whose driver names a supported model (see <see cref="RejectedQueues"/>),
    /// each once; with <paramref name="whateverDriver"/>, whatever their driver names (a queue the setup cannot configure that a
    /// printer is not set up automatically for). A queue that a candidate configures (a pool that is also a USB printer's queue)
    /// is not rejected.
    /// </summary>
    private void Rejected(IEnumerable<PrintQueueInfo> queues, bool whateverDriver = false)
    {
        foreach (var queue in queues)
        {
            if ((whateverDriver || _matcher.ModelForDriver(queue.DriverName) is not null) && CandidateWith(queue) is null
                && !_rejectedQueues.Contains(queue.Name, StringComparer.OrdinalIgnoreCase))
                _rejectedQueues.Add(queue.Name);
        }
    }

    /// <summary>The candidate that configures <paramref name="queue"/> (as its queue, or as one of its further queues), if any.</summary>
    private PrinterCandidate? CandidateWith(PrintQueueInfo queue) => _candidates.FirstOrDefault(c => HasQueue(c, queue));

    private static bool HasQueue(PrinterCandidate candidate, PrintQueueInfo queue) =>
        Same(candidate.Queue?.Name, queue.Name) || candidate.AdditionalQueues.Any(q => Same(q.Name, queue.Name));

    private void Claim(IEnumerable<PrintQueueInfo> queues)
    {
        foreach (var queue in queues)
            _claimedQueues.Add(queue.Name);
    }

    private static IReadOnlyList<PrintQueueInfo> Others(IEnumerable<PrintQueueInfo> queues, PrintQueueInfo? primary) =>
        queues.Where(q => !ReferenceEquals(q, primary)).OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static string QueueEvidence(PrintQueueInfo queue, ModelDefinition? model)
    {
        string verdict = model is null ? ""
            : DriverRules.IsAccepted(model, queue.DriverName) ? $" (an accepted driver of {model.Id})"
            : $" (not an accepted driver of {model.Id})";
        return $"Queue '{queue.Name}' on port {queue.PortName} uses driver '{queue.DriverName}'{verdict}.";
    }

    /// <summary>What the device says it is, for messages (the administrator-assigned printer name only as a last resort).</summary>
    private static string Describe(NetworkIdentity identity)
    {
        if (Ieee1284.TryParse(identity.Ieee1284DeviceId, out string manufacturer, out string model))
            return $"'{manufacturer} {model}'";
        return $"'{identity.VendorModelName ?? identity.DeviceDescription ?? identity.SysDescr ?? identity.PrinterName}'";
    }

    private static bool IsRequested(string address, IReadOnlyList<string> requestedAddresses) =>
        requestedAddresses.Contains(address, StringComparer.OrdinalIgnoreCase);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>One network printer: every spelling that reaches it, hosts of existing TCP/IP ports first.</summary>
    private sealed class NetworkPrinter
    {
        public List<string> Spellings { get; } = [];
        /// <summary>
        /// The spellings that are hosts of existing Standard TCP/IP ports, each with the queues on its ports, and the hosts that
        /// queues the setup cannot configure print to (pooled ports, LPR or Internet Ports), each with those queues.
        /// </summary>
        public List<(string Host, List<PrintQueueInfo> Queues)> QueueHosts { get; } = [];
        /// <summary>The queues on all of its hosts, each once (a pooled queue may print to several of them).</summary>
        public IEnumerable<PrintQueueInfo> Queues => QueueHosts.SelectMany(h => h.Queues).DistinctBy(q => q.Name, StringComparer.OrdinalIgnoreCase);
        /// <summary>The spellings without a queue (all of them requested): aliases of each queue host's candidate.</summary>
        public List<string> RequestedOnly => Spellings.Where(s => QueuesOf(s).Count == 0).ToList();
        /// <summary>The spellings the operator, command line or branch file named.</summary>
        public List<string> Requested { get; } = [];
        /// <summary>The probe that identifies the printer best; null when none of its spellings was probed.</summary>
        public NetworkIdentity? Identity { get; set; }

        public IReadOnlyList<PrintQueueInfo> QueuesOf(string spelling)
        {
            foreach (var (host, queues) in QueueHosts)
            {
                if (string.Equals(host, spelling, StringComparison.OrdinalIgnoreCase))
                    return queues;
            }
            return [];
        }
    }
}
