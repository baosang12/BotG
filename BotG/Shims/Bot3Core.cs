using System;
using System.Collections.Generic;
using cAlgo.API;

namespace Bot3.Core
{
    // Minimal shims to satisfy references to the external Bot3.Core package
    public interface IModule
    {
        void Initialize(BotContext ctx);
        void OnBar(IReadOnlyList<Bar> bars);
        void OnTick(Tick tick);
    }

    public class BotContext
    {
        public string SymbolName { get; set; } = string.Empty;
        public TimeFrame TimeFrame { get; set; } = TimeFrame.Minute;
        public object? Services { get; set; }
    }
}
