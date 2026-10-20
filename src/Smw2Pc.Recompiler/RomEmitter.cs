using System.Text;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Recompiler;

internal static class RomEmitter
{
    private const int BlocksPerGeneratedClass = 1_000;
    private const int EntriesPerGeneratedMethod = 16;

    public static void Write(SnesRom rom, IReadOnlyList<DecodedCpuBlock> blocks,
        HashSet<(ushort Address, byte Opcode)> audioEntries, string directory)
    {
        Directory.CreateDirectory(directory);
        var lowerer = new OperationLowerer();
        File.WriteAllText(Path.Combine(directory, "CompiledCpuOperations.g.cs"), lowerer.EmitCpuMethods(blocks));
        File.WriteAllText(Path.Combine(directory, "CompiledRomProgram.g.cs"), EmitMain(rom, blocks));
        File.WriteAllText(Path.Combine(directory, "CompiledAudioOperations.g.cs"), lowerer.EmitAudioMethods(Enumerable.Range(0, 256).Select(i => (byte)i)));
        File.WriteAllText(Path.Combine(directory, "CompiledAudioProgram.g.cs"), EmitAudio(audioEntries));
    }

    private static ulong Key(uint address, byte statusMode, bool emulationMode)
        => address | ((ulong)statusMode << 24) | (emulationMode ? 1UL << 32 : 0);

    private static ulong Key(DecodedCpuBlock block)
        => Key(block.StartAddress, block.StatusMode, block.EmulationMode);

