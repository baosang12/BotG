# 📋 Checklist triển khai PatternLayer

> **Lưu ý:** `CTRADER_PATH` trỏ tới thư mục documents của cTrader (ví dụ `C:\Users\TechCare\Documents\cAlgo`). Thư mục `Robots` và `Logs` nằm trực tiếp bên trong đường dẫn này.

## 🔖 Thông tin phiên bản

- **Module**: PatternLayer Phase 1
- **Phiên bản**: 1.0.0
- **Môi trường mục tiêu**: cTrader (.NET 6.0)
- **Ngày cập nhật**: 2025-12-03

## 🔧 Chuẩn bị trước triển khai

### Yêu cầu hệ thống

- [ ] Máy Windows 10/11 hoặc Windows Server 2016+
- [ ] Đã cài cTrader và .NET 6.0 Runtime
- [ ] CPU tối thiểu 4 nhân logic, RAM ≥ 8 GB (đảm bảo VolumeProfileDetector không gây nghẽn)
- [ ] Quyền ghi vào `%CTRADER_PATH%\Robots` và `%CTRADER_PATH%\Logs`
- [ ] Tối thiểu 500 MB dung lượng trống cho `Logs\PatternLayer` (telemetry v4 nhiều cột hơn)
- [ ] PowerShell 7+ để chạy scripts

### Kiểm chứng mã nguồn

- [ ] `dotnet test BotG.sln` hoàn tất (269 test tổng, 268 pass, 1 skip có chủ ý)
- [ ] Code review Phase 5 được phê duyệt
- [ ] `dotnet build BotG.sln -c Release` thành công
- [ ] Telemetry PatternLayer hoạt động trong môi trường dev
- [ ] `VolumeProfileDetector_Performance_LessThan5ms` ghi nhận trung bình < 5 ms (log lưu tại `Tests/Preprocessor/...VolumeProfileDetectorTests`)
- [ ] `SimplecTraderTelemetryLoggerTests` và `cTraderTelemetryLoggerTests` xác nhận `TelemetryVersion = 4`

### Sao lưu cài đặt hiện có

```powershell
$ctrader = $env:CTRADER_PATH
if (-not $ctrader) { $ctrader = "C:\\Users\\TechCare\\Documents\\cAlgo" }
$robots = Join-Path $ctrader "Robots"
$backupDir = "C:\\Backup\\BotG_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
New-Item $backupDir -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $robots "BotG.algo") (Join-Path $backupDir "BotG.algo") -Force
Copy-Item (Join-Path $ctrader "TrendAnalyzerConfig.json") $backupDir -Force -ErrorAction SilentlyContinue
```

- [ ] Xác nhận backup chứa `BotG.algo` và (nếu có) `TrendAnalyzerConfig.json`

## 🚀 Quy trình triển khai

### Bước 1: Build gói triển khai

```powershell
# Tại thư mục repo
./scripts/build-release.ps1 -Configuration Release -Clean
```

- [ ] `BuildOutput` chứa `BotG.algo`, các file config mẫu, scripts, docs
- [ ] ZIP `BotG_PatternLayer_Deployment_*.zip` được tạo

### Bước 2: Copy sang cTrader

```powershell
./scripts/deploy-to-ctrader.ps1 -CTraderPath "C:\\Users\\TechCare\\Documents\\cAlgo"
# Hoặc bỏ qua backup:
./scripts/deploy-to-ctrader.ps1 -CTraderPath "..." -SkipBackup
```

- [ ] `BotG.algo` trong `%CTRADER_PATH%\Robots` cập nhật timestamp mới
- [ ] Thư mục backup `Backup_yyyyMMdd_HHmmss` xuất hiện nếu không `-SkipBackup`
- [ ] `%CTRADER_PATH%\Logs\PatternLayer` tồn tại và có quyền ghi
- [ ] Không còn file DLL/PDB rơi vãi trong `Robots`

### Bước 3: Điều chỉnh cấu hình (nếu sử dụng file ngoài)

`TrendAnalyzerConfig.json` nằm tại `%CTRADER_PATH%\TrendAnalyzerConfig.json` (không nằm trong `Robots`).

```json
{
   "FeatureFlags": {
      "UsePatternLayer": true
   },
   "PatternTelemetry": {
      "EnablePatternLogging": true,
      "LogDirectory": "%CTRADER_PATH%\\Logs\\PatternLayer\\",
      "EnableConsoleOutput": true,
      "SampleRate": 1,
      "EnableDebugMode": false
   },
   "LayerWeights": {
      "Patterns": 0.10
   }
}
```

- [ ] Đảm bảo `LogDirectory` trỏ về đúng ổ đĩa/partition
- [ ] Điều chỉnh `SampleRate` (1 cho debug, >1 cho production)
- [ ] Thêm block `PatternLayer.VolumeProfile` nếu muốn kích hoạt detector mới (tham số xem mục 3.3 của Deployment Guide)
- [ ] Xác nhận `VolumeProfile.Weight` không làm tổng `LayerWeights` > 1; điều chỉnh các layer khác khi cần

## ✅ Kiểm tra sau triển khai

### Kiểm tra ngay lập tức

- [ ] Khởi động cTrader không lỗi
- [ ] BotG load thành công trên chart demo
- [ ] Console hiển thị thông điệp PatternLayer init (nếu bật)
- [ ] Không có lỗi quyền truy cập log

### Kiểm tra chức năng

- [ ] Sinh ít nhất một snapshot PatternLayer trong 5 phút đầu
- [ ] Các flag `LiquidityGrab`, `CleanBreakout`, `FailedBreakout` xuất hiện hợp lý
- [ ] Thời gian xử lý trung bình < 5 ms/tick (quan sát trong log)

