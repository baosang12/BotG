using System;

namespace BotG.Harness.Models
{
    public record Bar(
        DateTime Timestamp,
        double Open,
        double High,
        double Low,
        double Close,
        double Volume = 0
    )
    {
        public double Range => High - Low;
        public double BodySize => Math.Abs(Close - Open);
        public bool IsBullish => Close > Open;
    }
}