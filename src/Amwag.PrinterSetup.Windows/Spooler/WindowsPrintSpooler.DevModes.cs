using System.Runtime.InteropServices;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using static Amwag.PrinterSetup.Windows.Interop.Winspool;

namespace Amwag.PrinterSetup.Windows.Spooler;

// Driver capabilities, default DEVMODEs and jobs.
public sealed unsafe partial class WindowsPrintSpooler
{
    // DEVMODEW (wingdi.h): dmDeviceName WCHAR[32], dmSpecVersion, dmDriverVersion, then dmSize and dmDriverExtra.
    private const int DmSizeOffset = 68;
    private const int DmDriverExtraOffset = 70;

    private const uint MaxJobs = 512;

    public DriverCapabilities GetCapabilities(string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        string? port = QueuePort(queueName);

        int paperCount = DeviceCapabilitiesW(queueName, port, DC_PAPERS, null, null);
        if (paperCount < 0)
            throw Fail("DeviceCapabilities(DC_PAPERS)", Marshal.GetLastPInvokeError(), FindingCode.OperationFailed, $"queue '{queueName}'");

        ushort[] paperIds = Capability<ushort>(queueName, port, DC_PAPERS, 1);
        string[] paperNames = Names(queueName, port, DC_PAPERNAMES, PaperNameChars);
        POINT[] paperSizes = Capability<POINT>(queueName, port, DC_PAPERSIZE, 1);
        int papers = Math.Min(paperIds.Length, Math.Min(paperNames.Length, paperSizes.Length));
        if (papers != paperIds.Length || papers != paperNames.Length || papers != paperSizes.Length)
            _log.Warn("spooler.capabilities.count_mismatch", $"The driver of '{queueName}' reports different paper counts.", Data(
                ("queue", queueName), ("papers", paperIds.Length), ("names", paperNames.Length), ("sizes", paperSizes.Length)));

        ushort[] binIds = Capability<ushort>(queueName, port, DC_BINS, 1);
        string[] binNames = Names(queueName, port, DC_BINNAMES, BinNameChars);
        int bins = Math.Min(binIds.Length, binNames.Length);

        (int? minWidth, int? minHeight) = Extent(DeviceCapabilitiesW(queueName, port, DC_MINEXTENT, null, null));
        (int? maxWidth, int? maxHeight) = Extent(DeviceCapabilitiesW(queueName, port, DC_MAXEXTENT, null, null));
        int orientation = DeviceCapabilitiesW(queueName, port, DC_ORIENTATION, null, null);
        int driverVersion = DeviceCapabilitiesW(queueName, port, DC_DRIVER, null, null);

        var capabilities = new DriverCapabilities
        {
            QueueName = queueName,
            Papers = Enumerable.Range(0, papers)
                .Select(i => new PaperCapability(unchecked((short)paperIds[i]), paperNames[i], paperSizes[i].x, paperSizes[i].y))
                .ToList(),
            Bins = Enumerable.Range(0, bins).Select(i => new BinCapability(unchecked((short)binIds[i]), binNames[i])).ToList(),
            MinWidthTenthsOfMillimeter = minWidth,
            MinHeightTenthsOfMillimeter = minHeight,
            MaxWidthTenthsOfMillimeter = maxWidth,
            MaxHeightTenthsOfMillimeter = maxHeight,
            SupportsDuplex = DeviceCapabilitiesW(queueName, port, DC_DUPLEX, null, null) == 1,
            LandscapeRotation = orientation < 0 ? null : orientation,
            MediaReady = Names(queueName, port, DC_MEDIAREADY, PaperNameChars),
            DriverDllVersion = driverVersion < 0 ? null : $"0x{driverVersion:X4}",
        };

        _log.Debug("spooler.capabilities.read", $"Driver capabilities of '{queueName}' read.", Data(
            ("queue", queueName), ("papers", capabilities.Papers.Count), ("bins", capabilities.Bins.Count),
            ("minExtent", $"{minWidth} x {minHeight}"), ("maxExtent", $"{maxWidth} x {maxHeight}"),
            ("paperList", string.Join("; ", capabilities.Papers.Select(p => $"{p.Id} '{p.Name}' {p.WidthTenthsOfMillimeter}x{p.HeightTenthsOfMillimeter}")))));
        return capabilities;
    }

