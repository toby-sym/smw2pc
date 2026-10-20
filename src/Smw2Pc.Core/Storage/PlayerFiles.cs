namespace Smw2Pc.Core.Storage;

public static class PlayerFiles
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMW2PC");

    public static string RomDirectory(string sha256, string? root = null)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected a ROM SHA-256 hash.", nameof(sha256));
        return Path.Combine(root ?? Root, "roms", sha256.ToUpperInvariant());
    }

    public static void WriteAtomic(string path, ReadOnlySpan<byte> bytes, bool backup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            if (backup && File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static T ReadWithBackup<T>(string path, Func<byte[], T> read)
    {
        try { return read(File.ReadAllBytes(path)); }
        catch (Exception first) when (first is IOException or InvalidDataException or ArgumentException)
        {
            if (!File.Exists(path + ".bak")) throw;
            var bytes = File.ReadAllBytes(path + ".bak");
            var value = read(bytes);
            // Repair the primary only after validating the backup.
            WriteAtomic(path, bytes, backup: false);
            return value;
        }
    }
}
