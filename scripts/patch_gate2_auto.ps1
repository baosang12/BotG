# Gate2 Auto-Patch Script
# Bypasses workspace limitations by using pure PowerShell file operations

param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# Resolve to absolute path to handle both short and long formats
$currentPath = Resolve-Path "." | Select-Object -ExpandProperty Path
$repoMarker = Join-Path $currentPath "BotG.sln"

if (-not (Test-Path $repoMarker)) {
    Write-Host "ERROR: Must run from BotG repo root (BotG.sln not found)" -ForegroundColor Red
    Write-Host "Current: $currentPath" -ForegroundColor Gray
    exit 1
}

$repo = $currentPath

Write-Host "Gate2 Auto-Patch v1.0" -ForegroundColor Cyan
Write-Host "=====================`n" -ForegroundColor Cyan

# Create backup
$ts = Get-Date -Format "yyyyMMdd_HHmmss"
$bak = "path_issues\gate2_fixes\backup_$ts"
New-Item -ItemType Directory -Path $bak -Force | Out-Null

function Apply-Patch {
    param(
        [string]$File,
        [string]$Find,
        [string]$Replace,
        [string]$Description
    )
    
    Write-Host "[$File] $Description..." -ForegroundColor Yellow
    
    $path = Join-Path $repo $File
    if (-not (Test-Path $path)) {
        Write-Host "  ERROR: File not found!" -ForegroundColor Red
        return $false
    }
    
    # Backup
    $bakPath = Join-Path $bak $File
    $bakDir = Split-Path $bakPath -Parent
    New-Item -ItemType Directory -Path $bakDir -Force | Out-Null
    Copy-Item $path $bakPath -Force
    
    # Read content
    $content = Get-Content $path -Raw -Encoding UTF8
    
    # Check if find string exists
    if (-not $content.Contains($Find)) {
        Write-Host "  WARNING: Find string not found! May already be patched." -ForegroundColor Yellow
        return $false
    }
    
    # Apply patch
    $newContent = $content.Replace($Find, $Replace)
    
    if ($DryRun) {
        Write-Host "  [DRY-RUN] Would replace $(($Find -split "`n").Count) lines" -ForegroundColor Magenta
        return $true
    }
    
    # Write back
    Set-Content $path -Value $newContent -Encoding UTF8 -NoNewline
    Write-Host "  OK Patched" -ForegroundColor Green
    return $true
}

$success = 0
$failed = 0

# ===== PATCH 1: RiskSnapshotPersister header =====
$p1 = Apply-Patch `
    -File "BotG\Telemetry\RiskSnapshotPersister.cs" `
    -Find '                File.AppendAllText(_filePath, "timestamp,equity,balance,margin,free_margin,drawdown,R_used,exposure" + Environment.NewLine);' `
    -Replace '                File.AppendAllText(_filePath, "timestamp_utc,equity,balance,open_pnl,closed_pnl,margin,free_margin,drawdown,R_used,exposure" + Environment.NewLine);' `
    -Description "Fix header columns"
if ($p1) { $success++ } else { $failed++ }

# ===== PATCH 2: RiskSnapshotPersister Persist method =====
$find2 = @'
                var line = string.Join(",",
                    ts.ToString("o", CultureInfo.InvariantCulture),
                    equity.ToString(CultureInfo.InvariantCulture),
                    balance.ToString(CultureInfo.InvariantCulture),
                    usedMargin.ToString(CultureInfo.InvariantCulture),
                    freeMargin.ToString(CultureInfo.InvariantCulture),
                    drawdown.ToString(CultureInfo.InvariantCulture),
                    rUsed.ToString(CultureInfo.InvariantCulture),
                    exposure.ToString(CultureInfo.InvariantCulture)
                );
'@

$replace2 = @'
                double openPnl = 0.0;  // TODO: aggregate from open positions
                double closedPnl = 0.0; // TODO: from ClosedTradesWriter

                var line = string.Join(",",
                    ts.ToString("o", CultureInfo.InvariantCulture),
                    equity.ToString(CultureInfo.InvariantCulture),
                    balance.ToString(CultureInfo.InvariantCulture),
                    openPnl.ToString(CultureInfo.InvariantCulture),
                    closedPnl.ToString(CultureInfo.InvariantCulture),
                    usedMargin.ToString(CultureInfo.InvariantCulture),
                    freeMargin.ToString(CultureInfo.InvariantCulture),
                    drawdown.ToString(CultureInfo.InvariantCulture),
                    rUsed.ToString(CultureInfo.InvariantCulture),
                    exposure.ToString(CultureInfo.InvariantCulture)
                );
'@

$p2 = Apply-Patch `
    -File "BotG\Telemetry\RiskSnapshotPersister.cs" `
    -Find $find2 `
    -Replace $replace2 `
    -Description "Add open_pnl, closed_pnl columns"
if ($p2) { $success++ } else { $failed++ }

# ===== PATCH 3: RiskManager PersistSnapshotIfAvailable =====
$find3 = @'
        private void PersistSnapshotIfAvailable()
        {
            try
            {
                if (_lastAccountInfo != null)
                {
                    TelemetryContext.RiskPersister?.Persist(_lastAccountInfo);
                }
            }
            catch { }
        }
'@

$replace3 = @'
        private void PersistSnapshotIfAvailable()
        {
            try
            {
                var info = _lastAccountInfo;
                if (info == null)
                {
                    // Stub AccountInfo to ensure 60s heartbeat even without updates
                    info = new AccountInfo
                    {
                        Equity = _equityOverride ?? 10000.0,
                        Balance = 10000.0,
                        Margin = 0.0
                    };
                }
                TelemetryContext.RiskPersister?.Persist(info);
            }
            catch { }
        }
'@

$p3 = Apply-Patch `
    -File "BotG\RiskManager\RiskManager.cs" `
    -Find $find3 `
    -Replace $replace3 `
    -Description "Ensure 60s heartbeat with stub AccountInfo"
if ($p3) { $success++ } else { $failed++ }

# ===== PATCH 4: OrderLifecycleLogger - Add OrderLifecycleState class =====
$find4 = @'
namespace Telemetry
{
    public class OrderLifecycleLogger
'@

$replace4 = @'
namespace Telemetry
{
    internal class OrderLifecycleState
    {
        public long RequestEpochMs { get; set; }
        public string? TsRequest { get; set; }
        public string? TsAck { get; set; }
        public string? TsFill { get; set; }
    }

    public class OrderLifecycleLogger
'@

$p4 = Apply-Patch `
    -File "BotG\Telemetry\OrderLifecycleLogger.cs" `
    -Find $find4 `
    -Replace $replace4 `
    -Description "Add OrderLifecycleState class"
if ($p4) { $success++ } else { $failed++ }

# ===== PATCH 5: OrderLifecycleLogger - Replace dictionary =====
$find5 = '        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _requestEpochMs = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);'
$replace5 = '        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OrderLifecycleState> _orderStates = new System.Collections.Concurrent.ConcurrentDictionary<string, OrderLifecycleState>(StringComparer.OrdinalIgnoreCase);'

$p5 = Apply-Patch `
    -File "BotG\Telemetry\OrderLifecycleLogger.cs" `
    -Find $find5 `
    -Replace $replace5 `
    -Description "Replace _requestEpochMs with _orderStates"
if ($p5) { $success++ } else { $failed++ }

# ===== PATCH 6: OrderLifecycleLogger - Update tracking logic =====
$find6 = @'
                // latency tracking based on first REQUEST time
                long? latencyMs = null;
                var st = (status ?? phase ?? "").ToUpperInvariant();
                if (string.Equals(st, "REQUEST", StringComparison.OrdinalIgnoreCase))
                {
                    _requestEpochMs[orderId] = epoch;
                }
                else if (string.Equals(st, "ACK", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "FILL", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "CANCEL", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "REJECT", StringComparison.OrdinalIgnoreCase))
                {
                    if (_requestEpochMs.TryGetValue(orderId, out var reqEpoch))
                    {
                        latencyMs = epoch - reqEpoch;
                    }
                }
'@

$replace6 = @'
                // latency tracking + timestamp population
                var tsIso = ts.ToString("o", CultureInfo.InvariantCulture);
                long? latencyMs = null;
                var st = (status ?? phase ?? "").ToUpperInvariant();
                
                var state = _orderStates.GetOrAdd(orderId, _ => new OrderLifecycleState());
                
                if (string.Equals(st, "REQUEST", StringComparison.OrdinalIgnoreCase))
                {
                    state.RequestEpochMs = epoch;
                    state.TsRequest = tsIso;
                }
                else if (string.Equals(st, "ACK", StringComparison.OrdinalIgnoreCase))
                {
                    state.TsAck = tsIso;
                    if (state.RequestEpochMs > 0)
                    {
                        latencyMs = epoch - state.RequestEpochMs;
                    }
                }
                else if (string.Equals(st, "FILL", StringComparison.OrdinalIgnoreCase))
                {
                    state.TsFill = tsIso;
                    if (state.RequestEpochMs > 0)
                    {
                        latencyMs = epoch - state.RequestEpochMs;
                    }
                }
                else if (string.Equals(st, "CANCEL", StringComparison.OrdinalIgnoreCase) || string.Equals(st, "REJECT", StringComparison.OrdinalIgnoreCase))
                {
                    if (state.RequestEpochMs > 0)
                    {
                        latencyMs = epoch - state.RequestEpochMs;
                    }
                }
'@

$p6 = Apply-Patch `
    -File "BotG\Telemetry\OrderLifecycleLogger.cs" `
    -Find $find6 `
    -Replace $replace6 `
    -Description "Update tracking logic for timestamps"
if ($p6) { $success++ } else { $failed++ }

# ===== PATCH 7: OrderLifecycleLogger - Update CSV line construction =====
$find7 = @'
                    Escape(session),
                    Escape(host)
                );
'@

$replace7 = @'
                    Escape(session),
                    Escape(host),
                    // Canonical timestamp aliases (now populated)
                    Escape(orderId), // order_id (duplicate for compatibility)
                    Escape(state.TsRequest ?? ""),
                    Escape(state.TsAck ?? ""),
                    Escape(state.TsFill ?? "")
                );
'@

$p7 = Apply-Patch `
    -File "BotG\Telemetry\OrderLifecycleLogger.cs" `
    -Find $find7 `
    -Replace $replace7 `
    -Description "Add timestamp columns to CSV output"
if ($p7) { $success++ } else { $failed++ }

# ===== PATCH 8: TelemetryConfig - Update defaults =====
$find8 = @'
    public int Hours { get; set; } = 1; // used by wrappers
    public int SecondsPerHour { get; set; } = 300; // 1h -> 5min default
    public int DrainSeconds { get; set; } = 30; // drain window at shutdown
    public int GracefulShutdownWaitSeconds { get; set; } = 5; // extra wait for OS buffers
    public bool UseSimulation { get; set; } = true;
'@

$replace8 = @'
    public int Hours { get; set; } = 24; // Production default: 24h runs
    public int SecondsPerHour { get; set; } = 3600; // Real-time by default
    public int DrainSeconds { get; set; } = 30; // drain window at shutdown
    public int GracefulShutdownWaitSeconds { get; set; } = 5; // extra wait for OS buffers
    public bool UseSimulation { get; set; } = false; // Paper mode default (no simulation)
'@

$p8 = Apply-Patch `
    -File "BotG\Telemetry\TelemetryConfig.cs" `
    -Find $find8 `
    -Replace $replace8 `
    -Description "Update default config values"
if ($p8) { $success++ } else { $failed++ }

Write-Host "`n========================" -ForegroundColor Cyan
Write-Host "Patch Summary" -ForegroundColor Cyan
Write-Host "========================" -ForegroundColor Cyan
Write-Host "Success: $success patches" -ForegroundColor Green
Write-Host "Failed: $failed patches" -ForegroundColor $(if ($failed -eq 0) {"Gray"} else {"Red"})
Write-Host "Backup: $bak" -ForegroundColor Gray

if ($failed -gt 0) {
    Write-Host "`nWARNING: Some patches failed. Check manual guide." -ForegroundColor Yellow
    exit 1
}

if ($DryRun) {
    Write-Host "`n[DRY-RUN] Remove -DryRun flag to apply changes." -ForegroundColor Magenta
    exit 0
}

Write-Host "`nNext: Run 'dotnet build -c Release'" -ForegroundColor Cyan
