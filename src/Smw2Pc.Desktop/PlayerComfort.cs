using SDL3;

internal static partial class Program
{
    private enum PlayerCommand { None, SaveState, LoadState, TogglePause, ExportReport, OpenSaves }

    private static SDL.FRect PlayerRow(SDL.FRect panel, int index) => new()
    {
        X = panel.X + 12, Y = panel.Y + 100 + index * Math.Min(44, (panel.H - 164) / 8),
        W = panel.W - 24, H = Math.Min(36, (panel.H - 164) / 8 - 4),
    };

    private static SDL.FRect GetPlayerTabButton(SDL.FRect panel) => new()
    {
        X = panel.X + 20 + 2 * ((panel.W - 32) / 3), Y = panel.Y + 52,
        W = (panel.W - 32) / 3, H = 30,
    };

    private static bool HandlePlayerClick(float x, float y, SDL.FRect panel,
        DesktopSettings settings, out PlayerCommand command)
    {
        command = PlayerCommand.None;
        for (var row = 0; row < 8; row++)
        {
            var bounds = PlayerRow(panel, row);
            if (!IsInside(x, y, bounds)) continue;
            switch (row)
            {
                case 0:
                    settings.VolumePercent = Math.Clamp((int)Math.Round((x - bounds.X) / bounds.W * 100), 0, 100);
                    settings.Muted = false;
                    break;
                case 1: settings.Muted = !settings.Muted; break;
                case 2: settings.PauseWhenUnfocused = !settings.PauseWhenUnfocused; break;
                case 3: command = PlayerCommand.TogglePause; break;
                case 4: command = PlayerCommand.SaveState; break;
                case 5: command = PlayerCommand.LoadState; break;
                case 6: command = PlayerCommand.OpenSaves; break;
                case 7: command = PlayerCommand.ExportReport; break;
            }
            return true;
        }
        return false;
    }

    private static void DrawPlayerPanel(IntPtr renderer, UiTextRenderer? text, SDL.FRect panel,
        DesktopSettings settings, string status)
    {
        string[] labels = [
            $"Volume: {settings.VolumePercent}% (click to set)",
            $"Mute: {(settings.Muted ? "On" : "Off")}  [F10]",
            $"Pause when unfocused: {(settings.PauseWhenUnfocused ? "On" : "Off")}",
            "Pause / resume  [F9]", "Quick save  [F5]", "Load quick save  [F8]",
            "Open save folder", "Export compatibility report",
        ];
        for (var i = 0; i < labels.Length; i++)
            DrawSettingsRow(renderer, text, PlayerRow(panel, i), labels[i]);
        var volume = PlayerRow(panel, 0);
        volume.Y += volume.H - 3;
        volume.H = 3;
        volume.W *= settings.VolumePercent / 100f;
        SDL.SetRenderDrawColor(renderer, 105, 179, 247, 255);
        SDL.RenderFillRect(renderer, in volume);
        DrawText(renderer, text, "F11: fullscreen | Saves are automatic",
            panel.X + 20, panel.Y + panel.H - 58, 11, MakeUiColor(181, 194, 211));
        DrawText(renderer, text, Truncate(status, Math.Max(16, (int)((panel.W - 40) / 8))),
            panel.X + 20, panel.Y + panel.H - 32, 12, MakeUiColor(242, 190, 128));
    }
}
