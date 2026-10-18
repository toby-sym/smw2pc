namespace Smw2Pc.Core.Audio;

internal sealed partial class Spc700Cpu(SnesApu bus)
{
    private const byte Carry = 0x01;
    private const byte Zero = 0x02;
    private const byte Interrupt = 0x04;
    private const byte HalfCarry = 0x08;
    private const byte Break = 0x10;
    private const byte DirectPage = 0x20;
    private const byte Overflow = 0x40;
    private const byte Negative = 0x80;

    private static readonly byte[] BaseCycles =
    [
        2,8,4,5,3,4,3,6,2,6,5,4,5,4,6,8, 2,8,4,5,4,5,5,6,5,5,6,5,2,2,4,6,
        2,8,4,5,3,4,3,6,2,6,5,4,5,4,5,4, 2,8,4,5,4,5,5,6,5,5,6,5,2,2,3,8,
        2,8,4,5,3,4,3,6,2,6,4,4,5,4,6,6, 2,8,4,5,4,5,5,6,5,5,4,5,2,2,4,3,
        2,8,4,5,3,4,3,6,2,6,4,4,5,4,5,5, 2,8,4,5,4,5,5,6,5,5,5,5,2,2,3,6,
        2,8,4,5,3,4,3,6,2,6,5,4,5,2,4,5, 2,8,4,5,4,5,5,6,5,5,5,5,2,2,12,5,
        3,8,4,5,3,4,3,6,2,6,4,4,5,2,4,4, 2,8,4,5,4,5,5,6,5,5,5,5,2,2,3,4,
        3,8,4,5,4,5,4,7,2,5,6,4,5,2,4,9, 2,8,4,5,5,6,6,7,4,5,5,5,2,2,6,3,
        2,8,4,5,3,4,3,6,2,4,5,3,4,3,4,3, 2,8,4,5,4,5,5,6,3,4,5,4,2,2,4,3,
    ];

    private byte status;
#if !SMW2PC_RECOMPILED
    private byte opcode;
#endif
    private int extraCycles;

