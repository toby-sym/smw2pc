using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Cpu;

internal static partial class CpuInstructionExecutor
{
    private const ushort ByteMask = 0x00FF;

#if !SMW2PC_RECOMPILED
    public static void Execute(
        CpuState state,
        SnesMemoryBus memoryBus,
        CpuOperation operation,
        CpuAddressingMode addressingMode,
        uint operand,
        ushort nextProgramCounter)
    {
        var accumulatorIs8Bit = state.IsAccumulator8Bit;
        var indexesAre8Bit = state.AreIndexes8Bit;
        var accumulatorMask = accumulatorIs8Bit ? ByteMask : ushort.MaxValue;
        var indexMask = indexesAre8Bit ? ByteMask : ushort.MaxValue;
        var accumulator = (ushort)(state.Accumulator & accumulatorMask);

        switch (operation)
        {
            case CpuOperation.Nop:
                return;
            case CpuOperation.Stop:
                state.ProgramCounter = nextProgramCounter;
                state.Halted = true;
                return;
            case CpuOperation.WaitForInterrupt:
                state.ProgramCounter = nextProgramCounter;
                state.WaitingForInterrupt = true;
                return;
            case CpuOperation.Wdm:
                return;
            case CpuOperation.JumpAbsolute:
                state.ProgramCounter = (ushort)operand;
                return;
            case CpuOperation.JumpIndirect:
                state.ProgramCounter = memoryBus.ReadWordInBank((ushort)operand);
                return;
            case CpuOperation.JumpAbsoluteIndexedIndirect:
                var jumpPointer = (ushort)((ushort)operand + (indexesAre8Bit ? state.X & ByteMask : state.X));
                state.ProgramCounter = memoryBus.ReadWordInBank(((uint)state.ProgramBank << 16) | jumpPointer);
                return;
            case CpuOperation.JumpLong:
                state.ProgramCounter = (ushort)operand;
                state.ProgramBank = (byte)(operand >> 16);
                return;
            case CpuOperation.JumpIndirectLong:
                var pointerAddress = (ushort)operand;
                var targetLow = memoryBus.ReadByte(pointerAddress);
                var targetHigh = memoryBus.ReadByte((ushort)(pointerAddress + 1));
                state.ProgramBank = memoryBus.ReadByte((ushort)(pointerAddress + 2));
                state.ProgramCounter = (ushort)(targetLow | (targetHigh << 8));
                return;
            case CpuOperation.CallAbsolute:
                PushValue(state, memoryBus, unchecked((ushort)(nextProgramCounter - 1)), is8Bit: false);
                state.ProgramCounter = (ushort)operand;
                return;
            case CpuOperation.CallAbsoluteIndexedIndirect:
                PushValue(state, memoryBus, unchecked((ushort)(nextProgramCounter - 1)), is8Bit: false);
                var callPointer = (ushort)((ushort)operand + (indexesAre8Bit ? state.X & ByteMask : state.X));
                state.ProgramCounter = memoryBus.ReadWordInBank(((uint)state.ProgramBank << 16) | callPointer);
                return;
            case CpuOperation.CallLong:
                PushByte(state, memoryBus, state.ProgramBank);
                PushValue(state, memoryBus, unchecked((ushort)(nextProgramCounter - 1)), is8Bit: false);
                state.ProgramCounter = (ushort)operand;
                state.ProgramBank = (byte)(operand >> 16);
                return;
            case CpuOperation.ReturnFromSubroutine:
                state.ProgramCounter = unchecked((ushort)(PopValue(state, memoryBus, is8Bit: false) + 1));
                return;
            case CpuOperation.ReturnFromSubroutineLong:
                state.ProgramCounter = unchecked((ushort)(PopValue(state, memoryBus, is8Bit: false) + 1));
                state.ProgramBank = PopByte(state, memoryBus);
                return;
            case CpuOperation.ReturnFromInterrupt:
                state.Status = PopByte(state, memoryBus);
                NormalizeStatus(state);
                state.ProgramCounter = PopValue(state, memoryBus, is8Bit: false);
                if (!state.EmulationMode)
                {
                    state.ProgramBank = PopByte(state, memoryBus);
                }

                return;
            case CpuOperation.Break:
                EnterSoftwareInterrupt(state, memoryBus, nextProgramCounter, state.EmulationMode ? 0xFFFEu : 0xFFE6u, isBreak: true);
                return;
            case CpuOperation.Cop:
                EnterSoftwareInterrupt(state, memoryBus, nextProgramCounter, state.EmulationMode ? 0xFFF4u : 0xFFE4u, isBreak: false);
                return;
            case CpuOperation.BranchLong:
                state.ProgramCounter = unchecked((ushort)(nextProgramCounter + (short)operand));
                return;
            case CpuOperation.BlockMoveNext:
                MoveBlock(state, memoryBus, operand, increment: true);
                state.ProgramCounter = nextProgramCounter;
                return;
            case CpuOperation.BlockMovePrevious:
                MoveBlock(state, memoryBus, operand, increment: false);
                state.ProgramCounter = nextProgramCounter;
                return;
            case CpuOperation.BranchPlus:
            case CpuOperation.BranchMinus:
            case CpuOperation.BranchOverflowClear:
            case CpuOperation.BranchOverflowSet:
            case CpuOperation.BranchCarryClear:
            case CpuOperation.BranchCarrySet:
            case CpuOperation.BranchNotEqual:
            case CpuOperation.BranchEqual:
            case CpuOperation.BranchAlways:
                state.ProgramCounter = ShouldBranch(state, operation)
                    ? unchecked((ushort)(nextProgramCounter + (sbyte)operand))
                    : nextProgramCounter;
                return;
            case CpuOperation.LoadAccumulator:
                SetAccumulator(state, ReadOperand(state, memoryBus, addressingMode, operand, accumulatorIs8Bit), accumulatorIs8Bit);
                return;
            case CpuOperation.LoadX:
                state.X = (ushort)(ReadOperand(state, memoryBus, addressingMode, operand, indexesAre8Bit) & indexMask);
                SetNegativeZero(state, state.X, indexesAre8Bit);
                return;
            case CpuOperation.LoadY:
                state.Y = (ushort)(ReadOperand(state, memoryBus, addressingMode, operand, indexesAre8Bit) & indexMask);
                SetNegativeZero(state, state.Y, indexesAre8Bit);
                return;
            case CpuOperation.StoreAccumulator:
                WriteOperand(state, memoryBus, addressingMode, operand, accumulator, accumulatorIs8Bit);
                return;
            case CpuOperation.StoreX:
                WriteOperand(state, memoryBus, addressingMode, operand, state.X, indexesAre8Bit);
                return;
            case CpuOperation.StoreY:
                WriteOperand(state, memoryBus, addressingMode, operand, state.Y, indexesAre8Bit);
                return;
            case CpuOperation.StoreZero:
                WriteOperand(state, memoryBus, addressingMode, operand, 0, accumulatorIs8Bit);
                return;
            case CpuOperation.TestAndSetBits:
                TestAndModifyBits(state, memoryBus, addressingMode, operand, accumulator, accumulatorIs8Bit, setBits: true);
                return;
            case CpuOperation.TestAndResetBits:
                TestAndModifyBits(state, memoryBus, addressingMode, operand, accumulator, accumulatorIs8Bit, setBits: false);
                return;
            case CpuOperation.BitTest:
                BitTest(state, memoryBus, addressingMode, operand, accumulator, accumulatorIs8Bit);
                return;
            case CpuOperation.IncrementMemory:
                ModifyMemory(state, memoryBus, addressingMode, operand, accumulatorIs8Bit, increment: true);
                return;
            case CpuOperation.DecrementMemory:
                ModifyMemory(state, memoryBus, addressingMode, operand, accumulatorIs8Bit, increment: false);
                return;
            case CpuOperation.ShiftMemoryLeft:
                ShiftMemory(state, memoryBus, addressingMode, operand, accumulatorIs8Bit, left: true, rotate: false);
                return;
            case CpuOperation.ShiftMemoryRight:
                ShiftMemory(state, memoryBus, addressingMode, operand, accumulatorIs8Bit, left: false, rotate: false);
                return;
            case CpuOperation.RotateMemoryLeft:
                ShiftMemory(state, memoryBus, addressingMode, operand, accumulatorIs8Bit, left: true, rotate: true);
                return;
            case CpuOperation.RotateMemoryRight:
                ShiftMemory(state, memoryBus, addressingMode, operand, accumulatorIs8Bit, left: false, rotate: true);
                return;
            case CpuOperation.And:
                SetAccumulator(state, (ushort)(accumulator & ReadOperand(state, memoryBus, addressingMode, operand, accumulatorIs8Bit)), accumulatorIs8Bit);
                return;
            case CpuOperation.Or:
                SetAccumulator(state, (ushort)(accumulator | ReadOperand(state, memoryBus, addressingMode, operand, accumulatorIs8Bit)), accumulatorIs8Bit);
                return;
            case CpuOperation.ExclusiveOr:
                SetAccumulator(state, (ushort)(accumulator ^ ReadOperand(state, memoryBus, addressingMode, operand, accumulatorIs8Bit)), accumulatorIs8Bit);
                return;
            case CpuOperation.AddWithCarry:
                AddWithCarry(state, (ushort)ReadOperand(state, memoryBus, addressingMode, operand, accumulatorIs8Bit), accumulatorIs8Bit);
                return;
            case CpuOperation.SubtractWithCarry:
                SubtractWithCarry(state, (ushort)ReadOperand(state, memoryBus, addressingMode, operand, accumulatorIs8Bit), accumulatorIs8Bit);
                return;
            case CpuOperation.CompareAccumulator:
                Compare(state, accumulator, ReadOperand(state, memoryBus, addressingMode, operand, accumulatorIs8Bit), accumulatorIs8Bit);
                return;
            case CpuOperation.CompareX:
                Compare(state, state.X, ReadOperand(state, memoryBus, addressingMode, operand, indexesAre8Bit), indexesAre8Bit);
                return;
            case CpuOperation.CompareY:
                Compare(state, state.Y, ReadOperand(state, memoryBus, addressingMode, operand, indexesAre8Bit), indexesAre8Bit);
                return;
            case CpuOperation.IncrementAccumulator:
                SetAccumulator(state, (ushort)(accumulator + 1), accumulatorIs8Bit);
                return;
            case CpuOperation.DecrementAccumulator:
                SetAccumulator(state, (ushort)(accumulator - 1), accumulatorIs8Bit);
                return;
            case CpuOperation.ShiftAccumulatorLeft:
                ShiftAccumulator(state, accumulatorIs8Bit, left: true, rotate: false);
                return;
            case CpuOperation.ShiftAccumulatorRight:
                ShiftAccumulator(state, accumulatorIs8Bit, left: false, rotate: false);
                return;
            case CpuOperation.RotateAccumulatorLeft:
                ShiftAccumulator(state, accumulatorIs8Bit, left: true, rotate: true);
                return;
            case CpuOperation.RotateAccumulatorRight:
                ShiftAccumulator(state, accumulatorIs8Bit, left: false, rotate: true);
                return;
            case CpuOperation.IncrementX:
                SetIndex(state, (ushort)(state.X + 1), isX: true, indexesAre8Bit);
                return;
            case CpuOperation.DecrementX:
                SetIndex(state, (ushort)(state.X - 1), isX: true, indexesAre8Bit);
                return;
            case CpuOperation.IncrementY:
                SetIndex(state, (ushort)(state.Y + 1), isX: false, indexesAre8Bit);
                return;
            case CpuOperation.DecrementY:
                SetIndex(state, (ushort)(state.Y - 1), isX: false, indexesAre8Bit);
                return;
            case CpuOperation.TransferAToX:
                SetIndex(state, state.Accumulator, isX: true, indexesAre8Bit);
                return;
            case CpuOperation.TransferAToY:
                SetIndex(state, state.Accumulator, isX: false, indexesAre8Bit);
                return;
            case CpuOperation.TransferXToA:
                SetAccumulator(state, state.X, accumulatorIs8Bit);
                return;
            case CpuOperation.TransferYToA:
                SetAccumulator(state, state.Y, accumulatorIs8Bit);
                return;
            case CpuOperation.TransferXToY:
                SetIndex(state, state.X, isX: false, indexesAre8Bit);
                return;
            case CpuOperation.TransferYToX:
                SetIndex(state, state.Y, isX: true, indexesAre8Bit);
                return;
            case CpuOperation.TransferStackToX:
                SetIndex(state, state.StackPointer, isX: true, indexesAre8Bit);
                return;
            case CpuOperation.TransferXToStack:
                state.StackPointer = state.X;
                EnforceEmulationStack(state);
                return;
            case CpuOperation.ClearCarry:
                ClearFlag(state, CpuStatusFlags.Carry);
                return;
            case CpuOperation.SetCarry:
                SetFlag(state, CpuStatusFlags.Carry);
                return;
            case CpuOperation.ClearInterrupt:
                ClearFlag(state, CpuStatusFlags.InterruptDisable);
                return;
            case CpuOperation.SetInterrupt:
                SetFlag(state, CpuStatusFlags.InterruptDisable);
                return;
            case CpuOperation.ClearOverflow:
                ClearFlag(state, CpuStatusFlags.Overflow);
                return;
            case CpuOperation.ClearDecimal:
                ClearFlag(state, CpuStatusFlags.Decimal);
                return;
            case CpuOperation.SetDecimal:
                SetFlag(state, CpuStatusFlags.Decimal);
                return;
            case CpuOperation.ResetStatusBits:
                state.Status = (byte)(state.Status & ~(byte)operand);
                NormalizeStatus(state);
                return;
            case CpuOperation.SetStatusBits:
                var previousStatus = state.Status;
                state.Status |= (byte)operand;
                NormalizeStatus(state);
                if ((previousStatus & CpuStatusFlags.Index8Bit) == 0
                    && (state.Status & CpuStatusFlags.Index8Bit) != 0)
                {
                    state.X &= ByteMask;
                    state.Y &= ByteMask;
                }

                return;
            case CpuOperation.ExchangeCarryAndEmulation:
                var previousEmulationMode = state.EmulationMode;
                state.EmulationMode = (state.Status & CpuStatusFlags.Carry) != 0;
                SetCarry(state, previousEmulationMode);
                NormalizeStatus(state);
                return;
            case CpuOperation.ExchangeAccumulatorBytes:
                state.Accumulator = (ushort)((state.Accumulator << 8) | (state.Accumulator >> 8));
                state.SetNegativeZero8((byte)state.Accumulator);
                return;
            case CpuOperation.TransferAToDirectPage:
                state.DirectPage = state.Accumulator;
                state.SetNegativeZero16(state.DirectPage);
                return;
            case CpuOperation.TransferDirectPageToA:
                state.Accumulator = state.DirectPage;
                state.SetNegativeZero16(state.Accumulator);
                return;
            case CpuOperation.TransferAToStack:
                state.StackPointer = state.Accumulator;
                EnforceEmulationStack(state);
                return;
            case CpuOperation.TransferStackToA:
                SetAccumulator(state, state.StackPointer, is8Bit: false);
                return;
            case CpuOperation.PushAccumulator:
                PushValue(state, memoryBus, accumulator, accumulatorIs8Bit);
                return;
            case CpuOperation.PullAccumulator:
                SetAccumulator(state, PopValue(state, memoryBus, accumulatorIs8Bit), accumulatorIs8Bit);
                return;
            case CpuOperation.PushX:
                PushValue(state, memoryBus, state.X, indexesAre8Bit);
                return;
            case CpuOperation.PullX:
                SetIndex(state, PopValue(state, memoryBus, indexesAre8Bit), isX: true, indexesAre8Bit);
                return;
            case CpuOperation.PushY:
                PushValue(state, memoryBus, state.Y, indexesAre8Bit);
                return;
            case CpuOperation.PullY:
                SetIndex(state, PopValue(state, memoryBus, indexesAre8Bit), isX: false, indexesAre8Bit);
                return;
            case CpuOperation.PushStatus:
                PushByte(state, memoryBus, state.Status);
                return;
            case CpuOperation.PullStatus:
                state.Status = PopByte(state, memoryBus);
                NormalizeStatus(state);
                return;
            case CpuOperation.PushDataBank:
                PushByte(state, memoryBus, state.DataBank);
                return;
            case CpuOperation.PullDataBank:
                state.DataBank = PopByte(state, memoryBus);
                state.SetNegativeZero8(state.DataBank);
                return;
            case CpuOperation.PushProgramBank:
                PushByte(state, memoryBus, state.ProgramBank);
                return;
            case CpuOperation.PushEffectiveAddress:
                PushValue(state, memoryBus, (ushort)operand, is8Bit: false);
                return;
            case CpuOperation.PushEffectiveIndirect:
                var directPagePointer = (ushort)(state.DirectPage + (byte)operand);
                PushValue(state, memoryBus, memoryBus.ReadWordInBank(directPagePointer), is8Bit: false);
                return;
            case CpuOperation.PushEffectiveRelative:
                PushValue(state, memoryBus, unchecked((ushort)(nextProgramCounter + (short)operand)), is8Bit: false);
                return;
            case CpuOperation.PushDirectPage:
                PushValue(state, memoryBus, state.DirectPage, is8Bit: false);
                return;
            case CpuOperation.PullDirectPage:
                state.DirectPage = PopValue(state, memoryBus, is8Bit: false);
                state.SetNegativeZero16(state.DirectPage);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported CPU operation.");
        }
    }

#endif

