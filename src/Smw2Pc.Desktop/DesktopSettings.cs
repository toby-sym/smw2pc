using System.Text.Json;

internal enum GameAspectRatio
{
    OriginalPixels,
    Television,
    Widescreen,
    Stretch,
}

internal sealed class DesktopSettings
{
    private const int MinimumWindowWidth = 640;
    private const int MinimumWindowHeight = 480;
    private const int MaximumWindowWidth = 3840;
    private const int MaximumWindowHeight = 2160;
    private const int MinimumRenderFrameRate = 15;
    private const int MaximumRenderFrameRate = 240;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SMW2PC",
        "settings.json");

    public int WindowWidth { get; set; } = 960;

    public int WindowHeight { get; set; } = 720;

    public GameAspectRatio AspectRatio { get; set; } = GameAspectRatio.Television;

    public int RenderFrameRate { get; set; } = 60;

    public bool SmoothScaling { get; set; } = true;

    public static DesktopSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath));
                if (settings is not null)
                {
                    settings.WindowWidth = Math.Clamp(settings.WindowWidth, MinimumWindowWidth, MaximumWindowWidth);
                    settings.WindowHeight = Math.Clamp(settings.WindowHeight, MinimumWindowHeight, MaximumWindowHeight);
                    settings.RenderFrameRate = Math.Clamp(
                        settings.RenderFrameRate,
                        MinimumRenderFrameRate,
                        MaximumRenderFrameRate);
                    if (!Enum.IsDefined(settings.AspectRatio))
                    {
                        settings.AspectRatio = GameAspectRatio.Television;
                    }

                    return settings;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall back to usable defaults when the optional settings file is unreadable.
        }

        return new DesktopSettings();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Settings are optional; a write failure should not interrupt gameplay.
        }
    }

    public static int ClampRenderFrameRate(int frameRate)
    {
        return Math.Clamp(frameRate, MinimumRenderFrameRate, MaximumRenderFrameRate);
    }
}
