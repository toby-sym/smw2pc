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
    public void RendererInterpolatesObjectPositionInBothDirections()
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

        var halfwayPixel = ((20 * SnesVideoRenderer.Width) + 33) * SnesVideoRenderer.BytesPerPixel;
        var solidPixel = ((20 * SnesVideoRenderer.Width) + 34) * SnesVideoRenderer.BytesPerPixel;
        Assert.IsInRange((byte)128, (byte)144, pixels[halfwayPixel]);
        Assert.AreEqual(byte.MaxValue, pixels[solidPixel]);

        SetObjectZero(memoryBus, x: 20, y: 20);
        runner.Run(18_000);
        Assert.AreEqual((byte)29, memoryBus.Hardware.PreviousObjectAttributeMemory.Span[0]);
        Assert.AreEqual((byte)20, memoryBus.Hardware.CurrentFrameObjectAttributeMemory.Span[0]);

        SnesVideoRenderer.RenderFrame(memoryBus.Hardware, pixels, interpolationProgress: 0.25f);

        var reverseHalfwayPixel = ((20 * SnesVideoRenderer.Width) + 17) * SnesVideoRenderer.BytesPerPixel;
        var reverseSolidPixel = ((20 * SnesVideoRenderer.Width) + 18) * SnesVideoRenderer.BytesPerPixel;
        Assert.IsInRange((byte)56, (byte)72, pixels[reverseHalfwayPixel]);
        Assert.AreEqual(byte.MaxValue, pixels[reverseSolidPixel]);

        SetObjectZero(memoryBus, x: 29, y: 20, tile: 1);
        runner.Run(18_000);
        SnesVideoRenderer.RenderFrame(memoryBus.Hardware, pixels, interpolationProgress: 0.5f);

        var backgroundPixel = ((20 * SnesVideoRenderer.Width) + 24) * SnesVideoRenderer.BytesPerPixel;
        var clearPixel = ((20 * SnesVideoRenderer.Width) + 100) * SnesVideoRenderer.BytesPerPixel;
        var currentObjectPixel = ((20 * SnesVideoRenderer.Width) + 31) * SnesVideoRenderer.BytesPerPixel;
        CollectionAssert.AreEqual(pixels.AsSpan(clearPixel, 3).ToArray(), pixels.AsSpan(backgroundPixel, 3).ToArray());
        Assert.AreEqual(byte.MaxValue, pixels[currentObjectPixel]);
    }

    [TestMethod]
    public void RendererInterpolatesBackgroundScrollBetweenFrames()
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
        var halfwayPixel = 0;
        Assert.IsInRange((byte)120, (byte)140, pixels[halfwayPixel]);

        SnesVideoRenderer.RenderFrame(memoryBus.Hardware, pixels, interpolationProgress: 1f);
        Assert.IsGreaterThan((byte)240, pixels[halfwayPixel]);
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
