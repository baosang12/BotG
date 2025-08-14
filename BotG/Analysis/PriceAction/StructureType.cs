using System;

namespace Analysis.PriceAction
{
    /// <summary>
    /// StructureType: Describes market structure events (BOS, CHoCH, None, etc.)
    /// </summary>
    public enum StructureType
    {
        None = 0,
        BullishBreakOfStructure,
        BearishBreakOfStructure,
        BullishChangeOfCharacter,
        BearishChangeOfCharacter,
        Other
    }
}
