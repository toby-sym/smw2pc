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
    private const int ToolbarHeight = 72;
    private const int WindowMargin = 16;
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
        SDL.SetRenderVSync(renderer, 0);

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
        var statusMessage = string.IsNullOrEmpty(audioError)
            ? "Choose an .sfc/.smc ROM or a ZIP containing one ROM."
            : $"Choose a ROM. {audioError}";
        SnesRom? loadedRom = null;
        NativeCpuRunner? cpuRunner = null;
        SnesControllerButtons controllerButtons = SnesControllerButtons.None;
        var frameBuffer = new byte[SnesVideoRenderer.Width * SnesVideoRenderer.Height * SnesVideoRenderer.BytesPerPixel];
        var previousTimestamp = Stopwatch.GetTimestamp();
        var frameAccumulator = 0.0;
        var nextPresentationTimestamp = previousTimestamp;
        var settingsOpen = false;
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
            var openButton = new SDL.FRect
            {
                X = WindowMargin,
                Y = outputHeight - 58,
                W = 112,
                H = 28,
            };
            var settingsButton = new SDL.FRect
            {
                X = WindowMargin + 120,
                Y = outputHeight - 58,
                W = 112,
                H = 28,
            };
            var settingsPanel = GetSettingsPanel(outputWidth);

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
                else if (eventType == SDL.EventType.MouseButtonDown
                    && @event.Button.Button == 1
                    && IsInside(@event.Button.X * outputScaleX, @event.Button.Y * outputScaleY, openButton))
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
                else if (eventType == SDL.EventType.MouseButtonDown && @event.Button.Button == 1)
                {
                    var mouseX = @event.Button.X * outputScaleX;
                    var mouseY = @event.Button.Y * outputScaleY;
                    if (IsInside(mouseX, mouseY, settingsButton))
                    {
                        settingsOpen = !settingsOpen;
                    }
                    else if (settingsOpen && HandleSettingsClick(
                        mouseX,
                        mouseY,
                        settingsPanel,
                        settings,
                        window,
                        gameTexture,
                        ref settingsOpen,
                        ref statusMessage))
                    {
                        settingsSavePending = true;
                        settingsSaveTimestamp = Stopwatch.GetTimestamp();
                        nextPresentationTimestamp = settingsSaveTimestamp;
                    }
                }
                else if (eventType is SDL.EventType.KeyDown or SDL.EventType.KeyUp)
                {
                    var isDown = eventType == SDL.EventType.KeyDown;
                    var key = @event.Key.Key;
                    if (isDown && key == SDL.Keycode.F1)
                    {
                        ShowRomFileDialog(window);
                    }
                    else if (isDown && key == SDL.Keycode.F2)
                    {
                        settingsOpen = !settingsOpen;
                    }
                    else if (isDown && key == SDL.Keycode.Escape)
                    {
                        settingsOpen = false;
                    }
                    else
                    {
                        controllerButtons = SetButtonForKey(controllerButtons, key, isDown);
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

                    if (advancedFrames > 0)
                    {
                        SnesVideoRenderer.RenderFrame(cpuRunner.MemoryBus.Hardware, frameBuffer);
                        if (!SDL.UpdateTexture(gameTexture, IntPtr.Zero, frameBuffer, SnesVideoRenderer.Width * SnesVideoRenderer.BytesPerPixel))
                        {
                            statusMessage = $"Could not update the game frame: {SDL.GetError()}";
                        }
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

                var gameArea = GetGameArea(outputWidth, outputHeight, settings.AspectRatio);
                openButton.Y = outputHeight - 58;
                settingsButton.Y = outputHeight - 58;
                settingsPanel = GetSettingsPanel(outputWidth);
                SDL.SetRenderDrawColor(renderer, 24, 28, 40, 255);
                SDL.RenderClear(renderer);

                if (cpuRunner is not null || loadedRom is not null)
                {
                    SDL.RenderTexture(renderer, gameTexture, IntPtr.Zero, in gameArea);
                }
                else
                {
                    SDL.SetRenderDrawColor(renderer, 16, 20, 30, 255);
                    SDL.RenderFillRect(renderer, in gameArea);
                    SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
                    SDL.RenderDebugText(renderer, gameArea.X + 16, gameArea.Y + 24, "SMW2PC");
                    SDL.RenderDebugText(renderer, gameArea.X + 16, gameArea.Y + 48, "Open a Super Nintendo ROM to start.");
                }

                var toolbar = new SDL.FRect
                {
                    X = 0,
                    Y = outputHeight - ToolbarHeight,
                    W = outputWidth,
                    H = ToolbarHeight,
                };
                SDL.SetRenderDrawColor(renderer, 16, 20, 30, 255);
                SDL.RenderFillRect(renderer, in toolbar);
                SDL.SetRenderDrawColor(renderer, 52, 86, 132, 255);
                SDL.RenderFillRect(renderer, in openButton);
                SDL.RenderFillRect(renderer, in settingsButton);
                SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
                SDL.RenderDebugText(renderer, openButton.X + 12, openButton.Y + 8, "Open ROM");
                SDL.RenderDebugText(renderer, settingsButton.X + 8, settingsButton.Y + 8, "Settings · F2");
                var visibleStatus = loadedRom is not null
                    && cpuRunner is not null
                    && statusMessage.StartsWith("Running", StringComparison.Ordinal)
                    ? $"{loadedRom.Header.Title} · sim {loadedRom.Header.FrameRateHz:0.##} Hz · render {settings.RenderFrameRate} FPS"
                    : statusMessage;
                if (!string.IsNullOrEmpty(audioError)
                    && loadedRom is not null
                    && cpuRunner is not null
                    && statusMessage.StartsWith("Running", StringComparison.Ordinal))
                {
                    visibleStatus += $" · {audioError}";
                }

                SDL.RenderDebugText(renderer, 256, outputHeight - 52, visibleStatus);
                SDL.RenderDebugText(renderer, WindowMargin, outputHeight - 20,
                    "Arrows move · Z jump · X run · Enter start · F1 open ROM");

                if (settingsOpen)
                {
                    DrawSettingsPanel(renderer, settingsPanel, settings, loadedRom?.Header.FrameRateHz);
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
        var availableX = (float)WindowMargin;
        var availableY = (float)WindowMargin;
        var availableWidth = Math.Max(1, outputWidth - (WindowMargin * 2));
        var availableHeight = Math.Max(1, outputHeight - ToolbarHeight - (WindowMargin * 2));
        if (aspectRatio == GameAspectRatio.Stretch)
        {
            return new SDL.FRect
            {
                X = availableX,
                Y = availableY,
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
            X = availableX + ((availableWidth - destinationWidth) / 2f),
            Y = availableY + ((availableHeight - destinationHeight) / 2f),
            W = destinationWidth,
            H = destinationHeight,
        };
    }

    private static SDL.ScaleMode GetScaleMode(DesktopSettings settings)
    {
        return settings.SmoothScaling ? SDL.ScaleMode.Linear : SDL.ScaleMode.Nearest;
    }

    private static SDL.FRect GetSettingsPanel(int outputWidth)
    {
        return new SDL.FRect
        {
            X = Math.Max(WindowMargin, outputWidth - 336),
            Y = 32,
            W = 320,
            H = 294,
        };
    }

    private static bool HandleSettingsClick(
        float mouseX,
        float mouseY,
        SDL.FRect panel,
        DesktopSettings settings,
        IntPtr window,
        IntPtr gameTexture,
        ref bool settingsOpen,
        ref string statusMessage)
    {
        if (IsInside(mouseX, mouseY, GetPanelCloseButton(panel)))
        {
            settingsOpen = false;
            return false;
        }

        if (IsInside(mouseX, mouseY, GetResolutionRow(panel)))
        {
            var currentPreset = Array.FindIndex(
                ResolutionPresets,
                preset => preset.Width == settings.WindowWidth && preset.Height == settings.WindowHeight);
            var nextPreset = ResolutionPresets[(currentPreset + 1) % ResolutionPresets.Length];
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
        DesktopSettings settings,
        double? gameFrameRate)
    {
        SDL.SetRenderDrawColor(renderer, 28, 34, 48, 255);
        SDL.RenderFillRect(renderer, in panel);
        SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
        SDL.RenderDebugText(renderer, panel.X + 16, panel.Y + 16, "Display settings");

        var resolutionRow = GetResolutionRow(panel);
        DrawSettingsRow(renderer, resolutionRow, $"Window: {settings.WindowWidth} x {settings.WindowHeight}");
        var aspectRow = GetAspectRatioRow(panel);
        DrawSettingsRow(renderer, aspectRow, $"Aspect ratio: {GetAspectRatioName(settings.AspectRatio)}");

        SDL.RenderDebugText(renderer, panel.X + 16, panel.Y + 145, $"Render cap: {settings.RenderFrameRate} FPS");
        DrawSettingsButton(renderer, GetFrameRateDecreaseButton(panel), "-");
        DrawSettingsButton(renderer, GetFrameRateIncreaseButton(panel), "+");

        var scalingRow = GetScalingRow(panel);
        DrawSettingsRow(renderer, scalingRow, $"Smooth scaling: {(settings.SmoothScaling ? "On" : "Off")}");
        var gameTimingText = gameFrameRate is null
            ? "Game timing: ROM region (50 or 60 Hz)"
            : $"Game timing: fixed at {gameFrameRate:0.##} Hz";
        SDL.RenderDebugText(renderer, panel.X + 16, panel.Y + 224, gameTimingText);
        SDL.RenderDebugText(renderer, panel.X + 16, panel.Y + 240, "Display settings do not change game speed.");
        DrawSettingsButton(renderer, GetPanelCloseButton(panel), "Close");
    }

    private static void DrawSettingsRow(IntPtr renderer, SDL.FRect row, string text)
    {
        SDL.SetRenderDrawColor(renderer, 42, 50, 68, 255);
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
        SDL.RenderDebugText(renderer, button.X + (button.W / 2) - (text.Length * 4), button.Y + 8, text);
    }

    private static SDL.FRect GetResolutionRow(SDL.FRect panel) => new()
    {
        X = panel.X + 12,
        Y = panel.Y + 44,
        W = panel.W - 24,
        H = 36,
    };

    private static SDL.FRect GetAspectRatioRow(SDL.FRect panel) => new()
    {
        X = panel.X + 12,
        Y = panel.Y + 86,
        W = panel.W - 24,
        H = 36,
    };

    private static SDL.FRect GetFrameRateDecreaseButton(SDL.FRect panel) => new()
    {
        X = panel.X + panel.W - 92,
        Y = panel.Y + 132,
        W = 36,
        H = 28,
    };

    private static SDL.FRect GetFrameRateIncreaseButton(SDL.FRect panel) => new()
    {
        X = panel.X + panel.W - 48,
        Y = panel.Y + 132,
        W = 36,
        H = 28,
    };

    private static SDL.FRect GetScalingRow(SDL.FRect panel) => new()
    {
        X = panel.X + 12,
        Y = panel.Y + 172,
        W = panel.W - 24,
        H = 36,
    };

    private static SDL.FRect GetPanelCloseButton(SDL.FRect panel) => new()
    {
        X = panel.X + 12,
        Y = panel.Y + 254,
        W = panel.W - 24,
        H = 30,
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
        bool pressed)
    {
        var button = key switch
        {
            SDL.Keycode.Left => SnesControllerButtons.Left,
            SDL.Keycode.Right => SnesControllerButtons.Right,
            SDL.Keycode.Up => SnesControllerButtons.Up,
            SDL.Keycode.Down => SnesControllerButtons.Down,
            SDL.Keycode.Z => SnesControllerButtons.B,
            SDL.Keycode.X => SnesControllerButtons.A,
            SDL.Keycode.A => SnesControllerButtons.Y,
            SDL.Keycode.S => SnesControllerButtons.X,
            SDL.Keycode.Return => SnesControllerButtons.Start,
            SDL.Keycode.RShift => SnesControllerButtons.Select,
            SDL.Keycode.Q => SnesControllerButtons.L,
            SDL.Keycode.W => SnesControllerButtons.R,
            _ => SnesControllerButtons.None,
        };

        return pressed ? currentButtons | button : currentButtons & ~button;
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
