param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$vcvars = "D:\VS\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
$llSrc = "D:\ylcs\_tools\ll-src\LeviLamina-26.51.5\src"
$llLib = "D:\ylcs\_tools"

if (-not (Test-Path $vcvars)) {
    throw "找不到 vcvars64.bat: $vcvars"
}
if (-not (Test-Path (Join-Path $llSrc "ll\api\mod\NativeMod.h"))) {
    throw "找不到 LeviLamina 头文件: $llSrc"
}
if (-not (Test-Path (Join-Path $llLib "LeviLamina.lib"))) {
    throw "找不到 LeviLamina 导入库: $llLib\LeviLamina.lib"
}

$outDir = Join-Path $root "build"
New-Item -ItemType Directory -Force $outDir | Out-Null

$cmd = @(
    "call `"$vcvars`" >nul",
    "cl /nologo /std:c++20 /EHa /MD /utf-8 /W4",
    "/DNDEBUG /D_ITERATOR_DEBUG_LEVEL=0 /D_HAS_CXX23=1",
    "/DNOMINMAX /DUNICODE /D_UNICODE /DLL",
    "/DENTT_PACKED_PAGE=128 /DENTT_SPARSE_PAGE=4096 /DENTT_NO_MIXIN /DLL_PLAT_S",
    "/I`"$PSScriptRoot\src`" /I`"$llSrc`"",
    "`"$PSScriptRoot\src\JerCpldMod.cpp`"",
    "/Fe:`"$outDir\JERCPLD.dll`"",
    "/link /LIBPATH:`"$llLib`" LeviLamina.lib"
) -join " "

Push-Location $PSScriptRoot
try {
    & cmd.exe /c "`"$PSScriptRoot\build.bat`""
    if ($LASTEXITCODE -ne 0) {
        throw "编译失败，退出码 $LASTEXITCODE"
    }
} finally {
    Pop-Location
}

Copy-Item -Force (Join-Path $PSScriptRoot "manifest.json") (Join-Path $outDir "manifest.json")
Write-Host "已生成: $outDir\JERCPLD.dll"
