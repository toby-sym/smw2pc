namespace Smw2Pc.Core.Machine;

public sealed class SnesHardware
{
    private const int WorkRamSize = 0x20000;
    private const int VideoRamSize = 0x10000;
    private const int ControllerCount = 2;
    private const int InstructionsPerFrame = 18000;
    private readonly int scanlinesPerFrame;
    private readonly int vBlankStartInstruction;

    private readonly byte[] workRam = new byte[WorkRamSize];
    private readonly byte[] videoRam = new byte[VideoRamSize];
    private readonly byte[] colorRam = new byte[0x200];
    private readonly byte[] objectAttributeMemory = new byte[544];
    private readonly byte[] ppuRegisters = new byte[0x40];
    private readonly byte[] apuPorts = new byte[4];
    private readonly byte[] cpuRegisters = new byte[0x0E];
    private readonly byte[] dmaRegisters = new byte[0x80];
    private readonly ushort[] hdmaTableAddresses = new ushort[8];
    private readonly ushort[] hdmaDataAddresses = new ushort[8];
    private readonly byte[] hdmaLineCounts = new byte[8];
    private readonly bool[] hdmaChannelActive = new bool[8];
    private readonly bool[] hdmaRepeat = new bool[8];
    private readonly bool[] hdmaTransferThisLine = new bool[8];
    private readonly byte[] ppuRegistersByScanline;
    private readonly ushort[] fixedColorsByScanline;
    private readonly ushort[] backgroundHorizontalOffsets = new ushort[4];
    private readonly ushort[] backgroundVerticalOffsets = new ushort[4];
    private readonly byte[] backgroundScrollLatches = new byte[8];
    private readonly SnesControllerButtons[] controllerButtons = new SnesControllerButtons[ControllerCount];
    private readonly ushort[] controllerShiftRegisters = new ushort[ControllerCount];
    private readonly int[] controllerShiftPositions = new int[ControllerCount];
    private int apuHandshakeInstructionsRemaining = 256;
    private int frameInstructionPosition;
    private int currentScanline;
    private uint workRamPortAddress;
    private ushort videoRamWordAddress;
    private ushort objectAttributeWordAddress;
    private byte colorRamAddress;
    private byte colorRamLowByte;
    private byte fixedRed;
    private byte fixedGreen;
    private byte fixedBlue;
    private bool colorRamHighByte;
    private bool objectAttributeHighByte;
    private bool backgroundScrollHighByte;
    private bool controllerStrobe;
    private bool inVBlank;
    private bool hdmaFrameStarted;
    private bool nmiFlag;
    private bool nmiRequested;

    public SnesHardware(bool palRegion = false)
    {
        scanlinesPerFrame = palRegion ? 312 : 262;
        vBlankStartInstruction = InstructionsPerFrame * 225 / scanlinesPerFrame;
        ppuRegistersByScanline = new byte[scanlinesPerFrame * ppuRegisters.Length];
        fixedColorsByScanline = new ushort[scanlinesPerFrame];
        for (var scanline = 0; scanline < scanlinesPerFrame; scanline++)
        {
            CapturePpuScanline(scanline);
        }
    }

    public ReadOnlyMemory<byte> VideoRam => videoRam;

    public ReadOnlyMemory<byte> ColorRam => colorRam;

    public ReadOnlyMemory<byte> ObjectAttributeMemory => objectAttributeMemory;

    public ReadOnlyMemory<byte> PpuRegisters => ppuRegisters;

    public ushort FixedColor => (ushort)(fixedRed | (fixedGreen << 5) | (fixedBlue << 10));

    public ReadOnlyMemory<byte> GetPpuRegistersForScanline(int scanline)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(scanline, scanlinesPerFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(scanline);
        return new ReadOnlyMemory<byte>(ppuRegistersByScanline, scanline * ppuRegisters.Length, ppuRegisters.Length);
    }

    public ushort GetFixedColorForScanline(int scanline)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(scanline, scanlinesPerFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(scanline);
        return fixedColorsByScanline[scanline];
    }

