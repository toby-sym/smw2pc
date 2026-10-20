namespace Smw2Pc.Core.Audio;

public sealed partial class SnesApu
{
    private const int AudioRamSize = 0x10000;
    private const int SpcClockHz = 1_024_000;
    private const int DspCyclesPerSample = 32;

    private static readonly byte[] IplRom =
    [
        0xCD, 0xEF, 0xBD, 0xE8, 0x00, 0xC6, 0x1D, 0xD0, 0xFC, 0x8F, 0xAA, 0xF4, 0x8F, 0xBB, 0xF5, 0x78,
        0xCC, 0xF4, 0xD0, 0xFB, 0x2F, 0x19, 0xEB, 0xF4, 0xD0, 0xFC, 0x7E, 0xF4, 0xD0, 0x0B, 0xE4, 0xF5,
        0xCB, 0xF4, 0xD7, 0x00, 0xFC, 0xD0, 0xF3, 0xAB, 0x01, 0x10, 0xEF, 0x7E, 0xF4, 0x10, 0xEB, 0xBA,
        0xF6, 0xDA, 0x00, 0xBA, 0xF4, 0xC4, 0xF4, 0xDD, 0x5D, 0xD0, 0xDB, 0x1F, 0x00, 0x00, 0xC0, 0xFF,
    ];

    private readonly byte[] audioRam = new byte[AudioRamSize];
    private readonly byte[] cpuToSpcPorts = new byte[4];
    private readonly byte[] spcToCpuPorts = new byte[4];
    private readonly Timer[] timers = [new(128), new(128), new(16)];
    private readonly Spc700Cpu cpu;
    private readonly SnesDsp dsp;
    private readonly List<short> pendingSamples = [];
    private byte control;
    private byte dspAddress;
    private int pendingSpcCycles;
    private int dspCyclePosition;
    private double cpuCycleFraction;

    public SnesApu()
    {
        cpu = new Spc700Cpu(this);
        dsp = new SnesDsp(audioRam);
        Reset();
    }

    public ReadOnlyMemory<byte> AudioRam => audioRam;

#if !SMW2PC_RECOMPILED
    internal Action<ushort, byte>? InstructionObserved
    {
        get => cpu.InstructionObserved;
        set => cpu.InstructionObserved = value;
    }
#endif

    internal byte[] GetExecutionState() => cpu.GetExecutionState();

    public int QueuedSampleFrames => pendingSamples.Count / 2;

    public long TranslatedInstructionsExecuted => cpu.TranslatedInstructionsExecuted;

