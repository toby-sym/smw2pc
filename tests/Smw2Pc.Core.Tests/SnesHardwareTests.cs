using System.Buffers.Binary;
using System.Text;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Core.Tests;

[TestClass]
public sealed class SnesHardwareTests
{
    [TestMethod]
    public void PpuVramPortsWriteAndReadSequentialBytes()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage());
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));

        memoryBus.WriteByte(0x002115, 0x80);
        SetVideoRamAddress(memoryBus, 0);
        memoryBus.WriteByte(0x002118, 0x34);
        memoryBus.WriteByte(0x002119, 0x12);
        SetVideoRamAddress(memoryBus, 0);

        Assert.AreEqual((byte)0x34, memoryBus.ReadByte(0x002139));
        Assert.AreEqual((byte)0x12, memoryBus.ReadByte(0x80213A));
        Assert.AreEqual((byte)0x34, memoryBus.Hardware.VideoRam.Span[0]);
        Assert.AreEqual((byte)0x12, memoryBus.Hardware.VideoRam.Span[1]);
    }

    [TestMethod]
    [DataRow(0x00, 1)]
    [DataRow(0x01, 32)]
    [DataRow(0x02, 128)]
    [DataRow(0x80, 1)]
    [DataRow(0x81, 32)]
    [DataRow(0x82, 128)]
    public void PpuVramPortsIgnoreAddressBitFifteenAndWrapAllIncrementModes(int vmain, int increment)
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage());
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var incrementOnHighByte = (vmain & 0x80) != 0;
        memoryBus.WriteByte(0x002115, (byte)vmain);

        SetVideoRamAddress(memoryBus, 0xFFFF);
        WriteVramWord(memoryBus, 0x34, 0x12, incrementOnHighByte);
        WriteVramWord(memoryBus, 0x56, 0x78, incrementOnHighByte);

        var videoRam = memoryBus.Hardware.VideoRam.Span;
        var wrappedWordAddress = increment - 1;
        Assert.AreEqual((byte)0x34, videoRam[0xFFFE]);
        Assert.AreEqual((byte)0x12, videoRam[0xFFFF]);
        Assert.AreEqual((byte)0x56, videoRam[wrappedWordAddress * 2]);
        Assert.AreEqual((byte)0x78, videoRam[wrappedWordAddress * 2 + 1]);

        SetVideoRamAddress(memoryBus, 0xFFFF);
        Assert.AreEqual(((byte)0x34, (byte)0x12), ReadVramWord(memoryBus, incrementOnHighByte));
        Assert.AreEqual(((byte)0x56, (byte)0x78), ReadVramWord(memoryBus, incrementOnHighByte));
    }

    [TestMethod]
    public void HorizontalScrollWritesPreserveEveryFineScrollBitInBothDirections()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage());
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        for (var background = 0; background < 4; background++)
        {
            var register = (uint)(0x00210D + (background * 2));
            for (var step = 0; step < 2048; step++)
            {
                var offset = step < 1024 ? step : 2047 - step;
                memoryBus.WriteByte(register, (byte)offset);
                memoryBus.WriteByte(register, (byte)(offset >> 8));
                Assert.AreEqual((ushort)offset, memoryBus.Hardware.GetBackgroundHorizontalOffset(background),
                    $"BG{background + 1} scroll changed at offset {offset}.");
            }
        }
    }

    [TestMethod]
    public void HorizontalScrollFineLatchIsSharedAcrossBackgroundsAndSurvivesVerticalWrites()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage());
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        memoryBus.WriteByte(0x00210D, 0x05);
        memoryBus.WriteByte(0x002110, 0xA8);
        memoryBus.WriteByte(0x002111, 0x01);
        Assert.AreEqual((ushort)0x1AD, memoryBus.Hardware.GetBackgroundHorizontalOffset(2));
    }

    [TestMethod]
    public void WramDataPortSharesStorageWithDirectWramMapping()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage());
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));

        SetWorkRamPortAddress(memoryBus, 0x1ABCD);
        memoryBus.WriteByte(0x002180, 0x5A);

        Assert.AreEqual((byte)0x5A, memoryBus.ReadByte(0x7FABCD));
        SetWorkRamPortAddress(memoryBus, 0x1ABCD);
        Assert.AreEqual((byte)0x5A, memoryBus.ReadByte(0x002180));
    }

    [TestMethod]
    public void ControllerPortsLatchAndShiftButtonsInSerialOrder()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage());
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        memoryBus.Hardware.SetControllerButtons(
            0,
            SnesControllerButtons.B | SnesControllerButtons.Start | SnesControllerButtons.A);

        memoryBus.WriteByte(0x004016, 1);
        memoryBus.WriteByte(0x004016, 0);

        Assert.AreEqual((byte)1, memoryBus.ReadByte(0x004016) & 1);
        Assert.AreEqual((byte)0, memoryBus.ReadByte(0x004016) & 1);
        Assert.AreEqual((byte)0, memoryBus.ReadByte(0x004016) & 1);
        Assert.AreEqual((byte)1, memoryBus.ReadByte(0x004016) & 1);
        Assert.AreEqual((byte)0x80, memoryBus.ReadByte(0x004218));
        Assert.AreEqual((byte)0x90, memoryBus.ReadByte(0x004219));
    }

    [TestMethod]
    public void ApuPortDirectionsAreIndependentAndUnmappedReadsReturnOpenBus()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage());
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));

        memoryBus.WriteByte(0x002142, 0x6B);
        Assert.AreEqual((byte)0, memoryBus.ReadByte(0x00217E));

        memoryBus.WriteByte(0x7E0000, 0xA5);
        Assert.AreEqual((byte)0xA5, memoryBus.ReadByte(0x002120));
    }

    [TestMethod]
    public void RendererMovesObjectsWithoutBlendingPaletteColors()
    {
        var image = CreateRomImage();
        for (var index = 0; index < 31; index++)
        {
            image[index] = 0xEA;
        }

        image[31] = 0x80;
        image[32] = 0xDF;
        using var romFile = TemporaryRomFile.Create(image);
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);
        memoryBus.WriteByte(0x002100, 0x0F);
        memoryBus.WriteByte(0x002101, 0);
        memoryBus.WriteByte(0x00212C, 0x10);
        memoryBus.WriteByte(0x002121, 129);
        memoryBus.WriteByte(0x002122, 0x1F);
        memoryBus.WriteByte(0x002122, 0);
        memoryBus.WriteByte(0x002115, 0x80);
        SetVideoRamAddress(memoryBus, 0);
        for (var row = 0; row < 8; row++)
        {
            memoryBus.WriteByte(0x002118, 0xFF);
            memoryBus.WriteByte(0x002119, 0);
        }

        for (var row = 0; row < 8; row++)
        {
            memoryBus.WriteByte(0x002118, 0);
            memoryBus.WriteByte(0x002119, 0);
        }

        SetVideoRamAddress(memoryBus, 0x10);
        for (var row = 0; row < 8; row++)
        {
            memoryBus.WriteByte(0x002118, 0xFF);
            memoryBus.WriteByte(0x002119, 0);
        }

        for (var row = 0; row < 8; row++)
        {
            memoryBus.WriteByte(0x002118, 0);
            memoryBus.WriteByte(0x002119, 0);
        }

        SetObjectZero(memoryBus, x: 20, y: 20);
        runner.Run(18_000);
        SetObjectZero(memoryBus, x: 29, y: 20);
        runner.Run(18_000);
        Assert.AreEqual((byte)20, memoryBus.Hardware.PreviousObjectAttributeMemory.Span[0]);
        Assert.AreEqual((byte)29, memoryBus.Hardware.CurrentFrameObjectAttributeMemory.Span[0]);

        var pixels = new byte[SnesVideoRenderer.Width * SnesVideoRenderer.Height * SnesVideoRenderer.BytesPerPixel];
        SnesVideoRenderer.RenderFrame(memoryBus.Hardware, pixels, interpolationProgress: 0.5f);

        var clearedPixel = ((20 * SnesVideoRenderer.Width) + 24) * SnesVideoRenderer.BytesPerPixel;
        var solidPixel = ((20 * SnesVideoRenderer.Width) + 25) * SnesVideoRenderer.BytesPerPixel;
        Assert.AreEqual((byte)0, pixels[clearedPixel]);
        Assert.AreEqual(byte.MaxValue, pixels[solidPixel]);

        SetObjectZero(memoryBus, x: 20, y: 20);
        runner.Run(18_000);
        Assert.AreEqual((byte)29, memoryBus.Hardware.PreviousObjectAttributeMemory.Span[0]);
        Assert.AreEqual((byte)20, memoryBus.Hardware.CurrentFrameObjectAttributeMemory.Span[0]);

        SnesVideoRenderer.RenderFrame(memoryBus.Hardware, pixels, interpolationProgress: 0.25f);

        var reverseHalfwayPixel = ((20 * SnesVideoRenderer.Width) + 26) * SnesVideoRenderer.BytesPerPixel;
        var reverseSolidPixel = ((20 * SnesVideoRenderer.Width) + 27) * SnesVideoRenderer.BytesPerPixel;
        Assert.AreEqual((byte)0, pixels[reverseHalfwayPixel]);
        Assert.AreEqual(byte.MaxValue, pixels[reverseSolidPixel]);

        SetObjectZero(memoryBus, x: 29, y: 20, tile: 1);
        runner.Run(18_000);
        SnesVideoRenderer.RenderFrame(memoryBus.Hardware, pixels, interpolationProgress: 0.5f);

        var animatedTileHalfwayPixel = ((20 * SnesVideoRenderer.Width) + 24) * SnesVideoRenderer.BytesPerPixel;
        var currentObjectPixel = ((20 * SnesVideoRenderer.Width) + 31) * SnesVideoRenderer.BytesPerPixel;
        Assert.AreEqual((byte)0, pixels[animatedTileHalfwayPixel]);
        Assert.AreEqual(byte.MaxValue, pixels[currentObjectPixel]);

        SetVideoRamAddress(memoryBus, 0x10);
        for (var row = 0; row < 16; row++)
        {
            memoryBus.WriteByte(0x002118, 0);
            memoryBus.WriteByte(0x002119, 0);
        }

        SnesVideoRenderer.RenderFrame(memoryBus.Hardware, pixels, interpolationProgress: 1f);
        Assert.AreEqual((byte)0, pixels[currentObjectPixel]);
    }

    [TestMethod]
    public void RendererMovesBackgroundWithoutBlendingPaletteColors()
    {
        var image = CreateRomImage();
        image[0] = 0x80;
        image[1] = 0xFE;
        using var romFile = TemporaryRomFile.Create(image);
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);

        memoryBus.WriteByte(0x002100, 0x0F);
        memoryBus.WriteByte(0x002105, 0);
        memoryBus.WriteByte(0x002107, 4);
        memoryBus.WriteByte(0x00212C, 1);
        memoryBus.WriteByte(0x002121, 1);
        memoryBus.WriteByte(0x002122, 0x1F);
        memoryBus.WriteByte(0x002122, 0);
        memoryBus.WriteByte(0x002115, 0x80);
        SetVideoRamAddress(memoryBus, 0);
        for (var row = 0; row < 8; row++)
        {
            memoryBus.WriteByte(0x002118, 0x20);
            memoryBus.WriteByte(0x002119, 0);
        }

        runner.Run(18_000);
        memoryBus.WriteByte(0x00210D, 1);
        memoryBus.WriteByte(0x00210D, 0);
        Assert.AreEqual(1, memoryBus.Hardware.GetBackgroundHorizontalOffset(0));
        runner.Run(18_000);

        Assert.AreEqual(1, memoryBus.Hardware.GetBackgroundScrollDeltaForScanline(1, 0).Horizontal);
        var pixels = new byte[SnesVideoRenderer.Width * SnesVideoRenderer.Height * SnesVideoRenderer.BytesPerPixel];
        SnesVideoRenderer.RenderFrame(memoryBus.Hardware, pixels, interpolationProgress: 0.5f);
        var halfwayPixel = SnesVideoRenderer.BytesPerPixel;
        Assert.AreEqual(byte.MaxValue, pixels[halfwayPixel]);

        SnesVideoRenderer.RenderFrame(memoryBus.Hardware, pixels, interpolationProgress: 1f);
        Assert.IsGreaterThan((byte)240, pixels[halfwayPixel]);

        // Check the rendered terrain, including every fine-scroll boundary and reversal.
        for (var step = 0; step < 64; step++)
        {
            var offset = step < 32 ? step : 63 - step;
            memoryBus.WriteByte(0x00210D, (byte)offset);
            memoryBus.WriteByte(0x00210D, 0);
            runner.Run(18_000);
            SnesVideoRenderer.RenderFrame(memoryBus.Hardware, pixels, interpolationProgress: 1f);
            for (var x = 0; x < SnesVideoRenderer.Width; x++)
            {
                var expectedRed = ((x + offset) & 7) == 2 ? byte.MaxValue : (byte)0;
                Assert.AreEqual(expectedRed, pixels[x * SnesVideoRenderer.BytesPerPixel],
                    $"Terrain sampled the wrong pixel at scroll {offset}, screen X {x}.");
            }
        }
    }

    [TestMethod]
    public void ModeSevenAffineSteppingPreservesHorizontalFlip()
    {
        var image = CreateRomImage();
        image[0] = 0x80;
        image[1] = 0xFE;
        using var romFile = TemporaryRomFile.Create(image);
        var memoryBus = new SnesMemoryBus(SnesRom.Load(romFile.Path));
        var runner = new NativeCpuRunner(memoryBus);

        memoryBus.WriteByte(0x002100, 0x0F);
        memoryBus.WriteByte(0x002105, 7);
        memoryBus.WriteByte(0x00212C, 1);
        memoryBus.WriteByte(0x002115, 0x80);
        SetVideoRamAddress(memoryBus, 0);
        for (var word = 0; word < 1024; word++)
        {
            memoryBus.WriteByte(0x002118, 0);
            memoryBus.WriteByte(0x002119, (byte)((word & 7) + 1));
        }

        for (var color = 1; color <= 8; color++)
        {
            memoryBus.WriteByte(0x002121, (byte)color);
            memoryBus.WriteByte(0x002122, (byte)(color * 3));
            memoryBus.WriteByte(0x002122, 0);
        }

        memoryBus.WriteByte(0x00211B, 0);
        memoryBus.WriteByte(0x00211B, 1);
        memoryBus.WriteByte(0x00211E, 0);
        memoryBus.WriteByte(0x00211E, 1);
        runner.Run(18_000);

        var pixels = new byte[SnesVideoRenderer.Width * SnesVideoRenderer.Height * SnesVideoRenderer.BytesPerPixel];
        SnesVideoRenderer.RenderFrame(memoryBus.Hardware, pixels, interpolationProgress: 1f);
        var firstPixel = 0;
        var lastPixel = (SnesVideoRenderer.Width - 1) * SnesVideoRenderer.BytesPerPixel;
        var unflippedFirst = pixels[firstPixel];
        var unflippedLast = pixels[lastPixel];
        Assert.AreNotEqual(unflippedFirst, unflippedLast);

        memoryBus.WriteByte(0x00211A, 1);
        runner.Run(18_000);
        SnesVideoRenderer.RenderFrame(memoryBus.Hardware, pixels, interpolationProgress: 1f);

        Assert.AreEqual(unflippedLast, pixels[firstPixel]);
        Assert.AreEqual(unflippedFirst, pixels[lastPixel]);
    }

    private static void SetObjectZero(SnesMemoryBus memoryBus, byte x, byte y, byte tile = 0)
    {
        memoryBus.WriteByte(0x002102, 0);
        memoryBus.WriteByte(0x002103, 0);
        memoryBus.WriteByte(0x002104, x);
        memoryBus.WriteByte(0x002104, y);
        memoryBus.WriteByte(0x002104, tile);
        memoryBus.WriteByte(0x002104, 0);
    }

    private static void SetVideoRamAddress(SnesMemoryBus memoryBus, ushort address)
    {
        memoryBus.WriteByte(0x002116, (byte)address);
        memoryBus.WriteByte(0x002117, (byte)(address >> 8));
    }

    private static void WriteVramWord(SnesMemoryBus memoryBus, byte low, byte high, bool incrementOnHighByte)
    {
        if (incrementOnHighByte)
        {
            memoryBus.WriteByte(0x002118, low);
            memoryBus.WriteByte(0x002119, high);
        }
        else
        {
            memoryBus.WriteByte(0x002119, high);
            memoryBus.WriteByte(0x002118, low);
        }
    }

    private static (byte Low, byte High) ReadVramWord(SnesMemoryBus memoryBus, bool incrementOnHighByte)
    {
        if (incrementOnHighByte)
        {
            var low = memoryBus.ReadByte(0x002139);
            var high = memoryBus.ReadByte(0x00213A);
            return (low, high);
        }

        var highByte = memoryBus.ReadByte(0x00213A);
        var lowByte = memoryBus.ReadByte(0x002139);
        return (lowByte, highByte);
    }

    private static void SetWorkRamPortAddress(SnesMemoryBus memoryBus, int address)
    {
        memoryBus.WriteByte(0x002181, (byte)address);
        memoryBus.WriteByte(0x002182, (byte)(address >> 8));
        memoryBus.WriteByte(0x002183, (byte)(address >> 16));
    }

    private static byte[] CreateRomImage()
    {
        var image = new byte[0x8000];
        const int headerOffset = 0x7FC0;
        Encoding.ASCII.GetBytes("SYNTHETIC HW ROM".PadRight(21)).CopyTo(image, headerOffset);
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