    private static ushort ReadOperand(
        CpuState state,
        SnesMemoryBus memoryBus,
        CpuAddressingMode addressingMode,
        uint operand,
        bool is8Bit)
    {
        if (addressingMode is CpuAddressingMode.ImmediateAccumulator
            or CpuAddressingMode.ImmediateIndex
            or CpuAddressingMode.ImmediateByte)
        {
            return is8Bit || addressingMode == CpuAddressingMode.ImmediateByte
                ? (ushort)(operand & ByteMask)
                : (ushort)operand;
        }

        var address = GetEffectiveAddress(state, memoryBus, addressingMode, operand);
        if (is8Bit)
        {
            return memoryBus.ReadByte(address);
        }

        if (addressingMode is CpuAddressingMode.Long or CpuAddressingMode.LongX
            or CpuAddressingMode.DirectPageIndirectLong
            or CpuAddressingMode.DirectPageIndirectLongY)
        {
            var nextAddress = (address + 1) & 0xFFFFFF;
            return (ushort)(memoryBus.ReadByte(address) | (memoryBus.ReadByte(nextAddress) << 8));
        }

        return memoryBus.ReadWordInBank(address);
    }

    private static void WriteOperand(
        CpuState state,
        SnesMemoryBus memoryBus,
        CpuAddressingMode addressingMode,
        uint operand,
        ushort value,
        bool is8Bit)
    {
        var address = GetEffectiveAddress(state, memoryBus, addressingMode, operand);
        if (is8Bit)
        {
            memoryBus.WriteByte(address, (byte)value);
        }
        else
        {
            var nextAddress = addressingMode is CpuAddressingMode.Long or CpuAddressingMode.LongX
                or CpuAddressingMode.DirectPageIndirectLong
                or CpuAddressingMode.DirectPageIndirectLongY
                ? (address + 1) & 0xFFFFFF
                : (address & 0xFF0000) | ((address + 1) & 0xFFFF);
            memoryBus.WriteByte(address, (byte)value);
            memoryBus.WriteByte(nextAddress, (byte)(value >> 8));
        }
    }

