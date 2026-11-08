# AMWAG Printer Setup: Windows API platform reference (label: winapi-platform)

## Evidence conventions and files

**Source tags**

- **[SDK]**: Windows SDK 10.0.26100.0 headers on this machine, under `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\`.
- **[DOC]**: a Microsoft Learn page. I pulled the source markdown from github.com/MicrosoftDocs and saved a copy under the docs folder listed below.
- **[SPEC]**: the Print Schema Specification v2.0 (Namespace Extensions, v1.0, dated 4/17/2015). Download: https://download.microsoft.com/download/D/E/C/DECA6E6B-3E81-48E7-B7EF-6D92A547D03C/print-schema-spec-2-0.zip. SHA256 `314B1810F1FEE1F872C4FDF55F9DDA4AA6A16432F220F04176298F63304EB7BF`.
- **[OBS]**: observed on this dev machine (Windows 11 10.0.26300 x64, .NET 10.0.12).
  - Probes were C programs compiled with MSVC 14.44 x64 against the SDK above, plus read-only PowerShell cmdlets.
  - Nothing on the machine was changed. `HKCU\Printers` and `HKLM\...\Print\Printers\Microsoft Print to PDF` were exported before and after the probes, and the exports are byte-identical.
  - There is no USB, TCP/IP or WSD printer on this machine, so those behaviours could not be observed.
- **[COMMUNITY]**: a non-Microsoft source, or a Microsoft Q&A forum post.
- **UNVERIFIED**: not confirmed by any source above.
- Copyright note: Microsoft doc prose is paraphrased with its URL. At most one short quote is used.

**Evidence files**

All are under `C:\Users\abdel\AppData\Local\Temp\claude\C--Users-abdel-Desktop-Amwag-Printer\7737442f-5f1d-46cc-85b0-3b47c5e9c693\scratchpad\`:

| File | Contents |
|---|---|
| `winapi-tests\probe\probe-output.txt` | layouts, constants, trust and catalog checks, CfgMgr32, token, GetPrinter/DocumentProperties |
| `winapi-tests\probe\probe-tampered-output.txt` | trust checks against a byte-flipped catalog copy |
| `winapi-tests\probe\ptprobe-output2.txt` | Print Ticket API behaviour |
| `winapi-tests\probe\ifprops-output.txt` | print-queue device-interface properties |
| `winapi-tests\probe\pipesd-output.txt` | default named-pipe security descriptor |
| `winapi-tests\x509cat\x509cat-output.txt` | .NET 10 `CreateFromSignedFile` results |
| `winapi-local-evidence\pdf-printcaps.xml`, `pdf-printticket.xml`, `onenote-printcaps.xml` | real PrintCapabilities/PrintTicket XML from this machine |
| `research\winapi-platform\docs\` | MicrosoftDocs source markdown |
| `research\winapi-platform\txt-print-schema-spec-2-0\spec.txt` | spec text extract |
| `winapi-tests\probe\probe.c`, `ptprobe.c`, `ifprops.c`, `pipesd.c` | probe sources |

---

## 1. Print Ticket API (prntvpt.dll)

### Packaging

- **[SDK][DOC]** Header `prntvpt.h`, import library `Prntvpt.lib`, DLL `Prntvpt.dll`.
- **[SDK]** `DECLARE_HANDLE(HPTPROVIDER)` defines an opaque, pointer-sized handle. The header also has `#define PRINTTICKET_ISTREAM_APIS 1`.
- **[OBS]** `dumpbin /exports` shows single undecorated exports, with no A/W variants:
  - PTCloseProvider, PTConvertDevModeToPrintTicket, PTConvertPrintTicketToDevMode
  - PTGetPrintCapabilities, PTGetPrintDeviceCapabilities, PTGetPrintDeviceResources
  - PTMergeAndValidatePrintTicket, PTOpenProvider, PTOpenProviderEx
  - PTQuerySchemaVersionSupport, PTReleaseMemory
  - BindPTProviderThunk, UnbindPTProviderThunk
- **DEVMODE type.** The header declares `PDEVMODE`, which maps to the TCHAR variant. **[OBS]** The DLL accepts and returns **DEVMODEW**:
  - A 5428-byte DEVMODEW from `DocumentPropertiesW` converted correctly.
  - `PTConvertPrintTicketToDevMode` returned `dmSize=220` (that is, sizeof(DEVMODEW)), `dmDriverExtra=5208`, `dmFormName="A4"`.

### Signatures [SDK], x64, all `WINAPI`, all return `HRESULT`

```c
HRESULT PTQuerySchemaVersionSupport(PCWSTR pszPrinterName, DWORD *pMaxVersion);
HRESULT PTOpenProvider(PCWSTR pszPrinterName, DWORD dwVersion, HPTPROVIDER *phProvider);
HRESULT PTOpenProviderEx(PCWSTR pszPrinterName, DWORD dwMaxVersion, DWORD dwPrefVersion,
                         HPTPROVIDER *phProvider, DWORD *pUsedVersion);
HRESULT PTCloseProvider(HPTPROVIDER hProvider);
HRESULT PTReleaseMemory(PVOID pBuffer);
HRESULT PTGetPrintCapabilities(HPTPROVIDER hProvider, IStream *pPrintTicket /*opt*/,
                               IStream *pCapabilities /*inout*/, BSTR *pbstrErrorMessage /*opt*/);
HRESULT PTMergeAndValidatePrintTicket(HPTPROVIDER hProvider, IStream *pBaseTicket,
                               IStream *pDeltaTicket /*opt*/, EPrintTicketScope scope,
                               IStream *pResultTicket /*inout*/, BSTR *pbstrErrorMessage /*opt*/);
HRESULT PTConvertPrintTicketToDevMode(HPTPROVIDER hProvider, IStream *pPrintTicket,
                               EDefaultDevmodeType baseDevmodeType, EPrintTicketScope scope,
                               ULONG *pcbDevmode, PDEVMODE *ppDevmode, BSTR *pbstrErrorMessage /*opt*/);
HRESULT PTConvertDevModeToPrintTicket(HPTPROVIDER hProvider, ULONG cbDevmode, PDEVMODE pDevmode,
                               EPrintTicketScope scope, IStream *pPrintTicket /*inout*/);
```

### Enums [SDK, confirmed by compiled probe]

| Enum | Values |
|---|---|
| `EPrintTicketScope` | `kPTPageScope=0`, `kPTDocumentScope=1`, `kPTJobScope=2` |
| `EDefaultDevmodeType` | `kUserDefaultDevmode=0`, `kPrinterDefaultDevmode=1` |

### Return codes [SDK prntvpt.h]

| Name | Value | Meaning |
|---|---|---|
| S_PT_NO_CONFLICT | 0x00040001 | success (SUCCEEDED() is true) |
| S_PT_CONFLICT_RESOLVED | 0x00040002 | success; the merged ticket was changed |
| E_PRINTTICKET_FORMAT | 0x80040003 | base or input ticket is not valid Print Schema |
| E_PRINTCAPABILITIES_FORMAT | 0x80040004 | driver produced invalid PrintCapabilities |
| E_DELTA_PRINTTICKET_FORMAT | 0x80040005 | delta ticket is invalid |
| E_PRINTDEVICECAPABILITIES_FORMAT | 0x80040006 | PrintDeviceCapabilities invalid |

Because the S_PT_* codes are success codes, a merge does not return `S_OK`. Test with `SUCCEEDED()` or compare against both S_PT values.

### Semantics: documented [DOC] and observed [OBS]

**Printer name and blocking**
- All functions block and may be slow; do not call them on a UI thread [DOC].
- Pass the full queue name, not the truncated `DEVMODE.dmDeviceName`.

**Schema version**
- The Windows Vista schema is version 1 [DOC].
- [OBS] `PTQuerySchemaVersionSupport` returns 1.
- [OBS] `PTOpenProvider(name, 2, â€¦)` fails with 0x80040001. winerror.h assigns that value to OLE_E_ADVF; it is not a prntvpt.h constant, so its symbolic meaning is UNVERIFIED.
- [OBS] `PTOpenProviderEx(name, 1, 1, â€¦)` succeeds with `*pUsedVersion = 1`.

**PTGetPrintCapabilities**
- Writes at the stream's current seek position and leaves the seek at the end. Rewind before reading.
- Free `pbstrErrorMessage` with `SysFreeString`.
- [OBS] 49,161 bytes; seek position afterwards = 49,161.

**PTConvertDevModeToPrintTicket**
- `cbDevmode` must be `dmSize + dmDriverExtra`.
- Settings outside the requested scope are dropped. Job scope includes document and page scope.
- A DEVMODE from a different printer may lose its settings.
- The caller must rewind the stream afterwards.
- [OBS] The output contains a driver-private `<psf:ParameterInit name="ns0000:PageDevmodeSnapshot">` (base64 private DEVMODE data). Preserve it when round-tripping.

**PTConvertPrintTicketToDevMode**
- With `kUserDefaultDevmode`, the user default fills unspecified settings; if no user default exists, the queue default is used.
- The result can be internally inconsistent. Validate it with `DocumentProperties`.
- Free it with `PTReleaseMemory`.
- [OBS] An ISO A4 ticket produced `dmPaperSize=9`, `dmFormName="A4"`, `dmFields=0x00012D03`; `PTReleaseMemory` returned S_OK.

**PTMergeAndValidatePrintTicket**
- Validates base and delta against the Print Schema Framework, then checks viability with the driver. Viability problems are not reported in the error string. `pDeltaTicket` may be NULL [DOC].
- [OBS] results with Microsoft Print to PDF, job scope:

