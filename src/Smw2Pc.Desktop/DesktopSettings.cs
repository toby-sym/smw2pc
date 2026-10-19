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
    internal const int MaximumRenderFrameRate = 240;

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

    public SDL.GamepadButton MoveLeftGamepadButton { get; set; } = SDL.GamepadButton.DPadLeft;

    public SDL.GamepadButton MoveRightGamepadButton { get; set; } = SDL.GamepadButton.DPadRight;

    public SDL.GamepadButton MoveUpGamepadButton { get; set; } = SDL.GamepadButton.DPadUp;

    public SDL.GamepadButton MoveDownGamepadButton { get; set; } = SDL.GamepadButton.DPadDown;

    public SDL.GamepadButton ButtonYGamepadButton { get; set; } = SDL.GamepadButton.West;

    public SDL.GamepadButton ButtonXGamepadButton { get; set; } = SDL.GamepadButton.North;

    public SDL.GamepadButton ButtonAGamepadButton { get; set; } = SDL.GamepadButton.East;

    public SDL.GamepadButton ButtonBGamepadButton { get; set; } = SDL.GamepadButton.South;

    public SDL.GamepadButton StartGamepadButton { get; set; } = SDL.GamepadButton.Start;

    public SDL.GamepadButton SelectGamepadButton { get; set; } = SDL.GamepadButton.Back;

    public SDL.GamepadButton ButtonLGamepadButton { get; set; } = SDL.GamepadButton.LeftShoulder;

    public SDL.GamepadButton ButtonRGamepadButton { get; set; } = SDL.GamepadButton.RightShoulder;

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

                    settings.MoveLeftGamepadButton = GetValidGamepadButton(settings.MoveLeftGamepadButton, SDL.GamepadButton.DPadLeft);
                    settings.MoveRightGamepadButton = GetValidGamepadButton(settings.MoveRightGamepadButton, SDL.GamepadButton.DPadRight);
                    settings.MoveUpGamepadButton = GetValidGamepadButton(settings.MoveUpGamepadButton, SDL.GamepadButton.DPadUp);
                    settings.MoveDownGamepadButton = GetValidGamepadButton(settings.MoveDownGamepadButton, SDL.GamepadButton.DPadDown);
                    settings.ButtonYGamepadButton = GetValidGamepadButton(settings.ButtonYGamepadButton, SDL.GamepadButton.West);
                    settings.ButtonXGamepadButton = GetValidGamepadButton(settings.ButtonXGamepadButton, SDL.GamepadButton.North);
                    settings.ButtonAGamepadButton = GetValidGamepadButton(settings.ButtonAGamepadButton, SDL.GamepadButton.East);
                    settings.ButtonBGamepadButton = GetValidGamepadButton(settings.ButtonBGamepadButton, SDL.GamepadButton.South);
                    settings.StartGamepadButton = GetValidGamepadButton(settings.StartGamepadButton, SDL.GamepadButton.Start);
                    settings.SelectGamepadButton = GetValidGamepadButton(settings.SelectGamepadButton, SDL.GamepadButton.Back);
                    settings.ButtonLGamepadButton = GetValidGamepadButton(settings.ButtonLGamepadButton, SDL.GamepadButton.LeftShoulder);
                    settings.ButtonRGamepadButton = GetValidGamepadButton(settings.ButtonRGamepadButton, SDL.GamepadButton.RightShoulder);

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

    private static SDL.GamepadButton GetValidGamepadButton(SDL.GamepadButton button, SDL.GamepadButton fallback)
    {
        return Enum.IsDefined(button) && button != SDL.GamepadButton.Invalid ? button : fallback;
    }
}
