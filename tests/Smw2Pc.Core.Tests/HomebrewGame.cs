using System.Buffers.Binary;
using System.Text;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Core.Tests;

// Entirely authored here. No commercial ROM, graphics, music, or disassembly.
internal static class HomebrewGame
{
    public static SnesRom Create(bool pal = false, byte saveExponent = 1)
        => Smw2Pc.TestFixtures.HomebrewRom.Create(pal, saveExponent);

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
