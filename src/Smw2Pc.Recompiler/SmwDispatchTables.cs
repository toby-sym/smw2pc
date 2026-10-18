using Smw2Pc.Core.Rom;

namespace Smw2Pc.Recompiler;

// These SMW helpers consume their JSL return address and dispatch through an
// inline pointer table. Match their complete code before applying this rule:
// the bytes following such a call are data, not its normal continuation.
internal sealed class SmwDispatchTables
{
    private static readonly byte[] WordDispatcher = Convert.FromHexString(
        "84037A8400C23029FF000AA8688501C8B7008500E230A403DC0000");
    private static readonly byte[] LongDispatcher = Convert.FromHexString(
        "84057A8402C23029FF0085030A6503A8688503C8B7028500C8B7028501E230A405DC0000");
    private readonly Dictionary<uint, int> widths = [];

    public SmwDispatchTables(SnesRom rom)
    {
        var data = rom.Data.Span;
        for (var offset = 0; offset < data.Length; offset++)
        {
            var remaining = data[offset..];
            var width = remaining.StartsWith(WordDispatcher) ? 2 : remaining.StartsWith(LongDispatcher) ? 3 : 0;
            if (width == 0) continue;
            var address = (uint)((offset / 0x8000 << 16) | 0x8000 | (offset & 0x7FFF));
            widths.Add(address, width);
        }
    }

    public int PointerWidth(uint canonicalTarget) => widths.GetValueOrDefault(canonicalTarget);
    public int RecognizedHelpers => widths.Count;
}
