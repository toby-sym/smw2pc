using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Smw2Pc.Core.Rom;

public sealed class SnesRom
{
    private const int CopierHeaderSize = 0x200;
    private const int RomBankSize = 0x8000;
    private const int MinimumRomSize = RomBankSize;
    private const int MaximumRomSize = 64 * 1024 * 1024;
    private const int LoRomHeaderOffset = 0x7FC0;
    private const int HiRomHeaderOffset = 0xFFC0;
    private const int InternalTitleLength = 21;

    private readonly byte[] data;

    private SnesRom(string sourcePath, string fileName, byte[] data, bool hasCopierHeader, SnesRomHeader header)
    {
        SourcePath = sourcePath;
        FileName = fileName;
        this.data = data;
        Sha256 = Convert.ToHexString(SHA256.HashData(data));
        HasCopierHeader = hasCopierHeader;
        Header = header;
    }

    public string SourcePath { get; }

    public string FileName { get; }

    public ReadOnlyMemory<byte> Data => data;

    public long SizeBytes => data.LongLength;

    public bool HasCopierHeader { get; }

    public SnesRomHeader Header { get; }

    public string Sha256 { get; }

    public static SnesRom Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("The selected ROM file could not be found.", path);
        }

        if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return LoadFromZip(path);
        }

        if (fileInfo.Length > MaximumRomSize + CopierHeaderSize)
        {
            throw new InvalidDataException("The selected file is larger than the supported SNES ROM size.");
        }

        return Create(path, Path.GetFileName(path), File.ReadAllBytes(path));
    }

    public static SnesRom LoadBytes(ReadOnlyMemory<byte> bytes, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (bytes.Length > MaximumRomSize + CopierHeaderSize)
        {
            throw new InvalidDataException("The selected file is larger than the supported SNES ROM size.");
        }

        return Create(fileName, Path.GetFileName(fileName), bytes.ToArray());
    }

    private static SnesRom LoadFromZip(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var romEntries = archive.Entries
            .Where(entry => IsRomFileName(entry.Name))
            .ToArray();

        if (romEntries.Length == 0)
        {
            throw new InvalidDataException("The ZIP archive does not contain an .sfc or .smc ROM file.");
        }

        if (romEntries.Length > 1)
        {
            throw new InvalidDataException("The ZIP archive contains multiple ROM files; it must contain exactly one .sfc or .smc file.");
        }

        var entry = romEntries[0];
        if (entry.Length > MaximumRomSize + CopierHeaderSize)
        {
            throw new InvalidDataException("The ROM inside the ZIP archive is larger than the supported SNES ROM size.");
        }

        using var entryStream = entry.Open();
        using var romBuffer = new MemoryStream((int)entry.Length);
        var copyBuffer = new byte[81920];

        while (true)
        {
            var bytesRead = entryStream.Read(copyBuffer, 0, copyBuffer.Length);
            if (bytesRead == 0)
            {
                break;
            }

            if (romBuffer.Length + bytesRead > MaximumRomSize + CopierHeaderSize)
            {
                throw new InvalidDataException("The ROM inside the ZIP archive is larger than the supported SNES ROM size.");
            }

            romBuffer.Write(copyBuffer, 0, bytesRead);
        }

        return Create(path, entry.Name, romBuffer.ToArray());
    }

    private static bool IsRomFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".sfc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".smc", StringComparison.OrdinalIgnoreCase);
    }

    private static SnesRom Create(string sourcePath, string fileName, byte[] fileData)
    {
        var hasCopierHeader = fileData.Length % RomBankSize == CopierHeaderSize;
        var headerOffset = hasCopierHeader ? CopierHeaderSize : 0;
        var romSize = fileData.Length - headerOffset;

        if (romSize < MinimumRomSize)
        {
            throw new InvalidDataException("The selected file is too small to contain a SNES ROM.");
        }

        if (romSize > MaximumRomSize)
        {
            throw new InvalidDataException("The selected file is larger than the supported SNES ROM size.");
        }

        var romData = fileData.AsSpan(headerOffset).ToArray();
        var header = FindHeader(romData);

        return new SnesRom(sourcePath, fileName, romData, hasCopierHeader, header);
    }

    private static SnesRomHeader FindHeader(ReadOnlySpan<byte> romData)
    {
        var candidates = new List<SnesRomHeader>(2);

        if (TryReadHeader(romData, LoRomHeaderOffset, out var loRomHeader))
        {
            candidates.Add(loRomHeader);
        }

        if (TryReadHeader(romData, HiRomHeaderOffset, out var hiRomHeader))
        {
            candidates.Add(hiRomHeader);
        }

        return candidates
            .OrderByDescending(candidate => candidate.HeaderScore)
            .FirstOrDefault()
            ?? throw new InvalidDataException("The selected file does not contain a recognizable SNES ROM header.");
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> romData, int offset, out SnesRomHeader header)
    {
        header = default!;
        if (romData.Length < offset + 0x20)
        {
            return false;
        }

        var titleBytes = romData.Slice(offset, InternalTitleLength);
        foreach (var value in titleBytes)
        {
            if (value != 0 && (value < 0x20 || value > 0x7E))
            {
                return false;
            }
        }

        var title = Encoding.ASCII.GetString(titleBytes).TrimEnd('\0', ' ').Trim();
        var mapMode = romData[offset + 0x15];
        var countryCode = romData[offset + 0x19];

        if (string.IsNullOrWhiteSpace(title) || mapMode is < 0x20 or > 0x3F)
        {
            return false;
        }

        var complement = BinaryPrimitives.ReadUInt16LittleEndian(romData.Slice(offset + 0x1C, 2));
        var checksum = BinaryPrimitives.ReadUInt16LittleEndian(romData.Slice(offset + 0x1E, 2));
        var checksumPairValid = (complement ^ checksum) == ushort.MaxValue;

        header = new SnesRomHeader(title, mapMode, countryCode, offset, complement, checksum, checksumPairValid);
        return true;
    }
}

public sealed record SnesRomHeader(
    string Title,
    byte MapMode,
    byte CountryCode,
    int HeaderOffset,
    ushort Complement,
    ushort Checksum,
    bool HasValidChecksumPair)
{
    public bool IsPalRegion => CountryCode is >= 0x02 and <= 0x0C or 0x11;

    public double FrameRateHz => IsPalRegion ? 50.00697 : 60.0988;

    internal int HeaderScore => 2 + (HasValidChecksumPair ? 2 : 0);
}
