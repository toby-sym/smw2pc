using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Cpu;

public delegate void NativeCpuBlockCode(CpuState state, SnesMemoryBus memoryBus);

internal readonly record struct NativeCpuInstruction(
    CpuOperation Operation,
    CpuAddressingMode AddressingMode,
    uint Operand,
    ushort NextProgramCounter);

public sealed class NativeCpuBlock
{
    private readonly NativeCpuBlockCode code;

    internal NativeCpuBlock(DecodedCpuBlock decoded, NativeCpuBlockCode code)
    {
        Decoded = decoded;
        this.code = code;
    }
    internal DecodedCpuBlock Decoded { get; }
    public uint StartAddress => Decoded.StartAddress;
    public int InstructionCount => Decoded.InstructionCount;

    public void Execute(CpuState state, SnesMemoryBus memoryBus)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(memoryBus);
        code(state, memoryBus);
    }
}
