using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Smw2Pc.Core.Cpu;

namespace Smw2Pc.Recompiler;

internal sealed class OperationLowerer
{
    private readonly Dictionary<CpuOperation, SwitchSectionSyntax> cpuCases = [];
    private readonly Dictionary<int, SwitchSectionSyntax> spcCases = [];
    private readonly Dictionary<int, SwitchSectionSyntax> aluCases = [];
    private readonly MethodDeclarationSyntax branchBitMethod;
    private readonly MethodDeclarationSyntax carryBitMethod;

    public OperationLowerer()
    {
        var cpu = ReadSyntax("CpuInstructionExecutor.cs");
        var execute = cpu.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "Execute");
        foreach (var section in execute.DescendantNodes().OfType<SwitchStatementSyntax>().First().Sections)
            foreach (var label in section.Labels.OfType<CaseSwitchLabelSyntax>())
                cpuCases.Add(Enum.Parse<CpuOperation>(((MemberAccessExpressionSyntax)label.Value).Name.Identifier.Text), section);

        var spc = ReadSyntax("Spc700Cpu.cs");
        branchBitMethod = spc.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "BranchOnDirectPageBit");
        carryBitMethod = spc.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "ExecuteCarryBitOperation");
        foreach (var method in spc.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Identifier.Text is not ("Step" or "TryExecuteAlu")) continue;
            var cases = method.Identifier.Text == "Step" ? spcCases : aluCases;
            foreach (var section in method.DescendantNodes().OfType<SwitchStatementSyntax>().First().Sections)
                foreach (var label in section.Labels.OfType<CaseSwitchLabelSyntax>())
                    cases.Add(Convert.ToInt32(((LiteralExpressionSyntax)label.Value).Token.Value), section);
        }
        foreach (var operation in Enum.GetValues<CpuOperation>())
            if (!cpuCases.ContainsKey(operation)) throw new InvalidDataException($"No semantics for {operation}.");
    }

    public static string MethodName(NativeCpuInstruction instruction, byte mode, bool emulation)
        => $"Translated_{instruction.Operation}_{instruction.AddressingMode}_{(emulation ? 0x30 : mode):X2}";

    public string EmitCpuMethods(IReadOnlyList<DecodedCpuBlock> blocks)
    {
        var source = new StringBuilder("#pragma warning disable CS0219\nusing Smw2Pc.Core.Machine;\nnamespace Smw2Pc.Core.Cpu;\ninternal static partial class CpuInstructionExecutor\n{\n");
        var emitted = new HashSet<string>();
        foreach (var block in blocks)
            foreach (var instruction in block.Instructions)
            {
                var name = MethodName(instruction, block.StatusMode, block.EmulationMode);
                if (!emitted.Add(name)) continue;
                var accumulator8 = block.EmulationMode || (block.StatusMode & 0x20) != 0;
                var indexes8 = block.EmulationMode || (block.StatusMode & 0x10) != 0;
                var replacements = new Dictionary<string, string>
                {
                    ["operation"] = $"CpuOperation.{instruction.Operation}",
                    ["addressingMode"] = $"CpuAddressingMode.{instruction.AddressingMode}",
                };
                var body = RewriteStatements(cpuCases[instruction.Operation], replacements);
                if (instruction.Operation.ToString().StartsWith("Branch", StringComparison.Ordinal)
                    && instruction.Operation is not CpuOperation.BranchLong)
                {
                    var condition = instruction.Operation switch
                    {
                        CpuOperation.BranchPlus => "(state.Status & 0x80) == 0",
                        CpuOperation.BranchMinus => "(state.Status & 0x80) != 0",
                        CpuOperation.BranchOverflowClear => "(state.Status & 0x40) == 0",
                        CpuOperation.BranchOverflowSet => "(state.Status & 0x40) != 0",
                        CpuOperation.BranchCarryClear => "(state.Status & 0x01) == 0",
                        CpuOperation.BranchCarrySet => "(state.Status & 0x01) != 0",
                        CpuOperation.BranchNotEqual => "(state.Status & 0x02) == 0",
                        CpuOperation.BranchEqual => "(state.Status & 0x02) != 0",
                        _ => "true",
                    };
                    body = body.Replace($"ShouldBranch(state, CpuOperation.{instruction.Operation})", $"({condition})", StringComparison.Ordinal);
                }
                source.AppendLine($"internal static void {name}(CpuState state, SnesMemoryBus memoryBus, uint operand, ushort nextProgramCounter)");
                source.AppendLine("{");
                source.AppendLine($"const bool accumulatorIs8Bit = {(accumulator8 ? "true" : "false")};");
                source.AppendLine($"const bool indexesAre8Bit = {(indexes8 ? "true" : "false")};");
                source.AppendLine($"const ushort accumulatorMask = 0x{(accumulator8 ? 0xFF : 0xFFFF):X4};");
                source.AppendLine($"const ushort indexMask = 0x{(indexes8 ? 0xFF : 0xFFFF):X4};");
                source.AppendLine("var accumulator = (ushort)(state.Accumulator & accumulatorMask);");
                source.AppendLine(body);
                source.AppendLine("}");
            }
        return source.AppendLine("}").ToString();
    }

    public string EmitAudioMethods(IEnumerable<byte> opcodes)
    {
        var source = new StringBuilder("using System.Collections.Generic;\nnamespace Smw2Pc.Core.Audio;\ninternal sealed partial class Spc700Cpu\n{\n");
        foreach (var opcode in opcodes.Distinct().Order())
        {
            var form = opcode & 0x1F;
            var family = opcode & 0xE0;
            var isAlu = family <= 0xA0 && aluCases.ContainsKey(form);
            var section = isAlu ? aluCases[form] : spcCases.GetValueOrDefault(opcode)
                ?? throw new InvalidDataException($"No SPC700 semantics for ${opcode:X2}.");
            var replacements = new Dictionary<string, string> { ["opcode"] = $"0x{opcode:X2}" };
            if (isAlu) replacements["operation"] = $"((AluOperation)0x{family:X2})";
            replacements["BranchOnDirectPageBit"] = $"TranslatedBranchBit_{opcode:X2}";
            replacements["ExecuteCarryBitOperation"] = $"TranslatedCarry_{opcode:X2}";
            source.AppendLine($"private int TranslatedAudio_{opcode:X2}()");
            source.AppendLine("{");
            source.AppendLine("ProgramCounter++; extraCycles = 0;");
            // The original case bodies use break to leave their switch.
            source.AppendLine("do {");
            source.AppendLine(RewriteStatements(section, replacements));
            source.AppendLine("} while (false);");
            source.AppendLine($"return BaseCycles[0x{opcode:X2}] + extraCycles;");
            source.AppendLine("}");
            if (section.ToString().Contains("BranchOnDirectPageBit", StringComparison.Ordinal))
            {
                var clone = branchBitMethod.WithIdentifier(SyntaxFactory.Identifier($"TranslatedBranchBit_{opcode:X2}"))
                    .WithoutLeadingTrivia();
                source.AppendLine(new ConstantSubstitution(replacements).Visit(clone)!.NormalizeWhitespace().ToFullString());
            }
            if (section.ToString().Contains("ExecuteCarryBitOperation", StringComparison.Ordinal))
            {
                var dispatch = carryBitMethod.Body!.Statements.OfType<SwitchStatementSyntax>().Single();
                var selected = dispatch.Sections.Single(s => s.Labels.OfType<CaseSwitchLabelSyntax>()
                    .Any(l => Convert.ToInt32(((LiteralExpressionSyntax)l.Value).Token.Value) == opcode));
                var statements = carryBitMethod.Body.Statements.TakeWhile(s => s is not SwitchStatementSyntax)
                    .Concat(selected.Statements.Where(s => s is not BreakStatementSyntax));
                var clone = carryBitMethod.WithIdentifier(SyntaxFactory.Identifier($"TranslatedCarry_{opcode:X2}"))
                    .WithBody(SyntaxFactory.Block(statements)).WithoutLeadingTrivia();
                source.AppendLine(new ConstantSubstitution(replacements).Visit(clone)!.NormalizeWhitespace().ToFullString());
            }
        }
        return source.AppendLine("}").ToString();
    }

    private static string RewriteStatements(SwitchSectionSyntax section, Dictionary<string, string> replacements)
    {
        var rewriter = new ConstantSubstitution(replacements);
        return string.Join(Environment.NewLine, section.Statements.Select(s => rewriter.Visit(s)!.NormalizeWhitespace().ToFullString()));
    }

    private static SyntaxNode ReadSyntax(string name)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing semantic source {name}.");
        using var reader = new StreamReader(resource);
        return CSharpSyntaxTree.ParseText(reader.ReadToEnd()).GetRoot();
    }

    private sealed class ConstantSubstitution(Dictionary<string, string> replacements) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
            => replacements.TryGetValue(node.Identifier.Text, out var expression)
                ? SyntaxFactory.ParseExpression(expression).WithTriviaFrom(node) : base.VisitIdentifierName(node);
    }
}