    internal static string EmitMain(SnesRom rom, IReadOnlyList<DecodedCpuBlock> blocks)
    {
        var bus = new SnesMemoryBus(rom);
        var source = new StringBuilder("#nullable enable\nusing Smw2Pc.Core.Machine;\nusing Smw2Pc.Core.Rom;\nusing Smw2Pc.Core.Recompilation;\nnamespace Smw2Pc.Core.Cpu;\ninternal delegate int CompiledRomBlock(CpuState state, SnesMemoryBus memoryBus, int budget, int startIndex);\ninternal static class CompiledRomProgram\n{\n");
        source.AppendLine("private sealed record Entry(byte[]? ExpectedBytes, CompiledRomBlock Code, int StartIndex, bool Terminates);");
        source.AppendLine("private static readonly Dictionary<ulong, Entry[]> Entries = CreateEntries();");
        source.AppendLine($"public static void ValidateRom(SnesRom rom) {{ if (rom.Sha256 != \"{rom.Sha256}\") throw new InvalidDataException(\"This compiled player requires its original ROM.\"); }}");
        source.AppendLine("public static int Execute(CpuState state, SnesMemoryBus memoryBus, int budget) {");
        source.AppendLine("int total = 0; while (true) {");
        source.AppendLine("uint address = ((uint)state.ProgramBank << 16) | state.ProgramCounter;");
        source.AppendLine("var canonical = memoryBus.CanonicalCodeAddress(address);");
        source.AppendLine("ulong key = canonical | ((ulong)(state.EmulationMode ? 0x30 : state.Status & 0x30) << 24) | (state.EmulationMode ? 1UL << 32 : 0);");
        source.AppendLine("bool dispatched = false; if (Entries.TryGetValue(key, out var entries)) foreach (var entry in entries) {");
        source.AppendLine("bool matches = true; if (entry.ExpectedBytes is { } bytes) for (var i = 0; i < bytes.Length; i++) if (memoryBus.PeekCodeByte((address & 0xFF0000) | (ushort)(state.ProgramCounter + i)) != bytes[i]) { matches = false; break; }");
        source.AppendLine("if (matches) { int count = entry.Code(state, memoryBus, budget - total, entry.StartIndex); total += count; if (total == budget || entry.Terminates) return total; dispatched = true; break; } }");
        source.AppendLine("if (!dispatched) throw new MissingCompiledCodeException($\"No compiled 65C816 code at ${address:X6} (canonical ${canonical:X6}, mode ${state.Status & 0x30:X2}, E={state.EmulationMode}, bytes {memoryBus.PeekCodeByte(address):X2} {memoryBus.PeekCodeByte((address & 0xFF0000) | (ushort)(state.ProgramCounter + 1)):X2}). Rebuild with a profile or entry point covering this path.\"); }");
        source.AppendLine("}");

        var entryGroups = new Dictionary<ulong, List<(int BlockIndex, int StartIndex, byte[]? Guard, bool Terminates, int Remaining)>>();
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var isRom = bus.IsCartridgeRomAddress(block.StartAddress);
            uint address = block.StartAddress;
            for (var offset = 0; offset < block.Instructions.Count; offset++)
            {
                var key = Key(address, block.StatusMode, block.EmulationMode);
                var guard = isRom ? null : block.SourceBytes;
                var terminates = !isRom || NativeBlockTranslator.EndsBlock(block.Instructions[^1].Operation);
                if (!entryGroups.TryGetValue(key, out var entriesForKey)) entryGroups.Add(key, entriesForKey = []);
                entriesForKey.Add((index, offset, guard, terminates, block.Instructions.Count - offset));
                address = ((block.StartAddress & 0xFF0000) | block.Instructions[offset].NextProgramCounter);
                address = bus.CanonicalCodeAddress(address);
            }
        }
        var groups = entryGroups.Select(group => (group.Key, Entries: group.Value
            .Where(candidate => candidate.Guard is not null || candidate.Remaining == group.Value.Where(other => other.Guard is null).Select(other => other.Remaining).DefaultIfEmpty(0).Max())
            .DistinctBy(candidate => (candidate.BlockIndex, candidate.StartIndex, Guard: candidate.Guard is null ? "ROM" : Convert.ToHexString(candidate.Guard)))
            .ToArray())).ToArray();
        source.AppendLine("private static Dictionary<ulong, Entry[]> CreateEntries() { var entries = new Dictionary<ulong, Entry[]>();");
        for (var part = 0; part * EntriesPerGeneratedMethod < groups.Length; part++) source.AppendLine($"AddEntries{part}(entries);");
        source.AppendLine("return entries; }");
        for (var part = 0; part * EntriesPerGeneratedMethod < groups.Length; part++)
        {
            source.AppendLine($"private static void AddEntries{part}(Dictionary<ulong, Entry[]> entries) {{");
            foreach (var group in groups.Skip(part * EntriesPerGeneratedMethod).Take(EntriesPerGeneratedMethod))
            {
                source.Append($"entries.Add(0x{group.Key:X}UL, [");
                foreach (var item in group.Entries)
                {
                    var guard = item.Guard is null
                        ? "null" : "new byte[] { " + string.Join(",", item.Guard.Select(b => $"0x{b:X2}")) + " }";
                    source.Append($"new Entry({guard}, new CompiledRomBlock(CompiledRomBlocks{item.BlockIndex / BlocksPerGeneratedClass}.B{item.BlockIndex}), {item.StartIndex}, {item.Terminates.ToString().ToLowerInvariant()}),");
                }
                source.AppendLine("]);");
            }
            source.AppendLine("}");
        }

