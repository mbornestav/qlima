<#
.SYNOPSIS
    Builds a self-contained linux-arm64 release package of HeatBridge Agent.

.DESCRIPTION
    Produces artifacts/agent/heatbridge-agent-<version>-linux-arm64.tar.gz (+ .sha256) containing:
      app/                       self-contained agent and native runtime (no .NET installation needed)
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
if (Test-Path -LiteralPath $staging) { throw "Release already exists: $staging" }
New-Item -ItemType Directory -Force $appDir | Out-Null

# Explicit settings also work in clean checkouts, where user publish profiles are absent.
# Keep runtime files alongside the executable so the hardened service needs no bundle extraction directory.
& dotnet publish $project -c Release -r $Runtime --self-contained true -o $appDir `
    -p:PublishSingleFile=false -p:PublishTrimmed=false `
    "-p:InformationalVersion=$version" --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
if (-not (Test-Path -LiteralPath (Join-Path $appDir 'libcoreclr.so'))) { throw 'Package is missing the native .NET runtime.' }
if (-not (Test-Path -LiteralPath (Join-Path $appDir 'HeatBridge.Agent.dll'))) { throw 'Agent assembly is missing.' }
$runtimeConfig = Get-Content -Raw (Join-Path $appDir 'HeatBridge.Agent.runtimeconfig.json') | ConvertFrom-Json
if (-not $runtimeConfig.runtimeOptions.includedFrameworks) { throw 'Agent package must include its .NET runtime.' }

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
