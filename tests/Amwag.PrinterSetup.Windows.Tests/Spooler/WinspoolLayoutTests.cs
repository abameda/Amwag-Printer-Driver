using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Amwag.PrinterSetup.Windows.Interop.Winspool;

namespace Amwag.PrinterSetup.Windows.Tests.Spooler;

/// <summary>
/// Pins the interop structures to the x64 layouts of winspool.h / tcpxcv.h (Windows SDK 10.0.26100, default packing):
/// pointers and HANDLEs are 8 bytes and 8-aligned, DWORD 4, WORD 2, DWORDLONG 8; FILETIME, SIZEL, RECTL and POINT are
/// DWORD/LONG pairs (4-aligned) and SYSTEMTIME eight WORDs (2-aligned); a structure's size is rounded up to its widest
/// member. Every expected value below follows from those rules.
/// </summary>
public sealed class WinspoolLayoutTests
{
    public static TheoryData<Type, int> Sizes => new()
    {
        // 13 pointers (104) + 8 DWORDs (32).
        { typeof(PRINTER_INFO_2W), 136 },
        // A single LPDEVMODEW.
        { typeof(PRINTER_INFO_8W), 8 },
        { typeof(PRINTER_INFO_9W), 8 },
        // DWORD (4) + padding (4) + LPWSTR (8) + SIZEL (8) + RECTL (16).
        { typeof(FORM_INFO_1W), 40 },
        // 3 pointers (24) + 2 DWORDs (8).
        { typeof(PORT_INFO_2W), 32 },
        // DWORD + padding + 9 pointers.
        { typeof(DRIVER_INFO_3W), 80 },
        // DRIVER_INFO_3W (80) + pointer (8) + FILETIME (8) + DWORDLONG (8) + 4 pointers (32).
        { typeof(DRIVER_INFO_6W), 136 },
        // DRIVER_INFO_6W (136) + 4 pointers (32) + DWORD + padding (8) + pointer (8) + FILETIME (8) + DWORDLONG (8).
        { typeof(DRIVER_INFO_8W), 200 },
        // DWORD + padding + 6 pointers (56) + 5 DWORDs (20) + SYSTEMTIME (16) = 92, rounded up to 96.
        { typeof(JOB_INFO_1W), 96 },
        // DWORD + padding + 12 pointers (104) + 7 DWORDs (28) + SYSTEMTIME (16) + 2 DWORDs (8) = 156, rounded up to 160.
        { typeof(JOB_INFO_2W), 160 },
        // 2 pointers (16) + ACCESS_MASK (4) = 20, rounded up to 24.
        { typeof(PRINTER_DEFAULTSW), 24 },
        // No pointers, so identical on x86: see the offsets below; 964 is a multiple of the DWORD alignment.
        { typeof(PORT_DATA_1), 964 },
        { typeof(FILETIME), 8 },
        { typeof(SYSTEMTIME), 16 },
        { typeof(SIZEL), 8 },
        { typeof(RECTL), 16 },
        { typeof(POINT), 8 },
    };

