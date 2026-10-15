namespace Smw2Pc.Core.Cpu;

internal enum CpuOperation
{
    Nop,
    Stop,
    JumpAbsolute,
    CallAbsolute,
    ReturnFromSubroutine,
    BranchPlus,
    BranchMinus,
    BranchOverflowClear,
    BranchOverflowSet,
    BranchCarryClear,
    BranchCarrySet,
    BranchNotEqual,
    BranchEqual,
    BranchAlways,
    LoadAccumulator,
    LoadX,
    LoadY,
    StoreAccumulator,
    StoreX,
    StoreY,
    StoreZero,
    AddWithCarry,
    SubtractWithCarry,
    And,
    Or,
    ExclusiveOr,
    CompareAccumulator,
    CompareX,
    CompareY,
    IncrementAccumulator,
    DecrementAccumulator,
    ShiftAccumulatorLeft,
    ShiftAccumulatorRight,
    RotateAccumulatorLeft,
    RotateAccumulatorRight,
    IncrementX,
    DecrementX,
    IncrementY,
    DecrementY,
    TransferAToX,
    TransferAToY,
    TransferXToA,
    TransferYToA,
    TransferXToY,
    TransferYToX,
    TransferStackToX,
    TransferXToStack,
    ClearCarry,
    SetCarry,
    ClearInterrupt,
    SetInterrupt,
    ClearOverflow,
    ClearDecimal,
    SetDecimal,
    ResetStatusBits,
    SetStatusBits,
    ExchangeCarryAndEmulation,
    TransferAToDirectPage,
    TransferDirectPageToA,
    TransferAToStack,
    TransferStackToA,
    PushDirectPage,
    PullDirectPage,
    PushAccumulator,
    PullAccumulator,
    PushX,
    PullX,
    PushY,
    PullY,
    PushStatus,
    PullStatus,
    PushDataBank,
    PullDataBank,
    PushProgramBank,
}

internal enum CpuAddressingMode
{
    Implied,
    Accumulator,
    ImmediateAccumulator,
    ImmediateIndex,
    ImmediateByte,
    DirectPage,
    DirectPageX,
    DirectPageY,
    Absolute,
    AbsoluteX,
    AbsoluteY,
    Relative,
}

internal readonly record struct CpuInstructionDefinition(
    CpuOperation Operation,
    CpuAddressingMode AddressingMode);

internal static class CpuInstructionSet
{
    private static readonly IReadOnlyDictionary<byte, CpuInstructionDefinition> Definitions = CreateDefinitions();

    public static bool TryGetDefinition(byte opcode, out CpuInstructionDefinition definition)
    {
        return Definitions.TryGetValue(opcode, out definition);
    }

