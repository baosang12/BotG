# Hướng dẫn triển khai PatternLayer trên cTrader

## 1. Tổng quan
PatternLayer Phase 1 bổ sung telemetry CSV/console để giám sát chất lượng tín hiệu. Tài liệu này mô tả toàn bộ quy trình build, triển khai và giám sát trên môi trường cTrader (.NET 6.0).

> **Lưu ý:** `CTRADER_PATH` trỏ tới thư mục documents của cTrader (ví dụ `C:\Users\TechCare\Documents\cAlgo`). Tại đây có các thư mục `Robots`, `Logs` và file `TrendAnalyzerConfig.json`. cTrader chỉ nhận gói `BotG.algo` trong `Robots`; không copy DLL/PDB trực tiếp.

## 2. Triển khai nhanh

### 2.1 Lệnh một bước
```powershell
./scripts/build-release.ps1 -Configuration Release -DeployToCTrader -CTraderPath "C:\Users\TechCare\Documents\cAlgo"
```

### 2.2 Quy trình từng bước
1. Build gói: `./scripts/build-release.ps1 -Configuration Release -Clean`
2. Deploy `.algo`: `./scripts/deploy-to-ctrader.ps1 -CTraderPath "C:\Users\TechCare\Documents\cAlgo"`
3. Cấu hình `TrendAnalyzerConfig.json` (nằm ở root `CTRADER_PATH`)
4. Mở cTrader, nạp BotG và xác minh telemetry

## 3. Chi tiết cấu hình

### 3.1 Telemetry PatternLayer
```json
{
  "PatternTelemetry": {
    "EnablePatternLogging": true,
    "LogDirectory": "%CTRADER_PATH%\\Logs\\PatternLayer\\",
    "SampleRate": 1,
    "EnableConsoleOutput": true,
    "EnableDebugMode": false,
    "DebugSampleRate": 10,
    "DebugMinScoreThreshold": 50.0
  }
}
```
- `SampleRate`: số lượng tick bỏ qua (1 = log mọi tick)
- `EnableConsoleOutput`: bật log console để quan sát nhanh
- `EnableDebugMode`: chỉ dùng khi cần phân tích sâu (tăng chi phí CPU)

### 3.2 Kích hoạt PatternLayer
`TrendAnalyzerConfig.json` nên đặt ở root `CTRADER_PATH` (không nằm trong `Robots`). Ví dụ block kích hoạt:

```json
{
  "FeatureFlags": {
    "UsePatternLayer": true
  },
  "LayerWeights": {
    "Patterns": 0.10
  }
}
```

### 3.3 Tinh chỉnh VolumeProfileDetector

| Tham số | Giá trị mặc định | Miền được chuẩn hoá | Ghi chú |
| --- | --- | --- | --- |
| `LookbackBars` | 120 | 40 - 600 | Số lượng bar H1 dùng để xây profile |
| `MinBars` | 80 | 10 - `LookbackBars` | Ngưỡng tối thiểu để chạy detector |
| `NumberOfBuckets` | 24 | 10 - 80 | Độ phân giải profile, càng cao càng chi tiết |
| `ValueAreaPercentage` | 0.70 | 0.50 - 0.90 | % volume nằm trong Value Area |
| `HighVolumeThreshold` | 1.4 | >= 1.1 (và > `LowVolumeThreshold`) | Hệ số xác định HVN |
| `LowVolumeThreshold` | 0.6 | 0.1 - 0.95 | Hệ số xác định LVN |
| `PrimaryTimeFrame` | `"H1"` | `H1/H4/D1` | Timeframe cơ sở dùng để gom profile |

`TrendAnalyzerConfig.json` mẫu:

```json
{
  "PatternLayer": {
    "VolumeProfile": {
      "Enabled": true,
      "Weight": 0.25,
      "Parameters": {
        "LookbackBars": 150,
        "MinBars": 100,
        "NumberOfBuckets": 30,
        "ValueAreaPercentage": 0.70,
        "HighVolumeThreshold": 1.35,
        "LowVolumeThreshold": 0.55,
        "PrimaryTimeFrame": "H1"
      }
    }
  }
}
```

Khuyến nghị tuning:

- **Scalping / session M15**: hạ `LookbackBars` xuống 80, giảm `NumberOfBuckets` còn 18 để tránh nhiễu, giữ `PrimaryTimeFrame = H1` để ổn định.
- **Swing H4/D1**: nâng `LookbackBars` lên 240, tăng `ValueAreaPercentage` lên 0.75 để bao trùm vùng tích luỹ dài hơn.
- **Thị trường nhiễu**: tăng `LowVolumeThreshold` lên ~0.7 để loại bỏ LVN yếu, đồng thời giảm `HighVolumeThreshold` về 1.3 nhằm dễ bắt HVN mới hình thành.

Mọi tham số đều được `Normalize()` trước khi sử dụng, vì vậy nếu lỡ cấu hình giá trị vượt ngưỡng, engine sẽ tự kẹp về biên an toàn. Khi thay đổi trọng số `Weight`, đừng quên re-balance tổng `LayerWeights` để tránh PatternLayer chiếm tỷ lệ quá cao.

## 4. Telemetry output

### 4.1 Lược đồ CSV (Telemetry v4)

| Cột | Kiểu | Miền giá trị / chuẩn hóa | Ý nghĩa |
| --- | --- | --- | --- |
| `TimestampUTC` | datetime (UTC) | `yyyy-MM-dd HH:mm:ss.fff` | Thời điểm ghi nhận phân tích |
| `Symbol` | string | Mã cặp FX/CFD | Định danh tài sản |
| `Timeframe` | string | `M1`/`M5`/`H1`/`H4`/`D1`... | Khung thời gian snapshot |
| `PatternScore` | double | 0-100 | Điểm tổng hợp PatternLayer |
| `LiquidityScore` | double | 0-100 | Điểm detector Liquidity |
| `BreakoutScore` | double | 0-100 | Điểm detector BreakoutQuality |
| `LiquidityGrabFlag` | bool | `True/False` | Giá vừa grab thanh khoản |
| `CleanBreakoutFlag` | bool | `True/False` | Breakout sạch, ít wick |
| `FailedBreakoutFlag` | bool | `True/False` | Breakout thất bại |
| `ProcessingTimeMs` | double | >=0 | Thời gian xử lý của PatternLayer |
| `MarketCondition` | string | `trend/range/volatile/...` | Điều kiện thị trường suy diễn |
| `RSI` | double | 0-100 | RSI (nếu có) tại snapshot |
| `VolumeRatio` | double | >=0 | Tỉ lệ volume hiện tại so với trung bình |
| `CandleSize` | double | >=0 | Kích thước nến (pip/poin) |
| `AccumulationScore` | double | 0-100 | Điểm Accumulation/Distribution |
| `AccumulationFlags` | string | ví dụ `Distribution\|RangeCompression` | Các flag tích luỹ |
| `AccumulationConfidence` | double | 0-1 | Mức tin cậy detector Accumulation |
| `PhaseDetected` | string | `Accumulation/Distribution/...` | Pha thị trường nhận diện |
| `MarketStructureScore` | double | 0-100 | Điểm MarketStructure |
| `MarketStructureState` | string | `Uptrend/Downtrend/Range` | Trạng thái cấu trúc |
| `MarketStructureTrendDirection` | int | `-1/0/1` | Hướng trend (bear/flat/bull) |
| `MarketStructureBreakDetected` | bool | `True/False` | Phát hiện BOS/CHOCH |
| `MarketStructureSwingPoints` | int | >=0 | Số swing point dùng trong phân tích |
| `LastSwingHigh` | double | giá | Swing high gần nhất |
| `LastSwingLow` | double | giá | Swing low gần nhất |
| `VolumeProfileScore` | double | 0-100 | Điểm VolumeProfileDetector |
| `VolumeProfilePOC` | double | giá | Point of Control (POC) |
| `VolumeProfileVAHigh` | double | giá | Biên trên Value Area |
| `VolumeProfileVALow` | double | giá | Biên dưới Value Area |
| `VolumeProfileFlags` | string | ví dụ `HVN\|NearPOC\|ValueAreaBreakUp` | Các tín hiệu VP |
| `HVNCount` | int | >=0 | Số vùng High Volume Node |
| `LVNCount` | int | >=0 | Số vùng Low Volume Node |
| `VolumeConcentration` | double | 0-1 | % volume nằm trong VA |
| `TelemetryVersion` | int | >=1 | Phiên bản schema (hiện = 4) |

Tất cả số thực được ghi theo `InvariantCulture` (dấu chấm thập phân). File mới mặc định sinh mỗi 24h và được ghi tại `%CTRADER_PATH%\Logs\PatternLayer`.

#### Ví dụ header + sample row (v4)