    private static void ModifyMemory(
        CpuState state,
        SnesMemoryBus memoryBus,
        CpuAddressingMode addressingMode,
        uint operand,
        bool is8Bit,
        bool increment)
    {
        var value = ReadOperand(state, memoryBus, addressingMode, operand, is8Bit);
        var mask = is8Bit ? ByteMask : ushort.MaxValue;
        var result = (ushort)((value + (increment ? 1 : -1)) & mask);
        WriteOperand(state, memoryBus, addressingMode, operand, result, is8Bit);
        SetNegativeZero(state, result, is8Bit);
    }

    private static void TestAndModifyBits(
        CpuState state,
        SnesMemoryBus memoryBus,
        CpuAddressingMode addressingMode,
        uint operand,
        ushort accumulator,
        bool is8Bit,
        bool setBits)
    {
        var value = ReadOperand(state, memoryBus, addressingMode, operand, is8Bit);
        SetFlag(state, CpuStatusFlags.Zero, (value & accumulator) == 0);
        var result = setBits ? (ushort)(value | accumulator) : (ushort)(value & ~accumulator);
        WriteOperand(state, memoryBus, addressingMode, operand, result, is8Bit);
    }

    private static void BitTest(
        CpuState state,
        SnesMemoryBus memoryBus,
        CpuAddressingMode addressingMode,
        uint operand,
        ushort accumulator,
        bool is8Bit)
    {
        var value = ReadOperand(state, memoryBus, addressingMode, operand, is8Bit);
        SetFlag(state, CpuStatusFlags.Zero, (value & accumulator) == 0);

        var immediate8Bit = addressingMode == CpuAddressingMode.ImmediateAccumulator && is8Bit;
        if (!immediate8Bit)
        {
            var sign = is8Bit ? 0x80 : 0x8000;
            SetFlag(state, CpuStatusFlags.Negative, (value & sign) != 0);
            SetFlag(state, CpuStatusFlags.Overflow, (value & (sign >> 1)) != 0);
        }
    }

