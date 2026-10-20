using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Rom;
using System.Text;

namespace Smw2Pc.Core.Tests;

[TestClass]
public sealed class WidescreenTests
{
    [TestMethod]
    public void UnrecognizedSmwLayoutsRetainTheOriginalFraming()
    {
        var bytes = HomebrewGame.Create().Data.ToArray();
        Encoding.ASCII.GetBytes("SUPER MARIOWORLD".PadRight(21)).CopyTo(bytes, 0x7FC0);
        var bus = new SnesMemoryBus(SnesRom.LoadBytes(bytes, "different-layout.sfc"));
        var runner = new NativeCpuRunner(bus);
        bus.Widescreen.Enabled = true;
        runner.Run(18_000 * 40);
        Assert.IsTrue(bus.Widescreen.IsSuperMarioWorld);
        Assert.IsFalse(bus.Widescreen.IsSupported);
        var original = new byte[256 * 224 * 4];
        var wide = new byte[342 * 224 * 4];
        SnesVideoRenderer.RenderFrame(bus.Hardware, original, 1f);
        SnesVideoRenderer.RenderFrame(bus.Hardware, wide, 1f, VideoViewport.Widescreen);
        for (var y = 0; y < 224; y++)
        {
            Assert.IsTrue(original.AsSpan(y * 256 * 4, 256 * 4)
                .SequenceEqual(wide.AsSpan((y * 342 + 43) * 4, 256 * 4)));
            for (var x = 0; x < 342; x++)
            {
                if (x is >= 43 and < 299) continue;
                var pixel = (y * 342 + x) * 4;
                Assert.AreEqual((byte)0, wide[pixel]);
                Assert.AreEqual((byte)0, wide[pixel + 1]);
                Assert.AreEqual((byte)0, wide[pixel + 2]);
                Assert.AreEqual((byte)255, wide[pixel + 3]);
            }
        }
    }

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
        var bus = CreateObjectScene();
        WriteObjects(bus, (270, 40), (500, 40));
        bus.Hardware.AdvanceCpuInstructions(bus, 18_000);
        var pixels = new byte[342 * 224 * 4];
        SnesVideoRenderer.RenderFrame(bus.Hardware, pixels, 1f, VideoViewport.Widescreen);
        Assert.AreEqual((byte)255, pixels[(40 * 342 + 313) * 4]); // +270 +43
        Assert.AreEqual((byte)255, pixels[(40 * 342 + 31) * 4]); // -12 +43
        Assert.AreEqual((byte)0, pixels[(40 * 342 + 57) * 4]); // no +270 wrapping to +14
    }

    [TestMethod]
    [DataRow(254, 258, 256)]
    [DataRow(258, 254, 256)]
    [DataRow(510, 2, 0)]
    [DataRow(2, 510, 0)]
    public void ObjectInterpolationCrossesNineBitBoundariesContinuously(int previous, int current, int halfway)
    {
        var bus = CreateObjectScene();
        WriteObjects(bus, (previous, 40));
        bus.Hardware.AdvanceCpuInstructions(bus, 18_000);
        WriteObjects(bus, (current, 40));
        bus.Hardware.AdvanceCpuInstructions(bus, 18_000);
        var original = new byte[256 * 224 * 4];
        var wide = new byte[342 * 224 * 4];
        SnesVideoRenderer.RenderFrame(bus.Hardware, original, 0.5f);
        SnesVideoRenderer.RenderFrame(bus.Hardware, wide, 0.5f, VideoViewport.Widescreen);
        Assert.AreEqual((byte)255, wide[(40 * 342 + halfway + 43) * 4]);
        for (var y = 0; y < 224; y++)
            Assert.IsTrue(original.AsSpan(y * 256 * 4, 256 * 4)
                .SequenceEqual(wide.AsSpan((y * 342 + 43) * 4, 256 * 4)), $"Row {y}");
    }

    [TestMethod]
    public void ModeSevenSamplesExtraWorldColumnsAndFullWidthWindowsCoverThem()
    {
        var bus = new SnesMemoryBus(HomebrewGame.Create());
        bus.WriteByte(0x2100, 15);
        bus.WriteByte(0x2105, 7);
        bus.WriteByte(0x212C, 1);
        bus.WriteByte(0x211B, 0); bus.WriteByte(0x211B, 1); // A = 1
        bus.WriteByte(0x211C, 0); bus.WriteByte(0x211C, 0); // B = 0
        bus.WriteByte(0x211D, 0); bus.WriteByte(0x211D, 0); // C = 0
        bus.WriteByte(0x211E, 0); bus.WriteByte(0x211E, 1); // D = 1
        bus.WriteByte(0x2121, 1);
        bus.WriteByte(0x2122, 31); bus.WriteByte(0x2122, 0); // red
        bus.WriteByte(0x2122, 0xE0); bus.WriteByte(0x2122, 3); // green
        bus.WriteByte(0x2115, 0x80);
        bus.WriteByte(0x2116, 0); bus.WriteByte(0x2117, 0);
        for (var word = 0; word < 0x8000; word++)
        {
            bus.WriteByte(0x2118, (byte)(word % 128 >= 32 ? 1 : 0));
            bus.WriteByte(0x2119, (byte)(word < 64 ? 1 : word < 128 ? 2 : 0));
        }
        bus.Hardware.AdvanceCpuInstructions(bus, 18_000);
        var pixels = new byte[342 * 224 * 4];
        SnesVideoRenderer.RenderFrame(bus.Hardware, pixels, 1f, VideoViewport.Widescreen);
        var center = (40 * 342 + 43) * 4;
        var right = (40 * 342 + 300) * 4;
        Assert.AreEqual((byte)255, pixels[center]);
        Assert.AreEqual((byte)0, pixels[center + 1]);
        Assert.AreEqual((byte)0, pixels[right]);
        Assert.AreEqual((byte)255, pixels[right + 1]);
        bus.WriteByte(0x2125, 0x20); // Color window 1 enabled, 0..255.
        bus.WriteByte(0x2126, 0); bus.WriteByte(0x2127, 255);
        bus.WriteByte(0x2130, 0x80); // Clip main screen inside the color window.
        bus.Hardware.AdvanceCpuInstructions(bus, 18_000);
        SnesVideoRenderer.RenderFrame(bus.Hardware, pixels, 1f, VideoViewport.Widescreen);
        for (var x = 0; x < 342; x++)
        {
            var pixel = (40 * 342 + x) * 4;
            Assert.AreEqual((byte)0, pixels[pixel]);
            Assert.AreEqual((byte)0, pixels[pixel + 1]);
        }
    }

    private static SnesMemoryBus CreateObjectScene()
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
        return bus;
    }

    private static void WriteObjects(SnesMemoryBus bus, params (int X, int Y)[] objects)
    {
        bus.WriteByte(0x2102, 0);
        bus.WriteByte(0x2103, 0);
        for (var i = 0; i < 128; i++)
        {
            bus.WriteByte(0x2104, (byte)(i < objects.Length ? objects[i].X : 0));
            bus.WriteByte(0x2104, (byte)(i < objects.Length ? objects[i].Y : 240));
            bus.WriteByte(0x2104, 0);
            bus.WriteByte(0x2104, 0);
        }
        for (var group = 0; group < 32; group++)
        {
            byte high = 0;
            for (var i = 0; i < 4 && group * 4 + i < objects.Length; i++)
                high |= (byte)(((objects[group * 4 + i].X >> 8) & 1) << (i * 2));
            bus.WriteByte(0x2104, high);
        }
    }
}
