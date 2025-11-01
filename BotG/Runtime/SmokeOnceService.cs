using Telemetry;

namespace BotG.Runtime
{
    public sealed class SmokeOnceService
    {
        private bool _fired;
        public bool Fired => _fired;

        // Returns true if we should fire smoke_once given current config and internal state
        public bool ShouldFire(TelemetryConfig cfg)
        {
            try
            {
                if (cfg == null) return false;
                var opsOn = cfg.Ops != null && cfg.Ops.EnableTrading;
                var simOff = cfg.Simulation != null ? (cfg.Simulation.Enabled == false) : true; // default to allow if Simulation missing
                var debugOn = cfg.Debug != null && cfg.Debug.SmokeOnce;
                return opsOn && simOff && debugOn && !_fired;
            }
            catch { return false; }
        }

        public void MarkFired()
        {
            _fired = true;
        }
    }
}
