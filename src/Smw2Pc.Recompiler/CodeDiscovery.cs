using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Recompiler;

internal sealed class CodeDiscovery(SnesRom rom)
{
    private readonly SnesMemoryBus bus = new(rom);
    private readonly Dictionary<string, DecodedCpuBlock> blocks = [];
    private readonly Queue<(uint Address, byte Mode, bool Emulation)> pending = new();
    private readonly HashSet<(uint Address, byte Mode, bool Emulation)> visited = [];
    public HashSet<string> UnresolvedTransfers { get; } = [];
    public IReadOnlyList<DecodedCpuBlock> Blocks => blocks.Values
        .OrderBy(b => b.StartAddress).ThenBy(b => b.StatusMode).ThenBy(b => b.EmulationMode)
        .ThenBy(b => Convert.ToHexString(b.SourceBytes)).ToArray();

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

        while (pending.TryDequeue(out var key))
        {
            if (!visited.Add(key)) continue;
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
                case CpuOperation.JumpLong: Next(last.Operand); break;
                case CpuOperation.CallAbsolute:
                    Next(bank | (ushort)last.Operand); Next(next); break;
                case CpuOperation.CallLong:
                    Next(last.Operand); Next(next); break;
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
                    AllModes(next, includeEmulation: emulation); break;
                case CpuOperation.ExchangeCarryAndEmulation:
                    AllModes(next); break;
                case CpuOperation.CallAbsoluteIndexedIndirect:
                    Next(next);
                    goto case CpuOperation.JumpIndirect;
                case CpuOperation.JumpIndirect:
                case CpuOperation.JumpAbsoluteIndexedIndirect:
                case CpuOperation.JumpIndirectLong:
                    UnresolvedTransfers.Add($"${block.StartAddress:X6}: {last.Operation} (runtime target, profiled separately)");
                    break;
                case CpuOperation.Break:
                case CpuOperation.Cop:
                    AllModes(next); break;
                default: Next(next); break;
            }
        }
    }

    private void AllModes(uint address, bool includeEmulation = true)
    {
        foreach (var mode in new byte[] { 0, 0x10, 0x20, 0x30 }) Enqueue(address, mode, false);
        if (includeEmulation) Enqueue(address, 0x30, true);
    }

    private void Enqueue(uint address, byte mode, bool emulation)
    {
        if (!bus.IsCartridgeRomAddress(address)) return;
        var key = (bus.CanonicalCodeAddress(address), emulation ? (byte)0x30 : (byte)(mode & 0x30), emulation);
        if (!visited.Contains(key)) pending.Enqueue(key);
    }
}