    public DevMode? GetDevMode(string queueName, DevModeScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        using var printer = Open(queueName, PRINTER_ACCESS_USE, FindingCode.OperationFailed);

        DevMode? devMode = scope switch
        {
            DevModeScope.Global => ReadPrinterDevMode(printer, 8),
            DevModeScope.PerUser => ReadPrinterDevMode(printer, 9),
            DevModeScope.Effective => ReadEffectiveDevMode(printer, queueName),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown DEVMODE scope."),
        };

        _log.Debug("spooler.devmode.read", $"{scope} DEVMODE of '{queueName}' read.", DevModeData(queueName, scope, devMode));
        return devMode;
    }

    public DevMode ValidateDevMode(string queueName, DevMode requested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentNullException.ThrowIfNull(requested);
        using var printer = Open(queueName, PRINTER_ACCESS_USE, FindingCode.OperationFailed);
        using var output = new HGlobalBuffer(DevModeSize(printer, queueName));

        // A copy, so a driver writing into its input cannot change the caller's DEVMODE. Never DM_IN_PROMPT or DM_UPDATE.
        byte[] input = requested.ToArray();
        int result, error;
        fixed (byte* pInput = input)
        {
            result = DocumentPropertiesW(IntPtr.Zero, printer, queueName, (void*)output.Pointer, pInput, DM_IN_BUFFER | DM_OUT_BUFFER);
            error = Marshal.GetLastPInvokeError();
        }
        if (result != IDOK)
            throw Fail("DocumentProperties(DM_IN_BUFFER | DM_OUT_BUFFER)", error, FindingCode.DriverRejectedSetting, $"queue '{queueName}', returned {result}");

        DevMode validated = ReadDevMode("DocumentProperties", output, output.Pointer);
        _log.Debug("spooler.devmode.validated", $"The driver of '{queueName}' validated a DEVMODE.", Data(
            ("queue", queueName), ("requestedForm", requested.FormName), ("form", validated.FormName),
            ("requestedPaperSize", requested.PaperSize), ("paperSize", validated.PaperSize), ("defaultSource", validated.DefaultSource)));
        return validated;
    }

    /// <summary>
    /// Global: SetPrinter level 8 with PRINTER_ALL_ACCESS, which also resets the calling user's per-user DEVMODE to
    /// the new global default (https://learn.microsoft.com/windows/win32/printdocs/per-user-devmode). PerUser: level 9 with
    /// PRINTER_ACCESS_USE. Validate with <see cref="ValidateDevMode"/> first, as the SetPrinter remarks require.
    /// </summary>
    public void SetDevMode(string queueName, DevModeScope scope, DevMode devMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentNullException.ThrowIfNull(devMode);
        // The global defaults need a handle with PRINTER_ALL_ACCESS, as the queue changes (level 2) use: SetPrinter level 8 on
        // a handle opened with PRINTER_ACCESS_ADMINISTER alone fails with error 5 even when elevated (measured on Windows 11
        // build 26300 with the TA KXv4 driver; docs/research/kx-custom-paper-size.md section 6).
        (uint level, uint access) = scope switch
        {
            DevModeScope.Global => (8u, PRINTER_ALL_ACCESS),
            DevModeScope.PerUser => (9u, PRINTER_ACCESS_USE),
            _ => throw new ArgumentException("The effective DEVMODE is computed by the driver and cannot be written.", nameof(scope)),
        };

        using var printer = Open(queueName, access, FindingCode.DefaultsNotPersisted);
        byte[] bytes = devMode.ToArray();
        bool succeeded;
        int error;
        fixed (byte* pDevMode = bytes)
        {
            var global = new PRINTER_INFO_8W { pDevMode = (IntPtr)pDevMode };
            var perUser = new PRINTER_INFO_9W { pDevMode = (IntPtr)pDevMode };
            succeeded = level == 8 ? SetPrinterW(printer, 8, &global, 0) : SetPrinterW(printer, 9, &perUser, 0);
            error = succeeded ? 0 : Marshal.GetLastPInvokeError();
        }
        if (!succeeded)
            throw Fail($"SetPrinter(level {level})", error, FindingCode.DefaultsNotPersisted, $"queue '{queueName}'");

        _log.Info("spooler.devmode.written", $"{scope} DEVMODE of '{queueName}' written.", DevModeData(queueName, scope, devMode));
    }