    public byte ReadCpuPort(int port)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(port, 4);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        return spcToCpuPorts[port];
    }

    public void WriteCpuPort(int port, byte value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(port, 4);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        cpuToSpcPorts[port] = value;
    }

    public short[] DrainSamples()
    {
        if (pendingSamples.Count == 0)
        {
            return [];
        }

        var samples = pendingSamples.ToArray();
        pendingSamples.Clear();
        return samples;
    }

    public void Reset()
    {
        Array.Clear(audioRam);
        Array.Clear(cpuToSpcPorts);
        Array.Clear(spcToCpuPorts);
        foreach (var timer in timers)
        {
            timer.Reset();
        }

        dsp.Reset();
        control = 0xB0;
        dspAddress = 0;
        pendingSpcCycles = 0;
        dspCyclePosition = 0;
        cpuCycleFraction = 0;
        pendingSamples.Clear();
        cpu.Reset();
    }

    internal void AdvanceMainCpuInstructions(int instructionCount, double frameRateHz, int instructionsPerFrame)
    {
        if (instructionCount <= 0)
        {
            return;
        }

        var cyclesPerInstruction = SpcClockHz / (frameRateHz * instructionsPerFrame);
        cpuCycleFraction += instructionCount * cyclesPerInstruction;
        var cycles = (int)cpuCycleFraction;
        cpuCycleFraction -= cycles;
        AdvanceSpcCycles(cycles);
    }

    internal int ReadSpcMemory(ushort address)
    {
        if ((address & 0xFFF0) == 0x00F0)
        {
            return (address & 0x0F) switch
            {
                0x00 => 0,
                0x01 => control,
                0x02 => dspAddress,
                0x03 => dsp.Read(dspAddress),
                >= 0x04 and <= 0x07 => cpuToSpcPorts[(address & 0x0F) - 4],
                0x08 or 0x09 => audioRam[address],
                >= 0x0A and <= 0x0C => 0,
                >= 0x0D and <= 0x0F => timers[(address & 0x0F) - 0x0D].ReadOutput(),
                _ => 0,
            };
        }

        if (address >= 0xFFC0 && (control & 0x80) != 0)
        {
            return IplRom[address - 0xFFC0];
        }

        return audioRam[address];
    }

    internal void WriteSpcMemory(ushort address, byte value)
    {
        if ((address & 0xFFF0) != 0x00F0)
        {
            audioRam[address] = value;
            return;
        }

        switch (address & 0x0F)
        {
            case 0x00: // TEST is deliberately inert in the first audio pass.
                break;
            case 0x01:
                control = value;
                timers[0].SetEnabled((value & 0x01) != 0);
                timers[1].SetEnabled((value & 0x02) != 0);
                timers[2].SetEnabled((value & 0x04) != 0);
                if ((value & 0x10) != 0)
                {
                    cpuToSpcPorts[0] = 0;
                    cpuToSpcPorts[1] = 0;
                }

                if ((value & 0x20) != 0)
                {
                    cpuToSpcPorts[2] = 0;
                    cpuToSpcPorts[3] = 0;
                }

                break;
            case 0x02:
                dspAddress = value;
                break;
            case 0x03:
                if ((dspAddress & 0x80) == 0)
                {
                    dsp.Write(dspAddress, value);
                }

                break;
            case >= 0x04 and <= 0x07:
                spcToCpuPorts[(address & 0x0F) - 4] = value;
                break;
            case 0x08:
            case 0x09:
                audioRam[address] = value;
                break;
            case >= 0x0A and <= 0x0C:
                timers[(address & 0x0F) - 0x0A].Target = value;
                break;
            default:
                // The timer output registers ignore writes.
                break;
        }

        // Hardware writes also update the underlying RAM even when a register
        // overlays that address. F0-FF remains visible through the IPL page.
        audioRam[address] = value;
    }

    private void AdvanceSpcCycles(int cycles)
    {
        pendingSpcCycles += cycles;
        while (pendingSpcCycles > 0)
        {
            var consumed = cpu.Step();
            pendingSpcCycles -= consumed;
            timers[0].Advance(consumed);
            timers[1].Advance(consumed);
            timers[2].Advance(consumed);
            dspCyclePosition += consumed;
            while (dspCyclePosition >= DspCyclesPerSample)
            {
                dspCyclePosition -= DspCyclesPerSample;
                var (left, right) = dsp.RenderSample();
                pendingSamples.Add(left);
                pendingSamples.Add(right);
            }
        }
    }

    private sealed partial class Timer(int period)
    {
        private int divider;
        private int stage;
        private int output;
        private bool enabled;

        public byte Target { get; set; }

        public void Reset()
        {
            divider = 0;
            stage = 0;
            output = 0;
            Target = 0;
            enabled = false;
        }

        public void SetEnabled(bool value)
        {
            if (value && !enabled)
            {
                stage = 0;
                output = 0;
            }

            enabled = value;
        }

        public void Advance(int cycles)
        {
            divider += cycles;
            while (divider >= period)
            {
                divider -= period;
                if (!enabled)
                {
                    continue;
                }

                stage++;
                var target = Target == 0 ? 256 : Target;
                if (stage >= target)
                {
                    stage = 0;
                    output = (output + 1) & 0x0F;
                }
            }
        }

        public int ReadOutput()
        {
            var value = output;
            output = 0;
            return value;
        }
    }
}
