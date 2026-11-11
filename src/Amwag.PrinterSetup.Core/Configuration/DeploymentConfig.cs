using System.Text.Json;
using System.Text.Json.Serialization;
using Amwag.PrinterSetup.Core.Units;

namespace Amwag.PrinterSetup.Core.Configuration;

/// <summary>
/// The approved deployment configuration (config/amwag-printer-config.json): ticket paper, print preferences,
/// supported models and the approved driver manifest. Ticket dimensions are central and cannot be overridden per branch.
/// </summary>
public sealed record DeploymentConfig
{
    public int SchemaVersion { get; init; }
    public string ConfigVersion { get; init; } = "";
    public TicketPaperConfig Ticket { get; init; } = new();
    public PrintPreferencesConfig PrintPreferences { get; init; } = new();
    public ConnectionConfig Connection { get; init; } = new();
    public DriverSourceConfig DriverSource { get; init; } = new();
    public IReadOnlyList<ModelDefinition> Models { get; init; } = [];
    public IReadOnlyList<DriverPackageDefinition> DriverPackages { get; init; } = [];

    /// <summary>The IT Department members the window's Contact IT button offers; with none the button is not shown.</summary>
    public IReadOnlyList<ItContact> ItContacts { get; init; } = [];

    public ModelDefinition? FindModel(string? id) =>
        id is null ? null : Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public DriverPackageDefinition? FindPackage(string? id) =>
        id is null ? null : DriverPackages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed record TicketPaperConfig
{
    /// <summary>Windows form name; at most 31 characters so it fits DEVMODE.dmFormName.</summary>
    public string FormName { get; init; } = "";
    public decimal Width { get; init; }
    public decimal Height { get; init; }
    public LengthUnit Unit { get; init; } = LengthUnit.Inch;

    /// <summary>Must be true (with ConfirmedBy/ConfirmedOn) before Setup/Repair will run for employees.</summary>
    public bool DimensionsConfirmed { get; init; }
    public string? ConfirmedBy { get; init; }
    public string? ConfirmedOn { get; init; }
    public string? Notes { get; init; }

    [JsonIgnore]
    public PaperDimensions Dimensions => PaperDimensions.FromUnits(Width, Height, Unit);
}

public enum PageOrientationSetting { Portrait, Landscape }

public enum DuplexSetting { Simplex, LongEdge, ShortEdge }

public enum PaperSourcePolicy
{
    /// <summary>Leave the paper source to the driver default.</summary>
    DriverDefault,
    /// <summary>
    /// Use the model's multipurpose tray when the driver exposes it; otherwise warn and keep the driver default. A driver that
    /// replaces the tray gets its own source written once with a warning, which then counts as correct (no rewrite on every
    /// run). For a model whose ticketPaperSource.requiredForTicket is true the tray is required as with
    /// <see cref="RequireMultipurposeTray"/>: only that tray can feed the ticket.
    /// </summary>
    PreferMultipurposeTray,
    /// <summary>Fail configuration when the multipurpose tray cannot be selected for the ticket form.</summary>
    RequireMultipurposeTray,
}

public sealed record PrintPreferencesConfig
{
    public PageOrientationSetting Orientation { get; init; } = PageOrientationSetting.Portrait;
    public DuplexSetting Duplex { get; init; } = DuplexSetting.Simplex;
    public int ScalePercent { get; init; } = 100;
    public PaperSourcePolicy PaperSource { get; init; } = PaperSourcePolicy.PreferMultipurposeTray;
}

public sealed record ConnectionConfig
{
    /// <summary>Name given to queues this tool creates (USB queues created by Windows Plug and Play keep their own name).</summary>
    public string NewQueueName { get; init; } = "AMWAG Ticket Printer";
    public int RawPortNumber { get; init; } = 9100;
    public bool SnmpEnabled { get; init; } = true;
    public string SnmpCommunity { get; init; } = "public";

    /// <summary>Query (one SNMP request each) the addresses of existing TCP/IP queues that may be TA printers.</summary>
    public bool IdentifyExistingNetworkQueues { get; init; } = true;
    public int MaxNetworkProbes { get; init; } = 8;
    public int NetworkTimeoutMilliseconds { get; init; } = 2000;

    /// <summary>After installing a driver for a USB device, how long to wait for Windows to create the queue itself.</summary>
    public int UsbQueueWaitSeconds { get; init; } = 90;
}

public sealed record DriverSourceConfig
{
    /// <summary>Folder holding approved offline driver archives; relative paths resolve against the executable folder.</summary>
    public string OfflineFolder { get; init; } = "drivers";
    public bool AllowOnlineDownload { get; init; } = true;
    public int DownloadTimeoutSeconds { get; init; } = 900;
}

public sealed record ModelDefinition
{
    /// <summary>Stable identifier, e.g. "P-4030DN".</summary>
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";

    /// <summary>Exact Plug and Play hardware IDs taken from the approved INF (USBPRINT\, WSDPRINT\, LPTENUM\ ...).</summary>
    public IReadOnlyList<string> HardwareIds { get; init; } = [];

    /// <summary>IEEE 1284 MFG/MDL pairs (as encoded in the INF hardware IDs) used to match device IDs read over USB/SNMP.</summary>
    public IReadOnlyList<Ieee1284Identity> Ieee1284Ids { get; init; } = [];

