# Gate2 Timer Start Fix
# Problem: RiskManager timer never started because Harness calls Initialize(), not IModule.Initialize()
# Solution: Start timer in Initialize() method

$ErrorActionPreference = "Stop"
$repo = "D:\OneDrive\TAILIU~1\cAlgo\Sources\Robots\BotG"
if (-not (Test-Path "$repo\BotG.sln")) {
    throw "Not in BotG repo root"
}

$file = "$repo\BotG\RiskManager\RiskManager.cs"
$backup = "$repo\path_issues\gate2_fixes\backup_timer_fix_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
New-Item -ItemType Directory -Path $backup -Force | Out-Null
Copy-Item $file "$backup\RiskManager.cs" -Force

# Read file
$content = Get-Content $file -Raw

# Find string to replace: end of Initialize() method before closing brace
$oldStr = @"
            // Attempt auto-compute from symbol if settings did not provide a value
            TryAutoComputePointValueFromSymbol();
        }
"@

$newStr = @"
            // Attempt auto-compute from symbol if settings did not provide a value
            TryAutoComputePointValueFromSymbol();

            // Start risk snapshot timer at 60s period
            _snapshotTimer?.Change(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
"@

if ($content -notlike "*$oldStr*") {
    Write-Host "[ERROR] Find string not found in $file" -ForegroundColor Red
    Write-Host "Looking for:" -ForegroundColor Yellow
    Write-Host $oldStr
    exit 1
}

$newContent = $content.Replace($oldStr, $newStr)
[System.IO.File]::WriteAllText($file, $newContent, [System.Text.Encoding]::UTF8)

Write-Host "[OK] Timer start patch applied to RiskManager.cs" -ForegroundColor Green
Write-Host "Backup: $backup\RiskManager.cs" -ForegroundColor Cyan
