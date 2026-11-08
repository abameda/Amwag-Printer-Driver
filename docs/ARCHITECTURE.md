# AMWAG Printer Setup — Architecture

## Decision summary

| Decision | Choice | Why |
|---|---|---|
| Language/runtime | C# on .NET 10 (LTS) | First-class access to the documented Win32 print APIs through P/Invoke; long-term support; strong tooling and testability. |
| UI | WinForms, code-built, Per-Monitor-V2 DPI aware | Lightweight native Windows UI, built-in right-to-left mirroring for Arabic, no extra runtime (WinUI would need the Windows App SDK). |
| Packaging | Self-contained, single-file `win-x64` executable | Branch PCs need no .NET, Python or PowerShell modules; one file (plus the editable config and optional offline drivers) copies from a share or USB stick. |
| System changes | Documented Win32 APIs only (winspool, prntvpt, CfgMgr32, newdev, WinTrust) | No hidden PowerShell, no console windows, precise error codes, and DEVMODE-level control that `Set-PrintConfiguration` does not offer. |
| Privileges | `asInvoker` UI + elevated worker started on demand | Detection and diagnostics never need administrator rights; one UAC prompt covers all machine-wide steps; per-user preferences are written as the signed-in employee. |

Alternatives considered: .NET Framework 4.8 (tiny exe, but older runtime and libraries), WPF/WinUI (heavier,
WinUI needs an extra runtime), PowerShell + PrintManagement cmdlets (visible consoles, execution-policy issues,
no DEVMODE validation, weak error reporting).

## Projects

```
src/Amwag.PrinterSetup.Core      Hardware-independent logic: configuration + validation, model identification,
                                 assessment/planning, idempotent apply, verification, reports. No Windows calls.
src/Amwag.PrinterSetup.Windows   Real implementations of the Core abstractions: spooler (winspool + prntvpt),
                                 Plug and Play inventory (CfgMgr32 + USB port map), platform/policy inspection,
                                 network identification (TCP connect + SNMP GET), driver packages (download,
                                 SHA-256, WinTrust, protected extraction) and package-aware driver installation.
src/Amwag.PrinterSetup.App       WinForms UI (English/Arabic), command line, elevation broker and elevated worker,
                                 silent mode for IT deployment tools, test-ticket printing and job monitoring.
tests/*                          Unit and scenario tests with in-memory fakes (Testing), interop layout tests and
                                 read-only Windows integration tests.
config/amwag-printer-config.json The approved deployment configuration and driver manifest.
```

The Core talks to the system only through `IPrintSpooler`, `IDeviceInventory`, `IPlatformInspector`,
`INetworkPrinterProbe`, `IDriverPackageProvider` and `IDriverInstaller`. Each has a Windows implementation and an
in-memory fake, which is how all acceptance scenarios are tested without printers.

## Run sequence

