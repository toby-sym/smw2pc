using System.Text.Json;

namespace Smw2Pc.Core.Storage;

public static class RecentRoms
{
    public static IReadOnlyList<string> Load(string? root = null)
    {
        try
        {
            var path = Path.Combine(root ?? PlayerFiles.Root, "recent-roms.json");
            if (!File.Exists(path) && !File.Exists(path + ".bak")) return [];
            return PlayerFiles.ReadWithBackup(path, bytes =>
            {
                try
                {
                    return (JsonSerializer.Deserialize<string[]>(bytes) ?? [])
                        .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                        .Distinct(PathComparer).Take(5).ToArray();
                }
                catch (JsonException exception) { throw new InvalidDataException("Invalid recent ROM list.", exception); }
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { return []; }
    }

    public static void Add(string path, string? root = null)
    {
        if (!File.Exists(path)) return;
        path = Path.GetFullPath(path);
        var paths = new[] { path }.Concat(Load(root)).Distinct(PathComparer).Take(5).ToArray();
        PlayerFiles.WriteAtomic(Path.Combine(root ?? PlayerFiles.Root, "recent-roms.json"),
            JsonSerializer.SerializeToUtf8Bytes(paths));
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