    public IReadOnlyList<PrintJobInfo> GetJobs(string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        using var printer = Open(queueName, PRINTER_ACCESS_USE, FindingCode.OperationFailed);
        int count = 0;
        using var buffer = Fetch("EnumJobs", FindingCode.OperationFailed,
            (IntPtr b, int size, out int needed) => EnumJobsW(printer, 0, MaxJobs, 1, b, size, out needed, out count));

        var jobs = new List<PrintJobInfo>(count);
        JOB_INFO_1W* items = Items<JOB_INFO_1W>(buffer, count, "EnumJobs");
        for (int i = 0; i < count; i++)
        {
            JOB_INFO_1W item = items[i];
            jobs.Add(new PrintJobInfo
            {
                JobId = (int)item.JobId,
                Document = Text(item.pDocument),
                UserName = Text(item.pUserName),
                Status = (JobStatusFlags)item.Status,
                StatusText = SpoolerConversions.NullIfEmpty(Text(item.pStatus)),
                TotalPages = (int)item.TotalPages,
                PagesPrinted = (int)item.PagesPrinted,
                Submitted = SpoolerConversions.ToUtc(item.Submitted),
            });
        }

        _log.Debug("spooler.jobs.read", $"{jobs.Count} jobs of '{queueName}' enumerated.", Data(("queue", queueName), ("count", jobs.Count)));
        return jobs;
    }

    public DevMode? GetJobDevMode(string queueName, int jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        using var printer = Open(queueName, PRINTER_ACCESS_USE, FindingCode.OperationFailed);
        // GetJob reports a job id that is not (or no longer) in the queue as ERROR_INVALID_PARAMETER.
        using var buffer = TryFetch("GetJob", FindingCode.OperationFailed, ERROR_INVALID_PARAMETER,
            (IntPtr b, int size, out int needed) => GetJobW(printer, unchecked((uint)jobId), 2, b, size, out needed));

        IntPtr devModePointer = buffer is null ? IntPtr.Zero : Items<JOB_INFO_2W>(buffer, 1, "GetJob")->pDevMode;
        DevMode? devMode = buffer is null || devModePointer == IntPtr.Zero ? null : ReadDevMode("GetJob", buffer, devModePointer);

        _log.Debug("spooler.job.devmode", $"DEVMODE of job {jobId} on '{queueName}' read.", Data(
            ("queue", queueName), ("jobId", jobId), ("jobFound", buffer is not null), ("form", devMode?.FormName)));
        return devMode;
    }

    private string? QueuePort(string queueName)
    {
        using var printer = Open(queueName, PRINTER_ACCESS_USE, FindingCode.OperationFailed);
        using var buffer = Fetch("GetPrinter", FindingCode.OperationFailed,
            (IntPtr b, int size, out int needed) => GetPrinterW(printer, 2, b, size, out needed));
        return Text(Items<PRINTER_INFO_2W>(buffer, 1, "GetPrinter")->pPortName);
    }

    private DevMode? ReadPrinterDevMode(SafePrinterHandle printer, uint level)
    {
        using var buffer = Fetch($"GetPrinter(level {level})", FindingCode.OperationFailed,
            (IntPtr b, int size, out int needed) => GetPrinterW(printer, level, b, size, out needed));
        string operation = $"GetPrinter(level {level})";
        IntPtr devMode = level == 8 ? Items<PRINTER_INFO_8W>(buffer, 1, operation)->pDevMode : Items<PRINTER_INFO_9W>(buffer, 1, operation)->pDevMode;
        return devMode == IntPtr.Zero ? null : ReadDevMode(operation, buffer, devMode);
    }

    private DevMode ReadEffectiveDevMode(SafePrinterHandle printer, string queueName)
    {
        using var buffer = new HGlobalBuffer(DevModeSize(printer, queueName));
        int result = DocumentPropertiesW(IntPtr.Zero, printer, queueName, (void*)buffer.Pointer, null, DM_OUT_BUFFER);
        if (result != IDOK)
            throw Fail("DocumentProperties(DM_OUT_BUFFER)", Marshal.GetLastPInvokeError(), FindingCode.OperationFailed, $"queue '{queueName}', returned {result}");
        return ReadDevMode("DocumentProperties", buffer, buffer.Pointer);
    }

