using System;
using System.Threading;

namespace BotG.Strategies.Coordination
{
    public sealed class CooldownRecoverySystem
    {
        private DateTime _lastSuccessfulTradeUtc = DateTime.MinValue;
        private int _consecutiveCooldownBlocks;
        private CooldownRecoverySettings _settings = new();

        public void UpdateSettings(CooldownRecoverySettings settings)
        {
            _settings = settings ?? new CooldownRecoverySettings();
            if (!_settings.Enabled)
            {
                Interlocked.Exchange(ref _consecutiveCooldownBlocks, 0);
                _lastSuccessfulTradeUtc = DateTime.MinValue;
            }
        }

        public void RecordCooldownBlock()
        {
            if (!_settings.Enabled)
            {
                return;
            }

            Interlocked.Increment(ref _consecutiveCooldownBlocks);
        }

        public void RecordTradeExecuted()
        {
            if (!_settings.Enabled)
            {
                return;
            }

            _lastSuccessfulTradeUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _consecutiveCooldownBlocks, 0);
        }

        public double GetPenaltyMultiplier()
        {
            if (!_settings.Enabled)
            {
                return 1.0;
            }

            double multiplier = 1.0;

            var blocks = Math.Max(0, Volatile.Read(ref _consecutiveCooldownBlocks));
            if (_settings.MaxCooldownBlocksPerHour > 0 && blocks > _settings.MaxCooldownBlocksPerHour)
            {
                var over = blocks - _settings.MaxCooldownBlocksPerHour;
                var reduction = over * _settings.CooldownRecoveryRate;
                reduction = Math.Min(reduction, _settings.MaximumRecoveryReduction);
                multiplier = Math.Max(_settings.MinimumPenaltyMultiplier, 1.0 - reduction);
            }

            if (_lastSuccessfulTradeUtc != DateTime.MinValue)
            {
                var hoursSinceTrade = (DateTime.UtcNow - _lastSuccessfulTradeUtc).TotalHours;
                if (hoursSinceTrade >= _settings.LongDroughtHours)
                {
                    multiplier = Math.Min(multiplier, _settings.DroughtPenaltyMultiplier);
                }
            }

            return Math.Clamp(multiplier, _settings.MinimumPenaltyMultiplier, 1.0);
        }
    }
}
