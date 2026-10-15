namespace Smw2Pc.Core.Machine;

public sealed class SnesHardware
{
    private const int WorkRamSize = 0x20000;
    private const int VideoRamSize = 0x10000;
    private const int ControllerCount = 2;

    private readonly byte[] workRam = new byte[WorkRamSize];
    private readonly byte[] videoRam = new byte[VideoRamSize];
    private readonly byte[] ppuRegisters = new byte[0x40];
    private readonly byte[] apuPorts = new byte[4];
    private readonly byte[] cpuRegisters = new byte[0x0E];
    private readonly byte[] dmaRegisters = new byte[0x80];
    private readonly SnesControllerButtons[] controllerButtons = new SnesControllerButtons[ControllerCount];
    private readonly ushort[] controllerShiftRegisters = new ushort[ControllerCount];
    private readonly int[] controllerShiftPositions = new int[ControllerCount];
    private uint workRamPortAddress;
    private ushort videoRamWordAddress;
    private bool controllerStrobe;

    public ReadOnlyMemory<byte> VideoRam => videoRam;

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
            value = port < ControllerCount
                ? (byte)((ushort)controllerButtons[port] >> (highByte ? 8 : 0))
                : (byte)0;
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

    internal bool TryWriteRegister(ushort address, byte value)
    {
        if (address is >= 0x2100 and <= 0x213F)
        {
            WritePpuRegister(address, value);
            return true;
        }

        if (address is >= 0x2140 and <= 0x217F)
        {
            apuPorts[(address - 0x2140) & 0x03] = value;
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
            return true;
        }

        if (address is >= 0x4300 and <= 0x437F)
        {
            dmaRegisters[address - 0x4300] = value;
            return true;
        }

        return false;
    }

    private void WritePpuRegister(ushort address, byte value)
    {
        var register = address - 0x2100;
        if (register > 0x33)
        {
            return;
        }

        ppuRegisters[register] = value;
        switch (address)
        {
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
        }
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
