using System.IO.Compression;
using System.Security.Cryptography;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Rom;

namespace Smw2Pc.Core.Machine;

public static class MachineSnapshot
{
    private const int FormatVersion = 1;
    private const int MaximumPayloadBytes = 4 * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "SMWSTATE"u8;

    public static byte[] Capture(NativeCpuRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        using var payload = new MemoryStream();
        var state = new StateBuffer(payload, reading: false);
        runner.State.TransferState(state);
        runner.MemoryBus.TransferState(state);
        var bytes = payload.ToArray();
        using var result = new MemoryStream();
        using (var writer = new BinaryWriter(result, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(Convert.FromHexString(runner.MemoryBus.Rom.Sha256));
            writer.Write(bytes.Length);
            writer.Write(SHA256.HashData(bytes));
        }
        using (var compressed = new BrotliStream(result, CompressionLevel.Fastest, leaveOpen: true))
            compressed.Write(bytes);
        return result.ToArray();
    }

    // Restore into a fresh machine so a rejected file cannot damage a running game.
    public static NativeCpuRunner Restore(SnesRom rom, ReadOnlySpan<byte> snapshot)
    {
        ArgumentNullException.ThrowIfNull(rom);
        if (snapshot.Length > MaximumPayloadBytes)
            throw new InvalidDataException("The save state is too large.");
        using var source = new MemoryStream(snapshot.ToArray(), writable: false);
        using var reader = new BinaryReader(source);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)
            || reader.ReadInt32() != FormatVersion)
            throw new InvalidDataException("Unsupported save state format.");
        if (!reader.ReadBytes(32).AsSpan().SequenceEqual(Convert.FromHexString(rom.Sha256)))
            throw new InvalidDataException("This save state belongs to a different ROM.");
        var size = reader.ReadInt32();
        if (size < 0 || size > MaximumPayloadBytes)
            throw new InvalidDataException("Invalid save state length.");
        var checksum = reader.ReadBytes(32);
        var payload = new byte[size];
        using (var compressed = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true))
        {
            compressed.ReadExactly(payload);
            if (compressed.ReadByte() != -1)
                throw new InvalidDataException("Unexpected save state data.");
        }
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual(checksum))
            throw new InvalidDataException("The save state checksum is invalid.");
        var runner = new NativeCpuRunner(new SnesMemoryBus(rom));
        using var stateStream = new MemoryStream(payload, writable: false);
        var state = new StateBuffer(stateStream, reading: true);
        runner.State.TransferState(state);
        runner.MemoryBus.TransferState(state);
        if (stateStream.Position != stateStream.Length)
            throw new InvalidDataException("The save state schema is incompatible.");
        return runner;
    }
}
