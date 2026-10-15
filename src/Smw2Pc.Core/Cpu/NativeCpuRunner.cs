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
            var blockSize = Math.Min(MaximumBlockSize, instructionBudget - instructionsExecuted);
            var block = GetOrTranslateBlock(blockSize);
            block.Execute(State, MemoryBus);
            instructionsExecuted += block.InstructionCount;
        }

        return instructionsExecuted;
    }

    private NativeCpuBlock GetOrTranslateBlock(int maximumInstructions)
    {
        var address = ((uint)State.ProgramBank << 16) | State.ProgramCounter;
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

    private readonly record struct TranslationKey(
        uint ProgramAddress,
        byte RegisterWidthMode,
        bool EmulationMode,
        int MaximumInstructions);
}