```
Employee starts AMWAG Printer Setup.exe (standard user token)
 1 Detect   read-only snapshot: spooler, queues, ports, drivers, forms, PnP devices, platform policy,
            SNMP identity of requested/relevant network addresses -> candidates with match confidence.
            Addresses beyond connection.maxNetworkProbes are not probed, and every address that is cut is
            passed to the assembler. A queue that names no model on a single Standard TCP/IP port, whose
            host was cut and that leads to no probed spelling of a printer, is reported as "was not checked
            because the limit of network probes was reached; it may be the ticket printer's queue"
            (NetworkIdentityUnavailable warning) and listed in DetectionResult.UncheckedQueues.
 2 Select   with a configured address (an address the operator entered in this run, else --address, else
            branch.json printerAddress) only the printer there: asked to confirm or name its model when it
            cannot report it exactly, unless an existing queue with an approved model-specific driver
            identifies it (DriverOnly); its queues on several spellings are a choice when the address is not
            one of those spellings, also when one of them needs its model decided; when nothing usable is
            there, nothing (silent: exit 3; the window asks for an address). Without one: the
            branch.json/--model model's printer when no other printer may be of that model, else the one
            eligible printer when no other printer needs a decision and it is not of another model than
            configured; otherwise ask (several printers / probable network match / unknown network printer /
            IP entry). Several printers whose model must be confirmed are a choice first, and only the chosen
            one is confirmed. An operator who declines the printers offered in Setup may enter the ticket
            printer's address. IT's model (--model --confirm-model) that the printer contradicts stops the run
            with nothing changed (silent: exit 4), also in Repair for a printer without a queue. A silent run
            that stops for a choice names how to decide it. A USB printer that is not
            connected is left out while a USB printer of the same model is connected (an earlier unit).
            Repair and Diagnostics work on the same single ticket printer: the printer passed by the Repair
            buttons, else the printer Setup would choose (Repair: when it has a queue; a model to confirm is
            confirmed first; IT's model applies as in Setup), or among several the only queue already set up
            with the ticket paper (one pooled queue counts once; among printers whose model must be
            confirmed, only that one is asked about; when a rejected or unchecked queue is on the ticket
            paper as well, Repair asks). An earlier USB unit whose queue alone has the ticket paper is
            offered again (Setup, or deleting that queue, resolves it); Repair never chooses between
            printers silently. Diagnostics names every printer Setup would offer and leads its result with
            the ticket printer's own findings.
 3 Assess   read-only: driver, device binding, port, queue, form, driver media support, paper source,
            global and per-user defaults -> ordered plan + blockers
 4 Machine  elevated worker (same exe, --worker), one UAC prompt: driver package -> driver -> USB binding
   steps    -> port -> queue -> form -> global defaults. The worker re-detects and re-assesses on its own;
            it trusts only the selected target key, never a plan from the unelevated process, and refuses
            to apply configuration bytes other than those the window loaded (SHA-256 on its command line).
 5 User     back in the employee's process: per-user printing preferences (SetPrinter level 9)
   steps
 6 Verify   fresh reads in the employee's process; the worker's claims are never trusted. Also after a
            refused, failed or stopped machine step, the report shows the real state. A refusal that
            blocks the run (the configuration changed, the worker found a blocker) changed nothing and is
            not read back: the report shows the state detected before the step.
 7 Ready    summary of the state the run ended in (once actions ran, the plans' findings about the state
            before them are left out; a package that failed its integrity or signature check, or an invalid
            configuration, comes first, then for a failed run the failure that ended it; in Diagnostics the
            headline and warnings use only the ticket printer's findings when it is known), report, Print Test
            Ticket. Repair is offered only for a printer with a queue. In Diagnostics without a single ticket
            printer it is offered only when a printer Repair would ask about has a local queue, and never on
            a computer an environment blocker stops. After a Repair that found printers or a printer without
            a queue, or no printer at all, the window offers Run Setup. After a model nobody confirmed, or at
            the configured address (nothing usable there, or a queue left unchanged there), it offers Try
            again but no Run Setup.
```

Progress flows from the worker to the UI through a one-way named pipe (output only; the worker accepts no
commands from it). Only the current user, Administrators and SYSTEM can open it, and the window keeps only the client
whose process id is the started worker's. The worker in turn writes only to a pipe server in the window's process
(`--window-pid`, checked with GetNamedPipeServerProcessId), so a pipe name freed by a stopped window cannot be taken
over, and a write the window does not read within 30 s counts as the window being gone. The window's only stop signal
is closing its end. The worker writes a heartbeat every 2 s, so it notices even while an action blocks; it stops at the
next action boundary (finished actions stay, the rest are skipped) and exits with code 9. The window waits for that, at
most the apply-lock timeout plus one minute, before verifying. A worker that cannot reach the window changes nothing;
one that ends before connecting is reported as not started. A machine-wide mutex serializes system changes between
concurrent runs.

The worker compares the configuration bytes with the window's pin before checking anything that depends on them, such
as the target's model. Other bytes, bytes that no longer load, or a configuration without the target's model give a
findings-only refusal (`ConfigurationChanged` / `ConfigurationInvalid`, exit 6): the run is blocked, and the window asks
for a restart instead of offering Try again. The Repair buttons pass only the printer's key, plus the model when the
operator or IT asserted it, so Repair resolves the printer's current identity. Closing again while a run stops keeps
waiting; an elevated window, which runs the machine steps itself, never exits during a run, also not at Windows
shutdown.

## Identification rules

