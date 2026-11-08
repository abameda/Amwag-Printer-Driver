using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Windows.Interop;
using Amwag.PrinterSetup.Windows.Spooler;

namespace Amwag.PrinterSetup.Windows.Tests.Spooler;

public sealed class SpoolerConversionsTests
{
    [Theory]
    // Strings as Windows reports them (Get-PrinterPort shows the same PORT_INFO_2 fields).
    [InlineData("LPT1:", "Local Monitor", "Local Port", PortKind.Local)]
    [InlineData("PORTPROMPT:", "Local Monitor", "Local Port", PortKind.Local)]
    [InlineData("192.168.11.200", "TCPMON.DLL", "Standard TCP/IP Port", PortKind.StandardTcpIp)]
    [InlineData("IP_10.0.0.5", "Standard TCP/IP Port", null, PortKind.StandardTcpIp)]
    [InlineData("IP_10.0.0.5", "TCPMON.DLL", "Standard-TCP/IP-Port", PortKind.StandardTcpIp)]
    [InlineData("USB001", "USB Monitor", "Virtual printer port for USB", PortKind.Usb)]
    [InlineData("USB002", "Dynamic Print Monitor", null, PortKind.Usb)]
    [InlineData("usb003", null, null, PortKind.Usb)]
    [InlineData("WSD-5f3c1a2b-7d0e-4c1f-9a6b-2e8d4c0b1a90", null, null, PortKind.Wsd)]
    [InlineData("MyPort", "WSD Port", null, PortKind.Wsd)]
    [InlineData("10.1.2.80:Tickets", "LPR Port", "LPR Port", PortKind.Lpr)]
    [InlineData("printsrv:lp", "LPR Port", "LPR-Anschluss", PortKind.Lpr)]
    [InlineData("http://10.1.2.80:631/ipp/print", null, "Internet Port", PortKind.Internet)]
    [InlineData("https://printer12.branch.local/ipp/print", null, "Internetanschluss", PortKind.Internet)]
    [InlineData("SHRFAX:", "Microsoft Shared Fax Monitor", "Fax Monitor Port", PortKind.Other)]
    [InlineData("USB0001", null, null, PortKind.Other)]
    [InlineData("USBPRINT", null, null, PortKind.Other)]
    public void Ports_are_classified_by_monitor_description_and_name(string name, string? monitor, string? description, PortKind expected) =>
        Assert.Equal(expected, SpoolerConversions.ClassifyPort(name, monitor, description));

    [Theory]
    [InlineData(Winspool.FORM_USER, FormKind.User)]
    [InlineData(Winspool.FORM_BUILTIN, FormKind.Builtin)]
    [InlineData(Winspool.FORM_PRINTER, FormKind.Printer)]
    public void Form_flags_map_to_kinds(uint flags, FormKind expected) =>
        Assert.Equal(expected, SpoolerConversions.ToFormKind(flags));

    [Fact]
    public void Driver_version_unpacks_four_16_bit_parts_most_significant_first()
    {
        ulong packed = (10UL << 48) | (0UL << 32) | (26100UL << 16) | 4484UL;
        Assert.Equal(new Version(10, 0, 26100, 4484), SpoolerConversions.ToVersion(packed));
        Assert.Equal(new Version(65535, 1, 2, 3), SpoolerConversions.ToVersion(0xFFFF_0001_0002_0003));
        Assert.Null(SpoolerConversions.ToVersion(0));
    }

    [Fact]
    public void Driver_date_is_read_as_utc_filetime()
    {
        long ticks = new DateTime(2006, 6, 21, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var time = new Winspool.FILETIME { dwLowDateTime = unchecked((uint)ticks), dwHighDateTime = (uint)(ticks >> 32) };

        DateTime? date = SpoolerConversions.ToDate(time);

        Assert.Equal(new DateTime(2006, 6, 21, 0, 0, 0, DateTimeKind.Utc), date);
        Assert.Equal(DateTimeKind.Utc, date!.Value.Kind);
        Assert.Null(SpoolerConversions.ToDate(default));
        Assert.Null(SpoolerConversions.ToDate(new Winspool.FILETIME { dwLowDateTime = uint.MaxValue, dwHighDateTime = uint.MaxValue }));
    }

    [Fact]
    public void Job_submission_time_is_utc()
    {
        var time = new Winspool.SYSTEMTIME { wYear = 2026, wMonth = 11, wDayOfWeek = 2, wDay = 3, wHour = 14, wMinute = 5, wSecond = 6, wMilliseconds = 7 };

        DateTime? submitted = SpoolerConversions.ToUtc(time);

        Assert.Equal(new DateTime(2026, 11, 3, 14, 5, 6, 7, DateTimeKind.Utc), submitted);
        Assert.Equal(DateTimeKind.Utc, submitted!.Value.Kind);
        Assert.Null(SpoolerConversions.ToUtc(default));
        Assert.Null(SpoolerConversions.ToUtc(time with { wMonth = 13 }));
    }

    [Theory]
    [InlineData(1722, FindingCode.SpoolerUnavailable)]
    [InlineData(1726, FindingCode.SpoolerUnavailable)]
    [InlineData(1727, FindingCode.SpoolerUnavailable)]
    [InlineData(1753, FindingCode.SpoolerUnavailable)]
    [InlineData(5, FindingCode.ElevationRequired)]
    [InlineData(1802, FindingCode.QueueCreateFailed)]
    [InlineData(1797, FindingCode.QueueCreateFailed)]
    public void Win32_errors_map_to_finding_codes(int error, FindingCode expected) =>
        Assert.Equal(expected, SpoolerConversions.CodeFor(error, FindingCode.QueueCreateFailed));

    [Fact]
    public void Failure_names_the_api_error_and_context()
    {
        var exception = SpoolerConversions.Failure("AddPrinter", 1802, FindingCode.QueueCreateFailed, "queue 'AMWAG Ticket Printer'");

        Assert.Equal("AddPrinter", exception.Operation);
        Assert.Equal(1802, exception.Win32Error);
        Assert.Equal(FindingCode.QueueCreateFailed, exception.Code);
        Assert.StartsWith("AddPrinter failed (Win32 error 1802): ", exception.Message);
        Assert.Contains("queue 'AMWAG Ticket Printer'", exception.Message);
        Assert.False(exception.IsSpoolerUnavailable);
    }

    [Fact]
    public void Failure_without_an_error_code_says_so()
    {
        var exception = SpoolerConversions.Failure("DeviceCapabilities(DC_PAPERS)", 0, FindingCode.OperationFailed);

        Assert.Contains("without reporting an error code", exception.Message);
        Assert.Equal(FindingCode.OperationFailed, exception.Code);
    }

    [Fact]
    public void Rpc_failures_are_flagged_as_spooler_unavailable()
    {
        var exception = SpoolerConversions.Failure("EnumPrinters", 1722, FindingCode.OperationFailed);

        Assert.True(exception.IsSpoolerUnavailable);
        Assert.Equal(FindingCode.SpoolerUnavailable, exception.Code);
    }
}
