# Broker Command Investigation — 2025-10-31

## Summary
- Runtime artifacts for the 2025-10-31 session contain zero order activity (no REQUEST/ACK/FILL rows were produced).
- The preflight canary proof generated alongside the run shows the canary pipeline never issued a request.
- The robot bootstrap wires an empty strategy list and nothing in the codebase calls `_tradeManager.Process(...)`, so the execution module never submits orders to the broker.
- Canary trades are disabled by default (`TelemetryConfig.Preflight.Canary.Enabled = false`), so there is no automated fallback that would attempt a broker round-trip.

## Evidence
- Orders log contains only the CSV header (no data rows):

  ```powershell
  PS D:\botg\logs> Get-Content orders.csv | Select-Object -First 1
  phase,timestamp_iso,epoch_ms,orderId,intendedPrice,stopLoss,execPrice,theoretical_lots,theoretical_units,requestedVolume,filledSize,slippage,brokerMsg,client_order_id,side,action,type,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled,session,host,order_id,timestamp_request,timestamp_ack,timestamp_fill,symbol,bid_at_request,ask_at_request,spread_pips_at_request,bid_at_fill,ask_at_fill,spread_pips_at_fill,request_server_time,fill_server_time,timestamp,requested_lots,commission_usd,spread_cost_usd,slippage_pips
  PS D:\botg\logs> Get-Content orders.csv | Select-Object -Skip 1 -First 3
  #
  ```

  (File: `d:\botg\logs\orders.csv`)

- Canary proof written during the same window confirms no broker request occurred:

  ```powershell
  PS D:\botg\logs\preflight> Get-Content -Raw canary_proof.json
  {
    "generated_at": "2025-10-31T03:36:46.8361722Z",
    "fill": false,
    "ok": false,
    "ack": false,
    "requested": false,
    "label": "BotG_CANARY",
    "close": false
  }
  ```

  (File: `d:\botg\logs\preflight\canary_proof.json`)

- Code search shows nothing ever calls the trade manager’s `Process` method, so execution never reaches order submission:

  ```powershell
  PS D:\repos\BotG> rg --iglob '*.cs' "_tradeManager\.Process" --stats

  0 matches
  0 matched lines
  0 files contained matches
  180 files searched
  0 bytes printed
  697841 bytes searched
  ```

- Key source references:
  - `BotG/BotGRobot.cs:95` — constructs `strategies` as an empty list before creating the trade manager.
  - `BotG/TradeManager/TradeManager.cs:41` — `Process` delegates to `_executionModule.Execute(...)`, but no caller exists.
  - `BotG/Telemetry/TelemetryConfig.cs:277` — `CanaryConfig.Enabled` default is `false`, so the optional broker canary never runs without explicit configuration.

## Conclusion
Because no component ever enqueues trade signals, the execution module never issues broker commands. The disabled canary leaves the system without any automated sanity check that would submit a test order, which matches the empty telemetry artifacts.

## Suggested Next Actions
1. Wire at least one strategy or scheduler to call `_tradeManager.Process(...)` with real signals once preflight gates pass.
2. Enable `TelemetryConfig.Preflight.Canary.Enabled` when operating in paper mode so the canary can validate broker connectivity automatically.
3. After making the above changes, re-run the bot and confirm new entries appear in `orders.csv` alongside a `requested:true` canary proof.

