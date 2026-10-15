using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Cpu;

public delegate void NativeCpuBlockCode(CpuState state, SnesMemoryBus memoryBus);

public sealed class NativeCpuBlock
{
    private readonly NativeCpuBlockCode code;

    internal NativeCpuBlock(uint startAddress, int instructionCount, NativeCpuBlockCode code)
    {
        StartAddress = startAddress;
        InstructionCount = instructionCount;
        this.code = code;
    }

    public uint StartAddress { get; }

    public int InstructionCount { get; }

    public void Execute(CpuState state, SnesMemoryBus memoryBus)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(memoryBus);
        code(state, memoryBus);
    }
}
