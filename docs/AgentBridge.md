# Agent Communication Bridge

This lightweight bridge lets Agent A và Agent B chia sẻ tiến độ realtime bằng cách cập nhật một file JSON duy nhất trong repo (`monitor_logs/agent_bridge_status.json`).

## 1. Luồng giao tiếp

1. Mỗi khi trạng thái thay đổi (nhận task mới, hoàn thành, gặp blocker) agent cập nhật trường tương ứng trong file JSON.
2. Người còn lại dùng `Get-Content -Wait` để theo dõi thay đổi gần realtime (PowerShell tự động stream khi file đổi).
3. Mọi cập nhật đều ghi `lastUpdatedUtc` theo ISO 8601 để dễ audit.

## 2. Định dạng dữ liệu

```json
{
  "bridgeVersion": 1,
  "lastUpdatedUtc": "2025-11-14T00:00:00Z",
  "agents": {
    "agentA": {
      "owner": "Agent A",
      "status": "busy|idle|blocked",
      "currentTask": "",
      "nextDeliverable": "",
      "blockers": [],
      "notes": "",
    },
    "agentB": { /* cấu trúc tương tự */ }
  },
  "shared": {
    "currentFocus": "goal cả hai đang xử lý",
    "criticalRisks": [],
    "handoffRequests": [],
    "nextSyncWindowUtc": ""
  }
}
```

Giữ `bridgeVersion` = 1 để nhận biết schema hiện tại. Nếu cần thêm trường, cập nhật cả JSON lẫn tài liệu này.

## 3. Cập nhật nhanh bằng PowerShell

```pwsh
$bridge = Get-Content d:/repos/BotG/monitor_logs/agent_bridge_status.json | ConvertFrom-Json
$bridge.lastUpdatedUtc = (Get-Date).ToUniversalTime().ToString('o')
$bridge.agents.agentA.status = 'busy'
$bridge.agents.agentA.currentTask = 'Fix Bayesian fusion regression'
$bridge.agents.agentA.nextDeliverable = 'All Bayesian tests green'
$bridge.agents.agentA.blockers = @()
$bridge.agents.agentA.notes = 'ETA 45m'
$bridge.shared.currentFocus = 'Bayesian fusion + breakout handshake'
$bridge | ConvertTo-Json -Depth 5 | Set-Content d:/repos/BotG/monitor_logs/agent_bridge_status.json
```

Sử dụng `ConvertTo-Json -Depth 5` để đảm bảo toàn bộ cây dữ liệu được serialize.

## 4. Theo dõi realtime

```pwsh
Get-Content d:/repos/BotG/monitor_logs/agent_bridge_status.json -Wait
```

PowerShell sẽ stream thay đổi ngay khi file được ghi lại.

## 5. Nghi thức cập nhật

- **Tần suất:** tối thiểu 15 phút/lần hoặc ngay khi có blocker/hoàn thành milestone.
- **Blocker flag:** thêm mô tả cụ thể vào `blockers` và ping agent còn lại qua `handoffRequests`.
- **Handoff:** khi cần Agent B hỗ trợ, thêm bản ghi mới vào `shared.handoffRequests` dạng `{"from":"Agent A","item":"Need breakout evidence normalized"}`.
- **Reset cuối ngày:** dọn `blockers`, cập nhật `status` về `idle`, và ghi chú tổng kết vào `notes` để archive.

## 6. Checklist vận hành

- [ ] Giữ JSON luôn hợp lệ (dùng `ConvertTo-Json` thay vì chỉnh tay bằng editor không hỗ trợ).
- [ ] Khi merge thay đổi schema, thông báo qua tài liệu này và bump `bridgeVersion`.
- [ ] Theo dõi file bằng `-Wait` trong background terminal để tránh bỏ sót update.

Bridge này đủ nhẹ để chạy nội bộ nhưng vẫn đảm bảo cả hai agent có cùng nguồn sự thật chung theo thời gian thực.