        source.AppendLine("}");
        for (var firstBlock = 0; firstBlock < blocks.Count; firstBlock += BlocksPerGeneratedClass)
        {
            var lastBlock = Math.Min(firstBlock + BlocksPerGeneratedClass, blocks.Count);
            source.AppendLine($"internal static class CompiledRomBlocks{firstBlock / BlocksPerGeneratedClass} {{");
            for (var index = firstBlock; index < lastBlock; index++)
            {
                var block = blocks[index];
                source.AppendLine($"// ${block.StartAddress:X6}, M/X=${block.StatusMode:X2}, E={block.EmulationMode}");
                source.AppendLine($"internal static int B{index}(CpuState state, SnesMemoryBus memoryBus, int budget, int startIndex) {{ int executed = 0; switch (startIndex) {{");
                for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
                    source.AppendLine($"case {instructionIndex}: goto I{instructionIndex};");
                source.AppendLine("default: throw new ArgumentOutOfRangeException(nameof(startIndex)); }");
                var instructionAddress = block.StartAddress;
                for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
                {
                    var instruction = block.Instructions[instructionIndex];
                    source.AppendLine($"I{instructionIndex}:");
                    var operand = bus.Widescreen.TryGetGraphicsOperand(instructionAddress, instruction.Operand, out var extended)
                        ? $"memoryBus.Widescreen.GraphicsOperand(0x{instruction.Operand:X}u, 0x{extended:X}u)"
                        : $"0x{instruction.Operand:X}u";
                    source.AppendLine($"CpuInstructionExecutor.{OperationLowerer.MethodName(instruction, block.StatusMode, block.EmulationMode)}(state, memoryBus, {operand}, 0x{instruction.NextProgramCounter:X4});");
                    instructionAddress = (block.StartAddress & 0xFF0000) | instruction.NextProgramCounter;
                    var sequential = !NativeBlockTranslator.EndsBlock(instruction.Operation)
                        || instruction.Operation is CpuOperation.ResetStatusBits or CpuOperation.SetStatusBits
                            or CpuOperation.PullStatus or CpuOperation.ExchangeCarryAndEmulation;
                    if (sequential) source.AppendLine($"state.ProgramCounter = 0x{instruction.NextProgramCounter:X4};");
                    source.AppendLine("executed++;");
                    source.AppendLine("if (executed == budget) return executed;");
                }
                source.AppendLine("return executed; }");
            }
            source.AppendLine("}");
        }
        return source.ToString();
    }

    private static string EmitAudio(HashSet<(ushort Address, byte Opcode)> audioEntries)
    {
        var entries = audioEntries.OrderBy(e => e.Address).ThenBy(e => e.Opcode).ToArray();
        var source = new StringBuilder("using Smw2Pc.Core.Recompilation;\nnamespace Smw2Pc.Core.Audio;\ninternal sealed partial class Spc700Cpu\n{\n");
        source.AppendLine("private static readonly Func<Spc700Cpu, int>[] AudioMethods = CreateAudioMethods();");
        source.AppendLine("private static Func<Spc700Cpu, int>[] CreateAudioMethods() => [");
        for (var opcode = 0; opcode < 256; opcode++)
            source.AppendLine($"static cpu => cpu.TranslatedAudio_{opcode:X2}(),");
        source.AppendLine("];\n");
        source.AppendLine("private static readonly Dictionary<uint, Func<Spc700Cpu, int>> TranslatedAudioEntries = CreateAudioEntries();");
        source.AppendLine("private int ExecuteTranslatedStep() { uint key = ((uint)ProgramCounter << 8) | (byte)bus.ReadSpcMemory(ProgramCounter); if (TranslatedAudioEntries.TryGetValue(key, out var code)) return code(this); throw new MissingCompiledCodeException($\"No compiled SPC700 code at ${ProgramCounter:X4}, opcode ${(byte)key:X2}. Rebuild with an audio profile covering this path.\"); }");
        source.AppendLine("private static Dictionary<uint, Func<Spc700Cpu, int>> CreateAudioEntries() { var entries = new Dictionary<uint, Func<Spc700Cpu, int>>();");
        for (var part = 0; part * 256 < entries.Length; part++) source.AppendLine($"AddAudio{part}(entries);");
        source.AppendLine("return entries; }");
        for (var part = 0; part * 256 < entries.Length; part++)
        {
            source.AppendLine($"private static void AddAudio{part}(Dictionary<uint, Func<Spc700Cpu, int>> entries) {{");
            foreach (var entry in entries.Skip(part * 256).Take(256))
                source.AppendLine($"entries.Add(0x{((uint)entry.Address << 8) | entry.Opcode:X6}u, AudioMethods[0x{entry.Opcode:X2}]);");
            source.AppendLine("}");
        }
        return source.AppendLine("}").ToString();
    }
}
