# Monitoring

monitoring_summary.json fields:
- fill_rate: fraction of fills to requests
- trades_per_min: trades closed per minute
- avg_pnl_per_trade: average net PnL per trade
- max_drawdown: from analysis
- orphan_fills_count: from reconcile

Alerts: fill_rate < 0.9, orphan_fills > 0, max_drawdown > configured threshold.