    /// <summary>Exact model strings a device reports over the network (SNMP). Optional; unverified entries must be marked in Notes.</summary>
    public IReadOnlyList<string> NetworkModelNames { get; init; } = [];

    /// <summary>Drivers whose queues already count as correctly installed for this model.</summary>
    public IReadOnlyList<AcceptedDriver> AcceptedDrivers { get; init; } = [];

    /// <summary>Driver (INF model name) installed for new or repaired queues.</summary>
    public string PreferredDriverName { get; init; } = "";
    public string DriverPackageId { get; init; } = "";

    public PaperSourceRule TicketPaperSource { get; init; } = new();
    public PaperRangeMm? MultipurposeTrayRange { get; init; }
    public PaperRangeMm? CassetteRange { get; init; }
    public string? SpecSource { get; init; }
    public string? Notes { get; init; }
}

public sealed record Ieee1284Identity
{
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
}

public sealed record AcceptedDriver
{
    public string Name { get; init; } = "";
    /// <summary>Optional minimum driver version (a.b.c.d); older installed versions are treated as outdated.</summary>
    public string? MinVersion { get; init; }

    /// <summary>
    /// Whether the driver offers Windows forms (the forms database) as papers, so the ticket form the tool creates becomes a
    /// paper of the driver. False for a driver that keeps its own paper list (the TA/UTAX KX v3 driver: it offers only the
    /// custom page sizes made in its printing preferences, under their own names). A queue on such a driver is always moved to
    /// the preferred driver, and a new queue never uses it. Default true.
    /// </summary>
    public bool ListsWindowsForms { get; init; } = true;
}

public sealed record PaperSourceRule
{
    /// <summary>DMBIN_* identifiers of the multipurpose tray for the approved driver (locale independent, preferred).</summary>
    public IReadOnlyList<int> BinIds { get; init; } = [];

    /// <summary>Fallback exact display names of the multipurpose tray (case-insensitive).</summary>
    public IReadOnlyList<string> BinNames { get; init; } = [];

    /// <summary>
    /// True when the ticket size is outside the cassette range, so only the MP tray can feed it. The tray is then required
    /// under the PreferMultipurposeTray policy too (DriverDefault never manages the source).
    /// </summary>
    public bool RequiredForTicket { get; init; }
}

/// <summary>Official paper size range of a paper source, in millimeters.</summary>
public sealed record PaperRangeMm
{
    public decimal MinWidth { get; init; }
    public decimal MinHeight { get; init; }
    public decimal MaxWidth { get; init; }
    public decimal MaxHeight { get; init; }

    public bool Contains(PaperDimensions size) =>
        size.WidthMillimeters >= MinWidth && size.WidthMillimeters <= MaxWidth &&
        size.HeightMillimeters >= MinHeight && size.HeightMillimeters <= MaxHeight;
}

public sealed record DriverPackageDefinition
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>DriverVer line from the INF ([Version] section), for traceability.</summary>
    public string? InfDriverVer { get; init; }
    public string DriverType { get; init; } = "";
    public string SourcePage { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string FileName { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    /// <summary>Print environment, e.g. "Windows x64".</summary>
    public string Environment { get; init; } = "Windows x64";
    public string Architecture { get; init; } = "x64";
    /// <summary>Path of the x64 INF inside the archive (forward or back slashes).</summary>
    public string InfPath { get; init; } = "";
    public string? CatalogPath { get; init; }
    /// <summary>Acceptable catalog signer certificate subjects (exact match on the full subject or its CN).</summary>
    public IReadOnlyList<string> TrustedSigners { get; init; } = [];
    public IReadOnlyList<string> SupportedOs { get; init; } = [];
    /// <summary>
    /// Oldest Windows build the package may be installed on. Like <see cref="Approved"/>, it holds back the plan only when the
    /// driver must be installed: with an approved driver already installed, a package that only the USB device binding needs
    /// leaves the device on its driver (a warning) and the queue and its defaults are still configured.
    /// </summary>
    public int MinOsBuild { get; init; } = 10240;
    /// <summary>IT approved the package; an unapproved package is never downloaded or installed.</summary>
    public bool Approved { get; init; }
    public string? ApprovedBy { get; init; }
    public string? VerifiedOn { get; init; }
    public string? Notes { get; init; }
}

/// <summary>An IT Department member an employee can send a picture of the window and the log to on WhatsApp.</summary>
public sealed record ItContact
{
    public string Name { get; init; } = "";
    public string NameArabic { get; init; } = "";

    /// <summary>
    /// The WhatsApp number in international format as https://wa.me/ takes it: digits only, country code first, without "+",
    /// spaces or the national leading zero (Egypt's 0106 192 2226 is "201061922226").
    /// </summary>
    public string WhatsApp { get; init; } = "";
}

/// <summary>Optional per-branch file (branch.json). It cannot change ticket dimensions.</summary>
public sealed record BranchOverride
{
    public string? BranchId { get; init; }
    public string? BranchName { get; init; }
    public string? PrinterAddress { get; init; }
    public string? ModelId { get; init; }
    public string? QueueName { get; init; }
}

/// <summary>Shared JSON settings for configuration, branch files, reports and the worker pipe protocol.</summary>
public static class ConfigJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
