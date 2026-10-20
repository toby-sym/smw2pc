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
        Store(0x0F, 0x2100);
        var loop = code.Count;
        Emit(0xE6, 0x10, 0xAD, 0x19, 0x42, 0x85, 0x11,
            0x8F, 0x00, 0x00, 0x70, 0x8D, 0x0D, 0x21,
            0x9C, 0x21, 0x21, 0xA5, 0x10, 0x8D, 0x22, 0x21,
            0xA9, 0, 0x8D, 0x22, 0x21);
        Emit(0x80, unchecked((byte)(loop - code.Count - 2)));
        code.ToArray().CopyTo(bytes, 0);
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