    public int A { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int StackPointer { get; private set; }
    public ushort ProgramCounter { get; private set; }
    public bool Halted { get; private set; }
    public long TranslatedInstructionsExecuted { get; private set; }

#if !SMW2PC_RECOMPILED
    internal Action<ushort, byte>? InstructionObserved { get; set; }
#endif

    internal byte[] GetExecutionState()
    {
        return [(byte)A, (byte)X, (byte)Y, (byte)StackPointer, (byte)ProgramCounter,
            (byte)(ProgramCounter >> 8), status, (byte)(Halted ? 1 : 0)];
    }

    public void Reset()
    {
        A = 0;
        X = 0;
        Y = 0;
        StackPointer = 0xEF;
        status = 0;
        Halted = false;
        ProgramCounter = 0xFFC0;
        extraCycles = 0;
    }

    public int Step()
    {
        if (Halted)
        {
            return 2;
        }

#if SMW2PC_RECOMPILED
        TranslatedInstructionsExecuted++;
        return ExecuteTranslatedStep();
#else
        InstructionObserved?.Invoke(ProgramCounter, (byte)Read(ProgramCounter));
        opcode = Fetch();
        extraCycles = 0;
        var cycles = BaseCycles[opcode];

        if (TryExecuteAlu())
        {
            return cycles + extraCycles;
        }

        switch (opcode)
        {
            case 0x00: break;
            case 0x01: // TCALL 0..15
            case 0x11:
            case 0x21:
            case 0x31:
            case 0x41:
            case 0x51:
            case 0x61:
            case 0x71:
            case 0x81:
            case 0x91:
            case 0xA1:
            case 0xB1:
            case 0xC1:
            case 0xD1:
            case 0xE1:
            case 0xF1:
                PushWord(ProgramCounter);
                ProgramCounter = ReadWord((ushort)(0xFFDE - ((opcode >> 4) * 2)));
                break;
            case 0x02:
            case 0x22:
            case 0x42:
            case 0x62:
            case 0x82:
            case 0xA2:
            case 0xC2:
            case 0xE2:
                {
                    var address = DirectPageAddress(Fetch());
                    Write(address, (byte)(Read(address) | (1 << (opcode >> 5))));
                    break;
                }
            case 0x12:
            case 0x32:
            case 0x52:
            case 0x72:
            case 0x92:
            case 0xB2:
            case 0xD2:
            case 0xF2:
                {
                    var address = DirectPageAddress(Fetch());
                    Write(address, (byte)(Read(address) & ~(1 << (opcode >> 5))));
                    break;
                }
            case 0x03:
            case 0x23:
            case 0x43:
            case 0x63:
            case 0x83:
            case 0xA3:
            case 0xC3:
            case 0xE3:
                BranchOnDirectPageBit(set: true);
                break;
            case 0x13:
            case 0x33:
            case 0x53:
            case 0x73:
            case 0x93:
            case 0xB3:
            case 0xD3:
            case 0xF3:
                BranchOnDirectPageBit(set: false);
                break;
            case 0x0A:
            case 0x2A:
            case 0x4A:
            case 0x6A:
            case 0x8A:
            case 0xAA:
                ExecuteCarryBitOperation();
                break;
            case 0xCA:
                {
                    var (address, bit) = FetchBitAddress();
                    var value = Read(address);
                    Write(address, (byte)(((status & Carry) != 0) ? value | (1 << bit) : value & ~(1 << bit)));
                    break;
                }
            case 0xEA:
                {
                    var (address, bit) = FetchBitAddress();
                    Write(address, (byte)(Read(address) ^ (1 << bit)));
                    break;
                }
            case 0x0B: ShiftDirectPage(ShiftKind.Left, indexed: false); break;
            case 0x0C: ShiftAbsolute(ShiftKind.Left); break;
            case 0x1B: ShiftDirectPage(ShiftKind.Left, indexed: true); break;
            case 0x1C: A = Shift(A, ShiftKind.Left); break;
            case 0x2B: ShiftDirectPage(ShiftKind.RotateLeft, indexed: false); break;
            case 0x2C: ShiftAbsolute(ShiftKind.RotateLeft); break;
            case 0x3B: ShiftDirectPage(ShiftKind.RotateLeft, indexed: true); break;
            case 0x3C: A = Shift(A, ShiftKind.RotateLeft); break;
            case 0x4B: ShiftDirectPage(ShiftKind.Right, indexed: false); break;
            case 0x4C: ShiftAbsolute(ShiftKind.Right); break;
            case 0x5B: ShiftDirectPage(ShiftKind.Right, indexed: true); break;
            case 0x5C: A = Shift(A, ShiftKind.Right); break;
            case 0x6B: ShiftDirectPage(ShiftKind.RotateRight, indexed: false); break;
            case 0x6C: ShiftAbsolute(ShiftKind.RotateRight); break;
            case 0x7B: ShiftDirectPage(ShiftKind.RotateRight, indexed: true); break;
            case 0x7C: A = Shift(A, ShiftKind.RotateRight); break;
            case 0x8B: ModifyDirectPage(Fetch(), -1); break;
            case 0x8C: ModifyAbsolute(FetchWord(), -1); break;
            case 0x9B: ModifyDirectPage((Fetch() + X) & 0xFF, -1); break;
            case 0x9C: A = SetNZ(A - 1); break;
            case 0xAB: ModifyDirectPage(Fetch(), 1); break;
            case 0xAC: ModifyAbsolute(FetchWord(), 1); break;
            case 0xBB: ModifyDirectPage((Fetch() + X) & 0xFF, 1); break;
            case 0xBC: A = SetNZ(A + 1); break;
            case 0x1D: X = SetNZ(X - 1); break;
            case 0x3D: X = SetNZ(X + 1); break;
            case 0xDC: Y = SetNZ(Y - 1); break;
            case 0xFC: Y = SetNZ(Y + 1); break;
            case 0x1A: ModifyDirectPageWord(Fetch(), -1); break;
            case 0x3A: ModifyDirectPageWord(Fetch(), 1); break;
            case 0x1E: Compare(X, Read(FetchWord())); break;
            case 0x3E: Compare(X, ReadDirectPage(Fetch())); break;
            case 0x5E: Compare(Y, Read(FetchWord())); break;
            case 0x7E: Compare(Y, ReadDirectPage(Fetch())); break;
            case 0xC8: Compare(X, Fetch()); break;
            case 0xAD: Compare(Y, Fetch()); break;
            case 0x5A: CompareWord(AccumulatorPair, ReadDirectPageWord(Fetch())); break;
            case 0x7A: AddWord(ReadDirectPageWord(Fetch())); break;
            case 0x9A: SubtractWord(ReadDirectPageWord(Fetch())); break;
            case 0xBA: SetAccumulatorPair(SetNZ16(ReadDirectPageWord(Fetch()))); break;
            case 0xDA:
                {
                    var page = Fetch();
                    Read(DirectPageAddress(page));
                    WriteDirectPageWord(page, AccumulatorPair);
                    break;
                }
            case 0x0E: TestAndSet(FetchWord(), clear: false); break;
            case 0x4E: TestAndSet(FetchWord(), clear: true); break;
            case 0x0F:
                PushWord(ProgramCounter);
                Push(status);
                status = (byte)((status | Break) & ~Interrupt);
                ProgramCounter = ReadWord(0xFFDE);
                break;
            case 0x10: Branch((status & Negative) == 0); break;
            case 0x30: Branch((status & Negative) != 0); break;
            case 0x50: Branch((status & Overflow) == 0); break;
            case 0x70: Branch((status & Overflow) != 0); break;
            case 0x90: Branch((status & Carry) == 0); break;
            case 0xB0: Branch((status & Carry) != 0); break;
            case 0xD0: Branch((status & Zero) == 0); break;
            case 0xF0: Branch((status & Zero) != 0); break;
            case 0x2F: Branch(true); break;
            case 0x2E: CompareBranch(Fetch(), indexed: false); break;
            case 0xDE: CompareBranch(Fetch(), indexed: true); break;
            case 0x6E: DecrementBranch(); break;
            case 0xFE:
                Y = (Y - 1) & 0xFF;
                Branch(Y != 0);
                break;
            case 0x5F: ProgramCounter = FetchWord(); break;
            case 0x1F: ProgramCounter = ReadWord((ushort)(FetchWord() + X)); break;
            case 0x3F:
                {
                    var target = FetchWord();
                    PushWord(ProgramCounter);
                    ProgramCounter = target;
                    break;
                }
            case 0x4F:
                {
                    var target = Fetch();
                    PushWord(ProgramCounter);
                    ProgramCounter = (ushort)(0xFF00 | target);
                    break;
                }
            case 0x6F: ProgramCounter = PopWord(); break;
            case 0x7F: status = (byte)Pop(); ProgramCounter = PopWord(); break;
            case 0x20: status &= unchecked((byte)~DirectPage); break;
            case 0x40: status |= DirectPage; break;
            case 0x60: status &= unchecked((byte)~Carry); break;
            case 0x80: status |= Carry; break;
            case 0xA0: status |= Interrupt; break;
            case 0xC0: status &= unchecked((byte)~Interrupt); break;
            case 0xE0: status &= unchecked((byte)~(Overflow | HalfCarry)); break;
            case 0xED: status ^= Carry; break;
            case 0x9F: A = SetNZ((A << 4) | (A >> 4)); break;
            case 0xCF:
                {
                    var product = A * Y;
                    A = product & 0xFF;
                    Y = product >> 8;
                    SetNZ(Y);
                    break;
                }
            case 0x9E: Divide(); break;
            case 0xDF: DecimalAdjust(add: true); break;
            case 0xBE: DecimalAdjust(add: false); break;
            case 0xEF: case 0xFF: Halted = true; break;
            case 0xCD: X = SetNZ(Fetch()); break;
            case 0x8D: Y = SetNZ(Fetch()); break;
            case 0xE8: A = SetNZ(Fetch()); break;
            case 0xE4: A = SetNZ(ReadDirectPage(Fetch())); break;
            case 0xE5: A = SetNZ(Read(FetchWord())); break;
            case 0xE6: A = SetNZ(ReadDirectPage(X)); break;
            case 0xE7: A = SetNZ(Read(ReadDirectPageWord((Fetch() + X) & 0xFF))); break;
            case 0xF4: A = SetNZ(ReadDirectPage((Fetch() + X) & 0xFF)); break;
            case 0xF5: A = SetNZ(Read((ushort)(FetchWord() + X))); break;
            case 0xF6: A = SetNZ(Read((ushort)(FetchWord() + Y))); break;
            case 0xF7: A = SetNZ(Read((ushort)(ReadDirectPageWord(Fetch()) + Y))); break;
            case 0xBF: A = SetNZ(ReadDirectPage(X)); X = (X + 1) & 0xFF; break;
            case 0xF8: X = SetNZ(ReadDirectPage(Fetch())); break;
            case 0xF9: X = SetNZ(ReadDirectPage((Fetch() + Y) & 0xFF)); break;
            case 0xE9: X = SetNZ(Read(FetchWord())); break;
            case 0xEB: Y = SetNZ(ReadDirectPage(Fetch())); break;
            case 0xFB: Y = SetNZ(ReadDirectPage((Fetch() + X) & 0xFF)); break;
            case 0xEC: Y = SetNZ(Read(FetchWord())); break;
            case 0xC4: WriteDirectPage(Fetch(), A); break;
            case 0xC5: Write(FetchWord(), (byte)A); break;
            case 0xC6: WriteDirectPage(X, A); break;
            case 0xC7: Write(ReadDirectPageWord((Fetch() + X) & 0xFF), (byte)A); break;
            case 0xD4: WriteDirectPage((Fetch() + X) & 0xFF, A); break;
            case 0xD5: Write((ushort)(FetchWord() + X), (byte)A); break;
            case 0xD6: Write((ushort)(FetchWord() + Y), (byte)A); break;
            case 0xD7: Write((ushort)(ReadDirectPageWord(Fetch()) + Y), (byte)A); break;
            case 0xAF: WriteDirectPage(X, A); X = (X + 1) & 0xFF; break;
            case 0xD8: WriteDirectPage(Fetch(), X); break;
            case 0xD9: WriteDirectPage((Fetch() + Y) & 0xFF, X); break;
            case 0xC9: Write(FetchWord(), (byte)X); break;
            case 0xCB: WriteDirectPage(Fetch(), Y); break;
            case 0xDB: WriteDirectPage((Fetch() + X) & 0xFF, Y); break;
            case 0xCC: Write(FetchWord(), (byte)Y); break;
            case 0x8F: { var value = Fetch(); WriteDirectPage(Fetch(), value); break; }
            case 0xFA: { var source = Fetch(); var value = ReadDirectPage(source); WriteDirectPage(Fetch(), value); break; }
            case 0x7D: A = SetNZ(X); break;
            case 0xDD: A = SetNZ(Y); break;
            case 0x5D: X = SetNZ(A); break;
            case 0xFD: Y = SetNZ(A); break;
            case 0x9D: X = SetNZ(StackPointer); break;
            case 0xBD: StackPointer = X; break;
            case 0x2D: Push((byte)A); break;
            case 0x4D: Push((byte)X); break;
            case 0x6D: Push((byte)Y); break;
            case 0x0D: Push(status); break;
            case 0xAE: A = Pop(); break;
            case 0xCE: X = Pop(); break;
            case 0xEE: Y = Pop(); break;
            case 0x8E: status = (byte)Pop(); break;
            default:
                throw new InvalidOperationException($"Unimplemented SPC700 opcode 0x{opcode:X2} at 0x{ProgramCounter - 1:X4}.");
        }

        return cycles + extraCycles;
#endif
    }

#if !SMW2PC_RECOMPILED
    private bool TryExecuteAlu()
    {
        var family = opcode & 0xE0;
        if (family > 0xA0)
        {
            return false;
        }

        var form = opcode & 0x1F;
        if (form is not (0x04 or 0x05 or 0x06 or 0x07 or 0x08 or 0x09
            or 0x14 or 0x15 or 0x16 or 0x17 or 0x18 or 0x19))
        {
            return false;
        }

        var operation = (AluOperation)family;
        switch (form)
        {
            case 0x04: ApplyToAccumulator(operation, ReadDirectPage(Fetch())); break;
            case 0x05: ApplyToAccumulator(operation, Read(FetchWord())); break;
            case 0x06: ApplyToAccumulator(operation, ReadDirectPage(X)); break;
            case 0x07: ApplyToAccumulator(operation, Read(ReadDirectPageWord((Fetch() + X) & 0xFF))); break;
            case 0x08: ApplyToAccumulator(operation, Fetch()); break;
            case 0x09: ExecuteDirectPageToDirectPage(operation); break;
            case 0x14: ApplyToAccumulator(operation, ReadDirectPage((Fetch() + X) & 0xFF)); break;
            case 0x15: ApplyToAccumulator(operation, Read((ushort)(FetchWord() + X))); break;
            case 0x16: ApplyToAccumulator(operation, Read((ushort)(FetchWord() + Y))); break;
            case 0x17: ApplyToAccumulator(operation, Read((ushort)(ReadDirectPageWord(Fetch()) + Y))); break;
            case 0x18: ExecuteImmediateToDirectPage(operation); break;
            case 0x19:
                {
                    var valueY = ReadDirectPage(Y);
                    var valueX = ReadDirectPage(X);
                    if (operation == AluOperation.Compare)
                    {
                        Compare(valueX, valueY);
                    }
                    else
                    {
                        WriteDirectPage(X, ApplyAlu(operation, valueX, valueY));
                    }

                    break;
                }
            default: return false;
        }

        return true;
    }
#endif

    private void ExecuteDirectPageToDirectPage(AluOperation operation)
    {
        var source = ReadDirectPage(Fetch());
        var target = Fetch();
        var oldValue = ReadDirectPage(target);
        if (operation == AluOperation.Compare)
        {
            Compare(oldValue, source);
            return;
        }

        WriteDirectPage(target, ApplyAlu(operation, oldValue, source));
    }

    private void ExecuteImmediateToDirectPage(AluOperation operation)
    {
        var immediate = Fetch();
        var target = Fetch();
        var oldValue = ReadDirectPage(target);
        if (operation == AluOperation.Compare)
        {
            Compare(oldValue, immediate);
            return;
        }

        WriteDirectPage(target, ApplyAlu(operation, oldValue, immediate));
    }

    private void ApplyToAccumulator(AluOperation operation, int value)
    {
        if (operation == AluOperation.Compare)
        {
            Compare(A, value);
        }
        else
        {
            A = ApplyAlu(operation, A, value);
        }
    }

    private byte ApplyAlu(AluOperation operation, int left, int right)
    {
        return operation switch
        {
            AluOperation.Or => SetNZ(left | right),
            AluOperation.And => SetNZ(left & right),
            AluOperation.ExclusiveOr => SetNZ(left ^ right),
            AluOperation.Add => Add(left, right),
            AluOperation.Subtract => Add(left, right ^ 0xFF),
            _ => throw new InvalidOperationException("Compare does not produce a result."),
        };
    }

    private byte Add(int left, int right)
    {
        var sum = left + right + ((status & Carry) != 0 ? 1 : 0);
        status = SetFlag(status, Carry, sum > 0xFF);
        status = SetFlag(status, HalfCarry, ((left ^ right ^ sum) & 0x10) != 0);
        status = SetFlag(status, Overflow, ((left ^ sum) & (right ^ sum) & 0x80) != 0);
        return SetNZ(sum);
    }

    private void Compare(int left, int right)
    {
        var result = left - right;
        status = SetFlag(status, Carry, result >= 0);
        SetNZ(result);
    }

    private byte SetNZ(int value)
    {
        var result = value & 0xFF;
        status = SetFlag(status, Negative, (result & 0x80) != 0);
        status = SetFlag(status, Zero, result == 0);
        return (byte)result;
    }

    private static byte SetFlag(byte flags, byte mask, bool enabled)
    {
        return enabled ? (byte)(flags | mask) : (byte)(flags & ~mask);
    }

    private void Branch(bool condition)
    {
        var displacement = (sbyte)Fetch();
        if (condition)
        {
            ProgramCounter = (ushort)(ProgramCounter + displacement);
            extraCycles += 2;
        }
    }

#if !SMW2PC_RECOMPILED
    private void BranchOnDirectPageBit(bool set)
    {
        var pageOffset = Fetch();
        var value = ReadDirectPage(pageOffset);
        var displacement = (sbyte)Fetch();
        var bitIsSet = (value & (1 << ((opcode >> 5) & 7))) != 0;
        if (bitIsSet == set)
        {
            ProgramCounter = (ushort)(ProgramCounter + displacement);
            extraCycles += 2;
        }
    }

#endif

    private void CompareBranch(int pageOffset, bool indexed)
    {
        var value = ReadDirectPage((pageOffset + (indexed ? X : 0)) & 0xFF);
        Branch(A != value);
    }

    private void DecrementBranch()
    {
        var pageOffset = Fetch();
        var value = (ReadDirectPage(pageOffset) - 1) & 0xFF;
        WriteDirectPage(pageOffset, (byte)value);
        Branch(value != 0);
    }

#if !SMW2PC_RECOMPILED
    private void ExecuteCarryBitOperation()
    {
        var (address, bit) = FetchBitAddress();
        var bitValue = (Read(address) >> bit) & 1;
        switch (opcode)
        {
            case 0x0A: status |= (byte)(bitValue * Carry); break;
            case 0x2A: status |= (byte)((bitValue ^ 1) * Carry); break;
            case 0x4A: status = SetFlag(status, Carry, (status & Carry) != 0 && bitValue != 0); break;
            case 0x6A: status = SetFlag(status, Carry, (status & Carry) != 0 && bitValue == 0); break;
            case 0x8A: status ^= (byte)(bitValue * Carry); break;
            case 0xAA: status = SetFlag(status, Carry, bitValue != 0); break;
        }
    }

#endif

    private (ushort Address, int Bit) FetchBitAddress()
    {
        var encoded = FetchWord();
        return ((ushort)(encoded & 0x1FFF), (encoded >> 13) & 7);
    }

    private void ShiftDirectPage(ShiftKind kind, bool indexed)
    {
        var offset = (int)Fetch();
        if (indexed)
        {
            offset = (offset + X) & 0xFF;
        }

        var value = ReadDirectPage(offset);
        WriteDirectPage(offset, Shift(value, kind));
    }

    private void ShiftAbsolute(ShiftKind kind)
    {
        var address = FetchWord();
        Write(address, Shift(Read(address), kind));
    }

    private byte Shift(int value, ShiftKind kind)
    {
        int result;
        var carryIn = (status & Carry) != 0 ? 1 : 0;
        switch (kind)
        {
            case ShiftKind.Left:
                status = SetFlag(status, Carry, (value & 0x80) != 0);
                result = value << 1;
                break;
            case ShiftKind.Right:
                status = SetFlag(status, Carry, (value & 1) != 0);
                result = value >> 1;
                break;
            case ShiftKind.RotateLeft:
                status = SetFlag(status, Carry, (value & 0x80) != 0);
                result = (value << 1) | carryIn;
                break;
            default:
                status = SetFlag(status, Carry, (value & 1) != 0);
                result = (value >> 1) | (carryIn << 7);
                break;
        }

        return SetNZ(result);
    }

    private void ModifyDirectPage(int pageOffset, int delta)
    {
        var address = DirectPageAddress(pageOffset);
        Write(address, SetNZ(Read(address) + delta));
    }

    private void ModifyAbsolute(ushort address, int delta)
    {
        Write(address, SetNZ(Read(address) + delta));
    }

    private void ModifyDirectPageWord(int pageOffset, int delta)
    {
        var value = ReadDirectPageWord(pageOffset);
        WriteDirectPageWord(pageOffset, SetNZ16(value + delta));
    }

    private void AddWord(int value)
    {
        status &= unchecked((byte)~Carry);
        var result = AccumulatorPair + value;
        status = SetFlag(status, Carry, result > 0xFFFF);
        status = SetFlag(status, HalfCarry, ((AccumulatorPair ^ value ^ result) & 0x1000) != 0);
        status = SetFlag(status, Overflow, ((AccumulatorPair ^ result) & (value ^ result) & 0x8000) != 0);
        SetAccumulatorPair(SetNZ16(result));
    }

    private void SubtractWord(int value)
    {
        status |= Carry;
        var complement = value ^ 0xFFFF;
        var result = AccumulatorPair + complement + ((status & Carry) != 0 ? 1 : 0);
        status = SetFlag(status, Carry, result > 0xFFFF);
        status = SetFlag(status, HalfCarry, ((AccumulatorPair ^ complement ^ result) & 0x1000) != 0);
        status = SetFlag(status, Overflow, ((AccumulatorPair ^ result) & (complement ^ result) & 0x8000) != 0);
        SetAccumulatorPair(SetNZ16(result));
    }

    private void CompareWord(int left, int right)
    {
        var result = left - right;
        status = SetFlag(status, Carry, result >= 0);
        SetNZ16(result);
    }

    private int SetNZ16(int value)
    {
        var result = value & 0xFFFF;
        status = SetFlag(status, Negative, (result & 0x8000) != 0);
        status = SetFlag(status, Zero, result == 0);
        return result;
    }

    private int AccumulatorPair => (Y << 8) | A;

    private void SetAccumulatorPair(int value)
    {
        A = value & 0xFF;
        Y = (value >> 8) & 0xFF;
    }

    private void TestAndSet(ushort address, bool clear)
    {
        var value = Read(address);
        SetNZ(A - value);
        Write(address, (byte)(clear ? value & ~A : value | A));
    }

    private void Divide()
    {
        var dividend = AccumulatorPair;
        var divisor = X;
        status = SetFlag(status, HalfCarry, (X & 15) <= (Y & 15));
        status = SetFlag(status, Overflow, Y >= X);
        if (divisor == 0)
        {
            A = Y = 0xFF;
        }
        else if (Y < divisor * 2)
        {
            A = dividend / divisor;
            Y = dividend % divisor;
        }
        else
        {
            var remainder = dividend - (divisor << 9);
            A = 255 - remainder / (256 - divisor);
            Y = divisor + remainder % (256 - divisor);
        }

        A &= 0xFF;
        Y &= 0xFF;
        SetNZ(A);
    }

    private void DecimalAdjust(bool add)
    {
        if (add)
        {
            if ((status & Carry) != 0 || A > 0x99)
            {
                A += 0x60;
                status |= Carry;
            }

            if ((status & HalfCarry) != 0 || (A & 0x0F) > 9)
            {
                A += 6;
            }
        }
        else
        {
            if ((status & Carry) == 0 || A > 0x99)
            {
                A -= 0x60;
                status &= unchecked((byte)~Carry);
            }

            if ((status & HalfCarry) == 0 || (A & 0x0F) > 9)
            {
                A -= 6;
            }
        }

        A = SetNZ(A);
    }

    private int DirectPageAddress(int offset)
    {
        return ((status & DirectPage) != 0 ? 0x100 : 0) | (offset & 0xFF);
    }

    private int ReadDirectPage(int offset) => Read(DirectPageAddress(offset));

    private void WriteDirectPage(int offset, int value) => Write(DirectPageAddress(offset), (byte)value);

    private int ReadDirectPageWord(int offset)
    {
        return ReadDirectPage(offset) | (ReadDirectPage((offset + 1) & 0xFF) << 8);
    }

    private void WriteDirectPageWord(int offset, int value)
    {
        WriteDirectPage(offset, (byte)value);
        WriteDirectPage((offset + 1) & 0xFF, (byte)(value >> 8));
    }

    private int Read(ushort address) => bus.ReadSpcMemory(address);

    private int Read(int address) => bus.ReadSpcMemory((ushort)address);

    private void Write(int address, byte value) => bus.WriteSpcMemory((ushort)address, value);

    private ushort ReadWord(ushort address)
    {
        return (ushort)(Read(address) | (Read((ushort)(address + 1)) << 8));
    }

    private byte Fetch()
    {
        var value = (byte)Read(ProgramCounter);
        ProgramCounter++;
        return value;
    }

    private ushort FetchWord()
    {
        var low = Fetch();
        return (ushort)(low | (Fetch() << 8));
    }

    private void Push(byte value)
    {
        Write(0x0100 | StackPointer, value);
        StackPointer = (StackPointer - 1) & 0xFF;
    }

    private int Pop()
    {
        StackPointer = (StackPointer + 1) & 0xFF;
        return Read((ushort)(0x0100 | StackPointer));
    }

    private void PushWord(ushort value)
    {
        Push((byte)(value >> 8));
        Push((byte)value);
    }

    private ushort PopWord()
    {
        var low = Pop();
        return (ushort)(low | (Pop() << 8));
    }

    private enum AluOperation
    {
        Or = 0x00,
        And = 0x20,
        ExclusiveOr = 0x40,
        Compare = 0x60,
        Add = 0x80,
        Subtract = 0xA0,
    }

    private enum ShiftKind { Left, Right, RotateLeft, RotateRight }
}
