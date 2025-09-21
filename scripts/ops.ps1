#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

function Find-SmokeWrapper {
    [CmdletBinding()]
    param([switch]$Strict)
    $root = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $root 'scripts\run_smoke_60m_wrapper_v2.ps1'),
        (Join-Path $root 'scripts\run_smoke_60m_wrapper.ps1')
    )
    foreach ($p in $candidates) { if (Test-Path $p) { return $p } }
    if ($Strict -or $env:CI_BLOCK_FAIL -eq '1') { throw "Smoke wrapper not found" }
    return $null
}

function Resolve-Defaults {
    param(
        [string]$OutRoot = "D:\botg\runs",
        [string]$LogPath = "D:\botg\logs"
    )
    if (!(Test-Path -LiteralPath $OutRoot)) { New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null }
    if (!(Test-Path -LiteralPath $LogPath)) { New-Item -ItemType Directory -Force -Path $LogPath | Out-Null }
    return @($OutRoot, $LogPath)
}

function Resolve-LatestRun {
    param([string]$OutRoot = "D:\botg\runs")
    $latest = Get-ChildItem -LiteralPath $OutRoot -Directory -Filter "paper_smoke_60m_v2_*" -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) { return $latest.FullName }
    return $null
}

function Invoke-Smoke60mMergeLatest {
    param([string]$OutRoot = "D:\botg\runs", [string]$LogPath = "D:\botg\logs")
    $r = Resolve-Defaults -OutRoot $OutRoot -LogPath $LogPath
    $strict = ($env:CI_BLOCK_FAIL -eq '1')
    $wrapper = Find-SmokeWrapper -Strict:$strict
    if ($null -ne $wrapper) {
        & $wrapper -MergeOnly -OutRoot $r[0] -LogPath $r[1]
        if ($LASTEXITCODE -ne 0) { throw "Wrapper exit $LASTEXITCODE" }
    } else {
        Write-Host "[selftest] wrapper missing  soft pass"
    }
}

Write-Host "ops.ps1 loaded. Available functions:"
" - Invoke-Smoke60mMergeLatest [-OutRoot ...] [-LogPath ...]"
