using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Cpu;

public static class CpuStatusFlags
{
    public const byte Negative = 0x80;
    public const byte Overflow = 0x40;
    public const byte Accumulator8Bit = 0x20;
    public const byte Index8Bit = 0x10;
    public const byte Decimal = 0x08;
    public const byte InterruptDisable = 0x04;
    public const byte Zero = 0x02;
    public const byte Carry = 0x01;
}

public sealed class CpuState
{
    public ushort Accumulator;
    public ushort X;
    public ushort Y;
    public ushort StackPointer;
    public ushort DirectPage;
    public ushort ProgramCounter;
    public byte DataBank;
    public byte ProgramBank;
    public byte Status;
    public bool EmulationMode;
    public bool Halted;
    public bool WaitingForInterrupt;

    public bool IsAccumulator8Bit => EmulationMode || (Status & CpuStatusFlags.Accumulator8Bit) != 0;

    public bool AreIndexes8Bit => EmulationMode || (Status & CpuStatusFlags.Index8Bit) != 0;

    public static CpuState CreateResetState(SnesMemoryBus memoryBus)
    {
        ArgumentNullException.ThrowIfNull(memoryBus);

        var state = new CpuState
        {
            StackPointer = 0x01FF,
            Status = CpuStatusFlags.InterruptDisable
                | CpuStatusFlags.Accumulator8Bit
                | CpuStatusFlags.Index8Bit,
            EmulationMode = true,
        };
        state.ProgramCounter = memoryBus.ReadWordInBank(0x00FFFC);
        return state;
    }

    public void SetNegativeZero8(byte value)
    {
        Status = (byte)(Status & ~(CpuStatusFlags.Negative | CpuStatusFlags.Zero));
        if ((value & 0x80) != 0)
        {
            Status |= CpuStatusFlags.Negative;
        }

        if (value == 0)
        {
            Status |= CpuStatusFlags.Zero;
        }
    }

    public void SetNegativeZero16(ushort value)
    {
        Status = (byte)(Status & ~(CpuStatusFlags.Negative | CpuStatusFlags.Zero));
        if ((value & 0x8000) != 0)
        {
            Status |= CpuStatusFlags.Negative;
        }

        if (value == 0)
        {
            Status |= CpuStatusFlags.Zero;
        }
    }
}