    public ushort GetBackgroundHorizontalOffset(int background)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(background, 4);
        ArgumentOutOfRangeException.ThrowIfNegative(background);
        return backgroundHorizontalOffsets[background];
    }

    public ushort GetBackgroundVerticalOffset(int background)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(background, 4);
        ArgumentOutOfRangeException.ThrowIfNegative(background);
        return backgroundVerticalOffsets[background];
    }

    internal void AdvanceCpuInstructions(SnesMemoryBus memoryBus, int instructionCount)
    {
        ArgumentNullException.ThrowIfNull(memoryBus);
        if (instructionCount <= 0)
        {
            return;
        }

        if (apuHandshakeInstructionsRemaining > 0)
        {
            apuHandshakeInstructionsRemaining -= instructionCount;
            if (apuHandshakeInstructionsRemaining <= 0)
            {
                // The SPC700 boot ROM signals readiness with this two-byte handshake.
                // Audio execution remains out of scope for this silent startup stub.
                apuPorts[0] = 0xAA;
                apuPorts[1] = 0xBB;
            }
        }

        var instructionsRemaining = instructionCount;
        while (instructionsRemaining > 0)
        {
            if (!hdmaFrameStarted)
            {
                BeginHdmaFrame(memoryBus);
            }

            var nextScanlineBoundary = (int)(((long)(currentScanline + 1) * InstructionsPerFrame + scanlinesPerFrame - 1) / scanlinesPerFrame);
            var nextBoundary = Math.Min(
                inVBlank ? InstructionsPerFrame : vBlankStartInstruction,
                nextScanlineBoundary);
            var untilBoundary = nextBoundary - frameInstructionPosition;
            var advance = Math.Min(instructionsRemaining, untilBoundary);
            frameInstructionPosition += advance;
            instructionsRemaining -= advance;

            if (frameInstructionPosition == nextBoundary)
            {
                if (frameInstructionPosition == nextScanlineBoundary)
                {
                    ProcessHdmaScanline(memoryBus);
                    currentScanline++;
                    if (currentScanline < scanlinesPerFrame)
                    {
                        CapturePpuScanline(currentScanline);
                    }
                }

                if (frameInstructionPosition == InstructionsPerFrame)
                {
                    inVBlank = false;
                    frameInstructionPosition = 0;
                    currentScanline = 0;
                    hdmaFrameStarted = false;
                }
                else if (frameInstructionPosition == vBlankStartInstruction)
                {
                    inVBlank = true;
                    nmiFlag = true;
                    if ((cpuRegisters[0] & 0x80) != 0)
                    {
                        nmiRequested = true;
                    }
                }
            }
        }
    }

    private void BeginHdmaFrame(SnesMemoryBus memoryBus)
    {
        hdmaFrameStarted = true;
        currentScanline = 0;
        var enabledChannels = cpuRegisters[0x0C];
        for (var channel = 0; channel < 8; channel++)
        {
            var registerBase = channel * 0x10;
            hdmaChannelActive[channel] = (enabledChannels & (1 << channel)) != 0;
            hdmaLineCounts[channel] = 0;
            hdmaRepeat[channel] = false;
            hdmaTransferThisLine[channel] = false;
            if (!hdmaChannelActive[channel])
            {
                continue;
            }

            hdmaTableAddresses[channel] = (ushort)(dmaRegisters[registerBase + 2] | (dmaRegisters[registerBase + 3] << 8));
            dmaRegisters[registerBase + 8] = (byte)hdmaTableAddresses[channel];
            dmaRegisters[registerBase + 9] = (byte)(hdmaTableAddresses[channel] >> 8);
            LoadNextHdmaTableEntry(memoryBus, channel);
        }

        CapturePpuScanline(0);
    }

    private void ProcessHdmaScanline(SnesMemoryBus memoryBus)
    {
        for (var channel = 0; channel < 8; channel++)
        {
            if (!hdmaChannelActive[channel])
            {
                continue;
            }

            var registerBase = channel * 0x10;
            var mode = dmaRegisters[registerBase];
            if (hdmaTransferThisLine[channel])
            {
                var transferPattern = GetDmaTransferPattern(mode & 0x07);
                var indirect = (mode & 0x40) != 0;
                var dataBank = indirect ? dmaRegisters[registerBase + 7] : dmaRegisters[registerBase + 4];
                for (var index = 0; index < transferPattern.Length; index++)
                {
                    var bAddress = (ushort)(0x2100 + ((dmaRegisters[registerBase + 1] + transferPattern[index]) & 0xFF));
                    var aAddress = indirect
                        ? ((uint)dataBank << 16) | hdmaDataAddresses[channel]
                        : ((uint)dataBank << 16) | hdmaTableAddresses[channel];
                    if ((mode & 0x80) != 0)
                    {
                        memoryBus.WriteByte(aAddress, memoryBus.ReadByte(bAddress));
                    }
                    else
                    {
                        memoryBus.WriteByte(bAddress, memoryBus.ReadByte(aAddress));
                    }

                    if (indirect)
                    {
                        hdmaDataAddresses[channel]++;
                        dmaRegisters[registerBase + 5] = (byte)hdmaDataAddresses[channel];
                        dmaRegisters[registerBase + 6] = (byte)(hdmaDataAddresses[channel] >> 8);
                    }
                    else
                    {
                        hdmaTableAddresses[channel]++;
                    }
                }
            }

            hdmaLineCounts[channel]--;
            if (hdmaLineCounts[channel] == 0)
            {
                LoadNextHdmaTableEntry(memoryBus, channel);
            }
            else
            {
                hdmaTransferThisLine[channel] = hdmaRepeat[channel];
                dmaRegisters[registerBase + 10] = (byte)(hdmaLineCounts[channel] | (hdmaRepeat[channel] ? 0x80 : 0));
                if ((mode & 0x40) == 0)
                {
                    dmaRegisters[registerBase + 8] = (byte)hdmaTableAddresses[channel];
                    dmaRegisters[registerBase + 9] = (byte)(hdmaTableAddresses[channel] >> 8);
                }
            }
        }
    }

    private void LoadNextHdmaTableEntry(SnesMemoryBus memoryBus, int channel)
    {
        var registerBase = channel * 0x10;
        var mode = dmaRegisters[registerBase];
        var tableBank = dmaRegisters[registerBase + 4];
        var descriptor = memoryBus.ReadByte(((uint)tableBank << 16) | hdmaTableAddresses[channel]);
        hdmaTableAddresses[channel]++;
        if (descriptor == 0)
        {
            hdmaChannelActive[channel] = false;
            hdmaLineCounts[channel] = 0;
            hdmaTransferThisLine[channel] = false;
            dmaRegisters[registerBase + 10] = 0;
            return;
        }

        hdmaRepeat[channel] = (descriptor & 0x80) != 0;
        hdmaLineCounts[channel] = (byte)(descriptor & 0x7F);
        if (hdmaLineCounts[channel] == 0)
        {
            hdmaLineCounts[channel] = 128;
        }

        hdmaTransferThisLine[channel] = true;
        if ((mode & 0x40) != 0)
        {
            var pointerLow = memoryBus.ReadByte(((uint)tableBank << 16) | hdmaTableAddresses[channel]);
            hdmaTableAddresses[channel]++;
            var pointerHigh = memoryBus.ReadByte(((uint)tableBank << 16) | hdmaTableAddresses[channel]);
            hdmaTableAddresses[channel]++;
            hdmaDataAddresses[channel] = (ushort)(pointerLow | (pointerHigh << 8));
            dmaRegisters[registerBase + 5] = pointerLow;
            dmaRegisters[registerBase + 6] = pointerHigh;
        }

        dmaRegisters[registerBase + 8] = (byte)hdmaTableAddresses[channel];
        dmaRegisters[registerBase + 9] = (byte)(hdmaTableAddresses[channel] >> 8);
        dmaRegisters[registerBase + 10] = (byte)(hdmaLineCounts[channel] | (hdmaRepeat[channel] ? 0x80 : 0));
    }

    private void CapturePpuScanline(int scanline)
    {
        ppuRegisters.CopyTo(ppuRegistersByScanline, scanline * ppuRegisters.Length);
        fixedColorsByScanline[scanline] = FixedColor;
    }

    private static byte[] GetDmaTransferPattern(int mode)
    {
        return mode switch
        {
            1 => [0, 1],
            2 or 6 => [0, 0],
            3 or 7 => [0, 0, 1, 1],
            4 => [0, 1, 2, 3],
            5 => [0, 1, 0, 1],
            _ => [0],
        };
    }

    internal bool ConsumeNmiRequest()
    {
        var requested = nmiRequested;
        nmiRequested = false;
        return requested;
    }

    public void SetControllerButtons(int port, SnesControllerButtons buttons)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(port, ControllerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(port);

        controllerButtons[port] = buttons;
        if (controllerStrobe)
        {
            LatchControllers();
        }
    }

    public SnesControllerButtons GetControllerButtons(int port)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(port, ControllerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        return controllerButtons[port];
    }

    internal byte ReadWorkRam(int address)
    {
        return workRam[address & 0x1FFFF];
    }

    internal void WriteWorkRam(int address, byte value)
    {
        workRam[address & 0x1FFFF] = value;
    }

    internal bool TryReadRegister(ushort address, byte openBus, out byte value)
    {
        if (address is >= 0x2139 and <= 0x213A)
        {
            var highByte = address == 0x213A;
            value = videoRam[(videoRamWordAddress << 1) | (highByte ? 1 : 0)];
            IncrementVideoRamAddress(highByte);
            return true;
        }

        if (address is >= 0x2140 and <= 0x217F)
        {
            value = apuPorts[(address - 0x2140) & 0x03];
            return true;
        }

        if (address == 0x4210)
        {
            value = (byte)((openBus & 0x7F) | (nmiFlag ? 0x80 : 0));
            nmiFlag = false;
            return true;
        }

        if (address == 0x4212)
        {
            var horizontalPosition = (frameInstructionPosition * scanlinesPerFrame) % InstructionsPerFrame;
            var inHBlank = horizontalPosition >= (InstructionsPerFrame * 3 / 4);
            value = (byte)(
                (openBus & 0x3E)
                | (inHBlank ? 0x40 : 0)
                | (inVBlank ? 0x80 : 0));
            return true;
        }

        if (address == 0x2180)
        {
            value = workRam[workRamPortAddress];
            workRamPortAddress = (workRamPortAddress + 1) & 0x1FFFF;
            return true;
        }

        if (address is 0x4016 or 0x4017)
        {
            var port = address - 0x4016;
            var bit = ReadControllerBit(port);
            value = (byte)((openBus & 0xFE) | bit);
            return true;
        }

        if (address is >= 0x4218 and <= 0x421F)
        {
            var relative = address - 0x4218;
            var port = relative / 2;
            var highByte = (relative & 1) != 0;
            var autoJoypadValue = port < ControllerCount
                ? EncodeAutoJoypadButtons(controllerButtons[port])
                : (ushort)0;
            value = (byte)(autoJoypadValue >> (highByte ? 8 : 0));
            return true;
        }

        if (address is >= 0x4300 and <= 0x437F)
        {
            value = dmaRegisters[address - 0x4300];
            return true;
        }

        value = openBus;
        return false;
    }

    internal bool TryWriteRegister(SnesMemoryBus memoryBus, ushort address, byte value)
    {
        ArgumentNullException.ThrowIfNull(memoryBus);

        if (address is >= 0x2100 and <= 0x213F)
        {
            WritePpuRegister(address, value);
            return true;
        }

        if (address is >= 0x2140 and <= 0x217F)
        {
            apuPorts[(address - 0x2140) & 0x03] = value;
            apuHandshakeInstructionsRemaining = 256;
            return true;
        }

        if (address == 0x2180)
        {
            workRam[workRamPortAddress] = value;
            workRamPortAddress = (workRamPortAddress + 1) & 0x1FFFF;
            return true;
        }

        if (address is >= 0x2181 and <= 0x2183)
        {
            SetWorkRamPortAddress(address, value);
            return true;
        }

        if (address == 0x4016)
        {
            SetControllerStrobe((value & 1) != 0);
            return true;
        }

        if (address is >= 0x4200 and <= 0x420D)
        {
            cpuRegisters[address - 0x4200] = value;
            if (address == 0x4200 && nmiFlag && (value & 0x80) != 0)
            {
                nmiRequested = true;
            }

            if (address == 0x420B)
            {
                TransferDma(memoryBus, value);
            }

            return true;
        }

        if (address is >= 0x4300 and <= 0x437F)
        {
            dmaRegisters[address - 0x4300] = value;
            return true;
        }

        return false;
    }

    private void TransferDma(SnesMemoryBus memoryBus, byte enabledChannels)
    {
        for (var channel = 0; channel < 8; channel++)
        {
            if ((enabledChannels & (1 << channel)) == 0)
            {
                continue;
            }

            var registerBase = channel * 0x10;
            var mode = dmaRegisters[registerBase];
            var bBusAddress = dmaRegisters[registerBase + 1];
            var aAddress = (ushort)(dmaRegisters[registerBase + 2] | (dmaRegisters[registerBase + 3] << 8));
            var aBank = dmaRegisters[registerBase + 4];
            var transferSize = dmaRegisters[registerBase + 5] | (dmaRegisters[registerBase + 6] << 8);
            if (transferSize == 0)
            {
                transferSize = 0x10000;
            }

            var transferMode = mode & 0x07;
            var transferPattern = transferMode switch
            {
                1 => new byte[] { 0, 1 },
                3 or 7 => new byte[] { 0, 0, 1, 1 },
                4 => new byte[] { 0, 1, 2, 3 },
                5 => new byte[] { 0, 1, 0, 1 },
                _ => new byte[] { 0 },
            };
            var fixedAddress = (mode & 0x08) != 0;
            var decrementAddress = (mode & 0x10) != 0;
            var bToA = (mode & 0x80) != 0;
            for (var index = 0; index < transferSize; index++)
            {
                var bAddress = (ushort)(0x2100 + ((bBusAddress + transferPattern[index % transferPattern.Length]) & 0xFF));
                var aBusAddress = ((uint)aBank << 16) | aAddress;
                if (bToA)
                {
                    memoryBus.WriteByte(aBusAddress, memoryBus.ReadByte(bAddress));
                }
                else
                {
                    memoryBus.WriteByte(bAddress, memoryBus.ReadByte(aBusAddress));
                }

                if (!fixedAddress)
                {
                    aAddress = unchecked((ushort)(aAddress + (decrementAddress ? -1 : 1)));
                }
            }

            dmaRegisters[registerBase + 2] = (byte)aAddress;
            dmaRegisters[registerBase + 3] = (byte)(aAddress >> 8);
            dmaRegisters[registerBase + 5] = 0;
            dmaRegisters[registerBase + 6] = 0;
        }
    }

    private void WritePpuRegister(ushort address, byte value)
    {
        var register = address - 0x2100;
        if (register > 0x33)
        {
            return;
        }

        ppuRegisters[register] = value;
        if (address is >= 0x210D and <= 0x2114)
        {
            WriteBackgroundScroll(address, value);
        }

        switch (address)
        {
            case 0x2102:
                objectAttributeWordAddress = (ushort)((objectAttributeWordAddress & 0x100) | value);
                objectAttributeHighByte = false;
                break;
            case 0x2103:
                objectAttributeWordAddress = (ushort)((objectAttributeWordAddress & 0xFF) | ((value & 1) << 8));
                objectAttributeHighByte = false;
                break;
            case 0x2104:
                var objectAttributeByteAddress = objectAttributeWordAddress >= 0x100
                    ? 512 + ((objectAttributeWordAddress & 0x0F) * 2) + (objectAttributeHighByte ? 1 : 0)
                    : (objectAttributeWordAddress * 2) + (objectAttributeHighByte ? 1 : 0);
                objectAttributeMemory[objectAttributeByteAddress] = value;
                objectAttributeHighByte = !objectAttributeHighByte;
                if (!objectAttributeHighByte)
                {
                    objectAttributeWordAddress = (ushort)((objectAttributeWordAddress + 1) & 0x1FF);
                }

                break;
            case 0x2116:
                videoRamWordAddress = (ushort)((videoRamWordAddress & 0xFF00) | value);
                break;
            case 0x2117:
                videoRamWordAddress = (ushort)((videoRamWordAddress & 0x00FF) | (value << 8));
                break;
            case 0x2118:
                videoRam[videoRamWordAddress << 1] = value;
                IncrementVideoRamAddress(highByte: false);
                break;
            case 0x2119:
                videoRam[(videoRamWordAddress << 1) | 1] = value;
                IncrementVideoRamAddress(highByte: true);
                break;
            case 0x2121:
                colorRamAddress = value;
                colorRamHighByte = false;
                break;
            case 0x2122:
                if (!colorRamHighByte)
                {
                    colorRamLowByte = value;
                    colorRamHighByte = true;
                    break;
                }

                var colorRamByteAddress = colorRamAddress * 2;
                colorRam[colorRamByteAddress] = colorRamLowByte;
                colorRam[colorRamByteAddress + 1] = (byte)(value & 0x7F);
                colorRamHighByte = false;
                colorRamAddress++;
                break;
            case 0x2132:
                if ((value & 0x20) != 0)
                {
                    fixedRed = (byte)(value & 0x1F);
                }

                if ((value & 0x40) != 0)
                {
                    fixedGreen = (byte)(value & 0x1F);
                }

                if ((value & 0x80) != 0)
                {
                    fixedBlue = (byte)(value & 0x1F);
                }

                break;
        }
    }

    private void WriteBackgroundScroll(ushort address, byte value)
    {
        var relative = address - 0x210D;
        var background = relative / 2;
        var isVertical = (relative & 1) != 0;
        var latchIndex = relative;
        if (!backgroundScrollHighByte)
        {
            backgroundScrollLatches[latchIndex] = value;
        }
        else
        {
            var offset = (ushort)(backgroundScrollLatches[latchIndex] | ((value & 0x03) << 8));
            if (isVertical)
            {
                backgroundVerticalOffsets[background] = offset;
            }
            else
            {
                backgroundHorizontalOffsets[background] = offset;
            }
        }

        backgroundScrollHighByte = !backgroundScrollHighByte;
    }

    private void IncrementVideoRamAddress(bool highByte)
    {
        var incrementOnHighByte = (ppuRegisters[0x15] & 0x80) != 0;
        if (highByte != incrementOnHighByte)
        {
            return;
        }

        var increment = (ppuRegisters[0x15] & 0x03) switch
        {
            0 => 1,
            1 => 32,
            _ => 128,
        };
        videoRamWordAddress = (ushort)(videoRamWordAddress + increment);
    }

    private void SetWorkRamPortAddress(ushort register, byte value)
    {
        workRamPortAddress = register switch
        {
            0x2181 => (workRamPortAddress & 0x1FF00) | value,
            0x2182 => (workRamPortAddress & 0x100FF) | ((uint)value << 8),
            0x2183 => (workRamPortAddress & 0x0FFFF) | ((uint)(value & 1) << 16),
            _ => workRamPortAddress,
        };
    }

    private void SetControllerStrobe(bool strobe)
    {
        if (strobe || controllerStrobe)
        {
            LatchControllers();
        }

        controllerStrobe = strobe;
    }

    private void LatchControllers()
    {
        for (var port = 0; port < ControllerCount; port++)
        {
            controllerShiftRegisters[port] = (ushort)controllerButtons[port];
            controllerShiftPositions[port] = 0;
        }
    }

    private static ushort EncodeAutoJoypadButtons(SnesControllerButtons buttons)
    {
        ushort value = 0;
        value |= (ushort)(((buttons & SnesControllerButtons.B) != 0 ? 1 : 0) << 15);
        value |= (ushort)(((buttons & SnesControllerButtons.Y) != 0 ? 1 : 0) << 14);
        value |= (ushort)(((buttons & SnesControllerButtons.Select) != 0 ? 1 : 0) << 13);
        value |= (ushort)(((buttons & SnesControllerButtons.Start) != 0 ? 1 : 0) << 12);
        value |= (ushort)(((buttons & SnesControllerButtons.Up) != 0 ? 1 : 0) << 11);
        value |= (ushort)(((buttons & SnesControllerButtons.Down) != 0 ? 1 : 0) << 10);
        value |= (ushort)(((buttons & SnesControllerButtons.Left) != 0 ? 1 : 0) << 9);
        value |= (ushort)(((buttons & SnesControllerButtons.Right) != 0 ? 1 : 0) << 8);
        value |= (ushort)(((buttons & SnesControllerButtons.A) != 0 ? 1 : 0) << 7);
        value |= (ushort)(((buttons & SnesControllerButtons.X) != 0 ? 1 : 0) << 6);
        value |= (ushort)(((buttons & SnesControllerButtons.L) != 0 ? 1 : 0) << 5);
        value |= (ushort)(((buttons & SnesControllerButtons.R) != 0 ? 1 : 0) << 4);
        return value;
    }

    private byte ReadControllerBit(int port)
    {
        if (controllerStrobe)
        {
            return (byte)((controllerButtons[port] & SnesControllerButtons.B) != 0 ? 1 : 0);
        }

        var position = controllerShiftPositions[port];
        if (position >= 16)
        {
            return 1;
        }

        var bit = (byte)(controllerShiftRegisters[port] & 1);
        controllerShiftRegisters[port] >>= 1;
        controllerShiftPositions[port]++;
        return bit;
    }
}
