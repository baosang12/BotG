using System;
using DataFetcher.Models;
using DataFetcher.PreProcessors;
using DataFetcher.Aggregators;
using DataFetcher.Caching;
using Telemetry; // added

namespace DataFetcher.Service
{
    public class DataFetcherService
    {
        private readonly TickPreProcessor _tickPreProcessor;
        private readonly BarAggregator _barAggregator;
        private readonly TickCache _tickCache;
        private readonly BarCache _barCache;
        private AccountInfo _accountInfo;

        public DataFetcherService(int tickCacheSize = 1000)
        {
            _tickPreProcessor = new TickPreProcessor();
            _barAggregator = new BarAggregator();
            _tickCache = new TickCache(tickCacheSize);
            _barCache = new BarCache();
            // ensure telemetry initialized
            TelemetryContext.InitOnce();
        }

        public void UpdateAccount(AccountInfo info)
        {
            _accountInfo = info;
            // push to RiskManager snapshot persister if available
            try { TelemetryContext.RiskPersister?.Persist(info); } catch {}
        }

        public void OnTick(Tick tick)
        {
            // Tiền xử lý tick, lưu cache
            // TODO: Gọi TickPreProcessor thực tế
            _tickCache.Add(tick);
            try { TelemetryContext.Collector?.IncTick(); } catch {}
        }

        public void OnBar(Bar bar)
        {
            // Gom bar, lưu cache
            // TODO: Gọi BarAggregator thực tế
            _barCache.AddBar(bar.Tf, bar);
        }

        public TickCache GetTickCache() => _tickCache;
        public BarCache GetBarCache() => _barCache;
        public AccountInfo GetAccountInfo() => _accountInfo;
    }
}
