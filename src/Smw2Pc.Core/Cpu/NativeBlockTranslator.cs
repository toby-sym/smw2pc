using System.Reflection;
using System.Reflection.Emit;
using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Cpu;

public sealed class NativeBlockTranslator
{
    private static readonly FieldInfo AccumulatorField = GetField(nameof(CpuState.Accumulator));
    private static readonly FieldInfo XField = GetField(nameof(CpuState.X));
    private static readonly FieldInfo YField = GetField(nameof(CpuState.Y));
    private static readonly FieldInfo DataBankField = GetField(nameof(CpuState.DataBank));
    private static readonly FieldInfo ProgramCounterField = GetField(nameof(CpuState.ProgramCounter));
    private static readonly FieldInfo HaltedField = GetField(nameof(CpuState.Halted));
    private static readonly MethodInfo WriteByteMethod = typeof(SnesMemoryBus).GetMethod(nameof(SnesMemoryBus.WriteByte))!;
    private static readonly MethodInfo SetNegativeZero8Method = typeof(CpuState).GetMethod(nameof(CpuState.SetNegativeZero8))!;
    private static readonly MethodInfo SetNegativeZero16Method = typeof(CpuState).GetMethod(nameof(CpuState.SetNegativeZero16))!;

    public NativeCpuBlock Translate(SnesMemoryBus memoryBus, CpuState state, int maximumInstructions = 32)
    {
        ArgumentNullException.ThrowIfNull(memoryBus);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumInstructions);

        if (state.Halted)
        {
            throw new InvalidOperationException("A halted CPU cannot translate another instruction block.");
        }

        var startAddress = ComposeAddress(state.ProgramBank, state.ProgramCounter);
        if (!memoryBus.IsCartridgeRomAddress(startAddress))
        {
            throw new NotSupportedException("This first native translator pass only translates code mapped from cartridge ROM.");
        }

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

