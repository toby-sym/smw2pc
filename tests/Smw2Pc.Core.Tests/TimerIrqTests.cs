using System.Buffers.Binary;
using System.Text;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Core.Tests;

[TestClass]
public sealed class TimerIrqTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void VerticalIrqSwitchesBackgroundScrollBelowStatusBar(bool pal, bool emulation)
    {
        // The IRQ performs the same BG3 scroll switch that SMW uses to share
        // Layer 3 between its status bar and tide. It must leave the HUD fixed.
        using var rom = CreateRom([0xCB, 0x80, 0xFD],
            [0xAD, 0x11, 0x42, 0xA9, 0x40, 0x8D, 0x12, 0x21,
             0xA9, 0x00, 0x8D, 0x12, 0x21, 0x40], pal);
        var bus = new SnesMemoryBus(SnesRom.Load(rom.Path));
        var runner = new NativeCpuRunner(bus);
        runner.State.EmulationMode = emulation;
        runner.State.Status = CpuStatusFlags.Accumulator8Bit | CpuStatusFlags.Index8Bit | CpuStatusFlags.Decimal;
        bus.WriteByte(0x004209, 24);
        bus.WriteByte(0x004200, 0x20);

        runner.Run(18_000);

        Assert.AreEqual((ushort)0, bus.Hardware.GetBackgroundOffsetsForScanline(16).Span[6]);
        Assert.AreEqual((ushort)64, bus.Hardware.GetBackgroundOffsetsForScanline(80).Span[6]);
        Assert.AreEqual((ushort)0x01FF, runner.State.StackPointer);
        Assert.AreEqual(CpuStatusFlags.Decimal, runner.State.Status & CpuStatusFlags.Decimal);
        Assert.AreEqual(0, bus.ReadByte(0x004211) & 0x80);
    }

    [TestMethod]
    public void MaskedIrqStaysPendingUntilAcknowledgedAndPreservesOpenBusBits()
    {
        using var rom = CreateRom([0x80, 0xFE], [0xDB]);
        var bus = new SnesMemoryBus(SnesRom.Load(rom.Path));
        var runner = new NativeCpuRunner(bus);
        bus.WriteByte(0x004209, 8);
        bus.WriteByte(0x004200, 0x20);

        runner.Run(1_000);

        Assert.IsFalse(runner.State.Halted, "A masked IRQ must not enter the handler.");
        Assert.AreEqual((ushort)0x01FF, runner.State.StackPointer);
        bus.WriteByte(0x7E0010, 0x25);
        Assert.AreEqual((byte)0xA5, bus.ReadByte(0x004211));
        bus.WriteByte(0x7E0010, 0x35);
        Assert.AreEqual((byte)0x35, bus.ReadByte(0x004211));
    }

    [TestMethod]
    public void MaskedIrqWakesWaitWithoutEnteringHandler()
    {
        using var rom = CreateRom([0xCB, 0xA9, 0x5A, 0x85, 0x10, 0xDB], [0xDB]);
        var bus = new SnesMemoryBus(SnesRom.Load(rom.Path));
        var runner = new NativeCpuRunner(bus);
        bus.WriteByte(0x004209, 8);
        bus.WriteByte(0x004200, 0x20);

        runner.Run(1_000);

        Assert.AreEqual((byte)0x5A, bus.ReadByte(0x7E0010));
        Assert.AreEqual((ushort)0x01FF, runner.State.StackPointer);
        Assert.AreNotEqual(0, bus.ReadByte(0x004211) & 0x80);
    }

    [TestMethod]
    [DataRow(0x20, 511, 24, false, 1)] // V-only ignores HTIME.
    [DataRow(0x10, 256, 511, false, 262)] // H-only ignores VTIME.
    [DataRow(0x30, 256, 24, false, 1)]
    [DataRow(0x30, 511, 24, false, 0)] // Outside the horizontal counter range.
    [DataRow(0x20, 0, 264, false, 0)] // NTSC ends before scanline 264.
    [DataRow(0x20, 0, 264, true, 1)] // The high VTIME bit is needed in PAL.
    [DataRow(0x10, 256, 0, true, 312)]
    public void TimerModesUseBothNineBitComparators(int mode, int horizontal, int vertical, bool pal, int count)
    {
        using var rom = CreateRom([0xCB, 0x80, 0xFD],
            [0xAD, 0x11, 0x42, 0xEE, 0x10, 0x00, 0x40], pal);
        var bus = new SnesMemoryBus(SnesRom.Load(rom.Path));
        var runner = new NativeCpuRunner(bus);
        runner.State.Status &= unchecked((byte)~CpuStatusFlags.InterruptDisable);
        bus.WriteByte(0x004207, (byte)horizontal);
        bus.WriteByte(0x004208, (byte)(horizontal >> 8));
        bus.WriteByte(0x004209, (byte)vertical);
        bus.WriteByte(0x00420A, (byte)(vertical >> 8));
        bus.WriteByte(0x004200, (byte)mode);

        runner.Run(18_000);

        Assert.AreEqual((byte)count, bus.ReadByte(0x7E0010));
    }

    [TestMethod]
    public void DisablingTimerClearsPendingIrq()
    {
        using var rom = CreateRom([0x80, 0xFE], [0xDB]);
        var bus = new SnesMemoryBus(SnesRom.Load(rom.Path));
        var runner = new NativeCpuRunner(bus);
        bus.WriteByte(0x004209, 8);
        bus.WriteByte(0x004200, 0x20);
        runner.Run(1_000);

        bus.WriteByte(0x004200, 0);

        Assert.AreEqual(0, bus.ReadByte(0x004211) & 0x80);
    }

    private static TemporaryRom CreateRom(byte[] program, byte[] handler, bool pal = false)
    {
        var image = new byte[0x8000];
        program.CopyTo(image, 0);
        handler.CopyTo(image, 0x100);
        Encoding.ASCII.GetBytes("TIMER IRQ TEST".PadRight(21)).CopyTo(image, 0x7FC0);
        image[0x7FD5] = 0x20;
        image[0x7FD9] = (byte)(pal ? 2 : 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x7FDC, 2), 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x7FDE, 2), 0xEDCB);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x7FFC, 2), 0x8000);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x7FFE, 2), 0x8100);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x7FEE, 2), 0x8100);
        var path = Path.Combine(Path.GetTempPath(), $"timer-irq-{Guid.NewGuid():N}.sfc");
        File.WriteAllBytes(path, image);
        return new TemporaryRom(path);
    }

    private sealed record TemporaryRom(string Path) : IDisposable
    {
        public void Dispose() => File.Delete(Path);
    }
}
