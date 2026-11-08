using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Amwag.PrinterSetup.Windows.Interop;

internal static partial class Winspool
{
    /// <summary>A printer, print server or Xcv handle from OpenPrinter/AddPrinter, closed with ClosePrinter.</summary>
    public sealed class SafePrinterHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePrinterHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => ClosePrinter(handle);
    }

    /// <summary>An unmanaged buffer for the spooler's two-call APIs; a zero size holds no memory (the size query).</summary>
    public sealed class HGlobalBuffer : SafeHandleZeroOrMinusOneIsInvalid
    {
        public HGlobalBuffer(int size)
            : base(ownsHandle: true)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(size);
            Size = size;
            if (size > 0)
                SetHandle(Marshal.AllocHGlobal(size));
        }

        public int Size { get; }

        public IntPtr Pointer => handle;

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }
}
