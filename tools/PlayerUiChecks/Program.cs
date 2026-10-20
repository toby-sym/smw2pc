using System.Reflection;
using System.Text.Json;
using SDL3;
using Smw2Pc.Core.Rom;
using Smw2Pc.Core.Storage;

if (args.Length != 1) throw new ArgumentException("Supply an output directory for isolated UI checks.");
var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
Environment.SetEnvironmentVariable("SMW2PC_DATA_DIRECTORY", Path.Combine(output, Guid.NewGuid().ToString("N")));
Environment.SetEnvironmentVariable("SDL_VIDEO_DRIVER", "dummy");
Environment.SetEnvironmentVariable("SDL_AUDIO_DRIVER", "dummy");
Environment.SetEnvironmentVariable("SDL_RENDER_DRIVER", "software");
var player = typeof(DesktopSettings).Assembly;
var program = player.GetType("Program")!;
var flags = BindingFlags.NonPublic | BindingFlags.Static;
using var romStream = player.GetManifestResourceStream("Smw2Pc.Desktop.CompiledRom")
    ?? throw new InvalidOperationException("Build this tool with CompiledRomPath and RecompiledSourceDirectory.");
using var romBuffer = new MemoryStream();
romStream.CopyTo(romBuffer);
var rom = SnesRom.LoadBytes(romBuffer.ToArray(), "ui-homebrew.sfc");
var directory = PlayerFiles.RomDirectory(rom.Sha256);
var statePath = Path.Combine(directory, "quicksave.state");

void Key(SDL.Keycode key)
{
    var input = new SDL.Event();
    input.Type = (uint)SDL.EventType.KeyDown;
    input.Key.Key = key;
    if (!SDL.PushEvent(ref input)) throw new InvalidOperationException(SDL.GetError());
}
void Event(SDL.EventType type)
{
    var input = new SDL.Event { Type = (uint)type };
    if (!SDL.PushEvent(ref input)) throw new InvalidOperationException(SDL.GetError());
}
void Click(float x, float y)
{
    var input = new SDL.Event { Type = (uint)SDL.EventType.MouseButtonDown };
    input.Button.Button = 1; input.Button.X = x; input.Button.Y = y;
    if (!SDL.PushEvent(ref input)) throw new InvalidOperationException(SDL.GetError());
}
async Task Wait(Func<bool> ready)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (!ready())
    {
        if (DateTime.UtcNow > deadline) throw new TimeoutException("The player did not complete the UI action.");
        await Task.Delay(25);
    }
}
async Task<byte[]> Save()
{
    var previous = File.Exists(statePath) ? File.GetLastWriteTimeUtc(statePath) : DateTime.MinValue;
    Key(SDL.Keycode.F5);
    await Wait(() => File.Exists(statePath) && File.GetLastWriteTimeUtc(statePath) > previous);
    return File.ReadAllBytes(statePath);
}
static void Equal(byte[] a, byte[] b, bool expected, string action)
{
    if (a.AsSpan().SequenceEqual(b) != expected) throw new InvalidOperationException(action);
}

