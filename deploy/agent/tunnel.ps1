<#
.SYNOPSIS
    Development only: makes the local HeatBridge server reachable from the Raspberry Pi through an SSH
    reverse tunnel, without opening ports on the router or the Windows firewall.

.DESCRIPTION
    Forwards 127.0.0.1:<RemotePort> on the Pi to localhost:<LocalPort> on this computer. The agent on the Pi
    then uses ws://127.0.0.1:5172/agent/connect. The forwarded port is bound to the Pi's loopback interface
    only (OpenSSH default), so nobody else on the network can use it. The tunnel stays up while this
    script runs; with -Persistent it is re-established automatically if it drops.

.EXAMPLE
    ./deploy/agent/tunnel.ps1 -Target pi@192.168.50.187 -Persistent
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Target,
    [int] $RemotePort = 5172,
    [int] $LocalPort = 5171,
    [switch] $Persistent
)

$forward = "127.0.0.1:${RemotePort}:localhost:${LocalPort}"
do {
    Write-Host "==> Tunnel: ${Target} 127.0.0.1:$RemotePort -> localhost:$LocalPort (Ctrl+C to stop)"
    & ssh -N -T -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=3 -R $forward $Target
    if ($Persistent) {
        Write-Host "Tunnel closed (exit code $LASTEXITCODE); reconnecting in 5 s..."
        Start-Sleep -Seconds 5
    }
} while ($Persistent)
