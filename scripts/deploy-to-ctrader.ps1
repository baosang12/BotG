[CmdletBinding()]
param(
    [string]$CTraderPath,
    [string]$Configuration = "Release",
    [switch]$SkipBackup,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "   BotG PatternLayer - Triển khai cTrader" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

if (-not $CTraderPath) {
    $CTraderPath = $env:CTRADER_PATH
}

if (-not $CTraderPath) {
    Write-Host "[1/4] Lỗi: chưa cấu hình đường dẫn cTrader" -ForegroundColor Red
    Write-Host "   Dùng tham số -CTraderPath hoặc thiết lập CTRADER_PATH." -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $CTraderPath)) {
    Write-Host "[1/4] Lỗi: không tìm thấy thư mục: $CTraderPath" -ForegroundColor Red
    exit 1
}

function Resolve-RobotsDirectory {
    param([string]$Path)

    $leaf = Split-Path $Path -Leaf

    switch ($leaf) {
        "Robots" { return @{ Robots = $Path; Root = Split-Path $Path -Parent } }
        "cAlgo"  { return @{ Robots = Join-Path $Path "Robots"; Root = $Path } }
        default {
            $robotsCandidate = Join-Path $Path "Robots"
            if (Test-Path $robotsCandidate) { return @{ Robots = $robotsCandidate; Root = $Path } }

            $documentsCandidate = Join-Path $Path "cAlgo/Robots"
            if (Test-Path $documentsCandidate) {
                return @{ Robots = $documentsCandidate; Root = Join-Path $Path "cAlgo" }
            }

            return @{ Robots = $Path; Root = (Split-Path $Path -Parent) }
        }
    }
}

$resolved = (Resolve-Path -Path $CTraderPath).Path
$resolvedInfo = Resolve-RobotsDirectory -Path $resolved
$robotsDir = $resolvedInfo.Robots
$cAlgoRoot = $resolvedInfo.Root

if (-not (Test-Path $robotsDir)) {
    New-Item -Path $robotsDir -ItemType Directory -Force | Out-Null
}

$algoSource = Join-Path "./BotG/bin/$Configuration/net6.0" "BotG.algo"
if (-not (Test-Path $algoSource)) {
    Write-Host "[2/4] Lỗi: không tìm thấy BotG.algo tại $algoSource" -ForegroundColor Red
    Write-Host "   Hãy chạy scripts/build-release.ps1 trước." -ForegroundColor Yellow
    exit 1
}

Write-Host "[2/4] Chuẩn bị thư mục Robots: $robotsDir" -ForegroundColor Yellow

if (-not $SkipBackup) {
    $existingAlgo = Join-Path $robotsDir "BotG.algo"
    if (Test-Path $existingAlgo) {
        $backupDir = Join-Path $cAlgoRoot ("Backup_" + (Get-Date -Format "yyyyMMdd_HHmmss"))
        New-Item -Path $backupDir -ItemType Directory -Force | Out-Null
        Copy-Item -Path $existingAlgo -Destination (Join-Path $backupDir "BotG.algo") -Force
        Write-Host "   ✓ Đã backup BotG.algo tới $backupDir" -ForegroundColor Green
    }
}
else {
    Write-Host "   ⚠ Bỏ qua backup theo tuỳ chọn -SkipBackup" -ForegroundColor Yellow
}

Write-Host "[3/4] Copy BotG.algo" -ForegroundColor Yellow
try {
    Copy-Item -Path $algoSource -Destination (Join-Path $robotsDir "BotG.algo") -Force
    $info = Get-Item (Join-Path $robotsDir "BotG.algo")
    Write-Host "   ✓ Đã triển khai BotG.algo (timestamp: $($info.LastWriteTime))" -ForegroundColor Green
}
catch {
    Write-Host "   ✗ Không thể copy BotG.algo: $_" -ForegroundColor Red
    if (-not $Force) { exit 1 }
}

$logDir = "d:\botg\logs\patternlayer"
if (-not (Test-Path $logDir)) {
    New-Item -Path $logDir -ItemType Directory -Force | Out-Null
}

try {
    $acl = Get-Acl $logDir
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule("Everyone","Modify","ContainerInherit,ObjectInherit","None","Allow")
    $acl.SetAccessRule($rule)
    Set-Acl -Path $logDir -AclObject $acl
    Write-Host "   ✓ Đã thiết lập quyền ghi cho $logDir" -ForegroundColor Green
}
catch {
    Write-Host "   ⚠ Không thể đặt ACL cho thư mục log: $_" -ForegroundColor Yellow
}

Write-Host "[4/4] Tóm tắt" -ForegroundColor Cyan
Write-Host "   Nguồn .algo : $algoSource" -ForegroundColor White
Write-Host "   Thư mục Robots: $robotsDir" -ForegroundColor White
Write-Host ""
Write-Host "=========================================" -ForegroundColor Green
Write-Host "   Deployment hoàn tất" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
Write-Host "Tiếp theo:" -ForegroundColor Cyan
Write-Host "1. Mở cTrader và nhấn Build để xác nhận" -ForegroundColor White
Write-Host "2. Kiểm tra telemetry khi load BotG" -ForegroundColor White
