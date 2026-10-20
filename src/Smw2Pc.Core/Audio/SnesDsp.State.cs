using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Audio;

internal sealed partial class SnesDsp
{
    internal void TransferState(StateBuffer state)
    {
        state.Values(registers);
        state.Values(echoFirLeft);
        state.Values(echoFirRight);
        pendingKeyOn = state.Value(pendingKeyOn);
        latchedKeyOn = state.Value(latchedKeyOn);
        noiseLfsr = state.Value(noiseLfsr);
        envelopeClock = state.Value(envelopeClock);
        echoPosition = state.Value(echoPosition);
        echoLength = state.Value(echoLength);
        echoHistoryIndex = state.Value(echoHistoryIndex);
        foreach (var voice in voices) voice.TransferState(state);
    }
    private sealed partial class Voice
    {
        internal void TransferState(StateBuffer state)
        {
            state.Values(DecodedSamples);
            BrrAddress = state.Value(BrrAddress);
            LoopAddress = state.Value(LoopAddress);
            SampleIndex = state.Value(SampleIndex);
            Phase = state.Value(Phase);
            Envelope = state.Value(Envelope);
            HiddenEnvelope = state.Value(HiddenEnvelope);
            KeyOnDelay = state.Value(KeyOnDelay);
            Output = state.Value(Output);
            FilterHistoryOne = state.Value(FilterHistoryOne);
            FilterHistoryTwo = state.Value(FilterHistoryTwo);
            EnvelopeMode = state.Value(EnvelopeMode);
            IsReleased = state.Value(IsReleased);
            Ended = state.Value(Ended);
        }
    }
}
