using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SDL3;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Rom;

internal static class Program
{
    private const int InstructionsPerFrame = 18000;
    private const int MaximumCatchUpFrames = 5;
    private static readonly long SettingsSaveDelayTicks = Stopwatch.Frequency / 2;
    private static readonly (int Width, int Height)[] ResolutionPresets =
    [
        (800, 600),
        (960, 720),
        (1280, 720),
        (1280, 960),
        (1920, 1080),
    ];

    private static readonly ConcurrentQueue<FileDialogResult> PendingFileDialogResults = new();
    private static readonly SDL.DialogFileCallback RomFileDialogCallback = OnRomFileDialogCompleted;

    private enum SettingsTab
    {
        Display,
        Controls,
    }

    private enum GameAction
    {
        Left,
        Right,
        Up,
        Down,
        B,
        Y,
        A,
        X,
        Start,
        Select,
        L,
        R,
    }

    private readonly record struct BindingRow(GameAction Action, string Label, string Hint);

    private static readonly BindingRow[] BindingRows =
    [
        new(GameAction.Left, "Move left", "D-pad"),
        new(GameAction.Right, "Move right", "D-pad"),
        new(GameAction.Up, "Move up", "D-pad"),
        new(GameAction.Down, "Move down", "D-pad"),
        new(GameAction.B, "B", "Jump"),
        new(GameAction.Y, "Y", "Run"),
        new(GameAction.A, "A", "Button"),
        new(GameAction.X, "X", "Button"),
        new(GameAction.Start, "Start", "Pause"),
        new(GameAction.Select, "Select", "Menu"),
        new(GameAction.L, "L", "Shoulder"),
        new(GameAction.R, "R", "Shoulder"),
    ];

