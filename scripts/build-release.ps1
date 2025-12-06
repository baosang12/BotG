[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "./BuildOutput",
    [switch]$Clean,
    [switch]$DeployToCTrader,
    [string]$CTraderPath = $env:CTRADER_PATH
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "   BotG PatternLayer - Quy trình Build & Deployment" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

function New-OutputDirectory {
    param([string]$Path)

    if ($Clean -and (Test-Path $Path)) {
        Write-Host "[1/6] Đang xoá thư mục đầu ra cũ..." -ForegroundColor Yellow
        Remove-Item -Path $Path -Recurse -Force
        Write-Host "   ✓ Đã xoá thư mục $Path" -ForegroundColor Green
    }

    if (-not (Test-Path $Path)) {
        Write-Host "[1/6] Đang tạo thư mục đầu ra..." -ForegroundColor Yellow
        New-Item -Path $Path -ItemType Directory -Force | Out-Null
        Write-Host "   ✓ Đã tạo thư mục $Path" -ForegroundColor Green
    }
}

try {
    New-OutputDirectory -Path $OutputDir
}
catch {
    Write-Host "   ✗ Không thể chuẩn bị thư mục đầu ra: $_" -ForegroundColor Red
    exit 1
}

Write-Host "[2/6] Đang build solution ($Configuration)..." -ForegroundColor Yellow
& dotnet build BotG.sln -c $Configuration --no-incremental | Write-Host
if ($LASTEXITCODE -ne 0) {
    Write-Host "   ✗ Build thất bại với exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

$packageFiles = @(
    @{ Source = "./BotG/bin/$Configuration/net6.0/BotG.algo"; Dest = "BotG.algo"; Required = $true },
    @{ Source = "./BotG/bin/$Configuration/net6.0/BotG.dll"; Dest = "BotG.dll"; Required = $true },
    @{ Source = "./BotG/bin/$Configuration/net6.0/BotG.pdb"; Dest = "BotG.pdb"; Required = $false }
)

Write-Host "[3/6] Đang thu thập artifacts..." -ForegroundColor Yellow
foreach ($file in $packageFiles) {
    if (Test-Path $file.Source) {
        Copy-Item -Path $file.Source -Destination (Join-Path $OutputDir $file.Dest) -Force
        Write-Host "   ✓ Đã sao chép $($file.Dest)" -ForegroundColor Green
    }
    elseif ($file.Required) {
        Write-Host "   ✗ Thiếu file bắt buộc: $($file.Source)" -ForegroundColor Red
        exit 1
    }
    else {
        Write-Host "   ⚠ File tuỳ chọn không tồn tại: $($file.Dest)" -ForegroundColor Yellow
    }
}

$configFiles = @(
    @{ Source = "./config/TrendAnalyzerConfig.ctrader.json"; Dest = "TrendAnalyzerConfig.ctrader.json" },
    @{ Source = "./config/TrendAnalyzerConfig.development.json"; Dest = "TrendAnalyzerConfig.development.json" },
    @{ Source = "./config/TrendAnalyzerConfig.default.json"; Dest = "TrendAnalyzerConfig.default.json" }
)

foreach ($file in $configFiles) {
    if (Test-Path $file.Source) {
        Copy-Item -Path $file.Source -Destination (Join-Path $OutputDir $file.Dest) -Force
        Write-Host "   ✓ Đã sao chép $($file.Dest)" -ForegroundColor Green
    }
    else {
        Write-Host "   ⚠ Không tìm thấy cấu hình: $($file.Source)" -ForegroundColor Yellow
    }
}

$scriptFiles = @(
    @{ Source = "./scripts/deploy-to-ctrader.ps1"; Dest = "deploy-to-ctrader.ps1" },
    @{ Source = "./scripts/start-cTrader.bat"; Dest = "start-cTrader.bat" }
)

foreach ($file in $scriptFiles) {
    if (Test-Path $file.Source) {
        Copy-Item -Path $file.Source -Destination (Join-Path $OutputDir $file.Dest) -Force
        Write-Host "   ✓ Đã sao chép $($file.Dest)" -ForegroundColor Green
    }
    else {
        Write-Host "   ⚠ Script chưa sẵn sàng: $($file.Source)" -ForegroundColor Yellow
    }
}

$docFiles = @(
    @{ Source = "./docs/deployment-checklist.md"; Dest = "deployment-checklist.md" },
    @{ Source = "./docs/PATTERNLAYER_DEPLOYMENT_GUIDE.md"; Dest = "PATTERNLAYER_DEPLOYMENT_GUIDE.md" }
)

foreach ($file in $docFiles) {
    if (Test-Path $file.Source) {
        Copy-Item -Path $file.Source -Destination (Join-Path $OutputDir $file.Dest) -Force
        Write-Host "   ✓ Đã sao chép $($file.Dest)" -ForegroundColor Green
    }
    else {
        Write-Host "   ⚠ Thiếu tài liệu: $($file.Source)" -ForegroundColor Yellow
    }
}

$readmeContent = @"
# BotG PatternLayer - Gói triển khai

## Nội dung
- BotG.algo: gói duy nhất mà cTrader load được
- BotG.dll / BotG.pdb: phục vụ debug và automation nội bộ
- Bộ cấu hình TrendAnalyzerConfig.*
- Scripts triển khai và tài liệu hướng dẫn

## Triển khai nhanh
1. Chạy `sync_to_ctrader.ps1` (nếu chưa đồng bộ source) rồi build trong cTrader hoặc dùng `dotnet build` để sinh `.algo`
2. Chạy `deploy-to-ctrader.ps1` để copy BotG.algo sang thư mục `cAlgo\Robots`
3. Khởi động cTrader, nạp BotG algorithm và xác nhận telemetry PatternLayer hoạt động

## Lưu ý cấu hình
- Dùng `TrendAnalyzerConfig.ctrader.json` khi copy sang cTrader (`TrendAnalyzerConfig.json`)
- Dùng bản `development` cho môi trường test
- Điều chỉnh `PatternTelemetry.LogDirectory` cho phù hợp phân vùng ổ đĩa

## Telemetry
- CSV lưu tại `d:\botg\logs\patternlayer\`
- Console log có thể bật/tắt trong `PatternTelemetry.EnableConsoleOutput`

Chi tiết thêm: xem `deployment-checklist.md`.
"@

$readmePath = Join-Path $OutputDir "README_DEPLOYMENT.md"
Set-Content -Path $readmePath -Value $readmeContent -Encoding UTF8
Write-Host "   ✓ Đã tạo README_DEPLOYMENT.md" -ForegroundColor Green

Write-Host "[4/6] Đang tạo gói ZIP..." -ForegroundColor Yellow
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$deploymentPackage = Join-Path $OutputDir "BotG_PatternLayer_Deployment_$timestamp.zip"
Compress-Archive -Path (Join-Path $OutputDir '*') -DestinationPath $deploymentPackage -CompressionLevel Optimal -Force
Write-Host "   ✓ Đã tạo gói: $(Split-Path $deploymentPackage -Leaf)" -ForegroundColor Green

if ($DeployToCTrader) {
    Write-Host "[5/6] Bắt đầu copy sang cTrader..." -ForegroundColor Magenta
    if (-not $CTraderPath) {
        Write-Host "   ✗ Chưa khai báo đường dẫn cTrader. Dùng tham số -CTraderPath hoặc biến CTRADER_PATH." -ForegroundColor Red
        exit 1
    }

    if (-not (Test-Path $CTraderPath)) {
        Write-Host "   ✗ Đường dẫn cTrader không tồn tại: $CTraderPath" -ForegroundColor Red
        exit 1
    }

    if (Test-Path "./scripts/deploy-to-ctrader.ps1") {
        Write-Host "   → Gọi script deploy-to-ctrader.ps1" -ForegroundColor Cyan
        & ./scripts/deploy-to-ctrader.ps1 -CTraderPath $CTraderPath -Configuration $Configuration
    }
    else {
        Write-Host "   ⚠ Không tìm thấy scripts/deploy-to-ctrader.ps1, bỏ qua bước copy tự động" -ForegroundColor Yellow
    }
}

Write-Host "[6/6] Tóm tắt" -ForegroundColor Cyan
Write-Host "   Cấu hình build : $Configuration" -ForegroundColor White
Write-Host "   Output folder  : $OutputDir" -ForegroundColor White
Write-Host "   Gói ZIP        : $(Split-Path $deploymentPackage -Leaf)" -ForegroundColor White

if ($DeployToCTrader -and $CTraderPath) {
    Write-Host "   Đã triển khai  : $CTraderPath" -ForegroundColor White
}
else {
    Write-Host "   Đã triển khai  : Chưa (dùng -DeployToCTrader để copy)" -ForegroundColor White
}

Write-Host ""
Write-Host "=========================================" -ForegroundColor Green
Write-Host "   Build & Deployment hoàn tất" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
