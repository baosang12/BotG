using System;
namespace DataFetcher.Models
{
    public class Bar
    {
        public DateTime OpenTime { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
        public TimeFrame Tf { get; set; }
    }
    public enum TimeFrame
    {
        M1, M5, M15, M30, H1, H4, D1, W1, MN1
    }
}
