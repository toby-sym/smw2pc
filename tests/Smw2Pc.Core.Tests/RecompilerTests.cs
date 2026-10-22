using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Recompilation;
using Smw2Pc.Core.Rom;
using Smw2Pc.Recompiler;

namespace Smw2Pc.Core.Tests;

[TestClass]
public sealed class RecompilerTests
{
    [TestMethod]
    public void AllOpcodeAndWidthSpecializationsMatchReferenceSemantics()
    {
        var blocks = new List<DecodedCpuBlock>();
        foreach (var emulation in new[] { false, true })
            foreach (var mode in new byte[] { 0, 0x10, 0x20, 0x30 })
            {
                if (emulation && mode != 0x30) continue;
                for (var opcode = 0; opcode < 256; opcode++)
                {
                    var bus = new SnesMemoryBus(Rom([(byte)opcode, 0x20, 0x01, 0x00]));
                    blocks.Add(NativeBlockTranslator.Decode(bus, State(mode, emulation), 1));
                }
            }
        var lowerer = new OperationLowerer();
        var source = lowerer.EmitCpuMethods(blocks);
        Assert.IsFalse(source.Contains("CpuInstructionExecutor.Execute(", StringComparison.Ordinal));
        var assembly = Compile(source);
        var translated = assembly.GetType("Smw2Pc.Core.Cpu.CpuInstructionExecutor", throwOnError: true)!;

        foreach (var block in blocks)
        {
            var instruction = block.Instructions[0];
            foreach (var decimalMode in new[] { false, true })
            {
                var referenceState = State(block.StatusMode, block.EmulationMode);
                var translatedState = State(block.StatusMode, block.EmulationMode);
                if (decimalMode) { referenceState.Status |= 8; translatedState.Status |= 8; }
                var referenceBus = Bus();
                var translatedBus = Bus();
                CpuInstructionExecutor.Execute(referenceState, referenceBus, instruction.Operation,
                    instruction.AddressingMode, instruction.Operand, instruction.NextProgramCounter);
                var method = translated.GetMethod(OperationLowerer.MethodName(instruction, block.StatusMode, block.EmulationMode),
                    BindingFlags.Static | BindingFlags.NonPublic)!;
                method.Invoke(null, [translatedState, translatedBus, instruction.Operand, instruction.NextProgramCounter]);
                var reference = ProofPlayback.Capture(new NativeCpuRunnerForState(referenceBus, referenceState).Runner, 0);
                var actual = ProofPlayback.Capture(new NativeCpuRunnerForState(translatedBus, translatedState).Runner, 0);
                Assert.AreEqual(reference.StateSha256, actual.StateSha256,
                    $"{instruction.Operation}/{instruction.AddressingMode}, mode {block.StatusMode:X2}, E={block.EmulationMode}, decimal={decimalMode}");
            }
        }
    }

    [TestMethod]
    public void DiscoveryFollowsBackwardBraWithSignedByteAndTracksRepSepWidths()
    {
        // BRA $8000 must not be interpreted as a positive 16-bit displacement.
        var discovery = new CodeDiscovery(Rom([0x18, 0xFB, 0xC2, 0x30, 0xA9, 0x34, 0x12, 0xE2, 0x20, 0x80, 0xF5]));
        discovery.Analyze();
        Assert.IsTrue(discovery.Blocks.Any(b => b.StartAddress == 0x008000));
        Assert.IsTrue(discovery.Blocks.Any(b => b.StartAddress == 0x008004 && b.StatusMode == 0 && !b.EmulationMode));
        Assert.IsFalse(discovery.Blocks.Any(b => b.StartAddress == 0x008100));
    }

    [TestMethod]
    public void ExplicitEntryPointsCompileUnobservedNativeAndEmulationModes()
    {
        var program = new byte[0x8000];
        program[0x80] = 0xC2; program[0x81] = 0x30; program[0x82] = 0xDB;
        var discovery = new CodeDiscovery(Rom(program));

        discovery.AddEntryPoint(0x008080);
        discovery.Analyze();

        foreach (var mode in new byte[] { 0x00, 0x10, 0x20, 0x30 })
            Assert.IsTrue(discovery.Blocks.Any(block => block.StartAddress == 0x008080
                && block.StatusMode == mode && !block.EmulationMode), $"Missing native M/X=${mode:X2} entry.");
        Assert.IsTrue(discovery.Blocks.Any(block => block.StartAddress == 0x008080
            && block.StatusMode == 0x30 && block.EmulationMode));
        Assert.IsTrue(discovery.Blocks.Any(block => block.StartAddress == 0x008082
            && block.StatusMode == 0 && !block.EmulationMode));
    }