A printer is configured without questions only when its identity is exact or already established:

* **Exact** — a Plug and Play hardware ID (USB/WSD) equals an ID from the approved INF, or the IEEE 1284
  manufacturer/model reported over USB or SNMP equals the INF's MFG/MDL pair, or (over SNMP, without a 1284 ID) an
  approved network model name is the device's own model name: kcprtGeneralModelName, or hrDeviceDescr of a device
  whose sysObjectID is under the Kyocera enterprise 1.3.6.1.4.1.1347. The same name in sysDescr or any other text is
  only Probable.
* **DriverOnly** — an existing queue already uses an approved, model-specific driver and the device names no model of
  its own (not readable, or a brand string only) or names the driver's model.
* **Probable** — network strings name the model as a whole token together with a manufacturer token. Never
  configured without the operator confirming.
* **UserAsserted** — the printer cannot report its model; the operator (or IT via `--model --confirm-model`)
  explicitly chose it. An assertion the device contradicts (its exact or probable model, or a model its probes name
  under any of its spellings) is refused: IT's assertion stops the run with nothing changed (silent: exit 4), and an
  operator's choice leaves the other printers to choose from.

Partial name matches alone never identify a printer. The administrator-assigned printer name (prtGeneralPrinterName)
is shown in reports but is never model evidence. When the device and an existing queue disagree, the device wins: a
device that names another supported model gets its queue repaired; a device that names an unsupported model (IEEE
1284 MDL, kcprtGeneralModelName or hrDeviceDescr), several models, or another manufacturer is not configured,
whatever its queue's driver says. It is reported as `NetworkIdentityMismatch` when one of its queues uses a supported
model's driver or its address was requested; an unrelated TA/UTAX/Kyocera device is information only
(`UnsupportedPrinterDetected`), and another vendor's device on a class-driver queue is not reported. A USB queue is not
configured as its driver's model when the TA/UTAX/Kyocera device node on its port, connected or not, names an
unsupported model, several models or another model, or when a printer of another manufacturer is connected on its
port. A WSD port cannot be linked to its printer: a WSD queue is Exact when one connected WSD printer is its driver's
model; when none is, and a connected TA/UTAX/Kyocera WSD printer names an unsupported, several or another model and no
other WSD queue can be that printer's own, the queue is not configured (a switched-off WSD ticket printer next to such
a printer must be switched on first). The queues of one TCP/IP host whose drivers name different models are not
configured. A queue with a supported model's driver on ports the setup cannot configure as a network queue (ports
pooled without a USB port, an LPR Port named "host:queue", or an Internet Port named by its IPP URL) is linked to every
host those ports name: it is never configured, and the network printer at each of those hosts gets no candidate under
any of its spellings, so Setup never adds a second queue next to it (not even on the same port) and never configures
another queue of that printer; one `QueueLeftUnchanged` warning per printer names the queue, its ports and every
spelling of the printer. A queue pooled over a USB port and Standard TCP/IP ports is linked to the hosts of its TCP/IP
ports in the same way, so the printers there get no candidate either, but it stays the USB printer's queue and is
configured for it. A queue on a port that names no printer's host (a
redirected local port, a Standard TCP/IP port without a host, an LPR or Internet Port whose name does not give the host
reliably, pooled ports none of which gives one) is left unchanged (information) and is a possible duplicate of a
requested printer without a queue. A queue whose driver names no single model (a class driver) and that may be a TA
printer's is linked to its hosts like a queue with a supported model's driver when it is pooled over Standard TCP/IP
ports (also the TCP/IP ports of a pool with a USB port): the printers at its hosts get no candidate, one
`QueueLeftUnchanged` warning per printer names it, and it counts as a rejected queue; a mixed pool that a USB printer
configures stays that printer's queue and is not counted as rejected. On an LPR or Internet Port such a queue blocks
nothing, unless it is the ticket queue of a printer it prints to (one of its hosts is a spelling of that printer or
leads to the same IP address). This is judged with the ticket-queue rule among that printer's own queues and this one
(for a requested printer without a queue of its own, this one alone): the only one on the ticket form, else the only
one with the configured queue name. That printer then gets no candidate, and the `QueueLeftUnchanged` warning names the
queue; with the printer's address nothing is set up, also when the printer has no other queue. Otherwise a
`DuplicateQueues` warning on that printer names it (information when the printer's own queue is already set up as the
ticket printer; the run that configures the printer's own queue keeps the warning and ends ready with warnings), and
when its host is a requested printer without a queue, it is reported as a possible duplicate of it. A queue pooled
over several ports belongs to the USB printer on any of them and keeps its pooling. A
USB node that is not connected does not claim a queue, pooled or on its own port, that another printer on the queue's
ports uses. That printer is either a connected printer whose node has hardware IDs (whatever its model), or a node of
the model the queue's driver names (connected or not) when the node is of another model. The queue is then judged by
the printers on its ports (the device wins), so an earlier unit never claims, repairs or changes the driver of the
queue another printer uses. When the planner or the elevated worker cannot read the Plug and Play devices, only the
queues detection gave the target count as its own, and the plan or result reports that the devices could not be read
(`OperationFailed` warning). A rejected queue (`QueueLeftUnchanged`) may be the ticket printer's, so no other printer
is then chosen without asking. A queue already set up as the ticket printer by itself (on the ticket form, or with the
configured queue name) that may be a TA printer's and that no candidate configures counts as one as well, with a
`QueueLeftUnchanged` warning naming it: a WSD, LPR or Internet Port queue whose driver names no single model and whose
printer has no queue the setup configures, or a queue to a printer that got no candidate (it does not answer, or does
not report its model). The queue of a device that reports another vendor or an unsupported model does not. The
configured model (`modelId`, `--model`) does not choose a printer either when a rejected queue's printer may be of
that model (the queue uses that model's approved driver, a probe of a host it prints to suggests that model, or its
driver names no single model and no printer at its hosts answered SNMP, so it may be of any model; a rejected queue
whose driver and printer name only other models does not stop it), or when a printer whose model must be decided is
probably that model or does not report one. A queue the limit of network probes left unchecked
(`DetectionResult.UncheckedQueues`) counts the same way without a configured address: a single eligible printer is not
chosen alone next to it (the window offers that printer; a silent run exits 4), and `modelId`/`--model` picks none,
because that queue's printer may be of any model.
With a configured address the printer there is still chosen when the address is the host of its queue (or the printer
has no queue yet). When the address is only another spelling of the printer (its IP address while its queue prints to
a host name, or the reverse), an unchecked queue may be another queue of that printer, possibly the ticket queue, so
the printer is offered, not chosen (silent: exit 4).

Host names and IP addresses that lead to the same IP address (what the probe contacted, or the address itself when it
is an IP address), or that report the same SNMP serial number with the same exactly identified model, belong to one
printer; an empty or placeholder serial, or different models, never do. Each host of an existing queue is its own
candidate, keyed by that host. Once another queue host of the same printer has a candidate, a queue host whose printer
and drivers name no model (a class-driver queue while SNMP is off) is a candidate of its own whose model must be named,
whatever the order of the hosts. It is still such a choice when its own probe gets no answer on the print port, and the
`DuplicateQueues` warning then says "… does not answer on the print port; its model must be named before it can be set
up." When one printer has a queue on two spellings, a branch or `--address`
preference naming one spelling selects that spelling's queue; otherwise the operator chooses (Repair: the queue already
on the ticket form), and the report warns that both print to the same printer. An address that is a third spelling
names the printer, not a queue, so it does not choose either, also when one of the queues needs its model decided: that
queue is then a choice whose model must be named. A requested spelling without a queue is an alias of
every queue host of its printer, so Setup never adds a second port or queue for it; requested spellings without any
queue are one candidate, keyed by the first that answers. Among several queues on one USB port or TCP/IP host, the one
already set up as the ticket printer is configured: the only one whose machine-wide default is the ticket form, else
the only one with the configured queue name; when none can be told apart, the preferred driver's queue is configured
and a warning names the others (the run that configures it keeps that warning and ends ready with warnings: a later
detection takes that queue for the one already set up as the ticket printer). Ports follow the same rule: an existing
RAW Standard TCP/IP port is reused when its host is the printer's address or an IP address it is known by, or, when
the printer's address is itself a name, another name of it that every detection of the run probes again (`--address`,
`printerAddress`); a printer keyed by an IP address never gets its queue on a name's port, which a later run without
DNS could not link to that address. A requested address without a queue next to a WSD queue, or next to a queue whose
host name was not probed or did not resolve, is reported as a possible duplicate: of the same model, of no named model
(a driver that names no single model, which yields no candidate), or of any model when the requested printer does not
report its model. When Setup then adds a queue for that address, the run keeps the warning, because later runs can no
longer tell, and ends ready with warnings.

