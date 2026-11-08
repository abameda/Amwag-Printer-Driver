namespace Amwag.PrinterSetup.Core.Printing;

/// <summary>DEVMODE.dmFields flags (wingdi.h).</summary>
[Flags]
public enum DevModeFields : uint
{
    None = 0,
    Orientation = 0x00000001,
    PaperSize = 0x00000002,
    PaperLength = 0x00000004,
    PaperWidth = 0x00000008,
    Scale = 0x00000010,
    Position = 0x00000020,
    Nup = 0x00000040,
    DisplayOrientation = 0x00000080,
    Copies = 0x00000100,
    DefaultSource = 0x00000200,
    PrintQuality = 0x00000400,
    Color = 0x00000800,
    Duplex = 0x00001000,
    YResolution = 0x00002000,
    TTOption = 0x00004000,
    Collate = 0x00008000,
    FormName = 0x00010000,
    LogPixels = 0x00020000,
    BitsPerPel = 0x00040000,
    PelsWidth = 0x00080000,
    PelsHeight = 0x00100000,
    DisplayFlags = 0x00200000,
    DisplayFrequency = 0x00400000,
    IcmMethod = 0x00800000,
    IcmIntent = 0x01000000,
    MediaType = 0x02000000,
    DitherType = 0x04000000,
    PanningWidth = 0x08000000,
    PanningHeight = 0x10000000,
    DisplayFixedOutput = 0x20000000,
}

/// <summary>DMORIENT_* values.</summary>
public static class DmOrientation
{
    public const short Portrait = 1;
    public const short Landscape = 2;
}

/// <summary>DMDUP_* values.</summary>
public static class DmDuplex
{
    public const short Simplex = 1;
    public const short Vertical = 2;   // long edge
    public const short Horizontal = 3; // short edge
}

/// <summary>Selected DMPAPER_* values.</summary>
public static class DmPaper
{
    public const short Letter = 1;
    public const short A4 = 9;
    public const short A5 = 11;
    /// <summary>DMPAPER_USER: custom size given by dmPaperWidth/dmPaperLength.</summary>
    public const short User = 256;
}

/// <summary>DMBIN_* values.</summary>
public static class DmBin
{
    public const short Upper = 1;
    public const short OnlyOne = 1;
    public const short Lower = 2;
    public const short Middle = 3;
    public const short Manual = 4;
    public const short Envelope = 5;
    public const short EnvelopeManual = 6;
    public const short Auto = 7;
    public const short Tractor = 8;
    public const short SmallFormat = 9;
    public const short LargeFormat = 10;
    public const short LargeCapacity = 11;
    public const short Cassette = 14;
    public const short FormSource = 15;
    /// <summary>DMBIN_USER: first driver-defined bin identifier.</summary>
    public const short User = 256;
}

/// <summary>PRINTER_ATTRIBUTE_* flags (PRINTER_INFO_2.Attributes).</summary>
[Flags]
public enum PrinterAttributes : uint
{
    None = 0,
    Queued = 0x00000001,
    Direct = 0x00000002,
    Default = 0x00000004,
    Shared = 0x00000008,
    Network = 0x00000010,
    Hidden = 0x00000020,
    Local = 0x00000040,
    EnableDevq = 0x00000080,
    KeepPrintedJobs = 0x00000100,
    DoCompleteFirst = 0x00000200,
    WorkOffline = 0x00000400,
    EnableBidi = 0x00000800,
    RawOnly = 0x00001000,
    Published = 0x00002000,
    Fax = 0x00004000,
    Ts = 0x00008000,
    PushedUser = 0x00020000,
    PushedMachine = 0x00040000,
    Machine = 0x00080000,
    FriendlyName = 0x00100000,
    TsGenericDriver = 0x00200000,
    PerUser = 0x00400000,
    EnterpriseCloud = 0x00800000,
}

/// <summary>PRINTER_STATUS_* flags (PRINTER_INFO_2.Status).</summary>
[Flags]
public enum PrinterStatusFlags : uint
{
    None = 0,
    Paused = 0x00000001,
    Error = 0x00000002,
    PendingDeletion = 0x00000004,
    PaperJam = 0x00000008,
    PaperOut = 0x00000010,
    ManualFeed = 0x00000020,
    PaperProblem = 0x00000040,
    Offline = 0x00000080,
    IoActive = 0x00000100,
    Busy = 0x00000200,
    Printing = 0x00000400,
    OutputBinFull = 0x00000800,
    NotAvailable = 0x00001000,
    Waiting = 0x00002000,
    Processing = 0x00004000,
    Initializing = 0x00008000,
    WarmingUp = 0x00010000,
    TonerLow = 0x00020000,
    NoToner = 0x00040000,
    PagePunt = 0x00080000,
    UserIntervention = 0x00100000,
    OutOfMemory = 0x00200000,
    DoorOpen = 0x00400000,
    ServerUnknown = 0x00800000,
    PowerSave = 0x01000000,
}

/// <summary>JOB_STATUS_* flags (JOB_INFO_1.Status).</summary>
[Flags]
public enum JobStatusFlags : uint
{
    None = 0,
    Paused = 0x00000001,
    Error = 0x00000002,
    Deleting = 0x00000004,
    Spooling = 0x00000008,
    Printing = 0x00000010,
    Offline = 0x00000020,
    PaperOut = 0x00000040,
    Printed = 0x00000080,
    Deleted = 0x00000100,
    BlockedDevq = 0x00000200,
    UserIntervention = 0x00000400,
    Restart = 0x00000800,
    Complete = 0x00001000,
    Retained = 0x00002000,
    RenderingLocally = 0x00004000,
}
