<#
.SYNOPSIS
    Copies a HeatBridge Agent package to the Raspberry Pi over SSH and (optionally) runs the installer.

.DESCRIPTION
    1. Uploads the newest (or given) package from artifacts/agent to ~/heatbridge-agent-upload on the target.
    2. Verifies the SHA-256 checksum on the target.
    3. Unless -CopyOnly is given, extracts the package and runs "sudo bash install.sh" in an interactive SSH
       session, so you see every step and enter the sudo password yourself.

    Uses the OpenSSH client that ships with Windows (ssh/scp). Nothing on the Pi is changed outside
    ~/heatbridge-agent-upload except by install.sh (see deploy/agent/linux/install.sh).

.EXAMPLE
    ./deploy/agent/deploy.ps1 -Target pi@192.168.50.187

.EXAMPLE
    ./deploy/agent/deploy.ps1 -Target printpi -CopyOnly
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Target,
    [string] $Package,
    [switch] $CopyOnly
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

if (-not $Package) {
    $Package = Get-ChildItem (Join-Path $repo 'artifacts\agent\heatbridge-agent-*-linux-arm64.tar.gz') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Package) { throw 'No package found. Run ./deploy/agent/publish.ps1 first.' }
}

$file = Split-Path $Package -Leaf
$name = $file -replace '\.tar\.gz$', ''
$expected = (Get-FileHash -Algorithm SHA256 $Package).Hash.ToLowerInvariant()
$remoteDir = 'heatbridge-agent-upload'

Write-Host "==> Uploading $file to ${Target}:~/$remoteDir"
& ssh $Target "mkdir -p ~/$remoteDir"
if ($LASTEXITCODE -ne 0) { throw 'ssh failed' }
& scp $Package "${Target}:$remoteDir/"
if ($LASTEXITCODE -ne 0) { throw 'scp failed' }

$actual = (& ssh $Target "sha256sum ~/$remoteDir/$file" | Out-String).Split(' ')[0].Trim()
if ($actual -ne $expected) { throw "Checksum mismatch after upload (expected $expected, got $actual)." }
Write-Host "==> Checksum verified ($expected)"

$install = "set -e; cd ~/$remoteDir; rm -rf '$name'; tar -xzf '$file'; sudo bash '$name/install.sh'"
if ($CopyOnly) {
    Write-Host ''
    Write-Host 'Package copied. To install, run on the Pi:'
    Write-Host "  $install"
    return
}

Write-Host "==> Running the installer on $Target (you will be asked for your sudo password)"
& ssh -t $Target $install
if ($LASTEXITCODE -ne 0) { throw "Installer failed ($LASTEXITCODE)." }
