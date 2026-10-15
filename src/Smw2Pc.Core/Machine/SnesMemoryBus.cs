using Smw2Pc.Core.Rom;

namespace Smw2Pc.Core.Machine;

public sealed class SnesMemoryBus
{
    private const int WorkRamSize = 0x20000;
    private const int LoRomBankSize = 0x8000;
    private const int LoRomBankMask = LoRomBankSize - 1;
    private const int AddressMask = 0xFFFFFF;
    private const int BankMask = 0xFF0000;
    private const int OffsetMask = 0xFFFF;

    private readonly ReadOnlyMemory<byte> romData;
    private readonly byte[] workRam = new byte[WorkRamSize];

    public SnesMemoryBus(SnesRom rom)
    {
        ArgumentNullException.ThrowIfNull(rom);

        if (rom.Header.MapMode is not 0x20 and not 0x30)
        {
            throw new NotSupportedException(
                $"The current memory bus supports LoROM map modes 0x20 and 0x30; this ROM uses 0x{rom.Header.MapMode:X2}.");
        }

        romData = rom.Data;
    }

    public byte ReadByte(uint address)
    {
        address &= AddressMask;
        var bank = (int)(address >> 16);
        var offset = (int)(address & OffsetMask);

        if (bank is 0x7E or 0x7F)
        {
            return workRam[((bank - 0x7E) << 16) | offset];
        }

        if (IsLowWorkRamMirror(bank, offset))
        {
            return workRam[offset & 0x1FFF];
        }

        if (TryGetRomOffset(address, out var romOffset))
        {
            return romData.Span[romOffset];
        }

        // PPU, APU, and CPU registers will be mapped here in later runtime work.
        return 0xFF;
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
        var bank = (int)(address >> 16);
        var offset = (int)(address & OffsetMask);

        if (bank is 0x7E or 0x7F)
        {
            workRam[((bank - 0x7E) << 16) | offset] = value;
            return;
        }

        if (IsLowWorkRamMirror(bank, offset))
        {
            workRam[offset & 0x1FFF] = value;
        }
    }

    public bool IsCartridgeRomAddress(uint address)
    {
        return TryGetRomOffset(address & AddressMask, out _);
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
        var isSystemBank = bank <= 0x3F || bank is >= 0x80 and <= 0xBF;
        return isSystemBank && offset < 0x2000;
    }
}