var driver = Task.Run(async () =>
{
    try
    {
        await Wait(() => File.Exists(Path.Combine(directory, "save.srm")));
        Key(SDL.Keycode.F9); await Task.Delay(100);
        var paused = await Save(); await Task.Delay(100);
        Key(SDL.Keycode.F2); await Task.Delay(50);
        Click(700, 240); await Task.Delay(100); // Television -> extended 16:9, while paused.
        Equal(paused, await Save(), true, "Widescreen mode changed the paused machine.");
        for (var aspect = 0; aspect < 3; aspect++) { Click(700, 240); await Task.Delay(50); }
        Equal(paused, await Save(), true, "Switching viewport sizes changed the paused machine.");
        Key(SDL.Keycode.F2); await Task.Delay(50);
        Equal(paused, await Save(), true, "Manual pause advanced the machine.");
        Key(SDL.Keycode.F9); await Task.Delay(150);
        Key(SDL.Keycode.F9); await Task.Delay(100);
        Equal(paused, await Save(), false, "Resuming did not advance the machine.");
        // Restore while paused, then save again to verify the real F8/F5 path.
        var beforeLoad = await Save();
        Key(SDL.Keycode.F9); await Task.Delay(100);
        Key(SDL.Keycode.F9); await Task.Delay(100);
        Key(SDL.Keycode.F8); await Task.Delay(100);
        Equal(beforeLoad, await Save(), true, "Quick load failed to restore the machine.");
        Key(SDL.Keycode.F9); await Task.Delay(100);
        Event(SDL.EventType.WindowFocusLost); await Task.Delay(100);
        var background = await Save(); await Task.Delay(100);
        Equal(background, await Save(), true, "Focus pause advanced the machine.");
        Event(SDL.EventType.WindowFocusGained); await Task.Delay(100);
        Key(SDL.Keycode.F2); await Task.Delay(50);
        Click(881, 147); await Task.Delay(50); // Player tab in the default 960 x 720 window.
        Click(684, 198); await Task.Delay(50); // Volume at 25%.
        Click(700, 286); await Task.Delay(50); // Disable focus pause.
        Event(SDL.EventType.WindowFocusLost); await Task.Delay(100);
        var active = await Save(); await Task.Delay(100);
        Equal(active, await Save(), false, "Disabling focus pause did not keep gameplay running.");
        Click(642, 147); await Task.Delay(50); // Display tab.
        Click(700, 240); await Task.Delay(100); // Persist extended 16:9 for this ROM.
    }
    finally { Event(SDL.EventType.Quit); }
});
var exitCode = (int)program.GetMethod("Main", flags)!.Invoke(null, [Array.Empty<string>()])!;
await driver;
if (exitCode != 0) throw new InvalidOperationException($"Player exited with {exitCode}.");
var settings = DesktopSettings.Load(rom.Sha256);
if (settings.VolumePercent != 25 || settings.PauseWhenUnfocused || settings.AspectRatio != GameAspectRatio.Widescreen)
    throw new InvalidOperationException("Player controls did not persist per-ROM preferences.");

// Render the real panel at minimum and default window sizes for layout inspection.
SDL.Init(SDL.InitFlags.Video);
foreach (var size in new[] { (640, 480), (960, 720) })
{
    if (!SDL.CreateWindowAndRenderer("UI layout check", size.Item1, size.Item2, SDL.WindowFlags.Hidden, out var window, out var renderer))
        throw new InvalidOperationException(SDL.GetError());
    using var text = UiTextRenderer.TryCreate(renderer, BundledAssets.GetFontPath());
    var panel = program.GetMethod("GetSettingsPanel", flags)!.Invoke(null, [size.Item1, size.Item2]);
    var tab = Enum.Parse(program.GetNestedType("SettingsTab", BindingFlags.NonPublic)!, "Player");
    var mode = Enum.Parse(program.GetNestedType("BindingMode", BindingFlags.NonPublic)!, "Keyboard");
    var timing = Activator.CreateInstance(program.GetNestedType("FrameTimingDisplay", BindingFlags.NonPublic)!, ["", "", "", ""]);
    SDL.SetRenderDrawColor(renderer, 16, 19, 24, 255); SDL.RenderClear(renderer);
    program.GetMethod("DrawSettingsPanel", flags)!.Invoke(null,
        [renderer, text, panel, tab, null, settings, rom.Header.FrameRateHz, "Quick save loaded.", "", false, 180d, timing, mode, IntPtr.Zero, ""]);
    var surface = SDL.RenderReadPixels(renderer, null);
    if (surface == IntPtr.Zero || !SDL.SaveBMP(surface, Path.Combine(output, $"player-{size.Item1}x{size.Item2}.bmp")))
        throw new InvalidOperationException(SDL.GetError());
    SDL.DestroySurface(surface);
    text?.Dispose(); SDL.DestroyRenderer(renderer); SDL.DestroyWindow(window);
}
SDL.Quit();
File.WriteAllText(Path.Combine(output, "ui-result.json"), JsonSerializer.Serialize(new
{
    Passed = true,
    ManualPause = true,
    FocusPause = true,
    QuickSaveRestore = true,
    PerRomPreferences = true,
    WidescreenSwitching = true,
}));
Console.WriteLine("Player UI checks passed: pause, focus, quick save/load, volume and widescreen switching.");
