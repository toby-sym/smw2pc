namespace Smw2Pc.Core.Machine;

public sealed partial class SnesMemoryBus
{
    internal void TransferState(StateBuffer state)
    {
        openBus = state.Value(openBus);
        state.Values(saveRam);
        if (state.Reading) SaveRamRevision++;
        Hardware.TransferState(state);
    }
}
