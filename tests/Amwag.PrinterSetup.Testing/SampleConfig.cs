using System.Globalization;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Units;

namespace Amwag.PrinterSetup.Testing;

/// <summary>
/// A deployment configuration shaped like the real one: a confirmed AMWAG Ticket (3.24 in x 6.70 in; the shipped one is 3.27 in), three TA models
/// (P-4030DN, P-4531DN, P-5032DN) sharing one approved x64 KX package, MP tray bin 4 "MP tray", MP tray range
/// 70x148..216x356 mm and cassette range 105x148..216x356 mm. Hardware IDs are the 1284-derived IDs of the TA INFs
/// (MFG "UTAX_TA", or "TA_UTAX" for the P-5032DN, + MDL + checksum); driver names and the package are fixtures.
/// </summary>
public static class SampleConfig
{
    public const string FormName = "AMWAG Ticket";
    public const string PackageId = "ta-kx-x64";
    public const string P4030 = "P-4030DN";
    public const string P4531 = "P-4531DN";
    public const string P5032 = "P-5032DN";
    public const short MpTrayBinId = DmBin.Manual;
    public const string MpTrayBinName = "MP tray";
    public const string Environment = "Windows x64";
    public const string MicrosoftPcl6ClassDriver = "Microsoft PCL6 Class Driver";

    public static IReadOnlyList<string> ModelIds { get; } = [P4030, P4531, P5032];

    /// <summary>82 296 x 170 180 micrometers.</summary>
    public static PaperDimensions Ticket { get; } = PaperDimensions.FromUnits(3.24m, 6.70m, LengthUnit.Inch);

    public static DeploymentConfig Create() => new()
    {
        SchemaVersion = 1,
        ConfigVersion = "2026.11-test",
        Ticket = new TicketPaperConfig
        {
            FormName = FormName,
            Width = 3.24m,
            Height = 6.70m,
            Unit = LengthUnit.Inch,
            DimensionsConfirmed = true,
            ConfirmedBy = "AMWAG IT (test fixture)",
            ConfirmedOn = "2026-10-30",
        },
        PrintPreferences = new PrintPreferencesConfig(),
        Connection = new ConnectionConfig(),
        DriverSource = new DriverSourceConfig(),
        Models = ModelIds.Select(CreateModel).ToList(),
        DriverPackages = [CreatePackage()],
    };

    public static ModelDefinition Model(string modelId) =>
        Create().FindModel(modelId) ?? throw new ArgumentException($"Unknown sample model '{modelId}'.", nameof(modelId));

    /// <summary>Preferred (INF model) driver name, e.g. "TA P-4030DN KX"; Plug and Play queues are named after it.</summary>
    public static string DriverName(string modelId) => $"TA {modelId} KX";

    /// <summary>A second accepted driver of the model.</summary>
    public static string XpsDriverName(string modelId) => $"TA {modelId} KX (XPS)";

    /// <summary>The IEEE 1284 manufacturer (MFG) the model reports: "UTAX_TA", or "TA_UTAX" for the P-5032DN.</summary>
    public static string Ieee1284Manufacturer(string modelId) => modelId == P5032 ? "TA_UTAX" : "UTAX_TA";

    /// <summary>The hardware ID after the enumerator: MFG + MDL + checksum (e.g. "UTAX_TAP-4030DN1652").</summary>
    public static string HardwareIdName(string modelId) => modelId switch
    {
        P4030 => "UTAX_TAP-4030DN1652",
        P4531 => "UTAX_TAP-4531DND6CF",
        P5032 => "TA_UTAXP-5032DNEED0",
        _ => throw new ArgumentException($"Unknown sample model '{modelId}'.", nameof(modelId)),
    };

    public static string UsbHardwareId(string modelId) => @"USBPRINT\" + HardwareIdName(modelId);

    public static string WsdHardwareId(string modelId) => @"WSDPRINT\" + HardwareIdName(modelId);

    public static string DeviceId1284(string modelId) =>
        $"MFG:{Ieee1284Manufacturer(modelId)};MDL:{modelId};CMD:PCLXL,PCL5E,POSTSCRIPT,PDF;CLS:PRINTER;DES:TA Triumph-Adler {modelId};";

    public static string NetworkModelName(string modelId) => "TA Triumph-Adler " + modelId;

    public static string UsbInstanceId(string modelId, string serial = "VCF5X01234") => $@"USBPRINT\{HardwareIdName(modelId)}\7&2B5A1C3&0&{serial}";

