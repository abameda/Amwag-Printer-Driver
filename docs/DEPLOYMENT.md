# AMWAG Printer Setup — IT deployment guide

## 1. What is distributed

```
AMWAG Printer Setup\
  AMWAG Printer Setup.exe        self-contained Windows x64 executable (no .NET install needed)
  amwag-printer-config.json      approved configuration and driver manifest (also built into the exe)
  drivers\                       optional approved offline driver archive(s)
  docs\                          deployment, troubleshooting, acceptance checklist, ticket-software notes
  README.md, SHA256SUMS.txt
```

Optional per-branch file: `branch.json` next to the exe, or in `%ProgramData%\AMWAG\PrinterSetup\branch.json`.

**Which configuration is used.** `build.ps1` builds the configuration into the exe. `amwag-printer-config.json` and
`branch.json` next to the exe, and `%ProgramData%\AMWAG\PrinterSetup\branch.json`, are used only when only
administrators can change them:

* the drive keeps NTFS permissions (not a FAT/exFAT stick);
* the file and its folder are owned by Administrators, SYSTEM or TrustedInstaller (or a domain's Domain Admins or
  Enterprise Admins);
* no other account may change the file, or create, delete or rename files in its folder;
* no folder above it, up to the drive or share root, is a junction or link, or can be renamed, deleted, re-permissioned
  or taken over by another account.

Otherwise the tool uses the configuration built into the exe (and no branch file from that place) and logs a
`config.warning` naming the ignored file and the reason. The window, silent runs and the elevated helper all apply this
rule, and the elevated helper refuses to change anything if it loads different bytes than the window did. Only NTFS
permissions count (share permissions are not visible to the tool), and employees need the normal Read permission,
which includes reading permissions, on every folder of the path.

## 2. Before the first distribution (mandatory)

1. **Confirm the ticket size.** Measure the real ticket stock with a caliper. Edit `config\amwag-printer-config.json`:
   ```json
   "ticket": {
     "formName": "AMWAG Ticket",
     "width": 3.27, "height": 6.70, "unit": "in",
     "dimensionsConfirmed": true,
     "confirmedBy": "Name, IT department",
     "confirmedOn": "2026-11-15"
   }
   ```
   `unit` is `"in"` or `"mm"`. Until `dimensionsConfirmed` is `true` (with `confirmedBy` and `confirmedOn`),
   Setup and Repair refuse to change anything for employees; Diagnostics still works. IT can test an unconfirmed
   build with `--allow-unconfirmed-dimensions`.
2. **Review the driver manifest** (`driverPackages`, see `docs\DRIVER-MANIFEST.md`). The preferred package is the
   WHQL-signed KXv4 6.1.1603 PCL6 driver, which installs silently. KX 8.7.0917 is approved but only usable after IT
   deploys Kyocera's publisher certificate to *Trusted Publishers*.
