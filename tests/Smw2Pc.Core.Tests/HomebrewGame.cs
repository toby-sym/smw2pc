using System.Buffers.Binary;
using System.Text;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Core.Tests;

// Entirely authored here. No commercial ROM, graphics, music, or disassembly.
internal static class HomebrewGame
{
    public static SnesRom Create(bool pal = false, byte saveExponent = 1)
    {
        var bytes = new byte[0x8000];
        // Counter + controller input drive WRAM, SRAM, scroll and backdrop color.
        byte[] program = [
            0x78, 0xA9, 0x0F, 0x8D, 0x00, 0x21,
            0xE6, 0x10, 0xAD, 0x19, 0x42, 0x85, 0x11,
            0x8F, 0x00, 0x00, 0x70, 0x8D, 0x0D, 0x21,
            0x64, 0x20, 0x9C, 0x21, 0x21, 0xA5, 0x10,
            0x8D, 0x22, 0x21, 0xA9, 0x00, 0x8D, 0x22, 0x21,
            0x80, 0xE1];
        program.CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("SMW2PC HOMEBREW TEST  ").CopyTo(bytes, 0x7FC0);
        bytes[0x7FD5] = 0x20;
        bytes[0x7FD8] = saveExponent;
        bytes[0x7FD9] = pal ? (byte)2 : (byte)1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x7FDC), 0xEDCB);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x7FDE), 0x1234);
        foreach (var vector in new[] { 0x7FFA, 0x7FFC, 0x7FFE })
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(vector), 0x8000);
        return SnesRom.LoadBytes(bytes, "homebrew.sfc");
    }

    public static void ConfigureSound(SnesHardware hardware)
    {
        // A looping original BRR block exercises interpolation, envelope and echo history.
        var apu = hardware.Apu;
        apu.WriteSpcMemory(0x2000, 0x00); apu.WriteSpcMemory(0x2001, 0x30);
        apu.WriteSpcMemory(0x2002, 0x00); apu.WriteSpcMemory(0x2003, 0x30);
        apu.WriteSpcMemory(0x3000, 0x93);
        for (var i = 1; i <= 8; i++) apu.WriteSpcMemory((ushort)(0x3000 + i), (byte)(i * 13));
        void Dsp(byte register, byte value)
        { apu.WriteSpcMemory(0xF2, register); apu.WriteSpcMemory(0xF3, value); }
        Dsp(0x6C, 0); Dsp(0x0C, 0x7F); Dsp(0x1C, 0x7F);
        Dsp(0x00, 0x60); Dsp(0x01, 0x60); Dsp(0x02, 0x00); Dsp(0x03, 0x10);
        Dsp(0x05, 0); Dsp(0x07, 0x7F); Dsp(0x5D, 0x20); Dsp(0x4C, 1);
    }
}
