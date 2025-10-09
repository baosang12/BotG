$ErrorActionPreference = "Stop"

Write-Host "=== Patching RiskManager for KICKOFF snapshot + BOTG_RISK_FLUSH_SEC ===" -ForegroundColor Cyan

# Read file
$file = "BotG\RiskManager\RiskManager.cs"
$content = Get-Content $file -Raw -Encoding UTF8

# Patch 1: IModule.Initialize - Add kickoff + environment variable support
$old1 = @"
        void IModule.Initialize(BotContext ctx)
        {
            Initialize(new RiskSettings());
            TelemetryContext.InitOnce();
            // Snapshot every FlushIntervalSeconds
            _snapshotTimer?.Change(TimeSpan.FromSeconds(TelemetryContext.Config.FlushIntervalSeconds), TimeSpan.FromSeconds(TelemetryContext.Config.FlushIntervalSeconds));
        }
"@

$new1 = @"
        void IModule.Initialize(BotContext ctx)
        {
            Initialize(new RiskSettings());
            TelemetryContext.InitOnce();
            
            // KICKOFF: Write immediate snapshot at startup
            PersistSnapshotIfAvailable();
            
            // Read risk-specific flush interval from environment or use default 60s
            int riskFlushSec = 60;
            var envRiskFlush = Environment.GetEnvironmentVariable("BOTG_RISK_FLUSH_SEC");
            if (int.TryParse(envRiskFlush, out var sec) && sec > 0)
            {
                riskFlushSec = sec;
            }
            
            // Snapshot every riskFlushSec
            _snapshotTimer?.Change(TimeSpan.FromSeconds(riskFlushSec), TimeSpan.FromSeconds(riskFlushSec));
        }
"@

if ($content.Contains($old1)) {
    $content = $content.Replace($old1, $new1)
    Write-Host "[OK] Patched IModule.Initialize" -ForegroundColor Green
} else {
    Write-Host "[SKIP] IModule.Initialize not found or already patched" -ForegroundColor Yellow
}

# Patch 2: Initialize method - Add environment variable support
$old2 = @"
            // Start risk snapshot timer at 60s period
            _snapshotTimer?.Change(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
"@

$new2 = @"
            // Read risk-specific flush interval from environment or use default 60s
            int riskFlushSec = 60;
            var envRiskFlush = Environment.GetEnvironmentVariable("BOTG_RISK_FLUSH_SEC");
            if (int.TryParse(envRiskFlush, out var sec) && sec > 0)
            {
                riskFlushSec = sec;
            }
            
            // Start risk snapshot timer
            _snapshotTimer?.Change(TimeSpan.FromSeconds(riskFlushSec), TimeSpan.FromSeconds(riskFlushSec));
"@

if ($content.Contains($old2)) {
    $content = $content.Replace($old2, $new2)
    Write-Host "[OK] Patched Initialize method" -ForegroundColor Green
} else {
    Write-Host "[SKIP] Initialize method not found or already patched" -ForegroundColor Yellow
}

# Write back
[IO.File]::WriteAllText($file, $content, [Text.Encoding]::UTF8)

Write-Host "`n=== RiskManager Patch Complete ===" -ForegroundColor Green
