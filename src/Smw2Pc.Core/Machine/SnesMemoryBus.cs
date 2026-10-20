using Smw2Pc.Core.Rom;

namespace Smw2Pc.Core.Machine;

public sealed partial class SnesMemoryBus
{
    private const int LoRomBankSize = 0x8000;
    private const int LoRomBankMask = LoRomBankSize - 1;
    private const int AddressMask = 0xFFFFFF;
    private const int BankMask = 0xFF0000;
    private const int OffsetMask = 0xFFFF;

    private readonly ReadOnlyMemory<byte> romData;
    private ReadOnlyMemory<byte> presentationRomData;
    private byte openBus = 0xFF;
    private readonly byte[] saveRam;

    public SnesMemoryBus(SnesRom rom)
    {
        ArgumentNullException.ThrowIfNull(rom);

        if (rom.Header.MapMode is not 0x20 and not 0x30)
        {
            throw new NotSupportedException(
                $"The current memory bus supports LoROM map modes 0x20 and 0x30; this ROM uses 0x{rom.Header.MapMode:X2}.");
        }

        Rom = rom;
        romData = rom.Data;
        presentationRomData = romData;
        Hardware = new SnesHardware(rom.Header.IsPalRegion);
        saveRam = new byte[rom.Header.SaveRamSize];
        Array.Fill(saveRam, (byte)0xFF);
        Widescreen = new SmwWidescreen(this);
        Hardware.Widescreen = Widescreen;
    }

    public SmwWidescreen Widescreen { get; }

    internal void SetPresentationRomData(ReadOnlyMemory<byte> data) => presentationRomData = data;

    public SnesRom Rom { get; }

    public SnesHardware Hardware { get; }

    public ReadOnlyMemory<byte> SaveRam => saveRam;

    public long SaveRamRevision { get; private set; }

    public void LoadSaveRam(ReadOnlySpan<byte> data)
    {
        if (data.Length != saveRam.Length)
            throw new InvalidDataException("The save file size does not match this cartridge.");
        data.CopyTo(saveRam);
        SaveRamRevision++;
    }

    public byte ReadByte(uint address)
    {
        address &= AddressMask;
        var bank = (int)(address >> 16);
        var offset = (int)(address & OffsetMask);

        if (bank is 0x7E or 0x7F)
        {
            return ReadMappedByte(Hardware.ReadWorkRam(((bank - 0x7E) << 16) | offset));
        }

        if (IsLowWorkRamMirror(bank, offset))
        {
            return ReadMappedByte(Hardware.ReadWorkRam(offset & 0x1FFF));
        }

        if (TryGetSaveRamOffset(bank, offset, out var saveOffset))
            return ReadMappedByte(saveRam[saveOffset]);

        if (IsSystemBank(bank) && Hardware.TryReadRegister((ushort)offset, openBus, out var registerValue))
        {
            return ReadMappedByte(registerValue);
        }

        if (TryGetRomOffset(address, out var romOffset))
        {
            return ReadMappedByte(presentationRomData.Span[romOffset]);
        }

        return openBus;
    }

    public ushort ReadWordInBank(uint address)
    {
        address &= AddressMask;
        var bank = address & BankMask;
        var offset = address & OffsetMask;
        var low = ReadByte(address);
        var highAddress = bank | ((offset + 1) & OffsetMask);
        var high = ReadByte(highAddress);
        return (ushort)(low | (high << 8));
    }

    public void WriteByte(uint address, byte value)
    {
        address &= AddressMask;
        openBus = value;
        var bank = (int)(address >> 16);
        var offset = (int)(address & OffsetMask);

        if (bank is 0x7E or 0x7F)
        {
            Hardware.WriteWorkRam(((bank - 0x7E) << 16) | offset, value);
            return;
        }

        if (IsLowWorkRamMirror(bank, offset))
        {
            Hardware.WriteWorkRam(offset & 0x1FFF, value);
            return;
        }

        if (TryGetSaveRamOffset(bank, offset, out var saveOffset))
        {
            if (saveRam[saveOffset] != value)
            {
                saveRam[saveOffset] = value;
                SaveRamRevision++;
            }
            return;
        }

        if (IsSystemBank(bank))
        {
            Hardware.TryWriteRegister(this, (ushort)offset, value);
        }
    }

    public bool IsCartridgeRomAddress(uint address)
    {
        return TryGetRomOffset(address & AddressMask, out _);
    }

    // Compiler and code guards must inspect bytes without reading I/O or changing open bus.
    public byte PeekCodeByte(uint address)
    {
        address &= AddressMask;
        var bank = (int)(address >> 16);
        var offset = (int)(address & OffsetMask);
        if (bank is 0x7E or 0x7F)
            return Hardware.ReadWorkRam(((bank - 0x7E) << 16) | offset);
        if (IsLowWorkRamMirror(bank, offset))
            return Hardware.ReadWorkRam(offset & 0x1FFF);
        if (TryGetRomOffset(address, out var romOffset))
            return romData.Span[romOffset];
        throw new NotSupportedException($"Executable memory at ${address:X6} is not ROM or WRAM.");
    }

    public uint CanonicalCodeAddress(uint address)
    {
        if (TryGetRomOffset(address & AddressMask, out var offset))
            return (uint)((offset / LoRomBankSize << 16) | 0x8000 | (offset & LoRomBankMask));
        var bank = (int)((address >> 16) & 0xFF);
        var low = (int)(address & OffsetMask);
        return IsLowWorkRamMirror(bank, low) ? 0x7E0000u | (uint)low : address & AddressMask;
    }

    private bool TryGetSaveRamOffset(int bank, int offset, out int saveOffset)
    {
        // Standard LoROM SRAM: $70-$7D/$F0-$FF, lower 32 KiB, mirrored to cartridge size.
        saveOffset = 0;
        if (saveRam.Length == 0 || offset >= 0x8000
            || !(bank is >= 0x70 and <= 0x7D or >= 0xF0 and <= 0xFF)) return false;
        saveOffset = (((bank & 0x0F) << 15) | offset) % saveRam.Length;
        return true;
    }

    private bool TryGetRomOffset(uint address, out int romOffset)
    {
        var bank = (int)(address >> 16);
        var offset = (int)(address & OffsetMask);

        if (bank is 0x7E or 0x7F || offset < 0x8000)
        {
            romOffset = 0;
            return false;
        }

        var mappedBank = bank & 0x7F;
        var mappedOffset = (mappedBank * LoRomBankSize) | (offset & LoRomBankMask);
        romOffset = mappedOffset % romData.Length;
        return true;
    }

    private static bool IsLowWorkRamMirror(int bank, int offset)
    {
        return IsSystemBank(bank) && offset < 0x2000;
    }

    private byte ReadMappedByte(byte value)
    {
        openBus = value;
        return value;
    }

    private static bool IsSystemBank(int bank)
    {
        return bank <= 0x3F || bank is >= 0x80 and <= 0xBF;
    }
}