| Delta ticket | HRESULT | Result ticket |
|---|---|---|
| psk:ISOA4 + psk:Portrait | 0x00040001 | ISOA4 210000Ã—297000 |
| psk:CustomMediaSize 82296Ã—170180 (driver has no custom size) | **0x00040001** | **still NorthAmericaLetter**: delta silently dropped |
| psk:JobDuplexAllDocumentsContiguously, psk:PageScaling, psk:JobInputBin (features the driver lacks) | 0x00040001 | those features are absent from the result |
| malformed XML delta | 0x80040005 | error string from the XML parser, with position `(1,23)` |
| malformed XML base | 0x80040003 | same |

So the HRESULT never proves the delta was applied. Always re-parse the result ticket and compare it with what you requested.

### Threading and COM

- **[DOC]** Each PT function page says the handle must be used on the thread that opened it, and that use from another thread returns E_INVALIDARG.
- **[DOC]** Application Verifier, printing stop codes:
  - PrintTicket provider handles are not thread-safe. Open one per thread or serialize access.
  - Stop 0xA010 `MULTITHREADED_ACCESS_TO_PRINT_TICKET_PROVIDER_HANDLE` (concurrent use).
  - Stop 0xA011 `PRINT_TICKET_PROVIDER_HANDLE_ACCESSED_NOT_ON_THE_THREAD_THAT_OPENED_IT` (warning).
  - A leaked handle at thread exit is also flagged, as are network-bound print APIs called on a GUI thread.
- **[DOC]** `BindPTProviderThunk`, the deprecated equivalent of `PTOpenProviderEx`, requires `CoInitializeEx` before the call. The PTOpenProvider/Ex pages do not state any COM requirement.
- **[COMMUNITY]** A Chromium `win_helper.cc` comment says `PTOpenProvider` calls `CoInitializeEx(COINIT_MULTITHREADED)` internally.
- **[OBS] on 10.0.26300:**
  - `PTOpenProvider` succeeded on a thread with no COM initialisation, on an STA thread, and on an MTA thread.
  - After open and close on a thread with no COM, `CoInitializeEx(STA)` on that thread returned S_OK, so no apartment was left behind.
  - **The documented thread check is not enforced.** `PTGetPrintCapabilities` from a different thread returned S_OK. `PTCloseProvider` from a different thread returned S_OK **and closed the handle**: the next call on the opening thread returned 0x80070006 (E_HANDLE).
- **Rule:** keep each HPTPROVIDER, and the streams and BSTRs used with it, confined to one dedicated worker thread. Initialize COM on that thread; both models worked.

### Creating the IStream arguments

- **[SDK/DOC]** `WINOLEAPI CreateStreamOnHGlobal(HGLOBAL hGlobal, BOOL fDeleteOnRelease, LPSTREAM *ppstm);` (Ole32.lib / Ole32.dll).
- With `hGlobal = NULL`, a new empty stream is created.
- With `fDeleteOnRelease = TRUE`, the final Release frees the memory.
- The initial seek position is 0.
- If you pass an existing HGLOBAL, the stream's initial size is `GlobalSize` (which may be rounded). Use `IStream::SetSize` if the logical size matters.
- `GetHGlobalFromStream(LPSTREAM, HGLOBAL*)` returns the possibly reallocated handle. The doc says this stream implementation is thread-safe.
- **[DOC]** `SHCreateMemStream(const BYTE*, UINT)` (Shlwapi) is the recommended faster alternative. Differences:
  - No HGLOBAL access.
  - Thread-safe only from Windows 8 on.
  - A read past the end returns S_FALSE, where the HGLOBAL stream returns S_OK with 0 bytes read.
- **Pattern used in [OBS]:**
  1. Write the UTF-8 ticket bytes.
  2. `Seek(0, STREAM_SEEK_SET)` before passing the stream as input.
  3. Pass an empty stream for output.
  4. After the call, `Seek(0)` and read `Stat(STATFLAG_NONAME).cbSize` bytes.

### URLs

- https://learn.microsoft.com/en-us/windows/win32/api/prntvpt/ and the function pages under it:
  - `nf-prntvpt-ptopenprovider`, `-ptopenproviderex`, `-ptcloseprovider`
  - `-ptgetprintcapabilities`, `-ptconvertdevmodetoprintticket`, `-ptconvertprinttickettodevmode`
  - `-ptmergeandvalidateprintticket`, `-ptreleasememory`, `-ptqueryschemaversionsupport`
  - `ne-prntvpt-eprintticketscope`, `ne-prntvpt-edefaultdevmodetype`
- https://learn.microsoft.com/en-us/windows/win32/printdocs/bindptproviderthunk
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/application-verifier-stop-codes-printing
- https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-createstreamonhglobal
- https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-gethglobalfromstream
- https://learn.microsoft.com/en-us/windows/win32/api/shlwapi/nf-shlwapi-shcreatememstream
- [COMMUNITY] https://chromium.googlesource.com/chromium/src/+/30.0.1599.85/printing/backend/win_helper.cc

---

## 2. Print Schema keywords

### Namespace URIs

| Prefix (conventional) | URI | Source |
|---|---|---|
| psf | `http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework` | [OBS][SPEC] |
| psk | `http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords` | [OBS][SPEC] |
| xsi | `http://www.w3.org/2001/XMLSchema-instance` | [OBS][SPEC] |
| xsd (the spec uses `xs`) | `http://www.w3.org/2001/XMLSchema` | [OBS][SPEC] |
| psf2 | `http://schemas.microsoft.com/windows/2013/12/printing/printschemaframework2` | [SPEC] |
| psk12 | `http://schemas.microsoft.com/windows/2013/12/printing/printschemakeywordsv12` | [SPEC] |
| psk11 | UNVERIFIED | none found |
| driver-private (`ns0000`, `ns0001`) | driver-specific; see below | [OBS] |

Driver-private namespaces observed:
- `http://schemas.microsoft.com/windows/printing/oemdriverpt/Microsoft_Print To PDF/10.0.26100.9711/`
- `http://schemas.microsoft.com/windows/printing/oemdriverpt/Send_to_Microsoft_OneNote_16_Driver_10_0_26100_9711_`
- `http://schemas.microsoft.com/windows/2015/02/printing/printschemakeywords/microsoftprinttopdf`

**Caveats**
- The Learn keyword pages render the psk URI as `https://â€¦`, which looks like a link-rewriting artefact. Real Windows documents use `http://` [OBS].
- The spec says consumers must resolve prefixes from the document's declarations rather than assume them.
- Private namespace URIs embed the driver name and version, so they change with driver updates. Never hard-code them.

### Structure and typing

- Root elements are `<psf:PrintCapabilities version="1">` and `<psf:PrintTicket version="1">` [SPEC][OBS].
- **[OBS]** typing as Windows emits it:
  - QName values (`psf:SelectionType`, `psf:DataType`, `psf:Mandatory`) use `xsi:type="xsd:QName"`.
  - Integers use `xsd:integer`; strings use `xsd:string`.
  - The legacy Learn pages show `xs:string` for QName values, which differs.
- In PrintCapabilities, each option has a `constrained` attribute: `psk:None`, `psk:PrintTicketSettings`, `psk:AdminSettings` or `psk:DeviceSettings`, from least to most restrictive [DOC xml-attributes]. [OBS] `constrained="psk:None"`.
- Options in PrintCapabilities carry `<psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">Letter</psf:Value></psf:Property>` [OBS].

### psk:PageMediaSize (page scope, PickOne)

**Named sizes**
- Options such as `psk:NorthAmericaLetter`, `psk:ISOA4`, `psk:ISOA5`; the full list is on the doc page.
- Each option has ScoredProperties `psk:MediaSizeWidth` and `psk:MediaSizeHeight`, as `xsd:integer` in **microns**, greater than 0 [DOC][SPEC].

**Custom size: option `psk:CustomMediaSize`** [DOC]
- `psk:MediaSizeWidth` takes `ParameterRef psk:PageMediaSizeMediaSizeWidth`.
- `psk:MediaSizeHeight` takes `ParameterRef psk:PageMediaSizeMediaSizeHeight`.
- The two ParameterDefs carry:
  - `psf:DataType` = xs:integer
  - `psf:DefaultValue`, `psf:MinValue`, `psf:MaxValue`: device-defined ("undefined" in the schema), so read them from the device's PrintCapabilities
  - `psf:Multiple` = 1
  - `psf:Mandatory` = psk:Conditional
  - `psf:UnitType` = "microns"
- PostScript variant `psk:PSCustomMediaSize` uses `psk:PageMediaSizePSWidth`, `PSHeight`, `PSWidthOffset`, `PSHeightOffset` and `PSOrientation`.
- Spec 2.0 adds `psk12:Left/Right/Top/BottomImageableMargin` on CustomMediaSize, for PrintDeviceCapabilities only.
- In a ticket, values go in a root-level `<psf:ParameterInit name="â€¦"><psf:Value xsi:type="xsd:integer">â€¦</psf:Value></psf:ParameterInit>` [DOC parameterinit].
- AMWAG values: 3.24 in = **82296** Âµm; 6.70 in = **170180** Âµm.
- For XPS filters: CustomMediaSize is honoured for GPD drivers via these two parameters, and the PageMediaSize option is matched through `*PrintSchemaKeywordMap`, then default mappings, then the option name [DOC supported-printticket-features].

**Forms-database forms in PrintCapabilities**
- [OBS] Microsoft Print To PDF (a v4, GPD-based driver):
  - Standard sizes appear as `psk:*`.
  - Driver forms, which are also present in `HKLM\SYSTEM\CurrentControlSet\Control\Print\Forms`, appear in the private namespace under sanitized names: form "Architecture ASheet" becomes `ns0001:ArchitectureASheet`, and form " 10x15in" becomes `ns0001:_10x15in`. Each has a `psk:DisplayName`.
