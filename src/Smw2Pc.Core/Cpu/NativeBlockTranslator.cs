using System.Reflection;
using System.Reflection.Emit;
using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Cpu;

public sealed class NativeBlockTranslator
{
    private const int DefaultMaximumInstructions = 32;

    private static readonly MethodInfo ExecuteInstructionMethod = typeof(CpuInstructionExecutor).GetMethod(
        nameof(CpuInstructionExecutor.Execute),
        BindingFlags.Static | BindingFlags.Public)!;
    private static readonly FieldInfo ProgramCounterField = GetField(nameof(CpuState.ProgramCounter));

    public NativeCpuBlock Translate(
        SnesMemoryBus memoryBus,
        CpuState state,
        int maximumInstructions = DefaultMaximumInstructions)
    {
        ArgumentNullException.ThrowIfNull(memoryBus);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumInstructions);

        if (state.Halted)
        {
            throw new InvalidOperationException("A halted CPU cannot translate another instruction block.");
        }

        var startAddress = ComposeAddress(state.ProgramBank, state.ProgramCounter);
        var method = new DynamicMethod(
            $"native_block_{startAddress:X6}",
            typeof(void),
            [typeof(CpuState), typeof(SnesMemoryBus)],
            typeof(NativeBlockTranslator).Module,
            skipVisibility: true);
        var il = method.GetILGenerator();
        var programCounter = state.ProgramCounter;
        var instructionCount = 0;
        var terminatesBlock = false;

        while (instructionCount < maximumInstructions && !terminatesBlock)
        {
            var instructionAddress = ComposeAddress(state.ProgramBank, programCounter);
            var opcode = ReadProgramByte(memoryBus, state.ProgramBank, ref programCounter);
            instructionCount++;

            if (!CpuInstructionSet.TryGetDefinition(opcode, out var definition))
            {
                throw new NotSupportedException(
                    $"Opcode 0x{opcode:X2} at 0x{instructionAddress:X6} is not supported by the native translator yet.");
            }

            var operand = ReadOperand(memoryBus, state, definition.AddressingMode, ref programCounter);
            EmitInstruction(il, definition, operand, programCounter);
            terminatesBlock = EndsBlock(definition.Operation);

            if (terminatesBlock && definition.Operation is CpuOperation.ResetStatusBits
                or CpuOperation.SetStatusBits
                or CpuOperation.PullStatus
                or CpuOperation.ExchangeCarryAndEmulation)
            {
                EmitSetProgramCounter(il, programCounter);
            }
        }

        if (!terminatesBlock)
        {
            EmitSetProgramCounter(il, programCounter);
        }

