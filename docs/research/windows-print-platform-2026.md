# Windows printing platform status for vendor v3/v4 drivers, as of 2026-11-03 (label: platform-2026)

## Summary

- **Installing a signed vendor driver from a local package is still supported** on Windows 11. No date has been announced after which third-party drivers stop being installable. Microsoft's own text says so (section 1).
- **Three things since 2025 can still break the planned flow:**
  - **Windows Ready Print driver ranking.** Since the June 23, 2026 update KB5095093, Windows prefers its built-in IPP class driver for new printer installs on IPP-capable USB or discovered network printers. It is on by default. A TA printer that supports IPP-over-USB may get the Microsoft IPP queue instead of the KX driver, even when KX is staged.
  - **Windows protected print mode (WPP).** It is opt-in on 24H2 and later, but CIS Level 2 and Microsoft say it will become default "at a future date" (no date given). When on, it removes all third-party-driver queues and blocks new ones.
  - **Administrator protection.** Generally available since August 2026; Microsoft's security baselines turn it on with the October 2026 update. Elevated processes then run under a separate hidden admin profile, so per-user (HKCU) printing settings written while elevated land in the wrong profile.
- **Point and Print hardening (`RestrictDriverInstallationToAdministrators`) does not block an elevated local install.** Other policies can block even administrators (section 3).
- **pnputil exit codes** (documented): 0, 259 (`ERROR_NO_MORE_ITEMS`: no matching device, or the device already has a better or newer driver), 3010 (reboot required), 1641 (reboot started). Exit code 259 is also what you can expect when ranking prefers the IPP class driver.

---

## 1. End-of-servicing plan for third-party printer drivers

Source: https://learn.microsoft.com/en-us/windows-hardware/drivers/print/end-of-servicing-plan-for-third-party-printer-drivers-on-windows. Downloaded live today: `ms.date` 2025-05-09, `updated_at` 2025-07-18. The newest GitHub commit is 2025-07-17, so the timeline has not changed since the "May 2025 update â€“ Printer driver timeline date adjustment".

| Date | Exact plan text | Status on 2026-11-03 |
|---|---|---|
| September 2023 | "Announced legacy third-party printer driver for Windows end of servicing plan." | Done |
| January 15, 2026 | "For Windows 11+ and Windows Server 2025+, no new printer drivers will be published to Windows Update. Existing printer drivers on Windows Update can still be updated but only approved on a case-by-case basis." | In effect. A Microsoft spokesperson confirmed it on 2026-02-19 (quoted below). |
| July 1, 2026 | "Printer driver ranking order modified to always prefer Windows IPP inbox class driver." | **Shipped.** Details under "How the July 2026 change shipped". |
| July 1, 2027 | "Except for security-related fixes, third-party printer driver updates will no longer be allowed. Existing third-party printer drivers can be installed from Windows Update or users can install printer drivers by using an installation package provided by the print device manufacturer." | Upcoming |

**FAQ statements (exact text):**
- "Windows will continue to allow vendor-supplied printer drivers to be installed via separate installation packages."
- "Existing printer drivers can be installed on Windows PCs, even after the end of servicing."
- "This change applies to Windows 11+ and Windows Server 2025+."
- "Microsoft will continue to issue security fixes related to the legacy printer driver platform while the Windows OS version is still within Microsoft Support Lifecycle."
- "There are no plans to disable print features specifically related to the legacy printer driver platform."
- Since January 15, 2026, driver signing is case-by-case. The listed conditions under which a package can still be signed are: "A printer that cannot be Mopria certified.", "Driver package submissions specifying a ceiling of Windows 10 or lower, will continue to be signed.", "Native ARM64 printer drivers."

