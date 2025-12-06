using cAlgo.API;
using cAlgo.API.Internals;
using System;

namespace TradeManager
{
    /// <summary>
    /// Chuyển đổi risk USD và stop loss (pips) thành khối lượng lệnh theo yêu cầu broker.
    /// </summary>
    public static class VolumeCalculator
    {
        /// <summary>
        /// Tính khối lượng (units) dựa trên số USD muốn mạo hiểm và stop loss pips.
        /// </summary>
        /// <param name="riskUsd">Số tiền USD muốn mạo hiểm.</param>
        /// <param name="stopLossPips">Stop loss (pips).</param>
        /// <param name="symbol">Đối tượng Symbol từ cAlgo để lấy pip value và volume min.</param>
        /// <returns>Khối lượng lệnh (units) tối thiểu đạt broker yêu cầu.</returns>
        public static double Calculate(double riskUsd, double stopLossPips, cAlgo.API.Internals.Symbol symbol)
        {
            if (symbol == null)
                throw new ArgumentNullException(nameof(symbol));

            // ========================================
            // 🚨 BTCUSD VOLUME FIX - Friday Night 2025-11-07
            // Bitcoin price: >$100,000 USD
            // Fixed volume: 0.001 BTC ≈ $100 (safe for weekend run)
            // ========================================
            if (symbol.Name.ToUpper().Contains("BTC"))
            {
                // Return broker-defined minimum volume to avoid BadVolume errors
                // BTCUSD typically uses fractional contracts (e.g. 0.01 BTC)
                // Using the minimum ensures the order respects crypto limits
                var safeVolume = symbol.VolumeInUnitsMin;

                // Ensure volume respects broker step increments when available
                if (symbol.VolumeStep > 0)
                {
                    var steps = Math.Max(1, Math.Round(safeVolume / symbol.VolumeStep));
                    safeVolume = steps * symbol.VolumeStep;
                }

                return safeVolume;
            }

            // ========================================
            // 📋 ORIGINAL FOREX CALCULATION (preserved)
            // Used for EURUSD and other forex pairs
            // ========================================
            if (stopLossPips <= 0)
                // Invalid stop loss, return minimum volume
                return symbol.VolumeInUnitsMin;

            double pipValue = symbol.PipValue;
            double units = riskUsd / (stopLossPips * pipValue);
            // Đảm bảo không nhỏ hơn minimum volume broker quy định
            return Math.Max(units, symbol.VolumeInUnitsMin);
        }
    }
}