    /// <summary>DocumentProperties with fMode 0: the size of the driver's full DEVMODE (public part plus private data).</summary>
    private int DevModeSize(SafePrinterHandle printer, string queueName)
    {
        int size = DocumentPropertiesW(IntPtr.Zero, printer, queueName, null, null, 0);
        if (size <= 0)
            throw Fail("DocumentProperties", Marshal.GetLastPInvokeError(), FindingCode.OperationFailed, $"queue '{queueName}', size query returned {size}");
        return size;
    }

    /// <summary>Copies dmSize + dmDriverExtra bytes after checking they lie inside the buffer the spooler filled.</summary>
    private DevMode ReadDevMode(string operation, HGlobalBuffer buffer, IntPtr devMode)
    {
        long available = (long)buffer.Pointer + buffer.Size - (long)devMode;
        if ((long)devMode < (long)buffer.Pointer || available < DevMode.MinimumPublicSize)
            throw Fail(operation, ERROR_INVALID_DATA, FindingCode.OperationFailed, $"DEVMODE outside the returned data ({available} bytes available)");

        byte* bytes = (byte*)devMode;
        int size = *(ushort*)(bytes + DmSizeOffset);
        int extra = *(ushort*)(bytes + DmDriverExtraOffset);
        if (size < DevMode.MinimumPublicSize || size + extra > available)
            throw Fail(operation, ERROR_INVALID_DATA, FindingCode.OperationFailed, $"DEVMODE dmSize {size} + dmDriverExtra {extra} exceeds the {available} bytes returned");

        return DevMode.FromBytes(new ReadOnlySpan<byte>(bytes, size + extra));
    }

    /// <summary>An array capability: the first call counts the entries, the second fills them.</summary>
    private static T[] Capability<T>(string queueName, string? port, ushort capability, int unitsPerEntry)
        where T : unmanaged
    {
        int count = DeviceCapabilitiesW(queueName, port, capability, null, null);
        if (count <= 0)
            return [];

        var entries = new T[count * unitsPerEntry];
        fixed (T* output = entries)
            count = DeviceCapabilitiesW(queueName, port, capability, output, null);
        return count <= 0 ? [] : entries[..(Math.Min(count, entries.Length / unitsPerEntry) * unitsPerEntry)];
    }

    /// <summary>Fixed-width name entries (DC_PAPERNAMES, DC_BINNAMES, DC_MEDIAREADY), null-terminated unless full.</summary>
    private static string[] Names(string queueName, string? port, ushort capability, int charsPerEntry)
    {
        char[] chars = Capability<char>(queueName, port, capability, charsPerEntry);
        var names = new string[chars.Length / charsPerEntry];
        for (int i = 0; i < names.Length; i++)
        {
            ReadOnlySpan<char> entry = chars.AsSpan(i * charsPerEntry, charsPerEntry);
            int end = entry.IndexOf('\0');
            names[i] = new string(end < 0 ? entry : entry[..end]);
        }
        return names;
    }

    /// <summary>
    /// DC_MINEXTENT / DC_MAXEXTENT: LOWORD is dmPaperWidth, HIWORD dmPaperLength
    /// (https://learn.microsoft.com/windows/win32/api/wingdi/nf-wingdi-devicecapabilitiesw); -1 (failure) and 0 (not
    /// supported) are not reported.
    /// </summary>
    private static (int? Width, int? Height) Extent(int packed) =>
        packed is -1 or 0 ? (null, null) : (packed & 0xFFFF, (int)((uint)packed >> 16));

    private static Dictionary<string, object?> DevModeData(string queueName, DevModeScope scope, DevMode? devMode) =>
        Data(("queue", queueName), ("scope", scope), ("present", devMode is not null), ("form", devMode?.FormName),
            ("paperSize", devMode?.PaperSize), ("orientation", devMode?.Orientation), ("duplex", devMode?.Duplex),
            ("scale", devMode?.Scale), ("defaultSource", devMode?.DefaultSource), ("size", devMode?.TotalSize));
}