- [OBS] The OneNote v4 driver lists only its 10 GPD sizes. Forms that exist only in the forms database are not listed.
- [COMMUNITY] Microsoft Q&A 1190652 (IPP class driver): user forms appear as `ns0000:UserForm<N>`, with DisplayName set to the localized "User defined size" rather than the form name.
- How the KX driver (v3, Unidrv-based) exposes a form added with AddForm: **UNVERIFIED**.
- **Rule:** find the AMWAG form by `MediaSizeWidth`/`MediaSizeHeight` (82296 Ã— 170180, allowing for rounding) and/or by DisplayName, never by a hard-coded option name.

### Input bin features

- Three features: `psk:JobInputBin` (job scope), `psk:DocumentInputBin` (document) and `psk:PageInputBin` (page). They share one option model; each driver exposes some subset [DOC].
- **Standard `psk:` options** [DOC]: `psk:AutoSelect`, `psk:Manual` (FeedType Manual), `psk:Cassette` (BinType SheetFeed), `psk:Tractor` (BinType ContinuousFeed), `psk:AutoSheetFeeder`.
- **Scored properties** [DOC][SPEC]:
  - `psk:BinType` (psk:ContinuousFeed | psk:SheetFeed)
  - `psk:FeedType` (psk:Automatic | psk:Manual)
  - `psk:MediaCapacity` (psk:High | psk:Standard)
  - `psk:MediaSizeAutoSense`, `psk:MediaTypeAutoSense`
  - `psk:MediaSheetCapacity` (integer)
  - `psk:MediaPath`
- **Properties:** `psk:FeedFace`, `psk:FeedDirection`.
- **psk12 options** [SPEC]: `psk12:Main`, `Alternate`, `LargeCapacity`, `Envelope`, `Disc`, `Photo`, `Hagaki`, `MainRoll`, `AlternateRoll`, `Top`, `Middle`, `Bottom`, `Side`, `Left`, `Right`, `Center`, `Rear`, **`ByPassTray`**, `Tray1`â€“`Tray20`, `Roll1`â€“`Roll10`. Also ScoredProperties `psk12:MediaSizeLoaded` and `psk12:MediaTypeLoaded`.
- **XPS-filter default mapping, JobInputBin to GPD** [DOC]:

| PrintTicket option | GPD entries |
|---|---|
| Cassette | AUTO, CASSETTE, ENVFEED, ENVMANUAL |
| AutoSelect | FORMSOURCE |
| High | LARGECAPACITY, LARGEFMT, LOWER |
| Manual | MANUAL, MIDDLE, SMALLFMT |
| Tractor | TRACTOR, UPPER |

- The KX "MP tray" option name is **UNVERIFIED**. Select it from the driver's PrintCapabilities by DisplayName and/or `psk:FeedType = psk:Manual`.

### Other features

**psk:PageOrientation** [DOC]
- `psk:Portrait`
- `psk:Landscape`: content rotated 90Â° counter-clockwise
- `psk:ReversePortrait`: 180Â°
- `psk:ReverseLandscape`: 270Â° counter-clockwise
- [OBS] The PDF driver offers only Landscape and Portrait.

**psk:JobDuplexAllDocumentsContiguously** (job scope) [DOC]
- Options `psk:OneSided`, `psk:TwoSidedShortEdge`, `psk:TwoSidedLongEdge`; the two-sided options carry ScoredProperty `psk:DuplexMode`.
- `psk:DocumentDuplex` has the same options at document scope.

**psk:PageScaling** (page scope) [DOC]

| Option | Parameters |
|---|---|
| `psk:None` | none: no scaling |
| `psk:Custom` | `psk:PageScalingOffsetWidth`, `psk:PageScalingOffsetHeight`, `psk:PageScalingScaleWidth`, `psk:PageScalingScaleHeight` |
| `psk:CustomSquare` | ScoredProperties OffsetWidth / OffsetHeight / Scale, referencing `psk:PageScalingOffsetWidth`, `psk:PageScalingOffsetHeight`, `psk:PageScalingScale` |
| `psk:FitApplicationBleedSizeToPageImageableSize` | none |
| `psk:FitApplicationContentSizeToPageImageableSize` | none |
| `psk:FitApplicationMediaSizeToPageImageableSize` | none |
| `psk:FitApplicationMediaSizeToPageMediaSize` | none |

- Sub-feature `psk:ScaleOffsetAlignment` with options Center, TopLeft, and so on.
- ParameterDef `psk:PageScalingScale`: integer, UnitType "percent", MinValue 1, Mandatory Conditional; Default and Max are device-defined.
- For 100% scale, prefer `psk:None`. Whether `CustomSquare` with `PageScalingScale=100` also requires the offset parameters to be initialized is UNVERIFIED.

### Example AMWAG delta ticket

The custom-size fragment and the duplex/scaling fragment were each parsed without a format error by the provider on this machine. Viability depends on the device, so re-read the merged result.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<psf:PrintTicket version="1"
 xmlns:psf="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
 xmlns:psk="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords"
 xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
 <psf:Feature name="psk:PageMediaSize"><psf:Option name="psk:CustomMediaSize">
  <psf:ScoredProperty name="psk:MediaSizeWidth"><psf:ParameterRef name="psk:PageMediaSizeMediaSizeWidth"/></psf:ScoredProperty>
  <psf:ScoredProperty name="psk:MediaSizeHeight"><psf:ParameterRef name="psk:PageMediaSizeMediaSizeHeight"/></psf:ScoredProperty>
 </psf:Option></psf:Feature>
 <psf:ParameterInit name="psk:PageMediaSizeMediaSizeWidth"><psf:Value xsi:type="xsd:integer">82296</psf:Value></psf:ParameterInit>
 <psf:ParameterInit name="psk:PageMediaSizeMediaSizeHeight"><psf:Value xsi:type="xsd:integer">170180</psf:Value></psf:ParameterInit>
 <psf:Feature name="psk:PageOrientation"><psf:Option name="psk:Portrait"/></psf:Feature>
 <psf:Feature name="psk:JobDuplexAllDocumentsContiguously"><psf:Option name="psk:OneSided"/></psf:Feature>
 <psf:Feature name="psk:PageScaling"><psf:Option name="psk:None"/></psf:Feature>
</psf:PrintTicket>
```

### URLs

- https://learn.microsoft.com/en-us/windows/win32/printdocs/ followed by: `pagemediasize`, `pagemediasizemediasizewidth`, `pagemediasizemediasizeheight`, `parameterdef`, `parameterinit`, `parameterref`, `jobinputbin`, `documentinputbin`, `pageinputbin`, `pageorientation`, `jobduplexalldocumentscontiguously`, `documentduplex`, `pagescaling`, `pagescalingscale`, `xml-attributes`, `printschema`
- https://learn.microsoft.com/en-us/windows-hardware/drivers/print/supported-printticket-features
- [COMMUNITY] https://learn.microsoft.com/en-us/answers/questions/1190652/how-to-support-user-customize-page-in-print-suppor

---

## 3. CfgMgr32

### Signatures [SDK cfgmgr32.h]

`CONFIGRET` is `DWORD`; `DEVINST` is `DWORD`; `MAX_DEVICE_ID_LEN` is 200; import library `Cfgmgr32.lib`, DLL `CfgMgr32.dll`.

```c
CONFIGRET CM_Get_Device_ID_List_SizeW(PULONG pulLen, PCWSTR pszFilter /*opt*/, ULONG ulFlags);
CONFIGRET CM_Get_Device_ID_ListW(PCWSTR pszFilter /*opt*/, PZZWSTR Buffer, ULONG BufferLen, ULONG ulFlags);
CONFIGRET CM_Locate_DevNodeW(PDEVINST pdnDevInst, DEVINSTID_W pDeviceID /*opt*/, ULONG ulFlags);
CONFIGRET CM_Get_DevNode_PropertyW(DEVINST dnDevInst, const DEVPROPKEY *PropertyKey, DEVPROPTYPE *PropertyType,
                                   PBYTE PropertyBuffer /*opt*/, PULONG PropertyBufferSize, ULONG ulFlags /*0*/);
CONFIGRET CM_Get_DevNode_Status(PULONG pulStatus, PULONG pulProblemNumber, DEVINST dnDevInst, ULONG ulFlags /*0*/);
CONFIGRET CM_Get_Parent(PDEVINST pdnDevInst, DEVINST dnDevInst, ULONG ulFlags /*0*/);
CONFIGRET CM_Get_Device_IDW(DEVINST dnDevInst, PWSTR Buffer, ULONG BufferLen, ULONG ulFlags);
CONFIGRET CM_Get_Device_Interface_List_SizeW(PULONG pulLen, LPGUID InterfaceClassGuid, DEVINSTID_W pDeviceID /*opt*/, ULONG ulFlags);
CONFIGRET CM_Get_Device_Interface_ListW(LPGUID InterfaceClassGuid, DEVINSTID_W pDeviceID /*opt*/, PZZWSTR Buffer, ULONG BufferLen, ULONG ulFlags);
CONFIGRET CM_Open_Device_Interface_KeyW(LPCWSTR pszDeviceInterface, REGSAM samDesired, REGDISPOSITION Disposition, PHKEY phk, ULONG ulFlags);
CONFIGRET CM_Get_Device_Interface_PropertyW(LPCWSTR pszDeviceInterface, const DEVPROPKEY *PropertyKey, DEVPROPTYPE *PropertyType,
                                            PBYTE PropertyBuffer, PULONG PropertyBufferSize, ULONG ulFlags);
