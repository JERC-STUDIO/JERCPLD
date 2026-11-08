param(
    [string]$DepsDir
)

$ErrorActionPreference = 'Stop'
if (-not $DepsDir) { $DepsDir = Join-Path $PSScriptRoot 'deps' }
New-Item -ItemType Directory -Force $DepsDir | Out-Null
$tmp = Join-Path $env:TEMP 'jercpld-deps'
New-Item -ItemType Directory -Force $tmp | Out-Null

function Get-RemoteFile([string]$Url, [string]$OutFile) {
    if (Test-Path $OutFile) { return }
    $urls = @($Url)
    if ($Url -like 'https://github.com/*') {
        $urls += ($Url -replace '^https://github.com/', 'https://ghfast.top/https://github.com/')
    }
    foreach ($u in $urls) {
        Write-Output "download $u"
        & curl.exe -sL --fail --retry 3 --max-time 600 -o $OutFile $u
        if ((Test-Path $OutFile) -and (Get-Item $OutFile).Length -gt 1024) { return }
        if (Test-Path $OutFile) { Remove-Item $OutFile -Force }
    }
    throw "download failed: $Url"
}

$zipDeps = @(
    @{ Name = 'fmt-11.2.0'; Url = 'https://github.com/fmtlib/fmt/archive/refs/tags/11.2.0.zip'; Check = 'include\fmt\format.h' },
    @{ Name = 'glm-1.0.1'; Url = 'https://github.com/g-truc/glm/archive/refs/tags/1.0.1.zip'; Check = 'glm\glm.hpp' },
    @{ Name = 'gsl-4.2.0'; Url = 'https://github.com/microsoft/GSL/archive/refs/tags/v4.2.0.zip'; Check = 'include\gsl\gsl' },
    @{ Name = 'leveldb-1.23'; Url = 'https://github.com/google/leveldb/archive/refs/tags/1.23.zip'; Check = 'include\leveldb\db.h' },
    @{ Name = 'rapidjson-master'; Url = 'https://github.com/Tencent/rapidjson/archive/refs/heads/master.zip'; Check = 'include\rapidjson\document.h' },
    @{ Name = 'type_safe-0.2.4'; Url = 'https://github.com/foonathan/type_safe/archive/refs/tags/v0.2.4.zip'; Check = 'include\type_safe\strong_typedef.hpp' },
    @{ Name = 'magic_enum-0.9.7'; Url = 'https://github.com/Neargye/magic_enum/archive/refs/tags/v0.9.7.zip'; Check = 'include\magic_enum\magic_enum.hpp' },
    @{ Name = 'pcg-cpp-master'; Url = 'https://github.com/imneme/pcg-cpp/archive/refs/heads/master.zip'; Check = 'include\pcg_random.hpp' },
    @{ Name = 'ctre-3.8.1'; Url = 'https://github.com/hanickadot/compile-time-regular-expressions/archive/refs/tags/v3.8.1.zip'; Check = 'include\ctre.hpp' },
    @{ Name = 'concurrentqueue-master'; Url = 'https://github.com/cameron314/concurrentqueue/archive/refs/heads/master.zip'; Check = 'concurrentqueue.h' },
    @{ Name = 'parallel-hashmap-master'; Url = 'https://github.com/greg7mdp/parallel-hashmap/archive/refs/heads/master.zip'; Check = 'parallel_hashmap\phmap.h' }
)

foreach ($dep in $zipDeps) {
    $target = Join-Path $DepsDir $dep.Name
    if (Test-Path (Join-Path $target $dep.Check)) {
        Write-Output "skip $($dep.Name)"
        continue
    }
    $zip = Join-Path $tmp "$($dep.Name).zip"
    Get-RemoteFile $dep.Url $zip
    $extract = Join-Path $tmp "$($dep.Name)-extract"
    Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    $inner = Get-ChildItem $extract -Directory | Select-Object -First 1
    if ($inner) { Move-Item $inner.FullName $target -Force } else { Move-Item $extract $target -Force }
    if (-not (Test-Path (Join-Path $target $dep.Check))) { throw "unexpected layout: $($dep.Name)" }
    Write-Output "ok $($dep.Name)"
}

# 单文件依赖
$enttDir = Join-Path $DepsDir 'entt\entt'
New-Item -ItemType Directory -Force $enttDir | Out-Null
$entt = Join-Path $enttDir 'entt.hpp'
Get-RemoteFile 'https://cdn.jsdelivr.net/gh/skypjack/entt@v3.16.0/single_include/entt/entt.hpp' $entt
if ((Get-Item $entt).Length -lt 3000000) { throw 'entt.hpp looks truncated' }
@'
#pragma once
#include "entt/entt.hpp"
'@ | Set-Content -Path (Join-Path $enttDir 'fwd.hpp') -Encoding ascii

$expectedDir = Join-Path $DepsDir 'expected-lite-0.8.0\include\nonstd'
New-Item -ItemType Directory -Force $expectedDir | Out-Null
Get-RemoteFile 'https://cdn.jsdelivr.net/gh/martinmoene/expected-lite@0.10.0/include/nonstd/expected.hpp' (Join-Path $expectedDir 'expected.hpp')

$nlohmannDir = Join-Path $DepsDir 'nlohmann\nlohmann'
New-Item -ItemType Directory -Force $nlohmannDir | Out-Null
Get-RemoteFile 'https://cdn.jsdelivr.net/gh/nlohmann/json@v3.12.0/single_include/nlohmann/json.hpp' (Join-Path $nlohmannDir 'json.hpp')

$stbDir = Join-Path $DepsDir 'stb'
New-Item -ItemType Directory -Force $stbDir | Out-Null
Get-RemoteFile 'https://cdn.jsdelivr.net/gh/nothings/stb@master/stb_truetype.h' (Join-Path $stbDir 'stb_truetype.h')

Write-Output "deps ready: $DepsDir"
