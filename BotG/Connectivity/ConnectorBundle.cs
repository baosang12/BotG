using System;
using cAlgo.API;
using Connectivity.CTrader;
using Connectivity.Synthetic;

namespace Connectivity
{
    public sealed class ConnectorBundle
    {
        public string Mode { get; }
        public IMarketDataProvider MarketData { get; }
        public IOrderExecutor OrderExecutor { get; }
    internal ICTraderTickPump? TickPump { get; }

        private ConnectorBundle(string mode, IMarketDataProvider md, IOrderExecutor ex, ICTraderTickPump? pump)
        {
            Mode = mode;
            MarketData = md;
            OrderExecutor = ex;
            TickPump = pump;
        }

        public static ConnectorBundle Create(Robot? robot, string? explicitMode = null)
        {
            var mode = ResolveMode(explicitMode);
            return CreateInternal(mode, robot);
        }

        public static ConnectorBundle Create(string? explicitMode = null)
        {
            var mode = ResolveMode(explicitMode);
            return CreateInternal(mode, null);
        }

        private static string ResolveMode(string? explicitMode)
        {
            var mode = explicitMode;
            if (string.IsNullOrWhiteSpace(mode))
            {
                mode = Environment.GetEnvironmentVariable("DATASOURCE__MODE");
            }
            if (string.IsNullOrWhiteSpace(mode))
            {
                mode = "ctrader_demo";
            }
            return mode.Trim().ToLowerInvariant();
        }

        private static ConnectorBundle CreateInternal(string mode, Robot? robot)
        {
            switch (mode)
            {
                case "ctrader_demo":
                case "ctrader":
                case "live":
                    if (robot == null)
                    {
                        throw new InvalidOperationException("cTrader connector requires a Robot instance");
                    }
                    var md = new CTraderMarketDataProvider(robot);
                    var ex = new CTraderOrderExecutor(robot);
                    return new ConnectorBundle(mode, md, ex, md);
                case "synthetic":
                default:
                    var brokerName = Environment.GetEnvironmentVariable("BROKER__NAME") ?? "Synthetic";
                    var server = Environment.GetEnvironmentVariable("BROKER__SERVER") ?? "synthetic";
                    var accountId = Environment.GetEnvironmentVariable("BROKER__ACCOUNT_ID") ?? "SIM";
                    var synthMd = new SyntheticMarketDataProvider
                    {
                        BrokerName = brokerName,
                        Server = server,
                        AccountId = accountId
                    };
                    var synthExec = new SyntheticOrderExecutor(synthMd)
                    {
                        BrokerName = brokerName,
                        Server = server,
                        AccountId = accountId
                    };
                    return new ConnectorBundle("synthetic", synthMd, synthExec, null);
            }
        }
    }
}
