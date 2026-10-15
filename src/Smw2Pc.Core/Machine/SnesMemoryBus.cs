using Smw2Pc.Core.Rom;

namespace Smw2Pc.Core.Machine;

public sealed class SnesMemoryBus
{
    private const int LoRomBankSize = 0x8000;
    private const int LoRomBankMask = LoRomBankSize - 1;
    private const int AddressMask = 0xFFFFFF;
    private const int BankMask = 0xFF0000;
    private const int OffsetMask = 0xFFFF;

    private readonly ReadOnlyMemory<byte> romData;
    private byte openBus = 0xFF;

    public SnesMemoryBus(SnesRom rom)
    {
        ArgumentNullException.ThrowIfNull(rom);

        if (rom.Header.MapMode is not 0x20 and not 0x30)
        {
            throw new NotSupportedException(
                $"The current memory bus supports LoROM map modes 0x20 and 0x30; this ROM uses 0x{rom.Header.MapMode:X2}.");
        }

        romData = rom.Data;
        Hardware = new SnesHardware();
    }

    public SnesHardware Hardware { get; }

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

        if (IsSystemBank(bank) && Hardware.TryReadRegister((ushort)offset, openBus, out var registerValue))
        {
            return ReadMappedByte(registerValue);
        }

        if (TryGetRomOffset(address, out var romOffset))
        {
            return ReadMappedByte(romData.Span[romOffset]);
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

        if (IsSystemBank(bank))
        {
            Hardware.TryWriteRegister(this, (ushort)offset, value);
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
