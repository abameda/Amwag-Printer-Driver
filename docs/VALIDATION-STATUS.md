# AMWAG Printer Setup — validation status

Status of version 1.0.0 on 2026-11-04. Five levels are kept apart on purpose:

| Level | Meaning |
|---|---|
| **Implemented** | The behaviour exists in the source code. |
| **Automatically tested** | Unit and scenario tests prove it against in-memory fakes of the spooler, Plug and Play, network, driver store and file system. A fake can only be as right as its model of Windows and of the printers. |
| **Verified on Windows (read-only)** | Tests that call the real Windows APIs on the development PC, without changing its printing configuration. |
| **Physically verified** | Checked on a real TA Triumph-Adler printer with real ticket stock. |
| **Requires manual testing** | Needs a real printer, a second account or a branch PC: see `HARDWARE-ACCEPTANCE-CHECKLIST.md`. |

**Nothing has been physically verified yet.** No TA Triumph-Adler printer was available during development: no
ticket was printed, no TA driver was installed, and the `AMWAG Ticket` form was never created on a real PC. Simulated
printers and Windows accepting a print job never count as physical verification.

## Test totals

`dotnet test AmwagPrinterSetup.sln --filter "Category!=Screenshots"`: **4,524 tests, all passing** (Core 643,
Windows 513, App 3,368; build with 0 warnings; the same in Release through `build.ps1`).

* 129 of them (`Category=WindowsIntegration`: 85 in Windows.Tests, 44 in App.Tests) call real Windows APIs read-only.
* A further 41 `Category=Screenshots` tests render every screen in English and Arabic for design review.
* No test changes this PC's printers, drivers, forms, ports, services or `%ProgramData%`; tests that need a protected
  folder or a junction create them under `%TEMP%`.

Development PC: Windows 11 Home Single Language, build 26300, x64; .NET SDK 10.0.401 (runtime 10.0.12).

## Status by area

