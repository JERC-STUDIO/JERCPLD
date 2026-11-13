param(
    [string]$ModRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$ptlDir = Join-Path $ModRoot 'PTL'
$outFile = Join-Path $ModRoot 'ptl\ptl_rules_generated.h'

$pairs = @()
if (Test-Path $ptlDir) {
    foreach ($file in Get-ChildItem $ptlDir -File -Filter *.json | Sort-Object Name) {
        $raw = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8)
        $json = $raw | ConvertFrom-Json
        if ($null -eq $json.pair -or $json.pair.Count -ne 2) {
            throw "PTL file missing pair: $($file.Name)"
        }
        $name = "$($json.pair[0])-$($json.pair[1])"
        if ($raw.Contains(')PTLJSON"')) {
            throw "PTL file contains raw string terminator: $($file.Name)"
        }
        $pairs += [pscustomobject]@{ Name = $name; Rules = $raw }
    }
}

$sb = [Text.StringBuilder]::new()
[void]$sb.AppendLine('// auto-generated from PTL/*.json')
[void]$sb.AppendLine('#pragma once')
[void]$sb.AppendLine('#include <cstdint>')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('struct JercpldPtlEmbeddedPair {')
[void]$sb.AppendLine('    char const* name;')
[void]$sb.AppendLine('    char const* rules;')
[void]$sb.AppendLine('};')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('static const JercpldPtlEmbeddedPair kJercpldPtlPairs[] = {')
foreach ($p in $pairs) {
    [void]$sb.AppendLine("    { `"$($p.Name)`", R`"PTLJSON($($p.Rules))PTLJSON`" },")
}
[void]$sb.AppendLine('};')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('static const uint32_t kJercpldPtlPairCount =')
[void]$sb.AppendLine('    static_cast<uint32_t>(sizeof(kJercpldPtlPairs) / sizeof(kJercpldPtlPairs[0]));')
[void]$sb.AppendLine('')

[IO.File]::WriteAllText($outFile, $sb.ToString(), (New-Object Text.UTF8Encoding($false)))
Write-Output "generated $outFile with $($pairs.Count) pair(s)"