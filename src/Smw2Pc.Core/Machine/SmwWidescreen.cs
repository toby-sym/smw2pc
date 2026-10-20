using System.Buffers.Binary;

namespace Smw2Pc.Core.Machine;

/// <summary>Presentation and visibility adaptations for the stock SMW level layout.</summary>
public sealed class SmwWidescreen
{
    private readonly SnesMemoryBus bus;
    private readonly byte[]? extendedData;
    private readonly Dictionary<uint, (uint Original, uint Extended)> graphicsOperands = [];
    private bool enabled;

    internal SmwWidescreen(SnesMemoryBus bus)
    {
        this.bus = bus;
        var data = bus.Rom.Data.Span;
        if (data.Length != 0x80000 || bus.Rom.Header.Title != "SUPER MARIOWORLD") return;
        // Validate each region's actual data layout before adapting it. Code bytes and
        // the cartridge hash remain original, including in the strict compiled player.
        ReadOnlySpan<byte> despawn = [0x30, 0xC0, 0xA0, 0xC0, 0xA0, 0xF0, 0x60, 0x90,
            1, 0xFF, 1, 0xFF, 1, 0xFF, 1, 0xFF];
        var first = Matches(data, 0x01AC11, despawn) ? 0x01AC11
            : Matches(data, 0x01AC1D, despawn) ? 0x01AC1D : 0;
        if (first == 0 || !Matches(data, 0x02A7F6, [0xD0, 0, 0x20, 0xFF, 0, 1])
            || !Matches(data, 0x02D007, [0x30, 0xC0, 0xA0, 0xC0, 0xA0, 0x70, 0x60, 0xB0,
                1, 0xFF, 1, 0xFF, 1, 0xFF, 1, 0xFF])
            || !Matches(data, 0x03B83F, [0x30, 0xC0, 0xA0, 0x80, 0xA0, 0x40, 0x60, 0xB0,
                1, 0xFF, 1, 0xFF, 1, 0, 1, 0xFF])) return;
        ReadOnlySpan<byte> drawInfo = [0x9E, 0x6C, 0x18, 0x9E, 0xA0, 0x15, 0xB5, 0xE4,
            0xC5, 0x1A, 0xBD, 0xE0, 0x14, 0xE5, 0x1B, 0xF0, 3, 0xFE, 0xA0, 0x15,
            0xBD, 0xE0, 0x14, 0xEB, 0xB5, 0xE4, 0xC2, 0x20, 0x38, 0xE5, 0x1A, 0x18,
            0x69, 0x40, 0, 0xC9, 0x80, 1, 0xE2, 0x20, 0x2A, 0x29, 1, 0x9D, 0xC4, 0x15];
        foreach (var start in new[] { first == 0x01AC11 ? 0x01A365 : 0x01A371, 0x02D378, 0x03B760 })
        {
            if (!Matches(data, start, drawInfo)) return;
            graphicsOperands[(uint)(start + 32)] = (0x40, 0x40 + ExtraVisibility);
            graphicsOperands[(uint)(start + 35)] = (0x180, 0x180 + ExtraVisibility * 2);
        }
        IsSupported = true;
        extendedData = data.ToArray();
        ExtendBounds(first, 8);
        ExtendBounds(0x02D007, 8);
        ExtendBounds(0x03B83F, 8);
        // Leave the stationary offset alone. The two moving boundaries are widened.
        SetOffset(0x02A7F6, 3, 0, -48 - ExtraVisibility);
        SetOffset(0x02A7F6, 3, 2, 288 + ExtraVisibility);
    }

    private const int ExtraVisibility = VideoViewport.WidescreenWidth - SnesVideoRenderer.Width;
    public bool IsSupported { get; }
    public bool Enabled
    {
        get => enabled;
        set { if (enabled == value) return; enabled = value; PrepareFrame(); }
    }

    // Vertical levels, bosses, overworld maps and scripted scenes retain their
    // authored framing. Horizontal interactive layer 2 needs a separate map layout.
    public bool CanExtendLevel => enabled && IsSupported
        && bus.Hardware.ReadWorkRam(0x100) == 0x14
        && (bus.Hardware.ReadWorkRam(0x5B) & 0xC1) == 0
        && bus.Hardware.ReadWorkRam(0x1931) < 0x10
        && bus.Hardware.ReadWorkRam(0x5D) > 1;

