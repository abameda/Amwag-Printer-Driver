using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Amwag.PrinterSetup.Windows.Interop;

/// <summary>
/// Configuration Manager (cfgmgr32.dll), read-only queries. These functions report failures through their CONFIGRET
/// result rather than the thread's last error. Values are from the Windows SDK 10.0.26100 headers cfgmgr32.h, cfg.h,
/// devpropdef.h, devpkey.h and usbprint.h.
/// </summary>
internal static partial class CfgMgr32
{
    private const string Library = "cfgmgr32.dll";

    internal const uint CR_SUCCESS = 0x00;
    internal const uint CR_NO_SUCH_DEVNODE = 0x0D;
    internal const uint CR_BUFFER_SMALL = 0x1A;
    internal const uint CR_NO_SUCH_VALUE = 0x25;
    internal const uint CR_NO_SUCH_REGISTRY_KEY = 0x2E;

    internal const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    internal const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    internal const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;
    internal const uint CM_GET_DEVICE_INTERFACE_LIST_ALL_DEVICES = 0x00000001;
    internal const uint RegDisposition_OpenExisting = 0x00000001;

    internal const uint DN_HAS_PROBLEM = 0x00000400;

    internal const uint DEVPROP_TYPE_STRING = 0x00000012;
    internal const uint DEVPROP_TYPE_STRING_LIST = 0x00002012;

    /// <summary>GUID_DEVINTERFACE_USBPRINT, registered by usbprint.sys on the USB device that owns a USBPRINT\ printer node.</summary>
    internal static readonly Guid GUID_DEVINTERFACE_USBPRINT = new(0x28d78fad, 0x5a12, 0x11d1, 0xae, 0x5b, 0x00, 0x00, 0xf8, 0x03, 0xa8, 0xc2);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct DEVPROPKEY(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    internal static class DevPropKeys
    {
        private static readonly Guid DeviceGeneral = new(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0);
        private static readonly Guid DeviceNode = new(0x4340a6c5, 0x93fa, 0x4706, 0x97, 0x2c, 0x7b, 0x64, 0x80, 0x08, 0xa5, 0xa7);
        private static readonly Guid DeviceDriver = new(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0x0c, 0x75, 0xd6);

        internal static readonly DEVPROPKEY DeviceDesc = new(DeviceGeneral, 2);
        internal static readonly DEVPROPKEY HardwareIds = new(DeviceGeneral, 3);
        internal static readonly DEVPROPKEY CompatibleIds = new(DeviceGeneral, 4);
        internal static readonly DEVPROPKEY Class = new(DeviceGeneral, 9);
        internal static readonly DEVPROPKEY Manufacturer = new(DeviceGeneral, 13);
        internal static readonly DEVPROPKEY FriendlyName = new(DeviceGeneral, 14);
        internal static readonly DEVPROPKEY Parent = new(DeviceNode, 8);
        internal static readonly DEVPROPKEY DriverVersion = new(DeviceDriver, 3);
        internal static readonly DEVPROPKEY DriverDesc = new(DeviceDriver, 4);
    }

    [DllImport(Library, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    /// <summary><paramref name="bufferLength"/> is in characters.</summary>
    [DllImport(Library, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint bufferLength, uint flags);

    [DllImport(Library, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    /// <summary><paramref name="bufferSize"/> is in bytes; a null buffer with size 0 returns CR_BUFFER_SMALL and the required size.</summary>
    [DllImport(Library, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern uint CM_Get_DevNode_PropertyW(uint devInst, in DEVPROPKEY propertyKey, out uint propertyType, [Out] byte[]? buffer, ref uint bufferSize, uint flags);

    [DllImport(Library, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    [DllImport(Library, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, in Guid interfaceClassGuid, string? deviceId, uint flags);

    /// <summary><paramref name="bufferLength"/> is in characters.</summary>
    [DllImport(Library, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern uint CM_Get_Device_Interface_ListW(in Guid interfaceClassGuid, string? deviceId, [Out] char[] buffer, uint bufferLength, uint flags);

    /// <summary>Opens the interface's "Device Parameters" key (DeviceClasses\{class}\{interface}\#\Device Parameters).</summary>
    [DllImport(Library, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern uint CM_Open_Device_Interface_KeyW(string deviceInterface, uint samDesired, uint disposition, out SafeRegistryHandle key, uint flags);

    [DllImport(Library, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern uint CM_MapCrToWin32Err(uint configRet, uint defaultError);
}
