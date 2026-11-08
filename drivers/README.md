# Offline driver packages

Put the approved driver archives listed in `config/amwag-printer-config.json` (`driverPackages[].fileName`)
in this folder before running `build.ps1`. The build copies an archive into the distribution only after
confirming that its size and SHA-256 match the approved manifest; a mismatching file stops the build.

At run time, AMWAG Printer Setup looks for the archive in the distribution's `drivers` folder first, then in
its local cache (`%ProgramData%\AMWAG\PrinterSetup\DriverCache`), and only then downloads it from the approved
official URL (when `driverSource.allowOnlineDownload` is true). Every copy is checked against the manifest's
size and SHA-256 and the catalog signature before anything is installed.

Do not rename, re-zip or extract the archives: the SHA-256 is computed over the original file exactly as
downloaded from TA Triumph-Adler.
