<# .SYNOPSIS
Build a self-contained Linux ARM64 HeatBridge server package, including dashboard assets and installer.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $repo 'src/HeatBridge.Web/HeatBridge.Web.csproj'
$outputRoot = Join-Path $repo 'artifacts/server'
$commit = (& git -C $repo rev-parse --short HEAD)
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine Git revision.' }
if (& git -C $repo status --porcelain) { $commit = "$commit.dirty" }
$baseVersion = ([xml](Get-Content -Raw $project)).Project.PropertyGroup.Version
$version = "$baseVersion-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$commit"
$name = "heatbridge-web-$version-linux-arm64"
$staging = Join-Path $outputRoot $name
if (Test-Path -LiteralPath $staging) { throw "Release already exists: $staging" }
$appDir = New-Item -ItemType Directory -Path (Join-Path $staging 'app')

& dotnet publish $project -c Release -r linux-arm64 --self-contained true -o $appDir.FullName `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:ServerGarbageCollection=false "-p:InformationalVersion=$version" --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

$utf8 = [Text.UTF8Encoding]::new($false)
foreach ($file in Get-ChildItem (Join-Path $PSScriptRoot 'linux') -File) {
    $contents = [IO.File]::ReadAllText($file.FullName).Replace("`r`n", "`n")
    [IO.File]::WriteAllText((Join-Path $staging $file.Name), $contents, $utf8)
}
[IO.File]::WriteAllText((Join-Path $staging 'VERSION'), "$version`n", $utf8)
& (Join-Path $PSScriptRoot 'verify-package.ps1') -Directory $staging

$archive = Join-Path $outputRoot "$name.tar.gz"
& tar -C $outputRoot -czf $archive $name
if ($LASTEXITCODE -ne 0) { throw 'Could not create release archive.' }
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $name.tar.gz`n", $utf8)
Write-Host "Package: $archive"
Write-Host "SHA-256: $hash"
Write-Host 'Next: ./deploy/server/deploy.ps1 -Target pi@<host> -Port <ssh-port>'
