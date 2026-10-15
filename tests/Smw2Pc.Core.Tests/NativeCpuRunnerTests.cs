using System.Buffers.Binary;
using System.Text;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Core.Tests;

[TestClass]
public sealed class NativeCpuRunnerTests
{
    [TestMethod]
    public void ResetLoadsProgramCounterFromLoromVector()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage([0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));

        var runner = new NativeCpuRunner(memoryBus);

        Assert.AreEqual((ushort)0x8000, runner.State.ProgramCounter);
        Assert.AreEqual((byte)0, runner.State.ProgramBank);
        Assert.AreEqual((byte)0x34, runner.State.Status);
        Assert.AreEqual((ushort)0x01FF, runner.State.StackPointer);
        Assert.IsTrue(runner.State.EmulationMode);
    }

    [TestMethod]
    public void NativeBlockLoadsStoresFlagsAndStops()
    {
        using var romFile = TemporaryRomFile.Create(
            CreateRomImage([0xA9, 0x2A, 0x8D, 0x00, 0x00, 0xA9, 0x00, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);

        var instructionsExecuted = runner.Run(16);

        Assert.AreEqual(4, instructionsExecuted);
        Assert.AreEqual((ushort)0, runner.State.Accumulator);
        Assert.AreEqual((ushort)0x8008, runner.State.ProgramCounter);
        Assert.IsTrue(runner.State.Halted);
        Assert.AreNotEqual(0, runner.State.Status & CpuStatusFlags.Zero);
        Assert.AreEqual((byte)0x2A, memoryBus.ReadByte(0x7E0000));
        Assert.AreEqual(1, runner.CachedBlockCount);
        Assert.AreEqual(0, runner.Run(16));
    }

    [TestMethod]
    public void NativeBlockUsesAccumulatorWidthModeForImmediateLoads()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage([0xA9, 0x34, 0x12, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);
        runner.State.EmulationMode = false;
        runner.State.Status &= unchecked((byte)~CpuStatusFlags.Accumulator8Bit);

        runner.Run(8);

        Assert.AreEqual((ushort)0x1234, runner.State.Accumulator);
        Assert.IsTrue(runner.State.Halted);
    }

    [TestMethod]
    public void EightBitAccumulatorLoadPreservesHighByteAndUpdatesFlags()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage([0xA9, 0x80, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);
        runner.State.Accumulator = 0xAB00;

        runner.Run(8);

        Assert.AreEqual((ushort)0xAB80, runner.State.Accumulator);
        Assert.AreNotEqual(0, runner.State.Status & CpuStatusFlags.Negative);
        Assert.AreEqual(0, runner.State.Status & CpuStatusFlags.Zero);
    }

    [TestMethod]
    public void SixteenBitAccumulatorStoresBothBytes()
    {
        using var romFile = TemporaryRomFile.Create(
            CreateRomImage([0xA9, 0x34, 0x12, 0x8D, 0x00, 0x00, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);
        runner.State.EmulationMode = false;
        runner.State.Status &= unchecked((byte)~CpuStatusFlags.Accumulator8Bit);

        runner.Run(8);

        Assert.AreEqual((ushort)0x1234, runner.State.Accumulator);
        Assert.AreEqual((byte)0x34, memoryBus.ReadByte(0x7E0000));
        Assert.AreEqual((byte)0x12, memoryBus.ReadByte(0x7E0001));
    }

    [TestMethod]
    public void NativeBranchTerminatesBlockAndChangesProgramCounter()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage([0x80, 0x02, 0xA9, 0x11, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);

        var instructionsExecuted = runner.Run(8);

        Assert.AreEqual(2, instructionsExecuted);
        Assert.AreEqual((ushort)0x8005, runner.State.ProgramCounter);
        Assert.AreEqual((ushort)0, runner.State.Accumulator);
        Assert.IsTrue(runner.State.Halted);
        Assert.AreEqual(2, runner.CachedBlockCount);
    }

    [TestMethod]
    public void MemoryBusMapsLoromAndMirrorsWorkRam()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage([0xA9, 0x42, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));

        Assert.AreEqual((byte)0xA9, memoryBus.ReadByte(0x008000));
        Assert.AreEqual((byte)0xA9, memoryBus.ReadByte(0x808000));

        memoryBus.WriteByte(0x000123, 0x55);

        Assert.AreEqual((byte)0x55, memoryBus.ReadByte(0x7E0123));
        Assert.AreEqual((byte)0x55, memoryBus.ReadByte(0x800123));
    }

    [TestMethod]
    public void ArithmeticInstructionsUpdateCarryOverflowAndNegativeFlags()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage([0xA9, 0x7F, 0x18, 0x69, 0x01, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);

        runner.Run(8);

        Assert.AreEqual((ushort)0x80, runner.State.Accumulator);
        Assert.AreNotEqual(0, runner.State.Status & CpuStatusFlags.Negative);
        Assert.AreNotEqual(0, runner.State.Status & CpuStatusFlags.Overflow);
        Assert.AreEqual(0, runner.State.Status & CpuStatusFlags.Carry);
    }

    [TestMethod]
    public void CompareAndConditionalBranchSkipTheUnmatchedPath()
    {
        using var romFile = TemporaryRomFile.Create(
            CreateRomImage([0xA9, 0x05, 0xC9, 0x05, 0xF0, 0x02, 0xA9, 0x99, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);

        var instructionsExecuted = runner.Run(8);

        Assert.AreEqual(4, instructionsExecuted);
        Assert.AreEqual((ushort)0x05, runner.State.Accumulator);
        Assert.AreNotEqual(0, runner.State.Status & CpuStatusFlags.Zero);
        Assert.AreEqual((ushort)0x8009, runner.State.ProgramCounter);
    }

    [TestMethod]
    public void DirectPageAndIndexedAbsoluteInstructionsUseCurrentRegisters()
    {
        using var romFile = TemporaryRomFile.Create(
            CreateRomImage([0xA5, 0x02, 0x69, 0x04, 0x9D, 0x00, 0x00, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        memoryBus.WriteByte(0x000002, 0x03);
        var runner = new NativeCpuRunner(memoryBus);
        runner.State.X = 2;

        runner.Run(8);

        Assert.AreEqual((ushort)0x07, runner.State.Accumulator);
        Assert.AreEqual((byte)0x07, memoryBus.ReadByte(0x000002));
    }

    [TestMethod]
    public void RepAndSepEndBlocksBeforeImmediateWidthChanges()
    {
        using var romFile = TemporaryRomFile.Create(
            CreateRomImage([0xC2, 0x20, 0xA9, 0x34, 0x12, 0xE2, 0x20, 0xA9, 0x56, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);
        runner.State.EmulationMode = false;

        runner.Run(8);

        Assert.AreEqual((ushort)0x1256, runner.State.Accumulator);
        Assert.IsTrue(runner.State.IsAccumulator8Bit);
        Assert.AreEqual(3, runner.CachedBlockCount);
        Assert.IsTrue(runner.State.Halted);
    }

    [TestMethod]
    public void DecimalAddSetsZeroAndCarryForBcdOverflow()
    {
        using var romFile = TemporaryRomFile.Create(
            CreateRomImage([0xF8, 0xA9, 0x99, 0x18, 0x69, 0x01, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);

        runner.Run(8);

        Assert.AreEqual((ushort)0, runner.State.Accumulator);
        Assert.AreNotEqual(0, runner.State.Status & CpuStatusFlags.Carry);
        Assert.AreNotEqual(0, runner.State.Status & CpuStatusFlags.Zero);
    }

    [TestMethod]
    public void PushAndPullAccumulatorRoundTripAndRestoreStackPointer()
    {
        using var romFile = TemporaryRomFile.Create(
            CreateRomImage([0xA9, 0x34, 0x48, 0xA9, 0x00, 0x68, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);

        runner.Run(8);

        Assert.AreEqual((ushort)0x34, runner.State.Accumulator);
        Assert.AreEqual((ushort)0x01FF, runner.State.StackPointer);
        Assert.AreEqual((byte)0x34, memoryBus.ReadByte(0x0001FF));
    }

    [TestMethod]
    public void DecimalSubtractBorrowsAndPreservesBcdResult()
    {
        using var romFile = TemporaryRomFile.Create(
            CreateRomImage([0xF8, 0xA9, 0x00, 0x38, 0xE9, 0x01, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);

        runner.Run(8);

        Assert.AreEqual((ushort)0x99, runner.State.Accumulator);
        Assert.AreEqual(0, runner.State.Status & CpuStatusFlags.Carry);
        Assert.AreNotEqual(0, runner.State.Status & CpuStatusFlags.Negative);
    }

    [TestMethod]
    public void SubroutineCallAndReturnRestoreTheStackAndResumeAfterCall()
    {
        using var romFile = TemporaryRomFile.Create(
            CreateRomImage([0x20, 0x06, 0x80, 0xDB, 0xEA, 0xEA, 0xA9, 0x77, 0x60]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);

        runner.Run(8);

        Assert.AreEqual((ushort)0x77, runner.State.Accumulator);
        Assert.AreEqual((ushort)0x01FF, runner.State.StackPointer);
        Assert.AreEqual((ushort)0x8004, runner.State.ProgramCounter);
        Assert.IsTrue(runner.State.Halted);
    }

    [TestMethod]
    public void ExchangeCarryAndEmulationSwitchesToNativeWidthModes()
    {
        using var romFile = TemporaryRomFile.Create(
            CreateRomImage([0x18, 0xFB, 0xC2, 0x30, 0xA9, 0x34, 0x12, 0xDB]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);

        runner.Run(8);

        Assert.IsFalse(runner.State.EmulationMode);
        Assert.IsFalse(runner.State.IsAccumulator8Bit);
        Assert.IsFalse(runner.State.AreIndexes8Bit);
        Assert.AreEqual((ushort)0x1234, runner.State.Accumulator);
    }

    [TestMethod]
    public void TranslatorRejectsUnsupportedOpcodesClearly()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage([0x00]));
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);

        var exception = Assert.ThrowsExactly<NotSupportedException>(() => runner.Run(1));

        StringAssert.Contains(exception.Message, "Opcode 0x00 at 0x008000");
    }

    private static byte[] CreateRomImage(byte[] program)
    {
        var image = new byte[0x8000];
        program.CopyTo(image, 0);

        const int headerOffset = 0x7FC0;
        Encoding.ASCII.GetBytes("SYNTHETIC CPU ROM".PadRight(21)).CopyTo(image, headerOffset);
        image[headerOffset + 0x15] = 0x20;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headerOffset + 0x1C, 2), 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headerOffset + 0x1E, 2), 0xEDCB);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x7FFC, 2), 0x8000);
        return image;
    }

    private sealed class TemporaryRomFile : IDisposable
    {
        private TemporaryRomFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryRomFile Create(byte[] data)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.sfc");
            File.WriteAllBytes(path, data);
            return new TemporaryRomFile(path);
        }

        public void Dispose()
        {
            File.Delete(Path);
        }
    }
}