DWORD     CM_MapCrToWin32Err(CONFIGRET CmReturnCode, DWORD DefaultErr);   /* Windows 7+ */
```

### Flags [SDK]

**`CM_GETIDLIST_*`**

| Flag | Value |
|---|---|
| FILTER_NONE | 0x0 |
| FILTER_ENUMERATOR | 0x1 |
| FILTER_SERVICE | 0x2 |
| FILTER_EJECTRELATIONS | 0x4 |
| FILTER_REMOVALRELATIONS | 0x8 |
| FILTER_POWERRELATIONS | 0x10 |
| FILTER_BUSRELATIONS | 0x20 |
| DONOTGENERATE | 0x10000040 |
| FILTER_TRANSPORTRELATIONS | 0x80 (Windows 7+) |
| FILTER_PRESENT | 0x100 (Windows 7+) |
| FILTER_CLASS | 0x200 (Windows 7+) |

**`CM_LOCATE_DEVNODE_*`:** NORMAL 0x0, PHANTOM 0x1, CANCELREMOVE 0x2, NOVALIDATION 0x4. `CM_LOCATE_DEVINST_*` are aliases.

**Other:**
- `CM_GET_DEVICE_INTERFACE_LIST_PRESENT` 0x0, `..._ALL_DEVICES` 0x1.
- `RegDisposition_OpenAlways` 0x0, `RegDisposition_OpenExisting` 0x1.

### CONFIGRET values [SDK]

| Name | Value |
|---|---|
| CR_SUCCESS | 0x00 |
| CR_DEFAULT | 0x01 |
| CR_OUT_OF_MEMORY | 0x02 |
| CR_INVALID_POINTER | 0x03 |
| CR_INVALID_FLAG | 0x04 |
| CR_INVALID_DEVNODE (= CR_INVALID_DEVINST) | 0x05 |
| **CR_NO_SUCH_DEVNODE (= CR_NO_SUCH_DEVINST)** | **0x0D** |
| CR_FAILURE | 0x13 |
| **CR_BUFFER_SMALL** | **0x1A** |
| CR_REGISTRY_ERROR | 0x1D |
| CR_INVALID_DEVICE_ID | 0x1E |
| CR_INVALID_DATA | 0x1F |
| CR_DEVICE_NOT_THERE | 0x24 |
| **CR_NO_SUCH_VALUE** | **0x25** |
| CR_WRONG_TYPE | 0x26 |
| CR_NO_SUCH_REGISTRY_KEY | 0x2E |
| CR_ACCESS_DENIED | 0x33 |
| CR_CALL_NOT_IMPLEMENTED | 0x34 |
| CR_INVALID_PROPERTY | 0x35 |
| CR_NO_SUCH_DEVICE_INTERFACE | 0x37 |

### Semantics and caveats

**Device ID lists** [DOC]
- Lengths are in **characters**. The output is a double-NUL-terminated list.
- The size from `_Size` may be larger than needed.
- The filter format is `Enumerator[\DeviceID]`.
- `FILTER_SERVICE` **creates a devnode** for the service unless `DONOTGENERATE` is also set. Do not use it in read-only code.

**[OBS] list behaviour**
- `ENUMERATOR|PRESENT` with `"SWD\PRINTENUM"` returned 3 IDs.
- With `"USBPRINT"`, `"WSDPRINT"`, `"LPTENUM"` or `"NOSUCHENUMERATOR"` (none of which have devices here), it returned **CR_SUCCESS, length 1, empty list**. A missing enumerator is not an error.

**Locating devnodes**
- `CM_Locate_DevNodeW` with NULL or an empty ID returns the root devnode [DOC].
- [OBS] For a non-present devnode: NORMAL returns CR_NO_SUCH_DEVNODE (0x0D); PHANTOM returns CR_SUCCESS.

**Properties**
- [DOC] Pass `PropertyBuffer = NULL` with `*PropertyBufferSize = 0` to query the size.
- [OBS] That query returns CR_BUFFER_SMALL, the required byte count, **and** the type.
- [OBS] A missing property returns CR_NO_SUCH_VALUE. Example: `BusReportedDeviceDesc` on an SWD devnode.

**Phantom devnodes [OBS]**
- `CM_Get_DevNode_Status` returns CR_NO_SUCH_DEVINST, and the output variables are not written.
- `DEVPKEY_Device_ProblemCode` returns CR_NO_SUCH_VALUE.
- `DEVPKEY_Device_IsPresent` is 0x00.
- `CM_Get_Parent` succeeds.
- Use `DEVPKEY_Device_IsPresent` for presence checks.
- [DOC devpkey-device-parent] On Windows 8+, `DEVPKEY_Device_Parent` of a non-present device is its last parent if that still exists, otherwise the root.

**Other observations**
- [OBS] Instance-ID case varies between APIs (`SWD\PRINTENUM\PRINTQUEUES` in lists, `SWD\PRINTENUM\PrintQueues` in `Parent`). Compare instance IDs case-insensitively.
- [OBS] On print-queue devnodes (`SWD\PRINTENUM\{guid}`), `DriverDesc` = "Local Print Queue" and `DriverVersion` = "10.0.26100.1" come from `printqueue.inf`, **not** the printer driver.
- On USBPRINT/WSDPRINT devnodes these keys should describe the printer driver's INF. UNVERIFIED (no such device here).

### DEVPROPKEY

`struct { GUID fmtid; ULONG pid; }`: sizeof 20, fmtid at offset 0, pid at offset 16 [OBS compiled].

| Key | fmtid | pid | Type |
|---|---|---|---|
| DEVPKEY_Device_DeviceDesc | {a45c254e-df1c-4efd-8020-67d146a850e0} | 2 | STRING |
| DEVPKEY_Device_HardwareIds | same | 3 | STRING_LIST |
| DEVPKEY_Device_CompatibleIds | same | 4 | STRING_LIST |
| DEVPKEY_Device_Class | same | 9 | STRING |
| DEVPKEY_Device_ClassGuid | same | 10 | GUID |
| DEVPKEY_Device_Manufacturer | same | 13 | STRING |
| DEVPKEY_Device_FriendlyName | same | 14 | STRING |
| DEVPKEY_Device_InstanceId | {78c34fc8-104a-4aca-9ea4-524d52996e57} | 256 | STRING |
| DEVPKEY_Device_DevNodeStatus | {4340a6c5-93fa-4706-972c-7b648008a5a7} | 2 | UINT32 |
| DEVPKEY_Device_ProblemCode | same | 3 | UINT32 per header and [OBS 0x7]; the doc says INT32 |
| DEVPKEY_Device_Parent | same | 8 | STRING |
| DEVPKEY_Device_BusReportedDeviceDesc | {540b947e-8b40-45bc-a8a2-6a0b894cbda2} | 4 | STRING |
| DEVPKEY_Device_IsPresent | same | 5 | BOOLEAN |
| DEVPKEY_Device_DriverVersion | {a8b865dd-2e3d-4094-ad97-e593a70c75d6} | 3 | STRING |
| DEVPKEY_Device_DriverDesc | same | 4 | STRING |

Also useful [SDK]:
- `DEVPKEY_Device_ContainerId` {8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c},2 (GUID)
- `Service` ,6 Â· `Driver` ,11 Â· `EnumeratorName` ,24 Â· `LocationPaths` ,37 (fmtid a45c254eâ€¦)
- `Children` {4340a6c5â€¦},9
- `DriverInfPath` ,5 Â· `MatchingDeviceId` ,8 Â· `DriverProvider` ,9 (fmtid a8b865ddâ€¦)
- `DEVPKEY_NAME` {b725f130-47ef-101a-a5f1-02608c9eebac},10

### DEVPROPTYPE [SDK and compiled]

| Type | Value |
|---|---|
| EMPTY | 0x0 |
| NULL | 0x1 |
| INT32 | 0x6 |
| UINT32 | 0x7 |
| GUID | 0xD |
| FILETIME | 0x10 |
| BOOLEAN | 0x11 |
| STRING | 0x12 |
| **STRING_LIST** | **0x2012** (TYPEMOD_LIST 0x2000 \| STRING) |
| BINARY | 0x1003 (TYPEMOD_ARRAY 0x1000 \| BYTE) |
| ERROR | 0x17 |
| NTSTATUS | 0x18 |
| STRING_INDIRECT | 0x19 |

- `DEVPROP_BOOLEAN` is a 1-byte `CHAR`: TRUE = -1 (0xFF), FALSE = 0 [DOC][OBS].
- STRING_LIST is a REG_MULTI_SZ of UTF-16 strings [DOC].

### Device enumerators relevant to printers

**Documented [DOC]**
- USB printer: the USB bus creates `USB\VID_vvvv&PID_pppp[&MI_nn]\â€¦` with compatible ID `USB\Class_07`. usbprint.sys loads on it and creates a child PDO under `Enum\USBPRINT`, with hardware IDs of the form `USBPRINT\<Mfg><Model><crc>` and `<Mfg><Model><crc>` (printer-connected-to-a-usb-port).
- PnP ID markers in printer INFs: `LPTENUM` (parallel), `USBPRINT` (USB), `DOT4PRT` (Dot4), `BTHPRINT` (Bluetooth), `WSDPRINT` (WSD), `1394` (plug-and-play-ids-for-printer-inf-files).
- Parallel example from the docs: `LPTENUM\OEM_Company_NameXYZ_F84F`.

**Observed [OBS]**
- Print queues: `SWD\PRINTENUM\{guid}`, under root `SWD\PRINTENUM\PrintQueues`. Class `PrintQueue`, GUID {1ed2bbf9-11f0-4084-b21f-ad83a8e6dcdc} (= `GUID_DEVCLASS_PRINTQUEUE` [SDK devguid.h]). Hardware IDs `PRINTENUM\{084f01fa-e634-4d77-83ee-074817c03581}`, `PRINTENUM\LocalPrintQueue`.
- WSD discovery (a non-present Kyocera ECOSYS P6235cdn on this machine):
  - Container `SWD\DAFWSDProvider\uuid:<id>` (class SoftwareDevice) with per-service children `â€¦/uri:<id>/<Service>`.
  - The printer service devnode `â€¦/WSDPrinter` has class `WSDPrintDevice` {c30ecea0-11ef-4ef9-b02e-6af81e6e65c0} (not in SDK headers) and hardware ID `UMB\VEN_0103&DEV_0C55`.
  - The `WSDPRINT\â€¦` printer devnode beneath it could not be observed.
- Printer class: `GUID_DEVCLASS_PRINTER` {4d36e979-e325-11ce-bfc1-08002be10318} [SDK].
- IPP discovery enumerators: UNVERIFIED.

### Print-queue device interface [OBS]

- Interface class {0ecef634-6ef0-472a-8085-5ad023ecbccd} (not defined in SDK headers; treat as UNVERIFIED as a public constant).
- Properties found on it, readable with `CM_Get_Device_Interface_PropertyW`. PROPERTYKEY and DEVPROPKEY share the same layout.

| Property [SDK propkey.h] | Key | Observed value |
|---|---|---|
| `PKEY_DeviceInterface_PrinterName` | {0A7B84EF-0C27-463F-84EF-06C5070001BE},10 | queue name |
| **`PKEY_DeviceInterface_PrinterPortName`** | **{EEC7B761-6F94-41B1-949F-C729720DD13C},12** | "PORTPROMPT:", "nul:" |
| `PKEY_DeviceInterface_PrinterDriverName` | {AFC47170-14F5-498C-8F30-B0D19BE449C6},11 | driver name |
| `PKEY_DeviceInterface_PrinterDriverDirectory` | {847C66DE-B8D6-4AF9-ABC3-6F4F926BC039},14 | driver directory |

- `PrinterPortName` is documented ("the port where the printer is located") at https://learn.microsoft.com/en-us/windows/win32/properties/props-system-deviceinterface-printerportname.

### URLs

- https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/ followed by: `nf-cfgmgr32-cm_get_device_id_list_sizew`, `nf-cfgmgr32-cm_get_device_id_listw`, `nf-cfgmgr32-cm_locate_devnodew`, `nf-cfgmgr32-cm_get_devnode_propertyw`, `nf-cfgmgr32-cm_get_devnode_status`, `nf-cfgmgr32-cm_get_parent`, `nf-cfgmgr32-cm_get_device_interface_listw`, `nf-cfgmgr32-cm_open_device_interface_keyw`, `nf-cfgmgr32-cm_get_device_interface_propertyw`
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/ followed by: `devpkey-device-problemcode`, `devpkey-device-parent`, `devpkey-device-busreporteddevicedesc`, `devpkey-device-driverversion`, `property-data-type-identifiers`, `devprop-type-boolean`, `devprop-type-string-list`
- https://learn.microsoft.com/en-us/windows-hardware/drivers/print/printer-connected-to-a-usb-port
- https://learn.microsoft.com/en-us/windows-hardware/drivers/print/plug-and-play-ids-for-printer-inf-files

---

## 4. USB printer port mapping (USBnnn to device)

### Constants [SDK usbprint.h]

- `GUID_DEVINTERFACE_USBPRINT` = {28D78FAD-5A12-11D1-AE5B-0000F803A8C2}.
- `GUID_DEVINTERFACE_IPPUSB_PRINT` = {F2F40381-F46D-4E51-BCE7-62DE6CF2D098} (IPP over USB).
- The header also defines `IOCTL_USBPRINT_SET_PORT_NUMBER`, commented as setting the port number for an interface.

### Registry location

**[COMMUNITY]** Microsoft Q&A 552590, a Windows 10 dump posted by a user (not Microsoft-documented):
- Interface key: `HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses\{28d78fad-5a12-11d1-ae5b-0000f803a8c2}\##?#USB#VID_08A6&PID_8053&MI_00#6&27e1620&1&0000#{28d78fad-â€¦}`
  - `DeviceInstance` (REG_SZ) = `USB\VID_08A6&PID_8053&MI_00\6&27e1620&1&0000`. The interface sits on the **USB** devnode, which is the usbprint.sys function device; the `USBPRINT\â€¦` devnode is its child.
