using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Recompiler;

internal sealed class CodeDiscovery(SnesRom rom)
{
    private readonly SnesMemoryBus bus = new(rom);
    private readonly SmwDispatchTables dispatchers = new(rom);
    private readonly Dictionary<string, DecodedCpuBlock> blocks = [];
    private readonly Queue<(uint Address, byte Mode, bool Emulation)> pending = new();
    private readonly HashSet<(uint Address, byte Mode, bool Emulation)> visited = [];
    public HashSet<string> UnresolvedTransfers { get; } = [];
    public HashSet<string> InferredPointerTables { get; } = [];
    public int RecognizedSmwDispatchers => dispatchers.RecognizedHelpers;
    public IReadOnlyList<DecodedCpuBlock> Blocks => blocks.Values
        .OrderBy(b => b.StartAddress).ThenBy(b => b.StatusMode).ThenBy(b => b.EmulationMode)
        .ThenBy(b => Convert.ToHexString(b.SourceBytes)).ToArray();

    public void AddEntryPoint(uint address)
    {
        if (!bus.IsCartridgeRomAddress(address))
            throw new ArgumentOutOfRangeException(nameof(address), $"${address:X6} is not a cartridge ROM address.");
        AllModes(bus.CanonicalCodeAddress(address));
    }

    public void AddObserved(DecodedCpuBlock block)
    {
        block = block with
        {
            StartAddress = bus.CanonicalCodeAddress(block.StartAddress),
            StatusMode = block.EmulationMode ? (byte)0x30 : block.StatusMode
        };
        var isRom = bus.IsCartridgeRomAddress(block.StartAddress);
        var key = $"{block.StartAddress:X6}:{block.StatusMode:X2}:{block.EmulationMode}"
            + (isRom ? "" : ":" + Convert.ToHexString(block.SourceBytes));
        if (!blocks.TryGetValue(key, out var existing) || block.InstructionCount > existing.InstructionCount)
            blocks[key] = block;
        if (isRom) Enqueue(block.StartAddress, block.StatusMode, block.EmulationMode);
    }