    private static int Main()
    {
        var settings = DesktopSettings.Load();
        if (!SDL.Init(SDL.InitFlags.Video))
        {
            Console.Error.WriteLine($"SDL could not initialize: {SDL.GetError()}");
            return 1;
        }

        if (!SDL.CreateWindowAndRenderer(
                "SMW2PC",
                settings.WindowWidth,
                settings.WindowHeight,
                SDL.WindowFlags.Resizable,
                out var window,
                out var renderer))
        {
            Console.Error.WriteLine($"Could not create the window: {SDL.GetError()}");
            SDL.Quit();
            return 1;
        }

        SDL.SetWindowMinimumSize(window, 640, 480);

        var gameTexture = SDL.CreateTexture(
            renderer,
            SDL.PixelFormat.ABGR8888,
            SDL.TextureAccess.Streaming,
            SnesVideoRenderer.Width,
            SnesVideoRenderer.Height);
        if (gameTexture == IntPtr.Zero)
        {
            Console.Error.WriteLine($"Could not create the game texture: {SDL.GetError()}");
            SDL.DestroyRenderer(renderer);
            SDL.DestroyWindow(window);
            SDL.Quit();
            return 1;
        }

        SDL.SetTextureScaleMode(gameTexture, GetScaleMode(settings));
        var verticalSyncEnabled = SDL.SetRenderVSync(renderer, 1);
        if (!verticalSyncEnabled)
        {
            SDL.SetRenderVSync(renderer, 0);
        }

        var audioError = string.Empty;
        var audioStream = IntPtr.Zero;
        if (SDL.InitSubSystem(SDL.InitFlags.Audio))
        {
            var audioSpec = new SDL.AudioSpec
            {
                Format = SDL.AudioFormat.AudioS16LE,
                Channels = 2,
                Freq = 32_000,
            };
            audioStream = SDL.OpenAudioDeviceStream(
                SDL.AudioDeviceDefaultPlayback,
                in audioSpec,
                null!,
                IntPtr.Zero);
            if (audioStream == IntPtr.Zero)
            {
                audioError = $"Audio is disabled: {SDL.GetError()}";
            }
            else if (!SDL.ResumeAudioStreamDevice(audioStream))
            {
                audioError = $"Audio is disabled: {SDL.GetError()}";
                SDL.DestroyAudioStream(audioStream);
                audioStream = IntPtr.Zero;
            }
        }
        else
        {
            audioError = $"Audio is disabled: {SDL.GetError()}";
        }

        var running = true;
        var statusMessage = audioError;
        SnesRom? loadedRom = null;
        NativeCpuRunner? cpuRunner = null;
        SnesControllerButtons controllerButtons = SnesControllerButtons.None;
        var frameBuffer = new byte[SnesVideoRenderer.Width * SnesVideoRenderer.Height * SnesVideoRenderer.BytesPerPixel];
        var previousTimestamp = Stopwatch.GetTimestamp();
        var frameAccumulator = 0.0;
        var nextPresentationTimestamp = previousTimestamp;
        var settingsOpen = false;
        var settingsTab = SettingsTab.Display;
        GameAction? rebindingAction = null;
        var settingsSavePending = false;
        var settingsSaveTimestamp = 0L;

        while (running)
        {
            var currentTimestamp = Stopwatch.GetTimestamp();
            var elapsedSeconds = (double)(currentTimestamp - previousTimestamp) / Stopwatch.Frequency;
            previousTimestamp = currentTimestamp;
            SDL.GetWindowSize(window, out var windowWidth, out var windowHeight);
            if (!SDL.GetCurrentRenderOutputSize(renderer, out var outputWidth, out var outputHeight)
                || outputWidth <= 0
                || outputHeight <= 0)
            {
                outputWidth = windowWidth;
                outputHeight = windowHeight;
            }

            var outputScaleX = windowWidth > 0 ? (float)outputWidth / windowWidth : 1;
            var outputScaleY = windowHeight > 0 ? (float)outputHeight / windowHeight : 1;
            var settingsButton = GetSettingsButton(outputWidth);
            var gameArea = GetGameArea(outputWidth, outputHeight, settings.AspectRatio);
            var launchButton = GetLaunchButton(gameArea);
            var settingsPanel = GetSettingsPanel(outputWidth, outputHeight);

            while (SDL.PollEvent(out var @event))
            {
                var eventType = (SDL.EventType)@event.Type;
                if (eventType == SDL.EventType.Quit)
                {
                    running = false;
                }
                else if (eventType == SDL.EventType.WindowResized)
                {
                    SDL.GetWindowSize(window, out var resizedWidth, out var resizedHeight);
                    settings.WindowWidth = resizedWidth;
                    settings.WindowHeight = resizedHeight;
                    settingsSavePending = true;
                    settingsSaveTimestamp = Stopwatch.GetTimestamp();
                }
                else if (eventType == SDL.EventType.MouseButtonDown && @event.Button.Button == 1)
                {
                    var mouseX = @event.Button.X * outputScaleX;
                    var mouseY = @event.Button.Y * outputScaleY;
                    if (IsInside(mouseX, mouseY, settingsButton))
                    {
                        settingsOpen = !settingsOpen;
                        rebindingAction = null;
                    }
                    else if (settingsOpen && HandleSettingsClick(
                        mouseX,
                        mouseY,
                        settingsPanel,
                        ref settingsTab,
                        settings,
                        window,
                        gameTexture,
                        ref rebindingAction,
                        ref settingsOpen,
                        ref statusMessage))
                    {
                        settingsSavePending = true;
                        settingsSaveTimestamp = Stopwatch.GetTimestamp();
                        nextPresentationTimestamp = settingsSaveTimestamp;
                    }
                    else if (cpuRunner is null && IsInside(mouseX, mouseY, launchButton))
                    {
                        ShowRomFileDialog(window);
                    }
                }
                else if (eventType is SDL.EventType.KeyDown or SDL.EventType.KeyUp)
                {
                    var isDown = eventType == SDL.EventType.KeyDown;
                    var key = @event.Key.Key;
                    if (isDown && rebindingAction.HasValue)
                    {
                        if (key == SDL.Keycode.Escape)
                        {
                            rebindingAction = null;
                            statusMessage = "Key binding cancelled.";
                        }
                        else if (key is SDL.Keycode.F1 or SDL.Keycode.F2)
                        {
                            statusMessage = "F1 and F2 are reserved for app shortcuts.";
                        }
                        else if (TryGetActionForKey(settings, key, out var existingAction)
                            && existingAction != rebindingAction.Value)
                        {
                            statusMessage = $"{GetKeyName(key)} is already assigned to {GetActionName(existingAction)}.";
                        }
                        else
                        {
                            var action = rebindingAction.Value;
                            SetKeyForAction(settings, action, key);
                            rebindingAction = null;
                            controllerButtons = SnesControllerButtons.None;
                            cpuRunner?.MemoryBus.Hardware.SetControllerButtons(0, controllerButtons);
                            statusMessage = $"{GetActionName(action)} is now {GetKeyName(key)}.";
                            settingsSavePending = true;
                            settingsSaveTimestamp = Stopwatch.GetTimestamp();
                        }
                    }
                    else if (isDown && key == SDL.Keycode.F1)
                    {
                        ShowRomFileDialog(window);
                    }
                    else if (isDown && key == SDL.Keycode.F2)
                    {
                        settingsOpen = !settingsOpen;
                        rebindingAction = null;
                    }
                    else if (isDown && key == SDL.Keycode.Escape)
                    {
                        settingsOpen = false;
                    }
                    else
                    {
                        controllerButtons = SetButtonForKey(controllerButtons, key, isDown, settings);
                        cpuRunner?.MemoryBus.Hardware.SetControllerButtons(0, controllerButtons);
                    }
                }
            }

            while (PendingFileDialogResults.TryDequeue(out var dialogResult))
            {
                if (dialogResult.Error is not null)
                {
                    loadedRom = null;
                    statusMessage = dialogResult.Error;
                    continue;
                }

                if (dialogResult.Path is null)
                {
                    continue;
                }

                try
                {
                    loadedRom = SnesRom.Load(dialogResult.Path);
                    var memoryBus = new SnesMemoryBus(loadedRom);
                    cpuRunner = new NativeCpuRunner(memoryBus);
                    memoryBus.Hardware.SetControllerButtons(0, controllerButtons);
                    if (audioStream != IntPtr.Zero)
                    {
                        SDL.ClearAudioStream(audioStream);
                    }

                    frameAccumulator = 1.0 / loadedRom.Header.FrameRateHz;
                    previousTimestamp = Stopwatch.GetTimestamp();
                    nextPresentationTimestamp = previousTimestamp;
                    elapsedSeconds = 0;
                    var regionName = loadedRom.Header.IsPalRegion ? "PAL" : "NTSC";
                    statusMessage = $"Running {loadedRom.Header.Title} · {loadedRom.Header.FrameRateHz:0.##} Hz game timing ({regionName})";
                    if (!string.IsNullOrEmpty(audioError))
                    {
                        statusMessage += $" · {audioError}";
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException)
                {
                    loadedRom = null;
                    cpuRunner = null;
                    statusMessage = exception.Message;
                }
            }

            if (cpuRunner is not null)
            {
                try
                {
                    var frameDuration = 1.0 / loadedRom!.Header.FrameRateHz;
                    frameAccumulator += elapsedSeconds;
                    var advancedFrames = 0;
                    while (frameAccumulator >= frameDuration && advancedFrames < MaximumCatchUpFrames)
                    {
                        cpuRunner.Run(InstructionsPerFrame);
                        frameAccumulator -= frameDuration;
                        advancedFrames++;
                    }

                    var audioSamples = cpuRunner.MemoryBus.Hardware.Apu.DrainSamples();
                    if (audioStream != IntPtr.Zero && audioSamples.Length > 0)
                    {
                        var audioBytes = MemoryMarshal.AsBytes(audioSamples.AsSpan()).ToArray();
                        if (!SDL.PutAudioStreamData(audioStream, audioBytes, audioBytes.Length))
                        {
                            audioError = $"Audio output stopped: {SDL.GetError()}";
                            statusMessage = audioError;
                            SDL.DestroyAudioStream(audioStream);
                            audioStream = IntPtr.Zero;
                        }
                    }

                    var interpolationProgress = (float)(frameAccumulator / frameDuration);
                    SnesVideoRenderer.RenderFrame(
                        cpuRunner.MemoryBus.Hardware,
                        frameBuffer,
                        interpolationProgress);
                    if (!SDL.UpdateTexture(gameTexture, IntPtr.Zero, frameBuffer, SnesVideoRenderer.Width * SnesVideoRenderer.BytesPerPixel))
                    {
                        statusMessage = $"Could not update the game frame: {SDL.GetError()}";
                    }
                }
                catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
                {
                    statusMessage = $"Execution stopped at {cpuRunner.State.ProgramBank:X2}:{cpuRunner.State.ProgramCounter:X4}: {exception.Message}";
                    cpuRunner = null;
                    frameAccumulator = 0;
                }
            }
            else
            {
                frameAccumulator = 0;
            }

            var presentationTimestamp = Stopwatch.GetTimestamp();
            var renderIntervalTicks = Stopwatch.Frequency / settings.RenderFrameRate;
            if (presentationTimestamp >= nextPresentationTimestamp)
            {
                if (!SDL.GetCurrentRenderOutputSize(renderer, out outputWidth, out outputHeight)
                    || outputWidth <= 0
                    || outputHeight <= 0)
                {
                    outputWidth = windowWidth;
                    outputHeight = windowHeight;
                }

                gameArea = GetGameArea(outputWidth, outputHeight, settings.AspectRatio);
                launchButton = GetLaunchButton(gameArea);
                settingsButton = GetSettingsButton(outputWidth);
                settingsPanel = GetSettingsPanel(outputWidth, outputHeight);
                SDL.SetRenderDrawColor(renderer, 8, 11, 18, 255);
                SDL.RenderClear(renderer);

                if (cpuRunner is not null || loadedRom is not null)
                {
                    SDL.RenderTexture(renderer, gameTexture, IntPtr.Zero, in gameArea);
                }
                else
                {
                    SDL.SetRenderDrawColor(renderer, 12, 19, 31, 255);
                    SDL.RenderFillRect(renderer, in gameArea);
                    var titleX = gameArea.X + ((gameArea.W - 160) / 2);
                    var titleY = gameArea.Y + (gameArea.H * 0.42f);
                    SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
                    SDL.RenderDebugText(renderer, titleX, titleY, "SMW2PC");
                    SDL.RenderDebugText(renderer, titleX - 64, titleY + 28, "A native Super Mario World runner");
                    SDL.SetRenderDrawColor(renderer, 52, 86, 132, 255);
                    SDL.RenderFillRect(renderer, in launchButton);
                    SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
                    SDL.RenderDebugText(renderer, launchButton.X + 22, launchButton.Y + 11, "OPEN ROM");
                    SDL.RenderDebugText(renderer, titleX - 80, launchButton.Y + 48, "Choose an .sfc, .smc, or ZIP file");
                    if (!string.IsNullOrWhiteSpace(statusMessage))
                    {
                        SDL.SetRenderDrawColor(renderer, 242, 190, 128, 255);
                        SDL.RenderDebugText(renderer, gameArea.X + 24, gameArea.Y + gameArea.H - 30,
                            Truncate(statusMessage, Math.Max(12, (int)(gameArea.W / 8))));
                    }
                }

                DrawSettingsCog(renderer, settingsButton, settingsOpen);

                if (settingsOpen)
                {
                    DrawSettingsPanel(
                        renderer,
                        settingsPanel,
                        settingsTab,
                        rebindingAction,
                        settings,
                        loadedRom?.Header.FrameRateHz,
                        statusMessage,
                        audioError,
                        verticalSyncEnabled);
                }

                SDL.RenderPresent(renderer);

                if (presentationTimestamp >= nextPresentationTimestamp)
                {
                    var elapsedIntervals = ((presentationTimestamp - nextPresentationTimestamp) / renderIntervalTicks) + 1;
                    nextPresentationTimestamp += elapsedIntervals * renderIntervalTicks;
                }
            }

            var timestampAfterPresent = Stopwatch.GetTimestamp();
            if (settingsSavePending && timestampAfterPresent - settingsSaveTimestamp >= SettingsSaveDelayTicks)
            {
                settings.Save();
                settingsSavePending = false;
            }

            var timeUntilNextPresentation = nextPresentationTimestamp - timestampAfterPresent;
            if (timeUntilNextPresentation > 0)
            {
                var delayNanoseconds = (ulong)(timeUntilNextPresentation * 1_000_000_000.0 / Stopwatch.Frequency);
                SDL.DelayPrecise(delayNanoseconds);
            }
        }

        settings.Save();

        if (audioStream != IntPtr.Zero)
        {
            SDL.DestroyAudioStream(audioStream);
        }

        SDL.DestroyTexture(gameTexture);
        SDL.DestroyRenderer(renderer);
        SDL.DestroyWindow(window);
        SDL.Quit();
        return 0;
    }

    private static SDL.FRect GetGameArea(int outputWidth, int outputHeight, GameAspectRatio aspectRatio)
    {
        var availableWidth = Math.Max(1, outputWidth);
        var availableHeight = Math.Max(1, outputHeight);
        if (aspectRatio == GameAspectRatio.Stretch)
        {
            return new SDL.FRect
            {
                X = 0,
                Y = 0,
                W = availableWidth,
                H = availableHeight,
            };
        }

        var targetAspect = aspectRatio switch
        {
            GameAspectRatio.OriginalPixels => (float)SnesVideoRenderer.Width / SnesVideoRenderer.Height,
            GameAspectRatio.Widescreen => 16f / 9,
            _ => 4f / 3,
        };
        var availableAspect = (float)availableWidth / availableHeight;
        var destinationWidth = availableWidth;
        var destinationHeight = availableHeight;
        if (availableAspect > targetAspect)
        {
            destinationWidth = (int)(availableHeight * targetAspect);
        }
        else
        {
            destinationHeight = (int)(availableWidth / targetAspect);
        }

        return new SDL.FRect
        {
            X = (availableWidth - destinationWidth) / 2f,
            Y = (availableHeight - destinationHeight) / 2f,
            W = destinationWidth,
            H = destinationHeight,
        };
    }

    private static SDL.ScaleMode GetScaleMode(DesktopSettings settings)
    {
        return settings.SmoothScaling ? SDL.ScaleMode.Linear : SDL.ScaleMode.Nearest;
    }

    private static SDL.FRect GetSettingsButton(int outputWidth) => new()
    {
        X = outputWidth - 60,
        Y = 16,
        W = 44,
        H = 44,
    };

    private static SDL.FRect GetLaunchButton(SDL.FRect gameArea) => new()
    {
        X = gameArea.X + ((gameArea.W - 180) / 2),
        Y = gameArea.Y + (gameArea.H * 0.58f),
        W = 180,
        H = 40,
    };

    private static SDL.FRect GetSettingsPanel(int outputWidth, int outputHeight)
    {
        var width = Math.Min(360, outputWidth - 24);
        var height = Math.Min(560, outputHeight - 24);
        return new SDL.FRect
        {
            X = outputWidth - width - 12,
            Y = (outputHeight - height) / 2f,
            W = width,
            H = height,
        };
    }

    private static SDL.FRect GetDisplayTabButton(SDL.FRect panel) => new()
    {
        X = panel.X + 12,
        Y = panel.Y + 52,
        W = (panel.W - 28) / 2,
        H = 30,
    };

    private static SDL.FRect GetControlsTabButton(SDL.FRect panel) => new()
    {
        X = panel.X + 16 + ((panel.W - 28) / 2),
        Y = panel.Y + 52,
        W = (panel.W - 28) / 2,
        H = 30,
    };

    private static SDL.FRect GetPanelOpenRomButton(SDL.FRect panel) => new()
    {
        X = panel.X + panel.W - 132,
        Y = panel.Y + 12,
        W = 84,
        H = 28,
    };

    private static SDL.FRect GetPanelCloseButton(SDL.FRect panel) => new()
    {
        X = panel.X + panel.W - 40,
        Y = panel.Y + 12,
        W = 28,
        H = 28,
    };

    private static bool HandleSettingsClick(
        float mouseX,
        float mouseY,
        SDL.FRect panel,
        ref SettingsTab settingsTab,
        DesktopSettings settings,
        IntPtr window,
        IntPtr gameTexture,
        ref GameAction? rebindingAction,
        ref bool settingsOpen,
        ref string statusMessage)
    {
        if (IsInside(mouseX, mouseY, GetDisplayTabButton(panel)))
        {
            settingsTab = SettingsTab.Display;
            rebindingAction = null;
            return true;
        }

        if (IsInside(mouseX, mouseY, GetControlsTabButton(panel)))
        {
            settingsTab = SettingsTab.Controls;
            rebindingAction = null;
            return true;
        }

        if (IsInside(mouseX, mouseY, GetPanelCloseButton(panel)))
        {
            settingsOpen = false;
            rebindingAction = null;
            return true;
        }

        if (IsInside(mouseX, mouseY, GetPanelOpenRomButton(panel)))
        {
            ShowRomFileDialog(window);
            return true;
        }

        if (settingsTab == SettingsTab.Controls)
        {
            for (var index = 0; index < BindingRows.Length; index++)
            {
                if (IsInside(mouseX, mouseY, GetBindingRow(panel, index)))
                {
                    rebindingAction = BindingRows[index].Action;
                    statusMessage = $"Press a key for {GetActionName(rebindingAction.Value)}. Escape cancels.";
                    return true;
                }
            }

            return false;
        }

        if (IsInside(mouseX, mouseY, GetResolutionRow(panel)))
        {
            var currentPreset = Array.FindIndex(
                ResolutionPresets,
                preset => preset.Width == settings.WindowWidth && preset.Height == settings.WindowHeight);
            var nextPreset = ResolutionPresets[(currentPreset + 1 + ResolutionPresets.Length) % ResolutionPresets.Length];
            if (!SDL.SetWindowSize(window, nextPreset.Width, nextPreset.Height))
            {
                statusMessage = $"Could not resize the window: {SDL.GetError()}";
                return false;
            }

            settings.WindowWidth = nextPreset.Width;
            settings.WindowHeight = nextPreset.Height;
            return true;
        }

        if (IsInside(mouseX, mouseY, GetAspectRatioRow(panel)))
        {
            var aspectCount = Enum.GetValues<GameAspectRatio>().Length;
            settings.AspectRatio = (GameAspectRatio)(((int)settings.AspectRatio + 1) % aspectCount);
            return true;
        }

        if (IsInside(mouseX, mouseY, GetFrameRateDecreaseButton(panel)))
        {
            var updatedFrameRate = DesktopSettings.ClampRenderFrameRate(settings.RenderFrameRate - 5);
            if (updatedFrameRate == settings.RenderFrameRate)
            {
                return false;
            }

            settings.RenderFrameRate = updatedFrameRate;
            return true;
        }

        if (IsInside(mouseX, mouseY, GetFrameRateIncreaseButton(panel)))
        {
            var updatedFrameRate = DesktopSettings.ClampRenderFrameRate(settings.RenderFrameRate + 5);
            if (updatedFrameRate == settings.RenderFrameRate)
            {
                return false;
            }

            settings.RenderFrameRate = updatedFrameRate;
            return true;
        }

        if (IsInside(mouseX, mouseY, GetScalingRow(panel)))
        {
            var smoothScaling = !settings.SmoothScaling;
            if (!SDL.SetTextureScaleMode(
                    gameTexture,
                    smoothScaling ? SDL.ScaleMode.Linear : SDL.ScaleMode.Nearest))
            {
                statusMessage = $"Could not change texture scaling: {SDL.GetError()}";
                return false;
            }

            settings.SmoothScaling = smoothScaling;
            return true;
        }

        return false;
    }

    private static void DrawSettingsPanel(
        IntPtr renderer,
        SDL.FRect panel,
        SettingsTab settingsTab,
        GameAction? rebindingAction,
        DesktopSettings settings,
        double? gameFrameRate,
        string statusMessage,
        string audioError,
        bool verticalSyncEnabled)
    {
        SDL.SetRenderDrawColor(renderer, 8, 11, 18, 248);
        SDL.RenderFillRect(renderer, in panel);
        SDL.SetRenderDrawColor(renderer, 54, 67, 88, 255);
        SDL.RenderRect(renderer, in panel);
        SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
        SDL.RenderDebugText(renderer, panel.X + 16, panel.Y + 20, "SETTINGS");
        DrawSettingsButton(renderer, GetPanelOpenRomButton(panel), "OPEN ROM");
        DrawSettingsButton(renderer, GetPanelCloseButton(panel), "X");
        DrawSettingsTab(renderer, GetDisplayTabButton(panel), "DISPLAY", settingsTab == SettingsTab.Display);
        DrawSettingsTab(renderer, GetControlsTabButton(panel), "CONTROLS", settingsTab == SettingsTab.Controls);

        if (settingsTab == SettingsTab.Display)
        {
            DrawSettingsRow(renderer, GetResolutionRow(panel), $"Window size: {settings.WindowWidth} x {settings.WindowHeight}");
            DrawSettingsRow(renderer, GetAspectRatioRow(panel), $"Aspect ratio: {GetAspectRatioName(settings.AspectRatio)}");
            DrawSettingsRow(renderer, GetScalingRow(panel), $"Smooth filtering: {(settings.SmoothScaling ? "On" : "Off")}");

            SDL.RenderDebugText(renderer, panel.X + 20, panel.Y + 246, $"Presentation cap: {settings.RenderFrameRate} FPS");
            DrawSettingsButton(renderer, GetFrameRateDecreaseButton(panel), "-");
            DrawSettingsButton(renderer, GetFrameRateIncreaseButton(panel), "+");

            var gameTimingText = gameFrameRate is null
                ? "Game timing follows the ROM region (50 or 60 Hz)."
                : $"Game timing stays fixed at {gameFrameRate:0.##} Hz.";
            SDL.RenderDebugText(renderer, panel.X + 20, panel.Y + 284, gameTimingText);
            SDL.RenderDebugText(renderer, panel.X + 20, panel.Y + 302, "Display settings do not change physics.");

            var systemStatus = !string.IsNullOrWhiteSpace(audioError)
                ? audioError
                : !verticalSyncEnabled
                    ? "Vertical sync is unavailable on this display."
                    : statusMessage.StartsWith("Execution stopped", StringComparison.Ordinal)
                        || statusMessage.StartsWith("Could not", StringComparison.Ordinal)
                            ? statusMessage
                            : string.Empty;
            if (!string.IsNullOrWhiteSpace(systemStatus))
            {
                SDL.SetRenderDrawColor(renderer, 242, 190, 128, 255);
                SDL.RenderDebugText(renderer, panel.X + 20, panel.Y + panel.H - 32,
                    Truncate(systemStatus, Math.Max(16, (int)((panel.W - 40) / 8))));
            }
        }
        else
        {
            var controlsMessage = rebindingAction.HasValue
                || statusMessage.Contains("already assigned", StringComparison.Ordinal)
                || statusMessage.Contains("reserved", StringComparison.Ordinal)
                    ? statusMessage
                    : "Choose an action, then press a key.";
            SDL.RenderDebugText(renderer, panel.X + 20, panel.Y + 92,
                Truncate(controlsMessage, Math.Max(16, (int)((panel.W - 40) / 8))));
            for (var index = 0; index < BindingRows.Length; index++)
            {
                var row = BindingRows[index];
                DrawBindingRow(renderer, GetBindingRow(panel, index), row,
                    GetBoundKey(settings, row.Action), rebindingAction == row.Action);
            }

            SDL.SetRenderDrawColor(renderer, 177, 189, 205, 255);
            SDL.RenderDebugText(renderer, panel.X + 20, panel.Y + panel.H - 24,
                "F1 opens ROMs  |  F2 toggles settings  |  Esc cancels");
        }
    }

    private static void DrawSettingsRow(IntPtr renderer, SDL.FRect row, string text)
    {
        SDL.SetRenderDrawColor(renderer, 28, 36, 50, 255);
        SDL.RenderFillRect(renderer, in row);
        SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
        SDL.RenderDebugText(renderer, row.X + 10, row.Y + 12, text);
        SDL.RenderDebugText(renderer, row.X + row.W - 20, row.Y + 12, ">");
    }

    private static void DrawSettingsButton(IntPtr renderer, SDL.FRect button, string text)
    {
        SDL.SetRenderDrawColor(renderer, 52, 86, 132, 255);
        SDL.RenderFillRect(renderer, in button);
        SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
        SDL.RenderDebugText(renderer, button.X + (button.W / 2) - (text.Length * 4), button.Y + ((button.H - 8) / 2), text);
    }

    private static void DrawSettingsTab(IntPtr renderer, SDL.FRect tab, string text, bool selected)
    {
        SDL.SetRenderDrawColor(renderer, selected ? (byte)56 : (byte)27,
            selected ? (byte)91 : (byte)37, selected ? (byte)142 : (byte)51, 255);
        SDL.RenderFillRect(renderer, in tab);
        SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
        SDL.RenderDebugText(renderer, tab.X + (tab.W / 2) - (text.Length * 4), tab.Y + 11, text);
    }

    private static void DrawBindingRow(
        IntPtr renderer,
        SDL.FRect row,
        BindingRow binding,
        SDL.Keycode key,
        bool isListening)
    {
        SDL.SetRenderDrawColor(renderer, isListening ? (byte)58 : (byte)28,
            isListening ? (byte)67 : (byte)36, isListening ? (byte)87 : (byte)50, 255);
        SDL.RenderFillRect(renderer, in row);
        SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
        var label = string.IsNullOrEmpty(binding.Hint) ? binding.Label : $"{binding.Label}  {binding.Hint}";
        SDL.RenderDebugText(renderer, row.X + 8, row.Y + 7, label);

        var keyButton = new SDL.FRect
        {
            X = row.X + row.W - 106,
            Y = row.Y + 2,
            W = 98,
            H = row.H - 4,
        };
        DrawSettingsButton(renderer, keyButton, isListening ? "PRESS KEY" : GetKeyName(key));
    }

    private static void DrawSettingsCog(IntPtr renderer, SDL.FRect button, bool selected)
    {
        SDL.SetRenderDrawColor(renderer, selected ? (byte)56 : (byte)15,
            selected ? (byte)91 : (byte)22, selected ? (byte)142 : (byte)35, 235);
        SDL.RenderFillRect(renderer, in button);
        SDL.SetRenderDrawColor(renderer, 238, 244, 252, 255);
        var centerX = button.X + (button.W / 2);
        var centerY = button.Y + (button.H / 2);
        var spokes = new (float X, float Y)[]
        {
            (0, -1), (0.71f, -0.71f), (1, 0), (0.71f, 0.71f),
            (0, 1), (-0.71f, 0.71f), (-1, 0), (-0.71f, -0.71f),
        };
        foreach (var spoke in spokes)
        {
            SDL.RenderLine(renderer,
                centerX + (spoke.X * 6), centerY + (spoke.Y * 6),
                centerX + (spoke.X * 14), centerY + (spoke.Y * 14));
        }

        var hub = new SDL.FRect { X = centerX - 4, Y = centerY - 4, W = 8, H = 8 };
        SDL.RenderFillRect(renderer, in hub);
    }

    private static SDL.FRect GetResolutionRow(SDL.FRect panel) => new()
    {
        X = panel.X + 12,
        Y = panel.Y + 100,
        W = panel.W - 24,
        H = 34,
    };

    private static SDL.FRect GetAspectRatioRow(SDL.FRect panel) => new()
    {
        X = panel.X + 12,
        Y = panel.Y + 142,
        W = panel.W - 24,
        H = 34,
    };

    private static SDL.FRect GetFrameRateDecreaseButton(SDL.FRect panel) => new()
    {
        X = panel.X + panel.W - 92,
        Y = panel.Y + 230,
        W = 36,
        H = 30,
    };

    private static SDL.FRect GetFrameRateIncreaseButton(SDL.FRect panel) => new()
    {
        X = panel.X + panel.W - 48,
        Y = panel.Y + 230,
        W = 36,
        H = 30,
    };

    private static SDL.FRect GetScalingRow(SDL.FRect panel) => new()
    {
        X = panel.X + 12,
        Y = panel.Y + 184,
        W = panel.W - 24,
        H = 34,
    };

    private static SDL.FRect GetBindingRow(SDL.FRect panel, int index) => new()
    {
        X = panel.X + 12,
        Y = panel.Y + 104 + (index * 22),
        W = panel.W - 24,
        H = 21,
    };

    private static string GetAspectRatioName(GameAspectRatio aspectRatio)
    {
        return aspectRatio switch
        {
            GameAspectRatio.OriginalPixels => "Original pixels (8:7)",
            GameAspectRatio.Widescreen => "Widescreen (16:9)",
            GameAspectRatio.Stretch => "Stretch to window",
            _ => "Television (4:3)",
        };
    }

    private static string GetKeyName(SDL.Keycode key)
    {
        var keyName = SDL.GetKeyName(key);
        return string.IsNullOrWhiteSpace(keyName) ? key.ToString() : keyName.ToUpperInvariant();
    }

    private static string GetActionName(GameAction action)
    {
        return action switch
        {
            GameAction.Left => "Move left",
            GameAction.Right => "Move right",
            GameAction.Up => "Move up",
            GameAction.Down => "Move down",
            GameAction.B => "B / jump",
            GameAction.Y => "Y / run",
            GameAction.A => "A",
            GameAction.X => "X",
            GameAction.Start => "Start",
            GameAction.Select => "Select",
            GameAction.L => "L",
            GameAction.R => "R",
            _ => action.ToString(),
        };
    }

    private static string Truncate(string text, int maximumLength)
    {
        return text.Length <= maximumLength ? text : $"{text[..Math.Max(0, maximumLength - 3)]}...";
    }

    private static SDL.Keycode GetBoundKey(DesktopSettings settings, GameAction action)
    {
        return action switch
        {
            GameAction.Left => settings.MoveLeftKey,
            GameAction.Right => settings.MoveRightKey,
            GameAction.Up => settings.MoveUpKey,
            GameAction.Down => settings.MoveDownKey,
            GameAction.B => settings.ButtonBKey,
            GameAction.Y => settings.ButtonYKey,
            GameAction.A => settings.ButtonAKey,
            GameAction.X => settings.ButtonXKey,
            GameAction.Start => settings.StartKey,
            GameAction.Select => settings.SelectKey,
            GameAction.L => settings.ButtonLKey,
            GameAction.R => settings.ButtonRKey,
            _ => SDL.Keycode.Unknown,
        };
    }

    private static void SetKeyForAction(DesktopSettings settings, GameAction action, SDL.Keycode key)
    {
        switch (action)
        {
            case GameAction.Left: settings.MoveLeftKey = key; break;
            case GameAction.Right: settings.MoveRightKey = key; break;
            case GameAction.Up: settings.MoveUpKey = key; break;
            case GameAction.Down: settings.MoveDownKey = key; break;
            case GameAction.B: settings.ButtonBKey = key; break;
            case GameAction.Y: settings.ButtonYKey = key; break;
            case GameAction.A: settings.ButtonAKey = key; break;
            case GameAction.X: settings.ButtonXKey = key; break;
            case GameAction.Start: settings.StartKey = key; break;
            case GameAction.Select: settings.SelectKey = key; break;
            case GameAction.L: settings.ButtonLKey = key; break;
            case GameAction.R: settings.ButtonRKey = key; break;
        }
    }

    private static bool TryGetActionForKey(DesktopSettings settings, SDL.Keycode key, out GameAction action)
    {
        foreach (var row in BindingRows)
        {
            if (GetBoundKey(settings, row.Action) == key)
            {
                action = row.Action;
                return true;
            }
        }

        action = default;
        return false;
    }

    private static void OnRomFileDialogCompleted(IntPtr userdata, IntPtr fileList, int filter)
    {
        if (fileList == IntPtr.Zero)
        {
            PendingFileDialogResults.Enqueue(new FileDialogResult(null, "The ROM file dialog failed."));
            return;
        }

        var selectedFile = Marshal.ReadIntPtr(fileList);
        if (selectedFile == IntPtr.Zero)
        {
            return;
        }

        var path = Marshal.PtrToStringUTF8(selectedFile);
        if (!string.IsNullOrWhiteSpace(path))
        {
            PendingFileDialogResults.Enqueue(new FileDialogResult(path, null));
        }
    }

    private static void ShowRomFileDialog(IntPtr window)
    {
        SDL.ShowOpenFileDialog(
            RomFileDialogCallback,
            IntPtr.Zero,
            window,
            null!,
            0,
            null!,
            false);
    }

    private static SnesControllerButtons SetButtonForKey(
        SnesControllerButtons currentButtons,
        SDL.Keycode key,
        bool pressed,
        DesktopSettings settings)
    {
        foreach (var row in BindingRows)
        {
            if (GetBoundKey(settings, row.Action) != key)
            {
                continue;
            }

            var button = row.Action switch
            {
                GameAction.Left => SnesControllerButtons.Left,
                GameAction.Right => SnesControllerButtons.Right,
                GameAction.Up => SnesControllerButtons.Up,
                GameAction.Down => SnesControllerButtons.Down,
                GameAction.B => SnesControllerButtons.B,
                GameAction.Y => SnesControllerButtons.Y,
                GameAction.A => SnesControllerButtons.A,
                GameAction.X => SnesControllerButtons.X,
                GameAction.Start => SnesControllerButtons.Start,
                GameAction.Select => SnesControllerButtons.Select,
                GameAction.L => SnesControllerButtons.L,
                GameAction.R => SnesControllerButtons.R,
                _ => SnesControllerButtons.None,
            };

            return pressed ? currentButtons | button : currentButtons & ~button;
        }

        return currentButtons;
    }

    private static bool IsInside(float x, float y, SDL.FRect rectangle)
    {
        return x >= rectangle.X
            && x < rectangle.X + rectangle.W
            && y >= rectangle.Y
            && y < rectangle.Y + rectangle.H;
    }

    private sealed record FileDialogResult(string? Path, string? Error);
}
