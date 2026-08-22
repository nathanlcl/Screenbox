<#
.SYNOPSIS
    Downloads the pinned libmpv dev package and places libmpv-2.dll into Screenbox/Native/<Platform>/.

.DESCRIPTION
    Reads the pin file libmpv.version from the repository root (shinchiro tag, per-architecture
    file names, SHA256). Download order (SPEC section 8.2):
      a. Mirror release on this repository: .../releases/download/libmpv-<tag>/<file>  (primary)
      b. Upstream shinchiro/mpv-winbuild-cmake release (fallback, also used for the first
         bootstrap before any mirror exists)
    The archive SHA256 is always verified against libmpv.version; a mismatch fails the build
    (no silent downgrade). Extraction prefers 7z (preinstalled on GitHub Windows runners) and
    falls back to tar (bsdtar ships with Windows 10+ and can read 7z archives).

.PARAMETER Platform
    Target architecture: x64, x86 or arm64.

.PARAMETER MirrorRepo
    GitHub repo (owner/name) hosting the libmpv-<tag> mirror release. Defaults to the
    GITHUB_REPOSITORY environment variable, then to huynhsontung/Screenbox.

.PARAMETER Force
    Re-download even if the local DLL already matches the pinned hash.

.EXAMPLE
    .\scripts\fetch-libmpv.ps1 -Platform x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('x64', 'x86', 'arm64')]
    [Alias('Architecture')]
    [string]$Platform,

    [Parameter(Mandatory = $false)]
    [string]$MirrorRepo = '',

    [Parameter(Mandatory = $false)]
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$VersionFile = Join-Path $RepoRoot 'libmpv.version'
if (-not (Test-Path $VersionFile)) {
    throw "Pin file not found: $VersionFile"
}
$Pin = Get-Content $VersionFile -Raw | ConvertFrom-Json

$Tag = $Pin.shinchiroTag
$ArchProp = $Pin.architectures.PSObject.Properties[$Platform]
if ($null -eq $ArchProp) {
    throw "libmpv.version has no entry for architecture '$Platform'."
}
$ArchInfo = $ArchProp.Value
$FileName = $ArchInfo.file
$ExpectedHash = $ArchInfo.sha256.ToLowerInvariant()
if ($FileName -notmatch '^[\w.\-]+$' -or $ExpectedHash -notmatch '^[0-9a-f]{64}$') {
    throw "libmpv.version entry for '$Platform' is malformed (file='$FileName', sha256='$ExpectedHash')."
}

if ([string]::IsNullOrWhiteSpace($MirrorRepo)) {
    $MirrorRepo = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'huynhsontung/Screenbox' }
}

$NativeDir = Join-Path (Join-Path (Join-Path $RepoRoot 'Screenbox') 'Native') $Platform
$DllPath = Join-Path $NativeDir 'libmpv-2.dll'
$HashSidecar = Join-Path $NativeDir 'libmpv-2.dll.hash'
$ExpectedSidecar = "$ExpectedHash  $FileName"

# Step 2: incremental build shortcut - DLL present and sidecar matches the pinned hash.
if (-not $Force -and (Test-Path $DllPath) -and (Test-Path $HashSidecar) -and
    ((Get-Content $HashSidecar -Raw).Trim() -eq $ExpectedSidecar)) {
    Write-Host "libmpv-2.dll for $Platform already matches pinned hash ($($ExpectedHash.Substring(0, 12))...). Skipping download."
    exit 0
}

# Step 3: download, mirror first, upstream fallback.
$Urls = [ordered]@{
    "mirror ($MirrorRepo)" = "https://github.com/$MirrorRepo/releases/download/libmpv-$Tag/$FileName"
    'upstream (shinchiro)' = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$Tag/$FileName"
}

$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "screenbox-libmpv-$Platform-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $TempDir | Out-Null
$ArchivePath = Join-Path $TempDir $FileName

try {
    $Downloaded = $false
    foreach ($source in $Urls.GetEnumerator()) {
        foreach ($attempt in 1..2) {
            Write-Host "Downloading $FileName from $($source.Key) (attempt $attempt): $($source.Value)"
            try {
                Invoke-WebRequest -Uri $source.Value -OutFile $ArchivePath -UseBasicParsing -TimeoutSec 300
                $Downloaded = $true
                Write-Host "Downloaded $((Get-Item $ArchivePath).Length) bytes."
                break
            }
            catch {
                Write-Warning "Download from $($source.Key) failed: $($_.Exception.Message)"
                if (Test-Path $ArchivePath) { Remove-Item $ArchivePath -Force }
            }
        }
        if ($Downloaded) { break }
    }
    if (-not $Downloaded) {
        throw "Failed to download $FileName from all sources."
    }

    # Step 4: SHA256 verification - fail hard on mismatch, never silently downgrade.
    $ActualHash = (Get-FileHash -Path $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ActualHash -ne $ExpectedHash) {
        throw "SHA256 mismatch for $FileName. Expected: $ExpectedHash Actual: $ActualHash. Refusing to use an untrusted archive."
    }
    Write-Host "SHA256 verified: $ActualHash"

    # Step 5: extract libmpv-2.dll.
    $ExtractDir = Join-Path $TempDir 'extracted'
    New-Item -ItemType Directory -Force -Path $ExtractDir | Out-Null

    $SevenZip = $null
    foreach ($candidate in @('7z.exe', '7z', "$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe")) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (Get-Command $candidate -ErrorAction SilentlyContinue) { $SevenZip = $candidate; break }
        if (Test-Path $candidate) { $SevenZip = $candidate; break }
    }

    if ($SevenZip) {
        Write-Host "Extracting with 7z: $SevenZip"
        & $SevenZip x $ArchivePath "-o$ExtractDir" -y | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "7z extraction failed with exit code $LASTEXITCODE." }
    }
    else {
        Write-Host '7z not found, falling back to tar (bsdtar).'
        & tar.exe -xf $ArchivePath -C $ExtractDir
        if ($LASTEXITCODE -ne 0) { throw "tar extraction failed with exit code $LASTEXITCODE." }
    }

    $Dll = Get-ChildItem -Path $ExtractDir -Recurse -Filter 'libmpv-2.dll' | Select-Object -First 1
    if ($null -eq $Dll) {
        throw "libmpv-2.dll not found inside $FileName."
    }

    New-Item -ItemType Directory -Force -Path $NativeDir | Out-Null
    Copy-Item $Dll.FullName -Destination $DllPath -Force

    # Step 6: write hash sidecar for incremental runs.
    Set-Content -Path $HashSidecar -Value $ExpectedSidecar -NoNewline -Encoding ascii
    Write-Host "Installed $DllPath"
}
finally {
    Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
}
