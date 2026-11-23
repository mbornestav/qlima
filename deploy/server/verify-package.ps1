[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Directory)
$ErrorActionPreference = 'Stop'
foreach ($relative in @('VERSION', 'install.sh', 'preflight.sh', 'heatbridge-webctl', 'heatbridge-web.service', 'server-config.py',
    'app/HeatBridge.Web', 'app/HeatBridge.Web.dll', 'app/libcoreclr.so', 'app/HeatBridge.Web.runtimeconfig.json',
    'app/HeatBridge.Web.staticwebassets.endpoints.json', 'app/wwwroot/app.css', 'app/wwwroot/HeatBridge.Web.styles.css',
    'app/Microsoft.Data.Sqlite.dll', 'app/libe_sqlite3.so', 'configure-mailjet.sh')) {
    if (-not (Test-Path -LiteralPath (Join-Path $Directory $relative) -PathType Leaf)) { throw "Package is missing $relative" }
}
foreach ($file in Get-ChildItem (Join-Path $Directory 'app') -Recurse -File) {
    if ($file.Name -match '(?i)^(appsettings\.(Local|Development).*\.json|agent.*\.json|secrets?\..*|mailjet-.*|\.env.*|.*\.(pfx|pem|key|db|db-wal|db-shm))$') {
        throw "Machine settings or credentials must not be packaged: $($file.Name)"
    }
}
$runtime = Get-Content -Raw (Join-Path $Directory 'app/HeatBridge.Web.runtimeconfig.json') | ConvertFrom-Json
if (-not $runtime.runtimeOptions.includedFrameworks) { throw 'Package must include its .NET runtime.' }
foreach ($file in Get-ChildItem $Directory -File | Where-Object Extension -ne '.json') {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($bytes -contains 13 -or ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) {
        throw "Linux payload must have LF endings and no BOM: $($file.Name)"
    }
}
Write-Host 'Package verified: runtime, dashboard assets and clean production configuration.'
