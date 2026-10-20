using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Audio;

internal sealed partial class Spc700Cpu
{
    internal void TransferState(StateBuffer state)
    {
        status = state.Value(status);
        extraCycles = state.Value(extraCycles);
        A = state.Value(A);
        X = state.Value(X);
        Y = state.Value(Y);
        StackPointer = state.Value(StackPointer);
        ProgramCounter = state.Value(ProgramCounter);
        Halted = state.Value(Halted);
        TranslatedInstructionsExecuted = state.Value(TranslatedInstructionsExecuted);
    }
}
