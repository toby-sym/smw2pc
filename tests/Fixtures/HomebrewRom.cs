using System.Buffers.Binary;
using System.Text;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.TestFixtures;

// Original test software, authored for this repository. Includes no commercial assets or code.
internal static class HomebrewRom
{
    public static SnesRom Create(bool pal = false, byte saveExponent = 1)
    {
        var bytes = new byte[0x8000];
        var code = new List<byte> { 0x78 }; // SEI; reset starts with 8-bit registers.
        void Emit(params byte[] values) => code.AddRange(values);
        void Store(byte value, ushort address) => Emit(0xA9, value, 0x8D, (byte)address, (byte)(address >> 8));
        void Wait(byte value, ushort address) => Emit(0xAD, (byte)address, (byte)(address >> 8), 0xC9, value, 0xD0, 0xF9);
        void Echo() => Emit(0xCD, 0x40, 0x21, 0xD0, 0xFB);
        void Dma(byte mode, byte port, ushort source, ushort length)
        {
            Store(mode, 0x4300); Store(port, 0x4301);
            Store((byte)source, 0x4302); Store((byte)(source >> 8), 0x4303); Store(0, 0x4304);
            Store((byte)length, 0x4305); Store((byte)(length >> 8), 0x4306); Store(1, 0x420B);
        }

        // Upload an original noise instrument through the SPC700 boot protocol.
        var audio = new List<byte>();
        void Dsp(byte register, byte value) => audio.AddRange([0x8F, register, 0xF2, 0x8F, value, 0xF3]);
        Dsp(0x6C, 0x1F); Dsp(0x0C, 0x7F); Dsp(0x1C, 0x7F);
        Dsp(0x00, 0x60); Dsp(0x01, 0x60); Dsp(0x05, 0); Dsp(0x07, 0x7F);
        Dsp(0x3D, 1); Dsp(0x4C, 1);
        audio.AddRange([0x2F, 0xFE]);
        Wait(0xAA, 0x2140); Wait(0xBB, 0x2141);
        Store(0, 0x2142); Store(2, 0x2143); Store(1, 0x2141); Store(0xCC, 0x2140); Echo();
        for (var i = 0; i < audio.Count; i++)
        {
            Store(audio[i], 0x2141); Store((byte)i, 0x2140); Echo();
        }
        Store(0, 0x2141); Store(0, 0x2142); Store(2, 0x2143);
        Store((byte)(audio.Count + 1), 0x2140); Echo();
        Store(0x80, 0x2115); // DMA original tiles and a repeating terrain/bush tilemap.
        Store(0, 0x2116); Store(0x10, 0x2117); Dma(1, 0x18, 0xC000, 64);
        Store(0, 0x2116); Store(0, 0x2117); Dma(1, 0x18, 0xC100, 2048);
        Store(0, 0x2121); Dma(0, 0x22, 0xCC00, 32);
        Store(128, 0x2121); Dma(0, 0x22, 0xCC20, 32);
        Store(0, 0x2102); Store(0, 0x2103); Dma(0, 4, 0xC900, 544);
        Store(1, 0x2105); Store(0, 0x2107); Store(1, 0x210B); Store(0x11, 0x212C);
        Store(0x0F, 0x2100);
        var loop = code.Count;
        Emit(0xE6, 0x10, 0xAD, 0x19, 0x42, 0x85, 0x11,
            0x8F, 0x00, 0x00, 0x70, 0x8D, 0x0D, 0x21,
            0xA9, 0, 0x8D, 0x0D, 0x21,
            0x9C, 0x21, 0x21, 0xA5, 0x10, 0x8D, 0x22, 0x21,
            0xA9, 0, 0x8D, 0x22, 0x21);
        Store(0, 0x2102); Store(0, 0x2103);
        Emit(0xA5, 0x10, 0x8D, 0x04, 0x21);
        Store(96, 0x2104); Store(1, 0x2104); Store(0x31, 0x2104);
        Emit(0x80, unchecked((byte)(loop - code.Count - 2)));
        code.ToArray().CopyTo(bytes, 0);
        for (var tile = 0; tile < 2; tile++)
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 8; x++)
                {
                    var color = tile == 0 ? (y < 3 ? 1 : 3)
                        : (x - 3.5) * (x - 3.5) + (y - 3.5) * (y - 3.5) < 15 ? 1 + (x + y) % 2 : 0;
                    for (var plane = 0; plane < 4; plane++)
                        if ((color & (1 << plane)) != 0)
                            bytes[0x4000 + tile * 32 + y * 2 + plane % 2 + plane / 2 * 16] |= (byte)(0x80 >> x);
                }
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++) bytes[0x4100 + (y * 32 + x) * 2] = (byte)((x + y) % 2);
        for (var sprite = 0; sprite < 128; sprite++) bytes[0x4900 + sprite * 4 + 1] = 240;
        ushort[] colors = [0x7E80, 0x03E0, 0x0200, 0x0155];
        ushort[] spriteColors = [0, 0x001F, 0x02BF, 0x7FFF];
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x4C00 + i * 2), colors[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x4C20 + i * 2), spriteColors[i]);
        }
        Encoding.ASCII.GetBytes("SMW2PC HOMEBREW TEST  ").CopyTo(bytes, 0x7FC0);
        bytes[0x7FD5] = 0x20;
        bytes[0x7FD8] = saveExponent;
        bytes[0x7FD9] = pal ? (byte)2 : (byte)1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x7FDC), 0xEDCB);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x7FDE), 0x1234);
        foreach (var vector in new[] { 0x7FFA, 0x7FFC, 0x7FFE, 0x7FE4, 0x7FE6, 0x7FE8, 0x7FEA, 0x7FEE })
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(vector), 0x8000);
        return SnesRom.LoadBytes(bytes, "homebrew.sfc");
    }
}
