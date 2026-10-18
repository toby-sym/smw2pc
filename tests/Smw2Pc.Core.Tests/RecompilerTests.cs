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

    private static Assembly Compile(string generated)
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
        trees.Add(CSharpSyntaxTree.ParseText("global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;"));
        trees.Add(CSharpSyntaxTree.ParseText(generated, options));
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
        var data = new byte[0x8000];
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