    public void Analyze()
    {
        var reset = CpuState.CreateResetState(bus);
        Enqueue(reset.ProgramCounter, 0x30, true);
        foreach (var vector in new ushort[] { 0xFFF4, 0xFFFA, 0xFFFE, 0xFFE4, 0xFFE6, 0xFFE8, 0xFFEA, 0xFFEE })
        {
            var target = (uint)bus.ReadWordInBank(vector);
            if (vector >= 0xFFF0) Enqueue(target, 0x30, true);
            else AllModes(target, includeEmulation: false);
        }
        foreach (var table in dispatchers.StaticPointerTables)
            ExpandTable(table.Address, table.Width, 0x30, emulation: true, table.EntryCount, allModes: true);

        while (pending.TryDequeue(out var key))
        {
            if (!visited.Add(key)) continue;
            EnqueueAllModes(key.Address);
            if (visited.Count > 150_000)
                throw new InvalidDataException("Static discovery exceeded 150,000 entries; inspect the generated control-flow report.");
            DecodedCpuBlock block;
            try
            {
                block = NativeBlockTranslator.Decode(bus, new CpuState
                {
                    ProgramBank = (byte)(key.Address >> 16),
                    ProgramCounter = (ushort)key.Address,
                    Status = key.Mode,
                    EmulationMode = key.Emulation,
                });
            }
            catch (NotSupportedException)
            {
                UnresolvedTransfers.Add($"Non-ROM instruction boundary at ${key.Address:X6}.");
                continue;
            }
            AddObserved(block);
            var last = block.Instructions[^1];
            var bank = block.StartAddress & 0xFF0000;
            var next = bank | last.NextProgramCounter;
            var mode = key.Mode;
            var emulation = key.Emulation;
            void Next(uint target) => Enqueue(target, mode, emulation);
            switch (last.Operation)
            {
                case CpuOperation.Stop:
                case CpuOperation.ReturnFromSubroutine:
                case CpuOperation.ReturnFromSubroutineLong:
                case CpuOperation.ReturnFromInterrupt:
                    break;
                case CpuOperation.JumpAbsolute: Next(bank | (ushort)last.Operand); break;
                case CpuOperation.JumpLong:
                    Next(last.Operand);
                    if (dispatchers.TryGetTailDispatchTable(block, bus.CanonicalCodeAddress(last.Operand),
                            out var tailTable, out var tailWidth))
                        ExpandTable(tailTable, tailWidth, mode, emulation);
                    break;
                case CpuOperation.CallAbsolute:
                    Next(bank | (ushort)last.Operand); Next(next); break;
                case CpuOperation.CallLong:
                    Next(last.Operand);
                    var width = dispatchers.PointerWidth(bus.CanonicalCodeAddress(last.Operand));
                    if (width != 0) ExpandTable(next, width, 0x30, emulation);
                    else Next(next);
                    break;
                case CpuOperation.BranchAlways:
                    Next(bank | unchecked((ushort)(last.NextProgramCounter + (sbyte)last.Operand))); break;
                case CpuOperation.BranchLong:
                    Next(bank | unchecked((ushort)(last.NextProgramCounter + (short)last.Operand))); break;
                case CpuOperation.BranchPlus:
                case CpuOperation.BranchMinus:
                case CpuOperation.BranchOverflowClear:
                case CpuOperation.BranchOverflowSet:
                case CpuOperation.BranchCarryClear:
                case CpuOperation.BranchCarrySet:
                case CpuOperation.BranchNotEqual:
                case CpuOperation.BranchEqual:
                    Next(bank | unchecked((ushort)(last.NextProgramCounter + (sbyte)last.Operand)));
                    Next(next); break;
                case CpuOperation.ResetStatusBits:
                    Enqueue(next, (byte)(mode & ~(byte)last.Operand), emulation); break;
                case CpuOperation.SetStatusBits:
                    Enqueue(next, (byte)(mode | ((byte)last.Operand & 0x30)), emulation); break;
                case CpuOperation.PullStatus:
                    if (emulation) Enqueue(next, 0x30, true);
                    else AllModes(next, includeEmulation: false);
                    break;
                case CpuOperation.ExchangeCarryAndEmulation:
                    var preceding = block.Instructions.Count > 1 ? block.Instructions[^2].Operation : (CpuOperation?)null;
                    if (preceding == CpuOperation.ClearCarry) Enqueue(next, mode, false);
                    else if (preceding == CpuOperation.SetCarry) Enqueue(next, 0x30, true);
                    else AllModes(next);
                    break;
                case CpuOperation.CallAbsoluteIndexedIndirect:
                    Next(next);
                    goto case CpuOperation.JumpAbsoluteIndexedIndirect;
                case CpuOperation.JumpAbsoluteIndexedIndirect:
                    if (ExpandTable(bank | (ushort)last.Operand, 2, mode, emulation) == 0)
                        UnresolvedTransfers.Add($"${block.StartAddress:X6}: {last.Operation} (RAM or unbounded table)");
                    break;
                case CpuOperation.JumpIndirect:
                    if (bus.IsCartridgeRomAddress((ushort)last.Operand))
                        Next(bank | bus.ReadWordInBank((ushort)last.Operand));
                    else UnresolvedTransfers.Add($"${block.StartAddress:X6}: {last.Operation} (mutable pointer)");
                    break;
                case CpuOperation.JumpIndirectLong:
                    if (last.Operand >= 0x8000 && last.Operand <= 0xFFFD)
                        Next((uint)(bus.PeekCodeByte(last.Operand) | bus.PeekCodeByte(last.Operand + 1) << 8
                            | bus.PeekCodeByte(last.Operand + 2) << 16));
                    else UnresolvedTransfers.Add($"${block.StartAddress:X6}: {last.Operation} (mutable pointer)");
                    break;
                case CpuOperation.Break:
                case CpuOperation.Cop:
                    // Interrupt vectors were seeded above. A software interrupt does
                    // not provide a normal fallthrough edge into following bytes.
                    break;
                default: Next(next); break;
            }
        }
    }

    private int ExpandTable(uint table, int width, byte mode, bool emulation, int maximumEntries = 256, bool allModes = false)
    {
        if (!bus.IsCartridgeRomAddress(table)) return 0;
        var bank = table & 0xFF0000;
        var boundary = bank + 0x10000u;
        // The nearest forward target bounds a table followed by its code. Otherwise
        // stop at an invalid pointer or the 256-entry cap; record the inference.
        var count = 0;
        for (var index = 0; index < maximumEntries; index++)
        {
            var entry = table + (uint)(index * width);
            if (entry + width > boundary || (entry & 0xFFFF) + width > 0x10000) break;
            uint target = (uint)(bus.PeekCodeByte(entry) | bus.PeekCodeByte(entry + 1) << 8);
            target |= width == 3 ? (uint)bus.PeekCodeByte(entry + 2) << 16 : bank;
            if (!bus.IsCartridgeRomAddress(target) || target == 0xFFFFFF || target >= table && target < entry + width) break;
            if (target > table && (target & 0xFF0000) == bank) boundary = Math.Min(boundary, target);
            if (allModes) AllModes(target);
            else Enqueue(target, mode, emulation);
            count++;
        }
        if (count != 0) InferredPointerTables.Add($"${table:X6}: {count} contiguous {width}-byte ROM pointers");
        return count;
    }

    private void AllModes(uint address, bool includeEmulation = true)
    {
        foreach (var mode in new byte[] { 0, 0x10, 0x20, 0x30 }) Enqueue(address, mode, false);
        if (includeEmulation) Enqueue(address, 0x30, true);
    }

    private void EnqueueAllModes(uint address) => AllModes(address);

    private void Enqueue(uint address, byte mode, bool emulation)
    {
        if (!bus.IsCartridgeRomAddress(address)) return;
        var key = (bus.CanonicalCodeAddress(address), emulation ? (byte)0x30 : (byte)(mode & 0x30), emulation);
        if (!visited.Contains(key)) pending.Enqueue(key);
    }
}