- Subkey `â€¦\#\Device Parameters`:
  - `"Port Number"` REG_DWORD, e.g. 1
  - `"Base Name"` REG_SZ, e.g. "USB"
  - `"Port Description"` REG_SZ
  - `"Serial Number"` REG_SZ
  - `"Interface Number"` REG_DWORD
- The poster said some of these values were missing on a Windows 11 preview build.

### Documented way to read it

- Enumerate interfaces with `CM_Get_Device_Interface_ListW(&GUID_DEVINTERFACE_USBPRINT, NULL, â€¦, CM_GET_DEVICE_INTERFACE_LIST_PRESENT)`.
- Open each one with `CM_Open_Device_Interface_KeyW(path, KEY_READ, RegDisposition_OpenExisting, &hk, 0)`.
- [OBS] On print-queue interfaces, this call opens exactly the interface's `#\Device Parameters` key: one value (`FriendlyName`), no subkeys. Do not use `RegDisposition_OpenAlways`, which creates the key.

### Port names

- **[DOC, MS-RPRN 2.2.4.10, product note <220>]** The Windows port-name grammar is `USB_PORT = "USB" 1#DIGIT ":"`.
- The UI and EnumPorts show `USB001`. The rule that the name is Base Name plus a three-digit zero-padded Port Number is **UNVERIFIED** (community knowledge).
- Remote Desktop redirected "TSâ€¦" ports reportedly use the same interface GUID. **UNVERIFIED** (seen only in search results). Filter on `Base Name == "USB"`.

### Mapping chain

1. Queue to port: `PRINTER_INFO_2.pPortName`, or `PKEY_DeviceInterface_PrinterPortName` on the queue interface.
2. Port to interface: the USBPRINT interface whose Device Parameters match. Community-based.
3. Interface to device: the interface's `DeviceInstance` gives the USB devnode.
4. Its child `USBPRINT\â€¦` devnode carries the hardware IDs and the 1284 model.

USB monitor registry [OBS]: `HKLM\SYSTEM\CurrentControlSet\Control\Print\Monitors\USB Monitor`, `Driver`=`usbmon.dll`, with subkey `IppUsbAltIndex`.

Nothing in this section could be observed on this machine; no USBPRINT interfaces exist (list length 1, empty).

URLs:
- https://learn.microsoft.com/en-us/windows-hardware/drivers/print/programming-considerations-for-usbprint
- https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/03c5feb7-4fbb-4569-b064-9eb6ad833f50 (note <220> is in the appendix page e81cbc09-ab05-4a32-ae4a-8ec57b436c43)
- [COMMUNITY] https://learn.microsoft.com/en-us/answers/questions/552590/facing-an-issue-regarding-usbprint-port-creation-a

---

## 5. Standard TCP/IP and WSD port storage

### Monitor registrations [OBS]

Under `HKLM\SYSTEM\CurrentControlSet\Control\Print\Monitors\<name>`, value `Driver` (REG_SZ):

| Monitor | Driver | Notes |
|---|---|---|
| Standard TCP/IP Port | tcpmon.dll | |
| USB Monitor | usbmon.dll | |
| WSD Port | **APMon.dll** on 10.0.26300 | Learn still names it WSDMON/wsdmon.dll. Subkeys `Adapters\IPP`, `Adapters\WSPrint\OfflinePorts`, `Ports` (empty here) |
| Local Port | localspl.dll | |
| Appmon | AppMon.dll | |
| Virtual Port Monitor | VirtualMon.dll | |

### TCP/IP global values [OBS]

Under `â€¦\Standard TCP/IP Port\Ports`, all REG_DWORD (units UNVERIFIED):

| Value | Observed |
|---|---|
| LprAckTimeout | 0xb4 (180) |
| StatusUpdateEnabled | 1 |
| StatusUpdateInterval | 0xa (10) |
| TcpAckTimeout | 0x12c (300) |

### TCP/IP per-port values [COMMUNITY]

A 2009 registry export (iwan-it-admin-tips blog). Not Microsoft-documented, and **UNVERIFIED on current Windows** because this machine has no TCP/IP ports.

Key: `â€¦\Standard TCP/IP Port\Ports\<PortName>`:

| Value | Type | Meaning / example |
|---|---|---|
| Protocol | REG_DWORD | 1 = RAW, 2 = LPR |
| Version | REG_DWORD | 1 |
| HostName | REG_SZ | |
| IPAddress | REG_SZ | |
| HWAddress | REG_SZ | |
| PortNumber | REG_DWORD | 0x238c = 9100, 0x203 = 515 |
| SNMP Community | REG_SZ | "public" |
| SNMP Enabled | REG_DWORD | |
| SNMP Index | REG_DWORD | |
| Queue | REG_SZ | LPR only |
| Double Spool | REG_DWORD | LPR only |

### Supported read path [DOC]

1. `OpenPrinterW(L",XcvPort <PortName>", &hXcv, &defaults)`.
2. `XcvDataW(hXcv, L"GetConfigInfo", (PBYTE)&CONFIG_INFO_DATA_1{dwVersion=1}, sizeof, (PBYTE)&PORT_DATA_1, sizeof, &cbNeeded, &dwStatus)`.
3. A TRUE return means only that the data was delivered. Success is `dwStatus == NO_ERROR`.

Other single-value commands: `L"HostAddress"` and `L"IPAddress"`. AddPort, ConfigPort and DeletePort need SERVER_ACCESS_ADMINISTER. Minimum access for GetConfigInfo is UNVERIFIED.

**Prototype [SDK]:** `BOOL XcvDataW(HANDLE, PCWSTR pszDataName, PBYTE pInputData, DWORD cbInputData, PBYTE pOutputData, DWORD cbOutputData, PDWORD pcbOutputNeeded, PDWORD pdwStatus);`

**[SDK tcpxcv.h] constants and layouts**
- RAWTCP = PROTOCOL_RAWTCP_TYPE = 1; LPR = PROTOCOL_LPR_TYPE = 2.
- MAX_PORTNAME_LEN 64, MAX_NETWORKNAME_LEN 49, MAX_SNMP_COMMUNITY_STR_LEN 33, MAX_QUEUENAME_LEN 33, MAX_IPADDR_STR_LEN 16.
- [OBS x64] Sizes: `PORT_DATA_1` 964, `PORT_DATA_2` 1068, `CONFIG_INFO_DATA_1` 132 (`dwVersion` at offset 128).
- [OBS x64] `PORT_DATA_1` offsets:

