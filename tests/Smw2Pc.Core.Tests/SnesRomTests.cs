using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Core.Tests;

[TestClass]
public sealed class SnesRomTests
{
    [TestMethod]
    public void LoadReadsRomDataAndMetadataFromUnheaderedImage()
    {
        using var romFile = TemporaryRomFile.Create(CreateRomImage("TEST ROM", 0x20));

        var rom = SnesRom.Load(romFile.Path);

        Assert.AreEqual("TEST ROM", rom.Header.Title);
        Assert.AreEqual((byte)0x20, rom.Header.MapMode);
        Assert.AreEqual(0x7FC0, rom.Header.HeaderOffset);
        Assert.IsTrue(rom.Header.HasValidChecksumPair);
        Assert.IsFalse(rom.HasCopierHeader);
        Assert.AreEqual(0x8000, rom.SizeBytes);
        CollectionAssert.AreEqual(romFile.ExpectedData, rom.Data.ToArray());
    }

    [TestMethod]
    public void LoadRemoves512ByteCopierHeader()
    {
        var romData = CreateRomImage("HEADERED ROM", 0x21);
        var fileData = new byte[romData.Length + 0x200];
        Array.Fill(fileData, (byte)0xAA, 0, 0x200);
        romData.CopyTo(fileData, 0x200);
        using var romFile = TemporaryRomFile.Create(fileData);

        var rom = SnesRom.Load(romFile.Path);

        Assert.IsTrue(rom.HasCopierHeader);
        Assert.AreEqual("HEADERED ROM", rom.Header.Title);
        Assert.AreEqual((byte)0x21, rom.Header.MapMode);
        Assert.AreEqual(0x8000, rom.SizeBytes);
        CollectionAssert.AreEqual(romData, rom.Data.ToArray());
    }

    [TestMethod]
    public void LoadReadsRomFromZipArchiveWithoutExtractingIt()
    {
        var romData = CreateRomImage("ZIPPED ROM", 0x20);
        using var archive = TemporaryZipFile.Create(("games/ZIPPED ROM.SFC", romData));

        var rom = SnesRom.Load(archive.Path);

        Assert.AreEqual("ZIPPED ROM.SFC", rom.FileName);
        Assert.AreEqual(archive.Path, rom.SourcePath);
        Assert.AreEqual("ZIPPED ROM", rom.Header.Title);
        CollectionAssert.AreEqual(romData, rom.Data.ToArray());
    }

    [TestMethod]
    public void LoadRejectsZipWithoutRomFile()
    {
        using var archive = TemporaryZipFile.Create(("readme.txt", Encoding.UTF8.GetBytes("not a ROM")));

        Assert.ThrowsExactly<InvalidDataException>(() => SnesRom.Load(archive.Path));
    }

    [TestMethod]
    public void LoadRejectsZipWithMultipleRomFiles()
    {
        var romData = CreateRomImage("TEST ROM", 0x20);
        using var archive = TemporaryZipFile.Create(("one.sfc", romData), ("two.smc", romData));

        Assert.ThrowsExactly<InvalidDataException>(() => SnesRom.Load(archive.Path));
    }

    [TestMethod]
    public void LoadRejectsFilesThatAreTooSmall()
    {
        using var romFile = TemporaryRomFile.Create(new byte[128]);

        Assert.ThrowsExactly<InvalidDataException>(() => SnesRom.Load(romFile.Path));
    }

    [TestMethod]
    public void LoadRejectsImageWithoutRecognizableHeader()
    {
        using var romFile = TemporaryRomFile.Create(new byte[0x8000]);

        Assert.ThrowsExactly<InvalidDataException>(() => SnesRom.Load(romFile.Path));
    }

    [TestMethod]
    public void LoadReportsMissingFile()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sfc");

        Assert.ThrowsExactly<FileNotFoundException>(() => SnesRom.Load(missingPath));
    }

    private static byte[] CreateRomImage(string title, byte mapMode)
    {
        var image = new byte[0x8000];
        var headerOffset = 0x7FC0;
        Encoding.ASCII.GetBytes(title.PadRight(21)).CopyTo(image, headerOffset);
        image[headerOffset + 0x15] = mapMode;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headerOffset + 0x1C, 2), 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headerOffset + 0x1E, 2), 0xEDCB);
        return image;
    }

    private sealed class TemporaryRomFile(byte[] expectedData) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{Guid.NewGuid():N}.sfc");

        public byte[] ExpectedData { get; } = expectedData;

        public static TemporaryRomFile Create(byte[] data)
        {
            var file = new TemporaryRomFile(data);
            File.WriteAllBytes(file.Path, data);
            return file;
        }

        public void Dispose()
        {
            File.Delete(Path);
        }
    }

    private sealed class TemporaryZipFile : IDisposable
    {
        private TemporaryZipFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryZipFile Create(params (string Name, byte[] Data)[] entries)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var (name, data) in entries)
                {
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    stream.Write(data);
                }
            }

            return new TemporaryZipFile(path);
        }

        public void Dispose()
        {
            File.Delete(Path);
        }
    }
}
