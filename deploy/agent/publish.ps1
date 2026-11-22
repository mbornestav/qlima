<#
.SYNOPSIS
    Builds a self-contained linux-arm64 release package of HeatBridge Agent.

.DESCRIPTION
    Produces artifacts/agent/heatbridge-agent-<version>-linux-arm64.tar.gz (+ .sha256) containing:
      app/                       self-contained single-file agent (no .NET needed on the Pi)
      install.sh                 installer/updater (run with sudo on the Pi)
      heatbridge-agentctl        management tool
      heatbridge-agent.service   systemd unit
      agent.example.json         configuration template
      agent.env.example          optional environment overrides
      VERSION
    The version is unique per build (<csproj version>-<UTC timestamp>-<git commit>), so every package can be
    installed side by side and rolled back.

.EXAMPLE
    ./deploy/agent/publish.ps1
#>
[CmdletBinding()]
param(
    [string] $Runtime = 'linux-arm64'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repo 'src\HeatBridge.Agent\HeatBridge.Agent.csproj'
$outputRoot = Join-Path $repo 'artifacts\agent'

$baseVersion = ([regex]::Match((Get-Content -Raw $project), '<Version>([^<]+)</Version>')).Groups[1].Value
if (-not $baseVersion) { throw "No <Version> in $project" }
$commit = (& git -C $repo rev-parse --short HEAD)
if (-not $commit) { $commit = 'nogit' }
if (& git -C $repo status --porcelain) { $commit = "$commit.dirty" }
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss')
$version = "$baseVersion-$stamp-$commit"
$name = "heatbridge-agent-$version-$Runtime"
$staging = Join-Path $outputRoot $name
$appDir = Join-Path $staging 'app'

Write-Host "==> Publishing HeatBridge Agent $version for $Runtime"
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force $appDir | Out-Null

# Settings come from the publish profile: self-contained, single file, no trimming.
& dotnet publish $project -p:PublishProfile=$Runtime -r $Runtime -o $appDir "-p:InformationalVersion=$version" --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Linux files must have LF line endings and no BOM, whatever the Git checkout settings are.
$utf8 = New-Object System.Text.UTF8Encoding($false)
foreach ($file in 'install.sh', 'heatbridge-agentctl', 'heatbridge-agent.service', 'agent.example.json', 'agent.env.example') {
    $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "linux\$file")) -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $staging $file), $text, $utf8)
}
[IO.File]::WriteAllText((Join-Path $staging 'VERSION'), "$version`n", $utf8)

$archive = Join-Path $outputRoot "$name.tar.gz"
& tar.exe -C $outputRoot -czf $archive $name
if ($LASTEXITCODE -ne 0) { throw "tar failed ($LASTEXITCODE)" }
$hash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $name.tar.gz`n", $utf8)

$binary = Get-Item (Join-Path $appDir 'HeatBridge.Agent')
Write-Host ''
Write-Host "Package : $archive"
Write-Host "SHA-256 : $hash"
Write-Host ("Binary  : {0:N1} MB" -f ($binary.Length / 1MB))
Write-Host 'Contents:'
Get-ChildItem $appDir | ForEach-Object { Write-Host ("  app/{0} ({1:N0} bytes)" -f $_.Name, $_.Length) }
Write-Host ''
Write-Host "Next: ./deploy/agent/deploy.ps1 -Target <user>@192.168.50.187"