        il.Emit(OpCodes.Ret);
        var code = (NativeCpuBlockCode)method.CreateDelegate(typeof(NativeCpuBlockCode));
        return new NativeCpuBlock(startAddress, instructionCount, code);
    }

    private static void EmitInstruction(
        ILGenerator il,
        CpuInstructionDefinition definition,
        uint operand,
        ushort nextProgramCounter)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4, (int)definition.Operation);
        il.Emit(OpCodes.Ldc_I4, (int)definition.AddressingMode);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)operand));
        il.Emit(OpCodes.Ldc_I4, nextProgramCounter);
        il.Emit(OpCodes.Call, ExecuteInstructionMethod);
    }

    private static void EmitSetProgramCounter(ILGenerator il, ushort programCounter)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, programCounter);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, ProgramCounterField);
    }

    private static uint ReadOperand(
        SnesMemoryBus memoryBus,
        CpuState state,
        CpuAddressingMode addressingMode,
        ref ushort programCounter)
    {
        return addressingMode switch
        {
            CpuAddressingMode.Implied or CpuAddressingMode.Accumulator => 0,
            CpuAddressingMode.ImmediateAccumulator => ReadImmediate(
                memoryBus,
                state.ProgramBank,
                ref programCounter,
                state.IsAccumulator8Bit),
            CpuAddressingMode.ImmediateIndex => ReadImmediate(
                memoryBus,
                state.ProgramBank,
                ref programCounter,
                state.AreIndexes8Bit),
            CpuAddressingMode.ImmediateByte
            or CpuAddressingMode.DirectPage
            or CpuAddressingMode.DirectPageX
            or CpuAddressingMode.DirectPageY
            or CpuAddressingMode.DirectPageIndexedIndirect
            or CpuAddressingMode.DirectPageIndirect
            or CpuAddressingMode.DirectPageIndirectY
            or CpuAddressingMode.DirectPageIndirectLong
            or CpuAddressingMode.DirectPageIndirectLongY
            or CpuAddressingMode.StackRelative
            or CpuAddressingMode.StackRelativeIndirectY
            or CpuAddressingMode.Relative => ReadProgramByte(memoryBus, state.ProgramBank, ref programCounter),
            CpuAddressingMode.RelativeLong => ReadAbsoluteAddress(memoryBus, state.ProgramBank, ref programCounter),
            CpuAddressingMode.Absolute
            or CpuAddressingMode.AbsoluteX
            or CpuAddressingMode.AbsoluteY
            or CpuAddressingMode.AbsoluteIndirect
            or CpuAddressingMode.AbsoluteIndexedIndirect
            or CpuAddressingMode.AbsoluteIndirectLong => ReadAbsoluteAddress(memoryBus, state.ProgramBank, ref programCounter),
            CpuAddressingMode.BlockMove => ReadBlockMoveBanks(memoryBus, state.ProgramBank, ref programCounter),
            CpuAddressingMode.Long
            or CpuAddressingMode.LongX => ReadLongAddress(memoryBus, state.ProgramBank, ref programCounter),
            _ => throw new ArgumentOutOfRangeException(nameof(addressingMode), addressingMode, "Unsupported addressing mode."),
        };
    }

    private static ushort ReadImmediate(
        SnesMemoryBus memoryBus,
        byte programBank,
        ref ushort programCounter,
        bool is8Bit)
    {
        var low = ReadProgramByte(memoryBus, programBank, ref programCounter);
        if (is8Bit)
        {
            return low;
        }

        var high = ReadProgramByte(memoryBus, programBank, ref programCounter);
        return (ushort)(low | (high << 8));
    }

    private static ushort ReadAbsoluteAddress(SnesMemoryBus memoryBus, byte programBank, ref ushort programCounter)
    {
        var low = ReadProgramByte(memoryBus, programBank, ref programCounter);
        var high = ReadProgramByte(memoryBus, programBank, ref programCounter);
        return (ushort)(low | (high << 8));
    }

    private static uint ReadLongAddress(SnesMemoryBus memoryBus, byte programBank, ref ushort programCounter)
    {
        var low = ReadProgramByte(memoryBus, programBank, ref programCounter);
        var middle = ReadProgramByte(memoryBus, programBank, ref programCounter);
        var high = ReadProgramByte(memoryBus, programBank, ref programCounter);
        return (uint)(low | (middle << 8) | (high << 16));
    }

    private static uint ReadBlockMoveBanks(SnesMemoryBus memoryBus, byte programBank, ref ushort programCounter)
    {
        var destinationBank = ReadProgramByte(memoryBus, programBank, ref programCounter);
        var sourceBank = ReadProgramByte(memoryBus, programBank, ref programCounter);
        return (uint)(destinationBank | (sourceBank << 8));
    }

    private static byte ReadProgramByte(SnesMemoryBus memoryBus, byte programBank, ref ushort programCounter)
    {
        var address = ComposeAddress(programBank, programCounter);
        var value = memoryBus.ReadByte(address);
        programCounter = unchecked((ushort)(programCounter + 1));
        return value;
    }

    private static bool EndsBlock(CpuOperation operation)
    {
        return operation is CpuOperation.Stop
            or CpuOperation.JumpAbsolute
            or CpuOperation.JumpIndirect
            or CpuOperation.JumpAbsoluteIndexedIndirect
            or CpuOperation.JumpLong
            or CpuOperation.JumpIndirectLong
            or CpuOperation.CallAbsolute
            or CpuOperation.CallAbsoluteIndexedIndirect
            or CpuOperation.CallLong
            or CpuOperation.Break
            or CpuOperation.Cop
            or CpuOperation.ReturnFromSubroutine
            or CpuOperation.ReturnFromSubroutineLong
            or CpuOperation.ReturnFromInterrupt
            or CpuOperation.BranchLong
            or CpuOperation.BlockMoveNext
            or CpuOperation.BlockMovePrevious
            or CpuOperation.WaitForInterrupt
            or CpuOperation.BranchPlus
            or CpuOperation.BranchMinus
            or CpuOperation.BranchOverflowClear
            or CpuOperation.BranchOverflowSet
            or CpuOperation.BranchCarryClear
            or CpuOperation.BranchCarrySet
            or CpuOperation.BranchNotEqual
            or CpuOperation.BranchEqual
            or CpuOperation.BranchAlways
            or CpuOperation.ResetStatusBits
            or CpuOperation.SetStatusBits
            or CpuOperation.PullStatus
            or CpuOperation.ExchangeCarryAndEmulation;
    }

    private static uint ComposeAddress(byte bank, ushort offset)
    {
        return ((uint)bank << 16) | offset;
    }

    private static FieldInfo GetField(string name)
    {
        return typeof(CpuState).GetField(name, BindingFlags.Instance | BindingFlags.Public)!;
    }
}
