using Smw2Pc.Core.Cpu;
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
    private static readonly byte[] Bank0dPointerTablePrefix = Convert.FromHexString(
        "7BA50D7BA50D7BA50D7BA50D7BA50D7BA50D7BA50D4DA60D");
    private readonly Dictionary<uint, int> widths = [];

    public sealed record StaticPointerTable(uint Address, int Width, int EntryCount);
    public IReadOnlyList<StaticPointerTable> StaticPointerTables { get; private set; } = [];

    public SmwDispatchTables(SnesRom rom)
    {
        var data = rom.Data.Span;
        for (var offset = 0; offset < data.Length; offset++)
        {
            var remaining = data[offset..];
            var width = remaining.StartsWith(WordDispatcher) ? 2 : remaining.StartsWith(LongDispatcher) ? 3 : 0;
            var address = (uint)((offset / 0x8000 << 16) | 0x8000 | (offset & 0x7FFF));
            if (width != 0) widths.Add(address, width);

            const int pointerTableEntries = 240;
            const int pointerWidth = 3;
            var pointerTableBytes = pointerTableEntries * pointerWidth;
            if (!remaining.StartsWith(Bank0dPointerTablePrefix)
                || remaining.Length < pointerTableBytes + pointerWidth
                || (address & 0xFF0000) != 0x0D0000
                || (remaining[pointerTableBytes], remaining[pointerTableBytes + 1], remaining[pointerTableBytes + 2])
                    is not (0xE2, 0x30, 0x20))
                continue;

            var validTable = true;
            for (var entry = 0; entry < pointerTableEntries; entry++)
            {
                var entryOffset = entry * pointerWidth;
                var targetBank = remaining[entryOffset + 2];
                var targetAddress = remaining[entryOffset] | (remaining[entryOffset + 1] << 8);
                if (targetBank != 0x0D || targetAddress < 0x8000)
                {
                    validTable = false;
                    break;
                }
            }

            if (validTable)
                StaticPointerTables = [.. StaticPointerTables, new StaticPointerTable(address, pointerWidth, pointerTableEntries)];
        }
    }

    public int PointerWidth(uint canonicalTarget) => widths.GetValueOrDefault(canonicalTarget);
    public int RecognizedHelpers => widths.Count;

    public bool TryGetTailDispatchTable(DecodedCpuBlock block, uint canonicalTarget, out uint table, out int width)
    {
        table = 0;
        width = 0;
        if (block.Instructions.Count < 3) return false;

        var transfer = block.Instructions[^1];
        var relativePointer = block.Instructions[^2];
        var bankPush = block.Instructions[^3];
        width = PointerWidth(canonicalTarget);
        if (transfer.Operation != CpuOperation.JumpLong || width == 0
            || relativePointer.Operation != CpuOperation.PushEffectiveRelative
            || bankPush.Operation != CpuOperation.PushProgramBank)
        {
            width = 0;
            return false;
        }

        var bank = block.StartAddress & 0xFF0000;
        var pushedReturnAddress = unchecked((ushort)(relativePointer.NextProgramCounter + (short)relativePointer.Operand));
        // SMW's tail-call form pushes tableStart - 1, then jumps to ExecutePtr.
        table = bank | unchecked((ushort)(pushedReturnAddress + 1));
        var afterTransfer = bank | transfer.NextProgramCounter;
        if (table == afterTransfer) return true;

        table = 0;
        width = 0;
        return false;
    }
}