| Field | Offset |
|---|---|
| dwVersion | 128 |
| dwProtocol | 132 |
| sztHostAddress | 144 |
| sztIPAddress | 378 |
| dwPortNumber | 952 |
| dwSNMPEnabled | 956 |
| dwSNMPDevIndex | 960 |

- Default RAW port is 9100 [DOC troubleshoot article].

### WSD

- The `WSD-<GUID>` port naming and the per-port registry storage are **UNVERIFIED**. MS-RPRN does not specify WSD port names.
- **[DOC]** WSDMON XcvData commands for identification via an `",XcvPort <name>"` handle:

| Command | Returns |
|---|---|
| `DeviceID` | WSD DeviceID = PKEY_PNPX_GlobalIdentity |
| `PnPXID` | PKEY_PNPX_ID |
| `ServiceID` | PKEY_PNPX_ServiceID |
| `BackupPort` | WSD_BACKUP_PORT_DATA |
| `CleanupPort` | removes the port |
| `ResetCommunication` | |

- The TCPMON AddPort, DeletePort and MonitorUI commands are not supported by WSDMON.
- **Enumeration** [DOC]: `EnumPorts` level 2 returns `PORT_INFO_2 { LPWSTR pPortName; LPWSTR pMonitorName; LPWSTR pDescription; DWORD fPortType; DWORD Reserved; }`. Use `pMonitorName` ("Standard TCP/IP Port", "WSD Port", "USB Monitor") to classify ports. The WSD monitor name is localized per a Microsoft KB, so do not compare it to an English literal.

URLs:
- https://learn.microsoft.com/en-us/windows-hardware/drivers/print/tcpmon-xcv-interface
- https://learn.microsoft.com/en-us/windows-hardware/drivers/print/tcpmon-xcv-commands
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/tcpxcv/ns-tcpxcv-_port_data_1
- https://learn.microsoft.com/en-us/windows-hardware/drivers/print/wsdmon-port-monitor
- https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rprn/e1760a13-634e-4a7f-a7dd-9d02e4048f9b
- https://learn.microsoft.com/en-us/windows/win32/printdocs/enumports and https://learn.microsoft.com/en-us/windows/win32/printdocs/port-info-2
- https://learn.microsoft.com/en-us/troubleshoot/windows-server/printing/standard-port-monitor-for-tcpip
- [COMMUNITY] http://iwan-it-admin-tips.blogspot.com/2009/01/registry-key-for-tcp-ip-printer-port.html

---

## 6. Authenticode and catalog verification

### WinVerifyTrust

- **[SDK]** `LONG WINAPI WinVerifyTrust(HWND hwnd, GUID *pgActionID, LPVOID pWVTData);` (Wintrust.lib / Wintrust.dll).
- Success is exactly 0. The return value is a LONG; do not test it with SUCCEEDED/FAILED [DOC][SDK comment].
- `hwnd = INVALID_HANDLE_VALUE` means no interactive user.
- **Action GUIDs [SDK SoftPub.h, compiled]**

| GUID | Value |
|---|---|
| `WINTRUST_ACTION_GENERIC_VERIFY_V2` | {00AAC56B-CD44-11D0-8CC2-00C04FC295EE} |
| `DRIVER_ACTION_VERIFY` | {F750E6C3-38EE-11D1-85E5-00C04FC295EE} |
| `WINTRUST_ACTION_GENERIC_CHAIN_VERIFY` | {FC451C16-AC75-11D1-B4B8-00C04FB66EA0} |

### Structure layouts (x64) [OBS compiled]

**`WINTRUST_DATA`**: sizeof **88**; `cbStruct = sizeof`.

| Field | Offset |
|---|---|
| cbStruct | 0 |
| pPolicyCallbackData | 8 |
| pSIPClientData | 16 |
| dwUIChoice | 24 |
| fdwRevocationChecks | 28 |
| dwUnionChoice | 32 |
| union pFile / pCatalog / pBlob / pSgnr / pCert / pDetachedSig | 40 |
| dwStateAction | 48 |
| hWVTStateData | 56 |
| pwszURLReference | 64 |
| dwProvFlags | 72 |
| dwUIContext | 76 |
| pSignatureSettings | 80 (present when NTDDI_VERSION >= WIN8) |

**`WINTRUST_FILE_INFO`**: sizeof **32**. cbStruct 0, pcwszFilePath 8, hFile 16, pgKnownSubject 24.

**`WINTRUST_CATALOG_INFO`**: sizeof **72**.

| Field | Offset |
|---|---|
| cbStruct | 0 |
| dwCatalogVersion | 4 |
| pcwszCatalogFilePath | 8 |
| pcwszMemberTag | 16 |
| pcwszMemberFilePath | 24 |
| hMemberFile | 32 |
| pbCalculatedFileHash | 40 |
| cbCalculatedFileHash | 48 |
| pcCatalogContext | 56 |
| hCatAdmin | 64 (Windows 8+; required for non-SHA1 hashes) |

**`WINTRUST_SIGNATURE_SETTINGS`**: sizeof 32.

### Constants [SDK and DOC]

| Field | Values |
|---|---|
| dwUIChoice | WTD_UI_ALL 1, **WTD_UI_NONE 2**, WTD_UI_NOBAD 3, WTD_UI_NOGOOD 4 |
| fdwRevocationChecks | WTD_REVOKE_NONE 0, WTD_REVOKE_WHOLECHAIN 1 |
| dwUnionChoice | **WTD_CHOICE_FILE 1**, WTD_CHOICE_CATALOG 2, BLOB 3, SIGNER 4, CERT 5, DETACHED_SIG 6 |
| dwStateAction | IGNORE 0, **VERIFY 1**, **CLOSE 2**, AUTO_CACHE 3, AUTO_CACHE_FLUSH 4 |
| dwUIContext | EXECUTE 0, INSTALL 1 |

- Every VERIFY call must be followed by a CLOSE call with the same structure [DOC].

**dwProvFlags**

| Flag | Value | Note |
|---|---|---|
| WTD_USE_IE4_TRUST_FLAG | 0x1 | |
| WTD_NO_IE4_CHAIN_FLAG | 0x2 | |
| WTD_NO_POLICY_USAGE_FLAG | 0x4 | |
| WTD_USE_LOCAL_MACHINE_CERTS | 0x8 | |
| **WTD_REVOCATION_CHECK_NONE** | 0x10 | |
| WTD_REVOCATION_CHECK_END_CERT | 0x20 | |
| WTD_REVOCATION_CHECK_CHAIN | 0x40 | |
| WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT | 0x80 | |
| WTD_SAFER_FLAG | 0x100 | not supported |
| WTD_HASH_ONLY_FLAG | 0x200 | |
| WTD_USE_DEFAULT_OSVER_CHECK | 0x400 | |
| WTD_LIFETIME_SIGNING_FLAG | 0x800 | |
| **WTD_CACHE_ONLY_URL_RETRIEVAL** | 0x1000 | doc: needed to guarantee no network retrieval, combined with WTD_REVOKE_NONE |
| WTD_DISABLE_MD2_MD4 | 0x2000 | |
| WTD_MOTW | 0x4000 | |
| WTD_CODE_INTEGRITY_DRIVER_MODE | 0x8000 | |

### Return codes [SDK winerror.h]

| Name | Value | Message (paraphrased) |
|---|---|---|
| TRUST_E_NOSIGNATURE | 0x800B0100 | no signature present |
| TRUST_E_SUBJECT_NOT_TRUSTED | 0x800B0004 | subject not trusted for the action |
| CERT_E_UNTRUSTEDROOT | 0x800B0109 | chain ends in an untrusted root |
| TRUST_E_EXPLICIT_DISTRUST | 0x800B0111 | certificate explicitly marked untrusted |
| CRYPT_E_SECURITY_SETTINGS | 0x80092026 | blocked by a local security option |
| TRUST_E_BAD_DIGEST | 0x80096010 | signature did not verify |
| TRUST_E_SUBJECT_FORM_UNKNOWN | 0x800B0003 | subject form not supported |
| TRUST_E_PROVIDER_UNKNOWN | 0x800B0001 | |
| TRUST_E_ACTION_UNKNOWN | 0x800B0002 | |
| CERT_E_CHAINING | 0x800B010A | |
| CERT_E_EXPIRED | 0x800B0101 | |
| CERT_E_REVOKED | 0x800B010C | |
| CERT_E_REVOCATION_FAILURE | 0x800B010E | |
| CERT_E_UNTRUSTEDTESTROOT | 0x800B010D | |
| CERT_E_WRONG_USAGE | 0x800B0110 | |
| TRUST_E_TIME_STAMP | 0x80096005 | |
| TRUST_E_FAIL | 0x800B010B | |

### Observed results [OBS]

| Test | Result |
|---|---|
| `.cat` file as `WTD_CHOICE_FILE`, GENERIC_VERIFY_V2 | **0** |
| same, DRIVER_ACTION_VERIFY | 0 |
| `.inf` as `WTD_CHOICE_FILE` | **0x800B0003** (flat INF has no embedded signature; verify INFs through their catalog) |
| **Tampered** `.cat` (one byte of a member digest flipped) as FILE | **0x800B0100**, not BAD_DIGEST |
| Catalog member check (WTD_CHOICE_CATALOG), correct SHA-1 tag | 0, with both actions |
| Same, non-member file | 0x800B0100 |
| Same, wrong hash algorithm (SHA-256 against a SHA-1 catalog) | 0x800B0100 |
| Same, catalog's stored digest tampered | **0x80096010** |

- `Get-AuthenticodeSignature` on a `.cat` reported Valid; on the tampered copy it reported NotSigned.