## Ticket paper configuration

1. The form `AMWAG Ticket` is defined once, centrally, in micrometers (the forms-database unit):
   3.27 in × 6.70 in = 83 058 µm × 170 180 µm exactly (decimal arithmetic, no rounding).
2. `EnumForms`/`AddForm`/`SetForm` keep exactly one form with that name and exact size (never duplicated).
3. `DeviceCapabilities` (DC_PAPERS/DC_PAPERNAMES/DC_BINS/DC_MINEXTENT/DC_MAXEXTENT) shows whether the installed
   driver actually lists the form and which paper sources exist. A driver that does not list the form by name (the KX v3
   driver keeps its own paper list) is given its paper of the ticket size within 1 mm (closest first, never
   DMPAPER_USER), e.g. a custom page size made in its preferences. With neither, the size is reported as unsupported —
   nothing is written. A DEVMODE selects that paper by `dmPaperSize` with `dmFormName` its name or empty (KX leaves it empty).
   A queue on an accepted driver that does not offer the ticket form is moved to the preferred driver (`ChangeQueueDriver`),
   or to another installed approved driver that uses Windows forms.
   * A driver marked `listsWindowsForms: false` always moves. That is the KX v3 driver, measured at a branch: it ignores
     the forms database. It moves also when it has a custom page size of the ticket size, which has another name and
     may exist for one account only.
   * Another accepted driver moves when the exact form exists but it offers no paper of the ticket size.
   * Before the change, the queue's driver name and global DEVMODE are saved.
   * If `ConfigureGlobalDefaults` then fails, or is skipped because of a failure, both are restored in the same elevated
     run (`QueueDriverRestored`). A driver change for any other reason is never undone.
   * New queues never use a driver marked `listsWindowsForms: false`.

   Before the form exists, the ticket is checked against the driver's custom size range.
   * That range comes from the PrintCapabilities' `psk:CustomMediaSize` parameter limits.
   * DC_MINEXTENT/DC_MAXEXTENT are used only when the driver describes no custom size, because the KXv4 driver reports
     the bounding box of its listed papers there (92 mm minimum width, while its custom range starts at 70 mm).
   * The finding's `rangeSource` says which source was used.