    private static void ShiftMemory(
        CpuState state,
        SnesMemoryBus memoryBus,
        CpuAddressingMode addressingMode,
        uint operand,
        bool is8Bit,
        bool left,
        bool rotate)
    {
        var mask = is8Bit ? ByteMask : ushort.MaxValue;
        var sign = is8Bit ? 0x80 : 0x8000;
        var value = ReadOperand(state, memoryBus, addressingMode, operand, is8Bit) & mask;
        var carryIn = (state.Status & CpuStatusFlags.Carry) != 0;
        int result;
        bool carryOut;

        if (left)
        {
            carryOut = (value & sign) != 0;
            result = (value << 1) & mask;
            if (rotate && carryIn)
            {
                result |= 1;
            }
        }
        else
        {
            carryOut = (value & 1) != 0;
            result = value >> 1;
            if (rotate && carryIn)
            {
                result |= sign;
            }
        }

        SetCarry(state, carryOut);
        WriteOperand(state, memoryBus, addressingMode, operand, (ushort)result, is8Bit);
        SetNegativeZero(state, (ushort)result, is8Bit);
    }

    private static void MoveBlock(CpuState state, SnesMemoryBus memoryBus, uint operand, bool increment)
    {
        var destinationBank = (byte)operand;
        var sourceBank = (byte)(operand >> 8);
        var indexMask = state.AreIndexes8Bit ? ByteMask : ushort.MaxValue;
        var byteCount = (uint)state.Accumulator + 1;
        for (var moved = 0u; moved < byteCount; moved++)
        {
            var sourceAddress = ((uint)sourceBank << 16) | state.X;
            var destinationAddress = ((uint)destinationBank << 16) | state.Y;
            memoryBus.WriteByte(destinationAddress, memoryBus.ReadByte(sourceAddress));
            var delta = increment ? 1 : -1;
            state.X = (ushort)((state.X + delta) & indexMask);
            state.Y = (ushort)((state.Y + delta) & indexMask);
        }

        state.Accumulator = ushort.MaxValue;
        state.DataBank = destinationBank;
    }

