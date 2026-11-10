# Builds the FileFluss for Windows release files.
#
#   powershell -File windows/Packaging/build-release.ps1 -Version 1.0.0|1.0.0-beta.1 [-Architectures x64,arm64] [-SkipInstaller] [-RequireInstaller]
#
# For each architecture: a self-contained publish (no .NET install needed on the target), a
# portable .zip, and — when Inno Setup's iscc.exe is available — the per-user installer.
# SHA256SUMS.txt covers every file; the in-app updater refuses an installer it cannot match
# against it.
#
# Without Inno Setup the zips and SHA256SUMS.txt are still built and the installer is
# skipped with a warning. -RequireInstaller (the release workflow passes it) turns that
# warning into a failure, so a published release can never silently lack its installers.
#
# Runs under Windows PowerShell 5.1 and PowerShell 7.
#
# Output: windows/artifacts/release/

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+(-(alpha|beta|rc)\.\d+)?$')] [string] $Version,
    [string[]] $Architectures = @('x64', 'arm64'),
    [switch] $SkipInstaller,
    [switch] $RequireInstaller
)

$ErrorActionPreference = 'Stop'
$windows = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $windows 'artifacts'
$release = Join-Path $artifacts 'release'
$project = Join-Path $windows 'src\FileFluss.App\FileFluss.App.csproj'

function Invoke-Checked([string] $File, [string[]] $Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File exited with $LASTEXITCODE" }
}

# Optional Authenticode signing: set FILEFLUSS_SIGN_PFX (path to a .pfx) and
# FILEFLUSS_SIGN_PASSWORD. Unsigned builds work, but SmartScreen warns on first run.
$signtool = if ($env:FILEFLUSS_SIGN_PFX) {
    Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}

function Sign([string[]] $Files) {
    if (-not $env:FILEFLUSS_SIGN_PFX) { return }
    if (-not $signtool) { throw 'FILEFLUSS_SIGN_PFX is set but signtool.exe was not found.' }
    Invoke-Checked $signtool (@('sign', '/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256',
        '/f', $env:FILEFLUSS_SIGN_PFX, '/p', $env:FILEFLUSS_SIGN_PASSWORD) + $Files)
}

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

$buildInstaller = -not $SkipInstaller
if ($buildInstaller -and -not $iscc) {
    if ($RequireInstaller) { throw 'Inno Setup 6 (iscc.exe) was not found. Install it, or drop -RequireInstaller.' }
    Write-Warning 'Inno Setup 6 (iscc.exe) was not found: building the portable zips only, no installer.'
    $buildInstaller = $false
}

# Only now, once nothing can stop the build for a missing tool, clear the previous output.
foreach ($folder in @((Join-Path $artifacts 'publish'), $release)) {
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
}
New-Item -ItemType Directory -Force $release | Out-Null

# The parts of a publish that are easy to lose to a packaging change and that the app
# cannot run properly without. Each one has gone missing from some .NET app's release before.
function Assert-Publish([string] $Folder, [string] $Arch) {
    $required = @(
        'FileFluss.exe',
        'FileFluss.Core.dll',
        # Microsoft.Data.Sqlite's native engine, per RID: without it the search index fails at first use.
        'e_sqlite3.dll',
        # Logos and the app icon, loaded from disk at run time.
        'Assets\FileFluss.ico',
        'Assets\FileFluss.png',
        'Assets\ProviderLogos',
        # Satellite assemblies: without them the app silently falls back to English.
        'de\FileFluss.Core.resources.dll',
        'zh-Hans\FileFluss.Core.resources.dll',
        'zh-Hant\FileFluss.Core.resources.dll'
    )
    foreach ($item in $required) {
        if (-not (Test-Path (Join-Path $Folder $item))) { throw "The $Arch publish is missing $item." }
    }

    $stamped = (Get-Item (Join-Path $Folder 'FileFluss.exe')).VersionInfo.ProductVersion
    if (-not $stamped -or -not $stamped.StartsWith($Version)) {
        throw "FileFluss.exe in the $Arch publish carries version '$stamped', expected $Version."
    }
}

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# Entry by entry with '/' separators. Neither Compress-Archive nor ZipFile.CreateFromDirectory
# will do under Windows PowerShell 5.1: both write backslashes into entry names there, which
# other unzip tools treat as part of the file name.
function New-Zip([string] $Folder, [string] $Zip) {
    $root = (Resolve-Path $Folder).Path.TrimEnd('\') + '\'
    $archive = [System.IO.Compression.ZipFile]::Open($Zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $Folder -Recurse -File) {
            $name = $file.FullName.Substring($root.Length).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
}

foreach ($arch in $Architectures) {
    $rid = "win-$arch"
    $publish = Join-Path $artifacts "publish\$arch"
    Write-Host "== $rid =="

    Invoke-Checked dotnet @('publish', $project, '-o', $publish, '-c', 'Release', '-r', $rid, '--self-contained', 'true',
        "-p:Version=$Version", '-p:DebugType=none', '-nologo')

    Assert-Publish $publish $arch
    Sign @(Join-Path $publish 'FileFluss.exe')

    New-Zip $publish (Join-Path $release "FileFluss-$Version-$arch-portable.zip")

    if (-not $buildInstaller) { continue }
    # Windows' file-version field is numbers only; a beta's suffix stays in AppVersion.
    Invoke-Checked $iscc @('/Q', "/DAppVersion=$Version", "/DNumericVersion=$($Version -replace '-.*', '')", "/DArch=$arch", "/DSource=$publish", (Join-Path $PSScriptRoot 'FileFluss.iss'))
    Sign @(Join-Path $release "FileFluss-Setup-$Version-$arch.exe")
}

# "<sha256>  <name>" — the format sha256sum -c and the in-app updater both read.
$sums = Get-ChildItem $release -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
Set-Content -Path (Join-Path $release 'SHA256SUMS.txt') -Value $sums -Encoding ascii
Get-ChildItem $release | Format-Table Name, Length