4. The desired defaults (form, portrait, simplex, 100 %, multipurpose tray) are merged into the queue's existing
   DEVMODE (driver-private data preserved) and validated with `DocumentProperties(DM_IN_BUFFER | DM_OUT_BUFFER)`.
   Settings the driver drops or changes are reported per field instead of being assumed.
   `dmPaperWidth`/`dmPaperLength` are cleared when writing, because they override `dmPaperSize`: a stored DEVMODE
   whose size fields select another size than the form (more than 1 mm off) counts as wrong in assessment, read-back
   and verification. When a model's `ticketPaperSource.requiredForTicket` is set (all shipped models), a driver that
   does not offer or replaces the multipurpose tray gets no defaults at all, under `preferMultipurposeTray` too. When the
   tray is only preferred (`requiredForTicket: false`) and the driver replaces it, the driver's source is written with a
   warning and accepted afterwards: assessment, the per-user check and the already-correct check validate the desired
   DEVMODE read-only to learn it, so the defaults are not rewritten on every run.
5. The validated DEVMODE is written with `SetPrinter` level 8 (queue default for everyone) and, when the employee
   already has personal preferences for the queue, level 9 (per-user). Both are read back and compared.
   * Level 8 needs a printer handle opened with `PRINTER_ALL_ACCESS`; `PRINTER_ACCESS_ADMINISTER` alone is refused
     with error 5, even elevated (measured on Windows 11 build 26300).
   * Level 9 needs only `PRINTER_ACCESS_USE`.