    private static void EnterSoftwareInterrupt(
        CpuState state,
        SnesMemoryBus memoryBus,
        ushort nextProgramCounter,
        uint vector,
        bool isBreak)
    {
        if (!state.EmulationMode)
        {
            PushByte(state, memoryBus, state.ProgramBank);
        }

        PushByte(state, memoryBus, (byte)(nextProgramCounter >> 8));
        PushByte(state, memoryBus, (byte)nextProgramCounter);
        var status = state.EmulationMode
            ? (byte)((state.Status & 0xEF) | (isBreak ? 0x10 : 0))
            : state.Status;
        PushByte(state, memoryBus, status);
        state.Status |= CpuStatusFlags.InterruptDisable;
        state.Status &= unchecked((byte)~CpuStatusFlags.Decimal);
        state.ProgramBank = 0;
        state.ProgramCounter = memoryBus.ReadWordInBank(vector);
    }

    private static uint GetEffectiveAddress(
        CpuState state,
        SnesMemoryBus memoryBus,
        CpuAddressingMode addressingMode,
        uint operand)
    {
        var index = addressingMode is CpuAddressingMode.DirectPageY or CpuAddressingMode.AbsoluteY
            ? state.Y
            : state.X;
        if (state.AreIndexes8Bit)
        {
            index &= ByteMask;
        }

        if (addressingMode is CpuAddressingMode.DirectPage or CpuAddressingMode.DirectPageX or CpuAddressingMode.DirectPageY)
        {
            var offset = addressingMode == CpuAddressingMode.DirectPage
                ? (ushort)operand
                : (ushort)((ushort)operand + index);
            return (ushort)(state.DirectPage + offset);
        }

        if (addressingMode is CpuAddressingMode.Long or CpuAddressingMode.LongX)
        {
            return (operand + (addressingMode == CpuAddressingMode.LongX ? (uint)index : 0u)) & 0xFFFFFF;
        }

        if (addressingMode is CpuAddressingMode.DirectPageIndirectLong or CpuAddressingMode.DirectPageIndirectLongY)
        {
            var pointerAddress = (ushort)(state.DirectPage + (byte)operand);
            var bank = memoryBus.ReadByte((uint)(ushort)(pointerAddress + 2));
            var high = memoryBus.ReadByte((uint)(ushort)(pointerAddress + 1));
            var low = memoryBus.ReadByte(pointerAddress);
            var pointer = (uint)(low | (high << 8) | (bank << 16));
            var indirectIndex = addressingMode == CpuAddressingMode.DirectPageIndirectLongY ? state.Y : 0;
            if (addressingMode == CpuAddressingMode.DirectPageIndirectLongY && state.AreIndexes8Bit)
            {
                indirectIndex &= ByteMask;
            }

            return (pointer + (uint)indirectIndex) & 0xFFFFFF;
        }

        if (addressingMode is CpuAddressingMode.DirectPageIndexedIndirect
            or CpuAddressingMode.DirectPageIndirect
            or CpuAddressingMode.DirectPageIndirectY)
        {
            var pointerOffset = (ushort)(state.DirectPage + (byte)operand);
            if (addressingMode == CpuAddressingMode.DirectPageIndexedIndirect)
            {
                var indexX = state.AreIndexes8Bit ? state.X & ByteMask : state.X;
                pointerOffset = (ushort)(pointerOffset + indexX);
            }

            var pointer = memoryBus.ReadWordInBank(pointerOffset);
            var indirectY = addressingMode == CpuAddressingMode.DirectPageIndirectY
                ? (state.AreIndexes8Bit ? state.Y & ByteMask : state.Y)
                : 0;
            return ((uint)state.DataBank << 16) | (ushort)(pointer + indirectY);
        }

        if (addressingMode == CpuAddressingMode.StackRelative)
        {
            return (ushort)(state.StackPointer + (byte)operand);
        }

        if (addressingMode == CpuAddressingMode.StackRelativeIndirectY)
        {
            var pointerAddress = (ushort)(state.StackPointer + (byte)operand);
            var pointer = memoryBus.ReadWordInBank(pointerAddress);
            var indirectY = state.AreIndexes8Bit ? state.Y & ByteMask : state.Y;
            return ((uint)state.DataBank << 16) | (ushort)(pointer + indirectY);
        }

        var absolute = addressingMode switch
        {
            CpuAddressingMode.Absolute => (ushort)operand,
            CpuAddressingMode.AbsoluteX or CpuAddressingMode.AbsoluteY => (ushort)((ushort)operand + index),
            _ => throw new ArgumentOutOfRangeException(nameof(addressingMode), addressingMode, "Not a memory addressing mode."),
        };
        return ((uint)state.DataBank << 16) | absolute;
    }

