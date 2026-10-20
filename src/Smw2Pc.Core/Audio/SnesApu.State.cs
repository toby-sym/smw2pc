using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Audio;

public sealed partial class SnesApu
{
    internal void TransferState(StateBuffer state)
    {
        state.Values(audioRam);
        state.Values(cpuToSpcPorts);
        state.Values(spcToCpuPorts);
        control = state.Value(control);
        dspAddress = state.Value(dspAddress);
        pendingSpcCycles = state.Value(pendingSpcCycles);
        dspCyclePosition = state.Value(dspCyclePosition);
        cpuCycleFraction = state.Value(cpuCycleFraction);
        foreach (var timer in timers) timer.TransferState(state);
        cpu.TransferState(state);
        dsp.TransferState(state);
        var sampleCount = state.Value(pendingSamples.Count);
        if (sampleCount < 0 || sampleCount > 128000)
            throw new InvalidDataException("Invalid pending audio sample count.");
        if (state.Reading)
        {
            pendingSamples.Clear();
            for (var i = 0; i < sampleCount; i++) pendingSamples.Add(state.Value((short)0));
        }
        else foreach (var sample in pendingSamples) state.Value(sample);
    }

    private sealed partial class Timer
    {
        internal void TransferState(StateBuffer state)
        {
            state.Expect(period);
            divider = state.Value(divider);
            stage = state.Value(stage);
            output = state.Value(output);
            enabled = state.Value(enabled);
            Target = state.Value(Target);
        }
    }
}
