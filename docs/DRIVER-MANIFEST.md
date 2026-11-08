# Approved driver manifest — research record

Verified 2026-11-03. Every value below was obtained by downloading the official packages from the TA Triumph-Adler
website, computing hashes, and reading the INF/CAT files. Each package was then re-downloaded and re-checked by an
independent second pass, which reproduced every hash and INF line exactly. Nothing was installed or executed. The
machine-readable manifest is `config/amwag-printer-config.json` (`driverPackages`, `models`). Raw research data:
`docs/research/`.

## Packages

| | KXv4 6.1.1603 (PCL6 driver) — **preferred** | KX 8.7.0917 — approved alternative |
|---|---|---|
| Official name | KX XPS Universal Print Driver (KXv4) | KX Universal Print PCL/PostScript |
| URL | `https://www.triumph-adler.com/caas/v1/ta/media/103622/data/02a1879a81ae6c73a9753c16e9ab05aa/Kxv4_v6.1.1603_TA_RC7_WHQL.zip` | `https://www.triumph-adler.com/caas/v1/ta/media/102720/data/04e63668c3001927fbccf7c5c22a6cc3/Kx87_UPD_8.7.0917.zip` |
| File / size | `Kxv4_v6.1.1603_TA_RC7_WHQL.zip`, 155 412 996 bytes | `Kx87_UPD_8.7.0917.zip`, 203 308 603 bytes |
| SHA-256 | `2226F16A2C5A8EC24B16266B24F8E73160A6C9CEFC2ACF46EFD2FFE922EA273A` | `862A5367A365F0FF61365BAF0D229A0F761E6F64EFE8013E8FF6DA8C2C109E48` |
| Driver model | Windows v4 (GPD-based XPSDrv; PCL XL output via Microsoft filter) | Kyocera v3 vendor GDI driver (no GPD/PPD; Kyocera XML/MDX model data) |
| x64 INF | `.../PrnDrv/PCL Driver/64bit/win81 and newer/prnkycl1.inf` | `Kx87_UPD_8.7.0917/64bit/OEMSETUP.INF` |
| DriverVer | 04/03/2023, 6.1.1603.0 | 09/17/2026, 8.7.0917.0 |
| x64 catalog signer | **Microsoft Windows Hardware Compatibility Publisher (WHQL)**, timestamped | **KYOCERA Document Solutions Inc.** (Authenticode via DigiCert), timestamped, not WHQL |
| Silent install | Yes | Only if Kyocera's certificate (thumbprint `AA2098F3E70AC791261FF95930AE4C981B28C0D3`) is in LocalMachine *Trusted Publishers*; otherwise Windows refuses a silent install (expected `0x800F0242`) |
| Listed for | Windows 8/10/11, Server 2012–2022 (catalog valid for Windows 10.0) | Windows 10/11, Server 2016–2025 |

Both URLs are linked, with identical media ids, on the TA product pages of all three models and on the UTAX pages.
The web server publishes no checksum (chunked transfer, ETag is not a content hash); integrity rests on the SHA-256
pinned here plus the catalog signature check.

## Model coverage (x64)

| Model | KXv4 driver names | KX 8.7 driver name | INF hardware IDs |
|---|---|---|---|
| P-4030DN | `P-4030DN v4 KX (PCL6)`, `P-4030DN v4 KX (XPS)` | `P-4030DN KX` | `USBPRINT\`, `WSDPRINT\`, `LPTENUM\UTAX_TAP-4030DN1652` |
| P-4531DN | `P-4531DN v4 KX (PCL6)`, `P-4531DN v4 KX (XPS)` | `P-4531DN KX` | `USBPRINT\`, `WSDPRINT\UTAX_TAP-4531DND6CF` (+ `LPTENUM\` in KXv4) |
| P-5032DN | `P-5032DN v4 KX (PCL6)`, `P-5032DN v4 KX (XPS)` | `P-5032DN KX` | `USBPRINT\`, `WSDPRINT\TA_UTAXP-5032DNEED0` |

Neighbouring models with similar names (P-4030D, P-4030 MFP, P-4530DN, P-4531 MFP, P-4532DN, P-5031DN, P-5532DN)
have their own distinct entries; identification never relies on partial names.

### Does the KX Universal Print Driver support all three models?

Yes. KX 8.7.0917 contains exact, model-specific entries for all three models in its x64 INF
(`[TA/UTAX.NTamd64.6.0]`, one driver per model, page description language selected inside the driver), and every
file each model installs (46 per model) verifies against the package catalog. It also contains a model-independent
"Universal Printing System (TA/UTAX)" entry, which this tool does not use. The obstacle is not coverage but signing:
its x64 catalog is vendor-signed, so unattended installation needs IT to trust Kyocera's publisher certificate first.

### Why KXv4 PCL6 is preferred

* WHQL-signed: Windows installs it without a publisher-trust prompt and without changing any trust store.
* The PCL6 variant avoids a defect in the XPS variant's P-5032DN manifest (its PrinterDriverID does not match the
  INF) and does not depend on printer-side XPS support.
* Tray data verified from the driver itself: the multipurpose tray is the GPD option `Manual` ("MP Tray"),
  `dmDefaultSource = 4` (DMBIN_MANUAL); custom sizes 70 × 148 mm up to 216 × 356 mm (P-4030DN) or 216 × 915 mm,
  which contains the 83.058 × 170.18 mm ticket. Every cassette's minimum width is larger than the ticket.
* Existing queues already using `P-xxxx KX` (v3) or `P-xxxx v4 KX (XPS)` are accepted as correct and reused.

### Switching a model to KX 8.7

1. Deploy the Kyocera code-signing certificate (thumbprint above; extract it from the package's
   `64bit\utax.cat`) to *Trusted Publishers* on branch PCs through Group Policy. This is a security decision for IT:
   it trusts that exact Kyocera certificate for driver installation on those PCs.
2. In `models[]` set `preferredDriverName` to `P-xxxx KX` and `driverPackageId` to `ta-kx-8.7.0917`.
3. Re-run the hardware acceptance checklist.

## Points that need real hardware

* The Plug and Play IDs the printers actually report. KX's own installer database lists a second ID for some
  P-5032DN units (`UTAX_TAP-5032DNC673`), so the manifest also accepts IEEE 1284 MFG `UTAX_TA` for that model.
* SNMP model strings (`P-4030DN` etc. via the Kyocera model OID, hrDeviceDescr) and the IEEE 1284 device ID.
* Whether each driver lists the `AMWAG Ticket` form and keeps the MP tray, 100 % and single-sided settings —
  the tool checks this at run time and reports any rejection instead of assuming success.
* P-5032DN drivers default to two-sided printing; the tool always sets single-sided and verifies it.

## Platform notes (Microsoft, as of 2026-11-03)

* Since July 2026, Windows Plug and Play prefers the inbox IPP class driver for IPP-capable devices. The tool
  installs the approved driver explicitly and treats a queue with another driver as needing repair.
* Windows protected print mode (Windows 11 24H2+, opt-in) removes and blocks all third-party drivers; the tool
  detects it and stops with a clear message.
* From July 2027 third-party drivers receive security fixes only, but manufacturer installation packages remain
  installable.
