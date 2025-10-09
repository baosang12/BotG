# ==================================================
# GATE2 ARTIFACTS + METADATA RECTIFY PATCH
# ==================================================
# Fix 1: Bundle risk_snapshots.csv into artifact folder
# Fix 2: Flatten run_metadata.json schema
# ==================================================

param([switch]$DryRun)

$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Item $PSScriptRoot).Parent.FullName

function Apply-Patch {
    param(
        [string]$File,
        [string]$OldText,
        [string]$NewText,
        [string]$Description
    )
    $path = Join-Path $repoRoot $File
    Write-Host "Patching: $Description" -ForegroundColor Cyan
    if (-not (Test-Path $path)) { throw "File not found: $path" }
    $content = Get-Content $path -Raw
    if ($content -notmatch [regex]::Escape($OldText)) {
        Write-Warning "OLD text not found in $File - already patched or mismatch"
        return $false
    }
    if ($DryRun) {
        Write-Host "  [DRY RUN] Would replace in: $File" -ForegroundColor Yellow
        return $true
    }
    $content = $content.Replace($OldText, $NewText)
    Set-Content -Path $path -Value $content -NoNewline
    Write-Host "  ✓ Applied" -ForegroundColor Green
    return $true
}

# ===== PATCH 1: RiskSnapshotPersister to write in runDir =====
$oldText1 = @"
        // write runtime files inside runDir, but keep RiskSnapshot in base folder for continuity
        OrderLogger = new OrderLifecycleLogger(runDir, "orders.csv");
        ClosedTrades = new ClosedTradesWriter(runDir);
        RiskPersister = new RiskSnapshotPersister(Config.LogPath, Config.RiskSnapshotFile);
        Collector = new TelemetryCollector(runDir, Config.TelemetryFile, Config.FlushIntervalSeconds);
"@

$newText1 = @"
        // write all runtime files inside runDir (per-run artifact folder)
        OrderLogger = new OrderLifecycleLogger(runDir, "orders.csv");
        ClosedTrades = new ClosedTradesWriter(runDir);
        RiskPersister = new RiskSnapshotPersister(runDir, Config.RiskSnapshotFile);
        Collector = new TelemetryCollector(runDir, Config.TelemetryFile, Config.FlushIntervalSeconds);
"@

$patch1 = Apply-Patch `
    -File "BotG\Telemetry\TelemetryContext.cs" `
    -Description "Fix 1: Write risk_snapshots.csv into runDir (artifact folder)" `
    -OldText $oldText1 `
    -NewText $newText1

# ===== PATCH 2: Flatten run_metadata.json schema =====
$oldText2 = @"
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
"@

$newText2 = @"
                    var meta = new
                    {
                        run_id = Path.GetFileName(runDir),
                        start_time_iso = DateTime.UtcNow.ToString("o"),
                        host = Environment.MachineName,
                        git_commit = commit,
                        // Top-level DoD fields
                        mode = cfg.Mode,
                        hours = cfg.Hours,
                        seconds_per_hour = cfg.SecondsPerHour,
                        simulation = new
                        {
                            enabled = cfg.UseSimulation,
                            fill_probability = cfg.Simulation?.FillProbability,
                            simulate_partial_fills = cfg.Simulation?.SimulatePartialFills
                        },
                        // Nested config snapshot for backward compatibility
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

$patch2 = Apply-Patch `
    -File "BotG\Telemetry\RunInitializer.cs" `
    -Description "Fix 2: Flatten run_metadata.json with top-level fields" `
    -OldText $oldText2 `
    -NewText $newText2

Write-Host ""
Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "PATCH SUMMARY" -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "Patch 1 (RiskSnapshotPersister): $(if($patch1){'✓ Applied'}else{'✗ Skipped'})" -ForegroundColor $(if($patch1){'Green'}else{'Yellow'})
Write-Host "Patch 2 (Metadata flattening):   $(if($patch2){'✓ Applied'}else{'✗ Skipped'})" -ForegroundColor $(if($patch2){'Green'}else{'Yellow'})
Write-Host "===================================================" -ForegroundColor Cyan

if ($DryRun) {
    Write-Host ""
    Write-Host "[DRY RUN] No files were modified. Run without -DryRun to apply." -ForegroundColor Yellow
    exit 0
}

if ($patch1 -or $patch2) {
    Write-Host ""
    Write-Host "✓ Patches applied successfully!" -ForegroundColor Green
    exit 0
} else {
    Write-Warning "No patches were applied. Check if code has changed."
    exit 1
}
