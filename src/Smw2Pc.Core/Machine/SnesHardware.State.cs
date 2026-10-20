using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Machine;

public sealed partial class SnesHardware
{
    internal void TransferState(StateBuffer state)
    {
        state.Expect(scanlinesPerFrame);
        state.Expect(vBlankStartInstruction);
        state.Expect(frameRateHz);
        state.Values(workRam);
        state.Values(videoRam);
        state.Values(colorRam);
        state.Values(objectAttributeMemory);
        state.Values(currentFrameObjectAttributeMemory);
        state.Values(previousObjectAttributeMemory);
        state.Values(ppuRegisters);
        state.Values(cpuRegisters);
        state.Values(dmaRegisters);
        state.Values(hdmaTableAddresses);
        state.Values(hdmaDataAddresses);
        state.Values(hdmaLineCounts);
        state.Values(hdmaChannelActive);
        state.Values(hdmaRepeat);
        state.Values(hdmaTransferThisLine);
        state.Values(ppuRegistersByScanline);
        state.Values(fixedColorsByScanline);
        state.Values(mode7Parameters);
        state.Values(mode7ParametersByScanline);
        state.Values(backgroundOffsetsByScanline);
        state.Values(previousBackgroundOffsetsByScanline);
        state.Values(backgroundHorizontalOffsets);
        state.Values(backgroundVerticalOffsets);
        state.Values(controllerButtons);
        state.Values(controllerShiftRegisters);
        state.Values(controllerShiftPositions);
        frameInstructionPosition = state.Value(frameInstructionPosition);
        currentScanline = state.Value(currentScanline);
        workRamPortAddress = state.Value(workRamPortAddress);
        videoRamWordAddress = state.Value(videoRamWordAddress);
        objectAttributeWordAddress = state.Value(objectAttributeWordAddress);
        colorRamAddress = state.Value(colorRamAddress);
        colorRamLowByte = state.Value(colorRamLowByte);
        mode7WriteLatch = state.Value(mode7WriteLatch);
        fixedRed = state.Value(fixedRed);
        fixedGreen = state.Value(fixedGreen);
        fixedBlue = state.Value(fixedBlue);
        divideResult = state.Value(divideResult);
        multiplyOrRemainderResult = state.Value(multiplyOrRemainderResult);
        colorRamHighByte = state.Value(colorRamHighByte);
        objectAttributeHighByte = state.Value(objectAttributeHighByte);
        backgroundOffsetLatch = state.Value(backgroundOffsetLatch);
        horizontalBackgroundOffsetLatch = state.Value(horizontalBackgroundOffsetLatch);
        controllerStrobe = state.Value(controllerStrobe);
        inVBlank = state.Value(inVBlank);
        hdmaFrameStarted = state.Value(hdmaFrameStarted);
        hasCompletedPpuFrame = state.Value(hasCompletedPpuFrame);
        hasPreviousPpuFrameScrollOffsets = state.Value(hasPreviousPpuFrameScrollOffsets);
        hasCompletedObjectAttributeFrame = state.Value(hasCompletedObjectAttributeFrame);
        hasPreviousObjectAttributeFrame = state.Value(hasPreviousObjectAttributeFrame);
        nmiFlag = state.Value(nmiFlag);
        nmiRequested = state.Value(nmiRequested);
        timerIrqFlag = state.Value(timerIrqFlag);
        Apu.TransferState(state);
    }
}