    private static Dictionary<byte, CpuInstructionDefinition> CreateDefinitions()
    {
        var definitions = new Dictionary<byte, CpuInstructionDefinition>();
        Add(definitions, CpuOperation.Nop, CpuAddressingMode.Implied, 0xEA);
        Add(definitions, CpuOperation.Stop, CpuAddressingMode.Implied, 0xDB);
        Add(definitions, CpuOperation.JumpAbsolute, CpuAddressingMode.Absolute, 0x4C);
        Add(definitions, CpuOperation.CallAbsolute, CpuAddressingMode.Absolute, 0x20);
        Add(definitions, CpuOperation.ReturnFromSubroutine, CpuAddressingMode.Implied, 0x60);

        Add(definitions, CpuOperation.BranchPlus, CpuAddressingMode.Relative, 0x10);
        Add(definitions, CpuOperation.BranchMinus, CpuAddressingMode.Relative, 0x30);
        Add(definitions, CpuOperation.BranchOverflowClear, CpuAddressingMode.Relative, 0x50);
        Add(definitions, CpuOperation.BranchOverflowSet, CpuAddressingMode.Relative, 0x70);
        Add(definitions, CpuOperation.BranchCarryClear, CpuAddressingMode.Relative, 0x90);
        Add(definitions, CpuOperation.BranchCarrySet, CpuAddressingMode.Relative, 0xB0);
        Add(definitions, CpuOperation.BranchNotEqual, CpuAddressingMode.Relative, 0xD0);
        Add(definitions, CpuOperation.BranchEqual, CpuAddressingMode.Relative, 0xF0);
        Add(definitions, CpuOperation.BranchAlways, CpuAddressingMode.Relative, 0x80);

        Add(definitions, CpuOperation.LoadAccumulator, CpuAddressingMode.ImmediateAccumulator, 0xA9);
        Add(definitions, CpuOperation.LoadAccumulator, CpuAddressingMode.DirectPage, 0xA5);
        Add(definitions, CpuOperation.LoadAccumulator, CpuAddressingMode.DirectPageX, 0xB5);
        Add(definitions, CpuOperation.LoadAccumulator, CpuAddressingMode.Absolute, 0xAD);
        Add(definitions, CpuOperation.LoadAccumulator, CpuAddressingMode.AbsoluteX, 0xBD);
        Add(definitions, CpuOperation.LoadAccumulator, CpuAddressingMode.AbsoluteY, 0xB9);

        Add(definitions, CpuOperation.LoadX, CpuAddressingMode.ImmediateIndex, 0xA2);
        Add(definitions, CpuOperation.LoadX, CpuAddressingMode.DirectPage, 0xA6);
        Add(definitions, CpuOperation.LoadX, CpuAddressingMode.DirectPageY, 0xB6);
        Add(definitions, CpuOperation.LoadX, CpuAddressingMode.Absolute, 0xAE);
        Add(definitions, CpuOperation.LoadX, CpuAddressingMode.AbsoluteY, 0xBE);
        Add(definitions, CpuOperation.LoadY, CpuAddressingMode.ImmediateIndex, 0xA0);
        Add(definitions, CpuOperation.LoadY, CpuAddressingMode.DirectPage, 0xA4);
        Add(definitions, CpuOperation.LoadY, CpuAddressingMode.DirectPageX, 0xB4);
        Add(definitions, CpuOperation.LoadY, CpuAddressingMode.Absolute, 0xAC);
        Add(definitions, CpuOperation.LoadY, CpuAddressingMode.AbsoluteX, 0xBC);

        Add(definitions, CpuOperation.StoreAccumulator, CpuAddressingMode.DirectPage, 0x85);
        Add(definitions, CpuOperation.StoreAccumulator, CpuAddressingMode.DirectPageX, 0x95);
        Add(definitions, CpuOperation.StoreAccumulator, CpuAddressingMode.Absolute, 0x8D);
        Add(definitions, CpuOperation.StoreAccumulator, CpuAddressingMode.AbsoluteX, 0x9D);
        Add(definitions, CpuOperation.StoreAccumulator, CpuAddressingMode.AbsoluteY, 0x99);
        Add(definitions, CpuOperation.StoreX, CpuAddressingMode.DirectPage, 0x86);
        Add(definitions, CpuOperation.StoreX, CpuAddressingMode.DirectPageY, 0x96);
        Add(definitions, CpuOperation.StoreX, CpuAddressingMode.Absolute, 0x8E);
        Add(definitions, CpuOperation.StoreY, CpuAddressingMode.DirectPage, 0x84);
        Add(definitions, CpuOperation.StoreY, CpuAddressingMode.DirectPageX, 0x94);
        Add(definitions, CpuOperation.StoreY, CpuAddressingMode.Absolute, 0x8C);
        Add(definitions, CpuOperation.StoreZero, CpuAddressingMode.DirectPage, 0x64);
        Add(definitions, CpuOperation.StoreZero, CpuAddressingMode.DirectPageX, 0x74);
        Add(definitions, CpuOperation.StoreZero, CpuAddressingMode.Absolute, 0x9C);
        Add(definitions, CpuOperation.StoreZero, CpuAddressingMode.AbsoluteX, 0x9E);

        AddAccumulatorFamily(definitions, CpuOperation.Or, 0x09, 0x05, 0x15, 0x0D, 0x1D, 0x19);
        AddAccumulatorFamily(definitions, CpuOperation.And, 0x29, 0x25, 0x35, 0x2D, 0x3D, 0x39);
        AddAccumulatorFamily(definitions, CpuOperation.ExclusiveOr, 0x49, 0x45, 0x55, 0x4D, 0x5D, 0x59);
        AddAccumulatorFamily(definitions, CpuOperation.AddWithCarry, 0x69, 0x65, 0x75, 0x6D, 0x7D, 0x79);
        AddAccumulatorFamily(definitions, CpuOperation.SubtractWithCarry, 0xE9, 0xE5, 0xF5, 0xED, 0xFD, 0xF9);
        AddAccumulatorFamily(definitions, CpuOperation.CompareAccumulator, 0xC9, 0xC5, 0xD5, 0xCD, 0xDD, 0xD9);
        Add(definitions, CpuOperation.CompareX, CpuAddressingMode.ImmediateIndex, 0xE0);
        Add(definitions, CpuOperation.CompareX, CpuAddressingMode.DirectPage, 0xE4);
        Add(definitions, CpuOperation.CompareX, CpuAddressingMode.Absolute, 0xEC);
        Add(definitions, CpuOperation.CompareY, CpuAddressingMode.ImmediateIndex, 0xC0);
        Add(definitions, CpuOperation.CompareY, CpuAddressingMode.DirectPage, 0xC4);
        Add(definitions, CpuOperation.CompareY, CpuAddressingMode.Absolute, 0xCC);

        Add(definitions, CpuOperation.IncrementAccumulator, CpuAddressingMode.Accumulator, 0x1A);
        Add(definitions, CpuOperation.DecrementAccumulator, CpuAddressingMode.Accumulator, 0x3A);
        Add(definitions, CpuOperation.ShiftAccumulatorLeft, CpuAddressingMode.Accumulator, 0x0A);
        Add(definitions, CpuOperation.RotateAccumulatorLeft, CpuAddressingMode.Accumulator, 0x2A);
        Add(definitions, CpuOperation.ShiftAccumulatorRight, CpuAddressingMode.Accumulator, 0x4A);
        Add(definitions, CpuOperation.RotateAccumulatorRight, CpuAddressingMode.Accumulator, 0x6A);
        Add(definitions, CpuOperation.IncrementX, CpuAddressingMode.Implied, 0xE8);
        Add(definitions, CpuOperation.DecrementX, CpuAddressingMode.Implied, 0xCA);
        Add(definitions, CpuOperation.IncrementY, CpuAddressingMode.Implied, 0xC8);
        Add(definitions, CpuOperation.DecrementY, CpuAddressingMode.Implied, 0x88);

        Add(definitions, CpuOperation.TransferAToX, CpuAddressingMode.Implied, 0xAA);
        Add(definitions, CpuOperation.TransferAToY, CpuAddressingMode.Implied, 0xA8);
        Add(definitions, CpuOperation.TransferXToA, CpuAddressingMode.Implied, 0x8A);
        Add(definitions, CpuOperation.TransferYToA, CpuAddressingMode.Implied, 0x98);
        Add(definitions, CpuOperation.TransferXToY, CpuAddressingMode.Implied, 0x9B);
        Add(definitions, CpuOperation.TransferYToX, CpuAddressingMode.Implied, 0xBB);
        Add(definitions, CpuOperation.TransferStackToX, CpuAddressingMode.Implied, 0xBA);
        Add(definitions, CpuOperation.TransferXToStack, CpuAddressingMode.Implied, 0x9A);

        Add(definitions, CpuOperation.ClearCarry, CpuAddressingMode.Implied, 0x18);
        Add(definitions, CpuOperation.SetCarry, CpuAddressingMode.Implied, 0x38);
        Add(definitions, CpuOperation.ClearInterrupt, CpuAddressingMode.Implied, 0x58);
        Add(definitions, CpuOperation.SetInterrupt, CpuAddressingMode.Implied, 0x78);
        Add(definitions, CpuOperation.ClearOverflow, CpuAddressingMode.Implied, 0xB8);
        Add(definitions, CpuOperation.ClearDecimal, CpuAddressingMode.Implied, 0xD8);
        Add(definitions, CpuOperation.SetDecimal, CpuAddressingMode.Implied, 0xF8);
        Add(definitions, CpuOperation.ResetStatusBits, CpuAddressingMode.ImmediateByte, 0xC2);
        Add(definitions, CpuOperation.SetStatusBits, CpuAddressingMode.ImmediateByte, 0xE2);
        Add(definitions, CpuOperation.ExchangeCarryAndEmulation, CpuAddressingMode.Implied, 0xFB);
        Add(definitions, CpuOperation.TransferAToDirectPage, CpuAddressingMode.Implied, 0x5B);
        Add(definitions, CpuOperation.TransferDirectPageToA, CpuAddressingMode.Implied, 0x7B);
        Add(definitions, CpuOperation.TransferAToStack, CpuAddressingMode.Implied, 0x1B);
        Add(definitions, CpuOperation.TransferStackToA, CpuAddressingMode.Implied, 0x3B);

        Add(definitions, CpuOperation.PushAccumulator, CpuAddressingMode.Implied, 0x48);
        Add(definitions, CpuOperation.PullAccumulator, CpuAddressingMode.Implied, 0x68);
        Add(definitions, CpuOperation.PushX, CpuAddressingMode.Implied, 0xDA);
        Add(definitions, CpuOperation.PullX, CpuAddressingMode.Implied, 0xFA);
        Add(definitions, CpuOperation.PushY, CpuAddressingMode.Implied, 0x5A);
        Add(definitions, CpuOperation.PullY, CpuAddressingMode.Implied, 0x7A);
        Add(definitions, CpuOperation.PushStatus, CpuAddressingMode.Implied, 0x08);
        Add(definitions, CpuOperation.PullStatus, CpuAddressingMode.Implied, 0x28);
        Add(definitions, CpuOperation.PushDataBank, CpuAddressingMode.Implied, 0x8B);
        Add(definitions, CpuOperation.PullDataBank, CpuAddressingMode.Implied, 0xAB);
        Add(definitions, CpuOperation.PushProgramBank, CpuAddressingMode.Implied, 0x4B);
        Add(definitions, CpuOperation.PushDirectPage, CpuAddressingMode.Implied, 0x0B);
        Add(definitions, CpuOperation.PullDirectPage, CpuAddressingMode.Implied, 0x2B);

        return definitions;
    }

    private static void AddAccumulatorFamily(
        IDictionary<byte, CpuInstructionDefinition> definitions,
        CpuOperation operation,
        byte immediate,
        byte directPage,
        byte directPageX,
        byte absolute,
        byte absoluteX,
        byte absoluteY)
    {
        Add(definitions, operation, CpuAddressingMode.ImmediateAccumulator, immediate);
        Add(definitions, operation, CpuAddressingMode.DirectPage, directPage);
        Add(definitions, operation, CpuAddressingMode.DirectPageX, directPageX);
        Add(definitions, operation, CpuAddressingMode.Absolute, absolute);
        Add(definitions, operation, CpuAddressingMode.AbsoluteX, absoluteX);
        Add(definitions, operation, CpuAddressingMode.AbsoluteY, absoluteY);
    }

    private static void Add(
        IDictionary<byte, CpuInstructionDefinition> definitions,
        CpuOperation operation,
        CpuAddressingMode addressingMode,
        params byte[] opcodes)
    {
        foreach (var opcode in opcodes)
        {
            definitions.Add(opcode, new CpuInstructionDefinition(operation, addressingMode));
        }
    }
}