    /// <summary>A USBPRINT device node; unbound devices report problem code 28 (drivers not installed).</summary>
    public static PnpPrinterDevice UsbDevice(
        string modelId,
        string? usbPort = "USB001",
        bool present = true,
        bool driverBound = true,
        string? instanceId = null,
        string? driverDescription = null) => new()
        {
            InstanceId = instanceId ?? UsbInstanceId(modelId),
            Bus = PnpBus.Usb,
            HardwareIds = [UsbHardwareId(modelId), HardwareIdName(modelId)],
            CompatibleIds = [@"USBPRINT\" + Ieee1284Manufacturer(modelId) + modelId, "GENPRINT"],
            FriendlyName = NetworkModelName(modelId),
            Description = NetworkModelName(modelId),
            Manufacturer = driverBound ? "TA Triumph-Adler" : null,
            DeviceClass = driverBound ? "Printer" : null,
            IsPresent = present,
            ProblemCode = present && !driverBound ? 28 : null,
            DriverDescription = driverBound ? driverDescription ?? DriverName(modelId) : null,
            DriverVersion = driverBound ? "8.4.1013.0" : null,
            ParentInstanceId = @"USB\VID_0482&PID_0703\VCF5X01234",
            UsbPortName = usbPort,
        };

    /// <summary>
    /// What a TA printer reports over SNMP; with <paramref name="snmp"/> false (or no model) SNMP is silent. Each address has
    /// its own serial number (<see cref="SerialNumber"/>), so distinct fake printers never look like one printer. The resolved
    /// address is the IP literal itself unless <paramref name="resolvedAddress"/> is given (for a host name).
    /// </summary>
    public static NetworkIdentity NetworkIdentity(
        string address,
        string? modelId,
        bool reachable = true,
        bool snmp = true,
        string? resolvedAddress = null,
        string? serialNumber = null)
    {
        bool responds = snmp && modelId is not null;
        return new NetworkIdentity
        {
            Address = address,
            ResolvedAddress = resolvedAddress ?? FakeNetworkProbe.IpLiteral(address),
            RawPortReachable = reachable,
            SnmpResponded = responds,
            SysDescr = responds ? NetworkModelName(modelId!) : null,
            SysObjectId = responds ? "1.3.6.1.4.1.1347.41" : null,
            DeviceDescription = responds ? NetworkModelName(modelId!) : null,
            Ieee1284DeviceId = responds ? DeviceId1284(modelId!) : null,
            SerialNumber = responds ? serialNumber ?? SerialNumber(address) : null,
            Error = responds ? null : "SNMP request timed out.",
        };
    }

    /// <summary>A serial number of the printer at <paramref name="address"/>, the same in every run ("VCF" and seven digits).</summary>
    public static string SerialNumber(string address)
    {
        uint hash = 2166136261; // FNV-1a: string.GetHashCode changes from run to run.
        foreach (char c in address.ToUpperInvariant())
            hash = (hash ^ c) * 16777619;
        return "VCF" + (hash % 10_000_000).ToString("D7", CultureInfo.InvariantCulture);
    }

    private static ModelDefinition CreateModel(string id) => new()
    {
        Id = id,
        DisplayName = "TA Triumph-Adler " + id,
        HardwareIds = [UsbHardwareId(id), @"LPTENUM\" + HardwareIdName(id), WsdHardwareId(id)],
        Ieee1284Ids = id == P5032
            ? [new Ieee1284Identity { Manufacturer = "TA_UTAX", Model = id }, new Ieee1284Identity { Manufacturer = "UTAX_TA", Model = id }]
            : [new Ieee1284Identity { Manufacturer = "UTAX_TA", Model = id }],
        NetworkModelNames = [NetworkModelName(id)],
        AcceptedDrivers = [new AcceptedDriver { Name = DriverName(id), MinVersion = "8.0.0.0" }, new AcceptedDriver { Name = XpsDriverName(id) }],
        PreferredDriverName = DriverName(id),
        DriverPackageId = PackageId,
        TicketPaperSource = new PaperSourceRule { BinIds = [MpTrayBinId], BinNames = [MpTrayBinName], RequiredForTicket = true },
        MultipurposeTrayRange = new PaperRangeMm { MinWidth = 70, MinHeight = 148, MaxWidth = 216, MaxHeight = 356 },
        CassetteRange = new PaperRangeMm { MinWidth = 105, MinHeight = 148, MaxWidth = 216, MaxHeight = 356 },
        SpecSource = "Test fixture",
        Notes = "Fixture values; not the approved manifest.",
    };

    private static DriverPackageDefinition CreatePackage() => new()
    {
        Id = PackageId,
        DisplayName = "TA Triumph-Adler KX Driver (x64)",
        Publisher = "TA Triumph-Adler",
        Version = "8.4.1013",
        InfDriverVer = "06/12/2025,8.4.1013.0",
        DriverType = "KX (PCL XL / KPDL)",
        SourcePage = "https://www.triumph-adler.com/ta-en-de/customer-service/download-centre",
        DownloadUrl = "https://dl.triumph-adler.com/KX_Driver_8.4.1013_x64.zip",
        FileName = "KX_Driver_8.4.1013_x64.zip",
        SizeBytes = 51_234_567,
        Sha256 = "9f2c4e6a8b0d1f3e5a7c9b1d3f5e7a9c0b2d4f6e8a1c3e5b7d9f0a2c4e6b8d0f",
        Environment = Environment,
        Architecture = "x64",
        InfPath = "64bit/OEMSETUP.INF",
        CatalogPath = "64bit/oemsetup.cat",
        TrustedSigners = ["KYOCERA Document Solutions Inc."],
        SupportedOs = ["Windows 10", "Windows 11"],
        MinOsBuild = 10240,
        Approved = true,
        ApprovedBy = "AMWAG IT (test fixture)",
        VerifiedOn = "2026-10-30",
    };
}
