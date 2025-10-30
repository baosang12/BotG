# scripts/preflight/ctrader_connect.ps1
# PowerShell 7+, UTF-8 no BOM, CRLF
param(
    [int]$Seconds = 180,
    [string]$LogPath = 'D:\botg\logs',
    [string]$Symbol = 'EURUSD'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Helpers
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
function Write-JsonNoBom($path, $obj) {
    $json = $obj | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($path, $json, $utf8NoBom)
}
function Write-AllTextNoBom($path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, $utf8NoBom)
}

# Paths
$preDir = Join-Path $LogPath 'preflight'
$l1Csv = Join-Path $preDir 'l1_sample.csv'
$connJson = Join-Path $preDir 'connection_ok.json'
$telemetry = Join-Path $LogPath 'telemetry.csv'

# Ensure dirs
New-Item -ItemType Directory -Force -Path $preDir | Out-Null

# Validate telemetry file exists and has header
if (-not (Test-Path $telemetry)) {
    Write-JsonNoBom $connJson @{
        ok = $false
        reason = "telemetry.csv not found"
        symbol = $Symbol
        window_sec = $Seconds
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
    }
    exit 1
}

# Read header to verify schema
$fh = [System.IO.File]::Open($telemetry, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
$sr = New-Object System.IO.StreamReader($fh, $utf8NoBom, $true)
try {
    $header = $sr.ReadLine()
} finally {
    $sr.Dispose(); $fh.Dispose()
}
$expectedHeader = 'timestamp_iso,symbol,bid,ask'
if ($header -ne $expectedHeader) {
    Write-JsonNoBom $connJson @{
        ok = $false
        reason = "telemetry.csv header mismatch"
        got = $header
        expected = $expectedHeader
        symbol = $Symbol
        window_sec = $Seconds
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
    }
    exit 1
}

# Probe loop
$start = Get-Date
$end = $start.AddSeconds($Seconds)
$secondsWithTicks = 0
$totalTicks = 0
$lastStamp = $null
$l1Buffer = New-Object System.Collections.Generic.List[string]

while ((Get-Date) -lt $end) {
    Start-Sleep -Milliseconds 1000

    # Tail last line safely (share-read)
    $content = Get-Content -Path $telemetry -Tail 1 -Encoding UTF8
    if (-not $content) { continue }

    if ($content -match '^\d{4}-\d{2}-\d{2}T') {
        $parts = $content.Split(',')
        if ($parts.Length -ge 4 -and $parts[1] -eq $Symbol) {
            $lastStamp = [datetime]::Parse($parts[0], $null, [System.Globalization.DateTimeStyles]::AssumeUniversal)
            $totalTicks++
            $secondsWithTicks++
            # keep a rolling window of last 50 ticks
            $l1Buffer.Add($content)
            if ($l1Buffer.Count -gt 50) { $l1Buffer.RemoveAt(0) }
        }
    }
}

$windowSec = [math]::Max(1, [int]([datetime]::UtcNow - $start.ToUniversalTime()).TotalSeconds)
$tickRate = [math]::Round(($totalTicks / $windowSec), 3)
$activeRatio = [math]::Round(($secondsWithTicks / $windowSec), 3)
$lastAge = if ($lastStamp) { [math]::Round((([datetime]::UtcNow) - $lastStamp.ToUniversalTime()).TotalSeconds, 3) } else { 1e9 }

# PASS criteria
$ok = ($lastAge -le 5.0) -and ($activeRatio -ge 0.7) -and ($tickRate -ge 0.5)

# Write outputs
Write-AllTextNoBom $l1Csv ("timestamp_iso,symbol,bid,ask`r`n" + ($l1Buffer -join "`r`n"))

Write-JsonNoBom $connJson @{
    ok = $ok
    last_age_now_sec = $lastAge
    active_ratio = $activeRatio
    tick_rate_avg = $tickRate
    window_sec = $windowSec
    symbol = $Symbol
    generated_at = (Get-Date).ToUniversalTime().ToString("o")
}

if ($ok) { exit 0 } else { exit 1 }