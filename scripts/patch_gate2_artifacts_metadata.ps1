# ==================================================
# GATE2 ARTIFACTS + METADATA RECTIFY PATCH
# ==================================================
param([switch]$DryRun)
$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Item $PSScriptRoot).Parent.FullName

function Apply-Patch {
    param([string]$File,[string]$OldText,[string]$NewText,[string]$Description)
    $path = Join-Path $repoRoot $File
    Write-Host "Patching: $Description" -ForegroundColor Cyan
    if (-not (Test-Path $path)) { throw "File not found: $path" }
    $content = Get-Content $path -Raw
    if ($content -notmatch [regex]::Escape($OldText)) {
        Write-Warning "OLD text not found - already patched?"
        return $false
    }
    if ($DryRun) {
        Write-Host "  [DRY RUN] Would replace" -ForegroundColor Yellow
        return $true
    }
    $content = $content.Replace($OldText, $NewText)
    Set-Content -Path $path -Value $content -NoNewline
    Write-Host "   Applied" -ForegroundColor Green
    return $true
}

# PATCH 1
$p1 = Apply-Patch -File "BotG\Telemetry\TelemetryContext.cs" -Description "Fix 1: risk_snapshots.csv to runDir" -OldText @"
        // write runtime files inside runDir, but keep RiskSnapshot in base folder for continuity
        OrderLogger = new OrderLifecycleLogger(runDir, "orders.csv");
        ClosedTrades = new ClosedTradesWriter(runDir);
        RiskPersister = new RiskSnapshotPersister(Config.LogPath, Config.RiskSnapshotFile);
        Collector = new TelemetryCollector(runDir, Config.TelemetryFile, Config.FlushIntervalSeconds);
"@ -NewText @"
        // write all runtime files inside runDir (per-run artifact folder)
        OrderLogger = new OrderLifecycleLogger(runDir, "orders.csv");
        ClosedTrades = new ClosedTradesWriter(runDir);
        RiskPersister = new RiskSnapshotPersister(runDir, Config.RiskSnapshotFile);
        Collector = new TelemetryCollector(runDir, Config.TelemetryFile, Config.FlushIntervalSeconds);
"@

# PATCH 2
$p2 = Apply-Patch -File "BotG\Telemetry\RunInitializer.cs" -Description "Fix 2: Flatten metadata schema" -OldText @"
                    var meta = new
                    {
                        run_id = Path.GetFileName(runDir),
                        start_time_iso = DateTime.UtcNow.ToString("o"),
                        host = Environment.MachineName,
                        git_commit = commit,
                        config_snapshot = new
                        {
                            simulation = new
                            {
                                enabled = cfg.UseSimulation,
                                fill_probability = cfg.Simulation?.FillProbability,
                                simulate_partial_fills = cfg.Simulation?.SimulatePartialFills
                            },
                            execution = new { fee_per_trade = cfg.Execution?.FeePerTrade, fee_percent = cfg.Execution?.FeePercent, spread_pips = cfg.Execution?.SpreadPips },
                            log_path = cfg.LogPath,
                            hours = cfg.Hours,
                            seconds_per_hour = cfg.SecondsPerHour,
                            drain_seconds = cfg.DrainSeconds,
                            graceful_shutdown_wait_seconds = cfg.GracefulShutdownWaitSeconds
                        },
                        log_paths = new { run = runDir, base_log = cfg.LogPath },
                        extra
                    };
"@ -NewText @"
                    var meta = new
                    {
                        run_id = Path.GetFileName(runDir),
                        start_time_iso = DateTime.UtcNow.ToString("o"),
                        host = Environment.MachineName,
                        git_commit = commit,
                        mode = cfg.Mode,
                        hours = cfg.Hours,
                        seconds_per_hour = cfg.SecondsPerHour,
                        simulation = new
                        {
                            enabled = cfg.UseSimulation,
                            fill_probability = cfg.Simulation?.FillProbability,
                            simulate_partial_fills = cfg.Simulation?.SimulatePartialFills
                        },
                        config_snapshot = new
                        {
                            simulation = new
                            {
                                enabled = cfg.UseSimulation,
                                fill_probability = cfg.Simulation?.FillProbability,
                                simulate_partial_fills = cfg.Simulation?.SimulatePartialFills
                            },
                            execution = new { fee_per_trade = cfg.Execution?.FeePerTrade, fee_percent = cfg.Execution?.FeePercent, spread_pips = cfg.Execution?.SpreadPips },
                            log_path = cfg.LogPath,
                            hours = cfg.Hours,
                            seconds_per_hour = cfg.SecondsPerHour,
                            drain_seconds = cfg.DrainSeconds,
                            graceful_shutdown_wait_seconds = cfg.GracefulShutdownWaitSeconds
                        },
                        log_paths = new { run = runDir, base_log = cfg.LogPath },
                        extra
                    };
"@

Write-Host "==================================================="
Write-Host "Patch 1: $(if($p1){''}else{''}) | Patch 2: $(if($p2){''}else{''})"
Write-Host "==================================================="
if ($p1 -or $p2) { exit 0 } else { exit 1 }
