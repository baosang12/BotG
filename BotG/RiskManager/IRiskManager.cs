using cAlgo.API;
using Bar = DataFetcher.Models.Bar;
using Position = cAlgo.API.Position;
using TradeResult = cAlgo.API.TradeResult;

namespace BotG.RiskManager
{
    /// <summary>
    /// <summary>
    /// Giao diện chính cho module quản trị rủi ro.
    /// </summary>
    public interface IRiskManager
    {
        // 1. Xác định và kiểm soát rủi ro cấp giao dịch
        void EvaluateTradeRisk(Bar bar, double atr, double proposedSize, out double adjustedSize, out double stopLoss, out double takeProfit);

        // 2. Quản lý rủi ro tổng danh mục
        void EvaluatePortfolioRisk();

        // 3. Giám sát margin và đòn bẩy
        void MonitorMargin();

        // 4. Quản lý SL/TP (trailing stop, break-even)
        void ManageStops(Position position);

        // 5. Giám sát slippage và chi phí
        void MonitorSlippage(TradeResult result);

        // 6. Stress Testing & Scenario Analysis
        void RunStressTests();

        // 7. Giám sát và cảnh báo thời gian thực
        void CheckAlerts();

        // 8. Báo cáo và lưu trữ dữ liệu rủi ro
        void GenerateReports();

        // 9. Khởi tạo hoặc thiết lập cấu hình
        void Initialize(RiskSettings settings);
    }
}
