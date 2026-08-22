<#
.SYNOPSIS
    Downloads the pinned libmpv dev packages for all architectures from the upstream
    shinchiro/mpv-winbuild-cmake release and verifies their SHA256 against libmpv.version.

.DESCRIPTION
    Maintenance/bootstrap helper (SPEC section 8.3). It only downloads and verifies; the
    companion workflow .github/workflows/mirror-libmpv.yml publishes the verified archives
    as assets of this repository's "libmpv-<tag>" release. Daily CI must never depend on the
    upstream release being online, so this runs once per libmpv version bump.

.PARAMETER Tag
    shinchiro release tag to mirror. Must match libmpv.version's shinchiroTag; update the pin
    file first when bumping versions. Defaults to the pinned tag.

.PARAMETER OutputDirectory
    Directory receiving the verified .7z files. Created when missing.

.EXAMPLE
    .\scripts\mirror-libmpv.ps1 -OutputDirectory .\mirror-out
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Tag = '',

    [Parameter(Mandatory = $false)]
    [string]$OutputDirectory = 'mirror-out'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$VersionFile = Join-Path $RepoRoot 'libmpv.version'
if (-not (Test-Path $VersionFile)) {
    throw "Pin file not found: $VersionFile"
}
$Pin = Get-Content $VersionFile -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = $Pin.shinchiroTag
}
elseif ($Tag -ne $Pin.shinchiroTag) {
    throw "Requested tag '$Tag' does not match libmpv.version shinchiroTag '$($Pin.shinchiroTag)'. Update libmpv.version (with verified SHA256 hashes) before mirroring a new version."
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

foreach ($arch in @('x86', 'x64', 'arm64')) {
    $ArchProp = $Pin.architectures.PSObject.Properties[$arch]
    if ($null -eq $ArchProp) {
        throw "libmpv.version has no entry for architecture '$arch'."
    }
    $ArchInfo = $ArchProp.Value
    $FileName = $ArchInfo.file
    $ExpectedHash = $ArchInfo.sha256.ToLowerInvariant()
    $Url = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$Tag/$FileName"
    $Dest = Join-Path $OutputDirectory $FileName

    Write-Host "[$arch] Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing

    $ActualHash = (Get-FileHash -Path $Dest -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ActualHash -ne $ExpectedHash) {
        Remove-Item $Dest -Force
        throw "[$arch] SHA256 mismatch for $FileName. Expected: $ExpectedHash Actual: $ActualHash. Refusing to mirror an unverified archive."
    }
    Write-Host "[$arch] SHA256 verified: $ActualHash"
}

Write-Host "All packages downloaded and verified into $OutputDirectory"
