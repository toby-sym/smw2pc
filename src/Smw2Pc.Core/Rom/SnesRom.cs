using System.Buffers.Binary;
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

    private SnesRom(string sourcePath, byte[] data, bool hasCopierHeader, SnesRomHeader header)
    {
        SourcePath = sourcePath;
        this.data = data;
        HasCopierHeader = hasCopierHeader;
        Header = header;
    }

    public string SourcePath { get; }

    public string FileName => Path.GetFileName(SourcePath);

    public ReadOnlyMemory<byte> Data => data;

    public long SizeBytes => data.LongLength;

    public bool HasCopierHeader { get; }

    public SnesRomHeader Header { get; }

    public static SnesRom Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("The selected ROM file could not be found.", path);
        }

        var hasCopierHeader = fileInfo.Length % RomBankSize == CopierHeaderSize;
        var headerOffset = hasCopierHeader ? CopierHeaderSize : 0;
        var romSize = fileInfo.Length - headerOffset;

        if (romSize < MinimumRomSize)
        {
            throw new InvalidDataException("The selected file is too small to contain a SNES ROM.");
        }

        if (romSize > MaximumRomSize)
        {
            throw new InvalidDataException("The selected file is larger than the supported SNES ROM size.");
        }

        var fileData = File.ReadAllBytes(path);
        var romData = fileData.AsSpan(headerOffset).ToArray();
        var header = FindHeader(romData);

        return new SnesRom(path, romData, hasCopierHeader, header);
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

        if (string.IsNullOrWhiteSpace(title) || mapMode is < 0x20 or > 0x3F)
        {
            return false;
        }

        var complement = BinaryPrimitives.ReadUInt16LittleEndian(romData.Slice(offset + 0x1C, 2));
        var checksum = BinaryPrimitives.ReadUInt16LittleEndian(romData.Slice(offset + 0x1E, 2));
        var checksumPairValid = (complement ^ checksum) == ushort.MaxValue;

        header = new SnesRomHeader(title, mapMode, offset, complement, checksum, checksumPairValid);
        return true;
    }
}

public sealed record SnesRomHeader(
    string Title,
    byte MapMode,
    int HeaderOffset,
    ushort Complement,
    ushort Checksum,
    bool HasValidChecksumPair)
{
    internal int HeaderScore => 2 + (HasValidChecksumPair ? 2 : 0);
}