```text
TimestampUTC,Symbol,Timeframe,PatternScore,LiquidityScore,BreakoutScore,LiquidityGrabFlag,CleanBreakoutFlag,FailedBreakoutFlag,ProcessingTimeMs,MarketCondition,RSI,VolumeRatio,CandleSize,AccumulationScore,AccumulationFlags,AccumulationConfidence,PhaseDetected,MarketStructureScore,MarketStructureState,MarketStructureTrendDirection,MarketStructureBreakDetected,MarketStructureSwingPoints,LastSwingHigh,LastSwingLow,VolumeProfileScore,VolumeProfilePOC,VolumeProfileVAHigh,VolumeProfileVALow,VolumeProfileFlags,HVNCount,LVNCount,VolumeConcentration,TelemetryVersion
2025-12-06 07:44:31.967,EURUSD,M15,72.30,65.40,63.50,True,False,False,0.005,trend,54.00,1.18,0.42,58.00,Distribution,0.52,Distribution,67.80,Uptrend,1,True,6,1.10850,1.10220,69.10,1.10620,1.10980,1.10310,ValueAreaBreakUp|NearPOC,3,1,0.7400,4
```

### 4.2 Tương thích ngược

- Các parser cũ chỉ đọc 14 cột vẫn hoạt động: có thể cắt bỏ phần hậu bằng `TelemetryVersion` để phát hiện schema.
- `TelemetryVersion` tăng khi bổ sung cột mới; production parser nên kiểm tra `>=4` trước khi sử dụng dữ liệu VolumeProfile.
- Nếu cần giữ log v3 để so sánh, có thể đặt `EmitLegacyColumns=true` trong tool phân tích (không ảnh hưởng tới cTrader logger).

### 4.3 Console

```text
[08:30:00.123] PATTERN [EURUSD M5]: Score=85.5 | LiquidityGrab=True
[08:30:00.125] PATTERN LAYER ANALYSIS - EURUSD M5 | SampleRate=1 | Telemetry=ON
```

## 5. Hiệu năng và tinh chỉnh

| Chế độ | SampleRate | Console | DebugMode | Mục đích |
| --- | --- | --- | --- | --- |
| Debug sâu | 1 | Bật | Bật | Điều tra hoặc tuning |
| Giám sát demo | 5 | Bật | Tắt | Demo/QA |
| Production | 10 | Tắt | Tắt | Vận hành dài hạn |

Khuyến nghị giữ `ProcessingTimeMs < 5` và dùng SSD cho thư mục log.

## 6. Giám sát

- `%CTRADER_PATH%\\Logs\\PatternLayer\\PatternLayer_*.csv`: quay vòng hàng ngày
- Dung lượng dự kiến: ~10 MB/giờ với SampleRate 1
- CSV tương thích Excel, Power BI, pandas hoặc Logstash

## 7. Troubleshooting

| Vấn đề | Nguyên nhân thường gặp | Cách khắc phục |
| --- | --- | --- |
| Không tạo CSV | Sai `LogDirectory`, thiếu quyền | Chạy deploy bằng admin, cấp quyền Modify, kiểm tra ổ còn trống |
| CPU cao | SampleRate quá thấp hoặc bật debug | Tăng SampleRate, tắt debug/console |
| Không thấy flag | `UsePatternLayer` = false hoặc dữ liệu phẳng | Bật flag, xác minh feed, điều chỉnh thresholds |
| Lỗi logger | Đường dẫn không tồn tại | Tạo lại `Logs\PatternLayer`, kiểm tra ACL |

## 8. Bảo trì định kỳ

1. Kiểm tra dung lượng thư mục log mỗi sáng
2. Sao lưu `TrendAnalyzerConfig.json` trước khi chỉnh
3. Lặp lại build + deploy sau mỗi thay đổi lớn
4. Theo dõi CPU/Memory trong Task Manager khi market biến động cao

## 9. Quy trình cập nhật

1. Backup cài đặt hiện tại (`scripts/deploy-to-ctrader.ps1` tự tạo nếu không `-SkipBackup`)
2. Build phiên bản mới
3. Deploy và ghi chú timestamp
4. Theo dõi telemetry tối thiểu 24h trước khi chốt production

## 10. Tài liệu liên quan

- `docs/deployment-checklist.md`
- `docs/PATTERN_LAYER_CONFIGURATION_GUIDE.md`
- `scripts/start-cTrader.bat`

---
**Cập nhật cuối**: 2025-12-03
