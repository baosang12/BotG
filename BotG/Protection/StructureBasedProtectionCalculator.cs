namespace Protection
{
    // Structure-based: SL đặt sau cấu trúc + buffer (ATR/spread/min)
    // Đầu ra vẫn theo pips và giá, TP = SL * RR (mặc định)
    public class StructureBasedProtectionCalculator : IProtectionCalculator
    {
        public ProtectionResult Compute(ProtectionInputs inputs)
        {
            // 1) Tính buffer (pips)
            var atr = inputs.AtrPips > 0 ? inputs.AtrPips : 0.0;
            var spread = inputs.SpreadPips > 0 ? inputs.SpreadPips : 0.0;
            var minFloor = 5.0; // tối thiểu 5 pips để tránh quá sát
            var bufferPips = System.Math.Max(minFloor, System.Math.Max(atr * 1.2, spread * 3.0));

            // 2) Xác định SL price theo cấu trúc + buffer
            double slPrice;
            if (inputs.TradeType == cAlgo.API.TradeType.Buy)
            {
                var anchor = inputs.StructureLowPrice > 0 ? inputs.StructureLowPrice : (inputs.EntryPrice - inputs.ConfiguredStopLossPips * inputs.PipSize);
                slPrice = anchor - bufferPips * inputs.PipSize;
            }
            else
            {
                var anchor = inputs.StructureHighPrice > 0 ? inputs.StructureHighPrice : (inputs.EntryPrice + inputs.ConfiguredStopLossPips * inputs.PipSize);
                slPrice = anchor + bufferPips * inputs.PipSize;
            }

            // 3) Tính slPips từ entry
            var slPipsRaw = System.Math.Abs(inputs.EntryPrice - slPrice) / inputs.PipSize;
            var slPips = (int)System.Math.Ceiling(slPipsRaw);
            if (slPips <= 0) slPips = inputs.ConfiguredStopLossPips > 0 ? inputs.ConfiguredStopLossPips : 1;

            // 4) TP theo RR (đơn giản). Có thể thay đổi nếu cần logic TP khác
            var tpPips = (int)System.Math.Round(slPips * inputs.RiskRewardRatio);

            // 5) Giá TP theo hướng
            double tpPrice;
            if (inputs.TradeType == cAlgo.API.TradeType.Buy)
                tpPrice = inputs.EntryPrice + tpPips * inputs.PipSize;
            else
                tpPrice = inputs.EntryPrice - tpPips * inputs.PipSize;

            return new ProtectionResult
            {
                StopLossPips = slPips,
                TakeProfitPips = tpPips,
                StopLossPrice = slPrice,
                TakeProfitPrice = tpPrice,
                Notes = "Structure-based: anchor + buffer"
            };
        }
    }
}
