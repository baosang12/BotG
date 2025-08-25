param(
    [Parameter(Mandatory = $true)] [string] $OrdersCsv,
    [Parameter(Mandatory = $true)] [string] $OutDir
)

# Helper: quantile with linear interpolation
function Get-Quantile {
    param(
        [Parameter(Mandatory = $true)] $Values,
        [Parameter(Mandatory = $true)] [double] $Q
    )
    $arr = @($Values | Sort-Object)
    $n = $arr.Count
    if ($n -eq 0) { return $null }
    if ($n -eq 1) { return $arr[0] }
    if ($Q -le 0) { return $arr[0] }
    if ($Q -ge 1) { return $arr[$n-1] }
    $pos = ($n - 1) * $Q
    $lower = [int][Math]::Floor($pos)
    $upper = [int][Math]::Ceiling($pos)
    if ($lower -eq $upper) { return $arr[$lower] }
    $weight = $pos - $lower
    return $arr[$lower] + ($arr[$upper] - $arr[$lower]) * $weight
}

# Ensure output directory exists
if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir | Out-Null
}

# Import orders
$rows = Import-Csv -LiteralPath $OrdersCsv

$invar = [System.Globalization.CultureInfo]::InvariantCulture

# 1) Fill rate by side
$sides = @('BUY', 'SELL')
$bySide = foreach ($side in $sides) {
    $req = ($rows | Where-Object { $_.phase -eq 'REQUEST' -and $_.side -eq $side }).Count
    $ack = ($rows | Where-Object { $_.phase -eq 'ACK' -and $_.side -eq $side }).Count
    $fil = ($rows | Where-Object { $_.phase -eq 'FILL' -and $_.side -eq $side }).Count

    # Slippage stats for FILLs of this side
    $fillRows = $rows | Where-Object { $_.phase -eq 'FILL' -and $_.side -eq $side -and $_.slippage -ne $null -and $_.slippage -ne '' }
    $slips = @()
    foreach ($r in $fillRows) {
        try {
            $slips += [double]::Parse($r.slippage, $invar)
        } catch {}
    }
    $absSlips = @($slips | ForEach-Object { [math]::Abs($_) })
    $avgSlip = if ($slips.Count -gt 0) { ([math]::Round((($slips | Measure-Object -Average).Average), 10)) } else { $null }
    $p50Slip = if ($slips.Count -gt 0) { [math]::Round((Get-Quantile -Values $slips -Q 0.5), 10) } else { $null }
    $p90Slip = if ($slips.Count -gt 0) { [math]::Round((Get-Quantile -Values $slips -Q 0.9), 10) } else { $null }
    $avgAbsSlip = if ($absSlips.Count -gt 0) { ([math]::Round((($absSlips | Measure-Object -Average).Average), 10)) } else { $null }
    $p50AbsSlip = if ($absSlips.Count -gt 0) { [math]::Round((Get-Quantile -Values $absSlips -Q 0.5), 10) } else { $null }
    $p90AbsSlip = if ($absSlips.Count -gt 0) { [math]::Round((Get-Quantile -Values $absSlips -Q 0.9), 10) } else { $null }

    [pscustomobject]@{
        side               = $side
        requests           = $req
        acks               = $ack
        fills              = $fil
        fill_rate_percent  = if ($req -gt 0) { [math]::Round(100.0 * $fil / $req, 2) } else { 0 }
        avg_slip           = $avgSlip
        p50_slip           = $p50Slip
        p90_slip           = $p90Slip
        avg_abs_slip       = $avgAbsSlip
        p50_abs_slip       = $p50AbsSlip
        p90_abs_slip       = $p90AbsSlip
    }
}

$bySidePath = Join-Path $OutDir 'fill_rate_by_side.csv'
$bySide | Export-Csv -Path $bySidePath -NoTypeInformation

