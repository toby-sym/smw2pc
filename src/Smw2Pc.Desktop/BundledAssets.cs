using System.Security.Cryptography;

internal static class BundledAssets
{
    public static string GetFontPath()
    {
        var deployed = Path.Combine(AppContext.BaseDirectory, "assets", "fonts", "Inter-Variable.ttf");
        if (File.Exists(deployed)) return deployed;
        using var resource = typeof(Program).Assembly.GetManifestResourceStream("Smw2Pc.Desktop.Font")
            ?? throw new InvalidDataException("The bundled UI font is missing.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "smw2pc", "fonts");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(bytes)) + ".ttf");
        if (!File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { File.Delete(temporary); }
        }
        return path;
    }
}
