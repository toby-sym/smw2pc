namespace Smw2Pc.Core.Cpu;

public sealed class NativeCpuRunner
{
    private const int MaximumBlockSize = 32;

    private readonly NativeBlockTranslator translator = new();
    private readonly Dictionary<TranslationKey, NativeCpuBlock> translatedBlocks = [];

    public NativeCpuRunner(Machine.SnesMemoryBus memoryBus)
    {
        ArgumentNullException.ThrowIfNull(memoryBus);
        MemoryBus = memoryBus;
        State = CpuState.CreateResetState(memoryBus);
    }

    public Machine.SnesMemoryBus MemoryBus { get; }

    public CpuState State { get; }

    public int CachedBlockCount => translatedBlocks.Count;

    public int Run(int instructionBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instructionBudget);

        var instructionsExecuted = 0;
        while (instructionsExecuted < instructionBudget && !State.Halted)
        {
            if (State.WaitingForInterrupt)
            {
                MemoryBus.Hardware.AdvanceCpuInstructions(MemoryBus, 1);
                instructionsExecuted++;
                if (MemoryBus.Hardware.ConsumeNmiRequest())
                {
                    EnterNmi();
                }

                continue;
            }

            var blockSize = Math.Min(MaximumBlockSize, instructionBudget - instructionsExecuted);
            var block = GetOrTranslateBlock(blockSize);
            block.Execute(State, MemoryBus);
            MemoryBus.Hardware.AdvanceCpuInstructions(MemoryBus, block.InstructionCount);
            if (MemoryBus.Hardware.ConsumeNmiRequest())
            {
                EnterNmi();
            }

            instructionsExecuted += block.InstructionCount;
        }

        return instructionsExecuted;
    }

    private NativeCpuBlock GetOrTranslateBlock(int maximumInstructions)
    {
        var address = ((uint)State.ProgramBank << 16) | State.ProgramCounter;
        if (!MemoryBus.IsCartridgeRomAddress(address))
        {
            // WRAM can contain code copied from the cartridge and can be rewritten at runtime.
            // Translate one instruction at a time so each fetch sees current memory contents.
            return translator.Translate(MemoryBus, State, 1);
        }

        var mode = (byte)(State.Status & (CpuStatusFlags.Accumulator8Bit | CpuStatusFlags.Index8Bit));
        var key = new TranslationKey(address, mode, State.EmulationMode, maximumInstructions);

        if (translatedBlocks.TryGetValue(key, out var block))
        {
            return block;
        }

        block = translator.Translate(MemoryBus, State, maximumInstructions);
        translatedBlocks.Add(key, block);
        return block;
    }

    private void EnterNmi()
    {
        State.WaitingForInterrupt = false;

        if (!State.EmulationMode)
        {
            PushByte(State.ProgramBank);
        }

        PushByte((byte)(State.ProgramCounter >> 8));
        PushByte((byte)State.ProgramCounter);
        PushByte(State.Status);
        State.Status |= CpuStatusFlags.InterruptDisable;
        State.Status &= unchecked((byte)~CpuStatusFlags.Decimal);
        State.ProgramBank = 0;
        var vector = State.EmulationMode ? 0xFFFAu : 0xFFEAu;
        State.ProgramCounter = MemoryBus.ReadWordInBank(vector);
    }

    private void PushByte(byte value)
    {
        MemoryBus.WriteByte(State.StackPointer, value);
        State.StackPointer = State.EmulationMode
            ? (ushort)(0x0100 | ((State.StackPointer - 1) & 0x00FF))
            : (ushort)(State.StackPointer - 1);
    }

    private readonly record struct TranslationKey(
        uint ProgramAddress,
        byte RegisterWidthMode,
        bool EmulationMode,
        int MaximumInstructions);
}
