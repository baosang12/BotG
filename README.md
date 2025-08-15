# BotG Runtime Configuration

- Risk.PointValuePerUnit: Monetary value (in account currency) per one price unit per one volume unit. Used for sizing when converting price stop distance to risk per unit.
- Default is 1.0. TODO: replace with the broker-specific contract value (e.g., XAUUSD on ICMarkets RAW).
- Configure in `config.runtime.json` at repo root:

```
{
  "Risk": {
    "PointValuePerUnit": 1.0,
    "RiskPercentPerTrade": 0.01,
    "MinRiskUsdPerTrade": 3.0,
    "StopLossAtrMultiplier": 1.8
  }
}
```

Notes:
- If the config is missing, defaults are used and the app continues to run.
- ATR-based SL is still TODO to be wired with a real provider.
