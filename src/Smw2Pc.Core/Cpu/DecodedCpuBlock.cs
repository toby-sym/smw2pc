namespace Smw2Pc.Core.Cpu;

internal sealed record DecodedCpuBlock(
    uint StartAddress,
    byte StatusMode,
    bool EmulationMode,
    IReadOnlyList<NativeCpuInstruction> Instructions,
    byte[] SourceBytes)
{
    public int InstructionCount => Instructions.Count;
}