    [TestMethod]
    public void SmwInlineDispatcherFindsTargetsAndDoesNotDecodeThePointerTableAsCode()
    {
        var program = new byte[0x800];
        new byte[] { 0x18, 0xFB, 0x22, 0xDF, 0x86, 0x00, 0x10, 0x80, 0x20, 0x80 }.CopyTo(program, 0);
        program[0x10] = 0xDB; program[0x20] = 0xDB;
        Convert.FromHexString("84037A8400C23029FF000AA8688501C8B7008500E230A403DC0000").CopyTo(program, 0x6DF);
        var discovery = new CodeDiscovery(Rom(program));
        discovery.Analyze();
        Assert.AreEqual(1, discovery.RecognizedSmwDispatchers);
        Assert.IsTrue(discovery.Blocks.Any(b => b.StartAddress == 0x8010));
        Assert.IsTrue(discovery.Blocks.Any(b => b.StartAddress == 0x8020));
        Assert.IsFalse(discovery.Blocks.Any(b => b.StartAddress == 0x8006));
    }

    [TestMethod]
    public void StaticDiscoveryIncludesHandlersFromSmwBankedRoutineTable()
    {
        var program = new byte[0x80000];
        const int tableOffset = 0x0D * 0x8000 + (0xA13F - 0x8000);
        for (var index = 0; index < 240; index++)
        {
            var target = index switch
            {
                7 => 0x0DA64D,
                54 => 0x0DA68E,
                _ => 0x0DA57B,
            };
            var entryOffset = tableOffset + (index * 3);
            program[entryOffset] = (byte)target;
            program[entryOffset + 1] = (byte)(target >> 8);
            program[entryOffset + 2] = (byte)(target >> 16);
        }
        program[tableOffset + (240 * 3)] = 0xE2;
        program[tableOffset + (240 * 3) + 1] = 0x30;
        program[tableOffset + (240 * 3) + 2] = 0x20;
        program[0x0D * 0x8000 + (0xA68E - 0x8000)] = 0xDB;

        var rom = Rom(program);
        var discovery = new CodeDiscovery(rom);
        discovery.Analyze();

        foreach (var mode in new byte[] { 0x00, 0x10, 0x20, 0x30 })
            Assert.IsTrue(discovery.Blocks.Any(block => block.StartAddress == 0x0DA68E
                && block.StatusMode == mode && !block.EmulationMode), $"Missing native M/X=${mode:X2} entry.");
        Assert.IsTrue(discovery.Blocks.Any(block => block.StartAddress == 0x0DA68E
            && block.StatusMode == 0x30 && block.EmulationMode));
        Assert.Contains("$0DA13F: 240 contiguous 3-byte ROM pointers", discovery.InferredPointerTables);

        var blocks = discovery.Blocks;
        var assembly = Compile(new OperationLowerer().EmitCpuMethods(blocks), RomEmitter.EmitMain(rom, blocks));
        var execute = assembly.GetType("Smw2Pc.Core.Cpu.CompiledRomProgram")!
            .GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)!;
        foreach (var emulation in new[] { false, true })
        {
            var state = new CpuState { ProgramBank = 0x0D, ProgramCounter = 0xA68E, Status = 0x30, EmulationMode = emulation };
            Assert.AreEqual(1, execute.Invoke(null, [state, new SnesMemoryBus(rom), 1]));
            Assert.IsTrue(state.Halted);
        }
    }

    [TestMethod]
    public void CompiledBlocksHonorBudgetsAndMirrorsAndRejectChangedRamCode()
    {
        var rom = Rom([0xA9, 0x11, 0x8D, 0x00, 0x02, 0xA9, 0x22, 0xDB]);
        var discovery = new CodeDiscovery(rom);
        var ramBus = new SnesMemoryBus(rom);
        ramBus.WriteByte(0x7E0100, 0xA9); ramBus.WriteByte(0x7E0101, 0x77);
        var ramState = new CpuState { ProgramBank = 0x7E, ProgramCounter = 0x0100, Status = 0x30 };
        discovery.AddObserved(NativeBlockTranslator.Decode(ramBus, ramState, 1));
        discovery.Analyze();
        var blocks = discovery.Blocks;
        var assembly = Compile(new OperationLowerer().EmitCpuMethods(blocks), RomEmitter.EmitMain(rom, blocks));
        var execute = assembly.GetType("Smw2Pc.Core.Cpu.CompiledRomProgram")!.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)!;
        foreach (var bank in new byte[] { 0, 0x80 })
        {
            var reference = new NativeCpuRunner(new SnesMemoryBus(rom));
            var compiled = new NativeCpuRunner(new SnesMemoryBus(rom));
            reference.State.ProgramBank = bank; compiled.State.ProgramBank = bank;
            for (var step = 0; step < 4; step++)
            {
                reference.Run(1);
                var count = (int)execute.Invoke(null, [compiled.State, compiled.MemoryBus, 1])!;
                Assert.AreEqual(1, count);
                compiled.MemoryBus.Hardware.AdvanceCpuInstructions(compiled.MemoryBus, count);
                Assert.AreEqual(ProofPlayback.Capture(reference, step).StateSha256, ProofPlayback.Capture(compiled, step).StateSha256);
            }
        }
        Assert.AreEqual(1, execute.Invoke(null, [ramState, ramBus, 32]));
        Assert.AreEqual((ushort)0x77, ramState.Accumulator);
        ramState.ProgramCounter = 0x0100;
        ramBus.WriteByte(0x7E0101, 0x66);
        var failure = Assert.ThrowsExactly<TargetInvocationException>(() => execute.Invoke(null, [ramState, ramBus, 32]));
        Assert.IsInstanceOfType<MissingCompiledCodeException>(failure.InnerException);
    }

    [TestMethod]
    public void CompiledExecutionResumesAcrossStaticBlockLimitWithMatchingHardwareTiming()
    {
        var program = Enumerable.Repeat((byte)0xEA, 40).Append((byte)0xDB).ToArray();
        var rom = Rom(program);
        var bus = new SnesMemoryBus(rom);
        var state = new CpuState { ProgramBank = 0, ProgramCounter = 0x8000, Status = 0x30, EmulationMode = true };
        var blocks = new List<DecodedCpuBlock> { NativeBlockTranslator.Decode(bus, state, 32) };
        state.ProgramCounter = 0x8020;
        blocks.Add(NativeBlockTranslator.Decode(bus, state, 16));
        var assembly = Compile(new OperationLowerer().EmitCpuMethods(blocks), RomEmitter.EmitMain(rom, blocks));
        var execute = assembly.GetType("Smw2Pc.Core.Cpu.CompiledRomProgram")!.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)!;
        var reference = new NativeCpuRunner(new SnesMemoryBus(rom));
        var compiled = new NativeCpuRunner(new SnesMemoryBus(rom));

        Assert.AreEqual(7, execute.Invoke(null, [compiled.State, compiled.MemoryBus, 7]));
        compiled.MemoryBus.Hardware.AdvanceCpuInstructions(compiled.MemoryBus, 7);
        reference.Run(7);
        Assert.AreEqual(32, execute.Invoke(null, [compiled.State, compiled.MemoryBus, 32]));
        compiled.MemoryBus.Hardware.AdvanceCpuInstructions(compiled.MemoryBus, 32);
        reference.Run(32);
        Assert.AreEqual(ProofPlayback.Capture(reference, 1).StateSha256, ProofPlayback.Capture(compiled, 1).StateSha256);
    }

    private static Assembly Compile(params string[] generated)
    {
        var options = new CSharpParseOptions(preprocessorSymbols: ["SMW2PC_RECOMPILED"]);
        var resourceAssembly = typeof(OperationLowerer).Assembly;
        var trees = new List<SyntaxTree>();
        foreach (var resource in new[] { "CpuInstructionSet.cs", "CpuInstructionExecutor.cs" })
        {
            using var stream = resourceAssembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            trees.Add(CSharpSyntaxTree.ParseText(reader.ReadToEnd(), options));
        }
        trees.Add(CSharpSyntaxTree.ParseText("global using System;\nglobal using System.IO;\nglobal using System.Collections.Generic;\nglobal using System.Linq;"));
        trees.AddRange(generated.Select(source => CSharpSyntaxTree.ParseText(source, options)));
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(CpuState).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase);
        var compilation = CSharpCompilation.Create("TranslatedSemantics_" + Guid.NewGuid().ToString("N"), trees,
            paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(output.ToArray());
    }

    private static CpuState State(byte mode, bool emulation) => new()
    {
        ProgramCounter = 0x8000,
        Status = (byte)(mode | 0xC1),
        EmulationMode = emulation,
        StackPointer = 0x01EF,
        DirectPage = 0x100,
        DataBank = 0x7E,
        Accumulator = 0x42,
        X = 3,
        Y = 7,
    };

    private static SnesMemoryBus Bus()
    {
        var bus = new SnesMemoryBus(Rom([0xDB]));
        for (var i = 0; i < 0x2000; i++) bus.WriteByte(0x7E0000u + (uint)i, (byte)(i * 17 + 5));
        return bus;
    }

    private static SnesRom Rom(byte[] program)
    {
        var data = new byte[Math.Max(0x8000, program.Length)];
        program.CopyTo(data, 0);
        System.Text.Encoding.ASCII.GetBytes("RECOMPILER TEST       ").CopyTo(data, 0x7FC0);
        data[0x7FD5] = 0x20; data[0x7FDC] = 0xFF; data[0x7FDD] = 0xFF;
        data[0x7FFC] = 0; data[0x7FFD] = 0x80;
        return SnesRom.LoadBytes(data, "test.sfc");
    }

    private sealed class NativeCpuRunnerForState
    {
        public NativeCpuRunner Runner { get; }
        public NativeCpuRunnerForState(SnesMemoryBus bus, CpuState state)
        {
            Runner = new NativeCpuRunner(bus);
            foreach (var field in typeof(CpuState).GetFields()) field.SetValue(Runner.State, field.GetValue(state));
        }
    }
}
