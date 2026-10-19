using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SDL3;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Recompilation;
using Smw2Pc.Core.Rom;

internal static class Program
{
    private const string CompiledRomResourceName = "Smw2Pc.Desktop.CompiledRom";
    private const int InstructionsPerFrame = 18000;
    private const int MaximumCatchUpFrames = 5;
    private const int MaximumSoftwarePresentationRate = DesktopSettings.MaximumRenderFrameRate;
    private const int MaximumVSyncPresentationRate = 90;
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

    private static int Main(string[] args)
    {
        if (args.Contains("--verify-native")) return NativeDependencyProof.Run();
        if (!NativeCpuRunner.IsRecompiled)
        {
            Console.Error.WriteLine("The desktop player requires a ROM-specific compiled build. Run tools/build-rom-exe.ps1 with the target ROM.");
            return 2;
        }
        if (args.Contains("--verify") || args.Contains("--headless"))
            return HeadlessProof.Run(args);
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

        var textRenderer = UiTextRenderer.TryCreate(
            renderer,
            BundledAssets.GetFontPath());

        SDL.SetTextureScaleMode(gameTexture, SDL.ScaleMode.Nearest);
        var displayId = SDL.GetDisplayForWindow(window);
        var displayRefreshRate = GetDisplayRefreshRate(displayId);
        var configuredRenderRate = settings.RenderFrameRate;
        var presentationRefreshDivisor = GetPresentationRefreshDivisor(configuredRenderRate, displayRefreshRate);
        var presentationFrameRate = GetPresentationFrameRate(configuredRenderRate, displayRefreshRate, presentationRefreshDivisor);
        var verticalSyncEnabled = TrySetPresentationVSync(
            renderer,
            displayRefreshRate,
            presentationRefreshDivisor,
            presentationFrameRate);
        var nextDisplayModeCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
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
        var smoothedRenderToPresentTicks = 0L;

        using (var compiledRomStream = typeof(Program).Assembly.GetManifestResourceStream(CompiledRomResourceName))
        {
            if (compiledRomStream is not null)
            {
                try
                {
                    using var romBuffer = new MemoryStream();
                    compiledRomStream.CopyTo(romBuffer);
                    loadedRom = SnesRom.LoadBytes(romBuffer.ToArray(), "compiled.sfc");
                    var memoryBus = new SnesMemoryBus(loadedRom);
                    cpuRunner = new NativeCpuRunner(memoryBus);
                    memoryBus.Hardware.SetControllerButtons(0, controllerButtons);
                    frameAccumulator = 1.0 / loadedRom.Header.FrameRateHz;
                    smoothedRenderToPresentTicks = 0;
                    previousTimestamp = Stopwatch.GetTimestamp();
                    nextPresentationTimestamp = previousTimestamp;
                    var regionName = loadedRom.Header.IsPalRegion ? "PAL" : "NTSC";
                    statusMessage = $"Running compiled {loadedRom.Header.Title} · {loadedRom.Header.FrameRateHz:0.##} Hz game timing ({regionName})";
                    if (!string.IsNullOrEmpty(audioError))
                    {
                        statusMessage += $" · {audioError}";
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                        or InvalidDataException
                        or ArgumentException
                        or NotSupportedException)
                {
                    statusMessage = $"Could not start the compiled ROM: {exception.Message}";
                }
            }
        }

        while (running)
        {
            var currentTimestamp = Stopwatch.GetTimestamp();
            if (currentTimestamp >= nextDisplayModeCheck)
            {
                var currentDisplayId = SDL.GetDisplayForWindow(window);
                if (currentDisplayId != displayId)
                {
                    displayId = currentDisplayId;
                    displayRefreshRate = GetDisplayRefreshRate(displayId);
                    presentationRefreshDivisor = GetPresentationRefreshDivisor(settings.RenderFrameRate, displayRefreshRate);
                    presentationFrameRate = GetPresentationFrameRate(settings.RenderFrameRate, displayRefreshRate, presentationRefreshDivisor);
                    verticalSyncEnabled = TrySetPresentationVSync(
                        renderer,
                        displayRefreshRate,
                        presentationRefreshDivisor,
                        presentationFrameRate);
                    configuredRenderRate = settings.RenderFrameRate;
                    smoothedRenderToPresentTicks = 0;
                    nextPresentationTimestamp = currentTimestamp;
                }
                else
                {
                    var currentRefreshRate = GetDisplayRefreshRate(currentDisplayId);
                    if (Math.Abs(currentRefreshRate - displayRefreshRate) > 0.01)
                    {
                        displayRefreshRate = currentRefreshRate;
                        presentationRefreshDivisor = GetPresentationRefreshDivisor(settings.RenderFrameRate, displayRefreshRate);
                        presentationFrameRate = GetPresentationFrameRate(settings.RenderFrameRate, displayRefreshRate, presentationRefreshDivisor);
                        verticalSyncEnabled = TrySetPresentationVSync(
                            renderer,
                            displayRefreshRate,
                            presentationRefreshDivisor,
                            presentationFrameRate);
                        configuredRenderRate = settings.RenderFrameRate;
                        smoothedRenderToPresentTicks = 0;
                        nextPresentationTimestamp = currentTimestamp;
                    }
                }

                nextDisplayModeCheck = currentTimestamp + Stopwatch.Frequency;
            }

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

            var presentationTimestamp = Stopwatch.GetTimestamp();
            if (settings.RenderFrameRate != configuredRenderRate)
            {
                configuredRenderRate = settings.RenderFrameRate;
                presentationRefreshDivisor = GetPresentationRefreshDivisor(configuredRenderRate, displayRefreshRate);
                presentationFrameRate = GetPresentationFrameRate(configuredRenderRate, displayRefreshRate, presentationRefreshDivisor);
                verticalSyncEnabled = TrySetPresentationVSync(
                    renderer,
                    displayRefreshRate,
                    presentationRefreshDivisor,
                    presentationFrameRate);
                smoothedRenderToPresentTicks = 0;
                nextPresentationTimestamp = presentationTimestamp;
            }

            var renderIntervalTicks = Math.Max(1, (long)Math.Round(Stopwatch.Frequency / presentationFrameRate));
            var renderRequested = presentationTimestamp >= nextPresentationTimestamp;
            var presentedFrameRendererStart = 0L;

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

                    if (renderRequested)
                    {
                        var renderTimestamp = Stopwatch.GetTimestamp();
                        var timeSinceLoopStart = (double)(renderTimestamp - currentTimestamp) / Stopwatch.Frequency;
                        var interpolationProgress = (float)Math.Clamp(
                            (frameAccumulator + timeSinceLoopStart + (smoothedRenderToPresentTicks / (double)Stopwatch.Frequency)) / frameDuration,
                            0.0,
                            1.0);
                        presentedFrameRendererStart = Stopwatch.GetTimestamp();
                        SnesVideoRenderer.RenderFrame(
                            cpuRunner.MemoryBus.Hardware,
                            frameBuffer,
                            interpolationProgress);
                        if (!SDL.UpdateTexture(gameTexture, IntPtr.Zero, frameBuffer, SnesVideoRenderer.Width * SnesVideoRenderer.BytesPerPixel))
                        {
                            statusMessage = $"Could not update the game frame: {SDL.GetError()}";
                        }
                    }
                }
                catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
                {
                    statusMessage = $"Execution stopped at {cpuRunner.State.ProgramBank:X2}:{cpuRunner.State.ProgramCounter:X4}: {exception.Message}";
                    if (exception is MissingCompiledCodeException)
                    {
                        RecordExecutionFailure(exception, cpuRunner);
                    }
                    cpuRunner = null;
                    frameAccumulator = 0;
                }
            }
            else
            {
                frameAccumulator = 0;
            }

            if (renderRequested)
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
                    DrawLaunchScreen(renderer, textRenderer, gameArea, launchButton, statusMessage);
                }