### Kiểm tra telemetry

```powershell
Get-ChildItem "$env:CTRADER_PATH\Logs\PatternLayer" -Filter *.csv | Select-Object -Last 3
```

- [ ] CSV mới tạo, timestamp UTC đúng
- [ ] Cột `PatternScore`, `MarketCondition`, `VolumeProfileScore`, `VolumeProfilePOC` có giá trị
- [ ] Dung lượng file tăng đều, không vượt 10 MB/giờ với SampleRate = 1
- [ ] `TelemetryVersion` hiển thị = 4 trên tất cả dòng mới
- [ ] Header chứa đầy đủ các cột VolumeProfile (`VolumeProfilePOC`, `VolumeProfileVAHigh`, `VolumeProfileVALow`, `HVNCount`, `LVNCount`, `VolumeConcentration`)

```powershell
Get-Content "$env:CTRADER_PATH\Logs\PatternLayer\PatternLayer_*.csv" -TotalCount 1 \
   | Select-String "VolumeProfilePOC,VolumeProfileVAHigh,VolumeProfileVALow,VolumeProfileFlags"
```

### Checklist riêng cho VolumeProfileDetector

- [ ] Console của cTrader log thông điệp `VolumeProfile detector initialized` ngay sau khi BotG start
- [ ] `PatternLayer` CSV ghi nhận ít nhất một flag thuộc `VolumeProfileFlags` (`HVN`, `NearPOC`, `ValueAreaBreakUp`...) trong 30 phút đầu
- [ ] `ProcessingTimeMs` trung bình < 5 ms kể cả khi VolumeProfile bật (đọc trực tiếp cột `ProcessingTimeMs`)
- [ ] `VolumeProfileScore` dao động trong khoảng 40-70 khi thị trường bình thường; nếu kẹt tại 50 > 2h cần kiểm tra feed
- [ ] `HVNCount` + `LVNCount` không đều 0; nếu toàn 0 nghĩa là chưa đủ bar (`MinBars`) → cần sát thời gian warm-up lâu hơn

## 📊 Theo dõi & bảo trì

### Checklist hằng ngày

- [ ] `Logs\PatternLayer` không vượt 70% dung lượng ổ
- [ ] Không có lỗi `UnauthorizedAccess` trong console/log
- [ ] PatternLayer vẫn tạo output (không bị silent)
- [ ] CPU BotG < 10% trung bình

### Metric mục tiêu

| Metric | Mục tiêu | Báo động |
| --- | --- | --- |
| Thời gian phân tích | < 5 ms | > 10 ms |
| Dung lượng RAM BotG | < 100 MB | > 200 MB |
| Dung lượng CSV/ngày | < 250 MB | > 500 MB |
| Tỉ lệ lỗi logger | 0 | > 1 lỗi/giờ |
| VolumeProfileScore trung bình | 40 - 70 | < 25 hoặc > 80 liên tục 2h |

### Điều kiện cảnh báo

- ❌ Không có CSV mới trong 30 phút
- ❌ PatternLayer không sinh flag nào 24 h
- ❌ Lỗi IO/permission xuất hiện lặp lại
- ❌ Telemetry làm trễ (processing time > 15 ms)

## 🔄 Quy trình rollback

### Tự động (dựa vào backup script)

```powershell
$ctrader = "C:\\Users\\TechCare\\Documents\\cAlgo"
$robots = Join-Path $ctrader "Robots"
$lastBackup = Get-ChildItem $ctrader -Directory -Filter "Backup_*" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($lastBackup) {
      Copy-Item (Join-Path $lastBackup.FullName "BotG.algo") (Join-Path $robots "BotG.algo") -Force
      if (Test-Path (Join-Path $lastBackup.FullName "TrendAnalyzerConfig.json")) {
          Copy-Item (Join-Path $lastBackup.FullName "TrendAnalyzerConfig.json") $ctrader -Force
      }
      Write-Host "Đã rollback từ $($lastBackup.FullName)"
}
```

### Thủ công

1. [ ] Stop BotG trong cTrader
2. [ ] Copy trả `BotG.algo` cũ vào `Robots`
3. [ ] (Tuỳ chọn) Copy trả `TrendAnalyzerConfig.json`
4. [ ] Nếu có chỉnh `PatternLayer.VolumeProfile`, hoàn nguyên block này về giá trị trước rollout
5. [ ] Khởi động lại cTrader và test nhanh

## 🆘 Troubleshooting

| Tình huống | Triệu chứng | Cách xử lý |
| --- | --- | --- |
| Không có CSV | Thư mục log rỗng | Kiểm tra quyền ghi, path `%CTRADER_PATH%\Logs\PatternLayer`, dung lượng ổ, `EnablePatternLogging` |
| CPU cao | cTrader lag | Tăng `SampleRate`, tắt `EnableConsoleOutput`, đảm bảo không bật debug mode |
| Không thấy flag | Không có cột flag = true | Đảm bảo `UsePatternLayer` = true, dữ liệu thị trường đủ biến động, điều chỉnh thresholds |
| Lỗi quyền log | Console báo Access Denied | Chạy deploy script bằng admin, cấp `Modify` cho Everyone lên thư mục log |

## ✅ Hoàn tất

- [ ] Checklist này đã ký bởi Dev/QA/Ops
- [ ] Artifact và log triển khai lưu tại kho lưu trữ chuẩn
- [ ] Bàn giao cho nhóm vận hành trong ca kế tiếp
