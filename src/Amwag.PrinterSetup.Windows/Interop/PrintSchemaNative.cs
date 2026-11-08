using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Amwag.PrinterSetup.Windows.Interop;

/// <summary>
/// prntvpt.dll Print Ticket API plus the COM memory streams it reads and writes (prntvpt.h, combaseapi.h, objidl.h of the
/// Windows SDK 10.0.26100). A provider handle is only valid on the thread that opened it (E_INVALIDARG elsewhere).
/// </summary>
internal static unsafe class PrintSchemaNative
{
    private const string PrntVpt = "prntvpt.dll";
    private const string Ole32 = "ole32.dll";
    private const string Kernel32 = "kernel32.dll";

    /// <summary>Print Schema version 1 (Windows Vista and later).</summary>
    public const uint PrintSchemaVersion = 1;

    /// <summary>EPrintTicketScope.kPTJobScope: job, document and page settings.</summary>
    public const int kPTJobScope = 2;

    public const int E_PRINTTICKET_FORMAT = unchecked((int)0x80040003);
    public const int E_PRINTCAPABILITIES_FORMAT = unchecked((int)0x80040004);

    public const uint COINIT_MULTITHREADED = 0x0;

    private const uint STREAM_SEEK_SET = 0;
    private const uint STREAM_SEEK_END = 2;

    // IStream vtable slots (objidl.h): IUnknown 0-2, ISequentialStream Read 3 / Write 4, IStream Seek 5.
    private const int SeekSlot = 5;

    [DllImport(PrntVpt, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int PTOpenProvider(string pszPrinterName, uint dwVersion, out SafePrintTicketProvider phProvider);

    [DllImport(PrntVpt, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int PTCloseProvider(IntPtr hProvider);

    /// <summary>Writes the ticket at the stream's current position; the caller rewinds before reading it.</summary>
    [DllImport(PrntVpt, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int PTConvertDevModeToPrintTicket(SafePrintTicketProvider hProvider, uint cbDevmode, void* pDevmode, int scope, IntPtr pPrintTicket);

    /// <summary><paramref name="pPrintTicket"/> may be zero; a non-null error BSTR must be freed by the caller.</summary>
    [DllImport(PrntVpt, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int PTGetPrintCapabilities(SafePrintTicketProvider hProvider, IntPtr pPrintTicket, IntPtr pCapabilities, out IntPtr pbstrErrorMessage);

    [DllImport(Ole32, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport(Ole32, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern void CoUninitialize();

    [DllImport(Ole32, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateStreamOnHGlobal(IntPtr hGlobal, bool fDeleteOnRelease, out IntPtr ppstm);

    [DllImport(Ole32, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetHGlobalFromStream(IntPtr pstm, out IntPtr phglobal);

    [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    /// <summary>An HPTPROVIDER, closed with PTCloseProvider (on the thread that opened it).</summary>
    public sealed class SafePrintTicketProvider : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePrintTicketProvider()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => PTCloseProvider(handle) >= 0;
    }

    /// <summary>
    /// An IStream over HGLOBAL memory (CreateStreamOnHGlobal), used through its raw vtable so no runtime COM interop
    /// (which trimming can remove) is needed.
    /// </summary>
    public sealed class HGlobalStream : IDisposable
    {
        private IntPtr _stream;

        private HGlobalStream(IntPtr stream) => _stream = stream;

        public IntPtr Pointer => _stream;

        public static HGlobalStream Create()
        {
            int hr = CreateStreamOnHGlobal(IntPtr.Zero, fDeleteOnRelease: true, out IntPtr stream);
            Marshal.ThrowExceptionForHR(hr);
            return new HGlobalStream(stream);
        }

        public void Rewind() => Seek(0, STREAM_SEEK_SET);

        /// <summary>Copies the stream's logical content (the HGLOBAL itself can be larger).</summary>
        public byte[] ToArray()
        {
            ulong length = Seek(0, STREAM_SEEK_END);
            Marshal.ThrowExceptionForHR(GetHGlobalFromStream(_stream, out IntPtr memory));

            byte* data = (byte*)GlobalLock(memory);
            if (data is null)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                return new ReadOnlySpan<byte>(data, checked((int)length)).ToArray();
            }
            finally
            {
                GlobalUnlock(memory);
            }
        }

        public void Dispose()
        {
            if (_stream == IntPtr.Zero)
                return;
            Marshal.Release(_stream);
            _stream = IntPtr.Zero;
        }

        private ulong Seek(long offset, uint origin)
        {
            var seek = (delegate* unmanaged[Stdcall]<IntPtr, long, uint, ulong*, int>)(*(IntPtr**)_stream)[SeekSlot];
            ulong position;
            Marshal.ThrowExceptionForHR(seek(_stream, offset, origin, &position));
            return position;
        }
    }
}
