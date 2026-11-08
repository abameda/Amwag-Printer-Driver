using System.Runtime.InteropServices;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Windows.Interop;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static Amwag.PrinterSetup.Windows.Interop.CfgMgr32;

namespace Amwag.PrinterSetup.Windows.Devices;

/// <summary>Read-only CfgMgr32 queries with the size-then-fetch retry loops and CONFIGRET error mapping.</summary>
internal static class DeviceTree
{
    // A device can arrive between the size and the fetch call, which then fails with CR_BUFFER_SMALL; retry a few times.
    private const int MaxFetchAttempts = 5;
    private const uint ERROR_GEN_FAILURE = 31;

    /// <summary>Instance IDs of every device, present or not, that an enumerator (e.g. USBPRINT) has created.</summary>
    internal static IReadOnlyList<string> GetDeviceIds(string enumerator)
    {
        for (int attempt = 0; attempt < MaxFetchAttempts; attempt++)
        {
            uint cr = CM_Get_Device_ID_List_SizeW(out uint length, enumerator, CM_GETIDLIST_FILTER_ENUMERATOR);
            if (cr != CR_SUCCESS)
                throw Failure("CM_Get_Device_ID_List_SizeW", cr, enumerator);

            var buffer = new char[Math.Max(length, 1)];
            cr = CM_Get_Device_ID_ListW(enumerator, buffer, (uint)buffer.Length, CM_GETIDLIST_FILTER_ENUMERATOR);
            if (cr == CR_SUCCESS)
                return SplitMultiSz(buffer);
            if (cr != CR_BUFFER_SMALL)
                throw Failure("CM_Get_Device_ID_ListW", cr, enumerator);
        }

        throw Failure("CM_Get_Device_ID_ListW", CR_BUFFER_SMALL, enumerator);
    }

    /// <summary>The device node and whether the device is present (currently configured in the device tree).</summary>
    internal static (uint DevInst, bool IsPresent) Locate(string instanceId)
    {
        uint cr = CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL);
        if (cr == CR_SUCCESS)
            return (devInst, true);
        if (cr != CR_NO_SUCH_DEVNODE)
            throw Failure("CM_Locate_DevNodeW", cr, instanceId);

