# Win32 print spooler API reference for the AMWAG Printer Setup utility (x64, Unicode W functions)

## 0. How this was verified

**Sources**
- **Signatures, structs and constants** come from the Windows SDK 10.0.26100.0 headers on this machine: `um\winspool.h`, `um\wingdi.h`, `um\tcpxcv.h`, `um\winddiui.h`, `um\WinUser.h`, `um\winnt.h` and `shared\winerror.h`.
- **x64 offsets** were computed with 64-bit Python `ctypes`, using MSVC default packing and the header definitions. Every result matches a hand calculation.
  - Script: `C:\Users\abdel\AppData\Local\Temp\claude\C--Users-abdel-Desktop-Amwag-Printer\7737442f-5f1d-46cc-85b0-3b47c5e9c693\scratchpad\scripts-winapi-spooler\layouts_and_exports.py`
- **DLL bindings** were checked three ways:
  - by parsing the PE export tables of `C:\Windows\System32\winspool.drv` (198 names) and `spoolss.dll`;
  - by reading the import records in the SDK's `WinSpool.Lib` (script `...\scripts-winapi-spooler\implib_dll.py`).

**DLL result:** every function in this document is exported by **winspool.drv**, and `WinSpool.Lib` binds all of them to `WINSPOOL.DRV`. That includes `DeviceCapabilitiesW`, which is declared in wingdi.h.
- Microsoft Learn lists "Spoolss.dll" as the DLL for OpenPrinter2, AddForm, UploadPrinterDriverPackage, InstallPrinterDriverFromPackage, GetPrinterDriverPackagePath and DeletePrinterDriverPackage.
- That is wrong for application callers. On this machine `spoolss.dll` does not export `UploadPrinterDriverPackageW`, `InstallPrinterDriverFromPackageW`, `GetPrinterDriverPackagePathW`, `DeletePrinterDriverPackageW`, `DocumentPropertiesW`, `DeviceCapabilitiesW` or `IsValidDevmodeW`.

**How documentation is cited:** Microsoft text is paraphrased and the Learn or [MS-RPRN] URL is given for each fact.
- Each page was downloaded unchanged so it can be checked word for word: `C:\Users\abdel\AppData\Local\Temp\claude\C--Users-abdel-Desktop-Amwag-Printer\7737442f-5f1d-46cc-85b0-3b47c5e9c693\scratchpad\research\winapi-spooler\dl-<name>\page.html`
- A plain-text copy of each page is in `...\research\winapi-spooler\_text\<name>.txt`.

**"Observed" results** come from read-only probes on this machine.
- Environment: Windows 11 Home 10.0.26300, run as a non-elevated administrator (medium integrity, `BUILTIN\Administrators` is deny-only).
- Calls made: OpenPrinterW, OpenPrinter2W, EnumFormsW, GetFormW, GetPrinterW (levels 2/3/8/9), DocumentPropertiesW with fMode=0, DeviceCapabilitiesW and ClosePrinter.
- Nothing was added, set or deleted.
- Scripts: `...\scripts-winapi-spooler\readonly_probe.py` and `readonly_probe2.py` through `readonly_probe4.py`.
- The only installed queues are Microsoft Print to PDF and OneNote. Both use v4 drivers, so no Unidrv or PScript queue could be observed.

**x64 type sizes (LLP64)**

| Type | Size (bytes) |
|---|---|
| BOOL, DWORD, LONG, ULONG, UINT, HRESULT, ACCESS_MASK | 4 |
| Pointers, HANDLE, HWND, `size_t` | 8 |
| WORD, `short`, LANGID, WCHAR | 2 |
| SIZEL (cx, cy LONG) | 8 |
| RECTL (left, top, right, bottom LONG) | 16 |
| POINTL and POINT (two LONG) | 8 |
| POINTS (two SHORT) | 4 |
| FILETIME | 8 (alignment 4) |
| DWORDLONG | 8 |
| SYSTEMTIME (8 WORDs) | 16 (alignment 2) |

**Common rules for every function below**
- **Buffer sizing:** for every Enum*/Get* call that takes `cbBuf`/`pcbNeeded`, first call with `cbBuf=0`. The call fails with `ERROR_INSUFFICIENT_BUFFER` (122, 0x7A) and `*pcbNeeded` holds the required byte count.
  - Documented on the EnumPrinterDrivers, GetPrinterDriver, EnumPorts, EnumJobs and GetJob pages.
  - Observed for EnumFormsW (12424 bytes for 180 forms), GetPrinterW levels 2, 8 and 9, and GetFormW.
- **Keep the whole buffer:** the returned buffer holds the fixed structs followed by the strings and DEVMODEs their pointers refer to, so it must stay alive while the structs are used.
- **Blocking and threading:**
  - Every page carries a note that the call is blocking and synchronous.
  - The OpenPrinter, OpenPrinter2, EnumPrinters and AddPrinter pages say not to call them from DllMain.
  - Handles from OpenPrinter and AddPrinter are documented as not thread-safe.

---

## 1. OpenPrinterW, OpenPrinter2W, ClosePrinter

**Signatures** (DLL winspool.drv)
- `BOOL OpenPrinterW(_In_opt_ LPWSTR pPrinterName, _Out_ LPHANDLE phPrinter, _In_opt_ LPPRINTER_DEFAULTSW pDefault);`
- `BOOL OpenPrinter2W(_In_opt_ LPCWSTR pPrinterName, _Out_ LPHANDLE phPrinter, _In_opt_ PPRINTER_DEFAULTSW pDefault, _In_opt_ PPRINTER_OPTIONSW pOptions);` Requires Vista or later.
- `BOOL ClosePrinter(_In_ HANDLE hPrinter);`

**Docs:** https://learn.microsoft.com/en-us/windows/win32/printdocs/openprinter Â· .../openprinter2 Â· .../closeprinter Â· .../printer-defaults Â· .../printer-options Â· .../printer-option-flags

**Return values**
- Non-zero means success. Zero means failure; call GetLastError.
- Insufficient rights for the requested access: `ERROR_ACCESS_DENIED` (5) (OpenPrinter page).
- Name that is not well-formed or does not exist: `ERROR_INVALID_PRINTER_NAME` (1801, 0x709). This is normative in [MS-RPRN] 3.1.4.1.5 (https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/3f2d41eb-30e1-47e1-93bb-534ea50cf8eb). Observed: OpenPrinterW on a non-existent queue returned 1801.
- After ClosePrinter returns, the handle is invalid whether or not the call succeeded.

