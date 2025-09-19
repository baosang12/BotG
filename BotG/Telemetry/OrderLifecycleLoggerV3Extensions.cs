using System.Globalization;

namespace Telemetry
{
    public static class OrderLifecycleLoggerExtensions
    {
        // V3 API with streamlined parameters for SMC requirements
        public static void LogV3(this OrderLifecycleLogger logger, string phase, string orderId, string side,
                          double priceRequested, double? priceFilled,
                          double? stopLoss, double? takeProfit,
                          string status, string reason,
                          long? latencyMs, double? sizeRequested, double? sizeFilled)
        {
            logger.LogV2(
                phase: phase,
                orderId: orderId,
                clientOrderId: orderId,
                side: side,
                action: side?.ToUpperInvariant(),
                type: "Market",
                intendedPrice: priceRequested,
                stopLoss: stopLoss,
                execPrice: priceFilled,
                theoreticalLots: null,
                theoreticalUnits: sizeRequested,
                requestedVolume: sizeRequested,
                filledSize: sizeFilled,
                status: status,
                reason: reason,
                session: "SMC"
            );
        }
    }
}