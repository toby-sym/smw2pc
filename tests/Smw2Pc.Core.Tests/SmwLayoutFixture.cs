using System.Buffers.Binary;
using System.Text;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Core.Tests;

// Original test data with the small layout fingerprints used by the adapter.
// Contains no commercial graphics, music, level data, or executable game program.
internal static class SmwLayoutFixture
{
    public static SnesRom Create()
    {
        var bytes = new byte[0x80000];
        HomebrewGame.Create().Data.Span.CopyTo(bytes);
        Encoding.ASCII.GetBytes("SUPER MARIOWORLD".PadRight(21)).CopyTo(bytes, 0x7FC0);
        Put(0x01AC11, "30C0A0C0A0F0609001FF01FF01FF01FF");
        Put(0x02D007, "30C0A0C0A07060B001FF01FF01FF01FF");
        Put(0x03B83F, "30C0A080A04060B001FF01FF010001FF");
        Put(0x02A7F6, "D00020FF0001");
        Put(0x058776, "B08AE084F08A308B");
        foreach (var address in new[] { 0x01A365, 0x02D378, 0x03B760 })
            Put(address, "9E6C189EA015B5E4C51ABDE014E51BF003FEA015BDE014EBB5E4C22038E51A18694000C98001E2202A29019DC415");
        // Give every pipe appearance and quadrant distinct, original tile values.
        var pointers = new[] { 0x8AB0, 0x84E0, 0x8AF0, 0x8B30 };
        for (var screen = 0; screen < 4; screen++)
            for (var tile = 0; tile < 8; tile++)
                for (var quadrant = 0; quadrant < 4; quadrant++)
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(Offset(0x0D0000 | pointers[screen]) + tile * 8 + quadrant * 2),
                        Entry(screen, tile, quadrant));
        return SnesRom.LoadBytes(bytes, "original-layout-test.sfc");

        void Put(int address, string hex) => Convert.FromHexString(hex).CopyTo(bytes, Offset(address));
    }

    public static ushort Entry(int screen, int tile, int quadrant)
        => (ushort)(((screen + 3) << 10) | (tile * 4 + quadrant));

    private static int Offset(int address) => ((address >> 16) * 0x8000) + (address & 0x7FFF);
}
