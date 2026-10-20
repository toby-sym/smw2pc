using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Cpu;

public sealed partial class CpuState
{
    internal void TransferState(StateBuffer state)
    {
        Accumulator = state.Value(Accumulator);
        X = state.Value(X);
        Y = state.Value(Y);
        StackPointer = state.Value(StackPointer);
        DirectPage = state.Value(DirectPage);
        ProgramCounter = state.Value(ProgramCounter);
        DataBank = state.Value(DataBank);
        ProgramBank = state.Value(ProgramBank);
        Status = state.Value(Status);
        EmulationMode = state.Value(EmulationMode);
        Halted = state.Value(Halted);
        WaitingForInterrupt = state.Value(WaitingForInterrupt);
    }
}