    public static TheoryData<Type, string, int> Offsets => new()
    {
        { typeof(PRINTER_INFO_2W), nameof(PRINTER_INFO_2W.pServerName), 0 },
        { typeof(PRINTER_INFO_2W), nameof(PRINTER_INFO_2W.pPrinterName), 8 },
        { typeof(PRINTER_INFO_2W), nameof(PRINTER_INFO_2W.pPortName), 24 },
        { typeof(PRINTER_INFO_2W), nameof(PRINTER_INFO_2W.pDriverName), 32 },
        { typeof(PRINTER_INFO_2W), nameof(PRINTER_INFO_2W.pDevMode), 56 },
        { typeof(PRINTER_INFO_2W), nameof(PRINTER_INFO_2W.pPrintProcessor), 72 },
        { typeof(PRINTER_INFO_2W), nameof(PRINTER_INFO_2W.pDatatype), 80 },
        { typeof(PRINTER_INFO_2W), nameof(PRINTER_INFO_2W.pSecurityDescriptor), 96 },
        { typeof(PRINTER_INFO_2W), nameof(PRINTER_INFO_2W.Attributes), 104 },
        { typeof(PRINTER_INFO_2W), nameof(PRINTER_INFO_2W.Priority), 108 },
        { typeof(PRINTER_INFO_2W), nameof(PRINTER_INFO_2W.Status), 124 },
        { typeof(PRINTER_INFO_2W), nameof(PRINTER_INFO_2W.cJobs), 128 },
        { typeof(PRINTER_INFO_2W), nameof(PRINTER_INFO_2W.AveragePPM), 132 },

        { typeof(FORM_INFO_1W), nameof(FORM_INFO_1W.Flags), 0 },
        { typeof(FORM_INFO_1W), nameof(FORM_INFO_1W.pName), 8 },
        { typeof(FORM_INFO_1W), nameof(FORM_INFO_1W.Size), 16 },
        { typeof(FORM_INFO_1W), nameof(FORM_INFO_1W.ImageableArea), 24 },

        { typeof(PORT_INFO_2W), nameof(PORT_INFO_2W.pMonitorName), 8 },
        { typeof(PORT_INFO_2W), nameof(PORT_INFO_2W.pDescription), 16 },
        { typeof(PORT_INFO_2W), nameof(PORT_INFO_2W.fPortType), 24 },
        { typeof(PORT_INFO_2W), nameof(PORT_INFO_2W.Reserved), 28 },

        { typeof(DRIVER_INFO_3W), nameof(DRIVER_INFO_3W.pName), 8 },
        { typeof(DRIVER_INFO_3W), nameof(DRIVER_INFO_3W.pEnvironment), 16 },
        { typeof(DRIVER_INFO_3W), nameof(DRIVER_INFO_3W.pDefaultDataType), 72 },

        { typeof(DRIVER_INFO_6W), nameof(DRIVER_INFO_6W.pszzPreviousNames), 80 },
        { typeof(DRIVER_INFO_6W), nameof(DRIVER_INFO_6W.ftDriverDate), 88 },
        { typeof(DRIVER_INFO_6W), nameof(DRIVER_INFO_6W.dwlDriverVersion), 96 },
        { typeof(DRIVER_INFO_6W), nameof(DRIVER_INFO_6W.pszMfgName), 104 },
        { typeof(DRIVER_INFO_6W), nameof(DRIVER_INFO_6W.pszHardwareID), 120 },
        { typeof(DRIVER_INFO_6W), nameof(DRIVER_INFO_6W.pszProvider), 128 },

        { typeof(DRIVER_INFO_8W), nameof(DRIVER_INFO_8W.ftDriverDate), 88 },
        { typeof(DRIVER_INFO_8W), nameof(DRIVER_INFO_8W.dwlDriverVersion), 96 },
        { typeof(DRIVER_INFO_8W), nameof(DRIVER_INFO_8W.pszProvider), 128 },
        { typeof(DRIVER_INFO_8W), nameof(DRIVER_INFO_8W.pszPrintProcessor), 136 },
        { typeof(DRIVER_INFO_8W), nameof(DRIVER_INFO_8W.pszInfPath), 160 },
        { typeof(DRIVER_INFO_8W), nameof(DRIVER_INFO_8W.dwPrinterDriverAttributes), 168 },
        { typeof(DRIVER_INFO_8W), nameof(DRIVER_INFO_8W.pszzCoreDriverDependencies), 176 },
        { typeof(DRIVER_INFO_8W), nameof(DRIVER_INFO_8W.ftMinInboxDriverVerDate), 184 },
        { typeof(DRIVER_INFO_8W), nameof(DRIVER_INFO_8W.dwlMinInboxDriverVerVersion), 192 },

        { typeof(JOB_INFO_1W), nameof(JOB_INFO_1W.pPrinterName), 8 },
        { typeof(JOB_INFO_1W), nameof(JOB_INFO_1W.pDocument), 32 },
        { typeof(JOB_INFO_1W), nameof(JOB_INFO_1W.pStatus), 48 },
        { typeof(JOB_INFO_1W), nameof(JOB_INFO_1W.Status), 56 },
        { typeof(JOB_INFO_1W), nameof(JOB_INFO_1W.TotalPages), 68 },
        { typeof(JOB_INFO_1W), nameof(JOB_INFO_1W.PagesPrinted), 72 },
        { typeof(JOB_INFO_1W), nameof(JOB_INFO_1W.Submitted), 76 },

        { typeof(JOB_INFO_2W), nameof(JOB_INFO_2W.pDriverName), 72 },
        { typeof(JOB_INFO_2W), nameof(JOB_INFO_2W.pDevMode), 80 },
        { typeof(JOB_INFO_2W), nameof(JOB_INFO_2W.pSecurityDescriptor), 96 },
        { typeof(JOB_INFO_2W), nameof(JOB_INFO_2W.Status), 104 },
        { typeof(JOB_INFO_2W), nameof(JOB_INFO_2W.Size), 128 },
        { typeof(JOB_INFO_2W), nameof(JOB_INFO_2W.Submitted), 132 },
        { typeof(JOB_INFO_2W), nameof(JOB_INFO_2W.Time), 148 },
        { typeof(JOB_INFO_2W), nameof(JOB_INFO_2W.PagesPrinted), 152 },

        { typeof(PRINTER_DEFAULTSW), nameof(PRINTER_DEFAULTSW.pDevMode), 8 },
        { typeof(PRINTER_DEFAULTSW), nameof(PRINTER_DEFAULTSW.DesiredAccess), 16 },

        // WCHAR sztPortName[63 + 1] = 128 bytes, then four DWORDs.
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.sztPortName), 0 },
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.dwVersion), 128 },
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.dwProtocol), 132 },
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.cbSize), 136 },
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.dwReserved), 140 },
        // WCHAR[48 + 1] = 98 bytes, then WCHAR[32 + 1] = 66 bytes: 144 + 98 = 242, 242 + 66 = 308 (already DWORD-aligned).
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.sztHostAddress), 144 },
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.sztSNMPCommunity), 242 },
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.dwDoubleSpool), 308 },
        // WCHAR[32 + 1] = 66 and WCHAR[15 + 1] = 32: 312 + 66 = 378, 378 + 32 = 410; BYTE[540] ends at 950.
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.sztQueue), 312 },
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.sztIPAddress), 378 },
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.Reserved), 410 },
        // 950 is padded to the next DWORD boundary.
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.dwPortNumber), 952 },
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.dwSNMPEnabled), 956 },
        { typeof(PORT_DATA_1), nameof(PORT_DATA_1.dwSNMPDevIndex), 960 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Structure_sizes_match_the_x64_header_layout(Type type, int expected)
    {
        Assert.True(Environment.Is64BitProcess, "The expected layouts are the x64 ones.");
        Assert.Equal(expected, Marshal.SizeOf(type));
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Structures_are_blittable_so_pointer_arithmetic_uses_the_native_size(Type type, int expected)
    {
        int managed = (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(type).Invoke(null, null)!;
        Assert.Equal(expected, managed);
    }

    [Theory]
    [MemberData(nameof(Offsets))]
    public void Field_offsets_match_the_x64_header_layout(Type type, string field, int expected)
    {
        Assert.True(Environment.Is64BitProcess, "The expected layouts are the x64 ones.");
        Assert.Equal(expected, (int)Marshal.OffsetOf(type, field));
    }

    [Fact]
    public void Port_data_capacities_include_the_terminating_null()
    {
        // tcpxcv.h: MAX_PORTNAME_LEN 63 +1, MAX_NETWORKNAME_LEN 48 +1, MAX_SNMP_COMMUNITY_STR_LEN 32 +1,
        // MAX_QUEUENAME_LEN 32 +1, MAX_IPADDR_STR_LEN 15 +1.
        Assert.Equal(64, MAX_PORTNAME_LEN);
        Assert.Equal(49, MAX_NETWORKNAME_LEN);
        Assert.Equal(33, MAX_SNMP_COMMUNITY_STR_LEN);
        Assert.Equal(33, MAX_QUEUENAME_LEN);
        Assert.Equal(16, MAX_IPADDR_STR_LEN);
    }
}
