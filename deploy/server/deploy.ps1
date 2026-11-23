<# .SYNOPSIS
Preflight, upload, verify and install the Pi server using your existing SSH connection.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^(?:[A-Za-z0-9_.-]+@)?[A-Za-z0-9][A-Za-z0-9.-]*$')] [string] $Target,
    [ValidateRange(1, 65535)] [int] $Port = 22,
    [string] $IdentityFile,
    [string] $JumpHost,
    [string] $Package,
    [switch] $PreflightOnly,
    [switch] $CopyOnly
)
$ErrorActionPreference = 'Stop'
$sshOptions = @('-p', "$Port", '-o', 'ConnectTimeout=15', '-o', 'ServerAliveInterval=15', '-o', 'ServerAliveCountMax=3')
$scpOptions = @('-P', "$Port")
if ($IdentityFile) { $sshOptions += @('-i', $IdentityFile); $scpOptions += @('-i', $IdentityFile) }
if ($JumpHost) { $sshOptions += @('-J', $JumpHost); $scpOptions += @('-J', $JumpHost) }

# The preflight is read-only. Strip CR so this also works from Windows PowerShell 5.1.
$preflight = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'linux/preflight.sh'))
$preflight | & ssh @sshOptions -T $Target "tr -d '\r' | bash"
if ($LASTEXITCODE -ne 0) { throw 'Pi preflight failed; no services changed.' }
if ($PreflightOnly) { return }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $Package) {
    $Package = Get-ChildItem (Join-Path $repo 'artifacts/server/heatbridge-web-*-linux-arm64.tar.gz') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Package) { throw 'No server package found. Run ./deploy/server/publish.ps1 first.' }
}
$file = Split-Path $Package -Leaf
if ($file -notmatch '^heatbridge-web-[A-Za-z0-9.+_-]+-linux-arm64\.tar\.gz$') { throw 'Unexpected package filename.' }
$name = $file -replace '\.tar\.gz$', ''
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Package).Hash.ToLowerInvariant()
$remote = 'heatbridge-web-upload'
& ssh @sshOptions $Target "umask 077; mkdir -p ~/$remote"
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare upload directory.' }
& scp @scpOptions $Package "${Target}:$remote/"
if ($LASTEXITCODE -ne 0) { throw 'Upload failed.' }
$actual = (& ssh @sshOptions $Target "sha256sum ~/$remote/$file" | Out-String).Trim().Split(' ')[0]
if ($LASTEXITCODE -ne 0 -or $actual -ne $hash) { throw 'Uploaded package checksum verification failed.' }
Write-Host "Verified SHA-256: $hash"

# Names interpolated into this fixed shell program are restricted above; never replace existing extracts.
$install = "set -eu; cd ~/$remote; test ! -e '$name'; tar --no-same-owner -xzf '$file'; sudo bash '$name/install.sh'"
if ($CopyOnly) {
    Write-Host "Package copied. Run on the Pi: $install"
    return
}
& ssh @sshOptions -t $Target $install
if ($LASTEXITCODE -ne 0) { throw 'Installation failed; inspect the output and sudo heatbridge-webctl logs.' }
Write-Host "Open the dashboard tunnel: ./deploy/server/tunnel.ps1 -Target $Target -Port $Port"
