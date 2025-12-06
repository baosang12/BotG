using System;
using System.Collections.Generic;

namespace Strategies.Confirmation
{
    public class ConfirmationResult
    {
        public double TrendAlignment { get; set; }
        public double KeyLevelConfirmation { get; set; }
        public double VolumeConfirmation { get; set; }
        public double MomentumConfirmation { get; set; }
        public double OverallScore { get; set; }
        public double Threshold { get; set; } = 0.7;
        public Dictionary<string, string> Details { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsConfirmed => OverallScore >= Threshold;
    }

    internal enum TrendDirection
    {
        Range = 0,
        Up = 1,
        Down = -1
    }
}
