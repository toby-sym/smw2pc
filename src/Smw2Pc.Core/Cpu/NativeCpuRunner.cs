namespace Smw2Pc.Core.Cpu;

public sealed class NativeCpuRunner
{
    private const int MaximumBlockSize = 32;

#if !SMW2PC_RECOMPILED
    private readonly NativeBlockTranslator translator = new();
    private readonly Dictionary<TranslationKey, NativeCpuBlock> translatedBlocks = [];
    internal Action<DecodedCpuBlock>? BlockDiscovered { get; set; }
#endif

    public NativeCpuRunner(Machine.SnesMemoryBus memoryBus)
    {
        ArgumentNullException.ThrowIfNull(memoryBus);
        MemoryBus = memoryBus;
#if SMW2PC_RECOMPILED
        CompiledRomProgram.ValidateRom(memoryBus.Rom);
#endif
        State = CpuState.CreateResetState(memoryBus);
    }

    public Machine.SnesMemoryBus MemoryBus { get; }

    public CpuState State { get; }

    public int CachedBlockCount =>
#if SMW2PC_RECOMPILED
        0;
#else
        translatedBlocks.Count;
#endif

    public long CompiledInstructionsExecuted { get; private set; }

    public static bool IsRecompiled =>
#if SMW2PC_RECOMPILED
        true;
#else
        false;
#endif

    public int Run(int instructionBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instructionBudget);

        MemoryBus.Widescreen.PrepareFrame();
        var instructionsExecuted = 0;
        while (instructionsExecuted < instructionBudget && !State.Halted)
        {
            if (State.WaitingForInterrupt)
            {
                MemoryBus.Hardware.AdvanceCpuInstructions(MemoryBus, 1);
                instructionsExecuted++;
                ServiceInterrupts();

                continue;
            }

            var blockSize = Math.Min(MaximumBlockSize, instructionBudget - instructionsExecuted);
            blockSize = MemoryBus.Hardware.LimitInstructionsToTimerIrq(blockSize);
#if SMW2PC_RECOMPILED
            var executed = CompiledRomProgram.Execute(State, MemoryBus, blockSize);
            CompiledInstructionsExecuted += executed;
#else
            var block = GetOrTranslateBlock(blockSize);
            block.Execute(State, MemoryBus);
            var executed = block.InstructionCount;
#endif
            MemoryBus.Hardware.AdvanceCpuInstructions(MemoryBus, executed);
            ServiceInterrupts();

            instructionsExecuted += executed;
        }

        return instructionsExecuted;
    }

#if !SMW2PC_RECOMPILED
    private NativeCpuBlock GetOrTranslateBlock(int maximumInstructions)
    {
        var address = ((uint)State.ProgramBank << 16) | State.ProgramCounter;
        uint signature = 0;
        if (!MemoryBus.IsCartridgeRomAddress(address))
        {
            maximumInstructions = 1;
            for (var offset = 0; offset < 4; offset++)
                signature |= (uint)MemoryBus.PeekCodeByte((address & 0xFF0000) | (ushort)(State.ProgramCounter + offset)) << (offset * 8);
        }

        var mode = (byte)(State.Status & (CpuStatusFlags.Accumulator8Bit | CpuStatusFlags.Index8Bit));
        var key = new TranslationKey(address, mode, State.EmulationMode, maximumInstructions, signature);

        if (translatedBlocks.TryGetValue(key, out var block))
        {
            return block;
        }

        block = translator.Translate(MemoryBus, State, maximumInstructions);
        translatedBlocks.Add(key, block);
        BlockDiscovered?.Invoke(block.Decoded);
        return block;
    }
#endif

    private void ServiceInterrupts()
    {
        if (MemoryBus.Hardware.ConsumeNmiRequest())
        {
            EnterInterrupt(nmi: true);
        }
        else if (MemoryBus.Hardware.IrqRequested)
        {
            // WAI resumes on an asserted IRQ even when the CPU I flag prevents
            // entry into the handler. The hardware flag stays set until acknowledged.
            State.WaitingForInterrupt = false;
            if ((State.Status & CpuStatusFlags.InterruptDisable) == 0)
            {
                EnterInterrupt(nmi: false);
            }
        }
    }

    private void EnterInterrupt(bool nmi)
    {
        State.WaitingForInterrupt = false;

        if (!State.EmulationMode)
        {
            PushByte(State.ProgramBank);
        }

        PushByte((byte)(State.ProgramCounter >> 8));
        PushByte((byte)State.ProgramCounter);
        PushByte(State.EmulationMode ? (byte)(State.Status & ~0x10) : State.Status);
        State.Status |= CpuStatusFlags.InterruptDisable;
        State.Status &= unchecked((byte)~CpuStatusFlags.Decimal);
        State.ProgramBank = 0;
        var vector = nmi
            ? (State.EmulationMode ? 0xFFFAu : 0xFFEAu)
            : (State.EmulationMode ? 0xFFFEu : 0xFFEEu);
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
        int MaximumInstructions,
        uint CodeSignature);
}
