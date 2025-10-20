using System;
using System.Threading.Tasks;

namespace Connectivity
{
    public enum OrderSide
    {
        Buy,
        Sell
    }

    public enum OrderType
    {
        Market,
        Limit
    }

    public sealed record Quote(string Symbol, double Bid, double Ask, DateTime TimestampUtc);

    public sealed record NewOrder(
        string OrderId,
        string Symbol,
        OrderSide Side,
        double Volume,
        OrderType Type = OrderType.Market,
        double? Price = null,
        double? StopLoss = null,
        string? ClientTag = null
    );

    public sealed record OrderFill(
        string OrderId,
        string Symbol,
        OrderSide Side,
        double Price,
        double Volume,
        DateTime TimestampServer,
        DateTime TimestampLocal,
        string BrokerMessage
    );

    public sealed record OrderReject(
        string OrderId,
        string Symbol,
        string Reason,
        DateTime TimestampServer,
        DateTime TimestampLocal,
        string BrokerMessage
    );

    public interface IMarketDataProvider
    {
        event Action<Quote>? OnQuote;
        void Subscribe(string symbol);
        void Start();
        void Stop();
        string BrokerName { get; }
        string Server { get; }
        string AccountId { get; }
    }

    public interface IOrderExecutor
    {
        event Action<OrderFill>? OnFill;
        event Action<OrderReject>? OnReject;
        string BrokerName { get; }
        string Server { get; }
        string AccountId { get; }
        Task SendAsync(NewOrder order);
    }
}
