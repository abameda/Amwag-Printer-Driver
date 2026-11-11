<#
.SYNOPSIS
    Builds, tests and packages AMWAG Printer Setup as a self-contained, single-file Windows x64 executable.

.DESCRIPTION
    1. Restores and builds the solution.
    2. Runs the automated tests (hardware-independent unit tests and read-only Windows checks).
    3. Publishes "AMWAG Printer Setup.exe" (self-contained: no .NET install needed on branch PCs).
    4. Copies the deployment configuration and every approved offline driver archive found in .\drivers
       after re-checking its size and SHA-256 against the manifest (a mismatching archive stops the build).
    5. Copies the IT documentation, writes SHA256SUMS.txt and zips the distribution folder.
    6. Zips it once more without the driver archives: the much smaller online package.

    Output: .\dist\AMWAG Printer Setup\,  .\dist\AMWAG-Printer-Setup-<version>-win-x64.zip  (offline, with drivers)
            and  .\dist\AMWAG-Printer-Setup-<version>-win-x64-online.zip  (drivers downloaded when a PC needs one)

.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -SkipTests -NoDrivers
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    # Package without running the tests (not recommended for a deployment build).
    [switch] $SkipTests,

    # Do not include offline driver archives; branches will download the approved package when needed.
    [switch] $NoDrivers
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$solution = Join-Path $root 'AmwagPrinterSetup.sln'
$appProject = Join-Path $root 'src\Amwag.PrinterSetup.App\Amwag.PrinterSetup.App.csproj'
$configFile = Join-Path $root 'config\amwag-printer-config.json'
$driverSource = Join-Path $root 'drivers'
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'AMWAG Printer Setup'

function Invoke-Step([string] $Name, [scriptblock] $Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit code $LASTEXITCODE)." }
}

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
Write-Host "AMWAG Printer Setup $version ($Configuration), dotnet SDK $(& dotnet --version)"

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Invoke-Step 'Restore' { dotnet restore $solution --nologo }
Invoke-Step 'Build' { dotnet build $solution -c $Configuration --no-restore --nologo }

if (-not $SkipTests) {
    # Screenshot tests only render UI previews for designers; everything else must pass.
    Invoke-Step 'Test' { dotnet test $solution -c $Configuration --no-build --nologo --filter 'Category!=Screenshots' }
}

Invoke-Step 'Publish' {
    dotnet publish $appProject -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -o $stage --nologo
}

# The publish step already placed amwag-printer-config.json next to the exe; check it is the repository copy.
$publishedConfig = Join-Path $stage 'amwag-printer-config.json'
if (-not (Test-Path $publishedConfig)) { throw "Publish output is missing amwag-printer-config.json." }
if ((Get-FileHash $publishedConfig).Hash -ne (Get-FileHash $configFile).Hash) { throw "Published configuration differs from $configFile." }

$config = Get-Content $configFile -Raw | ConvertFrom-Json
if (-not $config.ticket.dimensionsConfirmed) {
    Write-Warning ("Ticket dimensions are NOT confirmed in config\amwag-printer-config.json. " +
        "Employees will not be able to run Setup/Repair until IT measures the ticket stock and sets " +
        "ticket.dimensionsConfirmed, confirmedBy and confirmedOn (see docs\DEPLOYMENT.md).")
}

if (-not $NoDrivers) {
    $driverTarget = Join-Path $stage 'drivers'
    foreach ($package in $config.driverPackages) {
        $archive = Join-Path $driverSource $package.fileName
        if (-not (Test-Path $archive)) {
            Write-Warning "Offline driver archive not found: $archive. Branch PCs will download '$($package.id)' from the approved URL when needed."
            continue
        }
        $item = Get-Item $archive
        $hash = (Get-FileHash $archive -Algorithm SHA256).Hash
        if ($item.Length -ne [long]$package.sizeBytes -or $hash -ne $package.sha256.ToUpperInvariant()) {
            throw "Offline archive $archive does not match the approved manifest (size $($item.Length), SHA-256 $hash)."
        }
        New-Item -ItemType Directory -Force -Path $driverTarget | Out-Null
        Copy-Item $archive -Destination $driverTarget
        Write-Host "    included approved driver archive $($package.fileName)"
    }
}

$docsTarget = Join-Path $stage 'docs'
New-Item -ItemType Directory -Force -Path $docsTarget | Out-Null
Copy-Item (Join-Path $root 'README.md') -Destination $stage
foreach ($doc in 'DEPLOYMENT.md', 'TROUBLESHOOTING.md', 'HARDWARE-ACCEPTANCE-CHECKLIST.md', 'TICKET-SOFTWARE-INTEGRATION.md', 'DRIVER-MANIFEST.md') {
    $path = Join-Path $root "docs\$doc"
    if (Test-Path $path) { Copy-Item $path -Destination $docsTarget }
}

function Write-Checksums([string] $Folder) {
    $sums = Get-ChildItem $Folder -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Folder.Length + 1)
        "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $relative
    }
    Set-Content -Path (Join-Path $Folder 'SHA256SUMS.txt') -Value $sums -Encoding UTF8
}

function Get-SizeText([string] $Path) { "$([math]::Round((Get-Item $Path).Length / 1MB, 1)) MB" }

Write-Checksums $stage
$zip = Join-Path $dist "AMWAG-Printer-Setup-$version-win-x64.zip"
Compress-Archive -Path $stage -DestinationPath $zip

# The driver archives are already compressed, so they stay most of the full package whatever the zip settings. The online
# package leaves them out: a PC that needs a driver downloads the approved archive from its official URL and checks its
# size, SHA-256 and signature first, so it needs internet access then. Kyocera's URL carries no version: if Kyocera
# replaces that file, the download is refused and the branch needs the full package (see docs\DRIVER-MANIFEST.md).
$onlineZip = $null
if ((Test-Path (Join-Path $stage 'drivers')) -and $config.driverSource.allowOnlineDownload) {
    $onlineRoot = Join-Path $dist 'online'
    $onlineStage = Join-Path $onlineRoot 'AMWAG Printer Setup'
    New-Item -ItemType Directory -Force -Path $onlineStage | Out-Null
    Get-ChildItem $stage | Where-Object Name -notin 'drivers', 'SHA256SUMS.txt' | Copy-Item -Destination $onlineStage -Recurse
    Write-Checksums $onlineStage
    $onlineZip = Join-Path $dist "AMWAG-Printer-Setup-$version-win-x64-online.zip"
    Compress-Archive -Path $onlineStage -DestinationPath $onlineZip
    Remove-Item -Recurse -Force $onlineRoot
}

$exe = Get-Item (Join-Path $stage 'AMWAG Printer Setup.exe')
Write-Host ""
Write-Host "Executable : $($exe.FullName) ($(Get-SizeText $exe.FullName))" -ForegroundColor Green
Write-Host "Package    : $zip ($(Get-SizeText $zip), offline: includes the driver archives)" -ForegroundColor Green
if ($onlineZip) {
    Write-Host "Online     : $onlineZip ($(Get-SizeText $onlineZip), downloads the approved driver when a PC needs one)" -ForegroundColor Green
}