    private static void SetAccumulator(CpuState state, ushort value, bool is8Bit)
    {
        if (is8Bit)
        {
            state.Accumulator = (ushort)((state.Accumulator & 0xFF00) | (value & ByteMask));
        }
        else
        {
            state.Accumulator = value;
        }

        SetNegativeZero(state, value, is8Bit);
    }

    private static void SetIndex(CpuState state, ushort value, bool isX, bool is8Bit)
    {
        value = is8Bit ? (ushort)(value & ByteMask) : value;
        if (isX)
        {
            state.X = value;
        }
        else
        {
            state.Y = value;
        }

        SetNegativeZero(state, value, is8Bit);
    }

    private static void AddWithCarry(CpuState state, ushort value, bool is8Bit)
    {
        var mask = is8Bit ? ByteMask : ushort.MaxValue;
        var sign = is8Bit ? 0x80 : 0x8000;
        var accumulator = state.Accumulator & mask;
        var carry = (state.Status & CpuStatusFlags.Carry) != 0 ? 1 : 0;
        var binarySum = accumulator + value + carry;
        var binaryResult = (ushort)(binarySum & mask);
        var overflow = ((~(accumulator ^ value) & (accumulator ^ binaryResult) & sign) != 0);
        var decimalMode = (state.Status & CpuStatusFlags.Decimal) != 0;
        bool decimalCarry;
        ushort result;
        if (decimalMode)
        {
            result = AddDecimal((ushort)accumulator, value, carry, is8Bit, out decimalCarry);
        }
        else
        {
            result = binaryResult;
            decimalCarry = false;
        }

        SetCarry(state, decimalMode ? decimalCarry : (binarySum & ~mask) != 0);
        SetOverflow(state, overflow);
        SetAccumulator(state, result, is8Bit);
    }

