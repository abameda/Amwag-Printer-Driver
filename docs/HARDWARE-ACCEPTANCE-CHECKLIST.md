# Physical printer acceptance checklist

Automated tests prove the logic with simulated printers and check Windows APIs read-only on a development PC.
They do **not** prove behaviour on real TA Triumph-Adler hardware. Complete this checklist on real printers before
distributing a build to branches, and keep the filled-in copy with the release.

Record for every run: date, tester, PC name, Windows edition/version/build, printer model and firmware, connection,
result (Pass/Fail), and the log file names from `%LOCALAPPDATA%\AMWAG\PrinterSetup\Logs` (the employee's window) and
`%ProgramData%\AMWAG\PrinterSetup\Logs` (the elevated worker and silent runs).

## 0. Preconditions

- [ ] `config\amwag-printer-config.json`: ticket width/height/unit measured on real ticket stock with a caliper;
      `dimensionsConfirmed: true`, `confirmedBy`, `confirmedOn` filled in.
- [ ] Driver manifest entries reviewed (version, SHA-256, signer) — see `docs\DRIVER-MANIFEST.md`.
- [ ] Test PCs: one Windows 10 22H2 x64 and one Windows 11 (current release) x64, each with a standard employee
      account and an administrator account available.
- [ ] Ticket stock, a ruler or caliper (0.5 mm resolution), one printer of each model: P-4030DN, P-4531DN, P-5032DN,
      FS-2100DN (and FS-2100D where a branch has one; it is USB-only without its optional network card).
- [ ] Network details for each printer (IP address, SNMP community if not `public`).

## 1. Core scenarios (repeat for each model)

| # | Scenario | Steps | Expected result |
|---|---|---|---|
| 1 | USB, no driver installed | Remove any TA queue/driver from the test PC (manually, as admin). Connect USB, power on. Run the tool as the employee. | Model detected automatically; one UAC prompt; driver installed; one queue created; ticket form configured; Ready screen. |
| 2 | USB, correct driver installed | Re-image or reuse scenario 1 PC; remove only the `AMWAG Ticket` defaults (set A4 in Printing Defaults). Run the tool. | No driver download/install; defaults corrected; Ready. |
| 3 | Installed, form missing | As admin delete form `AMWAG Ticket` in Print Server Properties → Forms. Run the tool. | Form recreated with exactly 82.30 × 170.18 mm; defaults set; Ready. |
| 4 | Already correct | Run the tool again immediately. | No UAC prompt; every step "already correct"; Ready. |
| 5 | Incorrect queue | (a) Set paper A4 / landscape / two-sided in Printing Defaults; (b) as the employee set personal Printing Preferences to A4; (c) change the queue's driver to a different driver. Run **Repair**. | All three corrected; per-user preferences corrected without a second UAC prompt; Ready. |
| 6 | TCP/IP printer | Network printer never set up on this PC. Run the tool; when asked, enter the printer's IP address. Run it again unchanged. Then (as admin) delete the queue and port the first run created and repeat with `branch.json` containing `printerAddress`. | Model identified over SNMP (or asked to confirm when SNMP is off); TCP/IP port + queue created; Ready. If Windows had already added the printer itself (a WSD or IPP queue with a class driver), the run that creates the queue ends ready with warnings instead (silent: exit 1), and its note *Another printer entry may print to this printer* names Windows' queue. The unchanged second run asks nothing and creates no port or queue (the queue's driver identifies the printer also with SNMP off). |
| 7 | Multiple printers | Connect two supported printers (USB + network, or two USB); give the second one A4 defaults. Run Setup, then **Repair** from the result screen, then `--mode repair` without `branch.json`. Repeat with one printer that has two queues on the same port (one for tickets, one for A4), both on A4 and neither with the configured queue name; then put both back on A4, give the ticket queue the configured queue name (rename it to the name in `connection.newQueueName`, or set `queueName` in `branch.json` to its name) and repeat once more. | Selection screen lists both with model and connection; only the chosen one is changed. Repair changes only the ticket printer; the second printer keeps its A4 defaults. With two queues on one printer that cannot be told apart, Setup changes only one, chosen by its driver and name: the `DuplicateQueues` warning names the other, and Setup ends ready with warnings (*Printer added more than once*; silent: exit 1). With the ticket queue named so, only the ticket queue is changed and the A4 queue keeps its defaults. |
| 8 | Offline printer | Power off the printer after it was configured. Run the tool. | Configuration verified; warning "printer offline/not connected"; nothing broken. |
| 9 | No printer | Disconnect all printers; no TA queues. | "No supported printer found"; offers IP entry; nothing changed. |
| 10 | No internet | Disconnect the network/internet. (a) With the offline driver archive in `drivers\`. (b) Without it. | (a) Installs from the offline archive. (b) Clear "driver package unavailable" error; nothing half-installed. |
| 11 | Integrity/signature failure | Copy the distribution, modify one byte of the driver archive in its `drivers\` folder, set `driverSource.allowOnlineDownload` to `false` in that copy's config, remove `%ProgramData%\AMWAG\PrinterSetup\DriverCache`, run on a PC without the driver. (With downloads allowed, the tampered copy is skipped and the verified download is used instead — also correct.) | Integrity error before any installation; nothing installed. |
| 12 | Repeated execution | Run Setup three times, then Repair twice. | Exactly one queue, one port (network) and one `AMWAG Ticket` form in Print Server Properties; logs show "already satisfied". |
| 13 | Form rejected by driver | (Covered by automated tests; optional manual check) Use a test config with an impossible size, e.g. 300 × 600 mm, on a test PC. | "Driver does not support this paper size"; defaults not changed. |
| 14 | Settings reverted/overridden | After setup, change Printing Preferences as the employee, then run **Repair**; run **Check ticket software** and print one ticket from the ticket software. | Repair restores the settings; the check reports which settings the ticket software sends. |
| 15 | Test print outcome | Print Test Ticket: (a) normal; (b) printer out of paper; (c) printer offline/door open. | (a) "Windows sent the ticket" + manual confirmation prompt; (b)/(c) the problem is reported; never "printed" without the employee's confirmation. |
| 16 | Same printer, other spelling | Network printer with a DNS name: set it up by IP address, then put its DNS name in `branch.json` (`printerAddress`) and run Setup and Repair. Repeat the other way round (queue by name, branch by IP). | The existing queue is configured; no second port or queue appears; the report says the address is another spelling of that printer (`DuplicateQueues`, information). |
| 17 | Printer at the branch address cannot be identified, or is off | On a PC without a queue for the branch printer (a queue with its model's driver would identify it), disable SNMP on the printer named in `branch.json`; connect a second supported printer by USB. Run Setup and cancel the model question (*Which printer model is this?*); then run `--silent --mode setup` without and with `--model <id> --confirm-model --address <ip>`. Then switch the branch printer off (on a PC without its queue) and run Setup and `--silent --mode setup`. | Only the printer at the branch address is asked about; after cancelling the model choice the window asks for an address and, without one, changes nothing (the USB printer is not set up); silent without the switches exits 4 and names them; with them, only the branch printer is set up. With the branch printer off, the USB printer is not set up either: the window asks for an address, silent exits 3. |
| 18 | Earlier USB unit | On a PC where a ticket printer was set up, replace it with another unit of the same model (or connect the same model on another USB port). Run Setup, Repair and Diagnostics. | Setup uses the connected unit without a choice screen and reports the old unit as information only; afterwards Repair repairs the connected unit without asking and Diagnostics is Ready. Run before Setup (the old unit's queue still alone has the ticket paper), Repair asks which unit is the ticket printer (silent: exit 4) and Diagnostics warns, both naming the old queue and the remedy. Then switch the only USB printer off next to a network printer of the same model: the tool asks instead of picking the network printer. Then replace the ticket printer with a unit of another supported model, pool the connected unit's queue over both units' USB ports and put the earlier unit's model in `branch.json` (`modelId`): `--silent --mode setup` changes nothing and exits 8 (`QueueLeftUnchanged` names the pool), and the pool keeps its driver and defaults. |
| 19 | Closing the window during setup | Start Setup on a PC without the driver; after the UAC prompt, close the window while the driver installs. | The window shows *Stopping after the current step…*, waits, reads the result back and closes; the log shows `worker.cancelled`; running the tool again finishes the setup. |
| 20 | Configuration on a USB stick or Desktop copy | Copy the distribution to a USB stick (FAT/exFAT) and to the Desktop; change `amwag-printer-config.json` there (for example the queue name). Run the tool from each copy. | The log shows the file as ignored (`config.warning`) and the built-in configuration is used; the change has no effect. The same edit in a folder only administrators can change is used. |
| 21 | Choosing among printers that cannot report their model exactly | Two network printers of the same supported model on Standard TCP/IP queues with a Microsoft class driver, whose SNMP reports only a descriptive model (no IEEE 1284 ID or Kyocera model name; check the report's candidates for *Probable*), with no `printerAddress`. Run Setup and choose the ticket printer; run Setup again and choose none, then enter the ticket printer's IP address; run `--silent --mode setup`. Before each later part put the chosen queue back on the class driver with A4 defaults (Setup moves it to the approved driver). Then disable SNMP on both and run Setup again. | The two printers are offered as a choice first; the model question comes only for the chosen one, and only its queue changes. Choosing none leads to the address entry, and the printer entered is set up. Silent Setup exits 4, changes nothing and names `--address <ip> --model <id> --confirm-model` for each printer. With SNMP off, class-driver queues of printers that do not report their model are not found without an address: the window asks for an address (silent: exit 3). |
| 22 | Wrong model given by IT | With SNMP enabled on the ticket printer, run `--silent --address <its ip> --model <another supported model> --confirm-model`, then the same with `--mode repair`. | Both exit 4 and change nothing; the report's `NetworkIdentityMismatch` error names the model given and the model the printer reports. |
| 23 | Pooled or unusual queue port | Pool the ticket printer's USB queue over two USB ports (Printer properties → Ports → *Enable printer pooling*), run Setup and Repair. Then put a queue with the TA driver on an IPP or LPR port next to a second supported printer and run `--silent --mode setup`. Then pool a queue with the Microsoft PCL6 Class Driver over the Standard TCP/IP port of a supported network printer and a second TCP/IP port, put that printer's IP address in `branch.json`, and run `--silent --mode setup`. | The pooled USB queue is configured, keeps both ports and no second queue is created. The queue on the IPP/LPR port is left unchanged, and its `QueueLeftUnchanged` warning names the printer it prints to; the silent run exits 4 instead of setting up the other printer. With that printer's IP address in `branch.json` the run sets up nothing, names the queue and exits 3. With the class-driver pool nothing is set up, the `QueueLeftUnchanged` warning names the class-driver pool and the printer, and the run exits 3. |
| 24 | Two queues of one printer, the branch names its IP address | Give the ticket printer two DNS names (for example `printer12` and `printer12.branch.local`). As admin, add queue *A4 office* on a Standard TCP/IP port to `printer12` with the approved TA driver, and queue *Tickets* on a port to `printer12.branch.local` with a Microsoft class driver and the ticket paper set by hand; put the printer's IP address in `branch.json` (`printerAddress`). Run `--silent --mode setup` and `--silent --mode repair`, then Repair in the window, then Setup (choose *Tickets*); once with SNMP off and once with SNMP on. Before the second pass put *Tickets* back on the class driver (Setup moves the chosen queue to the approved driver). Then put *Tickets* back on the class driver once more, switch the printer off and run `--silent --mode setup` and `--silent --mode repair` again. | SNMP off: silent runs exit 4 and change nothing; the report names both queues with their hosts and the switches for *Tickets* (`--address printer12.branch.local …`); Repair asks only about *Tickets* (the queue on the ticket paper) and never changes *A4 office*; Setup offers both queues and only the chosen one changes. SNMP on (the printer reports its exact model): silent Setup exits 4 naming both queues with their hosts; silent Repair and Repair in the window repair *Tickets* without a question. Printer switched off: both silent runs exit 4 and change nothing (*A4 office* is not configured in place of *Tickets*). |
| 25 | Port classification | With the LPR Port Monitor feature installed, add (as admin) an LPR port (server = the ticket printer's IP address, queue `lp`) and an IPP port (`http://<ip>:631/ipp/print`), and a queue with the approved TA driver on each. Run `Get-PrinterPort \| Format-List Name,Description,PortMonitor`, then Diagnostics. | The report's port list shows them as `[Lpr]` and `[Internet]`, and each TA-driver queue names the printer's IP address in its `QueueLeftUnchanged` warning. If Windows reports other monitor or description strings, record them (for `SpoolerConversions.ClassifyPort`); such ports then count as ports without a host (information plus the possible-duplicate warning). |
| 26 | Class-driver ticket queue on an IPP port | As admin, give the ticket printer an A4 queue with the approved TA driver on a Standard TCP/IP port, and a queue *Tickets* with the Microsoft IPP Class Driver on its IPP port (`http://<ip>:631/ipp/print`) with the `AMWAG Ticket` form set by hand. Put its IP address in `branch.json` (`printerAddress`), then run `--silent --mode setup` and `--silent --mode repair`. Then set *Tickets* back to A4 and run Setup. | With *Tickets* on the ticket form, both silent runs exit 3, nothing changes, and the `QueueLeftUnchanged` warning names *Tickets* and the printer. With *Tickets* on A4, Setup configures only the A4 queue, the `DuplicateQueues` warning names *Tickets* (*Check which queue employees print to*), and the run ends ready with warnings (*Printer added more than once*). |

## 2. Physical test-ticket checks (each model, USB and network)

Load ticket stock in the multipurpose (MP) tray as the printer's manual describes; if the printer asks for the
MP tray paper size on its panel, set the custom size there. Print **Test Ticket** five times in a row.

- [ ] Ticket fed from the MP tray every time; no jams, no blank extra pages.
- [ ] Text upright (portrait), arrow marked TOP at the leading/top edge as expected for the ticket stock.
- [ ] Horizontal 50 mm bar measures 50 mm ± 0.5 mm; vertical 50 mm bar measures 50 mm ± 0.5 mm (scale 100 %).
- [ ] Corner marks: note how many millimetres of each corner mark are missing (unprintable margin); record values.
- [ ] Center lines: distance from the vertical center line to the left and right edges differs by ≤ 1 mm; same for
      the horizontal line to the top and bottom edges (alignment).
- [ ] No text cut off inside the dashed safe-area box.
- [ ] Results consistent across the five tickets (feed drift ≤ 1 mm).

## 3. Ticket software end-to-end (per branch software version)

- [ ] Ticket software uses the queue configured by the tool.
- [ ] **Check ticket software** shows paper `AMWAG Ticket`, portrait, MP tray, 100 % (or the overrides are understood
      and corrected in the ticket software's page setup).
- [ ] A real ticket prints with the same alignment as the test ticket.

## 4. Elevation and deployment

- [ ] Standard employee account: one UAC prompt (consent or administrator credentials) and no second prompt.
- [ ] Declining UAC leaves the PC unchanged and shows "nothing was changed" with Try again.
- [ ] IT-managed: `"AMWAG Printer Setup.exe" --silent --mode setup` run as SYSTEM/administrator through the
      deployment tool returns exit code 0 or 1 and writes a report; employees then run the tool without UAC.
- [ ] Over-the-shoulder elevation: as a standard employee, approve the UAC prompt with a *different* administrator
      account's credentials; the progress screen keeps updating and the run finishes (the worker reaches the window).
- [ ] The signed exe's UAC prompt shows the company as the verified publisher (if the exe is signed).
- [ ] A fresh Setup on an offline PC without the driver files adds no queue: the result offers *Try again*, not
      *Repair*, and says the printer has not been added to Windows.
- [ ] Repair of a connected printer that was never set up shows *Nothing to repair yet* with *Run Setup*, and *Run
      Setup* sets it up.
- [ ] The window started with `--address <ip> --model <another model> --confirm-model` shows *Model does not match this
      printer* naming both models; nothing was changed and *Try again* is not offered.
- [ ] Diagnostics in Arabic on a PC with two supported printers shows its result within a few seconds, headlined by the
      ticket printer's own findings.
- [ ] With `branch.json` `printerAddress` set to an unused IP address on a PC with a set-up office TA printer,
      Diagnostics shows *Ticket printer not found at its address* (the headline names the IP address) and offers no
      Repair; *Checking driver* and *Verification* read *Not checked*.
- [ ] With the ticket queue pooled over two Standard TCP/IP ports and `printerAddress` set to one of them, Setup (cancel
      the address prompt), Repair and Diagnostics show *The ticket printer cannot be set up automatically*, with the
      headline *Printer entry left unchanged* naming the queue; *Run Setup* is not offered and nothing is changed.
- [ ] Diagnostics with a set-up office printer next to the ticket printer: *Verification* follows the ticket printer's
      result, not the office printer's.
- [ ] Two TA printers connected by USB and never set up: Repair shows *Nothing to repair yet* and *Printers found, none
      set up yet*; *Run Setup* offers the choice.

## Sign-off

| Model | Windows 10 USB | Windows 10 network | Windows 11 USB | Windows 11 network | Tester / date |
|---|---|---|---|---|---|
| P-4030DN | | | | | |
| P-4531DN | | | | | |
| P-5032DN | | | | | |
| FS-2100DN | | | | | |
| FS-2100D | | n/a without network card | | n/a without network card | |
