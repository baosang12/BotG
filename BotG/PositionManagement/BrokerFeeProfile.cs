using System;
using System.Collections.Generic;
using cAlgo.API;

namespace BotG.PositionManagement
{
    /// <summary>
    /// Mô tả phí hoa hồng và swap của từng symbol để bổ sung buffer cho breakeven.
    /// </summary>
    public class BrokerFeeProfile
    {
        /// <summary>Hoa hồng tính theo USD cho mỗi 1 triệu USD khối lượng (per side).</summary>
        public double CommissionPerMillionUsdPerSide { get; set; }

        /// <summary>Chi phí swap (pip) mỗi ngày cho lệnh BUY (dùng trị tuyệt đối dương).</summary>
        public double OvernightSwapPipsBuyCost { get; set; }

        /// <summary>Chi phí swap (pip) mỗi ngày cho lệnh SELL (dùng trị tuyệt đối dương).</summary>
        public double OvernightSwapPipsSellCost { get; set; }
    }

    public static class BrokerFeeTable
    {
        private static readonly Dictionary<string, BrokerFeeProfile> Profiles = new Dictionary<string, BrokerFeeProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["EURUSD"] = new BrokerFeeProfile
            {
                CommissionPerMillionUsdPerSide = 30.0,
                OvernightSwapPipsBuyCost = 0.93,
                OvernightSwapPipsSellCost = 0.0
            },
            ["XAUUSD"] = new BrokerFeeProfile
            {
                CommissionPerMillionUsdPerSide = 30.0,
                OvernightSwapPipsBuyCost = 53.06,
                OvernightSwapPipsSellCost = 0.0
            },
            ["EURGBP"] = new BrokerFeeProfile
            {
                CommissionPerMillionUsdPerSide = 30.0,
                OvernightSwapPipsBuyCost = 0.75,
                OvernightSwapPipsSellCost = 0.0
            },
            ["BTCUSD"] = new BrokerFeeProfile
            {
                CommissionPerMillionUsdPerSide = 0.0,
                OvernightSwapPipsBuyCost = 20.0,
                OvernightSwapPipsSellCost = 0.0
            }
        };

        public static bool TryGetProfile(string symbolName, out BrokerFeeProfile profile)
        {
            profile = null;
            if (string.IsNullOrWhiteSpace(symbolName))
            {
                return false;
            }

            return Profiles.TryGetValue(symbolName.Trim(), out profile);
        }
    }
}
