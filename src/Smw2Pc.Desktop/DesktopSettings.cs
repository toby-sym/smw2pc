using System.Text.Json;
using SDL3;

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

    public int RenderFrameRate { get; set; } = 240;

    public SDL.Keycode MoveLeftKey { get; set; } = SDL.Keycode.Left;

    public SDL.Keycode MoveRightKey { get; set; } = SDL.Keycode.Right;

    public SDL.Keycode MoveUpKey { get; set; } = SDL.Keycode.Up;

    public SDL.Keycode MoveDownKey { get; set; } = SDL.Keycode.Down;

    public SDL.Keycode ButtonYKey { get; set; } = SDL.Keycode.A;

    public SDL.Keycode ButtonXKey { get; set; } = SDL.Keycode.S;

    public SDL.Keycode ButtonAKey { get; set; } = SDL.Keycode.X;

    public SDL.Keycode ButtonBKey { get; set; } = SDL.Keycode.Z;

    public SDL.Keycode StartKey { get; set; } = SDL.Keycode.Return;

    public SDL.Keycode SelectKey { get; set; } = SDL.Keycode.RShift;

    public SDL.Keycode ButtonLKey { get; set; } = SDL.Keycode.Q;

    public SDL.Keycode ButtonRKey { get; set; } = SDL.Keycode.W;

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

                    settings.MoveLeftKey = GetValidKey(settings.MoveLeftKey, SDL.Keycode.Left);
                    settings.MoveRightKey = GetValidKey(settings.MoveRightKey, SDL.Keycode.Right);
                    settings.MoveUpKey = GetValidKey(settings.MoveUpKey, SDL.Keycode.Up);
                    settings.MoveDownKey = GetValidKey(settings.MoveDownKey, SDL.Keycode.Down);
                    settings.ButtonYKey = GetValidKey(settings.ButtonYKey, SDL.Keycode.A);
                    settings.ButtonXKey = GetValidKey(settings.ButtonXKey, SDL.Keycode.S);
                    settings.ButtonAKey = GetValidKey(settings.ButtonAKey, SDL.Keycode.X);
                    settings.ButtonBKey = GetValidKey(settings.ButtonBKey, SDL.Keycode.Z);
                    settings.StartKey = GetValidKey(settings.StartKey, SDL.Keycode.Return);
                    settings.SelectKey = GetValidKey(settings.SelectKey, SDL.Keycode.RShift);
                    settings.ButtonLKey = GetValidKey(settings.ButtonLKey, SDL.Keycode.Q);
                    settings.ButtonRKey = GetValidKey(settings.ButtonRKey, SDL.Keycode.W);

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

    private static SDL.Keycode GetValidKey(SDL.Keycode key, SDL.Keycode fallback)
    {
        return Enum.IsDefined(key) && key != SDL.Keycode.Unknown ? key : fallback;
    }
}
