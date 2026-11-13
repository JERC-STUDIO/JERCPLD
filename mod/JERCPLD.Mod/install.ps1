param(
    [string]$ServerDir = 'd:\ylcs',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$modRoot = $PSScriptRoot

if (-not $SkipBuild) {
    & (Join-Path $modRoot 'build.bat')
    if ($LASTEXITCODE -ne 0) { throw 'mod build failed' }
}

$dll = Join-Path $modRoot 'build\JERCPLD.dll'
if (-not (Test-Path $dll)) { throw "missing $dll, build first" }

$target = Join-Path $ServerDir 'plugins\JERCPLD'
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item $dll (Join-Path $target 'JERCPLD.dll') -Force
Copy-Item (Join-Path $modRoot 'manifest.json') (Join-Path $target 'manifest.json') -Force
Copy-Item (Join-Path $modRoot 'tooth.json') (Join-Path $target 'tooth.json') -Force -ErrorAction SilentlyContinue

$ptlDll = Join-Path $modRoot 'build\JERCPLD.PTL.dll'
if (-not (Test-Path $ptlDll)) { throw "missing $ptlDll, build first" }
$ptlDst = Join-Path $target 'PTL'
New-Item -ItemType Directory -Force $ptlDst | Out-Null
Copy-Item $ptlDll (Join-Path $ptlDst 'JERCPLD.PTL.dll') -Force

$configDir = Join-Path $target 'config'
New-Item -ItemType Directory -Force $configDir | Out-Null
$configPath = Join-Path $configDir 'config.json'
if (-not (Test-Path $configPath)) {
    $config = @'
{
  "translationEnabled": true,
  "serverVersion": "1.26.51",
  "serverProtocol": 2193,
  "acceptMinProtocol": 2193,
  "acceptMaxProtocol": 2225
}
'@
    [IO.File]::WriteAllText($configPath, $config, (New-Object System.Text.UTF8Encoding($false)))
}

Write-Output "installed to $target"
Get-ChildItem $target | Select-Object Name, Length
