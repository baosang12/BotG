using System;
using System.Globalization;

namespace Telemetry
{
    /// <summary>
    /// Snapshot of a single position for risk tracking
    /// </summary>
    public class PositionSnapshot
    {
        public string Symbol { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty; // "Long" or "Short"
        public double Volume { get; set; }
        public double EntryPrice { get; set; }
        public double CurrentPrice { get; set; }
        public double UnrealizedPnL { get; set; }
        public double Pips { get; set; }
        public double UsedMargin { get; set; }
        public DateTime OpenTime { get; set; }
        public long Id { get; set; }
        
        /// <summary>
        /// Create position snapshot from cTrader Position object
        /// </summary>
        public static PositionSnapshot FromPosition(dynamic position)
        {
            try
            {
                return new PositionSnapshot
                {
                    Symbol = position.SymbolName?.ToString() ?? position.SymbolCode?.ToString() ?? "UNKNOWN",
                    Direction = position.TradeType?.ToString() ?? "UNKNOWN",
                    Volume = (double)(position.VolumeInUnits ?? position.Volume ?? 0),
                    EntryPrice = (double)(position.EntryPrice ?? 0),
                    CurrentPrice = (double)(position.CurrentPrice ?? 0),
                    UnrealizedPnL = (double)(position.NetProfit ?? 0),
                    Pips = (double)(position.Pips ?? 0),
                    UsedMargin = (double)(position.Margin ?? 0),
                    OpenTime = position.EntryTime ?? DateTime.UtcNow,
                    Id = (long)(position.Id ?? 0)
                };
            }
            catch
            {
                // Fallback for missing properties
                return new PositionSnapshot
                {
                    Symbol = "ERROR",
                    Direction = "UNKNOWN",
                    UnrealizedPnL = 0.0
                };
            }
        }
        
        /// <summary>
        /// Format as CSV row: symbol,direction,volume,entry,current,pnl,pips,margin,open_time,id
        /// </summary>
        public string ToCsvRow()
        {
            return string.Join(",",
                CsvEscape(Symbol),
                CsvEscape(Direction),
                Volume.ToString(CultureInfo.InvariantCulture),
                EntryPrice.ToString(CultureInfo.InvariantCulture),
                CurrentPrice.ToString(CultureInfo.InvariantCulture),
                UnrealizedPnL.ToString(CultureInfo.InvariantCulture),
                Pips.ToString(CultureInfo.InvariantCulture),
                UsedMargin.ToString(CultureInfo.InvariantCulture),
                OpenTime.ToString("o", CultureInfo.InvariantCulture),
                Id.ToString(CultureInfo.InvariantCulture)
            );
        }

        private static string CsvEscape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Contains(',') || value.Contains('"'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }
    }
    
    /// <summary>
    /// Portfolio-level aggregations for risk analysis
    /// </summary>
    public class PortfolioMetrics
    {
        public double TotalLongExposure { get; set; }
        public double TotalShortExposure { get; set; }
        public double NetExposure { get; set; }
        public double LargestPositionPnL { get; set; }
        public double LargestPositionPercent { get; set; }
        public string MostExposedSymbol { get; set; } = string.Empty;
        public double MostExposedSymbolVolume { get; set; }
        public int TotalPositions { get; set; }
        public int LongPositions { get; set; }
        public int ShortPositions { get; set; }
        
        /// <summary>
        /// Calculate portfolio metrics from position snapshots
        /// </summary>
        public static PortfolioMetrics Calculate(PositionSnapshot[] positions, double totalEquity)
        {
            var metrics = new PortfolioMetrics
            {
                TotalPositions = positions.Length
            };
            
            if (positions.Length == 0)
                return metrics;
            
            // Calculate exposures
            foreach (var pos in positions)
            {
                var notional = Math.Abs(pos.Volume * pos.CurrentPrice);
                
                if (pos.Direction.Contains("Buy") || pos.Direction.Contains("Long"))
                {
                    metrics.TotalLongExposure += notional;
                    metrics.LongPositions++;
                }
                else if (pos.Direction.Contains("Sell") || pos.Direction.Contains("Short"))
                {
                    metrics.TotalShortExposure += notional;
                    metrics.ShortPositions++;
                }
                
                // Track largest position
                if (Math.Abs(pos.UnrealizedPnL) > Math.Abs(metrics.LargestPositionPnL))
                {
                    metrics.LargestPositionPnL = pos.UnrealizedPnL;
                    if (totalEquity > 0)
                    {
                        metrics.LargestPositionPercent = (Math.Abs(pos.UnrealizedPnL) / totalEquity) * 100.0;
                    }
                }
            }
            
            metrics.NetExposure = metrics.TotalLongExposure - metrics.TotalShortExposure;
            
            // Find most exposed symbol
            var symbolGroups = new System.Collections.Generic.Dictionary<string, double>();
            foreach (var pos in positions)
            {
                var notional = Math.Abs(pos.Volume * pos.CurrentPrice);
                if (!symbolGroups.ContainsKey(pos.Symbol))
                    symbolGroups[pos.Symbol] = 0.0;
                symbolGroups[pos.Symbol] += notional;
            }
            
            foreach (var kvp in symbolGroups)
            {
                if (kvp.Value > metrics.MostExposedSymbolVolume)
                {
                    metrics.MostExposedSymbol = kvp.Key;
                    metrics.MostExposedSymbolVolume = kvp.Value;
                }
            }
            
            return metrics;
        }
        
        /// <summary>
        /// Format as CSV row for appending to risk snapshot
        /// </summary>
        public string ToCsvRow()
        {
            return string.Join(",",
                TotalLongExposure.ToString(CultureInfo.InvariantCulture),
                TotalShortExposure.ToString(CultureInfo.InvariantCulture),
                NetExposure.ToString(CultureInfo.InvariantCulture),
                LargestPositionPnL.ToString(CultureInfo.InvariantCulture),
                LargestPositionPercent.ToString(CultureInfo.InvariantCulture),
                CsvEscape(MostExposedSymbol),
                MostExposedSymbolVolume.ToString(CultureInfo.InvariantCulture),
                TotalPositions.ToString(CultureInfo.InvariantCulture),
                LongPositions.ToString(CultureInfo.InvariantCulture),
                ShortPositions.ToString(CultureInfo.InvariantCulture)
            );
        }

        private static string CsvEscape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Contains(',') || value.Contains('"'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }
    }
}
