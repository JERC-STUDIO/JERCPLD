# One-command regression for JERCPLD: simulated 26.62 client -> 26.51 server.
# Requires: node + tools/verify-client/node_modules (bedrock-protocol 3.61.0).
# NOTE: the simulator closes the NetherNet transport without a graceful disconnect,
# which can leave a phantom session on the server (players=1 in /v1/join) and block
# the next simulated login. Restart the target before a repeat run, or pass
# -SkipPlayerCheck only when you know the reported player is stale.
param(
    [string]$ServerHost = '127.0.0.1',
    [int]$Port = 19132,
    [int]$UdpPort = 7551,
    [string]$Version = '1.26.60',
    [string]$SpoofVersion = '1.26.62',
    [string]$Username = 'JERCPLDRegress',
    [int]$RequireChunks = 20,
    [int]$DwellMs = 8000,
    [switch]$SkipPlayerCheck
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $SkipPlayerCheck) {
    try {
        $join = Invoke-RestMethod -Uri "http://${ServerHost}:${Port}/v1/join" -TimeoutSec 5
        if ($join.players -gt 0) {
            Write-Output "SKIP: /v1/join reports players=$($join.players). Restart the target or wait for sessions to clear; use -SkipPlayerCheck only for a known stale session."
            exit 9
        }
    } catch {
        Write-Output "WARN: /v1/join unavailable: $($_.Exception.Message)"
    }
}

$env:NETHERNET_DISCOVERY_PORT = [string]$UdpPort
Push-Location $here
try {
    node verify.js --host $ServerHost --port $Port --version $Version --spoof-game-version $SpoofVersion `
        --transport nethernet --username $Username --require-chunks $RequireChunks `
        --move --animate --dwell-ms $DwellMs --timeout 90000
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