3. **Offline driver**: keep the approved archive (`Kxv4_v6.1.1603_TA_RC7_WHQL.zip`, exact name and SHA-256 from the
   manifest) in `drivers\`; `build.ps1` re-checks it and ships it, so branches without internet can install.
4. **Printer panel**: on every printer set the MP tray paper size to *Custom* (Menu → Paper Settings → MP Tray Set. →
   Paper Size) and the media type for the ticket stock. Software cannot change this setting.
5. **Build**: `.\build.ps1` (requires the .NET 10 SDK on the build machine only). Output: `dist\`.
6. **Recommended**: sign `AMWAG Printer Setup.exe` with the company code-signing certificate (SmartScreen,
   AppLocker/WDAC allow-listing), and let employees start it from a folder only administrators can change (a
   read-only share, or a folder under `C:\Program Files`). The UAC prompt then shows the verified publisher, and nobody
   can swap the exe that an administrator approves. The exe loads code only from itself and from `System32`: it
   unpacks nothing to `%TEMP%` and does not load DLLs placed next to it. A copy on a USB stick, the Desktop or in
   Downloads therefore works too, but there the employee could replace the exe itself before an administrator
   approves the prompt (an unsigned or differently signed exe is the warning sign).
7. **Physical acceptance**: complete `docs\HARDWARE-ACCEPTANCE-CHECKLIST.md`.

## 3. Configuration reference (`amwag-printer-config.json`)

| Section | Key | Meaning |
|---|---|---|
| `ticket` | `formName`, `width`, `height`, `unit`, `dimensionsConfirmed`, `confirmedBy`, `confirmedOn` | The single, central ticket size. Branch files cannot change it. |
| `printPreferences` | `orientation` (`portrait`), `duplex` (`simplex`), `scalePercent` (100), `paperSource` | `paperSource`: `driverDefault`, `preferMultipurposeTray` (default) or `requireMultipurposeTray`. A model whose `ticketPaperSource.requiredForTicket` is `true` (all shipped models: only the multipurpose tray can feed the ticket) needs the tray under `preferMultipurposeTray` too: if the driver does not offer it or replaces it, no defaults are written. Only `driverDefault` leaves the source to the driver. Under `preferMultipurposeTray` a model with `requiredForTicket: false` keeps the driver's source when the driver replaces the tray (written once with a warning, then accepted). |
| `connection` | `newQueueName` | Name for network queues created by the tool (USB queues created by Windows keep the driver name). |
| | `rawPortNumber` (9100), `snmpEnabled`, `snmpCommunity` | Standard TCP/IP port settings and the SNMP community used to identify network printers. |
| | `identifyExistingNetworkQueues`, `maxNetworkProbes`, `networkTimeoutMilliseconds` | One SNMP query per relevant address; no subnet scanning. |
| | `usbQueueWaitSeconds` | How long to wait for Windows to create the USB queue after installing the driver. |
| `driverSource` | `offlineFolder` (`drivers`), `allowOnlineDownload`, `downloadTimeoutSeconds` | Offline folder may be absolute or a UNC path. |
| `models[]` | `id`, `displayName`, `hardwareIds`, `ieee1284Ids`, `networkModelNames`, `acceptedDrivers`, `preferredDriverName`, `driverPackageId`, `ticketPaperSource`, `multipurposeTrayRange`, `cassetteRange` | Supported models. New models are added here, without code changes, once their INF data is verified. |
| `models[].acceptedDrivers[]` | `name`, `minVersion`, `listsWindowsForms` (default `true`) | Drivers a queue may keep. `listsWindowsForms: false` marks a driver that ignores Windows forms and offers only its own paper list (the KX v3 `P-xxxx KX` drivers, measured at a branch).<br>A queue on such a driver is always moved to the preferred driver, also when a custom page size of the ticket size was made in it: that paper has another name, and may exist for one Windows account only. If the ticket defaults then fail, the queue gets its driver and defaults back.<br>A new queue never uses such a driver.<br>The preferred driver must list Windows forms. |
| `driverPackages[]` | `id`, `version`, `downloadUrl`, `fileName`, `sizeBytes`, `sha256`, `infPath`, `catalogPath`, `trustedSigners`, `approved` | Approved driver manifest. Downloads are allowed only from official TA/UTAX/Kyocera HTTPS hosts. `approved: false` or a `minOsBuild` above the PC's build stops the run only when the driver must be installed from the package; with an approved driver already installed, the USB device keeps its driver (a warning) and the queue and defaults are configured. |

The configuration is validated at start-up; errors are listed on screen (or in the silent-mode report) and nothing
is changed.

Order: `--config <path>` (IT testing only, see section 7), else `amwag-printer-config.json` next to the exe when only
administrators can change it (section 1), else the configuration built into the exe. The log entry `config.loaded`
shows the source, the SHA-256 of the exact bytes used (`configSha256`, `branchSha256`) and `ignoredFiles`.

To change the configuration, either rebuild with `build.ps1` (every copy, including USB sticks and Desktop copies, then
uses the new built-in configuration), or edit `amwag-printer-config.json` in a folder only administrators can change
(an IT share whose NTFS permissions give employees read only, or a folder under `C:\Program Files`). A changed file in
a folder employees can write to is ignored. If the file changes while an employee's window is open, the administrator
step changes nothing, also when the new file is not valid or no longer contains the printer's model: the window says the
configuration changed and to close the program and start it again (`ConfigurationChanged`), and offers Close instead of
Try again. After the restart an invalid file is reported by the program itself.

## 4. Branch overrides (`branch.json`)

```json
{
  "branchId": "ALX-01",
  "branchName": "Alexandria Station",
  "printerAddress": "10.20.1.50",
  "modelId": "P-5032DN",
  "queueName": "AMWAG Ticket Printer"
}
```

All fields are optional. `printerAddress` lets the tool find a network printer that Windows has never seen;
`modelId` and `printerAddress` also resolve the choice when several printers are present. Ticket dimensions cannot
be overridden per branch.

With `printerAddress` (or `--address`) only the printer at that address is set up, repaired or checked. When it cannot
report its exact model and no queue with an approved model-specific driver identifies it, the tool asks (silent: exit 4;
confirm with `--address <ip> --model <id> --confirm-model`); otherwise the existing queue's driver identifies it, and
`--model <id> --confirm-model` replaces that driver's model. When nothing answers there, or the device there is not a
supported printer, no other printer is used in its place (silent: exit 3); in the window the employee may enter the
right address instead, and that address then counts for the run. Without an address, `modelId` chooses among several
printers unless another printer may be of that model (one whose model must be confirmed; a queue left unchanged whose
printer may be of that model: it uses that model's driver, a probe of a host it prints to suggests that model, or its
driver names no single model and no printer at its hosts answered SNMP; or a queue the limit of network probes left
unchecked, see below), and a single printer of another model than `modelId` is offered as a choice rather than set up
without asking. Several printers whose model must be confirmed are a choice too: only the chosen one is confirmed. In
the window, Setup asks for the ticket printer's address when the employee chooses none of the printers offered; Repair
then ends and changes nothing. A silent run that stops for a choice says how to decide it:
its `Cancelled` warning names the switches or `branch.json` settings that can (or says that none can), and every
printer whose model must be confirmed gets its own `ProbableMatchRequiresConfirmation` error with its switches.

The address may be the printer's DNS name or its IP address. A queue that already prints to the printer under the
other spelling is recognised (both lead to the same IP address, or the printer reports the same serial number and
exact model): that queue is configured and no second one is created. When the printer has a queue on each spelling,
the address says which one is the ticket queue: the queue on that spelling is configured and the other keeps its
settings; without an address the employee chooses (silent: exit 4). An address that is a third spelling (for example
the IP address of a printer whose two queues are on two host names) names the printer, not one of its queues: the
employee chooses, Repair takes the queue already on the ticket paper, and a silent run stops with exit 4 and names
each queue with its host (pass that host with `--address`). This holds also when one of the queues uses a class driver
and the printer does not report its model: that queue is then a printer whose model must be named first, also when its
host does not answer on the print port (the `DuplicateQueues` warning then says "… does not answer on the print port;
its model must be named before it can be set up."). An existing Standard TCP/IP port without a queue that already
sends to the printer's IP address is used for a new queue; so is one to another configured name when the printer
itself is configured by name. No second port is created, and a port that another queue already prints to (a port of a
pooled queue) is never used for a second queue: the run is blocked (`QueueLeftUnchanged`, exit 8) and nothing is
changed. The same holds for the USB port of a printer that is not connected. When a stale `modelId` names the model of
an earlier unit (a USB ticket printer that was replaced by another model) whose port the connected unit's pooled queue
also lists, Setup adds no second queue there: the run is blocked (`QueueLeftUnchanged`, silent: exit 8) and nothing is
changed. Correct `modelId`, then run Setup again. A printer configured by IP address gets its new queue on a port to
that address, so later runs find it also when DNS does not answer. A WSD queue, or a queue whose host name was not
probed or does not resolve, cannot be compared this way; the report then warns about a possible duplicate
(`DuplicateQueues`), also when the printer at the address does not report its model, and for such a queue whose driver
names no model (for example a class driver Windows chose). So does a class-driver queue on an LPR or IPP port whose
host is that printer, unless it is already set up as its ticket queue (see below). When Setup then adds the queue, the
result keeps that warning ("A queue was added for …"), because later runs can no longer tell, and the run ends ready
with warnings (silent: exit 1; the window shows *Another printer entry may print to this printer*, naming every such
queue, those that print to the printer through an LPR or IPP port first).

A queue with an approved model's driver that is pooled over several Standard TCP/IP ports (for example over the old
and the new address after an IP change), or that uses an LPR Port or an Internet (IPP) port, is never configured, and
the printers it prints to are not set up automatically under any of their spellings. With the address of such a
printer nothing is set up: the report names the queue, its ports and the printer (`QueueLeftUnchanged` warning, and the
`NoSupportedPrinterFound` error names it), and a silent run exits 3. A queue pooled over a USB port and a TCP/IP port
stays the USB printer's queue, and the printer at its TCP/IP port is not set up automatically either. To set it up,
un-pool the queue or put it on a single Standard TCP/IP port (or delete it if it is not used), then run Setup again.

A queue with a class driver (such as the Microsoft PCL6 or IPP Class Driver) that is pooled over Standard TCP/IP ports
is handled like a pool with an approved model's driver. The printers it prints to are not set up automatically, and
the report names the queue (`QueueLeftUnchanged`); with such a printer's address a silent run exits 3. A class-driver
queue on an LPR or Internet (IPP) Port that is already set up as the ticket queue of the printer it prints to (it uses
the `AMWAG Ticket` form, or has the configured queue name) also keeps that printer from being set up, whether or not
the printer has a queue of its own: that queue is not configured either, no queue is added next to it, and with the
printer's address nothing is set up (silent: exit 3, and the `NoSupportedPrinterFound` error names the queue).
Otherwise that printer's own queue is configured (a printer without one gets a new queue, with the possible-duplicate
warning above), and a `DuplicateQueues` warning names the class-driver queue: check which queue employees print to.
The run that configures the printer's own queue keeps that warning and ends ready with warnings (silent: exit 1),
unless that queue was already set up as the ticket printer.

A queue that is already set up as the ticket printer (it uses the `AMWAG Ticket` form, or has the configured queue
name) and may be a TA printer's, but that leads to no printer the setup can configure, is not configured either: a
queue whose driver names no single model (such as a class driver) on a WSD, LPR or IPP port whose printer has no queue
the setup configures, or a queue to a printer that does not answer or does not report its model. Its
`QueueLeftUnchanged` warning names it, and no other printer is taken as the ticket printer without asking: without an
address the window offers the choice, and a silent run exits 4. If its printer is the ticket printer: for a queue on a
Standard TCP/IP port, name that port's host with `printerAddress` (silent: `--address <host> --model <id>
--confirm-model`, because the printer does not report its model; switch it on first if it does not answer), and the
queue itself is then configured; for a WSD, LPR or IPP queue, put it on a Standard TCP/IP port to that printer, or
delete it and name the printer with `printerAddress`, then run the setup again. A queue of a device that reports
another vendor or an unsupported model does not count.

Network probes are limited by `connection.maxNetworkProbes` (8). The configured addresses come first, then the hosts
of queues on one Standard TCP/IP port, and last the host names of pooled, LPR and IPP queues. When the limit cuts the
host of a class-driver queue on one Standard TCP/IP port that no probed spelling of a printer leads to, the report
names that queue
(`NetworkIdentityUnavailable`: "… was not checked because the limit of network probes was reached; it may be the
ticket printer's queue."). Such a queue keeps a single printer from being chosen without asking: without an address
the window offers that printer and a silent run exits 4, and `modelId`/`--model` chooses none, because the unchecked
queue's printer may be of any model. With an address (`printerAddress`, `--address` or the address entered in the
window) the printer there is still chosen when the address is the host of its queue, or when it has no queue yet;
when the address is only another spelling of it (its IP address while its queue prints to a host name, or the
reverse), it is offered, not chosen (silent: exit 4). Raise `maxNetworkProbes`, or name the ticket queue's host with
`--address` or `printerAddress`: a silent run that stops there names each unchecked queue with its host, and the host
of the queue that was found sets up that queue, so name it only if that queue is the ticket queue.

`branch.json` follows the same trust rule as the configuration (section 1). In `%ProgramData%\AMWAG\PrinterSetup` it
counts once that folder is secured. Every elevated run secures it (owner Administrators, read-only for users), so
deploy the file after the first IT-managed or administrator-approved run, or create the folder with those permissions.

## 5. Distribution options

* **Shared folder**: copy the distribution folder to a share that employees can read but **not modify**, enforced by
  NTFS permissions (owner Administrators; write access only for Administrators, SYSTEM or Domain Admins). Then
  `amwag-printer-config.json` and `branch.json` there are used and IT can edit them without rebuilding. Employees
  double-click the exe. If employees can write there, or only the share permissions restrict them, the files are
  ignored and the built-in configuration is used.
* **USB stick, or a copy on the Desktop or in Downloads**: the employee can change files there (a FAT/exFAT stick has
  no permissions at all), so the tool ignores `amwag-printer-config.json` and `branch.json` next to the exe and uses
  the configuration built into the exe; the log names each ignored file. Configuration changes for these copies need
  a rebuild; branch settings come from `%ProgramData%\AMWAG\PrinterSetup\branch.json` deployed by IT, or from
  `--address`/`--model`.
* **Deployment tools** (Intune Win32 app, Configuration Manager, GPO start-up script): use silent mode (section 7).
  If such a run logs the configuration file as ignored, the built-in configuration was used.

## 6. Employee use

Double-click `AMWAG Printer Setup.exe`. The tool starts immediately, shows six steps, and asks questions only when
needed: Windows administrator permission (UAC), several printers, an unknown network printer, or an error. On
success it shows the printer, driver, connection and ticket size, and offers **Print Test Ticket**.

Language: Arabic or English follows Windows; the button in the header switches at any time (`--lang ar|en`
forces one).

## 7. Command line

```
"AMWAG Printer Setup.exe" [--mode setup|repair|diagnostics] [--address <ip|host>] [--model <id>]
                          [--confirm-model] [--lang en|ar] [--config <path>] [--report <path>]
                          [--allow-unconfirmed-dimensions] [--silent] [--help]