    private static void SubtractWithCarry(CpuState state, ushort value, bool is8Bit)
    {
        var mask = is8Bit ? ByteMask : ushort.MaxValue;
        var sign = is8Bit ? 0x80 : 0x8000;
        var accumulator = state.Accumulator & mask;
        var borrow = (state.Status & CpuStatusFlags.Carry) != 0 ? 0 : 1;
        var binaryDifference = accumulator - value - borrow;
        var binaryResult = (ushort)(binaryDifference & mask);
        var overflow = (((accumulator ^ value) & (accumulator ^ binaryResult) & sign) != 0);
        var decimalMode = (state.Status & CpuStatusFlags.Decimal) != 0;
        bool decimalCarry;
        ushort result;
        if (decimalMode)
        {
            result = SubtractDecimal((ushort)accumulator, value, borrow, is8Bit, out decimalCarry);
        }
        else
        {
            result = binaryResult;
            decimalCarry = false;
        }

        SetCarry(state, decimalMode ? decimalCarry : binaryDifference >= 0);
        SetOverflow(state, overflow);
        SetAccumulator(state, result, is8Bit);
    }

    private static ushort AddDecimal(ushort left, ushort right, int carry, bool is8Bit, out bool carryOut)
    {
        var digits = is8Bit ? 2 : 4;
        var result = 0;
        for (var digit = 0; digit < digits; digit++)
        {
            var shift = digit * 4;
            var sum = ((left >> shift) & 0x0F) + ((right >> shift) & 0x0F) + carry;
            if (sum > 9)
            {
                sum += 6;
            }

            carry = sum > 0x0F ? 1 : 0;
            result |= (sum & 0x0F) << shift;
        }

        carryOut = carry != 0;
        return (ushort)result;
    }

    private static ushort SubtractDecimal(ushort left, ushort right, int borrow, bool is8Bit, out bool carryOut)
    {
        var digits = is8Bit ? 2 : 4;
        var result = 0;
        for (var digit = 0; digit < digits; digit++)
        {
            var shift = digit * 4;
            var difference = ((left >> shift) & 0x0F) - ((right >> shift) & 0x0F) - borrow;
            if (difference < 0)
            {
                difference -= 6;
                borrow = 1;
            }
            else
            {
                borrow = 0;
            }

            result |= (difference & 0x0F) << shift;
        }

        carryOut = borrow == 0;
        return (ushort)result;
    }

    private static void Compare(CpuState state, ushort register, ushort value, bool is8Bit)
    {
        var mask = is8Bit ? ByteMask : ushort.MaxValue;
        register &= mask;
        value &= mask;
        var result = (ushort)((register - value) & mask);
        SetCarry(state, register >= value);
        SetNegativeZero(state, result, is8Bit);
    }

