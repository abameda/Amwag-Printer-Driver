using System.Runtime.InteropServices;

// Every native library this layer calls (winspool, prntvpt, cfgmgr32, newdev, wintrust, advapi32 ...) ships with
// Windows. Resolving them from System32 only prevents DLL planting next to an executable run from a share or USB stick.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
