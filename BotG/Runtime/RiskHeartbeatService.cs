using System;
using Telemetry;

namespace BotG.Runtime {
  public sealed class RiskHeartbeatService {
    private DateTime _last = DateTime.MinValue;
    private readonly TimeSpan _period;
    private readonly Telemetry.RiskSnapshotPersister _persister;
    private readonly BotGRobot _robot;
    public RiskHeartbeatService(BotGRobot robot, Telemetry.RiskSnapshotPersister persister, int seconds=15) {
      _robot = robot; _persister = persister; _period = TimeSpan.FromSeconds(seconds);
    }
    public void Tick() {
      var now = DateTime.UtcNow;
      if (now - _last < _period) return;
      _last = now;
      // Convert BotGRobot.Account (cAlgo API) to DataFetcher.Models.AccountInfo
      var acc = _robot.Account;
      if (acc != null) {
        var info = new DataFetcher.Models.AccountInfo {
          Equity = acc.Equity,
          Balance = acc.Balance,
          Margin = acc.Margin,
          Positions = _robot.Positions.Count
        };
        _persister.Persist(info);
      }
    }
  }
}
