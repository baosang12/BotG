using System;
namespace DataFetcher.Models
{
    public class Tick
    {
        public DateTime Timestamp { get; set; }
        public double Bid { get; set; }
        public double Ask { get; set; }
        public long Volume { get; set; }
    }
}