### .NET `X509Certificate.CreateFromSignedFile(string)`

- **[DOC]** Marked `[Obsolete]` SYSLIB0057 in .NET 9/10; the doc has no remarks about validation.
- **[OBS] .NET 10.0.12 (and .NET Framework 4.8):**
  - Works on `.cat` files and returns the signer leaf (`CN=Microsoft Windowsâ€¦`).
  - **Also returns the certificate for the tampered `.cat`.** It extracts the certificate only and does **not** verify the signature or trust.
  - On an unsigned INF and on catalog-signed `notepad.exe` it throws `CryptographicException` 0x80092009 (CRYPT_E_NO_MATCH).
- Use it only for display. Use WinVerifyTrust for the trust decision.

### Catalog membership APIs [SDK mscat.h]

Wintrust.dll. The docs say some have no import library and recommend LoadLibrary/GetProcAddress; [OBS] all resolved via GetProcAddress.

```c
BOOL   CryptCATAdminAcquireContext2(HCATADMIN *phCatAdmin, const GUID *pgSubsystem /*opt*/, PCWSTR pwszHashAlgorithm /*opt*/,
                                    PCCERT_STRONG_SIGN_PARA pStrongHashPolicy /*opt*/, DWORD dwFlags /*0*/);   /* Win8+ */
BOOL   CryptCATAdminCalcHashFromFileHandle2(HCATADMIN hCatAdmin, HANDLE hFile, DWORD *pcbHash, BYTE *pbHash /*opt*/, DWORD dwFlags /*0*/);
BOOL   CryptCATAdminReleaseContext(HCATADMIN hCatAdmin, DWORD dwFlags /*0*/);
HANDLE CryptCATOpen(LPWSTR pwszFileName, DWORD fdwOpenFlags, HCRYPTPROV hProv /*opt*/, DWORD dwPublicVersion /*opt*/, DWORD dwEncodingType /*opt*/);
CRYPTCATMEMBER *CryptCATGetMemberInfo(HANDLE hCatalog, LPWSTR pwszReferenceTag);
CRYPTCATMEMBER *CryptCATEnumerateMember(HANDLE hCatalog, CRYPTCATMEMBER *pPrevMember);
BOOL   CryptCATClose(HANDLE hCatalog);
```

**Constants [SDK]**
- `CRYPTCAT_OPEN_CREATENEW` 0x1, `CRYPTCAT_OPEN_ALWAYS` 0x2, `CRYPTCAT_OPEN_EXISTING` 0x4.
- `CRYPTCAT_VERSION_1` 0x100, `CRYPTCAT_VERSION_2` 0x200.
- `CRYPTCAT_MAX_MEMBERTAG` 128 (64 for builds up to WIN10_RS1).
- `szOID_OIWSEC_sha1` "1.3.14.3.2.26"; `szOID_NIST_sha256` "2.16.840.1.101.3.4.2.1".
- `BCRYPT_SHA1_ALGORITHM` L"SHA1"; `BCRYPT_SHA256_ALGORITHM` L"SHA256".

**Semantics**
- `CryptCATOpen` returns **INVALID_HANDLE_VALUE** on failure, not NULL [DOC].
- An `dwEncodingType` of 0 means PKCS_7 | X509 ASN encoding.
- Do not free the pointer returned by `CryptCATGetMemberInfo` [DOC].
- [OBS] An existing catalog opened fine with `dwPublicVersion` 0, 0x100 or 0x200.

**Layouts (x64) [OBS]**
- `CRYPTCATMEMBER`, sizeof 104: cbStruct 0, pwszReferenceTag 8, pwszFileName 16, gSubjectType 24, fdwMemberFlags 40, pIndirectData 48, dwCertVersion 56, dwReserved 60, hReserved 64, sEncodedIndirectData 72, sEncodedMemberInfo 88.
- `SIP_INDIRECT_DATA`, sizeof 64: Data 0, DigestAlgorithm 24 (pszObjId at 24), Digest 48 (cbData at 48, pbData at 56).

**Observed tag facts [OBS]**
- Member tags are **uppercase** hex of the member hash.
- `CryptCATGetMemberInfo` is **case-sensitive**: an uppercase tag was found, the lowercase form was not.
- For a flat file (INF), the catalog hash equals the plain SHA-1 or SHA-256 of the file bytes (matched `Get-FileHash`). PE files use the Authenticode PE hash instead, which is not the plain file hash.
- The inbox catalog `prnms003.cat` stores **SHA-1** members (digest OID 1.3.14.3.2.26, 20 bytes) even on 10.0.26300.
- `CryptCATAdminAcquireContext2` with a NULL algorithm returned a SHA-1 (20-byte) context. The doc says the default may change, so do not rely on it.

### Recommended sequence

1. Verify the catalog: WinVerifyTrust with FILE, GENERIC_VERIFY_V2, `WTD_UI_NONE`, `WTD_REVOKE_NONE` plus `WTD_CACHE_ONLY_URL_RETRIEVAL` (or allow revocation online), VERIFY then CLOSE. Expect 0.
2. Read the member digest OID with `CryptCATOpen(cat, CRYPTCAT_OPEN_EXISTING, 0, 0, 0)` and `CryptCATEnumerateMember` (`pIndirectData->DigestAlgorithm.pszObjId`), then pick "SHA1" or "SHA256".
3. `CryptCATAdminAcquireContext2(&h, &DRIVER_ACTION_VERIFY, alg, NULL, 0)`.
4. Open the INF with `GENERIC_READ` and `FILE_SHARE_READ`. Call `CryptCATAdminCalcHashFromFileHandle2` twice: once for the size, once for the hash.
5. Build the uppercase hex tag.
6. Optionally check `CryptCATGetMemberInfo(hCat, tag) != NULL` (membership only).
7. Call WinVerifyTrust with `WTD_CHOICE_CATALOG`, filling `WINTRUST_CATALOG_INFO` with catalog path, tag, member path, member handle, hash, hash size and `hCatAdmin`. Expect 0.
8. Clean up: WTD_STATEACTION_CLOSE, `CryptCATClose`, `CryptCATAdminReleaseContext`, `CloseHandle`.

URLs:
- https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust
- https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_data
- https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_file_info
- https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_catalog_info
- https://learn.microsoft.com/en-us/windows/win32/api/mscat/ followed by: `nf-mscat-cryptcatadminacquirecontext2`, `nf-mscat-cryptcatadmincalchashfromfilehandle2`, `nf-mscat-cryptcatopen`, `nf-mscat-cryptcatgetmemberinfo`, `nf-mscat-cryptcatenumeratemember`, `ns-mscat-cryptcatmember`
- https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509certificate.createfromsignedfile?view=net-10.0

---

## 7. Default DEVMODE precedence (per-user vs global)

### Documented [DOC]

- `PRINTER_INFO_8` holds the **global** default printer settings. The global defaults are set by the printer's administrator.
- `PRINTER_INFO_9` holds the **per-user** default settings. Its DEVMODE is stored in the user's registry. Both structures contain only `LPDEVMODE pDevMode` (8 bytes on x64).
- Per-User DEVMODE page:
  - The per-user DEVMODE is set by calling `SetPrinter` with `PRINTER_INFO_2` **or** `PRINTER_INFO_9`.
  - Calling `SetPrinter` with `PRINTER_INFO_8` **resets the per-user DEVMODE to the global default**.
  - Which user's per-user entry is reset is not stated; assume the caller's (UNVERIFIED).
- `SetPrinter`:
  - Levels 0 and 2 through 9 are valid.
  - Validate a DEVMODE with `DocumentProperties` before setting it as a default.
  - `PRINTER_ACCESS_ADMINISTER` (0x4) is the right for administrative SetPrinter tasks.
  - Required access for level 9 specifically is UNVERIFIED.
- `GetPrinter`:
  - `pDevMode` in levels 2, 8 and 9 can be NULL.
  - Since Vista, data for printers hosted on a print server may come from a local cache.
- `DocumentProperties`:
  - `fMode = 0` returns the full size needed (DEVMODE plus driver extra).
  - `DM_OUT_BUFFER` writes the driver's "current print settings".
  - Without `DM_IN_BUFFER`, the system uses the printer's default DEVMODE.
  - Constants [SDK]: DM_UPDATE/DM_OUT_DEFAULT 1, DM_COPY/DM_OUT_BUFFER 2, DM_PROMPT/DM_IN_PROMPT 4, DM_MODIFY/DM_IN_BUFFER 8.
- `PTConvertPrintTicketToDevMode(kUserDefaultDevmode)` falls back to the queue default when no user default exists (section 1).

### Observed [OBS] (Microsoft Print to PDF, no per-user DEVMODE present)

- `GetPrinterW` level 9: `pDevMode = NULL` (`cbNeeded` = 8).
- Level 2 and level 8 returned identical DEVMODEs: Letter, `dmPaperSize` 1, `dmFields` 0x2D03.
- `GetPrinter` level 8 works with only `PRINTER_ACCESS_USE`.
- `DocumentPropertiesW(fMode=0)` returned 5428, which is 220 + `dmDriverExtra` 5208.
- `DocumentPropertiesW(DM_OUT_BUFFER, no input)` returned IDOK with the same settings. The driver added DM_FORMNAME (0x10000) to `dmFields`.
- Registry locations:
  - Global default: `HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers\<queue>\Default DevMode` (REG_BINARY).
  - `HKCU\Printers` has subkeys `Connections`, `ConvertUserDevModesCount` (REG_DWORD per printer), `Defaults\{GUID}` (REG_SZ default value = a printer name), `DevModePerUser` (empty here) and `DevModes2` (empty here). The semantics of `ConvertUserDevModesCount` and `Defaults\{GUID}` are UNVERIFIED.

### Community knowledge, not Microsoft-documented [COMMUNITY]