                DrawSettingsCog(renderer, settingsButton, settingsOpen);

                if (settingsOpen)
                {
                    DrawSettingsPanel(
                        renderer,
                        textRenderer,
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

                var presentedTimestamp = Stopwatch.GetTimestamp();
                if (presentedFrameRendererStart != 0)
                {
                    var rendererToPresentTicks = presentedTimestamp - presentedFrameRendererStart;
                    smoothedRenderToPresentTicks = smoothedRenderToPresentTicks == 0
                        ? rendererToPresentTicks
                        : smoothedRenderToPresentTicks + ((rendererToPresentTicks - smoothedRenderToPresentTicks) / 8);
                }

                var updatedRenderIntervalTicks = Math.Max(1, (long)Math.Round(Stopwatch.Frequency / presentationFrameRate));
                nextPresentationTimestamp += updatedRenderIntervalTicks;
                if (nextPresentationTimestamp < presentedTimestamp)
                {
                    // If rendering missed its slot, continue as soon as the work finishes.
                    // Advancing to the next future slot here adds a full extra interval after
                    // every slow frame and can cut the effective frame rate in half.
                    nextPresentationTimestamp = presentedTimestamp;
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

        textRenderer?.Dispose();
        SDL.DestroyTexture(gameTexture);
        SDL.DestroyRenderer(renderer);
        SDL.DestroyWindow(window);
        SDL.Quit();
        return 0;
    }

    private static double GetDisplayRefreshRate(uint displayId)
    {
        if (displayId == 0)
        {
            return 0;
        }

        var displayMode = SDL.GetCurrentDisplayMode(displayId);
        if (!displayMode.HasValue)
        {
            return 0;
        }

        var currentMode = displayMode.Value;
        if (currentMode.RefreshRateNumerator > 0 && currentMode.RefreshRateDenominator > 0)
        {
            return (double)currentMode.RefreshRateNumerator / currentMode.RefreshRateDenominator;
        }

        return currentMode.RefreshRate;
    }

    private static int GetPresentationRefreshDivisor(int configuredRate, double displayRefreshRate)
    {
        if (!double.IsFinite(displayRefreshRate) || displayRefreshRate <= 0)
        {
            return 0;
        }

        var softwareRateLimit = Math.Min(configuredRate, MaximumSoftwarePresentationRate);
        var refreshRateTolerance = GetRefreshRateTolerance(softwareRateLimit);
        if (displayRefreshRate < softwareRateLimit - refreshRateTolerance)
        {
            return 0;
        }

        var nearestDivisor = Math.Max(1, (int)Math.Round(displayRefreshRate / softwareRateLimit));
        if (Math.Abs((displayRefreshRate / nearestDivisor) - softwareRateLimit)
            <= refreshRateTolerance)
        {
            return nearestDivisor;
        }

        // Preserve the configured cap when the display cannot present at an exact
        // integer refresh interval. The software pacer handles those fractional rates.
        return Math.Max(1, (int)Math.Floor(displayRefreshRate / softwareRateLimit));
    }

    private static double GetPresentationFrameRate(int configuredRate, double displayRefreshRate, int refreshDivisor)
    {
        var softwareRateLimit = Math.Min(configuredRate, MaximumSoftwarePresentationRate);
        if (!double.IsFinite(displayRefreshRate) || displayRefreshRate <= 0 || refreshDivisor <= 0)
        {
            return softwareRateLimit;
        }

        var minimumStableRate = Math.Min(softwareRateLimit, Math.Min(60.0, displayRefreshRate));
        return Math.Max(minimumStableRate, Math.Min(softwareRateLimit, displayRefreshRate / refreshDivisor));
    }

    private static bool TrySetPresentationVSync(
        IntPtr renderer,
        double displayRefreshRate,
        int refreshDivisor,
        double presentationFrameRate)
    {
        if (!double.IsFinite(displayRefreshRate)
            || displayRefreshRate <= 0
            || refreshDivisor <= 0
            || !double.IsFinite(presentationFrameRate))
        {
            SDL.SetRenderVSync(renderer, 0);
            return false;
        }

        // At high refresh rates, a missed VSync deadline can hold presentation to
        // the next scanout and cut the effective rate in half. Use the precise
        // software pacer above 90 FPS so a single missed scanout does not become a cap.
        if (presentationFrameRate > MaximumVSyncPresentationRate)
        {
            SDL.SetRenderVSync(renderer, 0);
            return false;
        }

        var synchronizedFrameRate = displayRefreshRate / refreshDivisor;
        if (Math.Abs(synchronizedFrameRate - presentationFrameRate)
            > GetRefreshRateTolerance(presentationFrameRate))
        {
            SDL.SetRenderVSync(renderer, 0);
            return false;
        }

        if (SDL.SetRenderVSync(renderer, refreshDivisor))
        {
            return true;
        }

        SDL.SetRenderVSync(renderer, 0);
        return false;
    }

    private static double GetRefreshRateTolerance(double frameRate)
    {
        return Math.Max(0.25, frameRate * 0.005);
    }

    private static void DrawLaunchScreen(
        IntPtr renderer,
        UiTextRenderer? textRenderer,
        SDL.FRect gameArea,
        SDL.FRect launchButton,
        string statusMessage)
    {
        SDL.SetRenderDrawColor(renderer, 12, 19, 31, 255);
        SDL.RenderFillRect(renderer, in gameArea);

        var card = GetLaunchCard(gameArea);
        SDL.SetRenderDrawColor(renderer, 18, 29, 45, 255);
        SDL.RenderFillRect(renderer, in card);
        SDL.SetRenderDrawColor(renderer, 50, 69, 91, 255);
        SDL.RenderRect(renderer, in card);

        var accentBar = new SDL.FRect
        {
            X = card.X + 1,
            Y = card.Y + 1,
            W = card.W - 2,
            H = 4,
        };
        SDL.SetRenderDrawColor(renderer, 232, 176, 77, 255);
        SDL.RenderFillRect(renderer, in accentBar);

        var centerX = card.X + (card.W / 2);
        DrawCenteredText(renderer, textRenderer, "SMW2PC", centerX, card.Y + 42, 15,
            MakeUiColor(133, 207, 158), bold: true);
        DrawCenteredText(renderer, textRenderer, "SUPER MARIO WORLD", centerX, card.Y + 78, 32,
            MakeUiColor(248, 250, 255), bold: true);
        DrawCenteredText(renderer, textRenderer, "A native runner for your Super Nintendo game", centerX,
            card.Y + 132, 16, MakeUiColor(181, 194, 211));

        SDL.SetRenderDrawColor(renderer, 48, 123, 97, 255);
        SDL.RenderFillRect(renderer, in launchButton);
        SDL.SetRenderDrawColor(renderer, 111, 194, 153, 255);
        SDL.RenderRect(renderer, in launchButton);
        DrawCenteredText(renderer, textRenderer, "CHOOSE ROM FILE", centerX, launchButton.Y + 17, 16,
            MakeUiColor(255, 255, 255), bold: true);
        DrawCenteredText(renderer, textRenderer, "Supports .sfc, .smc, and ZIP files", centerX,
            card.Y + 288, 13, MakeUiColor(151, 166, 186));

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            DrawCenteredText(renderer, textRenderer,
                Truncate(statusMessage, Math.Max(12, (int)(card.W / 8))), centerX,
                card.Y + 322, 12, MakeUiColor(242, 190, 128));
        }
    }

    private static void DrawCenteredText(
        IntPtr renderer,
        UiTextRenderer? textRenderer,
        string text,
        float centerX,
        float y,
        float fontSize,
        SDL.Color color,
        bool bold = false)
    {
        var width = textRenderer?.Measure(text, fontSize, bold).Width ?? (text.Length * 8);
        DrawText(renderer, textRenderer, text, centerX - (width / 2), y, fontSize, color, bold);
    }

    private static void DrawText(
        IntPtr renderer,
        UiTextRenderer? textRenderer,
        string text,
        float x,
        float y,
        float fontSize,
        SDL.Color color,
        bool bold = false)
    {
        if (textRenderer?.Draw(text, x, y, fontSize, color, bold) == true)
        {
            return;
        }

        SDL.SetRenderDrawColor(renderer, color.R, color.G, color.B, color.A);
        SDL.RenderDebugText(renderer, x, y, text);
    }

    private static SDL.Color MakeUiColor(byte red, byte green, byte blue, byte alpha = 255) => new()
    {
        R = red,
        G = green,
        B = blue,
        A = alpha,
    };

    private static SDL.FRect GetGameArea(int outputWidth, int outputHeight, GameAspectRatio aspectRatio)
    {
        var availableWidth = Math.Max(1, outputWidth);
        var availableHeight = Math.Max(1, outputHeight);
        if (aspectRatio == GameAspectRatio.OriginalPixels)
        {
            var integerScale = Math.Min(
                availableWidth / SnesVideoRenderer.Width,
                availableHeight / SnesVideoRenderer.Height);
            if (integerScale > 0)
            {
                var integerWidth = SnesVideoRenderer.Width * integerScale;
                var integerHeight = SnesVideoRenderer.Height * integerScale;
                return new SDL.FRect
                {
                    X = (availableWidth - integerWidth) / 2f,
                    Y = (availableHeight - integerHeight) / 2f,
                    W = integerWidth,
                    H = integerHeight,
                };
            }
        }

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

    private static SDL.FRect GetSettingsButton(int outputWidth) => new()
    {
        X = outputWidth - 60,
        Y = 16,
        W = 44,
        H = 44,
    };

    private static SDL.FRect GetLaunchButton(SDL.FRect gameArea) => new()
    {
        X = GetLaunchCard(gameArea).X + ((GetLaunchCard(gameArea).W - 240) / 2),
        Y = GetLaunchCard(gameArea).Y + 210,
        W = 240,
        H = 54,
    };

    private static SDL.FRect GetLaunchCard(SDL.FRect gameArea)
    {
        var width = Math.Min(620, gameArea.W - 32);
        var height = Math.Min(360, gameArea.H - 32);
        return new SDL.FRect
        {
            X = gameArea.X + ((gameArea.W - width) / 2),
            Y = gameArea.Y + ((gameArea.H - height) / 2),
            W = width,
            H = height,
        };
    }

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

        return false;
    }

    private static void DrawSettingsPanel(
        IntPtr renderer,
        UiTextRenderer? textRenderer,
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
        DrawText(renderer, textRenderer, "SETTINGS", panel.X + 16, panel.Y + 18, 16,
            MakeUiColor(248, 250, 255), bold: true);
        DrawSettingsButton(renderer, textRenderer, GetPanelOpenRomButton(panel), "OPEN ROM", 11);
        DrawSettingsButton(renderer, textRenderer, GetPanelCloseButton(panel), "X", 14);
        DrawSettingsTab(renderer, textRenderer, GetDisplayTabButton(panel), "DISPLAY", settingsTab == SettingsTab.Display);
        DrawSettingsTab(renderer, textRenderer, GetControlsTabButton(panel), "CONTROLS", settingsTab == SettingsTab.Controls);

        if (settingsTab == SettingsTab.Display)
        {
            DrawSettingsRow(renderer, textRenderer, GetResolutionRow(panel), $"Window size: {settings.WindowWidth} x {settings.WindowHeight}");
            DrawSettingsRow(renderer, textRenderer, GetAspectRatioRow(panel), $"Aspect ratio: {GetAspectRatioName(settings.AspectRatio)}");
            DrawText(renderer, textRenderer, "Pixel scaling: Sharp (nearest-neighbor)",
                panel.X + 20, panel.Y + 194, 13, MakeUiColor(181, 194, 211));

            DrawText(renderer, textRenderer, $"Presentation cap: {settings.RenderFrameRate} FPS",
                panel.X + 20, panel.Y + 242, 14, MakeUiColor(248, 250, 255));
            DrawSettingsButton(renderer, textRenderer, GetFrameRateDecreaseButton(panel), "-", 16);
            DrawSettingsButton(renderer, textRenderer, GetFrameRateIncreaseButton(panel), "+", 16);

            var gameTimingText = gameFrameRate is null
                ? "Game timing follows the ROM region (50 or 60 Hz)."
                : $"Game timing stays fixed at {gameFrameRate:0.##} Hz.";
            DrawText(renderer, textRenderer, gameTimingText, panel.X + 20, panel.Y + 280, 12,
                MakeUiColor(181, 194, 211));
            DrawText(renderer, textRenderer, "Display settings do not change physics.",
                panel.X + 20, panel.Y + 300, 12, MakeUiColor(181, 194, 211));

            var systemStatus = !string.IsNullOrWhiteSpace(audioError)
                ? audioError
                : !verticalSyncEnabled
                    ? "Vertical sync is off; software frame pacing is active."
                    : statusMessage.StartsWith("Execution stopped", StringComparison.Ordinal)
                        || statusMessage.StartsWith("Could not", StringComparison.Ordinal)
                            ? statusMessage
                            : string.Empty;
            if (!string.IsNullOrWhiteSpace(systemStatus))
            {
                DrawText(renderer, textRenderer,
                    Truncate(systemStatus, Math.Max(16, (int)((panel.W - 40) / 8))),
                    panel.X + 20, panel.Y + panel.H - 32, 12, MakeUiColor(242, 190, 128));
            }
        }
        else
        {
            var controlsMessage = rebindingAction.HasValue
                || statusMessage.Contains("already assigned", StringComparison.Ordinal)
                || statusMessage.Contains("reserved", StringComparison.Ordinal)
                    ? statusMessage
                    : "Choose an action, then press a key.";
            DrawText(renderer, textRenderer,
                Truncate(controlsMessage, Math.Max(16, (int)((panel.W - 40) / 8))),
                panel.X + 20, panel.Y + 91, 12, MakeUiColor(181, 194, 211));
            for (var index = 0; index < BindingRows.Length; index++)
            {
                var row = BindingRows[index];
                DrawBindingRow(renderer, textRenderer, GetBindingRow(panel, index), row,
                    GetBoundKey(settings, row.Action), rebindingAction == row.Action);
            }

            DrawText(renderer, textRenderer, "F1 opens ROMs  |  F2 toggles settings  |  Esc cancels",
                panel.X + 20, panel.Y + panel.H - 24, 11, MakeUiColor(177, 189, 205));
        }
    }

    private static void DrawSettingsRow(IntPtr renderer, UiTextRenderer? textRenderer, SDL.FRect row, string text)
    {
        SDL.SetRenderDrawColor(renderer, 28, 36, 50, 255);
        SDL.RenderFillRect(renderer, in row);
        DrawText(renderer, textRenderer, text, row.X + 10, row.Y + 9, 13, MakeUiColor(248, 250, 255));
        DrawText(renderer, textRenderer, ">", row.X + row.W - 20, row.Y + 8, 16, MakeUiColor(151, 166, 186));
    }

    private static void DrawSettingsButton(
        IntPtr renderer,
        UiTextRenderer? textRenderer,
        SDL.FRect button,
        string text,
        float fontSize = 13)
    {
        SDL.SetRenderDrawColor(renderer, 52, 86, 132, 255);
        SDL.RenderFillRect(renderer, in button);
        var textHeight = textRenderer?.Measure(text, fontSize, bold: true).Height ?? 8;
        DrawCenteredText(renderer, textRenderer, text, button.X + (button.W / 2),
            button.Y + ((button.H - textHeight) / 2), fontSize, MakeUiColor(255, 255, 255), bold: true);
    }

    private static void DrawSettingsTab(
        IntPtr renderer,
        UiTextRenderer? textRenderer,
        SDL.FRect tab,
        string text,
        bool selected)
    {
        SDL.SetRenderDrawColor(renderer, selected ? (byte)56 : (byte)27,
            selected ? (byte)91 : (byte)37, selected ? (byte)142 : (byte)51, 255);
        SDL.RenderFillRect(renderer, in tab);
        DrawCenteredText(renderer, textRenderer, text, tab.X + (tab.W / 2), tab.Y + 7, 12,
            MakeUiColor(255, 255, 255), bold: true);
    }

    private static void DrawBindingRow(
        IntPtr renderer,
        UiTextRenderer? textRenderer,
        SDL.FRect row,
        BindingRow binding,
        SDL.Keycode key,
        bool isListening)
    {
        SDL.SetRenderDrawColor(renderer, isListening ? (byte)58 : (byte)28,
            isListening ? (byte)67 : (byte)36, isListening ? (byte)87 : (byte)50, 255);
        SDL.RenderFillRect(renderer, in row);
        var label = string.IsNullOrEmpty(binding.Hint) ? binding.Label : $"{binding.Label}  {binding.Hint}";
        DrawText(renderer, textRenderer, label, row.X + 8, row.Y + 4, 12, MakeUiColor(238, 243, 250));

        var keyButton = new SDL.FRect
        {
            X = row.X + row.W - 106,
            Y = row.Y + 2,
            W = 98,
            H = row.H - 4,
        };
        DrawSettingsButton(renderer, textRenderer, keyButton, isListening ? "PRESS KEY" : GetKeyName(key), 11);
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

        var hubOutline = new SDL.FRect { X = centerX - 5, Y = centerY - 5, W = 10, H = 10 };
        SDL.RenderRect(renderer, in hubOutline);
        SDL.SetRenderDrawColor(renderer, selected ? (byte)56 : (byte)15,
            selected ? (byte)91 : (byte)22, selected ? (byte)142 : (byte)35, 255);
        var hubHole = new SDL.FRect { X = centerX - 2, Y = centerY - 2, W = 4, H = 4 };
        SDL.RenderFillRect(renderer, in hubHole);
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

    private static SDL.FRect GetBindingRow(SDL.FRect panel, int index) => new()
    {
        X = panel.X + 12,
        Y = panel.Y + 104 + (index * 24),
        W = panel.W - 24,
        H = 23,
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

    private static void RecordExecutionFailure(Exception exception, NativeCpuRunner runner)
    {
        try
        {
            var state = runner.State;
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMW2PC");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "execution-errors.log");
            File.AppendAllText(logPath,
                $"[{DateTimeOffset.Now:O}] ROM execution stopped\n"
                + $"CPU={state.ProgramBank:X2}:{state.ProgramCounter:X4} A={state.Accumulator:X4} "
                + $"X={state.X:X4} Y={state.Y:X4} S={state.StackPointer:X4} D={state.DirectPage:X4} "
                + $"DB={state.DataBank:X2} P={state.Status:X2} E={state.EmulationMode}\n"
                + $"GameMode=${runner.MemoryBus.ReadByte(0x7E0100):X2} "
                + $"Level=${runner.MemoryBus.ReadByte(0x7E13BF):X2}\n"
                + exception + Environment.NewLine + Environment.NewLine);
        }
        catch (IOException)
        {
            // Keep the on-screen execution error available if local logging fails.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the on-screen execution error available if local logging fails.
        }
    }

    private sealed record FileDialogResult(string? Path, string? Error);
}