**Microsoft spokesperson, via Windows Central, update dated Feb 19, 2026:** "Windows has not ended support for legacy printer drivers. If your printer works with Windows today, it will continue to work, and no action is required. As of Jan 15, 2026, legacy drivers submitted to Windows Hardware Quality Labs and published to Windows Update will only be approved on a case-by-case basisâ€¦ An update to the Windows Roadmap stated that Windows will no longer support V3 and V4 printer driversâ€”this update was inaccurate and has since been removed."
https://www.windowscentral.com/microsoft/windows-11/windows-11-finally-pulls-the-plug-on-legacy-printer-drivers-starting-january-2026

### How the July 2026 change shipped

- **KB5095093**, June 23, 2026 preview, OS builds 26200.8737 / 26100.8737. It sits in the "Gradual rollout" tab; I checked the page HTML. Exact text:
  > "[Printing] New! New printer installations use Internet Printing Protocol (IPP) by default when supported, simplifying setup and improving reliability. â€¦ To control this behavior, use the toggle in Settings > Bluetooth & devices > Printers & scanners > Default install printers using Windows Ready Print."

  https://support.microsoft.com/help/5095093
- **KB5101650**, July 14, 2026 security update, builds 26200.8875 / 26100.8875. It "includes new features and quality improvements that were part of" KB5095093. https://support.microsoft.com/help/5101650
- **Microsoft Tech Community post**, partner news 2026-06-09 and IT Pro blog 2026-06-12:
  - "Starting in July 2026, new printer installations will default to Windows Ready Print where supported"
  - "The configuration applies to new printer installations only, without affecting existing devices."
  - Group Policy: Administrative Templates > Printers > "Configure Windows Ready Print driver ranking".

  https://techcommunity.microsoft.com/blog/partnernews/introducing-windows-ready-print-and-modernized-driver-selection/4526895
- **Printers Policy CSP**, `ms.date` 2026-10-14, `ConfigureWindowsReadyPrintDriverRanking`:
  - OMA-URI: `./Device/Vendor/MSFT/Policy/Config/Printers/ConfigureWindowsReadyPrintDriverRanking`
  - Type `int`; 0 = Disabled; 1 = Enabled (default).
  - Applies to "Windows 11, version 24H2 with KB5095093 [10.0.26100.8737] and later".
  - Description: "When enabled, Windows will prefer the Microsoft IPP Class Driver for printers that support the Internet Printing Protocol (IPP)."
  - The registry key is **not documented**. The page's Group Policy path reads "Printing > AT > Printers", as printed.

  https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-printers
- **Windows 11 26H2 security baseline**, 2026-09-29. It sets the policy to Enabled and states its scope:
  > "driver ranking applies when printers are installed via a connection method that supports IPP, such as USB or network multicast discovery. In those cases, if the printer supports IPP, Windows will install using the class driver even if a vendor V3/V4 driver is available. If the printer doesn't support IPP, or is installed directly as a TCP/IP printer, there's no change in behavior."

  https://techcommunity.microsoft.com/blog/microsoft-security-baselines/windows-11-version-26h2-security-baseline/4560382
- Whether the ranking change was backported to 23H2 or 26H1 is UNVERIFIED; the CSP lists only 24H2 with KB5095093 and later.

### Is a local install of a signed vendor v3 package still supported?

| OS | OS lifecycle (Microsoft Lifecycle / release health) | Local vendor-package install |
|---|---|---|
| Win 11 26H2 (build 26300) | Home/Pro supported to 10/10/2028; Enterprise/Education to 10/9/2029 | Supported. Caveats: WPP, ranking, policies. |
| Win 11 25H2 (26200) | Home/Pro to 10/13/2027; Enterprise/Education to 10/11/2028 | Supported, same caveats |
| Win 11 24H2 (26100) | **Home/Pro: "As of October 13, 2026 â€¦ have reached end of updates."** Enterprise/Education until October 12, 2027 | Still works technically, but the OS itself is unsupported on Home/Pro |
| Win 10 22H2 (19045) | Support ended October 14, 2025. Commercial ESU "maximum of three years". Consumer ESU: Microsoft's page now says "through October 12, 2027" (extended in mid-2026; press corroborates). | Supported. The end-of-servicing plan does not apply (it applies to Windows 11+). No WPP or ranking change; ESU is security-only. |