| Area | Implemented | Automatically tested | Verified on Windows (read-only) | Requires manual testing |
|---|---|---|---|---|
| **Detection**: USB Plug and Play hardware IDs, IEEE 1284 IDs, existing queues, WSD queues, SNMP identity of requested addresses and existing TCP/IP queue hosts (bounded by `connection.maxNetworkProbes`); no subnet scanning; queues pooled without a USB port or on an LPR or IPP port never configured and their printers not set up automatically (a pool with a USB port stays the USB printer's queue and is configured for it; a class-driver LPR or IPP queue blocks its printer only as its ticket queue); no second queue on a port another queue already prints to | Yes | Yes (detection, model matcher, network evidence, shipped-configuration scenarios) | PnP enumeration incl. not-connected USB nodes; SNMP/TCP probing against loopback test agents; spooler queue, port and driver enumeration | Real P-4030DN/P-4531DN/P-5032DN over USB and network, SNMP on and off (checklist 1, 6, 17) |
| **Exact identification**: hardware IDs, 1284 MFG/MDL, Kyocera model name; administrator-set printer name never used; unsupported or other-vendor devices refused | Yes | Yes | — | Real firmware strings of all three models (checklist 6) |
| **Selection**: one printer automatically only when unambiguous; with a configured address only the printer there (an address the operator types counts for the run), and its queues on other spellings a choice also when one needs its model decided; selection screen, also before a model question when several printers need one; optional IP entry, also after declining the printers offered in Setup; IT's model refused when the printer contradicts it, also in Repair without a queue; earlier USB units left out; queues that could not be matched to their printer, that the probe limit left unchecked, or that are already set up as the ticket printer but lead to no printer the setup configures, keep a single printer from being chosen without asking (and `modelId` from choosing one when their printer may be of that model); silent runs that stop name how to decide; Setup, Repair and Diagnostics use the same choice, and Diagnostics leads with the ticket printer's own findings, ends its step list on it and offers Repair only where Repair can change something | Yes | Yes (selection, resolve, flow and window tests) | — | Checklist 7, 9, 17, 18, 21, 22, 24 |
| **One printer, several spellings** (DNS name / IP / IPv4-mapped / IPv6 with same serial): a requested spelling reuses the printer's queue and port; queues on two spellings stay separate choices | Yes | Yes | — | Checklist 16 |
| **Several queues on one printer**: the queue already set up for tickets is the one configured; the others keep their settings; a run that had to choose one by driver and name, or that added a queue next to another entry that may print to the same printer, keeps that warning and ends ready with warnings (silent: exit 1) | Yes | Yes | — | Checklist 6, 7 (second queue on the same printer), 26 |
| **Approved driver manifest**, offline archive and approved online download, size + SHA-256, catalog signature and signer allow-list, reuse of a correct installed driver | Yes | Yes (download, extraction, provider, policy tests) | WinTrust catalog checks on Windows' own catalogs; protected extraction folders with real ACLs under `%TEMP%` | Install of KXv4 6.1.1603 on Windows 10 and 11, online and offline; tampered archive (checklist 1, 10, 11) |
| **USB device binding** (`UpdateDriverForPlugAndPlayDevices`), queue creation, Plug and Play queue adoption | Yes | Yes | — | Checklist 1 and 2; the IPP class-driver case |
| **Ticket form** created once in micrometres; **defaults** portrait, simplex, 100 %, MP tray, validated by the driver and read back; paper-size fields that override the form detected | Yes | Yes (assessment, apply, DEVMODE, verification tests) | Forms and DEVMODE reading, validation that persists nothing, `DeviceCapabilities`, PrintTicket conversion — all on Microsoft Print to PDF | The TA driver's behaviour with the custom form and MP tray (checklist 2, 3, 5, 13) |
| **Per-user preferences** corrected as the employee, without a second UAC prompt | Yes | Yes | Reading per-user DEVMODE | Checklist 5 |
| **Setup / Repair / Diagnostics**; Repair changes only the ticket printer; Diagnostics changes nothing | Yes | Yes (Diagnostics proven write-free against a recording fake) | — | Checklist 4, 5, 7, 12, 14 |
| **Idempotence**: no duplicate queues, ports or forms | Yes | Yes | — | Checklist 12 |
| **Elevation**: window runs as the employee; one elevated worker only when needed; restricted one-way pipe that each side checks (client and server process id); heartbeat and bounded writes; closing the window stops at an action boundary and reads back | Yes | Yes (broker, pipe, worker, flow and window tests with scripted workers) | Real named pipes: the DACL, the client and server process-id checks, a foreign server process, a stalled reader, heartbeats and a closed window | One UAC prompt with consent and with another administrator's credentials; closing during a driver install (checklist 19, section 4) |
| **Configuration trust**: files next to the exe and `branch.json` used only when only administrators can change them; worker refuses other bytes than the window loaded | Yes | Yes | Owner/ACL/junction checks on real files and folders | USB stick (FAT/exFAT), Desktop copy and read-only share (checklist 20) |
| **Silent IT mode** with documented exit codes and reports | Yes | Yes (exit-code mapping, silent interaction, worker) | — | Intune/Configuration Manager run as SYSTEM (section 4) |
| **UI**: one click, six steps, English and Arabic (right-to-left), no console windows | Yes | Yes (window smoke tests, localization completeness) | Real WinForms windows; 41 rendered screenshots in both languages | Usability with employees on branch PCs |
| **Print Test Ticket** only on request; physical result confirmed by the employee; never reported as printed from the spooler alone | Yes | Yes (layout, job monitor, printer tests) | Job listing on the PDF queue | Checklist 15 and section 2 (measured ticket) |
| **Ticket software check** | Yes | Yes | — | Section 3 of the checklist |
| **Logs and reports** (JSON lines, text and JSON report) | Yes | Yes | — | — |
| **Build and packaging**: `build.ps1`, self-contained single-file win-x64 exe, offline driver re-check, SHA256SUMS | Yes | Build and tests run by `build.ps1` | Published and inspected on this PC (see below) | Signing with the company certificate |

## The published exe

Checked on the development PC with the published `AMWAG Printer Setup.exe` (self-contained, single file):

* Its imported DLLs are Windows system DLLs and API sets; its only delay-loaded DLL (`VERSION.dll`) is restricted to
  `System32` by the exe's dependent-load flags, and every P/Invoke of the tool is restricted to `System32`.
* It contains no native libraries to unpack, so nothing is extracted to `%TEMP%`.
* A satellite DLL placed next to a single-file test exe of the same .NET version was not loaded.
* So from a folder an employee can change, the only thing they could swap is the exe itself: sign it, and prefer a
  folder only administrators can change (DEPLOYMENT.md §2, step 6).
* On 2026-11-04 the published exe (47.4 MB, not signed) ran `--silent --mode diagnostics --report <path>` on this PC,
  which has no TA printer, as a standard user: exit 3 (*No supported TA Triumph-Adler printer was found*) in about two
  seconds, a complete text and JSON report, the log under `%LOCALAPPDATA%\AMWAG\PrinterSetup\Logs`, nothing unpacked
  to `%TEMP%`, and the PC's printers, ports and drivers the same before and after.

## Decisions and actions for IT

1. **Ticket size: done.** Measured and confirmed as 3.27 × 6.70 in (83.058 × 170.18 mm) on 2026-11-04. If the stock
   ever changes, update `ticket` in the configuration and rebuild; an unconfirmed size blocks Setup and Repair.
2. **Driver choice.** The preferred KXv4 6.1.1603 PCL6 driver is WHQL-signed and installs silently. KX 8.7.0917 is
   approved but installs silently only after IT deploys Kyocera's publisher certificate to *Trusted Publishers* —
   a security decision for IT (DRIVER-MANIFEST.md).
3. **Sign the exe** with the company code-signing certificate (recommended).
4. **Set the MP tray paper size to *Custom*** on each printer's control panel (software cannot change it).
5. **Run the physical acceptance checklist** on all three models, on Windows 10 and Windows 11, before distributing.

## Known limitations

* Network printers that Windows has never seen are found only by address (`branch.json`, `--address` or the
  operator's entry); there is no subnet scanning.
* Network probes are limited by `connection.maxNetworkProbes` (8): the configured addresses first, then the hosts of
  existing queues. A class-driver queue whose host the limit cuts is named in the report (*Printer not checked*), and
  no printer is then taken as the ticket printer without asking; raise the limit or name the ticket queue's host.
* The IP addresses that pooled, LPR and IPP queues print to are not probed (only their host names are). A
  class-driver pool to another vendor's printers is therefore reported as a queue left unchanged, and the ticket
  printer is offered rather than chosen (silent: exit 4), also with `modelId` (the pool's printers may be of any model),
  until the pool is deleted or the ticket printer is named with `printerAddress`.
* When a stale `modelId` names an earlier USB unit of another model whose port the connected unit's pooled queue also
  lists, Setup is blocked (exit 8, the queue left unchanged is named) while Repair and Diagnostics still point to
  Setup: correct `modelId`.
* A WSD queue's network address is not known, so a requested address next to a WSD queue of the same model is only
  warned about as a possible duplicate.
* A queue on a printer's IPv6 address and the printer's IPv4 address are recognized as one printer only when both
  report the same serial number and exact model; otherwise Setup by the IPv4 address adds its own queue, without a
  duplicate warning.
* `--address`/`--model` cannot choose between two connected USB printers of the same model.
* When the approved driver is already installed but its package cannot be obtained, the queue is configured with the
  installed driver and the USB device keeps its current driver (a warning); Repair with the package available links it.
* Applications can override printer defaults per job (TICKET-SOFTWARE-INTEGRATION.md); per-user preferences of other
  Windows accounts are corrected only when those users run the tool.
* Windows protected print mode blocks vendor drivers.
* Windows accepting a print job does not prove a ticket printed; the employee confirms the test ticket.