    private static void ShiftAccumulator(CpuState state, bool is8Bit, bool left, bool rotate)
    {
        var mask = is8Bit ? ByteMask : ushort.MaxValue;
        var sign = is8Bit ? 0x80 : 0x8000;
        var accumulator = state.Accumulator & mask;
        var carryIn = (state.Status & CpuStatusFlags.Carry) != 0;
        int result;
        bool carryOut;

        if (left)
        {
            carryOut = (accumulator & sign) != 0;
            result = (accumulator << 1) & mask;
            if (rotate && carryIn)
            {
                result |= 1;
            }
        }
        else
        {
            carryOut = (accumulator & 1) != 0;
            result = accumulator >> 1;
            if (rotate && carryIn)
            {
                result |= sign;
            }
        }

        SetCarry(state, carryOut);
        SetAccumulator(state, (ushort)result, is8Bit);
    }

    private static bool ShouldBranch(CpuState state, CpuOperation operation)
    {
        return operation switch
        {
            CpuOperation.BranchPlus => (state.Status & CpuStatusFlags.Negative) == 0,
            CpuOperation.BranchMinus => (state.Status & CpuStatusFlags.Negative) != 0,
            CpuOperation.BranchOverflowClear => (state.Status & CpuStatusFlags.Overflow) == 0,
            CpuOperation.BranchOverflowSet => (state.Status & CpuStatusFlags.Overflow) != 0,
            CpuOperation.BranchCarryClear => (state.Status & CpuStatusFlags.Carry) == 0,
            CpuOperation.BranchCarrySet => (state.Status & CpuStatusFlags.Carry) != 0,
            CpuOperation.BranchNotEqual => (state.Status & CpuStatusFlags.Zero) == 0,
            CpuOperation.BranchEqual => (state.Status & CpuStatusFlags.Zero) != 0,
            CpuOperation.BranchAlways => true,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not a branch operation."),
        };
    }

    private static void PushValue(CpuState state, SnesMemoryBus memoryBus, ushort value, bool is8Bit)
    {
        if (!is8Bit)
        {
            PushByte(state, memoryBus, (byte)(value >> 8));
        }

        PushByte(state, memoryBus, (byte)value);
    }

    private static ushort PopValue(CpuState state, SnesMemoryBus memoryBus, bool is8Bit)
    {
        var low = PopByte(state, memoryBus);
        return is8Bit ? low : (ushort)(low | (PopByte(state, memoryBus) << 8));
    }

    private static void PushByte(CpuState state, SnesMemoryBus memoryBus, byte value)
    {
        memoryBus.WriteByte(state.StackPointer, value);
        state.StackPointer = state.EmulationMode
            ? (ushort)(0x0100 | ((state.StackPointer - 1) & ByteMask))
            : (ushort)(state.StackPointer - 1);
    }

    private static byte PopByte(CpuState state, SnesMemoryBus memoryBus)
    {
        state.StackPointer = state.EmulationMode
            ? (ushort)(0x0100 | ((state.StackPointer + 1) & ByteMask))
            : (ushort)(state.StackPointer + 1);
        return memoryBus.ReadByte(state.StackPointer);
    }

    private static void NormalizeStatus(CpuState state)
    {
        if (state.EmulationMode)
        {
            state.Status |= CpuStatusFlags.Accumulator8Bit | CpuStatusFlags.Index8Bit;
            state.StackPointer = (ushort)(0x0100 | (state.StackPointer & ByteMask));
        }

        if ((state.Status & CpuStatusFlags.Index8Bit) != 0)
        {
            state.X &= ByteMask;
            state.Y &= ByteMask;
        }
    }

    private static void EnforceEmulationStack(CpuState state)
    {
        if (state.EmulationMode)
        {
            state.StackPointer = (ushort)(0x0100 | (state.StackPointer & ByteMask));
        }
    }

    private static void SetNegativeZero(CpuState state, ushort value, bool is8Bit)
    {
        if (is8Bit)
        {
            state.SetNegativeZero8((byte)value);
        }
        else
        {
            state.SetNegativeZero16(value);
        }
    }

    private static void SetCarry(CpuState state, bool value)
    {
        if (value)
        {
            state.Status |= CpuStatusFlags.Carry;
        }
        else
        {
            state.Status &= unchecked((byte)~CpuStatusFlags.Carry);
        }
    }

    private static void SetOverflow(CpuState state, bool value)
    {
        if (value)
        {
            state.Status |= CpuStatusFlags.Overflow;
        }
        else
        {
            state.Status &= unchecked((byte)~CpuStatusFlags.Overflow);
        }
    }

    private static void SetFlag(CpuState state, byte flag)
    {
        state.Status |= flag;
    }

    private static void SetFlag(CpuState state, byte flag, bool value)
    {
        if (value)
        {
            SetFlag(state, flag);
        }
        else
        {
            ClearFlag(state, flag);
        }
    }

    private static void ClearFlag(CpuState state, byte flag)
    {
        state.Status &= unchecked((byte)~flag);
    }
}