ThinPrint blog:
- Local-queue Printing Preferences are stored in `HKCU\Printers\DevModePerUser\<queue name>`.
- For connections, they are stored in `HKCU\Printers\Connections\,,<server>,<queue>\DevMode`.
- Preferences start equal to the Printing Defaults. Once a user saves preferences, the global defaults stop applying to that user.
- `DevModes2` is named only in community posts; its purpose is UNVERIFIED.

### UNVERIFIED (not tested, to avoid modifying settings)

- That `DocumentProperties(DM_OUT_BUFFER)` and `GetPrinter` level 2 return the per-user DEVMODE when one exists.

### Implication

- Setting only the global default (level 8, or level 2 as an administrator) does not override a per-user DEVMODE that the user saved earlier.
- Level 9, or the level 8 reset, acts on the **calling** user's HKCU (see section 8 for the over-the-shoulder consequence).

URLs: https://learn.microsoft.com/en-us/windows/win32/printdocs/ followed by `per-user-devmode`, `printer-info-8`, `printer-info-9`, `setprinter`, `getprinter`, `documentproperties`, `openprinter`; [COMMUNITY] https://www.thinprint.com/en/blog/printing-defaults-vs-printing-preferences-vs-printing-properties

---

## 8. UAC, elevation and named pipes

### ShellExecuteExW

- **[SDK]** `BOOL ShellExecuteExW(SHELLEXECUTEINFOW *pExecInfo);` (Shell32.lib / Shell32.dll).
- **`SHELLEXECUTEINFOW` (x64) [OBS]**: sizeof **112**.

| Field | Offset |
|---|---|
| cbSize | 0 |
| fMask | 4 |
| hwnd | 8 |
| lpVerb | 16 |
| lpFile | 24 |
| lpParameters | 32 |
| lpDirectory | 40 |
| nShow | 48 |
| hInstApp | 56 |
| lpIDList | 64 |
| lpClass | 72 |
| hkeyClass | 80 |
| dwHotKey | 88 |
| hIcon / hMonitor union | 96 |
| hProcess | 104 |

- **Self-elevation:** set `lpVerb = L"runas"`, which launches as administrator with a UAC consent or credential prompt [DOC], and set `fMask |= SEE_MASK_NOCLOSEPROCESS (0x40)` so that `hProcess` is returned.
- Other masks: `SEE_MASK_NOASYNC` 0x100 (needed when the caller has no message loop or exits soon), `SEE_MASK_FLAG_NO_UI` 0x400 (does not suppress security prompts). `SW_SHOWNORMAL` = 1.
- The docs advise `CoInitializeEx(NULL, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE)` before calling.
- **Failure:** FALSE with `GetLastError()`. The doc lists `ERROR_CANCELLED` (1223) for a prompt the user cancelled.
  - The specific "UAC declined gives 1223" mapping is widely relied on, but I did not reproduce it because it needs a UAC prompt: UNVERIFIED-by-test.
  - In .NET, `Process.Start` with `UseShellExecute=true` and `Verb="runas"` surfaces this as `Win32Exception` with `NativeErrorCode` 1223. UNVERIFIED-by-test.
- **[DOC] .NET:** `ProcessStartInfo.UseShellExecute` defaults to **false** on .NET Core / .NET 5+ (true on .NET Framework). It must be true for the verb to take effect.
- **[DOC]** `CreateProcess` on a `requireAdministrator` executable does not elevate; it fails with `ERROR_ELEVATION_REQUIRED` (740).

### Detecting elevation

- **[SDK]** `BOOL GetTokenInformation(HANDLE TokenHandle, TOKEN_INFORMATION_CLASS cls, LPVOID buf, DWORD cb, PDWORD pcbReturned);` (Advapi32), on `OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, â€¦)`.
- `TOKEN_INFORMATION_CLASS` [compiled]: TokenUser 1, **TokenElevationType 18**, TokenLinkedToken 19, **TokenElevation 20**, TokenIntegrityLevel 25.
- `TOKEN_ELEVATION { DWORD TokenIsElevated; }` (4 bytes): nonzero means elevated [DOC].
- `TOKEN_ELEVATION_TYPE` (4 bytes): Default 1 (no linked token), Full 2 (elevated), Limited 3 (filtered).
- Integrity RIDs [SDK]: LOW 0x1000, MEDIUM 0x2000, MEDIUM_PLUS 0x2100, HIGH 0x3000, SYSTEM 0x4000.
- [OBS] This non-elevated process: TokenIsElevated 0, type 3 (Limited), integrity 0x2000.
- Use `TokenIsElevated` as the yes/no test. A type of Default does not imply "not elevated", for example when UAC is off.

### Named pipe: can a high-integrity client of the same user open a pipe created by a medium-integrity server with default security?

**Answer: yes, by documented rules and an observed security descriptor. Not run end to end, because that would need an elevated process.**

- **[SDK]** `CreateNamedPipeW(LPCWSTR, DWORD dwOpenMode, DWORD dwPipeMode, DWORD nMaxInstances, DWORD nOutBufferSize, DWORD nInBufferSize, DWORD nDefaultTimeOut, LPSECURITY_ATTRIBUTES /*opt*/)`.
- **[DOC]** With NULL security attributes, the default security descriptor gives full control to LocalSystem, Administrators and the creator owner, and read access to Everyone and Anonymous.
- **[OBS]** Default SD of a pipe created with NULL security attributes by this medium-integrity process:
  `O:<user>G:<user>D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;<user SID>)(A;;FR;;;WD)(A;;FR;;;AN)`
  It has **no explicit mandatory label**, so it carries the implicit medium label.
- **[DOC]** Mandatory Integrity Control (MIC):
  - Objects are created with `SYSTEM_MANDATORY_LABEL_NO_WRITE_UP`, which restricts only **lower**-integrity subjects.
  - Objects without a label are treated as medium.
  - A high-integrity client is therefore not blocked by MIC, and its DACL match is the user SID and/or Administrators, both granting full access.
- **Reverse direction** (elevated server, medium client): the DACL depends on the elevated token's defaults. UNVERIFIED. Set an explicit SD and label if you need it.

**Hardening [DOC, constants compiled]**
- Pass an explicit SD; the default gives read to Everyone and Anonymous.
- `PIPE_REJECT_REMOTE_CLIENTS` (0x8).
- `FILE_FLAG_FIRST_PIPE_INSTANCE` (0x00080000): a second creator gets `ERROR_ACCESS_DENIED`, which defends against pre-creation squatting.
- The doc suggests the logon SID to keep other sessions out.

**Client impersonation level [DOC]**
- Set in `CreateFileW` with `SECURITY_SQOS_PRESENT` (0x00100000) plus `SECURITY_IDENTIFICATION` (0x00010000), or `SECURITY_ANONYMOUS` (0).
- The default is impersonation level (`SECURITY_IMPERSONATION` 0x00020000).
- `ImpersonateNamedPipeClient` succeeds if the requested level is below impersonation, the caller has SeImpersonatePrivilege, the token came from LogonUser, **or the identity is the same as the caller**. A medium-integrity server of the same user can therefore impersonate the elevated client unless the client restricts the level.
- Whether Windows downgrades impersonation of a higher-integrity token: UNVERIFIED.

**Server-side identity**
- `GetNamedPipeClientProcessId(HANDLE, PULONG)` [SDK] can be compared with the PID of the child launched via `SEE_MASK_NOCLOSEPROCESS`.

### Over-the-shoulder elevation (credentials of a different administrator)

- **[DOC]** The UAC game-developer article says that "a process with elevated privileges runs as the administrative user who entered credentials" (learn.microsoft.com/windows/win32/dxtecharts/user-account-control-for-game-developers).
- **[DOC] same article:** the elevated process gets **that administrator's** profile folders and HKCU, as do its child processes. When the logged-on administrator only clicks consent, the locations are the same user's. Elevated code should not operate on per-user data.

**Consequences for AMWAG**
- In the elevated worker, `SetPrinter` level 9 and HKCU printer state (`DevModePerUser`, `Connections`, the per-user default printer) hit the **administrator's** hive, not the logged-on user's. Do per-user steps in the non-elevated process.
- The pipe still opens for a different administrator, because Administrators has full control in the default DACL. A server check of "client SID == my SID" would reject it.
- Whether the over-the-shoulder token shares the logged-on session's logon SID is UNVERIFIED, so a logon-SID-only DACL may block over-the-shoulder clients.

URLs:
- https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecuteexw and https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-shellexecuteinfow
- https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.useshellexecute?view=net-10.0
- https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-gettokeninformation
- https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-token_elevation_type and https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-token_elevation
- https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights
- https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-createnamedpipew
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew
- https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-impersonatenamedpipeclient
- https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control and https://learn.microsoft.com/en-us/windows/win32/secauthz/impersonation-levels
- https://learn.microsoft.com/en-us/previous-versions/dotnet/articles/bb625963(v=msdn.10)
- https://learn.microsoft.com/en-us/windows/win32/dxtecharts/user-account-control-for-game-developers
- https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works

---

## UNVERIFIED items to resolve on target hardware

- How the KX v3 driver names AddForm forms in PrintCapabilities, and its MP-tray option QName, CustomMediaSize support and min/max (section 2).
- `psk11` namespace URI.
- USB "Port Number"/"Base Name" semantics and the `USB%03u` formatting; Windows 11 presence of these values; TS ports sharing the GUID (section 4).
- TCP/IP per-port registry value names and types on current Windows; WSD port naming and storage (section 5).
- `DocumentProperties` / `GetPrinter` level 2 behaviour when a per-user DEVMODE exists; required access for `SetPrinter` level 9; whose per-user entry a level-8 reset clears (section 7).
- UAC decline mapping to 1223 (and the .NET `Win32Exception` 1223); impersonation-level downgrade across integrity levels; over-the-shoulder logon SID (section 8).
- Symbolic meaning of 0x80040001 from `PTOpenProvider(version 2)` (section 1).