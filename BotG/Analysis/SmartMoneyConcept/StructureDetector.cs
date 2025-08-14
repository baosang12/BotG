using System;
using System.Collections.Generic;
using System.Linq;

namespace Analysis.SmartMoneyConcept
{
    public enum StructureType { BOS, CHoCH }

    public class StructureEvent
    {
        public StructureType Type { get; set; }
        public Pivot Pivot { get; set; }
    }

    /// <summary>
    /// Phát hiện Break of Structure (BOS) và Change of Character (CHoCH) dựa trên pivots.
    /// </summary>
    public class StructureDetector
    {
        /// <summary>
        /// Detect BOS: khi pivot hiện tại vượt pivot trước đó cùng type (High hay Low).
        /// Detect CHoCH: khi có BOS ngược hướng.
        /// </summary>
        public List<StructureEvent> DetectStructure(List<Pivot> pivots)
        {
            var events = new List<StructureEvent>();
            Pivot lastHighPivot = null;
            Pivot lastLowPivot = null;
            StructureType? lastBOS = null;

            foreach (var p in pivots.OrderBy(p => p.Index))
            {
                if (p.Type == PivotType.High)
                {
                    if (lastHighPivot != null && p.Price > lastHighPivot.Price)
                    {
                        events.Add(new StructureEvent { Type = StructureType.BOS, Pivot = p });
                        lastBOS = StructureType.BOS;
                    }
                    lastHighPivot = p;
                    // CHoCH: low pivot after bullish BOS
                    if (lastBOS == StructureType.BOS && lastLowPivot != null && lastLowPivot.Price < p.Price)
                    {
                        events.Add(new StructureEvent { Type = StructureType.CHoCH, Pivot = lastLowPivot });
                    }
                }
                else if (p.Type == PivotType.Low)
                {
                    if (lastLowPivot != null && p.Price < lastLowPivot.Price)
                    {
                        events.Add(new StructureEvent { Type = StructureType.BOS, Pivot = p });
                        lastBOS = StructureType.BOS;
                    }
                    lastLowPivot = p;
                    // CHoCH: high pivot after bearish BOS
                    if (lastBOS == StructureType.BOS && lastHighPivot != null && lastHighPivot.Price > p.Price)
                    {
                        events.Add(new StructureEvent { Type = StructureType.CHoCH, Pivot = lastHighPivot });
                    }
                }
            }
            return events;
        }
    }
}