        cr = CM_Locate_DevNodeW(out devInst, instanceId, CM_LOCATE_DEVNODE_PHANTOM);
        if (cr != CR_SUCCESS)
            throw Failure("CM_Locate_DevNodeW", cr, instanceId);
        return (devInst, false);
    }

    /// <summary>A DEVPROP_TYPE_STRING property, or null when it is absent, empty or of another type.</summary>
    internal static string? GetString(uint devInst, in DEVPROPKEY key)
    {
        byte[]? data = GetProperty(devInst, key, DEVPROP_TYPE_STRING);
        if (data is null)
            return null;

        ReadOnlySpan<char> text = MemoryMarshal.Cast<byte, char>(data);
        int end = text.IndexOf('\0');
        if (end >= 0)
            text = text[..end];
        return text.IsEmpty ? null : text.ToString();
    }

    /// <summary>A DEVPROP_TYPE_STRING_LIST (multi-sz) property; empty when absent or of another type.</summary>
    internal static IReadOnlyList<string> GetStringList(uint devInst, in DEVPROPKEY key)
    {
        byte[]? data = GetProperty(devInst, key, DEVPROP_TYPE_STRING_LIST);
        return data is null ? [] : SplitMultiSz(MemoryMarshal.Cast<byte, char>(data));
    }

    /// <summary>The CM_PROB_* code of a present device that reports DN_HAS_PROBLEM; null when it has none or just went away.</summary>
    internal static int? GetProblemCode(uint devInst)
    {
        uint cr = CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0);
        if (cr == CR_NO_SUCH_DEVNODE)
            return null;
        if (cr != CR_SUCCESS)
            throw Failure("CM_Get_DevNode_Status", cr, $"devnode {devInst}");
        return (status & DN_HAS_PROBLEM) != 0 ? (int)problem : null;
    }

    /// <summary>Symbolic link names of the device's interfaces of one class, including interfaces of non-present devices.</summary>
    internal static IReadOnlyList<string> GetInterfaces(in Guid interfaceClass, string instanceId)
    {
        for (int attempt = 0; attempt < MaxFetchAttempts; attempt++)
        {
            uint cr = CM_Get_Device_Interface_List_SizeW(out uint length, interfaceClass, instanceId, CM_GET_DEVICE_INTERFACE_LIST_ALL_DEVICES);
            if (cr != CR_SUCCESS)
                throw Failure("CM_Get_Device_Interface_List_SizeW", cr, instanceId);

            var buffer = new char[Math.Max(length, 1)];
            cr = CM_Get_Device_Interface_ListW(interfaceClass, instanceId, buffer, (uint)buffer.Length, CM_GET_DEVICE_INTERFACE_LIST_ALL_DEVICES);
            if (cr == CR_SUCCESS)
                return SplitMultiSz(buffer);
            if (cr != CR_BUFFER_SMALL)
                throw Failure("CM_Get_Device_Interface_ListW", cr, instanceId);
        }

        throw Failure("CM_Get_Device_Interface_ListW", CR_BUFFER_SMALL, instanceId);
    }

    /// <summary>The interface's "Device Parameters" registry key opened for reading values, or null when it does not exist.</summary>
    internal static RegistryKey? OpenInterfaceKey(string interfacePath)
    {
        uint cr = CM_Open_Device_Interface_KeyW(interfacePath, Advapi32.KEY_QUERY_VALUE, RegDisposition_OpenExisting, out SafeRegistryHandle handle, 0);
        if (cr == CR_SUCCESS)
            return RegistryKey.FromHandle(handle);

        handle.Dispose();
        if (cr == CR_NO_SUCH_REGISTRY_KEY)
            return null;
        throw Failure("CM_Open_Device_Interface_KeyW", cr, interfacePath);
    }

    /// <summary>Splits a REG_MULTI_SZ style buffer; stops at the empty string that terminates the list.</summary>
    internal static IReadOnlyList<string> SplitMultiSz(ReadOnlySpan<char> buffer)
    {
        var items = new List<string>();
        while (!buffer.IsEmpty)
        {
            int end = buffer.IndexOf('\0');
            ReadOnlySpan<char> item = end < 0 ? buffer : buffer[..end];
            if (item.IsEmpty)
                break;

            items.Add(item.ToString());
            if (end < 0)
                break;
            buffer = buffer[(end + 1)..];
        }

        return items;
    }

    private static byte[]? GetProperty(uint devInst, in DEVPROPKEY key, uint expectedType)
    {
        uint size = 0;
        uint cr = CM_Get_DevNode_PropertyW(devInst, key, out uint type, null, ref size, 0);
        for (int attempt = 0; attempt < MaxFetchAttempts; attempt++)
        {
            if (cr == CR_NO_SUCH_VALUE)
                return null;
            if (cr == CR_SUCCESS)
                return type == expectedType ? [] : null;
            if (cr != CR_BUFFER_SMALL)
                throw Failure("CM_Get_DevNode_PropertyW", cr, Describe(key));

            var buffer = new byte[size];
            cr = CM_Get_DevNode_PropertyW(devInst, key, out type, buffer, ref size, 0);
            if (cr == CR_SUCCESS)
                return type == expectedType ? buffer.AsSpan(0, (int)Math.Min(size, (uint)buffer.Length)).ToArray() : null;
        }

        throw Failure("CM_Get_DevNode_PropertyW", cr, Describe(key));
    }

    private static string Describe(in DEVPROPKEY key) => $"property {{{key.FormatId}}} {key.PropertyId}";

    private static PrinterSetupException Failure(string api, uint configRet, string subject)
    {
        int win32Error = (int)CM_MapCrToWin32Err(configRet, ERROR_GEN_FAILURE);
        return new PrinterSetupException(
            FindingCode.OperationFailed,
            $"{api} failed for {subject} (CONFIGRET 0x{configRet:X2}, Win32 error {win32Error}).",
            win32Error);
    }
}
