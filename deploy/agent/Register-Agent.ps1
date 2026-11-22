<#
.SYNOPSIS
    Registers an agent's secret hash with the local development server (dotnet user-secrets).

.DESCRIPTION
    The agent secret is generated on the agent (sudo heatbridge-agentctl new-secret) and never leaves it.
    Only its hash (sha256:<hex>) is stored on the server. For development this script stores the hash in
    the web project's user secrets, outside the repository. In production, provide
    HeatBridge__RemoteAgents__Agents__<index>__SecretHash as an environment variable instead.

    -Index is the agent's position in HeatBridge:RemoteAgents:Agents (printpi is 0 in appsettings.Development.json).

.EXAMPLE
    ./deploy/agent/Register-Agent.ps1 -SecretHash sha256:3f5a...

.EXAMPLE
    # Generate a secret on this computer instead (e.g. to run the agent locally):
    ./deploy/agent/Register-Agent.ps1 -GenerateForLocalAgent
#>
[CmdletBinding(DefaultParameterSetName = 'Hash')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Hash')] [string] $SecretHash,
    [Parameter(Mandatory, ParameterSetName = 'Generate')] [switch] $GenerateForLocalAgent,
    [int] $Index = 0
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$web = Join-Path $repo 'src\HeatBridge.Web'
$agent = Join-Path $repo 'src\HeatBridge.Agent'

if ($GenerateForLocalAgent) {
    # Works in Windows PowerShell 5.1 and PowerShell 7.
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $secret = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hashBytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($secret)) } finally { $sha.Dispose() }
    $SecretHash = 'sha256:' + (($hashBytes | ForEach-Object { $_.ToString('x2') }) -join '')
    & dotnet user-secrets set 'HeatBridge:Agent:Secret' $secret --project $agent | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets failed' }
    Write-Host 'Stored a new secret in the local agent''s user secrets.'
}

if ($SecretHash -notmatch '^sha256:[0-9a-fA-F]{64}$') { throw 'SecretHash must look like sha256:<64 hex digits>.' }
& dotnet user-secrets set "HeatBridge:RemoteAgents:Agents:${Index}:SecretHash" $SecretHash.ToLowerInvariant() --project $web | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets failed' }
Write-Host "Registered $SecretHash for agent index $Index in the web project's user secrets."
Write-Host 'Restart the server with: dotnet run --project src/HeatBridge.Web --launch-profile http-agent'