```

| Switch | Meaning |
|---|---|
| `--mode setup` | Default. Detect, install if needed, configure, verify. |
| `--mode repair` | Check and repair the existing queue of the ticket printer; never creates a queue. The ticket printer is the one Setup would choose (`--address`/`--model` or `branch.json`, else the only printer); when it has no queue yet Repair changes nothing and says to run Setup (the window offers *Run Setup* instead of *Try again*; a `--model --confirm-model` that the printer contradicts is still refused, exit 4); a model that must be confirmed is confirmed as in Setup (silent: `--confirm-model`, else exit 4), also when that printer's queue is the only one next to a printer without a queue, and `--model --confirm-model` replaces the model of an old queue driver as in Setup. Among several printers it repairs the only one already set up with the ticket paper (one queue pooled over several printers counts once; among printers whose model must be confirmed, it asks only about the one on the ticket paper), otherwise it asks (silent: exit 4, nothing changed); it also asks when a queue left unchanged or unchecked (`QueueLeftUnchanged`, `NetworkIdentityUnavailable`) is on the ticket paper as well. When the queue of an unplugged USB printer is the only one with the ticket paper while a unit of the same model is connected (a replaced or switched-off ticket printer), Repair asks which one is the ticket printer (silent: exit 4); running Setup once (it sets up the connected unit; when printers whose model must be confirmed are found as well, Setup offers them next to it, so choose the ticket printer in Setup's window, or set up a network one silently with its switches) or deleting the old queue resolves it. The window's Repair buttons repair the printer that was just set up or tested. Other printers are never changed; the report lists them as not changed. |
| `--mode diagnostics` | Inspect and report only; never changes anything; no UAC prompt. The exit code follows the ticket printer only (chosen as Setup and Repair choose it); other supported printers are checked and reported. With several and none known to be the ticket printer (also several queues of the printer at the configured address): 1 when one of them is ready, else 2, with a `MultipleSupportedPrinters` warning that names each of them, printers whose model must be confirmed first with their switches; when Repair would ask to confirm the printer whose queue has the ticket paper, that printer decides (2, `ProbableMatchRequiresConfirmation`). Only the ticket printer's own findings lead the result; the others are in the report; when nothing usable is at the configured address, 2 while other supported printers are found (they are checked for the report), else 3. A computer Setup could not change (spooler not running, protected print mode, unsupported Windows) gives 7 or 8, as Setup would. |
| `--address` | Network printer to identify (single SNMP query; no scanning): its DNS name or IP address. An existing queue for the same printer under the other spelling is reused. |
| `--model` + `--confirm-model` | IT asserts the model of the printer at `--address` when it cannot report its exact identity (SNMP disabled, or only a probable match), in Setup and Repair. It also replaces the model of an existing queue's driver at that address (a replaced printer that does not report its model): the queue's driver is then changed. A model the printer itself contradicts (its exact or probable model, or a model its probes name) is refused: nothing is changed, exit 4, and a `NetworkIdentityMismatch` error names both models; also in Repair when the printer has no queue yet, and when every queue of the printer at `--address` contradicts it. In the window the refusal names both models and the address, says that the printer was not changed, and offers no *Try again*. Without it, a silent run whose printer needs this confirmation stops with exit 4 and a `ProbableMatchRequiresConfirmation` error that names the switches. |
| `--report <path>` | Write the text report there (plus a `.json` next to it). |
| `--config <path>` | IT testing only: use another configuration file (must exist). The elevated helper never loads a file named on the command line, so test it in Diagnostics, in an elevated window or in a silent run as administrator. |
| `--silent` | No windows. Needs an elevated context (administrator or SYSTEM) for Setup/Repair. |
| `--allow-unconfirmed-dimensions` | IT testing only. |

### Exit codes (silent mode and the internal elevated worker)

| Code | Meaning |
|---|---|
| 0 | Ready |
| 1 | Ready with warnings (for example printer offline, physical check pending, USB printer not linked to the approved driver, or a restart pending to finish installing the driver on it; also when Setup added a queue for the printer while another queue may print to it, such as Windows' own WSD or IPP class-driver queue or a queue whose host name was not probed or did not resolve, and when the run configured a queue not yet set up as the ticket printer next to other queues of the printer (chosen by driver and name among queues that could not be told apart, or next to a class-driver queue on an LPR or IPP port to that printer): the `DuplicateQueues` warning names the other queues, and a later run, which can no longer tell, ends ready) |
| 2 | Not ready (a verification check failed; also when the administrator step was refused, for example because another run held the apply lock, and nothing was changed; Diagnostics: also when the ticket printer's model must be confirmed first, or nothing usable is at the configured address while other supported printers are found) |
| 3 | No supported printer found, none at the configured address (also when a queue the setup cannot configure, pooled or on an LPR or IPP port, prints to the printer there: the `NoSupportedPrinterFound` error names it), or the employee did not confirm the model of the printer (Repair: also when the ticket printer, or every supported printer found, has no queue yet) |
| 4 | Setup and Repair: a decision nobody made: several printers, or a printer next to a queue that could not be matched to its printer or cannot be configured (`QueueLeftUnchanged` or `NetworkIdentityMismatch`) or that the limit of network probes left unchecked (`NetworkIdentityUnavailable`; also at a configured address that is only another spelling of the printer) (choose with `--address`/`--model` or `branch.json`; the `Cancelled` warning says which of them can decide, and `--model`/`modelId` cannot when that queue's printer may be of that model: its driver's model, a model a probe of its hosts suggests, or any model when its driver names none and no printer at its hosts answered SNMP; nor next to an unchecked queue), the printer's model must be confirmed (`--address <ip> --model <id> --confirm-model`; the `ProbableMatchRequiresConfirmation` finding names the switches), or the printer contradicts the model given with `--confirm-model` (`NetworkIdentityMismatch` error). `--address`/`--model` cannot choose between two connected USB printers of the same model; Setup ignores an unplugged USB printer of the same model as a connected one, and Repair asks about it only when its queue alone has the ticket paper (run Setup once, or delete that queue). Diagnostics never returns 4. |
| 5 | Administrator rights required or UAC declined |
| 6 | Configuration invalid, ticket dimensions unconfirmed, or the configuration changed between the window and the administrator step |
| 7 | Print Spooler unavailable (also in Diagnostics) |
| 8 | Blocked (for example protected print mode, unsupported OS/architecture, driver does not support the ticket size, or a queue the setup does not configure for this printer already prints to the Standard TCP/IP or USB port a new queue would use; protected print mode and the platform also in Diagnostics) |
| 9 | Cancelled (the window was closed during the run: the step running with administrator rights finished first, later steps were not started, and the result was read back) |
| 10 | Internal error (see log) |
| 11 | Invalid command-line arguments |

The internal worker reports only to the window that started it (`--window-pid`). If it cannot reach that window, it
changes nothing and exits 9; the window reports a helper that ended before connecting as not started (nothing was
changed).

In the window, *Installing driver* and *Configuring ticket size* read *Not started; nothing was changed* when they never
began (approval declined, the helper did not start or refused, a blocker of the printer, or all their actions were
skipped after an earlier one failed), *Not finished: another step failed* when they began but an action of the other
step failed, *Not finished: the setup was stopped* after a stop, and *Not changed: Diagnostics only checks* in
Diagnostics; *Already installed* / *Already correct* mean there was nothing to do. A step the run ended before reads
*Not reached* (also both apply steps after a blocker of the computer, or a declined approval to start the Print
Spooler). *Detecting printer* reads *This computer cannot be set up as it is* when an environment blocker stops the
run: Print Spooler, protected print mode, Windows version or processor, configuration, or unconfirmed ticket
dimensions. It reads *Printer found; the model given does not match* when IT's `--model` with `--confirm-model` is
refused. During Setup and Repair *Checking driver* reads *Driver will be installed* (or *updated*), then *Driver is
ready*, or *Driver not installed* (*not updated*) when the read-back finds it missing; it reads *Driver state not
confirmed* when the read-back could not find the printer again and no install action of the run says what happened.
In Diagnostics, *Checking driver* and *Verification* end on the ticket printer, never on another printer checked after
it. Without a single ticket printer they read *Checked; ticket printer not known*, or *Not checked* when the ticket
printer could not be checked (its model must be confirmed, or nothing usable is at its address). A run blocked after
changes were made (exit 8) says *Some changes were made, but the setup cannot finish* instead of *Nothing was changed*;
starting the Print Spooler alone is not a change.

## 8. Administrator permissions

Installing a driver, creating a port/queue/form and setting the queue's defaults need administrator rights.
The tool runs as the employee, detects everything without elevation, and requests elevation **once**, only when a
machine-wide change is actually needed (it re-runs itself elevated as an internal worker). Personal printing
preferences are then corrected as the employee, without elevation. If nothing needs changing there is no prompt.

If employees cannot approve UAC:

1. **IT-managed Setup** — run once per PC with an administrator account or through the deployment tool:
   ```
   "AMWAG Printer Setup.exe" --silent --mode setup --report "C:\ProgramData\AMWAG\PrinterSetup\Reports\setup.txt"
   ```
   For a network printer add `--address <ip>` (or deploy `branch.json`). Treat exit codes 0 and 1 as success.
2. Afterwards employees can run the tool themselves: it verifies everything and fixes their personal printing
   preferences without UAC. If machine-wide repairs are needed later, IT re-runs step 1 (or `--mode repair`).
3. Running as SYSTEM sets the queue defaults for all users. A user who had changed personal preferences before is
   corrected the next time they run the tool.

Intune Win32 app: install command as above; detection rule example — exit code from a scheduled
`--mode diagnostics --silent` run, or registry/printer presence of the queue name. Diagnostics judges only the ticket
printer; other supported printers do not change its exit code.

## 9. What the tool writes

| Location | Content |
|---|---|
| Windows driver store / print spooler | Approved TA driver (only when missing), Standard TCP/IP port (network), print queue (only when missing), form `AMWAG Ticket`, queue default DEVMODE. |
| `HKCU` (per-user printing preferences) | Only when the employee already had personal preferences for the queue. |
| `%LOCALAPPDATA%\AMWAG\PrinterSetup\Logs` | JSON-lines logs of the employee's (unelevated) window, kept 60 days. |
| `%ProgramData%\AMWAG\PrinterSetup\Logs` | Logs of the elevated worker and of silent/IT runs. |
| `%ProgramData%\AMWAG\PrinterSetup\Reports` | Reports from silent runs (per-user folder when not elevated). |
| `%ProgramData%\AMWAG\PrinterSetup\DriverCache`, `Packages` | Verified driver archive and the extracted x64 driver folder. |

Only elevated processes create or write anything under `%ProgramData%\AMWAG`. They secure every level of that folder
chain: owner Administrators, no write access for standard users, junctions and links refused; a folder owned by
another account is moved aside (never deleted) and recreated, and a folder or junction that another account creates
while the tool is creating it is refused. An old extraction folder is deleted only when neither it nor any folder above
it can be changed, renamed or replaced by a standard user. Elevated runs read their configuration only from the
built-in copy or from files only administrators can change (section 1), and the elevated helper refuses to apply
configuration bytes other than those the window loaded. So a standard user cannot redirect or swap what an elevated
run reads, writes, extracts or installs. (A copy of the tool in a folder the employee can change can still have its
exe replaced; see section 2, step 6.)

The tool never deletes or renames queues, ports, drivers or forms, never changes other printers and never changes
the Windows default printer.

## 10. Adding a printer model later

1. Download the official driver package for the model; verify it supports the model (INF model line and hardware
   IDs) and record version, size, SHA-256, INF path and catalog signer in `driverPackages` (or reuse an existing
   package).
2. Add a `models[]` entry with the exact INF hardware IDs, IEEE 1284 MFG/MDL, accepted driver names, preferred
   driver, MP tray bin id/name, and official paper ranges.
3. Run the tests (`build.ps1` validates the configuration), then complete the hardware checklist for that model.

## 11. Removing the configuration (manual rollback)

The tool does not uninstall. To undo on a PC (administrator): Settings → Printers & scanners → remove the AMWAG
queue; Print Server Properties → Forms → delete `AMWAG Ticket`; Print Server Properties → Drivers → remove the TA
driver if no other queue uses it; Ports → delete the `IP_<address>` port if unused.
