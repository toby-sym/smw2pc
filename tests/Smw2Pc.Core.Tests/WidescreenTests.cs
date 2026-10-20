using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;

namespace Smw2Pc.Core.Tests;

[TestClass]
public sealed class WidescreenTests
{
    [TestMethod]
    public void WiderPresentationPreservesCenterPixelsAndMachineState()
    {
        var runner = new NativeCpuRunner(new SnesMemoryBus(HomebrewGame.Create()));
        runner.Run(18_000 * 40);
        var before = MachineSnapshot.Capture(runner);
        var original = new byte[256 * 224 * 4];
        var wide = new byte[342 * 224 * 4];
        foreach (var progress in new[] { 0f, 0.25f, 0.5f, 1f })
        {
            SnesVideoRenderer.RenderFrame(runner.MemoryBus.Hardware, original, progress);
            SnesVideoRenderer.RenderFrame(runner.MemoryBus.Hardware, wide, progress, VideoViewport.Widescreen);
            for (var y = 0; y < 224; y++)
                Assert.IsTrue(original.AsSpan(y * 256 * 4, 256 * 4)
                    .SequenceEqual(wide.AsSpan((y * 342 + 43) * 4, 256 * 4)), $"Row {y}, progress {progress}");
        }
        CollectionAssert.AreEqual(before, MachineSnapshot.Capture(runner));
        Assert.ThrowsExactly<ArgumentException>(() =>
            SnesVideoRenderer.RenderFrame(runner.MemoryBus.Hardware, original, 1f, VideoViewport.Widescreen));
    }

    [TestMethod]
    public void ObjectsOnBothExtendedSidesUseTheirNineBitCoordinates()
    {
        var bus = new SnesMemoryBus(HomebrewGame.Create());
        bus.WriteByte(0x2100, 15);
        bus.WriteByte(0x2101, 0);
        bus.WriteByte(0x212C, 0x10);
        bus.WriteByte(0x2121, 129);
        bus.WriteByte(0x2122, 31);
        bus.WriteByte(0x2122, 0);
        bus.WriteByte(0x2115, 0x80);
        bus.WriteByte(0x2116, 0);
        bus.WriteByte(0x2117, 0);
        for (var y = 0; y < 16; y++)
        {
            bus.WriteByte(0x2118, (byte)(y < 8 ? 255 : 0));
            bus.WriteByte(0x2119, 0);
        }
        bus.WriteByte(0x2102, 0);
        bus.WriteByte(0x2103, 0);
        for (var i = 0; i < 128; i++)
        {
            bus.WriteByte(0x2104, (byte)(i == 0 ? 270 : i == 1 ? 500 : 0));
            bus.WriteByte(0x2104, (byte)(i < 2 ? 40 : 240));
            bus.WriteByte(0x2104, 0);
            bus.WriteByte(0x2104, 0);
        }
        for (var i = 0; i < 32; i++) bus.WriteByte(0x2104, (byte)(i == 0 ? 5 : 0));
        bus.Hardware.AdvanceCpuInstructions(bus, 18_000);
        var pixels = new byte[342 * 224 * 4];
        SnesVideoRenderer.RenderFrame(bus.Hardware, pixels, 1f, VideoViewport.Widescreen);
        Assert.AreEqual((byte)255, pixels[(40 * 342 + 313) * 4]); // +270 +43
        Assert.AreEqual((byte)255, pixels[(40 * 342 + 31) * 4]); // -12 +43
        Assert.AreEqual((byte)0, pixels[(40 * 342 + 57) * 4]); // no +270 wrapping to +14
    }
}