    internal void PrepareFrame() => bus.SetPresentationRomData(CanExtendLevel ? extendedData! : bus.Rom.Data);

    // The compiler emits these six adaptations directly at validated graphics
    // instructions. No instruction decoder or runtime compilation is introduced.
    internal bool TryGetGraphicsOperand(uint address, uint original, out uint extended)
    {
        extended = original;
        if (!IsSupported || !graphicsOperands.TryGetValue(address & 0x7FFFFF, out var operand)
            || operand.Original != original) return false;
        extended = operand.Extended;
        return true;
    }

    public uint GraphicsOperand(uint original, uint extended) => CanExtendLevel ? extended : original;

    internal int GetOriginX(int width, float interpolationProgress)
    {
        const int sceneScanline = 33; // Below the status bar's IRQ split.
        var hardware = bus.Hardware;
        var offset = hardware.GetBackgroundOffsetsForScanline(sceneScanline).Span[0];
        var (delta, _) = hardware.GetBackgroundScrollDeltaForScanline(sceneScanline, 0);
        if (Math.Abs(delta) > 16) delta = 0;
        // Round in the PPU ring's coordinates, just like the original renderer.
        // Rounding a full world coordinate can differ by one pixel at a ring wrap.
        var camera = GetLevelXOffset(offset) - offset
            + (int)MathF.Round(offset - delta * (1f - interpolationProgress), MidpointRounding.AwayFromZero);
        var levelWidth = bus.Hardware.ReadWorkRam(0x5D) * 256;
        return Math.Clamp(camera - (width - 256) / 2, 0, Math.Max(0, levelWidth - width)) - camera;
    }

    internal int GetLevelXOffset(int ppuOffset)
    {
        var camera = ReadWord(0x1A);
        // The PPU ring has a 512px period. Preserve the full world coordinate and
        // the scanline's scroll, including a possible one-frame camera difference.
        var difference = ((ppuOffset - camera + 256) & 511) - 256;
        return camera + difference;
    }

    internal int ReadForegroundEntry(int worldX, int worldY)
    {
        if (worldX < 0 || worldX >= bus.Hardware.ReadWorkRam(0x5D) * 256 || (uint)worldY >= 432)
            return -1;
        var index = (worldX >> 8) * 0x1B0 + (worldY >> 4) * 16 + ((worldX >> 4) & 15);
        var ram = bus.Hardware.WorkRam.Span;
        var tile = ram[0xC800 + index] | (ram[0x1C800 + index] << 8);
        if (tile >= 512) return -1;
        var pointer = BinaryPrimitives.ReadUInt16LittleEndian(ram[(0xFBE + tile * 2)..]);
        if (pointer < 0x8000 || pointer > 0xFFF8) return -1;
        var quadrant = ((worldY & 8) != 0 ? 2 : 0) + ((worldX & 8) != 0 ? 4 : 0);
        return BinaryPrimitives.ReadUInt16LittleEndian(bus.Rom.Data.Span[(Offset(0x0D0000 | pointer) + quadrant)..]);
    }

    private ushort ReadWord(int address) => BinaryPrimitives.ReadUInt16LittleEndian(bus.Hardware.WorkRam.Span[address..]);
    private static int Offset(int address) => ((address >> 16) * 0x8000) + (address & 0x7FFF);
    private static bool Matches(ReadOnlySpan<byte> data, int address, ReadOnlySpan<byte> expected)
        => data.Slice(Offset(address), expected.Length).SequenceEqual(expected);

    private void ExtendBounds(int address, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var offset = (short)(extendedData![Offset(address) + i] | (extendedData[Offset(address) + count + i] << 8));
            SetOffset(address, count, i, offset + ((i & 1) == 0 ? ExtraVisibility : -ExtraVisibility));
        }
    }

    private void SetOffset(int address, int count, int index, int value)
    {
        extendedData![Offset(address) + index] = (byte)value;
        extendedData[Offset(address) + count + index] = (byte)(value >> 8);
    }
}