6. Verification also converts the effective DEVMODE to a PrintTicket (`PTConvertDevModeToPrintTicket`) as an
   independent check of the media size the driver will use.

## Driver integrity chain

Approved manifest entry (official HTTPS host allow-list, file name, size, SHA-256, INF path, trusted catalog
signers) → archive from the offline folder, cache or approved URL, verified by size and SHA-256 → extraction into
an administrator-only folder with path-traversal protection → catalog Authenticode check (`WinVerifyTrust`) and
signer allow-list → `UploadPrinterDriverPackage` + `InstallPrinterDriverFromPackage` (Windows enforces its own
driver signing policy; nothing is bypassed) → `UpdateDriverForPlugAndPlayDevices` for a connected USB device.

A package needed only to install the driver on the USB device (an approved driver is already installed) never holds
back the queue and its defaults. When it cannot be obtained, or may not be installed on this PC (not approved,
`minOsBuild`), the binding is skipped with a `DeviceDriverNotBound` warning; when it fails a check (integrity,
signature, configuration) that failure keeps its own code as a warning, so a sign of tampering is not lost; the result
screen lists it first and never leaves it out. The package is requested at most once per run. Verification reports a
connected USB device that is not linked to an accepted driver as a warning; a device on an accepted driver that only
waits for a restart (problem code 14) counts as linked: verification reports the pending restart (`RebootRequired`) and
Repair does not bind it again.

## Safety properties

* Idempotent: every action re-reads current state, does nothing when already satisfied, and verifies its result.
* Never deletes or renames queues, ports, drivers or forms; never changes other printers or the Windows default
  printer. Duplicate queues found for the same printer are reported, not removed.
* Never adds a queue on a Standard TCP/IP port to the printer, or on a USB printer's port, that another local queue
  already prints to. That queue may be a pooled queue (also one a USB printer configures), or the queue of the printer
  a USB port now leads to. The plan gets a `QueueLeftUnchanged` blocker that names that queue as one "which the setup
  does not configure for this printer", nothing is changed, and the elevated worker refuses the same from its own
  reads.
* Setup and Repair configure exactly one printer, the ticket printer; other supported printers are reported as not
  changed. A printer is configured without asking only when the choice is unambiguous. With a configured address only
  the printer there is a candidate: when it cannot report its exact model and no queue with an approved model-specific
  driver identifies it (DriverOnly), it is asked about (silent: exit 4), and when it does not answer or is not a
  supported printer, no other printer is configured in its place.
* The window, the elevated worker and the final check find the chosen printer by its key, or by any of its spellings
  when another detection keys it differently, and re-apply the identity rules each time; an asserted model is refused
  when the printer reports another one, and the run then stops for IT. The final check of a network printer probes the
  same addresses as the worker (the existing TCP/IP queues' hosts included).
* Closing the window never abandons the elevated worker: it stops after the action that is running, the window waits,
  reads the system back, then closes.
* Configuration files next to the exe (and `branch.json` in `%ProgramData%`) are used only when only administrators can
  change them: the NTFS owner and permissions of the file, its folder and every folder above it are checked, and no
  junctions or links are allowed. Otherwise the built-in configuration applies. The window, silent runs and the
  elevated worker use the same rule, and the worker refuses other bytes than the window loaded.
* Diagnostics mode performs no write calls (enforced by tests against a recording fake spooler).
* Ticket dimensions must be explicitly confirmed in the configuration before employees can run Setup/Repair.
* Structured JSON-lines logs (the employee's window: `%LOCALAPPDATA%\AMWAG\PrinterSetup\Logs`; the elevated worker and
  silent runs: `%ProgramData%\AMWAG\PrinterSetup\Logs`) and a text/JSON report for every run.

## Known technical limitations

See `docs/TROUBLESHOOTING.md` and `docs/TICKET-SOFTWARE-INTEGRATION.md`. In short: network printers that were
never configured can only be found by address (no subnet scanning); applications can override printer defaults
per job; per-user preferences of *other* Windows accounts can only be corrected when those users run Repair;
Windows protected print mode blocks vendor drivers; and Windows accepting a print job does not prove a ticket
physically printed — the test ticket is checked by a person.
