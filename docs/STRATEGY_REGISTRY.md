# Registry chiến lược

Từ bản build ngày 2025-11-21, BotG tải danh sách chiến lược từ file `strategy_registry.json` (cùng thư mục với `config.runtime.json`).

## Cấu trúc file

```json
{
  "strategies": [
    {
      "name": "SmaCrossover",
      "type": "Strategies.SmaCrossoverStrategy",
      "enabled": true,
      "dependencies": ["MultiTimeframe"],
      "parameters": { }
    }
  ]
}
```

- `name`: nhãn nội bộ, dùng trong log.
- `type`: tên đầy đủ của lớp chiến lược.
- `enabled`: `true` để bật chiến lược, `false` để tắt mà không cần rebuild.
- `dependencies`: (tùy chọn) các điều kiện hạ tầng (`MultiTimeframe`, `Regime`, `SessionAnalyzer`). Nếu điều kiện chưa sẵn sàng, chiến lược bị bỏ qua và ghi log lý do.
- `parameters`: (tùy chọn) khối cấu hình riêng cho chiến lược. Ví dụ `BreakoutStrategy` ánh xạ trực tiếp vào `BreakoutStrategyConfig`.

## Hot reload

- Nếu `config.runtime.json` có block
  ```json
  "StrategyRegistry": {
    "ConfigPath": "strategy_registry.json",
    "HotReloadEnabled": true,
    "WatchDebounceSeconds": 2.0
  }
  ```
  thì bot tự theo dõi file và tự động reload sau mỗi lần chỉnh sửa (debounce theo `WatchDebounceSeconds`).
- Log `STRATEGY/WatcherReady` xác nhận watcher đã bật. Khi reload thành công sẽ có log `STRATEGY/RegistryApply` kèm danh sách chiến lược bật/tắt.

## Fallback

- Nếu file rỗng hoặc lỗi parse, bot sẽ quay về bộ chiến lược mặc định (SMA, RSI, PriceAction, Volatility, Breakout).
- Log `STRATEGY/RegistryFallback` sẽ xuất hiện để cảnh báo đang dùng cấu hình legacy.

## Audit lệnh

- Mỗi khi chiến lược yêu cầu đặt lệnh, TradeManager ghi log `TRADE/StrategyOrder` chứa `strategy`, `action`, `price`, `timestamp` để dễ truy vết nguồn gốc lệnh.

## Quy trình thêm chiến lược mới

1. Tạo lớp chiến lược trong `BotG/Strategies/...` và triển khai `IStrategy`.
2. Nếu cần, bổ sung factory trong `Strategies/Registry/StrategyRegistry.cs` (hoặc ánh xạ đến loại có sẵn).
3. Thêm entry mới vào `strategy_registry.json` và set `enabled=true`.
4. Đợi log `STRATEGY/RegistryApply` xác nhận chiến lược đã được load (hoặc restart bot trong môi trường không bật hot reload).