**Kyocera notice that conflicts with Microsoft.** Kyocera US (https://www.kyoceradocumentsolutions.us/en/support/microsoft-printing-support.html) says:
- "â€¦in July 2027, traditional print drivers will stop being supported altogether." This contradicts Microsoft's July 2027 text; treat Microsoft as authoritative for Windows behaviour.
- "End users can currently utilize Kyocera print drivers, but they will no longer be signed or WHQL certified by Microsoft, â€¦" (the page continues: signed by Kyocera instead).

Whether current TA KX packages are WHQL- or Kyocera-signed is UNVERIFIED here. It matters because of the signing prompt described in section 3.

---

## 2. Windows protected print mode (WPP)

**Windows versions.** Windows 11 24H2 (10.0.26100) and later, per the CSP. Kyocera's notice: "Windows11, version 24H2 â€¦ includes 'Windows Protected Print Mode' as a new feature". Not available on Windows 10.

**Default.** CSP: "By default, Windows protected print isn't enabled". FAQ: "Windows protected print mode will be enabled by default at a future date." No date announced (FAQ updated 2026-06-18). CIS Microsoft Windows 11 Enterprise v5.0.0 L2, item 18.7.9: "Ensure 'Configure Windows protected print' is set to 'Enabled'".

**How it is turned on:**
- **Settings:** Settings > Bluetooth & Devices > Printers & scanners > Printer Preferences > Windows protected print mode > **Set up** (to disable: **Turn Off**). "If Windows protected print mode is enabled as group policy, you won't be able to disable it."
- **Group Policy:** Computer Configuration > Administrative Templates > Printers > **Configure Windows protected print** (Printing.admx).
- **Registry (from the CSP's ADMX mapping):** `HKLM\Software\Policies\Microsoft\Windows NT\Printers\WPP`, value **`WindowsProtectedPrintGroupPolicyState`**. The data value meaning (presumably 1 = enabled) is UNVERIFIED; Printing.admx is not present on this Home test machine.
- **Intune/CSP:** `./Device/Vendor/MSFT/Policy/Config/Printers/ConfigureWindowsProtectedPrint`, String `<enabled/>`. Editions listed: Pro, Enterprise, Education, IoT.
- **UNVERIFIED (third-party guides only, e.g. woshub):** values `WindowsProtectedPrintMode` and `WindowsProtectedPrintOobeConfigComplete` (DWORD) under the same WPP key, which the guides attribute to the Settings toggle.

**Effect on existing third-party queues** (https://learn.microsoft.com/en-us/windows/modern-print/windows-protected-print-mode/windows-protected-print-mode, updated 2026-09-02):
- "Upon enabling Windows protected print mode, printers that use third-party drivers are uninstalled."
- "The print driver is deleted from the print driver store, and it can't be used while Windows protected print mode is active."
- A Mopria-certified printer that was installed with a third-party driver "is uninstalled, but can be reinstalled. When reinstalled, it uses Windows Ready Print". Even after WPP is turned off again, it stays on Windows Ready Print until removed and reinstalled.
- XPS Document Writer and Fax are removed.
- FAQ: "If you disable Windows protected print mode, you'll have to manually reinstall any non-compatible printers."
- Kyocera (2024-12-26): "Restoring the WPP setting does not recover the removed drivers." https://www.kyoceradocumentsolutions.com/global/en/support-and-download/security-information/info_20241226.html

**Effect on new installs:**
- "Windows protected print mode prevents the installation of third-party drivers" (enterprise page).
- "Non-compatible printers are uninstalled and will be not able to be reinstalled while Windows protected print mode is enabled." (FAQ)
- "Devices that do not support Windows Ready Print cannot be installed." (Tech Community post)
- "prevents Point and Print from ever installing third-party drivers" and "many legacy APIs are updated to restrict the configuration to values that make sense only when using IPP" (developer page).
- The exact error returned by pnputil, `InstallPrinterDriverFromPackage` or `AddPrinter` under WPP is UNVERIFIED. A third-party guide (woshub) quotes the message "Adding a printer driver is not supported right now because Windows protected print is turned on".

**Detection from code:**
- **Primary, documented API:** `Windows.Graphics.Printing.ProtectedPrint.WindowsProtectedPrintInfo.IsProtectedPrintEnabled`, declared `public static bool IsProtectedPrintEnabled { get; }`. Introduced in Windows 11 24H2 (10.0.26100.0), `UniversalApiContract` v19.
  https://learn.microsoft.com/en-us/uwp/api/windows.graphics.printing.protectedprint.windowsprotectedprintinfo.isprotectedprintenabled
- **Tested on this machine** from an unpackaged desktop process: the type is present, contract v19 is present, and `IsProtectedPrintEnabled` returns False.
- **Project impact:** the projects currently target `net10.0-windows`, which does not give access to this API. Use `net10.0-windows10.0.26100.0`; the installed .NET SDK 10.0.401 lists that target as supported. Set `SupportedOSPlatformVersion` lower to keep Windows 10, and guard the call:
  ```csharp
  bool? wpp = null;
  if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) &&
      Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Graphics.Printing.ProtectedPrint.WindowsProtectedPrintInfo"))
      wpp = Windows.Graphics.Printing.ProtectedPrint.WindowsProtectedPrintInfo.IsProtectedPrintEnabled;
  ```
- **Fallback:** read the policy value above. It only reflects the Group Policy / MDM setting.

---

## 3. Point and Print / PrintNightmare hardening and other blockers

**`RestrictDriverInstallationToAdministrators`**
- KB5005652: "Windows updates released August 10, 2021 and later will, by default, require administrative privilege to install drivers."
- Registry: `HKEY_LOCAL_MACHINE\Software\Policies\Microsoft\Windows NT\Printers\PointAndPrint`, DWORD `RestrictDriverInstallationToAdministrators`. Value 1 or absent means administrators only.
- KB text: "If you are not using Point and Print, you should not be affected by this change". https://support.microsoft.com/help/5005652
- Policy CSP (22H2+), "Limits print driver installation to Administrators": "By default, users that aren't Administrators can't install print drivers on this computer."
- **Relevance:** an elevated admin process installing from a local package is not blocked. A non-elevated process with a filtered UAC token would be. The utility must run elevated.

**Policies that can block even an elevated administrator:**
- **WPP** (section 2).
- **`ConfigureDriverValidationLevel`** ("Manage Print Driver signature validation", Windows 11 22H2+). Key `Software\Policies\Microsoft\Windows NT\Printers\Driver`. Values:
  - 0 = inbox only
  - 1 = adds the "PrintDrivers" certificate store
  - 2 = adds WHQL
  - 3 = adds the Trusted Publishers store
  - 4 (default) = "Allow all validly signed drivers"
- **`ManageDriverExclusionList`**: SHA256 hash of the INF or main driver DLL; same `...\Printers\Driver` key.
- **Device Installation Restrictions** under `Software\Policies\Microsoft\Windows\DeviceInstall\Restrictions`: `DenyDeviceClasses`, `DenyDeviceIDs`, `DenyInstanceIDs`, `DenyUnspecified`, `AllowDenyLayered`. The Printer class GUID is `{4d36e979-e325-11ce-bfc1-08002be10318}` (read with `pnputil /enum-classes` on this machine).
- **Driver signature trust** (https://learn.microsoft.com/en-us/windows-hardware/drivers/install/signature-categories-and-driver-installation). If a KX package is signed by a vendor certificate rather than WHQL, an administrator install of a "publisher of unknown trust" shows a Windows Security prompt. "signed by an untrusted publisher" means Windows does not install the package; it logs to `setupapi.dev.log`.
- **Windows Driver Policy** (April 2026): "This feature applies to kernel-mode drivers only". It does not apply to user-mode print drivers.

**Administrator protection**
- Generally available with KB5120998 (2026-08-27). "The feature is off by default"; security baselines enable it "with the October 2026 Windows security update".
- An elevated process runs under "a hidden, system-generated, profile-separated user account".
- Microsoft's developer blog: "default registry hive for the current user will map to SMAA user's registry hive" (SMAA = the hidden system-managed admin account).
- "Network Drives/Resources Inaccessible from elevated apps": copy driver packages to a local disk before elevating.

---

## 4. PnP behaviour after staging while a USB printer is connected

**What is documented:**
- `pnputil /add-driver <inf> /install`: "install/update drivers on any matching devices".
  - "If the driver is not the highest ranked driver on the system, PnPUtil will not force it onto the device."
  - Check with `pnputil /enum-devices /instanceid <id> /drivers`.
- Installing a driver on a printer device triggers the printer class installer, which creates a queue.
- **Duplicate check:** "If the class installer finds an installed print queue whose driver and port names match these two parameters, it does not install a second print queue, but instead associates it with the devnode entry." This page dates from 2017; nothing newer contradicts it.
- **Queue name:** v4 setup-concepts page: "For v3 drivers, the print queue name was dictated first by the driver name, then by the user." and "Initially, the print queue name is set to the driver name." So a KX v3 PnP queue is named after the INF model string. The exact string is UNVERIFIED here; see the INF research.
  - A v4 driver may be renamed afterwards using Bidi FriendlyName, or Manufacturer + ModelName, or the IEEE 1284 ID.
  - A same-name duplicate typically gets a "Copy1" suffix. KB5014666 known issue: "duplicate copies of printers â€¦ (commonly with a similar name and the suffix "Copy1"), and applications which refer to the printer by a specific name cannot print."
- **Port:** USB virtual port from the "USB Monitor" (usbmon.dll on this machine), commonly named `USB001`, `USB002`. Not formally documented on current Learn pages: UNVERIFIED.
- **Delay:** no Microsoft-documented service level. Upper bound per device-installation policy: "If you disable or don't configure this policy setting, Windows waits 240 seconds for a device installation task to complete before terminating the installation." Typical timing is UNVERIFIED.

**What to watch for:**
- **Ranking interaction:**
  - Inbox `prnms012.inf` ("Microsoft IPP Class Driver", `DriverVer` 10.0.26100.9711 on this machine) contains `MS_IPP_PREF,1284_CID_MS_IPP_PREF` with `FeatureScore=0x00`.
  - Microsoft's ranking doc: lower rank is better, and an unspecified feature score is `0x00FF0000`.
  - `usbprint.h` has `IOCTL_USBPRINT_ADD_MSIPP_COMPAT_ID` ("Mark whether the MSIPP compat id should be added").
  - `IOCTL_USBPRINT_GET_INTERFACE_TYPE` returns `USB_PRINTER_INTERFACE_CLASSIC`=1 ("7-1-2"), `_IPP`=2 ("7-1-4"), `_DUAL`=3.
  - Expected outcome: on a dual-mode or IPP USB printer with ranking on, the IPP class driver wins and pnputil returns 259. Inferred from those files, not from a Microsoft statement: UNVERIFIED.
  - Whether the TA P-4030DN, P-4531DN or P-5032DN expose IPP-over-USB, or are Mopria-certified, is UNVERIFIED.
- **`UploadPrinterDriverPackage` + `InstallPrinterDriverFromPackage`** stage the package and install the spooler driver only. Whether that alone makes PnP retry an already-present devnode that has no driver is UNVERIFIED. Trigger it explicitly with `pnputil /add-driver /install` or `UpdateDriverForPlugAndPlayDevicesW` (signatures in section 6), or create the queue yourself on the existing USB port.
- **Suggested approach:**
  1. Watch for the new queue with `FindFirstPrinterChangeNotification(PRINTER_CHANGE_ADD_PRINTER=0x1)` and `EnumPrinters(PRINTER_ENUM_LOCAL=0x2)`. Match on the USB port, the driver name and the MS IPP driver name.
  2. Treat a "Microsoft IPP Class Driver" queue or a "Copy1" name as a finding.
  3. After a timeout of about 240 s, fall back to an explicit `AddPrinter`.
  4. For network printers, use a TCP/IP port; the baseline says there is no ranking change for those.

---

## 5. Forms, per-user preferences, IPP class driver

- **`AddForm` / forms database:** I found no 2025â€“2026 Microsoft change. The `AddForm` page has `ms.date` 2018-05-31 and `updated_at` 2021-01-07. Under WPP, "legacy APIs â€¦ restrict the configuration"; the effect on `AddForm` is UNVERIFIED.
- **Per-user printing preferences:** `PRINTER_INFO_9`: "The DEVMODE is stored in the user's registry." Under Administrator protection, an elevated write goes to the hidden admin profile.
  - Set global defaults (`PRINTER_INFO_2`/`8`) while elevated.
  - Do anything per-user (level 9, the default printer) in the non-elevated user process.
  - Related policy: `LegacyDefaultPrinterMode`, at `Software\Microsoft\Windows NT\CurrentVersion\Windows` (user scope), "Turn off Windows default printer management".
- **IPP class driver and custom sizes:** no Microsoft document confirms that Windows user forms or arbitrary custom sizes work: UNVERIFIED.
  - Michael Sweet on the PWG list, 2021: "Windows 10's IPP class driver only supports well-known media sizes".
  - OpenPrinting/cups issue #1017, 2025 community reports: Windows 11 24H2 supports custom sizes when the printer advertises custom ranges, "but sadly, not Windows 10".
  - Microsoft Q&A 1190652: forms appear as generic "UserForm###" entries; IPP class driver reportedly ignores `media-size-supported` ranges.
  - Microsoft's standard PCL6/PS class drivers support only "Letter, A4".
  - The Universal Print connector drops page sizes it cannot map (e.g. a vendor-named "ns0001:ISOA6_PAPER" entry).
  - A lab test is required before relying on the IPP fallback.
- **New IPP policies (Printers CSP):**
  - `RequireIppsPolicy`: key `...\Printers\IPP`, value `RequireIpps`; 24H2 with KB5062660.
  - `ConfigureIppTlsCertificatePolicy`: 24H2 with KB5052093.
  - `ConfigureIppPageCountsPolicy`: value `AlwaysSendIppPageCounts`.
- **Release health (2026-11-02):** no open or recent printing known issues for 24H2, 25H2, 26H2 or Windows 10 22H2.

---

## 6. pnputil (docs: `ms.date` 2024-01-08; local help on build 26300.9711)

- **Syntax:** `PNPUTIL /add-driver <filename.inf | *.inf> [/subdirs] [/install] [/reboot]`. Available since Windows 10 1607, so it works on Windows 10 22H2. The legacy form `-a [-i]` still works. Must run elevated.
- **`/subdirs`:** traverse sub-directories. **`/install`:** install or update on matching devices. **`/reboot`:** reboot if needed (do not use in the utility).
- **Return values, documented** (https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-return-values):
  - `ERROR_SUCCESS` (0)
  - `ERROR_NO_MORE_ITEMS` (259): "No devices match the supplied driver or the target device is already using a better or newer driver than the driver specified for installation."
  - `ERROR_SUCCESS_REBOOT_REQUIRED` (3010): "if the `/install /add-driver` options were specified, one or more devices were successfully installed and a system reboot is required"
  - `ERROR_SUCCESS_REBOOT_INITIATED` (1641)
- Microsoft's advice: "to get actionable return values we recommend specifying `/install /add-driver` with one INF at a time". Logs are in `%windir%\inf\setupapi.dev.log`.
- That the package stays staged when 259 is returned is an inference: UNVERIFIED. Confirm with `pnputil /enum-drivers`.
- **Build 26300 help shows flags not documented on Learn:**
  - `/enum-drivers`: `/ids`, `/devices`, `/format <txt|xml|csv>`, `/output-file`
  - `/enum-devices`: `/location`, `/format`, `/output-file`

  Whether these exist on older builds is UNVERIFIED. `/enum-devices /deviceids` requires Windows 11 21H2 or later.
- **Related APIs** (local SDK 10.0.26100.0 headers):
  - `UpdateDriverForPlugAndPlayDevicesW(HWND, LPCWSTR HardwareId, LPCWSTR FullInfPath, DWORD InstallFlags, PBOOL)`; `INSTALLFLAG_FORCE` 0x1, `INSTALLFLAG_NONINTERACTIVE` 0x4
  - `DiInstallDriverW`; `DIIRFLAG_FORCE_INF` 0x2
  - `UploadPrinterDriverPackageW`: `UPDP_SILENT_UPLOAD` 0x1, `UPDP_UPLOAD_ALWAYS` 0x2, `UPDP_CHECK_DRIVERSTORE` 0x4. Doc: "Only signed driver packages can be uploaded."
  - `InstallPrinterDriverFromPackageW(pszServer, pszInfPath, pszDriverName, pszEnvironment, dwFlags)`: `IPDFP_COPY_ALL_FILES` 0x1
  - `FORM_USER` 0, `FORM_BUILTIN` 1, `FORM_PRINTER` 2

---

## 7. Read-only observations on this machine

Windows 11 26H2 Home, build 26300.9711. Nothing on the machine was changed.

- `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Printers\DriverRanking\UseWindowsReadyPrintDriverRanking` = 1 (DWORD), present on a Home edition without Group Policy. It is likely the backing value for the ranking toggle or policy, but that mapping is UNVERIFIED.
- No `...\Printers\WPP` or `...\Printers\PointAndPrint` keys exist.
- Print monitors: "USB Monitor" -> usbmon.dll; "WSD Port" -> APMon.dll.
- PrintQueue device class GUID: `{1ed2bbf9-11f0-4084-b21f-ad83a8e6dcdc}`.

## 8. Open items (UNVERIFIED)

- Whether the three TA models support IPP-over-USB or are Mopria-certified.
- What value the WPP policy key holds when enabled, and the registry values written by the Settings toggles.
- The exact error when installing under WPP.
- Typical PnP install delay.
- Whether `UploadPrinterDriverPackage` alone triggers a PnP install.
- Whether the IPP class driver honours custom forms.
- Whether TA KX packages are WHQL- or Kyocera-signed.
- Exact end date of commercial ESU year 3.

## Kyocera/TA notices

- **WPP notice** (Kyocera, 2024-12-26): linked in section 2.
- **KX driver installer vulnerability** (Kyocera and TA, 2026-09-30): "A DLL loading vulnerability exists in the KX Driver installer"; fixed in "KX Driver version 8.6A.1412".
  - https://www.kyoceradocumentsolutions.com/global/en/support-and-download/security-information/info_20260930.html
  - https://www.triumph-adler.com/ta-en-de/legal-informations/security-advice/vulnerability-in-the-printer-driver-kx-driver

## Evidence files

Downloaded pages and extracted text are in `C:\Users\abdel\AppData\Local\Temp\claude\C--Users-abdel-Desktop-Amwag-Printer\7737442f-5f1d-46cc-85b0-3b47c5e9c693\scratchpad\research\platform-2026\`: one `dl-*` folder per page, and extracted text under `scripts\*.txt`. The live end-of-servicing page has SHA256 `e723c904d470c5f45fc703b21d092c0d8d0fb6509ceb62ef6350ab446daa1039`.