            switch (opcode)
            {
                case 0xEA: // NOP
                    break;
                case 0xA9: // LDA immediate
                    EmitLoadImmediate(
                        il,
                        AccumulatorField,
                        ReadImmediate(memoryBus, state.ProgramBank, ref programCounter, state.IsAccumulator8Bit),
                        state.IsAccumulator8Bit,
                        preserveHighByte: state.IsAccumulator8Bit,
                        SetNegativeZero8Method,
                        SetNegativeZero16Method);
                    break;
                case 0xA2: // LDX immediate
                    EmitLoadImmediate(
                        il,
                        XField,
                        ReadImmediate(memoryBus, state.ProgramBank, ref programCounter, state.AreIndexes8Bit),
                        state.AreIndexes8Bit,
                        preserveHighByte: false,
                        SetNegativeZero8Method,
                        SetNegativeZero16Method);
                    break;
                case 0xA0: // LDY immediate
                    EmitLoadImmediate(
                        il,
                        YField,
                        ReadImmediate(memoryBus, state.ProgramBank, ref programCounter, state.AreIndexes8Bit),
                        state.AreIndexes8Bit,
                        preserveHighByte: false,
                        SetNegativeZero8Method,
                        SetNegativeZero16Method);
                    break;
                case 0x8D: // STA absolute
                    EmitStoreAbsolute(
                        il,
                        ReadAbsoluteAddress(memoryBus, state.ProgramBank, ref programCounter),
                        AccumulatorField,
                        state.IsAccumulator8Bit);
                    break;
                case 0x9C: // STZ absolute
                    EmitStoreZeroAbsolute(
                        il,
                        ReadAbsoluteAddress(memoryBus, state.ProgramBank, ref programCounter),
                        state.IsAccumulator8Bit);
                    break;
                case 0x80: // BRA relative
                    var displacement = (sbyte)ReadProgramByte(memoryBus, state.ProgramBank, ref programCounter);
                    programCounter = unchecked((ushort)(programCounter + displacement));
                    EmitSetProgramCounter(il, programCounter);
                    il.Emit(OpCodes.Ret);
                    terminatesBlock = true;
                    break;
                case 0x4C: // JMP absolute
                    programCounter = ReadAbsoluteAddress(memoryBus, state.ProgramBank, ref programCounter);
                    EmitSetProgramCounter(il, programCounter);
                    il.Emit(OpCodes.Ret);
                    terminatesBlock = true;
                    break;
                case 0xDB: // STP
                    EmitSetProgramCounter(il, programCounter);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Stfld, HaltedField);
                    il.Emit(OpCodes.Ret);
                    terminatesBlock = true;
                    break;
                default:
                    throw new NotSupportedException(
                        $"Opcode 0x{opcode:X2} at 0x{instructionAddress:X6} is not supported by the native translator yet.");
            }
        }

        if (!terminatesBlock)
        {
            EmitSetProgramCounter(il, programCounter);
            il.Emit(OpCodes.Ret);
        }

        var code = (NativeCpuBlockCode)method.CreateDelegate(typeof(NativeCpuBlockCode));
        return new NativeCpuBlock(startAddress, instructionCount, code);
    }

    private static void EmitLoadImmediate(
        ILGenerator il,
        FieldInfo registerField,
        ushort value,
        bool is8Bit,
        bool preserveHighByte,
        MethodInfo setNegativeZero8,
        MethodInfo setNegativeZero16)
    {
        il.Emit(OpCodes.Ldarg_0);
        if (is8Bit && preserveHighByte)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, registerField);
            il.Emit(OpCodes.Ldc_I4, 0xFF00);
            il.Emit(OpCodes.And);
        }

        il.Emit(OpCodes.Ldc_I4, is8Bit ? value & 0xFF : value);
        if (is8Bit && preserveHighByte)
        {
            il.Emit(OpCodes.Or);
        }

        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, registerField);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, is8Bit ? value & 0xFF : value);
        if (is8Bit)
        {
            il.Emit(OpCodes.Conv_U1);
            il.Emit(OpCodes.Callvirt, setNegativeZero8);
        }
        else
        {
            il.Emit(OpCodes.Conv_U2);
            il.Emit(OpCodes.Callvirt, setNegativeZero16);
        }
    }

    private static void EmitStoreAbsolute(ILGenerator il, ushort address, FieldInfo sourceRegister, bool is8Bit)
    {
        EmitStoreByte(il, address, sourceRegister, highByte: false);
        if (!is8Bit)
        {
            EmitStoreByte(il, unchecked((ushort)(address + 1)), sourceRegister, highByte: true);
        }
    }

    private static void EmitStoreZeroAbsolute(ILGenerator il, ushort address, bool is8Bit)
    {
        EmitStoreZeroByte(il, address);
        if (!is8Bit)
        {
            EmitStoreZeroByte(il, unchecked((ushort)(address + 1)));
        }
    }

    private static void EmitStoreByte(ILGenerator il, ushort address, FieldInfo sourceRegister, bool highByte)
    {
        il.Emit(OpCodes.Ldarg_1);
        EmitDataBankAddress(il, address);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, sourceRegister);
        if (highByte)
        {
            il.Emit(OpCodes.Ldc_I4_8);
            il.Emit(OpCodes.Shr_Un);
        }

        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Callvirt, WriteByteMethod);
    }

    private static void EmitStoreZeroByte(ILGenerator il, ushort address)
    {
        il.Emit(OpCodes.Ldarg_1);
        EmitDataBankAddress(il, address);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Callvirt, WriteByteMethod);
    }

    private static void EmitDataBankAddress(ILGenerator il, ushort address)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, DataBankField);
        il.Emit(OpCodes.Ldc_I4, 16);
        il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldc_I4, address);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U4);
    }

    private static void EmitSetProgramCounter(ILGenerator il, ushort programCounter)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, programCounter);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, ProgramCounterField);
    }

    private static byte ReadProgramByte(SnesMemoryBus memoryBus, byte programBank, ref ushort programCounter)
    {
        var address = ComposeAddress(programBank, programCounter);
        if (!memoryBus.IsCartridgeRomAddress(address))
        {
            throw new NotSupportedException($"Instruction data at 0x{address:X6} is not mapped from cartridge ROM.");
        }

        var value = memoryBus.ReadByte(address);
        programCounter = unchecked((ushort)(programCounter + 1));
        return value;
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

    private static uint ComposeAddress(byte bank, ushort offset)
    {
        return ((uint)bank << 16) | offset;
    }

    private static FieldInfo GetField(string name)
    {
        return typeof(CpuState).GetField(name, BindingFlags.Instance | BindingFlags.Public)!;
    }
}
