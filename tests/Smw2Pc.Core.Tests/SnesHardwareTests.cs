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
    public void BackgroundScrollHistoryRetainsTwoCompletedFrames()
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

        SetBackgroundHorizontalScroll(memoryBus, 8);
        runner.Run(18_000);
        SetBackgroundHorizontalScroll(memoryBus, 24);
        runner.Run(18_000);
        SetBackgroundHorizontalScroll(memoryBus, 40);
        runner.Run(18_000);

        Assert.IsTrue(memoryBus.Hardware.HasOlderPpuFrameScrollOffsets);
        Assert.AreEqual((ushort)40, memoryBus.Hardware.GetBackgroundOffsetsForScanline(100).Span[0]);
        Assert.AreEqual((Horizontal: 16, Vertical: 0), memoryBus.Hardware.GetBackgroundScrollDeltaForScanline(100, 0));
        Assert.AreEqual((Horizontal: 16, Vertical: 0), memoryBus.Hardware.GetPreviousBackgroundScrollDeltaForScanline(100, 0));
    }

    private static void SetBackgroundHorizontalScroll(SnesMemoryBus memoryBus, byte offset)
    {
        memoryBus.WriteByte(0x00210D, offset);
        memoryBus.WriteByte(0x00210D, 0);
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
