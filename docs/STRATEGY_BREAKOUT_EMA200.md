# Chiến lược Breakout EMA200

Chiến lược swing mới tập trung bắt chuyển pha giá quanh đường `EMA200` trên khung H1 (mặc định), ưu tiên độ chính xác hơn là số lượng lệnh.

## Logic tín hiệu

- **Điều kiện nền tảng**
  - Phải có tối thiểu 240 nến trên timeframe được chọn (mặc định H1).
  - ATR(14) ≥ `MinimumAtr` (mặc định 0.0008) và ADX(14) ≥ `MinimumAdx` (mặc định 22).
- **Điểm kích hoạt**
  - Nến trước đóng cửa cùng phía với EMA200, nến hiện tại xuyên qua và đóng cửa đối diện => xác định breakout.
  - Khoảng cách giữa giá đóng cửa và EMA200 phải ≥ `max(BreakoutBuffer, ATR * MinimumDistanceAtrMultiple)` để loại bỏ nhiễu nhỏ.
- **Quản trị vị thế**
  - Stop-loss đặt cách EMA200 `AtrStopMultiplier * ATR` về phía đối diện.
  - Take-profit đặt `AtrTakeProfitMultiplier * ATR` theo hướng breakout.
  - Cooldown mặc định 90 phút để tránh vào liên tiếp cùng chiều.

## Tham số chính (`Ema200BreakoutStrategyConfig`)

| Tham số | Mô tả | Giá trị mặc định |
| --- | --- | --- |
| `TriggerTimeframe` | Timeframe dùng để kiểm tra breakout (`H1`, `H4`, …) | `H1` |
| `EmaPeriod` | Chu kỳ EMA tham chiếu | `200` |
| `AtrPeriod` / `AdxPeriod` | Chu kỳ ATR/ADX | `14` |
| `MinimumAtr` | Biến động tối thiểu (đơn vị giá) | `0.0008` |
| `MinimumAdx` | Lực xu hướng tối thiểu | `22` |
| `BreakoutBuffer` | Biên độ yêu cầu vượt EMA | `0.0003` |
| `MinimumDistanceAtrMultiple` | Yêu cầu tối thiểu theo ATR | `0.25` |
| `AtrStopMultiplier` / `AtrTakeProfitMultiplier` | Hệ số ATR cho SL/TP | `1.6` / `3.2` |
| `CooldownMinutes` | Thời gian chờ trước khi tái entry cùng chiều | `90` |

## Wiring

- Registry: `strategy_registry.json` đã thêm entry `Ema200Breakout` (mặc định disable, bật lên bằng cách đặt `"enabled": true`).
- Runtime: `config.runtime.json`
  - `StrategyCoordination.StrategyWeights` và module fusion đã chứa trọng số mặc định.
  - `ExitProfiles.StrategyOverrides` map chiến lược này sang profile `breakout_aggressive`.

## Kiểm thử & vận hành

1. `dotnet build` bảo đảm project compile.
2. Sao chép `strategy_registry.json` và `config.runtime.json` sang thư mục runtime (`D:\botg`).
3. Khởi động lại daemon hoặc bot để nhận chiến lược mới.
4. Theo dõi log `PIPELINE` để xác minh sự kiện `DeepSeek.Ema200Breakout` xuất hiện cùng dữ liệu ATR/ADX và mô tả trigger.

Có thể tinh chỉnh thêm ngưỡng ATR/ADX hoặc timeframe thông qua tham số registry mà không cần rebuild.
