using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.Testing;

/// <summary>A write call made by the code under test through <see cref="IPrintSpooler"/> (recorded even when it failed).</summary>
public sealed record SpoolerWrite(string Operation, string Target, string? Detail = null);

/// <summary>
/// In-memory print spooler: queues, ports, drivers, forms, jobs (with the DEVMODE each was submitted with), and the Global
/// and PerUser DEVMODE of each queue, with a
/// <see cref="FakeDriverProfile"/> per driver name. <c>Seed*</c> members set up state without being recorded; every write
/// member of <see cref="IPrintSpooler"/> is recorded in <see cref="Writes"/>. While the service is not running every call
/// except GetServiceStatus/TryStartService throws <see cref="SpoolerException"/> 1722, like the real spooler client.
/// Failures can be injected per operation (the <see cref="IPrintSpooler"/> member name).
/// </summary>
public sealed class FakePrintSpooler : IPrintSpooler
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorFileExists = 80;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorAlreadyExists = 183;
    public const int ErrorRpcServerUnavailable = 1722;
    public const int ErrorUnknownPort = 1796;
    public const int ErrorUnknownPrinterDriver = 1797;
    public const int ErrorInvalidPrinterName = 1801;
    public const int ErrorPrinterAlreadyExists = 1802;
    public const int ErrorInvalidFormName = 1902;

    private const string StandardTcpIpMonitor = "Standard TCP/IP Port";

    /// <summary>Built-in forms of the Windows forms database (micrometers, full imageable area).</summary>
    public static IReadOnlyList<FormInfo> StandardForms { get; } =
    [
        Builtin("Letter", 215_900, 279_400),
        Builtin("Legal", 215_900, 355_600),
        Builtin("Executive", 184_150, 266_700),
        Builtin("A4", 210_000, 297_000),
        Builtin("A5", 148_000, 210_000),
        Builtin("A6", 105_000, 148_000),
        Builtin("B5 (JIS)", 182_000, 257_000),
        Builtin("Envelope #10", 104_775, 241_300),
        Builtin("Envelope DL", 110_000, 220_000),
        Builtin("Envelope C5", 162_000, 229_000),
        Builtin("Envelope Monarch", 98_425, 190_500),
    ];

    /// <summary>Local ports every Windows installation has.</summary>
    public static IReadOnlyList<PrinterPortInfo> LocalPorts { get; } =
    [
        LocalPort("PORTPROMPT:"),
        LocalPort("FILE:"),
        LocalPort("nul:"),
        LocalPort("LPT1:"),
        LocalPort("COM1:"),
    ];

    private readonly object _gate = new();
    private readonly List<PrintQueueInfo> _queues = [];
    private readonly List<PrinterPortInfo> _ports = [.. LocalPorts];
    private readonly List<PrinterDriverInfo> _drivers = [];
    private readonly List<FormInfo> _forms = [.. StandardForms];
    private readonly Dictionary<string, DevMode?> _global = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DevMode> _perUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<PrintJobInfo>> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<int, DevMode>> _jobDevModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FakeDriverProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Win32Error, FindingCode Code)> _failures = new(StringComparer.Ordinal);
    private readonly List<SpoolerWrite> _writes = [];
    private SpoolerServiceStatus _status = new(ServiceRunState.Running, ServiceStartMode.Automatic);
    private FakeDriverProfile _defaultProfile = FakeDriverProfile.Default;

    public SpoolerServiceStatus ServiceStatus
    {
        get { lock (_gate) return _status; }
        set { lock (_gate) _status = value; }
    }

    /// <summary>Whether TryStartService can start a stopped (not disabled) service.</summary>
    public bool StartServiceSucceeds { get; set; } = true;

    /// <summary>Scopes whose SetDevMode calls are accepted but silently not persisted (the driver or a policy reverts them).</summary>
    public HashSet<DevModeScope> RevertDevModeWrites { get; } = [];

    /// <summary>Profile of drivers that have no profile of their own.</summary>
    public FakeDriverProfile DefaultProfile
    {
        get { lock (_gate) return _defaultProfile; }
        set { lock (_gate) _defaultProfile = value; }
    }

    public IReadOnlyList<PrintQueueInfo> Queues
    {
        get { lock (_gate) return [.. _queues]; }
    }

    public IReadOnlyList<PrinterPortInfo> Ports
    {
        get { lock (_gate) return [.. _ports]; }
    }

    public IReadOnlyList<PrinterDriverInfo> Drivers
    {
        get { lock (_gate) return [.. _drivers]; }
    }

    public IReadOnlyList<FormInfo> Forms
    {
        get { lock (_gate) return [.. _forms]; }
    }

    /// <summary>Every write call, in order (TryStartService, AddForm, SetForm, AddStandardTcpIpPort, AddQueue, SetQueueDriver, SetQueuePort, SetDevMode).</summary>
    public IReadOnlyList<SpoolerWrite> Writes
    {
        get { lock (_gate) return [.. _writes]; }
    }

    // ---- Test setup (not recorded) -------------------------------------------------------------------------------

    /// <summary>Adds or replaces a queue; its Global DEVMODE is <paramref name="globalDefaults"/> or the driver profile's default.</summary>
    public void SeedQueue(PrintQueueInfo queue, DevMode? globalDefaults = null)
    {
        lock (_gate)
        {
            RemoveQueueLocked(queue.Name);
            AddQueueLocked(queue, globalDefaults);
        }
    }

    public void SeedPort(PrinterPortInfo port)
    {
        lock (_gate)
        {
            _ports.RemoveAll(p => Same(p.Name, port.Name));
            _ports.Add(port);
        }
    }

    public void SeedUsbPort(string name)
    {
        lock (_gate)
        {
            if (FindPort(name) is null)
                _ports.Add(UsbPort(name));
        }
    }

    /// <summary>Adds the next free USBnnn port (as the USB monitor does when a printer interface appears).</summary>
    public string AllocateUsbPort()
    {
        lock (_gate)
        {
            for (int n = 1; ; n++)
            {
                string name = FormattableString.Invariant($"USB{n:000}");
                if (FindPort(name) is not null)
                    continue;
                _ports.Add(UsbPort(name));
                return name;
            }
        }
    }

    /// <summary>Adds or replaces an installed driver (same name and environment).</summary>
    public void SeedDriver(PrinterDriverInfo driver)
    {
        lock (_gate)
        {
            _drivers.RemoveAll(d => Same(d.Name, driver.Name) && Same(d.Environment, driver.Environment));
            _drivers.Add(driver);
        }
    }

    /// <summary>Adds or replaces a form in the forms database.</summary>
    public void SeedForm(FormInfo form)
    {
        lock (_gate)
        {
            int index = _forms.FindIndex(f => Same(f.Name, form.Name));
            if (index >= 0)
                _forms[index] = form;
            else
                _forms.Add(form);
        }
    }

    public void RemoveQueue(string name)
    {
        lock (_gate)
            RemoveQueueLocked(name);
    }

    public void RemoveForm(string name)
    {
        lock (_gate)
            _forms.RemoveAll(f => Same(f.Name, name));
    }

    public void UpdateQueue(string name, Func<PrintQueueInfo, PrintQueueInfo> change)
    {
        lock (_gate)
        {
            int index = _queues.FindIndex(q => Same(q.Name, name));
            if (index < 0)
                throw new InvalidOperationException($"No fake queue '{name}'.");
            _queues[index] = change(_queues[index]);
        }
    }

    /// <summary>Sets a stored DEVMODE directly (Global or PerUser; null removes it).</summary>
    public void SetStoredDevMode(string queueName, DevModeScope scope, DevMode? devMode)
    {
        lock (_gate)
        {
            switch (scope)
            {
                case DevModeScope.Global:
                    _global[queueName] = devMode?.Clone();
                    break;
                case DevModeScope.PerUser when devMode is null:
                    _perUser.Remove(queueName);
                    break;
                case DevModeScope.PerUser:
                    _perUser[queueName] = devMode.Clone();
                    break;
                default:
                    throw new ArgumentException("Only Global and PerUser DEVMODEs are stored.", nameof(scope));
            }
        }
    }

    public DevMode? GetStoredDevMode(string queueName, DevModeScope scope)
    {
        lock (_gate)
        {
            return scope switch
            {
                DevModeScope.Global => _global.GetValueOrDefault(queueName)?.Clone(),
                DevModeScope.PerUser => _perUser.GetValueOrDefault(queueName)?.Clone(),
                _ => throw new ArgumentException("Only Global and PerUser DEVMODEs are stored.", nameof(scope)),
            };
        }
    }

    public void AddJob(string queueName, PrintJobInfo job) => AddJob(queueName, job, null);

    /// <summary>
    /// Adds a spooled job submitted with <paramref name="devMode"/>: what GetJob level 2 reports for it while it is in the
    /// queue (null: the job carries no DEVMODE).
    /// </summary>
    public void AddJob(string queueName, PrintJobInfo job, DevMode? devMode)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(queueName, out var jobs))
                _jobs[queueName] = jobs = [];
            jobs.Add(job);

            if (!_jobDevModes.TryGetValue(queueName, out var devModes))
                _jobDevModes[queueName] = devModes = [];
            if (devMode is null)
                devModes.Remove(job.JobId);
            else
                devModes[job.JobId] = devMode.Clone();
        }
    }

    /// <summary>
    /// Spools a document the way an application does: a new job (the next free id) that GetJobs lists and whose DEVMODE
    /// GetJobDevMode returns until <see cref="RemoveJob"/>. Returns the job.
    /// </summary>
    public PrintJobInfo SubmitJob(string queueName, string document, DevMode? devMode, JobStatusFlags status = JobStatusFlags.Spooling, string userName = "employee")
    {
        lock (_gate)
        {
            int id = _jobs.Values.SelectMany(j => j).Select(j => j.JobId).DefaultIfEmpty(0).Max() + 1;
            var job = new PrintJobInfo { JobId = id, Document = document, UserName = userName, Status = status, TotalPages = 1, Submitted = DateTime.UtcNow };
            AddJob(queueName, job, devMode);
            return job;
        }
    }

    /// <summary>The job leaves the queue (printed or deleted): GetJobs stops listing it and GetJobDevMode returns null. False when there was no such job.</summary>
    public bool RemoveJob(string queueName, int jobId)
    {
        lock (_gate)
        {
            if (_jobDevModes.TryGetValue(queueName, out var devModes))
                devModes.Remove(jobId);
            return _jobs.TryGetValue(queueName, out var jobs) && jobs.RemoveAll(j => j.JobId == jobId) > 0;
        }
    }

    /// <summary>
    /// What Windows Plug and Play does after a USB printer is bound: creates a queue named after the driver
    /// ("Name (Copy n)" when taken) on the device's port. Returns null when a queue already uses the port or the driver is missing.
    /// </summary>
    public string? CreatePlugAndPlayQueue(string driverName, string portName)
    {
        lock (_gate)
        {
            if (_queues.Any(q => Same(q.PortName, portName)))
                return null;
            var driver = FindDriver(driverName);
            if (driver is null)
                return null;

            string name = driver.Name;
            for (int copy = 1; FindQueue(name) is not null; copy++)
                name = FormattableString.Invariant($"{driver.Name} (Copy {copy})");
            AddQueueLocked(NewLocalQueue(name, driver.Name, portName), null);
            return name;
        }
    }

    public FakeDriverProfile ProfileFor(string driverName)
    {
        lock (_gate)
            return ProfileLocked(driverName);
    }

    public void SetProfile(string driverName, FakeDriverProfile profile)
    {
        lock (_gate)
            _profiles[driverName] = profile;
    }

    /// <summary>Makes every call of <paramref name="operation"/> (an <see cref="IPrintSpooler"/> member name) throw a spooler error.</summary>
    public void FailOperation(string operation, int win32Error, FindingCode code = FindingCode.OperationFailed)
    {
        lock (_gate)
            _failures[operation] = (win32Error, code);
    }

    public void ClearFailure(string operation)
    {
        lock (_gate)
            _failures.Remove(operation);
    }

    // ---- IPrintSpooler -------------------------------------------------------------------------------------------

    public SpoolerServiceStatus GetServiceStatus()
    {
        lock (_gate)
        {
            ThrowIfFailing(nameof(GetServiceStatus));
            return _status;
        }
    }

    public bool TryStartService(TimeSpan timeout, out string? failureReason)
    {
        lock (_gate)
        {
            Record(nameof(TryStartService), "Spooler");
            ThrowIfFailing(nameof(TryStartService));
            failureReason = null;
            if (_status.IsRunning)
                return true;
            if (_status.StartMode == ServiceStartMode.Disabled)
                failureReason = "The Print Spooler service is disabled.";
            else if (_status.State == ServiceRunState.NotInstalled)
                failureReason = "The Print Spooler service is not installed.";
            else if (!StartServiceSucceeds)
                failureReason = FormattableString.Invariant($"The Print Spooler service did not start within {timeout.TotalSeconds:0} s.");
            else
                _status = _status with { State = ServiceRunState.Running };
            return failureReason is null;
        }
    }

    public IReadOnlyList<PrintQueueInfo> GetQueues()
    {
        lock (_gate)
        {
            Guard(nameof(GetQueues));
            return [.. _queues];
        }
    }

    public IReadOnlyList<PrinterPortInfo> GetPorts()
    {
        lock (_gate)
        {
            Guard(nameof(GetPorts));
            return [.. _ports];
        }
    }

    public IReadOnlyList<PrinterDriverInfo> GetDrivers()
    {
        lock (_gate)
        {
            Guard(nameof(GetDrivers));
            return [.. _drivers];
        }
    }

    public IReadOnlyList<FormInfo> GetForms()
    {
        lock (_gate)
        {
            Guard(nameof(GetForms));
            return [.. _forms];
        }
    }

    public void AddForm(FormInfo form)
    {
        lock (_gate)
        {
            Record(nameof(AddForm), form.Name);
            Guard(nameof(AddForm));
            if (FindForm(form.Name) is not null)
                throw new SpoolerException(nameof(AddForm), ErrorFileExists, $"The form '{form.Name}' already exists.");
            if (form.Kind != FormKind.User)
                throw new SpoolerException(nameof(AddForm), ErrorInvalidParameter, "Only user forms can be added.");
            _forms.Add(form);
        }
    }

    public void SetForm(string formName, FormInfo form)
    {
        lock (_gate)
        {
            Record(nameof(SetForm), formName);
            Guard(nameof(SetForm));
            int index = _forms.FindIndex(f => Same(f.Name, formName));
            if (index < 0)
                throw new SpoolerException(nameof(SetForm), ErrorInvalidFormName, $"The form '{formName}' does not exist.");
            if (_forms[index].Kind != FormKind.User)
                throw new SpoolerException(nameof(SetForm), ErrorAccessDenied, $"The form '{formName}' is not a user form.");
            _forms[index] = form with { Kind = FormKind.User };
        }
    }

    public void AddStandardTcpIpPort(TcpIpPortRequest request)
    {
        lock (_gate)
        {
            Record(nameof(AddStandardTcpIpPort), request.PortName, FormattableString.Invariant($"{request.HostAddress}:{request.PortNumber}"));
            Guard(nameof(AddStandardTcpIpPort));
            if (FindPort(request.PortName) is not null)
                throw new SpoolerException(nameof(AddStandardTcpIpPort), ErrorAlreadyExists, $"The port '{request.PortName}' already exists.");
            _ports.Add(new PrinterPortInfo
            {
                Name = request.PortName,
                MonitorName = StandardTcpIpMonitor,
                Description = StandardTcpIpMonitor,
                Kind = PortKind.StandardTcpIp,
                HostAddress = request.HostAddress,
                PortNumber = request.PortNumber,
                Protocol = TcpPortProtocol.Raw,
                SnmpEnabled = request.SnmpEnabled,
            });
        }
    }

    public void AddQueue(NewQueueRequest request)
    {
        lock (_gate)
        {
            Record(nameof(AddQueue), request.Name, $"driver={request.DriverName}; port={request.PortName}");
            Guard(nameof(AddQueue));
            if (FindQueue(request.Name) is not null)
                throw new SpoolerException(nameof(AddQueue), ErrorPrinterAlreadyExists, $"The printer '{request.Name}' already exists.");
            var driver = FindDriver(request.DriverName)
                ?? throw new SpoolerException(nameof(AddQueue), ErrorUnknownPrinterDriver, $"The printer driver '{request.DriverName}' is unknown.");
            if (FindPort(request.PortName) is null)
                throw new SpoolerException(nameof(AddQueue), ErrorUnknownPort, $"The port '{request.PortName}' is unknown.");

            AddQueueLocked(NewLocalQueue(request.Name, driver.Name, request.PortName) with
            {
                Location = request.Location,
                Comment = request.Comment,
                PrintProcessor = request.PrintProcessor ?? "winprint",
                Datatype = request.Datatype,
            }, null);
        }
    }

    public void SetQueueDriver(string queueName, string driverName)
    {
        lock (_gate)
        {
            Record(nameof(SetQueueDriver), queueName, driverName);
            Guard(nameof(SetQueueDriver));
            int index = QueueIndexOrThrow(queueName, nameof(SetQueueDriver));
            var driver = FindDriver(driverName)
                ?? throw new SpoolerException(nameof(SetQueueDriver), ErrorUnknownPrinterDriver, $"The printer driver '{driverName}' is unknown.");
            var queue = _queues[index] with { DriverName = driver.Name };
            _queues[index] = queue;
            // The new driver re-initializes the queue's default DEVMODE.
            _global[queue.Name] = FakeDevModes.CreateDefault(ProfileLocked(driver.Name), queue.Name);
        }
    }

    public void SetQueuePort(string queueName, string portName)
    {
        lock (_gate)
        {
            Record(nameof(SetQueuePort), queueName, portName);
            Guard(nameof(SetQueuePort));
            int index = QueueIndexOrThrow(queueName, nameof(SetQueuePort));
            var port = FindPort(portName)
                ?? throw new SpoolerException(nameof(SetQueuePort), ErrorUnknownPort, $"The port '{portName}' is unknown.");
            _queues[index] = _queues[index] with { PortName = port.Name };
        }
    }

    public DriverCapabilities GetCapabilities(string queueName)
    {
        lock (_gate)
        {
            Guard(nameof(GetCapabilities));
            var queue = QueueOrThrow(queueName, nameof(GetCapabilities));
            var profile = ProfileLocked(queue.DriverName);
            if (profile.CapabilitiesWin32Error is { } error)
                throw new SpoolerException(nameof(GetCapabilities), error, "DeviceCapabilities failed.");

            var papers = PapersLocked(profile);
            var listed = papers.Where(p => p.Id != DmPaper.User).ToList();
            bool box = profile.ExtentsFromListedPapers && listed.Count > 0;
            return new DriverCapabilities
            {
                QueueName = queue.Name,
                Papers = papers,
                Bins = profile.Bins,
                MinWidthTenthsOfMillimeter = box ? listed.Min(p => p.WidthTenthsOfMillimeter) : profile.MinWidthTenthsOfMillimeter,
                MinHeightTenthsOfMillimeter = box ? listed.Min(p => p.HeightTenthsOfMillimeter) : profile.MinHeightTenthsOfMillimeter,
                MaxWidthTenthsOfMillimeter = box ? listed.Max(p => p.WidthTenthsOfMillimeter) : profile.MaxWidthTenthsOfMillimeter,
                MaxHeightTenthsOfMillimeter = box ? listed.Max(p => p.HeightTenthsOfMillimeter) : profile.MaxHeightTenthsOfMillimeter,
                SupportsDuplex = profile.SupportsDuplex,
                LandscapeRotation = 90,
                DriverDllVersion = profile.DriverDllVersion,
            };
        }
    }

    public DevMode? GetDevMode(string queueName, DevModeScope scope)
    {
        lock (_gate)
        {
            Guard(nameof(GetDevMode));
            var queue = QueueOrThrow(queueName, nameof(GetDevMode));
            return scope switch
            {
                DevModeScope.Global => _global.GetValueOrDefault(queue.Name)?.Clone(),
                DevModeScope.PerUser => _perUser.GetValueOrDefault(queue.Name)?.Clone(),
                _ => ValidateLocked(queue, EffectiveSourceLocked(queue)),
            };
        }
    }

    public DevMode ValidateDevMode(string queueName, DevMode requested)
    {
        lock (_gate)
        {
            Guard(nameof(ValidateDevMode));
            return ValidateLocked(QueueOrThrow(queueName, nameof(ValidateDevMode)), requested);
        }
    }

    public void SetDevMode(string queueName, DevModeScope scope, DevMode devMode)
    {
        lock (_gate)
        {
            Record(nameof(SetDevMode), queueName, scope.ToString());
            Guard(nameof(SetDevMode));
            var queue = QueueOrThrow(queueName, nameof(SetDevMode));
            if (scope == DevModeScope.Effective)
                throw new SpoolerException(nameof(SetDevMode), ErrorInvalidParameter, "The effective DEVMODE is not writable.");
            if (RevertDevModeWrites.Contains(scope))
                return;
            if (scope == DevModeScope.Global)
                _global[queue.Name] = devMode.Clone();
            else
                _perUser[queue.Name] = devMode.Clone();
        }
    }

    public PrintCapabilitiesSummary GetPrintCapabilities(string queueName, DevMode? devMode)
    {
        lock (_gate)
        {
            Guard(nameof(GetPrintCapabilities));
            var queue = QueueOrThrow(queueName, nameof(GetPrintCapabilities));
            var profile = ProfileLocked(queue.DriverName);
            if (!profile.PrintCapabilitiesAvailable)
                return new PrintCapabilitiesSummary { Available = false, Error = "PTGetPrintCapabilities failed (0x80070057)." };

            var papers = PapersLocked(profile);
            return new PrintCapabilitiesSummary
            {
                Available = true,
                MediaSizes = papers.Select(p => new MediaSizeCapability(p.Name, p.Name, WidthMicrometers(p), HeightMicrometers(p))).ToList(),
                CustomSizeRange = profile is { MinWidthTenthsOfMillimeter: { } minW, MaxWidthTenthsOfMillimeter: { } maxW, MinHeightTenthsOfMillimeter: { } minH, MaxHeightTenthsOfMillimeter: { } maxH }
                    ? new CustomMediaSizeRange(minW * 100, maxW * 100, minH * 100, maxH * 100)
                    : null,
                InputBins = profile.Bins.Select(b => b.Name).ToList(),
            };
        }
    }

    public PrintTicketMediaInfo DescribePrintTicket(string queueName, DevMode devMode)
    {
        lock (_gate)
        {
            Guard(nameof(DescribePrintTicket));
            var queue = QueueOrThrow(queueName, nameof(DescribePrintTicket));
            var profile = ProfileLocked(queue.DriverName);
            if (!profile.PrintTicketAvailable)
                return new PrintTicketMediaInfo { Available = false, Error = "PTConvertDevModeToPrintTicket failed (0x80070057)." };
            if (profile.PrintTicketOverride is { } overridden)
                return overridden;

            var papers = PapersLocked(profile);
            var paper = papers.FirstOrDefault(p => p.Id == devMode.PaperSize) ?? papers.FirstOrDefault(p => Same(p.Name, devMode.FormName));
            return new PrintTicketMediaInfo
            {
                Available = true,
                MediaSizeName = paper?.Name,
                WidthMicrometers = paper is null ? null : WidthMicrometers(paper),
                HeightMicrometers = paper is null ? null : HeightMicrometers(paper),
                InputBin = profile.Bins.FirstOrDefault(b => b.Id == devMode.DefaultSource)?.Name,
                Orientation = devMode.Orientation == DmOrientation.Landscape ? "psk:Landscape" : "psk:Portrait",
                Duplex = devMode.Duplex switch
                {
                    DmDuplex.Vertical => "psk:TwoSidedLongEdge",
                    DmDuplex.Horizontal => "psk:TwoSidedShortEdge",
                    _ => "psk:OneSided",
                },
            };
        }
    }

    public IReadOnlyList<PrintJobInfo> GetJobs(string queueName)
    {
        lock (_gate)
        {
            Guard(nameof(GetJobs));
            var queue = QueueOrThrow(queueName, nameof(GetJobs));
            return _jobs.TryGetValue(queue.Name, out var jobs) ? [.. jobs] : [];
        }
    }

    /// <summary>GetJob level 2: the DEVMODE the job was submitted with; null when the job is not (or no longer) queued or carries none.</summary>
    public DevMode? GetJobDevMode(string queueName, int jobId)
    {
        lock (_gate)
        {
            Guard(nameof(GetJobDevMode));
            var queue = QueueOrThrow(queueName, nameof(GetJobDevMode));
            bool queued = _jobs.TryGetValue(queue.Name, out var jobs) && jobs.Any(j => j.JobId == jobId);
            return queued && _jobDevModes.TryGetValue(queue.Name, out var devModes) && devModes.TryGetValue(jobId, out var devMode)
                ? devMode.Clone()
                : null;
        }
    }

    // ---- Internals (callers hold the lock) -----------------------------------------------------------------------

    private void Guard(string operation)
    {
        if (!_status.IsRunning)
            throw new SpoolerException(operation, ErrorRpcServerUnavailable, "The RPC server is unavailable.");
        ThrowIfFailing(operation);
    }

    private void ThrowIfFailing(string operation)
    {
        if (_failures.TryGetValue(operation, out var failure))
            throw new SpoolerException(operation, failure.Win32Error, $"Injected failure of {operation}.", failure.Code);
    }

    private void Record(string operation, string target, string? detail = null) => _writes.Add(new SpoolerWrite(operation, target, detail));

    private FakeDriverProfile ProfileLocked(string driverName) =>
        _profiles.TryGetValue(driverName, out var profile) ? profile : _defaultProfile;

    private void AddQueueLocked(PrintQueueInfo queue, DevMode? globalDefaults)
    {
        _queues.Add(queue);
        _global[queue.Name] = globalDefaults?.Clone() ?? FakeDevModes.CreateDefault(ProfileLocked(queue.DriverName), queue.Name);
    }

    private void RemoveQueueLocked(string name)
    {
        _queues.RemoveAll(q => Same(q.Name, name));
        _global.Remove(name);
        _perUser.Remove(name);
        _jobs.Remove(name);
        _jobDevModes.Remove(name);
    }

    private DevMode EffectiveSourceLocked(PrintQueueInfo queue) =>
        _perUser.GetValueOrDefault(queue.Name)
        ?? _global.GetValueOrDefault(queue.Name)
        ?? FakeDevModes.CreateDefault(ProfileLocked(queue.DriverName), queue.Name);

    /// <summary>
    /// DocumentProperties-like merge, as the v4 driver measured on Windows does it: when dmPaperWidth and dmPaperLength are
    /// both set and positive they select the first paper of that size (within 1 mm, in DC_PAPERS order) before dmPaperSize and
    /// then dmFormName (a size no paper has is ignored); the output carries the resolved paper's size with both size flags
    /// cleared (set, with <see cref="FakeDriverProfile.SetsPaperSizeFields"/>). Enforces the profile's other rules.
    /// </summary>
    private DevMode ValidateLocked(PrintQueueInfo queue, DevMode requested)
    {
        var profile = ProfileLocked(queue.DriverName);
        var papers = PapersLocked(profile);
        var baseline = _global.GetValueOrDefault(queue.Name) ?? FakeDevModes.CreateDefault(profile, queue.Name);
        var result = requested.DriverExtra == profile.PrivateSize ? requested.Clone() : MergeIntoDefault(profile, queue.Name, requested);

        bool sized = requested.Has(DevModeFields.PaperWidth | DevModeFields.PaperLength) && requested.PaperWidth > 0 && requested.PaperLength > 0;
        var paper = (sized ? papers.FirstOrDefault(p => SameSize(p, requested)) : null)
            ?? (requested.Has(DevModeFields.PaperSize) ? papers.FirstOrDefault(p => p.Id == requested.PaperSize) : null)
            ?? (requested.Has(DevModeFields.FormName) ? papers.FirstOrDefault(p => Same(p.Name, requested.FormName)) : null)
            ?? profile.DefaultPaper;
        result.PaperSize = paper.Id;
        result.FormName = profile.ClearsFormName ? "" : paper.Name;
        result.PaperWidth = (short)paper.WidthTenthsOfMillimeter;
        result.PaperLength = (short)paper.HeightTenthsOfMillimeter;
        result.Fields = profile.SetsPaperSizeFields
            ? result.Fields | DevModeFields.PaperSize | DevModeFields.FormName | DevModeFields.PaperWidth | DevModeFields.PaperLength
            : (result.Fields | DevModeFields.PaperSize | DevModeFields.FormName) & ~(DevModeFields.PaperWidth | DevModeFields.PaperLength);

        if (profile.IgnoreOrientation || result.Orientation is not (DmOrientation.Portrait or DmOrientation.Landscape))
            result.Orientation = baseline.Orientation;
        result.Scale = profile.IgnoreScale ? baseline.Scale : Math.Clamp(result.Scale, profile.MinScale, profile.MaxScale);

        if (!profile.SupportsDuplex)
        {
            result.Fields &= ~DevModeFields.Duplex;
            result.Duplex = DmDuplex.Simplex;
        }
        else if (result.Duplex is < DmDuplex.Simplex or > DmDuplex.Horizontal)
        {
            result.Duplex = profile.DefaultDuplex;
        }

        if (profile is { CassetteMinimumWidthTenthsOfMillimeter: { } minimum, SourceBelowCassetteMinimum: { } forced } && paper.WidthTenthsOfMillimeter < minimum)
            result.DefaultSource = forced;
        else if (profile.Bins.All(b => b.Id != result.DefaultSource))
            result.DefaultSource = profile.DefaultSource;
        return result;
    }

    private static DevMode MergeIntoDefault(FakeDriverProfile profile, string queueName, DevMode requested)
    {
        var merged = FakeDevModes.CreateDefault(profile, queueName);
        merged.Fields = requested.Fields | DevModeFields.PaperSize | DevModeFields.FormName;
        merged.Orientation = requested.Orientation;
        merged.PaperSize = requested.PaperSize;
        merged.FormName = requested.FormName;
        merged.Scale = requested.Scale;
        merged.Duplex = requested.Duplex;
        merged.DefaultSource = requested.DefaultSource;
        return merged;
    }

    /// <summary>The profile's built-in papers plus forms-database forms that fit its extents (ids from 257, in forms order).</summary>
    private List<PaperCapability> PapersLocked(FakeDriverProfile profile)
    {
        var papers = profile.BuiltInPapers.Where(p => profile.Lists(p.Name)).ToList();
        short next = FakeDriverProfile.FirstFormPaperId;
        foreach (var form in _forms)
        {
            if (form.Kind == FormKind.Builtin || profile.BuiltInPapers.Any(p => Same(p.Name, form.Name)))
                continue;
            int width = Tenths(form.WidthMicrometers);
            int height = Tenths(form.HeightMicrometers);
            if (!profile.Fits(width, height) || !profile.Lists(form.Name))
                continue;
            papers.Add(new PaperCapability(next++, form.Name, width, height));
        }
        return papers;
    }

    private int WidthMicrometers(PaperCapability paper) => FindForm(paper.Name)?.WidthMicrometers ?? paper.WidthTenthsOfMillimeter * 100;

    private int HeightMicrometers(PaperCapability paper) => FindForm(paper.Name)?.HeightMicrometers ?? paper.HeightTenthsOfMillimeter * 100;

    private PrintQueueInfo? FindQueue(string name) => _queues.FirstOrDefault(q => Same(q.Name, name));

    private PrinterPortInfo? FindPort(string name) => _ports.FirstOrDefault(p => Same(p.Name, name));

    private PrinterDriverInfo? FindDriver(string name) => _drivers.FirstOrDefault(d => Same(d.Name, name));

    private FormInfo? FindForm(string name) => _forms.FirstOrDefault(f => Same(f.Name, name));

    private PrintQueueInfo QueueOrThrow(string name, string operation) =>
        FindQueue(name) ?? throw new SpoolerException(operation, ErrorInvalidPrinterName, $"The printer '{name}' does not exist.");

    private int QueueIndexOrThrow(string name, string operation)
    {
        int index = _queues.FindIndex(q => Same(q.Name, name));
        return index >= 0 ? index : throw new SpoolerException(operation, ErrorInvalidPrinterName, $"The printer '{name}' does not exist.");
    }

    private static PrintQueueInfo NewLocalQueue(string name, string driverName, string portName) => new()
    {
        Name = name,
        DriverName = driverName,
        PortName = portName,
        PrintProcessor = "winprint",
        Datatype = "RAW",
        Attributes = PrinterAttributes.Local | PrinterAttributes.DoCompleteFirst | PrinterAttributes.EnableBidi,
    };

    private static int Tenths(int micrometers) => (int)Math.Round(micrometers / 100.0, MidpointRounding.AwayFromZero);

    /// <summary>dmPaperWidth/dmPaperLength within 1 mm (10 tenths) of the paper, the tolerance drivers match a size to a form with.</summary>
    private static bool SameSize(PaperCapability paper, DevMode devMode) =>
        Math.Abs(paper.WidthTenthsOfMillimeter - devMode.PaperWidth) <= 10 && Math.Abs(paper.HeightTenthsOfMillimeter - devMode.PaperLength) <= 10;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static FormInfo Builtin(string name, int width, int height) => new(name, FormKind.Builtin, width, height, 0, 0, width, height);

    private static PrinterPortInfo LocalPort(string name) =>
        new() { Name = name, MonitorName = "Local Port", Description = "Local Port", Kind = PortKind.Local };

    private static PrinterPortInfo UsbPort(string name) =>
        new() { Name = name, MonitorName = "USB Monitor", Description = "Virtual printer port for USB", Kind = PortKind.Usb };
}