**pPrinterName**
- Accepted forms: a printer name; `"PrinterName,Job nnnn"`; `"Server,XcvMonitor MonitorName"`; `"Server,XcvPort PortName"`.
- **NULL means the local print server.** For OpenPrinter2W this is documented from Vista on. Observed: OpenPrinterW(NULL) and OpenPrinter2W(NULL) both succeed.
- Local Xcv names use an empty server part: `",XcvMonitor Standard TCP/IP Port"` and `",XcvPort <PortName>"`. A remote name is `"<Server>\\,XcvMonitor ..."` (https://learn.microsoft.com/en-us/windows-hardware/drivers/print/tcpmon-xcv-interface).

**pDefault**
- If pDefault is NULL, a printer is opened with `PRINTER_ACCESS_USE` (OpenPrinter page).
- For a server object, [MS-RPRN] RpcOpenPrinterEx says generic read access is assumed, which maps to SERVER_READ (https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/0e81ce18-72b1-46c3-8584-a205393b04ff).
- To open a server: set `pDatatype` and `pDevMode` to NULL and `DesiredAccess` to a server mask.

**OpenPrinter2W caveats** (OpenPrinter2 page)
- `OpenPrinter2A` is not implemented and returns `ERROR_NOT_SUPPORTED`.
- For a local printer, `dwFlags` is ignored except `PRINTER_OPTION_CLIENT_CHANGE`, which makes the call fail with `ERROR_ACCESS_DENIED`. Locally it therefore gives no benefit over OpenPrinter.
- For remote printers, OpenPrinter goes through a spooler cache. OpenPrinter2W with `PRINTER_OPTION_NO_CACHE` bypasses the cache but blocks on the network.

**PRINTER_DEFAULTSW** (24 bytes, alignment 8)

| Offset | Field | Type |
|---|---|---|
| +0 | pDatatype | LPWSTR |
| +8 | pDevMode | LPDEVMODEW |
| +16 | DesiredAccess | ACCESS_MASK (DWORD) |
| +20 | (padding, 4) | |

**PRINTER_OPTIONSW** (8 bytes)

| Offset | Field | Notes |
|---|---|---|
| +0 | cbSize | UINT, set to 8 |
| +4 | dwFlags | DWORD |

**PRINTER_OPTION_FLAGS** (winspool.h)
- `PRINTER_OPTION_NO_CACHE` 0x1
- `PRINTER_OPTION_CACHE` 0x2
- `PRINTER_OPTION_CLIENT_CHANGE` 0x4
- `PRINTER_OPTION_NO_CLIENT_DATA` 0x8. This value is not on the Learn page, so its meaning is **UNVERIFIED**.

**Access masks** (winspool.h and winnt.h, hex values cross-checked against [MS-RPRN] 2.2.3.1, https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/945c4df5-4969-49c5-b1ce-59c1b3f35024)

| Name | Value |
|---|---|
| SERVER_ACCESS_ADMINISTER | 0x00000001 |
| SERVER_ACCESS_ENUMERATE | 0x00000002 |
| PRINTER_ACCESS_ADMINISTER | 0x00000004 |
| PRINTER_ACCESS_USE | 0x00000008 |
| JOB_ACCESS_ADMINISTER / JOB_ACCESS_READ | 0x10 / 0x20 |
| PRINTER_ACCESS_MANAGE_LIMITED (Windows 8.1 and later) | 0x00000040 |
| STANDARD_RIGHTS_REQUIRED | 0x000F0000 |
| STANDARD_RIGHTS_READ / WRITE / EXECUTE (= READ_CONTROL) | 0x00020000 |
| **SERVER_ALL_ACCESS** | **0x000F0003** |
| SERVER_READ / SERVER_EXECUTE | 0x00020002 |
| SERVER_WRITE | 0x00020003 |
| **PRINTER_ALL_ACCESS** | **0x000F000C** |
| **PRINTER_READ** (= PRINTER_WRITE = PRINTER_EXECUTE) | **0x00020008** |
| JOB_ALL_ACCESS | 0x000F0030 |
| DELETE | 0x00010000 |
| WRITE_DAC | 0x00040000 |
| WRITE_OWNER | 0x00080000 |
| SYNCHRONIZE | 0x00100000 |

- PRINTER_ALL_ACCESS deliberately excludes SYNCHRONIZE (OpenPrinter page). [MS-RPRN] says SYNCHRONIZE must not be sent.
- In [MS-RPRN], GENERIC_READ and GENERIC_ALL map to SERVER_READ / PRINTER_READ and to SERVER_ALL_ACCESS / PRINTER_ALL_ACCESS.

**Observed access on this machine (non-elevated)**
- `OpenPrinterW(NULL, SERVER_ACCESS_ADMINISTER)` succeeded.
- `OpenPrinterW(",XcvMonitor Standard TCP/IP Port", SERVER_ACCESS_ADMINISTER)` succeeded.
- `OpenPrinterW("Microsoft Print to PDF", PRINTER_ALL_ACCESS)` failed with error 5.
- Why: the local print server's DACL, read with GetPrinter level 3 and converted to SDDL, contains these ACEs:
  - `(A;;CCDCSDRCWDWO;;;IU)`: INTERACTIVE users get SERVER_ALL_ACCESS.
  - `(A;;CCDCSDRCWDWO;;;BA)`: the same for Administrators.
  - `(A;;DCRC;;;WD)`: Everyone gets SERVER_READ.
  - `(D;;CC;;;BG)`: Guests are denied SERVER_ACCESS_ADMINISTER.
- The PDF queue grants Everyone `SWRC` (= PRINTER_READ). Its admin rights are granted only to BA, and BA is deny-only in a non-elevated token.
- These ACLs are specific to this machine and its policies.

---

## 2. EnumPrintersW, GetPrinterW, SetPrinterW

**Signatures** (winspool.drv)
- `BOOL EnumPrintersW(DWORD Flags, _In_opt_ LPWSTR Name, DWORD Level, LPBYTE pPrinterEnum, DWORD cbBuf, LPDWORD pcbNeeded, LPDWORD pcReturned);`
- `BOOL GetPrinterW(HANDLE hPrinter, DWORD Level, LPBYTE pPrinter, DWORD cbBuf, LPDWORD pcbNeeded);`
- `BOOL SetPrinterW(HANDLE hPrinter, DWORD Level, LPBYTE pPrinter, DWORD Command);`

**Docs:** .../printdocs/enumprinters Â· .../getprinter Â· .../setprinter Â· .../printer-info-2 Â· .../printer-info-4 Â· .../printer-info-8 Â· .../printer-info-9 Â· https://learn.microsoft.com/en-us/windows/win32/printdocs/per-user-devmode

**PRINTER_ENUM_\* flags**

| Flag | Value |
|---|---|
| DEFAULT | 0x1 |
| **LOCAL** | **0x2** |
| **CONNECTIONS** (same value as FAVORITE) | **0x4** |
| NAME | 0x8 |
| REMOTE | 0x10 |
| SHARED | 0x20 |
| NETWORK | 0x40 |
| EXPAND | 0x4000 |
| CONTAINER | 0x8000 |
| ICONMASK | 0x00FF0000 |
| HIDE | 0x01000000 |
| CATEGORY_ALL | 0x02000000 |
| CATEGORY_3D | 0x04000000 |

**EnumPrinters behaviour** (EnumPrinters page)
- Valid levels are 1, 2, 4 and 5.
- **Level 4:**
  - Only LOCAL and CONNECTIONS are allowed, and Name must be NULL.
  - It reads the registry and returns quickly; on Vista and later it comes from a cache.
- **Level 2:**
  - It opens every remote connection, so it can stall until RPC times out.
  - `pSecurityDescriptor` is always NULL in the results.
- `Name = NULL` with `LOCAL | CONNECTIONS` returns the locally installed printers plus the user's network connections.
- 3D printers are excluded unless a CATEGORY flag is passed.
- Connections are per user: the page describes them as the printers the user previously connected to.

**PRINTER_INFO_2W** (136 bytes, alignment 8)

| Offset | Field | Offset | Field |
|---|---|---|---|
| +0x00 | pServerName | +0x60 | pSecurityDescriptor |
| +0x08 | pPrinterName | +0x68 | Attributes (DWORD) |
| +0x10 | pShareName | +0x6C | Priority |
| +0x18 | pPortName | +0x70 | DefaultPriority |
| +0x20 | pDriverName | +0x74 | StartTime (minutes after 00:00 GMT) |
| +0x28 | pComment | +0x78 | UntilTime |
| +0x30 | pLocation | +0x7C | Status |
| +0x38 | pDevMode | +0x80 | cJobs |
| +0x40 | pSepFile | +0x84 | AveragePPM |
| +0x48 | pPrintProcessor | | |
| +0x50 | pDatatype | | |
| +0x58 | pParameters | | |

**Other levels**
- **PRINTER_INFO_4W** (24 bytes): +0 pPrinterName, +8 pServerName, +16 Attributes, +20 padding.
- **PRINTER_INFO_8W** and **PRINTER_INFO_9W** (8 bytes each): +0 pDevMode.

**What pDevMode means at each level**

| Level | Meaning | Source |
|---|---|---|
| 8 | Global default printer settings, set by the printer's administrator | GetPrinter page, PRINTER_INFO_8 page |
| 9 | Per-user default settings, stored in the user's registry | PRINTER_INFO_9 page |
| 2 | Only described as "default printer data" | PRINTER_INFO_2 page |

- [MS-RPRN] PRINTER_CONTAINER says level 9 is never used remotely and the server rejects it with `ERROR_NOT_SUPPORTED`, so it is handled on the client per user (https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/ad7d4d4e-0feb-45e0-93ed-b3b2b9a313d1).
- **Level 2 with a per-user DEVMODE present: UNVERIFIED.** No documentation says whether GetPrinter level 2 returns the per-user DEVMODE when one exists.
- Observed on Microsoft Print to PDF, which has no per-user DEVMODE:
  - Levels 2 and 8 returned the same public DEVMODE: Letter, dmFields 0x2D03, dmDriverExtra 5208.
  - Level 9 returned `pDevMode = NULL` with pcbNeeded = 8.
- The GetPrinter page says a NULL pDevMode at levels 2, 8 or 9 means the printer is unusable until its driver is reinstalled. The level-9 observation shows a NULL there only means no per-user DEVMODE exists.

**Setting the DEVMODE: the documentation conflicts**
- The SetPrinter page treats changing the PRINTER_INFO_2 DEVMODE as setting the printer's global defaults, and requires validating it with DocumentProperties first.
- The Per-User DEVMODE page says that SetPrinter with PRINTER_INFO_2 *or* PRINTER_INFO_9 sets the **per-user** DEVMODE, and that SetPrinter with PRINTER_INFO_8 resets the per-user DEVMODE to the global one.
- **Recommendation:** use level 8 for the queue-wide default and level 9 for the current user, and do not rely on level 2 for either.
- Whether level 8 overrides *other* users' stored per-user DEVMODEs is **UNVERIFIED**.

**SetPrinter rules** (SetPrinter page)
- Valid levels are 0 and 2 through 9.
- If Command is non-zero, Level must be 0. If Level is non-zero, Command must be 0.
- Commands:
  - `PRINTER_CONTROL_PAUSE` 1, `PRINTER_CONTROL_RESUME` 2, `PRINTER_CONTROL_PURGE` 3.
  - `PRINTER_CONTROL_SET_STATUS` 4: pPrinter points to a DWORD status. PAUSED and PENDING_DELETION cannot be set this way.
- At level 2 it ignores pServerName, AveragePPM, Status and cJobs.
- Pass `pSecurityDescriptor = NULL` to leave security unchanged. Otherwise you need WRITE_OWNER, WRITE_DAC or ACCESS_SYSTEM_SECURITY for the parts of the descriptor you change.
- It cannot change the default printer.
- Recommended sequence: GetPrinter level 2, modify, SetPrinter.
- Level 7 can fail with ERROR_IO_PENDING (997, publish continues in the background) or ERROR_FILE_NOT_FOUND (update of an unpublished printer).
- Firewall: when called by a machine administrator, SetPrinter enables the File and Print Sharing exception. A non-administrator's call fails if the exception is not already enabled.

**Access required**
- **GetPrinter:** Windows checks PRINTER_ACCESS_USE on printer handles and SERVER_ACCESS_ENUMERATE on server handles ([MS-RPRN] 3.1.4.2.6, note `<278>`, https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/e81cbc09-ab05-4a32-ae4a-8ec57b436c43). Reading the security descriptor needs READ_CONTROL, and the SACL needs ACCESS_SYSTEM_SECURITY.
- **SetPrinter, any level including 2, 8 and the level-0 commands:** Windows checks PRINTER_ACCESS_ADMINISTER on printer handles and SERVER_ACCESS_ADMINISTER on server handles ([MS-RPRN] 3.1.4.2.5, note `<274>`). KB 140285 opens the printer with PRINTER_ALL_ACCESS before calling SetPrinter (https://learn.microsoft.com/en-us/troubleshoot/windows/win32/modify-printer-settings-setprinter-api).
  - **[OBS 2026-11-04]** Windows 11 build 26300, TA KXv4 queues, elevated: SetPrinter level 8 on a handle opened with
    PRINTER_ACCESS_ADMINISTER alone failed with error 5 (access denied). The same call on a handle opened with
    PRINTER_ALL_ACCESS succeeded, as did level 2 with PRINTER_ALL_ACCESS. The tool therefore opens with
    PRINTER_ALL_ACCESS for every SetPrinter except level 9 (kx-custom-paper-size.md, section 6).
- **SetPrinter level 9:** not covered by MS-RPRN.
  - **[OBS 2026-11-04]** PRINTER_ACCESS_USE is enough: it worked for the signed-in user without elevation on the same KXv4
    queues.
- **PRINTER_ACCESS_MANAGE_LIMITED:** documented for SetPrinter and SetPrinterData style tasks; which levels it allows is **UNVERIFIED**.

---

## 3. AddPrinterW (level 2)

**Signature:** `HANDLE AddPrinterW(_In_opt_ LPWSTR pName /*NULL = local*/, DWORD Level, LPBYTE pPrinter);`
- The Learn syntax block shows `LPTSTR *pName`; that is a documentation typo.
- Docs: .../printdocs/addprinter

**Return value:** a new handle with PRINTER_ALL_ACCESS, or NULL on failure (then call GetLastError). The handle is not thread-safe.

**Requirements** (AddPrinter page)
- Level must be 2.
  - The header allows 1â€“2. Under [MS-RPRN], level 1 only updates the server's list of known printers and returns `ERROR_PRINTER_ALREADY_EXISTS` by design.
- `pPrinterName`, `pPortName`, `pDriverName` and `pPrintProcessor` must be non-NULL.
- Status, cJobs and AveragePPM must not be set; [MS-RPRN] says they must be zero.
- Members you may set: Attributes, pPrintProcessor, DefaultPriority, Priority, pComment, pSecurityDescriptor (NULL gives the default descriptor), pDatatype, pSepFile, pDevMode, pShareName, pLocation, StartTime, pParameters and UntilTime.
- **The caller needs SeLoadDriverPrivilege and SERVER_ACCESS_ADMINISTER on the server.**
- If a printer with the same name exists, AddPrinter fails. The exception is a printer that is pending deletion: it is reconfigured as if by SetPrinter.
- After creating the printer, the docs say to call PrinterProperties, which shows UI.
- AddPrinter enables the File and Print Sharing firewall exception.

**Related facts**
- `pDatatype` is optional. If supplied, the print processor must support it.
- Print processor name: DRIVER_INFO_8.pszPrintProcessor gives it; the Learn example value is "WinPrint".
- Observed Attributes on an existing local queue: 0x240, which is LOCAL plus DO_COMPLETE_FIRST.

**Errors** ([MS-RPRN] RpcAddPrinter https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/bd0bb8d5-26fd-4705-81f5-614d32b9f38f, RpcAddPrinterEx .../3af45717-308f-4b4f-8428-430d12856110, and PRINTER_CONTAINER parameters .../07b78bd9-247c-4c58-981e-9a77538238ae; values from winerror.h)

| Condition | Code | Decimal | Hex |
|---|---|---|---|
| Driver not installed | ERROR_UNKNOWN_PRINTER_DRIVER | 1797 | 0x705 |
| Port missing | ERROR_UNKNOWN_PORT | 1796 | 0x704 |
| Print processor missing | ERROR_UNKNOWN_PRINTPROCESSOR | 1798 | 0x706 |
| Name already used | ERROR_PRINTER_ALREADY_EXISTS | 1802 | 0x70A |
| Name not well-formed | ERROR_INVALID_PRINTER_NAME | 1801 | 0x709 |
| Unsupported datatype | ERROR_INVALID_DATATYPE | 1804 | 0x70C |
| Bad separator file | ERROR_INVALID_SEPARATOR_FILE | 1799 | 0x707 |
| Priority out of range | ERROR_INVALID_PRIORITY | 1800 | 0x708 |
| Shared but the driver cannot be shared | ERROR_PRINTER_NOT_SHAREABLE | 3022 | 0xBCE |
| Bad level | ERROR_INVALID_LEVEL | 124 | 0x7C |
| Any other validation failure | ERROR_INVALID_PARAMETER | 87 | 0x57 |
| Missing rights | ERROR_ACCESS_DENIED | 5 | 0x5 |

- Product-behavior notes `<269>` and `<289>`: Windows checks that the caller is allowed to install a printer, typically SERVER_ACCESS_ADMINISTER.
- Note `<291>`: for RpcAddPrinterEx, Windows fails unless the driver, port and print processor are all installed.

---

## 4. Forms: EnumFormsW, GetFormW, AddFormW, SetFormW, DeleteFormW

**Signatures** (winspool.drv)
- `BOOL AddFormW(HANDLE hPrinter, DWORD Level /*1|2*/, LPBYTE pForm);`
- `BOOL DeleteFormW(HANDLE hPrinter, LPWSTR pFormName);`
- `BOOL GetFormW(HANDLE hPrinter, LPWSTR pFormName, DWORD Level, LPBYTE pForm, DWORD cbBuf, LPDWORD pcbNeeded);`
- `BOOL SetFormW(HANDLE hPrinter, LPWSTR pFormName, DWORD Level, LPBYTE pForm);` The Learn syntax block shows `LPTSTR pForm`; the header has LPBYTE.
- `BOOL EnumFormsW(HANDLE hPrinter, DWORD Level, LPBYTE pForm, DWORD cbBuf, LPDWORD pcbNeeded, LPDWORD pcReturned);`
- Docs: .../printdocs/enumforms Â· getform Â· addform Â· setform Â· deleteform Â· form-info-1 Â· form-info-2

**FORM_INFO_1W** (40 bytes)

| Offset | Field |
|---|---|
| +0 | Flags (DWORD) |
| +4 | (padding, 4) |
| +8 | pName (LPWSTR) |
| +16 | Size.cx |
| +20 | Size.cy |
| +24 | ImageableArea.left |
| +28 | ImageableArea.top |
| +32 | ImageableArea.right |
| +36 | ImageableArea.bottom |

**FORM_INFO_2W** (88 bytes)

| Offset | Field | Notes |
|---|---|---|
| +0 | Flags | |
| +8 | pName | |
| +16 | Size | SIZEL |
| +24 | ImageableArea | RECTL |
| **+40** | **pKeyword** | **LPCSTR: an ANSI string even in the W struct** (winspool.h) |
| +48 | StringType | |
| +56 | pMuiDll | |
| +64 | dwResourceId | |
| +72 | pDisplayName | |
| +80 | wLangId | WORD, followed by 6 bytes of padding |

- `MAX_FORM_KEYWORD_LENGTH` is 64 (written as 63+1 in the header).

**Constants**
- `FORM_USER` 0x0, `FORM_BUILTIN` 0x1, `FORM_PRINTER` 0x2.
- `STRING_NONE` 0x1, `STRING_MUIDLL` 0x2, `STRING_LANGPAIR` 0x4.
- FORM_INFO_2 rules: only one Flags value may be set, and FORM_BUILTIN must not be passed to AddForm or SetForm because built-in forms cannot be modified. Pointer and ID requirements apply for each StringType (FORM_INFO_2 page).

**Units and geometry**
- Size and ImageableArea are in **thousandths of a millimetre**.
- ImageableArea is the printable rectangle in absolute coordinates on the form: left/top is the upper-left corner, right/bottom is the lower-right corner ([MS-RPRN] 2.2.2.5.1, https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/95e918de-fbdb-4d0a-ad4c-d7005ab33305).
  - The Win32 FORM_INFO_1 page's description of ImageableArea is a copy of the Size text; use the MS-RPRN definition.
- Observed: built-in forms have ImageableArea = (0, 0, cx, cy).
- **AMWAG Ticket** (arithmetic):
  - 3.24 in Ã— 25.4 = 82.296 mm, so `Size.cx = 82296`.
  - 6.70 in Ã— 25.4 = 170.18 mm, so `Size.cy = 170180`.
  - A full-page imageable area is `{0, 0, 82296, 170180}`.
  - In DEVMODE tenths of a millimetre this is 822.96 by 1701.8, which would round to 823 by 1702. How a driver rounds it is **UNVERIFIED**.

**Name length**
- The Win32 FORM_INFO_1 and FORM_INFO_2 pages limit form names to 31 characters.
- [MS-RPRN] note `<214>` says Windows allows up to 260 characters including the terminator.
- DEVMODE.dmFormName holds 32 WCHARs (CCHFORMNAME) including the terminator, so keep names at 31 characters or fewer. "AMWAG Ticket" is 12.

**Errors**
- **AddForm on an existing name fails with `ERROR_FILE_EXISTS` (80, 0x50).** It is not ERROR_ALREADY_EXISTS (183) ([MS-RPRN] RpcAddForm, https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/d6eb5430-4a27-4e5a-b698-8140a12fa889).
- AddForm at level 2 also fails if the same pKeyword already exists (AddForm page).
- SetForm, DeleteForm and GetForm on an unknown name fail with `ERROR_INVALID_FORM_NAME` (1902, 0x76E) ([MS-RPRN] RpcSetForm .../c17cbf66-01a2-4ac0-b57b-1d2d8d1f73de, RpcDeleteForm .../1b25a567-2008-4ca1-ac93-feaeaec7ec54, RpcGetForm .../be369d1d-1ede-49b1-81c2-09c69411c25d). Observed for GetFormW on a printer handle.
- A bad level gives ERROR_INVALID_LEVEL; other invalid containers give ERROR_INVALID_PARAMETER.
- `ERROR_INVALID_FORM_SIZE` (1903, 0x76F) exists in winerror.h, but whether AddForm returns it is **UNVERIFIED**.

**Other behaviour**
- DeleteForm only removes forms that were added with AddForm.
- SetForm with FORM_INFO_2 can be called repeatedly to add display-name/language pairs; the most recent Size and ImageableArea apply to every language.
- Remote callers using level 2 always get StringType STRING_LANGPAIR and cannot use STRING_MUIDLL.

**Access**
- AddForm, DeleteForm and SetForm: Windows checks that the **user has SERVER_ACCESS_ADMINISTER** (product-behavior notes `<353>`, `<354>`, `<355>`).
- EnumForms: the server performs no access check.
- GetForm: assumes the handle needs no further check.
- winspool.h defines `SPLREG_ALLOW_USER_MANAGEFORMS` = "AllowUserManageForms". It is not documented on Learn, so its meaning is **UNVERIFIED**.

**Server handle or printer handle**
- [MS-RPRN] allows either handle type for the form methods.
- **Observed on this build:**
  - EnumFormsW returns the same 180 forms (12424 bytes) through a server handle and through a printer handle.
  - **GetFormW on any local server handle fails with `ERROR_INVALID_HANDLE` (6).** This held for OpenPrinterW(NULL) with SERVER_ACCESS_ENUMERATE, with SERVER_READ, and with NULL defaults, and for OpenPrinter2W(NULL).
  - The same GetFormW call succeeds on a printer handle.
- Whether AddForm, SetForm and DeleteForm accept a local server handle is **UNVERIFIED**; it could not be tested without making changes. Which access the handle itself needs is also **UNVERIFIED**.

**Forms are server-wide**
- [MS-RPRN] 3.1.1 defines a server-level "List of Form Objects" that persists across restarts (https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/1bd61448-db4b-4284-a4b1-8346732402d4).
- The product-behavior appendix places that list at `HKLM\SYSTEM\CurrentControlSet\Control\Print\Forms`.
- Forms are therefore not per printer and not per user. The FORM_PRINTER flag marks forms that a driver added.
- **Observed:** 180 forms in total.
  - 118 are FORM_BUILTIN (positions 1â€“118), in DMPAPER order: position 1 is Letter and position 9 is A4.
  - 62 are FORM_PRINTER (positions 119â€“180).

---

## 5. DocumentPropertiesW

**Signature:** `LONG DocumentPropertiesW(_In_opt_ HWND hWnd, _In_ HANDLE hPrinter, _In_ LPWSTR pDeviceName, _Out_opt_ PDEVMODEW pDevModeOutput, _In_opt_ PDEVMODEW pDevModeInput, DWORD fMode);`
- The header annotates that when fMode is 0 the return value is at least sizeof(DEVMODE).
- Docs: .../printdocs/documentproperties

**fMode values** (wingdi.h)

| Name | Value | Alias |
|---|---|---|
| DM_UPDATE | 1 | DM_OUT_DEFAULT |
| DM_COPY | 2 | DM_OUT_BUFFER |
| DM_PROMPT | 4 | DM_IN_PROMPT |
| DM_MODIFY | 8 | DM_IN_BUFFER |

Driver-side values from winddiui.h, not for applications: DM_ADVANCED 0x10, DM_NOPERMISSION 0x20, DM_USER_DEFAULT 0x40, DM_INVALIDATE_DRIVER_CACHE 0x20000000, DM_PROMPT_NON_MODAL 0x40000000.

**Semantics** (DocumentProperties page)
- `fMode = 0`: returns the required buffer size, which includes the driver's private data.
- `DM_IN_BUFFER`: merges `pDevModeInput` into the driver's current settings, for the members flagged in dmFields only; the input wins on conflict. Without this flag the printer's default DEVMODE is used as the input.
- `DM_OUT_BUFFER`: writes the full current settings, including private data, into a caller-allocated `pDevModeOutput`.
- `DM_IN_PROMPT`: shows the driver's dialog.
- To change settings, pass at least one input flag and one output flag.
- `pDeviceName` can be obtained from GetPrinter.

**Return value:** IDOK (1) on success without UI; IDOK or IDCANCEL (2) when the dialog was shown; **less than 0 on failure** (IDOK and IDCANCEL are defined in WinUser.h).

**DM_UPDATE**
- Not documented on the DocumentProperties page.
- On the driver side, DOCUMENTPROPERTYHEADER marks DM_OUT_DEFAULT/DM_UPDATE as unused (https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/winddiui/ns-winddiui-_documentpropertyheader).
- The ExtDeviceMode page says DM_UPDATE shows UI and then writes per-user settings to the user's registry hive (https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/winspool/nf-winspool-extdevicemode).
- Its effect when passed to DocumentProperties is **UNVERIFIED**; do not use it.

**Documented procedure to change a DEVMODE**
1. Call with `fMode = 0` to get the size.
2. Allocate that many bytes.
3. Call with `DM_OUT_BUFFER` to fill the buffer.
4. Change the public members and set the matching dmFields bits.
5. Call again with the same buffer as both input and output and `DM_IN_BUFFER | DM_OUT_BUFFER`.

- The SetPrinter page requires this validation before SetPrinter changes a default DEVMODE.
- KB 167345 adds that a dmFields bit only means a member is used; check which values are supported with DeviceCapabilities (https://learn.microsoft.com/en-us/troubleshoot/windows/win32/modify-printer-settings-documentproperties).
- Whether DM_OUT_BUFFER without an input returns the per-user or the global default is **UNVERIFIED**. The driver-side DrvDeviceCapabilities page describes a NULL DEVMODE as the queue's current default, giving the user default as an example.
- **Observed:** DocumentPropertiesW(fMode=0) returned 5428 for Microsoft Print to PDF, which equals dmSize 220 plus dmDriverExtra 5208.

**Related:** `BOOL IsValidDevmodeW(_In_opt_ PDEVMODEW pDevmode, size_t DevmodeSize);` (`size_t` is 8 bytes on x64)
- Checks only the public part of a DEVMODE and fixes minor errors.
- Returns FALSE for serious structural problems or a NULL pointer.
- Works in a least-privileged user account context.
- Docs: .../printdocs/isvaliddevmode

---

## 6. DEVMODEW (wingdi.h)

**Layout:** 220 bytes, alignment 4. It contains no pointers, so x86 and x64 are identical. Driver-private data starts at offset `dmSize` and is `dmDriverExtra` bytes long.

| Offset | Field | Offset | Field |
|---|---|---|---|
| 0 | dmDeviceName WCHAR[32] | 100 | dmCollate |
| 64 | dmSpecVersion WORD | 102 | dmFormName WCHAR[32] |
| 66 | dmDriverVersion WORD | 166 | dmLogPixels WORD |
| 68 | dmSize WORD | 168 | dmBitsPerPel |
| 70 | dmDriverExtra WORD | 172 | dmPelsWidth |
| 72 | dmFields DWORD | 176 | dmPelsHeight |
| 76 | dmOrientation short | 180 | dmDisplayFlags / dmNup (union) |
| 78 | dmPaperSize | 184 | dmDisplayFrequency |
| 80 | dmPaperLength | 188 | dmICMMethod |
| 82 | dmPaperWidth | 192 | dmICMIntent |
| 84 | dmScale | 196 | dmMediaType |
| 86 | dmCopies | 200 | dmDitherType |
| 88 | dmDefaultSource | 204 | dmReserved1 |
| 90 | dmPrintQuality | 208 | dmReserved2 |
| 92 | dmColor | 212 | dmPanningWidth |
| 94 | dmDuplex | 216 | dmPanningHeight |
| 96 | dmYResolution | | |
| 98 | dmTTOption | | |

- Offsets 76â€“91 are a union whose display view is dmPosition POINTL at +76, dmDisplayOrientation at +84 and dmDisplayFixedOutput at +88.
- `DM_SPECVERSION` = **0x0401** (for WINVER 0x0500 and later). [MS-RPRN] says dmSpecVersion should be 0x0401 and dmSize must be a multiple of 4 (https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/ccc2a501-794e-4d2b-b312-f69c75131c2e).

**dmFields bits**

| Flag | Value | Flag | Value |
|---|---|---|---|
| DM_ORIENTATION | 0x00000001 | DM_COLOR | 0x00000800 |
| DM_PAPERSIZE | 0x00000002 | DM_DUPLEX | 0x00001000 |
| DM_PAPERLENGTH | 0x00000004 | DM_YRESOLUTION | 0x00002000 |
| DM_PAPERWIDTH | 0x00000008 | DM_TTOPTION | 0x00004000 |
| DM_SCALE | 0x00000010 | DM_COLLATE | 0x00008000 |
| DM_POSITION | 0x00000020 | DM_FORMNAME | 0x00010000 |
| DM_NUP | 0x00000040 | DM_ICMMETHOD / DM_ICMINTENT | 0x00800000 / 0x01000000 |
| DM_COPIES | 0x00000100 | DM_MEDIATYPE | 0x02000000 |
| DM_DEFAULTSOURCE | 0x00000200 | DM_DITHERTYPE | 0x04000000 |
| DM_PRINTQUALITY | 0x00000400 | | |

**Value constants**
- **Orientation:** DMORIENT_PORTRAIT 1, DMORIENT_LANDSCAPE 2.
- **Duplex:** DMDUP_SIMPLEX 1, DMDUP_VERTICAL 2 (long-edge binding), DMDUP_HORIZONTAL 3 (short-edge binding).
- **Colour:** DMCOLOR_MONOCHROME 1, DMCOLOR_COLOR 2.
- **Collate:** DMCOLLATE_FALSE 0, DMCOLLATE_TRUE 1.
- **N-up:** DMNUP_SYSTEM 1, DMNUP_ONEUP 2.
- **Paper:** DMPAPER_LETTER 1, DMPAPER_A4 9, DMPAPER_FIRST 1, DMPAPER_LAST 118 (= DMPAPER_PENV_10_ROTATED), **DMPAPER_USER 256**.
- **Bins:**

  | Name | Value |
  |---|---|
  | DMBIN_UPPER (= DMBIN_ONLYONE) | 1 |
  | DMBIN_LOWER | 2 |
  | DMBIN_MIDDLE | 3 |
  | **DMBIN_MANUAL** | **4** |
  | DMBIN_ENVELOPE | 5 |
  | DMBIN_ENVMANUAL | 6 |
  | **DMBIN_AUTO** | **7** |
  | DMBIN_TRACTOR | 8 |
  | DMBIN_SMALLFMT | 9 |
  | DMBIN_LARGEFMT | 10 |
  | DMBIN_LARGECAPACITY | 11 |
  | DMBIN_CASSETTE | 14 |
  | **DMBIN_FORMSOURCE** | **15** (= DMBIN_LAST) |
  | **DMBIN_USER** | **256** (device-specific bins start here) |

- **Print quality:** DMRES_DRAFT âˆ’1, DMRES_LOW âˆ’2, DMRES_MEDIUM âˆ’3, DMRES_HIGH âˆ’4.
- **TrueType:** DMTT_BITMAP 1, DMTT_DOWNLOAD 2, DMTT_SUBDEV 3, DMTT_DOWNLOAD_OUTLINE 4.
- **Media:** DMMEDIA_STANDARD 1, DMMEDIA_TRANSPARENCY 2, DMMEDIA_GLOSSY 3, DMMEDIA_USER 256.

**Field semantics** (DEVMODEW/DEVMODEA pages, https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-devmodew and .../ns-wingdi-devmodea)
- `dmPaperLength` and `dmPaperWidth` are in **tenths of a millimetre** and override dmPaperSize.
- `dmPaperSize` is either a DMPAPER_\* constant or a device-specific value of DMPAPER_USER (256) or more. It is 0 when both length and width are given.
  - [MS-RPRN] is stricter: DM_PAPERSIZE **must not** be set together with DM_PAPERLENGTH or DM_PAPERWIDTH.
- `dmScale`: output is scaled by dmScale/100, so 100 means 100%.
- `dmDefaultSource`: a DMBIN_\* value or a device value of 256 or more. DMBIN_FORMSOURCE means the bin is chosen automatically to match the form.
- `dmFormName` must be a name that EnumForms returns.

**Driver defaults:** a Unidrv driver in non-XPSDrv mode never sets DM_SCALE in its default DEVMODE; PScript5 always does (https://learn.microsoft.com/en-us/windows-hardware/drivers/print/unidrv-pscript5-driver-default-devmode-setup-behaviorchanges). Check DC_FIELDS before relying on dmScale.

**Observed DEVMODE for Microsoft Print to PDF**
- dmSpecVersion 0x0401, dmDriverVersion 0x0603, dmSize 220, dmDriverExtra 5208.
- dmFields 0x2D03 = ORIENTATION | PAPERSIZE | COPIES | PRINTQUALITY | COLOR | YRESOLUTION.
- dmFormName is "Letter" even though DM_FORMNAME is not set in dmFields.
- DC_FIELDS for the same driver returned 0x12D03, which does include DM_FORMNAME.

---

## 7. DeviceCapabilitiesW

**Signature:** `int DeviceCapabilitiesW(_In_ LPCWSTR pDevice /*queue name, not driver name*/, _In_opt_ LPCWSTR pPort, _In_ WORD fwCapability, _Out_opt_ LPWSTR pOutput, _In_opt_ const DEVMODEW *pDevMode);`
- Declared in wingdi.h, exported by winspool.drv.
- A NULL pDevMode means the driver's current default settings.
- Docs: https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-devicecapabilitiesw

**Return value:** depends on the capability. 0 usually means the call completed but the capability is unsupported. âˆ’1 means either unsupported or a general failure. No GetLastError behaviour is documented.

**Capabilities.** For array capabilities, a NULL pOutput returns the element count.

| Capability | Value | Output (W version) |
|---|---|---|
| DC_FIELDS | 1 | Return value = the driver's dmFields |
| DC_PAPERS | 2 | WORD[] of dmPaperSize IDs |
| DC_PAPERSIZE | 3 | POINT[] (8 bytes each): width, length in **tenths of mm**, portrait orientation |
| DC_MINEXTENT | 4 | Return value: LOWORD = minimum dmPaperWidth, HIWORD = minimum dmPaperLength |
| DC_MAXEXTENT | 5 | Same layout, maximums |
| DC_BINS | 6 | WORD[] of DMBIN values |
| DC_DUPLEX | 7 | 1 if duplex is supported, 0 if not |
| DC_SIZE | 8 | Return value = dmSize |
| DC_EXTRA | 9 | Return value = dmDriverExtra |
| DC_VERSION | 10 | Specification version (= dmSpecVersion) |
| DC_DRIVER | 11 | Driver version (= dmDriverVersion) |
| DC_BINNAMES | 12 | **24 WCHARs per name (48 bytes)**; not null-terminated if exactly 24 long |
| DC_ENUMRESOLUTIONS | 13 | LONG pairs (x, y DPI) |
| DC_FILEDEPENDENCIES | 14 | 64 WCHARs per name |
| DC_TRUETYPE | 15 | Flags |
| DC_PAPERNAMES | 16 | **64 WCHARs per name (128 bytes)**; not null-terminated if exactly 64 long |
| DC_ORIENTATION | 17 | 0, 90 or 270 |
| DC_COPIES | 18 | Maximum copies |
| DC_COLLATE | 22 | 1 or 0 |
| DC_PERSONALITY | 25 | 32 WCHARs per name |
| DC_PRINTERMEM | 28 | Kilobytes |
| DC_MEDIAREADY | 29 | 64 WCHARs per name |
| DC_STAPLE | 30 | Non-zero if stapling is supported |
| DC_PRINTRATEPPM | 31 | Pages per minute |
| DC_COLORDEVICE | 32 | 1 or 0 |
| DC_NUP | 33 | DWORD[] |
| DC_MEDIATYPENAMES | 34 | 64 WCHARs per name |
| DC_MEDIATYPES | 35 | DWORD[] |

Also defined: DC_BINADJUST 19, DC_EMF_COMPLIANT 20, DC_DATATYPE_PRODUCED 21, DC_MANUFACTURER 23 and DC_MODEL 24, which the DrvDeviceCapabilities page says are unused on NT. DC_PRINTRATE is 26 and DC_PRINTRATEUNIT is 27.

**Observed for Microsoft Print to PDF (v4 driver)**
- DC_SIZE 220, DC_EXTRA 5208, DC_VERSION 0x0401, DC_DRIVER 0x0603, DC_DUPLEX 0, DC_ORIENTATION 90, DC_COPIES 1.
- DC_MINEXTENT 508 Ã— 850 and DC_MAXEXTENT 9170 Ã— 12970 (tenths of mm).
- DC_PAPERS, DC_PAPERNAMES and DC_PAPERSIZE each returned 84 entries; for example Letter = ID 1, 2159 Ã— 2794. No bins and no N-up values were reported.

**How drivers include forms from the forms database**
- **Unidrv:**
  - User-defined (custom) paper sizes are forms that administrators define in the print folder, so they are specific to the system.
  - A printer supports them only if its GPD has a PaperSize option `CUSTOMSIZE` with `*MinSize` and `*MaxSize` defining the allowed range (https://learn.microsoft.com/en-us/windows-hardware/drivers/print/supporting-user-defined-paper-sizes, .../specifying-paper-sizes, .../option-attributes-for-the-papersize-feature).
  - Unidrv writes its own GPD forms into the database as FORM_PRINTER entries, filling FORM_INFO_2 from the GPD (https://learn.microsoft.com/en-us/windows-hardware/drivers/print/adding-forms-to-the-forms-database).
  - The exact filter Unidrv applies when building DC_PAPERS is **UNVERIFIED**; it is not documented.
- **Plug-ins:** a Unidrv or PScript5 UI plug-in can replace the DC_PAPERS, DC_PAPERNAMES and DC_PAPERSIZE output entirely (`S_DEVCAP_OUTPUT_FULL_REPLACEMENT`, Vista and later; https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/prcomoem/nf-prcomoem-iprintoemui-devicecapabilities).
- **PScript5:**
  - Its `%CustomPageSize` setting resets dmPaperSize to DMPAPER_CUSTOMSIZE (https://learn.microsoft.com/en-us/windows-hardware/drivers/print/pscript5-keywords).
  - The [MS-RPRN] appendix says the PSCRIPT private custom-size fields are used only when dmPaperSize is 0x7FFF.
  - DMPAPER_CUSTOMSIZE is not in the SDK headers, so the name-to-0x7FFF link is **UNVERIFIED**.

**How dmPaperSize maps to forms database entries**
- **Not documented by Microsoft (UNVERIFIED as a general rule).**
- **Observed (v4 PDF driver):** every DC_PAPERS ID equals the form's 1-based position in EnumFormsW level-1 order. For example, ID 119 = form 119 "ASMEFSheet" and ID 174 = form 174.
  - This includes non-DMPAPER forms with IDs between 119 and 174, which falls between DMPAPER_LAST (118) and DMPAPER_USER (256), so the documented "256 and above" rule is not what this driver does.
  - The driver listed 84 of the 180 forms. It left out forms 175â€“180 even though several of them fit inside its minimum and maximum extents, so which forms a driver includes is driver-specific.
- The Kyocera driver's behaviour is **UNVERIFIED**.
- A form's position depends on which drivers were installed first, so never hard-code an ID. Find the form's index in DC_PAPERNAMES at run time and take DC_PAPERS at the same index.

---

## 8. Ports: EnumPortsW, XcvDataW (TCP/IP AddPort and DeletePort), DeletePortW

**Signatures** (winspool.drv)
- `BOOL EnumPortsW(_In_opt_ LPWSTR pName, DWORD Level /*1|2*/, LPBYTE pPort, DWORD cbBuf, LPDWORD pcbNeeded, LPDWORD pcReturned);`
- `BOOL XcvDataW(_In_ HANDLE hXcv, _In_ PCWSTR pszDataName, _In_reads_bytes_opt_(cbInputData) PBYTE pInputData, DWORD cbInputData, _Out_writes_bytes_opt_(cbOutputData) PBYTE pOutputData, DWORD cbOutputData, _Out_ PDWORD pcbOutputNeeded, _Out_opt_ PDWORD pdwStatus);` There is only a W version; `XcvData` is #defined to `XcvDataW`.
- `BOOL DeletePortW(_In_opt_ LPWSTR pName, _In_ HWND hWnd, _In_ LPWSTR pPortName);`
- `BOOL AddPortW(_In_opt_ LPWSTR pName, _In_ HWND hWnd, _In_ LPWSTR pMonitorName);` (shows UI)
- Docs:
  - Learn pages: .../printdocs/enumports Â· port-info-1 Â· port-info-2
  - XcvData: https://learn.microsoft.com/en-us/previous-versions/ff564255(v=vs.85)
  - TCPMON Xcv interface: https://learn.microsoft.com/en-us/windows-hardware/drivers/print/tcpmon-xcv-interface
  - TCPMON Xcv commands: .../print/tcpmon-xcv-commands
  - PORT_DATA_1: https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/tcpxcv/ns-tcpxcv-_port_data_1
  - [MS-RPRN] RpcXcvData: https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/bf9d2351-aac0-481e-9d37-05eb5cc961da

**Port structures**
- **PORT_INFO_1W** (8 bytes): +0 pName.
- **PORT_INFO_2W** (32 bytes): +0 pPortName, +8 pMonitorName, +16 pDescription, +24 fPortType, +28 Reserved (must be 0).
- fPortType: PORT_TYPE_WRITE 0x1, PORT_TYPE_READ 0x2, PORT_TYPE_REDIRECTED 0x4, PORT_TYPE_NET_ATTACHED 0x8.
- EnumPorts: Windows checks SERVER_ACCESS_ENUMERATE (note `<356>`). It can succeed even on a server with no printers.

**XcvData return value and status**
- TCPMON Xcv interface page: **"A return value of TRUE does not indicate that the operation was successful."** The monitor's result is in `*pdwStatus`.
- The BOOL return value only says whether the request reached the monitor; [MS-RPRN] defines it the same way.
- Always pass a non-NULL pdwStatus: [MS-RPRN] says the server should reject NULL with ERROR_INVALID_PARAMETER.
- For "AddPort", [MS-RPRN] requires a non-NULL pInputData, a non-zero cbInputData and a null-terminated string in the input, otherwise ERROR_INVALID_DATA. PORT_DATA_1 starts with the port name, which satisfies this.

**Opening the Xcv handle**
- New port: `OpenPrinterW(",XcvMonitor Standard TCP/IP Port", &h, {NULL, NULL, SERVER_ACCESS_ADMINISTER})`.
- Existing port: `",XcvPort <PortName>"`.
- Observed: the monitor open succeeds on this machine even without elevation; see Â§1.

**TCPMON commands**

| pszDataName | Input | Output | Access needed |
|---|---|---|---|
| AddPort | PORT_DATA_1, cb = 964 | NULL / 0; pcbOutputNeeded must point to a DWORD | SERVER_ACCESS_ADMINISTER |
| ConfigPort | Same as AddPort | Same as AddPort | SERVER_ACCESS_ADMINISTER |
| DeletePort | DELETE_PORT_DATA_1, cb = 236 | NULL / 0 | SERVER_ACCESS_ADMINISTER |
| GetConfigInfo | CONFIG_INFO_DATA_1 (dwVersion = 1) | PORT_DATA_1 | â€” |
| HostAddress, IPAddress, SNMPCommunity, SNMPDeviceIndex, SNMPEnabled | â€” | Data buffer | â€” |
| MonitorUI | â€” | Data buffer | SERVER_ACCESS_ADMINISTER |

**Status values in \*pdwStatus**

| Status | Value | Meaning |
|---|---|---|
| NO_ERROR | 0 | Success |
| ERROR_ACCESS_DENIED | 5 | Insufficient rights |
| ERROR_INVALID_HANDLE | 6 | Bad Xcv handle |
| ERROR_INVALID_DATA | 13 | Input NULL or too short |
| ERROR_INVALID_PARAMETER | 87 | Unknown command, or an output-buffer problem |
| ERROR_INSUFFICIENT_BUFFER | 122 | Output buffer too small |
| ERROR_INVALID_LEVEL | 124 | dwVersion is not 1 |

**tcpxcv.h constants**
- MAX_PORTNAME_LEN 64, MAX_NETWORKNAME_LEN 49, MAX_NETWORKNAME2_LEN 128, MAX_SNMP_COMMUNITY_STR_LEN 33, MAX_QUEUENAME_LEN 33, MAX_IPADDR_STR_LEN 16, MAX_ADDRESS_STR_LEN 13, MAX_DEVICEDESCRIPTION_STR_LEN 257.
- Most of these macros are written as unparenthesized sums such as `63 +1`, so in C an expression like `2*MAX_PORTNAME_LEN` evaluates to 127.
- **`RESERVED_BYTE_ARRAY_SIZE` does not exist** in SDK 10.0.26100.0 (searched every header under `um\` and `shared\`). The reserved arrays are literal sizes: 540 in PORT_DATA_1, 514 in PORT_DATA_2, 98 in DELETE_PORT_DATA_1 and 128 in CONFIG_INFO_DATA_1.
- PROTOCOL_UNKNOWN_TYPE 0, **PROTOCOL_RAWTCP_TYPE 1**, **PROTOCOL_LPR_TYPE 2**.

**PORT_DATA_1** (964 bytes, alignment 4)

| Offset | Field | Notes |
|---|---|---|
| +0 | sztPortName WCHAR[64] | |
| +128 | dwVersion | Must be 1 |
| +132 | dwProtocol | |
| +136 | cbSize | sizeof = 964 |
| +140 | dwReserved | 0 |
| +144 | sztHostAddress WCHAR[49] | |
| +242 | sztSNMPCommunity WCHAR[33] | |
| +308 | dwDoubleSpool | BOOL |
| +312 | sztQueue WCHAR[33] | LPR queue name |
| +378 | sztIPAddress WCHAR[16] | |
| +410 | Reserved BYTE[540] | Zero-filled |
| +950 | (2 bytes padding) | |
| +952 | dwPortNumber | |
| +956 | dwSNMPEnabled | BOOL |
| +960 | dwSNMPDevIndex | |

- The documentation does not say which of sztHostAddress or sztIPAddress TCPMON uses for RAW ports: **UNVERIFIED**.
- The device's RAW port number and SNMP settings are **UNVERIFIED** here; they belong to the device research.
- **Documentation inconsistency:** the PORT_DATA_1 page's remark says pOutputData receives the struct for AddPort and ConfigPort. The TCPMON command table and [MS-RPRN] put it in **pInputData** with pOutputData NULL; follow the command table.

**Other TCPMON structures**
- **DELETE_PORT_DATA_1** (236 bytes): +0 psztPortName WCHAR[64], +128 Reserved[98], +226 padding (2), +228 dwVersion (= 1), +232 dwReserved (= 0).
- **CONFIG_INFO_DATA_1** (132 bytes): +0 Reserved[128], +128 dwVersion (= 1).
- **PORT_DATA_2** (1068 bytes): +144 sztHostAddress WCHAR[128], +400 SNMP community, +468 dwDoubleSpool, +472 queue, +538 Reserved[514], +1052 dwPortNumber, +1056 dwSNMPEnabled, +1060 dwSNMPDevIndex, +1064 dwPortMonitorMibIndex.

**DeletePortW**
- It calls the monitor UI DLL's DeletePortUI (https://learn.microsoft.com/en-us/windows-hardware/drivers/print/deleting-a-port).
- [MS-RPRN] RpcDeletePort errors: ERROR_UNKNOWN_PORT; ERROR_NOT_SUPPORTED; **ERROR_BUSY (170, 0xAA)** if a printer or job uses the port. On current Windows it checks SERVER_ACCESS_ADMINISTER (note `<358>`).
- For silent removal use XcvData "DeletePort".
- Whether "DeletePort" needs an XcvMonitor handle or an XcvPort handle is **UNVERIFIED**.

---

## 9. EnumPrinterDriversW, GetPrinterDriverW

**Signatures**
- `BOOL EnumPrinterDriversW(_In_opt_ LPWSTR pName, _In_opt_ LPWSTR pEnvironment, DWORD Level, LPBYTE pDriverInfo, DWORD cbBuf, LPDWORD pcbNeeded, LPDWORD pcReturned);`
- `BOOL GetPrinterDriverW(HANDLE hPrinter, _In_opt_ LPWSTR pEnvironment, DWORD Level, LPBYTE pDriverInfo, DWORD cbBuf, LPDWORD pcbNeeded);`
- Valid levels are 1â€“6 and 8.
- Docs: .../printdocs/enumprinterdrivers Â· getprinterdriver Â· driver-info-2 Â· driver-info-6 Â· driver-info-8

**Environment and behaviour**
- A NULL environment means the caller's own environment, not the server's. The x64 string is **"Windows x64"**; EnumPrinterDrivers also accepts "all".
- Observed environments from Get-PrinterDriver: "Windows x64" and "Windows NT x86".
- GetPrinterDriver **installs the driver locally if it is missing**, which matters for connections. A missing driver gives ERROR_UNKNOWN_PRINTER_DRIVER (1797).

**DRIVER_INFO_2W** (48 bytes)

| Offset | Field |
|---|---|
| +0 | cVersion (DWORD) |
| +4 | (padding, 4) |
| +8 | pName |
| +16 | pEnvironment |
| +24 | pDriverPath |
| +32 | pDataFile |
| +40 | pConfigFile |

**DRIVER_INFO_6W** (136 bytes): the first six fields are as in DRIVER_INFO_2W, then:

| Offset | Field |
|---|---|
| +48 | pHelpFile |
| +56 | pDependentFiles |
| +64 | pMonitorName |
| +72 | pDefaultDataType |
| +80 | pszzPreviousNames |
| +88 | **ftDriverDate** (FILETIME) |
| +96 | **dwlDriverVersion** (DWORDLONG) |
| +104 | **pszMfgName** |
| +112 | pszOEMUrl |
| +120 | **pszHardwareID** |
| +128 | pszProvider |

**DRIVER_INFO_8W** (200 bytes): offsets +0 to +128 are as in DRIVER_INFO_6W, then:

| Offset | Field | Notes |
|---|---|---|
| +136 | pszPrintProcessor | |
| +144 | pszVendorSetup | |
| +152 | pszzColorProfiles | |
| +160 | **pszInfPath** | The INF's path in the driver store |
| +168 | **dwPrinterDriverAttributes** | |
| +172 | (padding, 4) | |
| +176 | pszzCoreDriverDependencies | |
| +184 | ftMinInboxDriverVerDate | |
| +192 | dwlMinInboxDriverVerVersion | |

**Which level gives which field**
- Version, date, manufacturer and hardware ID: levels **6 and 8**. Drivers that were added at a level other than 6 return NULL strings and 0 for version and date at level 6 (DRIVER_INFO_6 page).
- INF path and driver attributes: **level 8 only**. pszInfPath, dwPrinterDriverAttributes and pszzCoreDriverDependencies must be NULL or 0 when the struct is passed to AddPrinterDriver(Ex).
- The older `DRIVER_INFO_5.dwDriverAttributes` field uses DRIVER_KERNELMODE 1 and DRIVER_USERMODE 2.

**PRINTER_DRIVER_\* attribute flags** (winspool.h)

| Flag | Value |
|---|---|
| PACKAGE_AWARE | 0x1 |
| XPS | 0x2 |
| SANDBOX_ENABLED | 0x4 |
| CLASS | 0x8 |
| DERIVED | 0x10 |
| NOT_SHAREABLE | 0x20 |
| CATEGORY_FAX | 0x40 |
| CATEGORY_FILE | 0x80 |
| CATEGORY_VIRTUAL | 0x100 |
| CATEGORY_SERVICE | 0x200 |
| SOFT_RESET_REQUIRED | 0x400 |
| SANDBOX_DISABLED | 0x800 |
| CATEGORY_3D | 0x1000 |
| CATEGORY_CLOUD | 0x2000 |

**cVersion**
- The Learn pages say the only supported value is 3.
- [MS-RPRN] note `<13>` defines 3 as a user-mode v3 driver and 4 as a Windows 8+ driver (https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/4464eaf0-f34f-40d5-b970-736437a21913). v4 drivers report 4; observed MajorVersion 4 on this machine.

**dwlDriverVersion encoding**
- [MS-RPRN] says the format is up to the manufacturer.
- Note `<16>` gives the Windows encoding as four 16-bit fields from high to low: major, minor, build, revision. Its example is 0x000500020ECE0726 = 5.2.3790.1830.
- ftDriverDate is the manufacturer's build date.

---

## 10. Driver package APIs (all return HRESULT; S_OK on success)

**Signatures** (winspool.h, winspool.drv)
- `HRESULT UploadPrinterDriverPackageW(_In_opt_ LPCWSTR pszServer, _In_ LPCWSTR pszInfPath, _In_opt_ LPCWSTR pszEnvironment, _In_ DWORD dwFlags, _In_ HWND hwnd, _Out_writes_(*pcchDestInfPath) LPWSTR pszDestInfPath, _Inout_ PULONG pcchDestInfPath);`
- `HRESULT InstallPrinterDriverFromPackageW(_In_opt_ LPCWSTR pszServer, _In_opt_ LPCWSTR pszInfPath, _In_ LPCWSTR pszDriverName, _In_opt_ LPCWSTR pszEnvironment, _In_ DWORD dwFlags);`
- `HRESULT GetPrinterDriverPackagePathW(_In_opt_ LPCWSTR pszServer, _In_opt_ LPCWSTR pszEnvironment, _In_opt_ LPCWSTR pszLanguage, _In_ LPCWSTR pszPackageID, _Inout_updates_opt_(cchDriverPackageCab) LPWSTR pszDriverPackageCab, _In_opt_ DWORD cchDriverPackageCab, _Out_ LPDWORD pcchRequiredSize);`
- `HRESULT DeletePrinterDriverPackageW(_In_opt_ LPCWSTR pszServer, _In_ LPCWSTR pszInfPath, _In_opt_ LPCWSTR pszEnvironment);`
- Docs: .../printdocs/uploadprinterdriverpackage Â· installprinterdriverfrompackage Â· getprinterdriverpackagepath Â· deleteprinterdriverpackage

**Flags**
- `UPDP_SILENT_UPLOAD` 0x1: no UI.
- `UPDP_UPLOAD_ALWAYS` 0x2: upload even if the package is already in the store.
- `UPDP_CHECK_DRIVERSTORE` 0x4: check the store first; ignored when UPLOAD_ALWAYS is set.
- `IPDFP_COPY_ALL_FILES` 0x1: copy every file and ignore time stamps. The only other valid value is 0, which copies only newer files; the header comment ties 0 to APD_COPY_NEW_FILES.

**UploadPrinterDriverPackageW**
- `pszInfPath` is the source path of the .inf.
- `pszDestInfPath` receives the INF's path inside the driver store.
- `pcchDestInfPath` holds the buffer size in characters on input, and the path length including the terminator on output.
- What happens when the buffer is too small is **UNVERIFIED**.
- The docs say the store is typically `%windir%\inf` or `%windir%\System32\DriverStore\FileRepository`, that only one package can be uploaded per call (dependencies must be uploaded separately), and that **only signed packages can be uploaded**.

**InstallPrinterDriverFromPackageW**
- **`pszInfPath` may be NULL**; this means an INF that shipped with Windows (the header marks it `_In_opt_`).
- Otherwise pass the driver-store path, which is the pszDestInfPath from the upload.
- It also installs the package's colour profiles and print processors.
- Remote installs, and local installs under Terminal Services, require printer-administration rights.
- **Only signed packages can be installed on a remote computer.**

**GetPrinterDriverPackagePathW**
- `pszPackageID` usually comes from GetCorePrinterDrivers.
- To size the buffer, call with `pszDriverPackageCab = NULL`, then retry with `*pcchRequiredSize` characters.
- [MS-RPRN] says the server returns ERROR_INSUFFICIENT_BUFFER when the buffer is too small (https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/e453b113-50c6-4a12-9ca6-e327b5accca6).
- Whether the caller then sees 122 or 0x8007007A is **UNVERIFIED**. A bare 122 would pass the SUCCEEDED() test, so compare the required size with the buffer size instead.

**DeletePrinterDriverPackageW**
- Fails with E_ACCESSDENIED (0x80070005) for a package that shipped with Windows; those packages cannot be removed.
- For a package in use, the page names "ERROR_PRINT_DRIVER_PACKAGE_IN_USE" wrapped in HRESULT_CODE. The winerror.h name is **ERROR_PRINTER_DRIVER_PACKAGE_IN_USE (3015, 0xBC7; HRESULT_FROM_WIN32 = 0x80070BC7)**. The exact value returned is **UNVERIFIED**.
- The caller needs printer-administration privileges.

**Privileges**
- Adding a package to the driver store (staging) requires administrator rights. Once a package is in the store, a standard user can install it, unless Group Policy grants driver installation rights to standard users (https://learn.microsoft.com/en-us/windows-hardware/drivers/print/point-and-print-with-packages).
- The Upload page itself states no privilege requirement.

**Unsigned packages:** the HRESULT returned for an unsigned or untrusted package is **UNVERIFIED**. The following codes exist in winerror.h but are not documented for these APIs:
- TRUST_E_NOSIGNATURE 0x800B0100
- SPAPI_E_NO_CATALOG_FOR_OEM_INF 0x800F022F
- SPAPI_E_DRIVER_STORE_ADD_FAILED 0x800F0247
- SPAPI_E_AUTHENTICODE_TRUST_NOT_ESTABLISHED 0x800F0242

**Driver servicing (2026):** Microsoft's end-of-servicing plan says that from **1 July 2026** the printer driver ranking always prefers the Windows IPP inbox class driver. Vendor-supplied installation packages remain allowed (https://learn.microsoft.com/en-us/windows-hardware/drivers/print/end-of-servicing-plan-for-third-party-printer-drivers-on-windows). The effect on an AddPrinter call that names a driver explicitly is **UNVERIFIED**.

---

## 11. EnumJobsW and GetJobW; job and printer status values

**Signatures**
- `BOOL EnumJobsW(HANDLE hPrinter, DWORD FirstJob /*0-based*/, DWORD NoJobs, DWORD Level /*1,2,3*/, LPBYTE pJob, DWORD cbBuf, LPDWORD pcbNeeded, LPDWORD pcReturned);`
- `BOOL GetJobW(HANDLE hPrinter, DWORD JobId, DWORD Level /*1,2*/, LPBYTE pJob, DWORD cbBuf, LPDWORD pcbNeeded);`
- Docs: .../printdocs/enumjobs Â· getjob Â· job-info-1
- To get the job count, read cJobs from GetPrinter level 2 (EnumJobs page).

**JOB_INFO_1W** (96 bytes)

| Offset | Field | Offset | Field |
|---|---|---|---|
| +0 | JobId | +56 | Status |
| +4 | (padding, 4) | +60 | Priority |
| +8 | pPrinterName | +64 | Position |
| +16 | pMachineName | +68 | TotalPages |
| +24 | pUserName | +72 | PagesPrinted |
| +32 | pDocument | +76 | Submitted SYSTEMTIME (16 bytes) |
| +40 | pDatatype | +92 | (padding, 4) |
| +48 | pStatus | | |

**JOB_INFO_1 semantics** (JOB_INFO_1 page)
- Check `pStatus` before `Status`.
- Status 0 means the queue was paused after the job finished spooling.
- Submitted is in UTC.
- TotalPages and PagesPrinted can be 0.
- If the port monitor does not support TrueEndOfJob, the job is marked PRINTED as soon as it is sent.
- Priority: MIN_PRIORITY 1, MAX_PRIORITY 99, DEF_PRIORITY 1, NO_PRIORITY 0.

**JOB_STATUS_\***

| Name | Value |
|---|---|
| PAUSED | 0x1 |
| ERROR | 0x2 |
| DELETING | 0x4 |
| SPOOLING | 0x8 |
| PRINTING | 0x10 |
| OFFLINE | 0x20 |
| PAPEROUT | 0x40 |
| PRINTED | 0x80 |
| DELETED | 0x100 |
| BLOCKED_DEVQ | 0x200 |
| USER_INTERVENTION | 0x400 |
| RESTART | 0x800 |
| COMPLETE | 0x1000 (sent to the printer, not necessarily printed) |
| RETAINED | 0x2000 |
| RENDERING_LOCALLY | 0x4000 |

**PRINTER_STATUS_\***

| Name | Value |
|---|---|
| PAUSED | 0x1 |
| ERROR | 0x2 |
| PENDING_DELETION | 0x4 |
| PAPER_JAM | 0x8 |
| PAPER_OUT | 0x10 |
| MANUAL_FEED | 0x20 |
| PAPER_PROBLEM | 0x40 |
| OFFLINE | 0x80 |
| IO_ACTIVE | 0x100 |
| BUSY | 0x200 |
| PRINTING | 0x400 |
| OUTPUT_BIN_FULL | 0x800 |
| NOT_AVAILABLE | 0x1000 |
| WAITING | 0x2000 |
| PROCESSING | 0x4000 |
| INITIALIZING | 0x8000 |
| WARMING_UP | 0x10000 |
| TONER_LOW | 0x20000 |
| NO_TONER | 0x40000 |
| PAGE_PUNT | 0x80000 |
| USER_INTERVENTION | 0x100000 |
| OUT_OF_MEMORY | 0x200000 |
| DOOR_OPEN | 0x400000 |
| SERVER_UNKNOWN | 0x800000 |
| POWER_SAVE | 0x1000000 |
| SERVER_OFFLINE | 0x2000000 |
| DRIVER_UPDATE_NEEDED | 0x4000000 |

**PRINTER_ATTRIBUTE_\***

| Name | Value |
|---|---|
| QUEUED | 0x1 |
| DIRECT | 0x2 |
| DEFAULT | 0x4 |
| SHARED | 0x8 |
| NETWORK | 0x10 |
| HIDDEN | 0x20 |
| LOCAL | 0x40 |
| ENABLE_DEVQ | 0x80 |
| KEEPPRINTEDJOBS | 0x100 |
| DO_COMPLETE_FIRST | 0x200 |
| WORK_OFFLINE | 0x400 |
| ENABLE_BIDI | 0x800 |
| RAW_ONLY | 0x1000 |
| PUBLISHED | 0x2000 |
| FAX | 0x4000 |
| TS | 0x8000 |
| PUSHED_USER | 0x20000 |
| PUSHED_MACHINE | 0x40000 |
| MACHINE | 0x80000 |
| FRIENDLY_NAME | 0x100000 |
| TS_GENERIC_DRIVER | 0x200000 |
| PER_USER | 0x400000 |
| ENTERPRISE_CLOUD | 0x800000 |

PRINTER_INFO_4 notes that some connections can report both LOCAL and NETWORK.

---

## 12. Elevation, privileges, and errors when the spooler is unavailable

**Documented privilege requirements**

| Operation | Requirement | Source |
|---|---|---|
| AddPrinter | SeLoadDriverPrivilege plus SERVER_ACCESS_ADMINISTER | AddPrinter page |
| SetPrinter | PRINTER_ACCESS_ADMINISTER, or SERVER_ACCESS_ADMINISTER on a server handle | [MS-RPRN] `<274>` |
| AddForm, SetForm, DeleteForm | User has SERVER_ACCESS_ADMINISTER | [MS-RPRN] `<353>`â€“`<355>` |
| TCPMON AddPort, ConfigPort, DeletePort, MonitorUI | SERVER_ACCESS_ADMINISTER | TCPMON Xcv commands |
| Driver-store staging | Administrator | Point and Print with Packages |
| DeletePrinterDriverPackage | Printer administration | DeletePrinterDriverPackage page |

- SetPrinter's firewall-exception step also succeeds only for administrators (SetPrinter page).
- Per-user state, namely PRINTER_INFO_9 (stored in the user's registry) and printer connections (the user's own), belongs to the account the token runs as.

**Observed on this machine**
- The non-elevated admin token has BUILTIN\Administrators as deny-only (`whoami /groups`).
- Printer-level PRINTER_ALL_ACCESS failed with error 5 without elevation.
- Server-level SERVER_ACCESS_ADMINISTER and the TCPMON XcvMonitor open succeeded without elevation, because of the INTERACTIVE ACE described in Â§1.
- None of the modifying calls was attempted.

**Inference (UNVERIFIED):** if the tool is elevated with a *different* account's credentials, per-user operations such as level-9 DEVMODEs, connections and the default printer act on that other account's profile.

**Spooler stopped or unreachable**
- No Microsoft API page states which error code is returned. The Microsoft troubleshooting article lists symptoms only (https://learn.microsoft.com/en-us/troubleshoot/windows-server/printing/print-spooler-service-not-running).
- `RPC_S_SERVER_UNAVAILABLE` = 1722 (0x6BA; HRESULT 0x800706BA) is documented as "the RPC server is unavailable" (https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1700-3999-).
- The link between this code and a stopped spooler is **UNVERIFIED**. It is commonly reported, but I could not test it because stopping the spooler was off-limits.
- Related codes, also **UNVERIFIED** for these APIs:

  | Code | Decimal | Hex |
  |---|---|---|
  | RPC_S_UNKNOWN_IF | 1717 | 0x6B5 |
  | RPC_S_CALL_FAILED | 1726 | 0x6BE |
  | RPC_S_CALL_FAILED_DNE | 1727 | 0x6BF |
  | EPT_S_NOT_REGISTERED | 1753 | 0x6D9 |
  | ERROR_SERVICE_NOT_ACTIVE | 1062 | 0x426 |

**Other documented caveats**
- Every call is blocking, so keep these calls off the UI thread.
- Do not call OpenPrinter, OpenPrinter2, EnumPrinters or AddPrinter from DllMain.
- Do not share a printer handle across threads without your own synchronisation.
- For remote printers, OpenPrinter, GetPrinter and EnumForms can return cached data.

---

## Evidence files

- Raw pages: `C:\Users\abdel\AppData\Local\Temp\claude\C--Users-abdel-Desktop-Amwag-Printer\7737442f-5f1d-46cc-85b0-3b47c5e9c693\scratchpad\research\winapi-spooler\dl-*\page.html`
- Text copies: `C:\Users\abdel\AppData\Local\Temp\claude\C--Users-abdel-Desktop-Amwag-Printer\7737442f-5f1d-46cc-85b0-3b47c5e9c693\scratchpad\research\winapi-spooler\_text\*.txt`
- Scripts: `C:\Users\abdel\AppData\Local\Temp\claude\C--Users-abdel-Desktop-Amwag-Printer\7737442f-5f1d-46cc-85b0-3b47c5e9c693\scratchpad\scripts-winapi-spooler\` â€” `fetch-doc.ps1`, `layouts_and_exports.py`, `implib_dll.py`, `readonly_probe.py`, `readonly_probe2.py`, `readonly_probe3.py`, `readonly_probe4.py`