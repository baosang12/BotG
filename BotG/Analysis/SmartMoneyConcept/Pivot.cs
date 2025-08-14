using System;

namespace Analysis.SmartMoneyConcept
{
    public enum PivotType { High, Low }

    public class Pivot
    {
        public PivotType Type { get; set; }
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public int Index { get; set; }
        // Classification
        public bool IsMajor { get; set; }
        public bool IsMinor { get; set; }
    }
}