# 2) Fill breakdown by UTC hour
foreach ($r in $rows) {
    try {
        $dto = [DateTimeOffset]::Parse($r.timestamp_iso)
        $utc = $dto.ToUniversalTime()
        $r | Add-Member -NotePropertyName hour_utc -NotePropertyValue ($utc.ToString("yyyy-MM-dd HH:00:00'Z'")) -Force
    } catch {
        $r | Add-Member -NotePropertyName hour_utc -NotePropertyValue '' -Force
    }
}

$byHour = $rows |
    Where-Object { $_.hour_utc -ne '' } |
    Group-Object -Property hour_utc |
    ForEach-Object {
        $g = $_.Group
        $requests = ($g | Where-Object { $_.phase -eq 'REQUEST' }).Count
        $acks     = ($g | Where-Object { $_.phase -eq 'ACK' }).Count
        $fills    = ($g | Where-Object { $_.phase -eq 'FILL' }).Count

        # Slippage stats for fills in this hour
        $fillRows = $g | Where-Object { $_.phase -eq 'FILL' -and $_.slippage -ne $null -and $_.slippage -ne '' }
        $slips = @()
        foreach ($r in $fillRows) {
            try {
                $slips += [double]::Parse($r.slippage, $invar)
            } catch {}
        }
        $absSlips = @($slips | ForEach-Object { [math]::Abs($_) })
        $avgSlip = if ($slips.Count -gt 0) { ([math]::Round((($slips | Measure-Object -Average).Average), 10)) } else { $null }
        $p50Slip = if ($slips.Count -gt 0) { [math]::Round((Get-Quantile -Values $slips -Q 0.5), 10) } else { $null }
        $p90Slip = if ($slips.Count -gt 0) { [math]::Round((Get-Quantile -Values $slips -Q 0.9), 10) } else { $null }
        $avgAbsSlip = if ($absSlips.Count -gt 0) { ([math]::Round((($absSlips | Measure-Object -Average).Average), 10)) } else { $null }
        $p50AbsSlip = if ($absSlips.Count -gt 0) { [math]::Round((Get-Quantile -Values $absSlips -Q 0.5), 10) } else { $null }
        $p90AbsSlip = if ($absSlips.Count -gt 0) { [math]::Round((Get-Quantile -Values $absSlips -Q 0.9), 10) } else { $null }

        [pscustomobject]@{
            hour_utc           = $_.Name
            requests           = $requests
            acks               = $acks
            fills              = $fills
            fill_rate_percent  = if ($requests -gt 0) { [math]::Round(100.0 * $fills / $requests, 2) } else { 0 }
            avg_slip           = $avgSlip
            p50_slip           = $p50Slip
            p90_slip           = $p90Slip
            avg_abs_slip       = $avgAbsSlip
            p50_abs_slip       = $p50AbsSlip
            p90_abs_slip       = $p90AbsSlip
        }
    } |
    Sort-Object -Property hour_utc

$byHourPath = Join-Path $OutDir 'fill_breakdown_by_hour.csv'
$byHour | Export-Csv -Path $byHourPath -NoTypeInformation

# Console output
Write-Host "Fill rate by side:" -ForegroundColor Cyan
$bySide | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

Write-Host "Fill breakdown by UTC hour:" -ForegroundColor Cyan
$byHour | Format-Table -AutoSize | Out-String -Width 220 | Write-Host

# Sample broker messages (first 10 distinct)
$brokerMsgs = $rows |
    Where-Object { $_.phase -eq 'FILL' -and $_.brokerMsg -and $_.brokerMsg.Trim().Length -gt 0 } |
    Select-Object -ExpandProperty brokerMsg -Unique |
    Select-Object -First 10

if ($brokerMsgs -and $brokerMsgs.Count -gt 0) {
    Write-Host "Sample broker messages (first 10 distinct):" -ForegroundColor Cyan
    $i = 1
    foreach ($msg in $brokerMsgs) {
        Write-Host ("{0}. {1}" -f $i, $msg)
        $i += 1
    }
} else {
    Write-Host "No non-empty broker messages found."
}

Write-Host "" 
Write-Host ("CSV exported:" ) -ForegroundColor Green
Write-Host (" - " + $bySidePath)
Write-Host (" - " + $byHourPath)
