<# .SYNOPSIS
Privately access the Pi dashboard at http://localhost:5171 through SSH. Ctrl+C closes access.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^(?:[A-Za-z0-9_.-]+@)?[A-Za-z0-9][A-Za-z0-9.-]*$')] [string] $Target,
    [ValidateRange(1, 65535)] [int] $Port = 22,
    [ValidateRange(1, 65535)] [int] $LocalPort = 5171,
    [string] $IdentityFile,
    [string] $JumpHost,
    [switch] $Persistent
)
$options = @('-p', "$Port", '-N', '-T', '-o', 'ExitOnForwardFailure=yes', '-o', 'ConnectTimeout=15',
    '-o', 'ServerAliveInterval=15', '-o', 'ServerAliveCountMax=3', '-L', "127.0.0.1:${LocalPort}:127.0.0.1:5171")
if ($IdentityFile) { $options += @('-i', $IdentityFile) }
if ($JumpHost) { $options += @('-J', $JumpHost) }
do {
    Write-Host "Dashboard: http://localhost:$LocalPort (keep this terminal open; Ctrl+C to stop)"
    & ssh @options $Target
    if (-not $Persistent) {
        if ($LASTEXITCODE -ne 0) { throw "SSH tunnel ended with exit code $LASTEXITCODE." }
        break
    }
    Write-Host 'SSH disconnected. Reconnecting in 5 seconds...'
    Start-Sleep -Seconds 5
} while ($Persistent)
