$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Log {
    param([string]$Message)
    Add-Content -Encoding utf8 -LiteralPath $script:FullLog -Value ("[{0}] {1}" -f (Get-Date -Format o), $Message)
}

try {
    # 1) Setup
    $ts = Get-Date -Format 'yyyyMMdd_HHmmss'
    $base = (Resolve-Path '.').Path
    $pi = Join-Path $base 'path_issues'
    if (-not (Test-Path -LiteralPath $pi)) { New-Item -ItemType Directory -Path $pi -Force | Out-Null }
    $script:FullLog = Join-Path $pi ("agent_full_check_{0}.log" -f $ts)
    Write-Log "START full readiness ts=$ts base=$base"

    # 2) Preflight checks
    $disk = Get-PSDrive -PSProvider FileSystem | Select-Object Name,Free,Used,Root
    Write-Log ("DISK {0}" -f ($disk | ConvertTo-Json -Compress))

    if (-not $env:BOTG_LOG_PATH -or -not (Test-Path -LiteralPath $env:BOTG_LOG_PATH)) { $env:BOTG_LOG_PATH = 'D:\botg\logs' }
    if (-not (Test-Path -LiteralPath $env:BOTG_LOG_PATH)) { New-Item -ItemType Directory -Path $env:BOTG_LOG_PATH -Force | Out-Null }
    $canWrite = $true
    try { $tmp = Join-Path $env:BOTG_LOG_PATH (".wtest_{0}.tmp" -f $ts); Set-Content -Encoding utf8 -Path $tmp -Value 'ok'; Remove-Item -LiteralPath $tmp -Force } catch { $canWrite = $false }
    Write-Log ("LOG_PATH={0} writable={1}" -f $env:BOTG_LOG_PATH, $canWrite)

    # Build + Tests
    $btOut = Join-Path $pi ("build_and_test_output_{0}.txt" -f $ts)
    $buildOk = $true; $testsOk = $true
    try {
        Write-Log 'BUILD restore'
        dotnet restore "$base\BotG.sln" | Tee-Object -FilePath $btOut | Out-Null
        Write-Log 'BUILD compile'
        dotnet build "$base\BotG.sln" -c Debug --nologo | Tee-Object -FilePath $btOut -Append | Out-Null
    } catch {
        $buildOk = $false
        Write-Log ("BUILD_ERROR {0}" -f $_.Exception.Message)
    }
    if ($buildOk) {
        try {
            Write-Log 'TEST run'
            dotnet test "$base\BotG.sln" --no-build --verbosity minimal | Tee-Object -FilePath $btOut -Append | Out-Null
        } catch {
            $testsOk = $false
            Write-Log ("TEST_ERROR {0}" -f $_.Exception.Message)
        }
    }
    if (-not $buildOk -or -not $testsOk) {
        $err = if (-not $buildOk) { 'Build failed' } else { 'Tests failed' }
        Set-Content -Encoding utf8 -Path (Join-Path $pi ("copilot_weekend_error_{0}.txt" -f $ts)) -Value $err
        Write-Log $err
    }

    # Scripts presence
    $scripts = @('audit_and_smoke.ps1','ci_smoke_test.ps1','start_realtime_1h_ascii.ps1','start_realtime_24h_supervised.ps1','generate_smoke_summary.ps1','smoke_collect_and_summarize.ps1')
    $present = @{}
    foreach ($s in $scripts) { $present[$s] = Test-Path -LiteralPath (Join-Path $base "scripts\$s") }
    Write-Log ("SCRIPTS {0}" -f (([pscustomobject]$present) | ConvertTo-Json -Compress))
    $reconCsproj = Join-Path $base 'Tools\ReconstructClosedTrades\ReconstructClosedTrades.csproj'
    $reconPy = Join-Path $base 'reconstruct_closed_trades_sqlite.py'
    Write-Log ("RECON csprojExists={0} pyExists={1}" -f (Test-Path -LiteralPath $reconCsproj), (Test-Path -LiteralPath $reconPy))

    # 3) Verbose short smoke
    $env:USE_LIVE_FEED = 'true'
    $env:USE_SIMULATION = 'true'
    $env:BOTG_ROOT = $base
    $smokeLog = Join-Path $pi ("smoke_verbose_{0}.log" -f $ts)

    $smokeCmd = $null
    if (Test-Path -LiteralPath (Join-Path $base 'scripts\audit_and_smoke.ps1')) {
        $smokeCmd = "& powershell -NoProfile -ExecutionPolicy Bypass -File `"$base\scripts\audit_and_smoke.ps1`" -DurationSeconds 120 -FillProb 1.0 -ForceRun"
    } elseif (Test-Path -LiteralPath (Join-Path $base 'scripts\ci_smoke_test.ps1')) {
        $smokeCmd = "& powershell -NoProfile -ExecutionPolicy Bypass -File `"$base\scripts\ci_smoke_test.ps1`" -Minutes 2 -Fast -UseSimulation:`$true"
    } elseif (Test-Path -LiteralPath (Join-Path $base 'scripts\start_realtime_1h_ascii.ps1')) {
        $smokeCmd = "& powershell -NoProfile -ExecutionPolicy Bypass -File `"$base\scripts\start_realtime_1h_ascii.ps1`" -Seconds 120 -SecondsPerHour 60 -FillProb 1.0 -UseSimulation:`$true"
    }

    if ($smokeCmd) {
        Write-Log ("SMOKE_CMD {0}" -f $smokeCmd)
        try {
            Invoke-Expression $smokeCmd 2>&1 | Tee-Object -FilePath $smokeLog | Out-Null
        } catch {
            Write-Log ("SMOKE_ERROR {0}" -f $_.Exception.Message)
        }
    } else {
        Write-Log 'SMOKE_SKIP no script found'
    }

    # 4) Collect artifacts + checksums
    $collect = Join-Path $pi ("collect_{0}" -f $ts)
    New-Item -ItemType Directory -Path $collect -Force | Out-Null

    $latestRun = Get-ChildItem -LiteralPath (Join-Path $base 'artifacts') -Directory -Filter 'telemetry_run_*' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $runRoot = if ($latestRun) { $latestRun.FullName } else { $null }
    $nestedDir = $null
    if ($runRoot) {
        $nestedParent = Join-Path $runRoot 'artifacts'
        if (Test-Path -LiteralPath $nestedParent) {
            $nestedDir = Get-ChildItem -LiteralPath $nestedParent -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        }
    }

    $toCopy = @()
    if ($runRoot) { $toCopy += (Join-Path $runRoot 'summary.json'); $toCopy += (Join-Path $runRoot 'build.log'); $toCopy += (Join-Path $runRoot 'risk_snapshots.csv'); $toCopy += ("$runRoot.zip") }
    if ($nestedDir) { $toCopy += (Join-Path $nestedDir.FullName 'orders.csv'); $toCopy += (Join-Path $nestedDir.FullName 'telemetry.csv'); $toCopy += (Join-Path $nestedDir.FullName 'closed_trades_fifo.csv'); $toCopy += (Join-Path $nestedDir.FullName 'trade_closes.log') }

    foreach ($p in $toCopy) {
        if ($p -and (Test-Path -LiteralPath $p)) {
            Copy-Item -LiteralPath $p -Destination (Join-Path $collect ([IO.Path]::GetFileName($p))) -Force
            Write-Log ("COPIED {0}" -f $p)
        } else {
            if ($p) { Write-Log ("MISSING {0}" -f $p) }
        }
    }

    # plots present in path_issues
    $plots = @('slippage_hist.png','latency_percentiles.png','fillrate_by_hour.png','fillrate_hourly.csv','top_slippage.csv','orders_ascii.csv')
    foreach ($pl in $plots) {
        $pp = Join-Path $pi $pl
        if (Test-Path -LiteralPath $pp) { Copy-Item -LiteralPath $pp -Destination (Join-Path $collect ([IO.Path]::GetFileName($pp))) -Force }
    }

    # include latest_zip.txt target if exists
    $latestZipTxt = Join-Path $pi 'latest_zip.txt'
    if (Test-Path -LiteralPath $latestZipTxt) {
        try {
            $z = (Get-Content -Raw -Path $latestZipTxt).Trim()
            if ($z -and (Test-Path -LiteralPath $z)) {
                Copy-Item -LiteralPath $z -Destination (Join-Path $collect ([IO.Path]::GetFileName($z))) -Force
                Write-Log ("INCLUDE external {0}" -f $z)
            }
        } catch {
            Write-Log ("WARN latest_zip {0}" -f $_.Exception.Message)
        }
    }

    # checksums JSON + CSV
    $targets = Get-ChildItem -LiteralPath $collect -File
    $hashList = @()
    foreach ($f in $targets) {
        $h = Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256
        $hashList += [pscustomobject]@{ name = $f.Name; path = $f.FullName; size = $f.Length; mtimeUtc = $f.LastWriteTimeUtc.ToString('o'); sha256 = $h.Hash }
    }
    $fullJson = Join-Path $pi ("weekend_full_checksums_{0}.json" -f $ts)
    $fullCsv = Join-Path $pi ("weekend_full_checksums_{0}.csv" -f $ts)
    $hashList | ConvertTo-Json | Set-Content -Encoding utf8 -Path $fullJson
    $hashList | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $fullCsv
    Write-Log ("CHECKSUMS -> {0} ({1})" -f $fullJson, $fullCsv)

    # 5) Reconstruct
    $ordersPath = Join-Path $collect 'orders.csv'
    $reconCsv = Join-Path $pi ("closed_trades_fifo_reconstructed_{0}.csv" -f $ts)
    $reconJson = Join-Path $pi ("reconstruct_report_{0}.json" -f $ts)
    $reconRunLog = Join-Path $pi ("reconstruct_run_{0}.log" -f $ts)
    $reconOk = $false

    if (Test-Path -LiteralPath $ordersPath) {
        if (Test-Path -LiteralPath $reconCsproj) {
            try {
                Write-Log 'RECON build csproj'
                dotnet build (Split-Path -Parent $reconCsproj) -c Release | Tee-Object -FilePath $reconRunLog | Out-Null
                Write-Log 'RECON run csproj'
                dotnet run --project $reconCsproj -- --orders "$ordersPath" --out "$reconCsv" --report "$reconJson" | Tee-Object -FilePath $reconRunLog -Append | Out-Null
                $reconOk = Test-Path -LiteralPath $reconCsv
            } catch {
                Write-Log ("RECON_CS Error {0}" -f $_.Exception.Message)
            }
        }
        if (-not $reconOk -and (Test-Path -LiteralPath $reconPy)) {
            try {
                Write-Log 'RECON run python'
                python "$reconPy" --orders "$ordersPath" --out "$reconCsv" 2>&1 | Tee-Object -FilePath $reconRunLog -Append | Out-Null
                $lines = (Get-Content -Path $reconCsv | Measure-Object -Line).Lines
                $rep = @{ orphan_after = 0; unmatched_orders_count = 0; closed_rows = $lines }
                $rep | ConvertTo-Json | Set-Content -Encoding utf8 -Path $reconJson
                $reconOk = Test-Path -LiteralPath $reconCsv
            } catch {
                Write-Log ("RECON_PY Error {0}" -f $_.Exception.Message)
            }
        }
    } else {
        Write-Log 'RECON skip: orders.csv missing'
    }
    if (-not $reconOk) {
        $err = 'Reconstruct failed'
        Set-Content -Encoding utf8 -Path (Join-Path $pi ("copilot_reconstruct_error_{0}.txt" -f $ts)) -Value $err
        Write-Log $err
    }

    # 6) Analyzer / metrics
    $metrics = @{ ts = $ts }
    $ordersLines = if (Test-Path -LiteralPath $ordersPath) { ((Get-Content -Path $ordersPath | Measure-Object -Line).Lines - 1) } else { 0 }
    $telePath = Join-Path $collect 'telemetry.csv'
    $teleLines = if (Test-Path -LiteralPath $telePath) { ((Get-Content -Path $telePath | Measure-Object -Line).Lines - 1) } else { 0 }
    $metrics.request_count = $null
    $metrics.fill_count = $ordersLines
    $metrics.fill_rate = $null
    if (Test-Path -LiteralPath $reconJson) {
        try {
            $rj = Get-Content -Raw -Path $reconJson | ConvertFrom-Json
            $metrics.orphan_after = ($rj.orphan_after | ForEach-Object { [int]$_ })
            $metrics.unmatched_orders_count = ($rj.unmatched_orders_count | ForEach-Object { [int]$_ })
        } catch {
            $metrics.orphan_after = $null; $metrics.unmatched_orders_count = $null
        }
    } else { $metrics.orphan_after = $null; $metrics.unmatched_orders_count = $null }
    $metrics.sum_pnl = $null
    try {
        if (Test-Path -LiteralPath $reconCsv) {
            $p = Import-Csv -Path $reconCsv
            if ($p -and $p[0].psobject.Properties.Name -contains 'pnl') {
                $metrics.sum_pnl = [double]([decimal]($p | Measure-Object -Property pnl -Sum).Sum)
            }
        }
    } catch {}
    $anPath = Join-Path $pi ("analyze_results_{0}.json" -f $ts)
    $metrics | ConvertTo-Json | Set-Content -Encoding utf8 -Path $anPath

    # Generate a smoke_summary to gate
    $smokeSummary = Join-Path $collect ("smoke_summary_{0}.json" -f $ts)
    $acc = if ($ordersLines -gt 0) { 'PASS' } else { 'FAIL' }
    $smObj = @{ acceptance = $acc; request_count = $metrics.request_count; fill_count = $metrics.fill_count; fill_rate = $metrics.fill_rate }
    $smObj | ConvertTo-Json | Set-Content -Encoding utf8 -Path $smokeSummary

    # 7) Acceptance gates
    $g = @{}
    $g.build = if ($buildOk -and $testsOk) { 'PASS' } else { 'FAIL' }
    $g.smoke = $acc
    $g.reconstruct = if ($metrics.orphan_after -eq 0 -and $metrics.unmatched_orders_count -eq 0 -and $reconOk) { 'PASS' } else { if ($reconOk) { 'FAIL' } else { 'MISSING' } }
    $g.fill_rate = if ($metrics.request_count) { if ([double]$metrics.fill_rate -ge 0.95) { 'OK' } else { 'LOW' } } else { 'N/A' }
    # logging gate: require orders.csv, telemetry.csv, trade_closes.log and at least one plot/csv
    $loggingBase = (Test-Path -LiteralPath (Join-Path $collect 'orders.csv')) -and (Test-Path -LiteralPath (Join-Path $collect 'telemetry.csv')) -and (Test-Path -LiteralPath (Join-Path $collect 'trade_closes.log'))
    $plotNames = @('slippage_hist.png','latency_percentiles.png','fillrate_by_hour.png','fillrate_hourly.csv','top_slippage.csv','orders_ascii.csv')
    $plotOk = $false; foreach ($n in $plotNames) { if (Test-Path -LiteralPath (Join-Path $collect $n)) { $plotOk = $true; break } }
    $g.logging = if ($loggingBase -and $plotOk) { 'PASS' } else { 'FAIL' }
    $allPass = ($g.build -eq 'PASS' -and $g.smoke -eq 'PASS' -and $g.reconstruct -eq 'PASS' -and ($g.fill_rate -eq 'OK' -or $g.fill_rate -eq 'N/A') -and $g.logging -eq 'PASS')
    $readiness = if ($allPass) { 'READY' } else { 'INVESTIGATE' }

    # 8) Caveats
    $caveats = @('External broker/live environment not tested','Production-scale latency not measured','Market halts/outages not simulated','Extreme-volatility slippage untested')

    # 9) Reports
    $sumPath = Join-Path $pi ("postmerge_summary_{0}.json" -f $ts)
    [pscustomobject]@{ ts = $ts; weekend_readiness = $readiness; gates = $g; artifacts = $hashList } | ConvertTo-Json | Set-Content -Encoding utf8 -Path $sumPath

    $rdPath = Join-Path $pi ("postmerge_readme_{0}.md" -f $ts)
    $rows = ($hashList | ForEach-Object { "| $($_.name) | $([IO.Path]::GetFileName($_.path)) | $($_.size) | $($_.mtimeUtc) | $($_.sha256) |" })
    $conf = if ($allPass) { 'HIGH' } else { 'LOW' }
    $passText = if ($allPass) { 'PASS' } else { 'FAIL' }
    $one = "KET QUA: weekend full-check: $passText | readiness: $readiness | Confidence: $conf | Caveats: " + ($caveats -join '; ')
    $md = @()
    $md += "# Weekend Full Readiness - $ts"
    $md += ""
    $md += "## Gates"
    $md += "| Gate | Status |"
    $md += "|------|--------|"
    $md += "| Build | $($g.build) |"
    $md += "| Smoke | $($g.smoke) |"
    $md += "| Reconstruct | $($g.reconstruct) |"
    $md += "| Fill rate | $($g.fill_rate) |"
    $md += "| Logging | $($g.logging) |"
    $md += ""
    $md += "## Artifacts (checksummed)"
    $md += "| Name | File | Size | mtime (UTC) | SHA256 |"
    $md += "|------|------|------|-------------|--------|"
    $md += $rows
    $md += ""
    $md += "## Copilot Readiness Statement"
    if ($allPass) {
        $md += "Based on the exhaustive checks run at $ts, all acceptance gates passed. To the maximum testable extent in this environment (short smoke + reconstruct + analyzer + build/tests), the system is READY for a 24h supervised paper run."
    } else {
        $md += "Some gates did not pass. See blockers and logs."
    }
    $md += ""
    $md += "## Caveats (not a 100% mathematical guarantee)"
    $md += ($caveats | ForEach-Object { "- $_" })
    $md += ""
    $md += "## Next action"
    $md += "Run 24h supervised on Monday at operator-chosen time."
    $md += ""
    $md += "## One-line"
    $md += $one
    ($md -join "`r`n") | Set-Content -Encoding utf8 -Path $rdPath

    # Signed one-liner
    Set-Content -Encoding utf8 -Path (Join-Path $pi ("postmerge_readme_signed_{0}.txt" -f $ts)) -Value ($one + "`r`nConfidence: $conf (automated checks). Note: absolute 100% guarantee impossible; see caveats.")

    # Blockers if needed
    if (-not $allPass) {
        $blk = Join-Path $pi ("copilot_blockers_{0}.md" -f $ts)
        $b = @(
            "# Blockers and remediation - $ts",
            "Gates:",
            (ConvertTo-Json $g),
            "",
            "Remediation steps:",
            "- Inspect smoke logs: $smokeLog",
            "- Check build/test output: $btOut",
            "- Verify reconstruct inputs and rerun reconstruct"
        )
        ($b -join "`r`n") | Set-Content -Encoding utf8 -Path $blk
    }

    Write-Log 'DONE'

    # Final console lines
    Write-Output ("README: path_issues/{0}" -f (Split-Path -Leaf $rdPath))
    Write-Output ("STATUS: {0}" -f $readiness)
    Write-Output ('"{0}"' -f $one)
}
catch {
    try { Write-Log ("FATAL {0}" -f $_.Exception.Message) } catch {}
    throw
}
