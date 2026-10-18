using System.Text;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Recompiler;

internal static class RomEmitter
{
    public static void Write(SnesRom rom, IReadOnlyList<DecodedCpuBlock> blocks,
        HashSet<(ushort Address, byte Opcode)> audioEntries, string directory)
    {
        Directory.CreateDirectory(directory);
        var lowerer = new OperationLowerer();
        File.WriteAllText(Path.Combine(directory, "CompiledCpuOperations.g.cs"), lowerer.EmitCpuMethods(blocks));
        File.WriteAllText(Path.Combine(directory, "CompiledRomProgram.g.cs"), EmitMain(rom, blocks));
        File.WriteAllText(Path.Combine(directory, "CompiledAudioOperations.g.cs"), lowerer.EmitAudioMethods(audioEntries.Select(e => e.Opcode)));
        File.WriteAllText(Path.Combine(directory, "CompiledAudioProgram.g.cs"), EmitAudio(audioEntries));
    }

    private static ulong Key(DecodedCpuBlock block)
        => block.StartAddress | ((ulong)block.StatusMode << 24) | (block.EmulationMode ? 1UL << 32 : 0);

    private static string EmitMain(SnesRom rom, IReadOnlyList<DecodedCpuBlock> blocks)
    {
        var bus = new SnesMemoryBus(rom);
        var source = new StringBuilder("#nullable enable\nusing Smw2Pc.Core.Machine;\nusing Smw2Pc.Core.Rom;\nusing Smw2Pc.Core.Recompilation;\nnamespace Smw2Pc.Core.Cpu;\ninternal static class CompiledRomProgram\n{\n");
        source.AppendLine("private delegate int CompiledBlock(CpuState state, SnesMemoryBus memoryBus, int budget);");
        source.AppendLine("private sealed record Entry(byte[]? ExpectedBytes, CompiledBlock Code);");
        source.AppendLine("private static readonly Dictionary<ulong, Entry[]> Entries = CreateEntries();");
        source.AppendLine($"public static void ValidateRom(SnesRom rom) {{ if (rom.Sha256 != \"{rom.Sha256}\") throw new InvalidDataException(\"This compiled player requires its original ROM.\"); }}");
        source.AppendLine("public static int Execute(CpuState state, SnesMemoryBus memoryBus, int budget) {");
        source.AppendLine("uint address = ((uint)state.ProgramBank << 16) | state.ProgramCounter;");
        source.AppendLine("var canonical = memoryBus.CanonicalCodeAddress(address);");
        source.AppendLine("ulong key = canonical | ((ulong)(state.EmulationMode ? 0x30 : state.Status & 0x30) << 24) | (state.EmulationMode ? 1UL << 32 : 0);");
        source.AppendLine("if (Entries.TryGetValue(key, out var entries)) foreach (var entry in entries) {");
        source.AppendLine("bool matches = true; if (entry.ExpectedBytes is { } bytes) for (var i = 0; i < bytes.Length; i++) if (memoryBus.PeekCodeByte((address & 0xFF0000) | (ushort)(state.ProgramCounter + i)) != bytes[i]) { matches = false; break; }");
        source.AppendLine("if (matches) return entry.Code(state, memoryBus, budget); }");
        source.AppendLine("throw new MissingCompiledCodeException($\"No compiled 65C816 code at ${address:X6} (canonical ${canonical:X6}, mode ${state.Status & 0x30:X2}, E={state.EmulationMode}, bytes {memoryBus.PeekCodeByte(address):X2} {memoryBus.PeekCodeByte((address & 0xFF0000) | (ushort)(state.ProgramCounter + 1)):X2}). Rebuild with a profile or entry point covering this path.\"); }");

        var groups = blocks.Select((b, i) => (Block: b, Index: i)).GroupBy(p => Key(p.Block)).ToArray();
        source.AppendLine("private static Dictionary<ulong, Entry[]> CreateEntries() { var entries = new Dictionary<ulong, Entry[]>();");
        for (var part = 0; part * 256 < groups.Length; part++) source.AppendLine($"AddEntries{part}(entries);");
        source.AppendLine("return entries; }");
        for (var part = 0; part * 256 < groups.Length; part++)
        {
            source.AppendLine($"private static void AddEntries{part}(Dictionary<ulong, Entry[]> entries) {{");
            foreach (var group in groups.Skip(part * 256).Take(256))
            {
                source.Append($"entries.Add(0x{group.Key:X}UL, [");
                foreach (var item in group)
                {
                    var guard = bus.IsCartridgeRomAddress(item.Block.StartAddress)
                        ? "null" : "new byte[] { " + string.Join(",", item.Block.SourceBytes.Select(b => $"0x{b:X2}")) + " }";
                    source.Append($"new Entry({guard}, B{item.Index}),");
                }
                source.AppendLine("]);");
            }
            source.AppendLine("}");
        }

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            source.AppendLine($"// ${block.StartAddress:X6}, M/X=${block.StatusMode:X2}, E={block.EmulationMode}");
            source.AppendLine($"private static int B{index}(CpuState state, SnesMemoryBus memoryBus, int budget) {{");
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                var instruction = block.Instructions[instructionIndex];
                source.AppendLine($"CpuInstructionExecutor.{OperationLowerer.MethodName(instruction, block.StatusMode, block.EmulationMode)}(state, memoryBus, 0x{instruction.Operand:X}u, 0x{instruction.NextProgramCounter:X4});");
                var sequential = !NativeBlockTranslator.EndsBlock(instruction.Operation)
                    || instruction.Operation is CpuOperation.ResetStatusBits or CpuOperation.SetStatusBits
                        or CpuOperation.PullStatus or CpuOperation.ExchangeCarryAndEmulation;
                if (sequential) source.AppendLine($"state.ProgramCounter = 0x{instruction.NextProgramCounter:X4};");
                if (instructionIndex + 1 < block.InstructionCount)
                    source.AppendLine($"if (budget == {instructionIndex + 1}) return {instructionIndex + 1};");
            }
            source.AppendLine($"return {block.InstructionCount}; }}");
        }
        return source.AppendLine("}").ToString();
    }

    private static string EmitAudio(HashSet<(ushort Address, byte Opcode)> audioEntries)
    {
        var entries = audioEntries.OrderBy(e => e.Address).ThenBy(e => e.Opcode).ToArray();
        var source = new StringBuilder("using Smw2Pc.Core.Recompilation;\nnamespace Smw2Pc.Core.Audio;\ninternal sealed partial class Spc700Cpu\n{\n");
        source.AppendLine("private static readonly Dictionary<uint, Func<Spc700Cpu, int>> TranslatedAudioEntries = CreateAudioEntries();");
        source.AppendLine("private int ExecuteTranslatedStep() { uint key = ((uint)ProgramCounter << 8) | (byte)bus.ReadSpcMemory(ProgramCounter); if (TranslatedAudioEntries.TryGetValue(key, out var code)) return code(this); throw new MissingCompiledCodeException($\"No compiled SPC700 code at ${ProgramCounter:X4}, opcode ${(byte)key:X2}. Rebuild with an audio profile covering this path.\"); }");
        source.AppendLine("private static Dictionary<uint, Func<Spc700Cpu, int>> CreateAudioEntries() { var entries = new Dictionary<uint, Func<Spc700Cpu, int>>();");
        for (var part = 0; part * 256 < entries.Length; part++) source.AppendLine($"AddAudio{part}(entries);");
        source.AppendLine("return entries; }");
        for (var part = 0; part * 256 < entries.Length; part++)
        {
            source.AppendLine($"private static void AddAudio{part}(Dictionary<uint, Func<Spc700Cpu, int>> entries) {{");
            foreach (var entry in entries.Skip(part * 256).Take(256))
                source.AppendLine($"entries.Add(0x{((uint)entry.Address << 8) | entry.Opcode:X6}u, static cpu => cpu.TranslatedAudio_{entry.Opcode:X2}());");
            source.AppendLine("}");
        }
        return source.AppendLine("}").ToString();
    }
}
