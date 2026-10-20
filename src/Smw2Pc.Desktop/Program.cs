using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SDL3;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Recompilation;
using Smw2Pc.Core.Rom;
using Smw2Pc.Core.Storage;

internal static partial class Program
{
    private const string CompiledRomResourceName = "Smw2Pc.Desktop.CompiledRom";
    private const int InstructionsPerFrame = 18000;
    private const int MaximumCatchUpFrames = 5;
    private const int AudioSampleRateHz = 32_000;
    private const int AudioBytesPerSampleFrame = sizeof(short) * 2;
    private const int AudioStartupBufferSampleFrames = AudioSampleRateHz / 100;
    private const double AudioTargetQueueSeconds = 0.020;
    private const double AudioQueueSmoothingSeconds = 0.250;
    private const double MaximumAudioClockAdjustment = 0.005;
    private const int MaximumSoftwarePresentationRate = DesktopSettings.MaximumRenderFrameRate;
    private const int MaximumVSyncPresentationRate = 90;
    private const int PresentationIntervalSampleCount = 180;
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
        Player,
    }

    private enum BindingMode
    {
        Keyboard,
        Gamepad,
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

    private readonly record struct FrameTimingDisplay(string Output, string Work, string Wait, string Peak);

    private struct PresentationTimingSample
    {
        public long IntervalTicks;
        public long EmulationTicks;
        public long GameRenderTicks;
        public long DrawTicks;
        public long PresentTicks;
        public long WaitTicks;
    }

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

    private const short GamepadStickDeadZone = 16_000;

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

        var gamepadSubsystemInitialized = SDL.InitSubSystem(SDL.InitFlags.Gamepad);
        if (gamepadSubsystemInitialized)
            SDL.SetGamepadEventsEnabled(true);

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

        uint activeGamepadId = 0;
        var gamepadName = string.Empty;
        var gamepad = gamepadSubsystemInitialized
            ? OpenFirstGamepad(out activeGamepadId, out gamepadName)
            : IntPtr.Zero;

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
        var audioStreamPaused = false;
        var smoothedAudioQueueSeconds = AudioTargetQueueSeconds;
        if (SDL.InitSubSystem(SDL.InitFlags.Audio))
        {
            var audioSpec = new SDL.AudioSpec
            {
                Format = SDL.AudioFormat.AudioS16LE,
                Channels = 2,
                Freq = AudioSampleRateHz,
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
            else
            {
                // OpenAudioDeviceStream starts paused. Keep it that way until the
                // first game audio block has been queued, avoiding startup underruns.
                audioStreamPaused = true;
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
        SnesControllerButtons keyboardButtons = SnesControllerButtons.None;
        SnesControllerButtons gamepadButtons = SnesControllerButtons.None;
        SnesControllerButtons controllerButtons = SnesControllerButtons.None;
        var renderWidth = SnesVideoRenderer.Width;
        var frameBuffer = new byte[SnesVideoRenderer.Width * SnesVideoRenderer.Height * SnesVideoRenderer.BytesPerPixel];
        var previousTimestamp = Stopwatch.GetTimestamp();
        var frameAccumulator = 0.0;
        var nextPresentationTimestamp = previousTimestamp;
        var settingsOpen = false;
        var settingsTab = SettingsTab.Display;
        var bindingMode = BindingMode.Keyboard;
        GameAction? rebindingAction = null;
        var settingsSavePending = false;
        var settingsSaveTimestamp = 0L;
        var smoothedRenderToPresentTicks = 0L;
        var presentationSamples = new PresentationTimingSample[PresentationIntervalSampleCount];
        var presentationIntervalCount = 0;
        var nextPresentationIntervalIndex = 0;
        var lastPresentedTimestamp = 0L;
        var measuredPresentedFrameRate = 0.0;
        var longestPresentationIntervalMilliseconds = 0.0;
        var meanEmulationMilliseconds = 0.0;
        var meanGameRenderMilliseconds = 0.0;
        var meanDrawMilliseconds = 0.0;
        var meanPresentMilliseconds = 0.0;
        var meanPacerWaitMilliseconds = 0.0;
        var meanOtherMilliseconds = 0.0;
        var peakEmulationMilliseconds = 0.0;
        var peakGameRenderMilliseconds = 0.0;
        var peakPresentMilliseconds = 0.0;
        var nextTimingDisplayUpdate = 0L;
        var timingDisplay = new FrameTimingDisplay("Output: collecting frame timing...", "", "", "");
        var emulationTicksSinceLastPresentation = 0L;
        var gameRenderTicksSinceLastPresentation = 0L;
        var drawTicksSinceLastPresentation = 0L;
        var presentTicksSinceLastPresentation = 0L;
        var pacerWaitTicksSinceLastPresentation = 0L;

        GameSaveStore? saveStore = null;
        var pendingPlayerCommand = PlayerCommand.None;
        var manuallyPaused = false;
        var windowFocused = true;
        var wasPaused = false;
        var appliedFullscreen = false;
        var appliedGain = -1f;
        var nextBatterySaveTimestamp = 0L;
        Task<bool>? batterySaveTask = null;
        var settingsSaveTask = Task.CompletedTask;

        void RenderGameFrame(float progress)
        {
            var wide = settings.AspectRatio == GameAspectRatio.Widescreen;
            var viewport = wide ? VideoViewport.Widescreen : VideoViewport.Original;
            cpuRunner!.MemoryBus.Widescreen.Enabled = wide;
            if (renderWidth != viewport.Width)
            {
                var replacement = SDL.CreateTexture(renderer, SDL.PixelFormat.ABGR8888,
                    SDL.TextureAccess.Streaming, viewport.Width, SnesVideoRenderer.Height);
                if (replacement == IntPtr.Zero)
                    throw new InvalidOperationException($"Could not create the game texture: {SDL.GetError()}");
                SDL.SetTextureScaleMode(replacement, SDL.ScaleMode.Nearest);
                SDL.DestroyTexture(gameTexture);
                gameTexture = replacement;
                renderWidth = viewport.Width;
                frameBuffer = new byte[renderWidth * SnesVideoRenderer.Height * SnesVideoRenderer.BytesPerPixel];
            }
            SnesVideoRenderer.RenderFrame(cpuRunner.MemoryBus.Hardware, frameBuffer, progress, viewport);
            if (!SDL.UpdateTexture(gameTexture, IntPtr.Zero, frameBuffer, renderWidth * SnesVideoRenderer.BytesPerPixel))
                statusMessage = $"Could not update the game frame: {SDL.GetError()}";
        }

        void CompleteBatteryWrite()
        {
            if (batterySaveTask is null) return;
            try { batterySaveTask.GetAwaiter().GetResult(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { statusMessage = $"Could not save progress: {exception.Message}"; }
            batterySaveTask = null;
        }

        void PersistBattery()
        {
            CompleteBatteryWrite();
            if (cpuRunner is null || saveStore is null) return;
            try { saveStore.SaveBattery(cpuRunner.MemoryBus); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { statusMessage = $"Could not save progress: {exception.Message}"; }
        }

        void ResetAudio()
        {
            if (audioStream == IntPtr.Zero) return;
            SDL.PauseAudioStreamDevice(audioStream);
            SDL.ClearAudioStream(audioStream);
            SDL.SetAudioStreamFrequencyRatio(audioStream, 1f);
            audioStreamPaused = true;
            smoothedAudioQueueSeconds = AudioTargetQueueSeconds;
        }

        void StartGame(SnesRom rom)
        {
            // Validate the replacement before releasing the running game.
            var bus = new SnesMemoryBus(rom);
            var runner = new NativeCpuRunner(bus);
            var store = new GameSaveStore(rom);
            var warning = string.Empty;
            try { store.LoadBattery(bus); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            { warning = $" | Could not load progress: {exception.Message}"; }
            PersistBattery();
            settingsSaveTask.GetAwaiter().GetResult();
            settings.Save();
            settings = DesktopSettings.Load(rom.Sha256);
            bus.Widescreen.Enabled = settings.AspectRatio == GameAspectRatio.Widescreen;
            loadedRom = rom;
            cpuRunner = runner;
            saveStore = store;
            keyboardButtons = gamepadButtons = controllerButtons = SnesControllerButtons.None;
            manuallyPaused = false;
            wasPaused = false;
            rebindingAction = null;
            appliedGain = -1;
            if (!settings.Fullscreen) SDL.SetWindowSize(window, settings.WindowWidth, settings.WindowHeight);
            SDL.SetWindowTitle(window, $"{rom.Header.Title} - SMW2PC");
            ResetAudio();
            frameAccumulator = 1.0 / rom.Header.FrameRateHz;
            smoothedRenderToPresentTicks = 0;
            previousTimestamp = Stopwatch.GetTimestamp();
            nextPresentationTimestamp = previousTimestamp;
            nextBatterySaveTimestamp = previousTimestamp + Stopwatch.Frequency * 2;
            statusMessage = $"Running {rom.Header.Title} | {rom.Header.FrameRateHz:0.##} Hz" + warning;
            try { RecentRoms.Add(rom.SourcePath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        using (var compiledRomStream = typeof(Program).Assembly.GetManifestResourceStream(CompiledRomResourceName))
        {
            if (compiledRomStream is not null)
            {
                try
                {
                    using var romBuffer = new MemoryStream();
                    compiledRomStream.CopyTo(romBuffer);
                    StartGame(SnesRom.LoadBytes(romBuffer.ToArray(), "compiled.sfc"));
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
                else if (eventType is SDL.EventType.WindowFocusLost or SDL.EventType.WindowFocusGained)
                {
                    windowFocused = eventType == SDL.EventType.WindowFocusGained;
                    keyboardButtons = gamepadButtons = controllerButtons = SnesControllerButtons.None;
                    cpuRunner?.MemoryBus.Hardware.SetControllerButtons(0, controllerButtons);
                }
                else if (eventType == SDL.EventType.WindowResized)
                {
                    SDL.GetWindowSize(window, out var resizedWidth, out var resizedHeight);
                    if (!appliedFullscreen)
                    {
                        settings.WindowWidth = resizedWidth;
                        settings.WindowHeight = resizedHeight;
                    }
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
                    else if (settingsOpen && settingsTab == SettingsTab.Player
                        && HandlePlayerClick(mouseX, mouseY, settingsPanel, settings, out pendingPlayerCommand))
                    {
                        settingsSavePending = true;
                        settingsSaveTimestamp = Stopwatch.GetTimestamp();
                    }
                    else if (settingsOpen && HandleSettingsClick(
                        mouseX,
                        mouseY,
                        settingsPanel,
                        ref settingsTab,
                        ref bindingMode,
                        settings,
                        window,
                        gamepad != IntPtr.Zero,
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
                    if (isDown && @event.Key.Repeat) continue;
                    if (isDown && rebindingAction.HasValue && key == SDL.Keycode.Escape)
                    {
                        rebindingAction = null;
                        statusMessage = "Binding cancelled.";
                    }
                    else if (isDown && rebindingAction.HasValue && bindingMode == BindingMode.Keyboard)
                    {
                        if (DesktopSettings.IsReservedKey(key))
                        {
                            statusMessage = "This key is reserved for an app shortcut.";
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
                            keyboardButtons = SnesControllerButtons.None;
                            controllerButtons = keyboardButtons | gamepadButtons;
                            cpuRunner?.MemoryBus.Hardware.SetControllerButtons(0, controllerButtons);
                            statusMessage = $"{GetActionName(action)} is now {GetKeyName(key)}.";
                            settingsSavePending = true;
                            settingsSaveTimestamp = Stopwatch.GetTimestamp();
                        }
                    }
                    else if (isDown && key is SDL.Keycode.F5 or SDL.Keycode.F8 or SDL.Keycode.F9)
                    {
                        pendingPlayerCommand = key == SDL.Keycode.F5 ? PlayerCommand.SaveState
                            : key == SDL.Keycode.F8 ? PlayerCommand.LoadState : PlayerCommand.TogglePause;
                    }
                    else if (isDown && key == SDL.Keycode.F10)
                    {
                        settings.Muted = !settings.Muted;
                        settingsSavePending = true;
                        settingsSaveTimestamp = Stopwatch.GetTimestamp();
                    }
                    else if (isDown && key == SDL.Keycode.F11)
                    {
                        settings.Fullscreen = !settings.Fullscreen;
                        settingsSavePending = true;
                        settingsSaveTimestamp = Stopwatch.GetTimestamp();
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
                        rebindingAction = null;
                    }
                    else
                    {
                        keyboardButtons = SetButtonForKey(keyboardButtons, key, isDown, settings);
                        controllerButtons = keyboardButtons | gamepadButtons;
                        cpuRunner?.MemoryBus.Hardware.SetControllerButtons(0, controllerButtons);
                    }
                }
                else if (eventType == SDL.EventType.GamepadAdded && gamepad == IntPtr.Zero)
                {
                    gamepad = OpenGamepad(@event.GDevice.Which, out gamepadName);
                    if (gamepad != IntPtr.Zero)
                    {
                        activeGamepadId = @event.GDevice.Which;
                        statusMessage = $"Controller connected: {gamepadName}.";
                    }
                }
                else if (eventType == SDL.EventType.GamepadRemoved && @event.GDevice.Which == activeGamepadId)
                {
                    if (gamepad != IntPtr.Zero)
                        SDL.CloseGamepad(gamepad);
                    gamepad = gamepadSubsystemInitialized
                        ? OpenFirstGamepad(out activeGamepadId, out gamepadName)
                        : IntPtr.Zero;
                    gamepadButtons = SnesControllerButtons.None;
                    controllerButtons = keyboardButtons;
                    cpuRunner?.MemoryBus.Hardware.SetControllerButtons(0, controllerButtons);
                    statusMessage = gamepad != IntPtr.Zero
                        ? $"Controller connected: {gamepadName}."
                        : "Controller disconnected.";
                }
                else if (eventType == SDL.EventType.GamepadButtonDown
                    && @event.GButton.Which == activeGamepadId
                    && rebindingAction.HasValue
                    && bindingMode == BindingMode.Gamepad)
                {
                    var button = (SDL.GamepadButton)@event.GButton.Button;
                    if (TryGetActionForGamepadButton(settings, button, out var existingAction)
                        && existingAction != rebindingAction.Value)
                    {
                        statusMessage = $"{GetGamepadButtonName(gamepad, button)} is already assigned to {GetActionName(existingAction)}.";
                    }
                    else
                    {
                        var action = rebindingAction.Value;
                        SetGamepadButtonForAction(settings, action, button);
                        rebindingAction = null;
                        statusMessage = $"{GetActionName(action)} is now {GetGamepadButtonName(gamepad, button)}.";
                        settingsSavePending = true;
                        settingsSaveTimestamp = Stopwatch.GetTimestamp();
                    }
                }
            }

            gamepadButtons = windowFocused && !manuallyPaused
                ? GetGamepadControllerButtons(gamepad, settings) : SnesControllerButtons.None;
            controllerButtons = keyboardButtons | gamepadButtons;
            cpuRunner?.MemoryBus.Hardware.SetControllerButtons(0, controllerButtons);

            while (PendingFileDialogResults.TryDequeue(out var dialogResult))
            {
                if (dialogResult.Error is not null)
                {
                    statusMessage = dialogResult.Error;
                    continue;
                }

                if (dialogResult.Path is null)
                {
                    continue;
                }

                try
                {
                    StartGame(SnesRom.Load(dialogResult.Path));
                    elapsedSeconds = 0;
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException
                        or InvalidDataException)
                {
                    statusMessage = exception.Message;
                }
            }

            if (pendingPlayerCommand != PlayerCommand.None)
            {
                try
                {
                    if (pendingPlayerCommand == PlayerCommand.TogglePause)
                    {
                        manuallyPaused = !manuallyPaused;
                        keyboardButtons = SnesControllerButtons.None;
                    }
                    else if (loadedRom is null || saveStore is null)
                        statusMessage = "Open a game first.";
                    else if (pendingPlayerCommand == PlayerCommand.ExportReport)
                        statusMessage = $"Report saved: {PlayerCompatibilityReport.Export(loadedRom, cpuRunner)}";
                    else if (pendingPlayerCommand == PlayerCommand.OpenSaves)
                    {
                        Directory.CreateDirectory(saveStore.DirectoryPath);
                        Process.Start(new ProcessStartInfo(saveStore.DirectoryPath) { UseShellExecute = true });
                    }
                    else if (pendingPlayerCommand == PlayerCommand.LoadState)
                    {
                        CompleteBatteryWrite();
                        cpuRunner = saveStore.LoadState();
                        keyboardButtons = gamepadButtons = controllerButtons = SnesControllerButtons.None;
                        cpuRunner.MemoryBus.Hardware.SetControllerButtons(0, controllerButtons);
                        frameAccumulator = 0;
                        elapsedSeconds = 0;
                        previousTimestamp = Stopwatch.GetTimestamp();
                        ResetAudio();
                        RenderGameFrame(1f);
                        statusMessage = "Quick save loaded.";
                    }
                    else if (cpuRunner is not null)
                    {
                        saveStore.SaveState(cpuRunner);
                        statusMessage = "Quick save written.";
                    }
                    else statusMessage = "The game has stopped. Load a quick save to resume.";
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or InvalidDataException or ArgumentException or System.ComponentModel.Win32Exception)
                { statusMessage = $"Player action failed: {exception.Message}"; }
                pendingPlayerCommand = PlayerCommand.None;
            }

            if (settings.Fullscreen != appliedFullscreen)
            {
                if (SDL.SetWindowFullscreen(window, settings.Fullscreen)) appliedFullscreen = settings.Fullscreen;
                else { settings.Fullscreen = appliedFullscreen; statusMessage = SDL.GetError(); }
            }
            var gain = settings.Muted ? 0f : settings.VolumePercent / 100f;
            if (audioStream != IntPtr.Zero && gain != appliedGain)
            {
                if (SDL.SetAudioStreamGain(audioStream, gain)) appliedGain = gain;
                else statusMessage = $"Could not set volume: {SDL.GetError()}";
            }
            var gamePaused = manuallyPaused || (settings.PauseWhenUnfocused && !windowFocused);
            if (gamePaused != wasPaused)
            {
                frameAccumulator = 0;
                elapsedSeconds = 0;
                smoothedRenderToPresentTicks = 0;
                if (gamePaused && audioStream != IntPtr.Zero)
                {
                    SDL.PauseAudioStreamDevice(audioStream);
                    audioStreamPaused = true;
                }
                wasPaused = gamePaused;
            }
            if (batterySaveTask?.IsCompleted == true) CompleteBatteryWrite();
            if (batterySaveTask is null && Stopwatch.GetTimestamp() >= nextBatterySaveTimestamp)
            {
                if (cpuRunner is not null && saveStore is not null)
                    batterySaveTask = saveStore.SaveBatteryAsync(cpuRunner.MemoryBus);
                nextBatterySaveTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
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

            // Software pacing prepares the next frame immediately, then waits at
            // presentation. Starting the rasterizer at the deadline adds its varying
            // cost to every frame interval and needlessly loses slots on small overruns.
            var renderRequested = !verticalSyncEnabled || presentationTimestamp >= nextPresentationTimestamp;
            var presentedFrameRendererStart = 0L;

            if (cpuRunner is not null && !gamePaused)
            {
                var emulationWorkStartTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    cpuRunner.MemoryBus.Widescreen.Enabled = settings.AspectRatio == GameAspectRatio.Widescreen;
                    var frameDuration = 1.0 / loadedRom!.Header.FrameRateHz;
                    if (audioStream != IntPtr.Zero)
                    {
                        var queuedBytes = SDL.GetAudioStreamQueued(audioStream);
                        if (queuedBytes < 0)
                        {
                            audioError = $"Audio output stopped: {SDL.GetError()}";
                            statusMessage = audioError;
                            SDL.DestroyAudioStream(audioStream);
                            audioStream = IntPtr.Zero;
                            audioStreamPaused = false;
                            frameAccumulator = frameDuration;
                        }
                        else
                        {
                            // Keep gameplay on the ROM's fixed clock. Let audio follow
                            // it with a small correction for drift between device clocks.
                            var playbackRate = GetAudioPlaybackRate(
                                queuedBytes,
                                elapsedSeconds,
                                ref smoothedAudioQueueSeconds);
                            if (!SDL.SetAudioStreamFrequencyRatio(audioStream, (float)playbackRate))
                            {
                                audioError = $"Audio output stopped: {SDL.GetError()}";
                                statusMessage = audioError;
                                SDL.DestroyAudioStream(audioStream);
                                audioStream = IntPtr.Zero;
                                audioStreamPaused = false;
                                frameAccumulator = frameDuration;
                            }

                            if (audioStreamPaused
                                && audioStream != IntPtr.Zero
                                && queuedBytes >= AudioStartupBufferSampleFrames * AudioBytesPerSampleFrame)
                            {
                                if (SDL.ResumeAudioStreamDevice(audioStream))
                                {
                                    audioStreamPaused = false;
                                }
                                else
                                {
                                    audioError = $"Audio output stopped: {SDL.GetError()}";
                                    statusMessage = audioError;
                                    SDL.DestroyAudioStream(audioStream);
                                    audioStream = IntPtr.Zero;
                                    audioStreamPaused = false;
                                    frameAccumulator = frameDuration;
                                }
                            }
                        }
                    }

                    frameAccumulator += elapsedSeconds;
                    if (frameAccumulator > frameDuration * MaximumCatchUpFrames)
                    {
                        // After a long stall, resume from the current audio/video point
                        // instead of queuing a burst of stale gameplay sound.
                        frameAccumulator = frameDuration;
                    }

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
                            audioStreamPaused = false;
                            frameAccumulator = frameDuration;
                        }
                    }

                    if (audioStreamPaused
                        && audioStream != IntPtr.Zero
                        && SDL.GetAudioStreamQueued(audioStream) >= AudioStartupBufferSampleFrames * AudioBytesPerSampleFrame)
                    {
                        if (SDL.ResumeAudioStreamDevice(audioStream))
                        {
                            audioStreamPaused = false;
                        }
                        else
                        {
                            audioError = $"Audio output stopped: {SDL.GetError()}";
                            statusMessage = audioError;
                            SDL.DestroyAudioStream(audioStream);
                            audioStream = IntPtr.Zero;
                            audioStreamPaused = false;
                            frameAccumulator = frameDuration;
                        }
                    }

                    emulationTicksSinceLastPresentation += Stopwatch.GetTimestamp() - emulationWorkStartTimestamp;
                    emulationWorkStartTimestamp = 0;

                    if (renderRequested)
                    {
                        var renderTimestamp = Stopwatch.GetTimestamp();
                        var timeSinceLoopStart = (double)(renderTimestamp - currentTimestamp) / Stopwatch.Frequency;
                        var predictedRenderToPresentTicks = verticalSyncEnabled
                            ? smoothedRenderToPresentTicks
                            : Math.Max(smoothedRenderToPresentTicks, nextPresentationTimestamp - renderTimestamp);
                        var interpolationProgress = (float)Math.Clamp(
                            (frameAccumulator + timeSinceLoopStart + (predictedRenderToPresentTicks / (double)Stopwatch.Frequency)) / frameDuration,
                            0.0,
                            1.0);
                        presentedFrameRendererStart = Stopwatch.GetTimestamp();
                        RenderGameFrame(interpolationProgress);
                        gameRenderTicksSinceLastPresentation += Stopwatch.GetTimestamp() - presentedFrameRendererStart;
                    }
                }
                catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
                {
                    statusMessage = $"Execution stopped at {cpuRunner.State.ProgramBank:X2}:{cpuRunner.State.ProgramCounter:X4}: {exception.Message}";
                    if (exception is MissingCompiledCodeException)
                    {
                        RecordExecutionFailure(exception, cpuRunner);
                        try { PlayerCompatibilityReport.Export(loadedRom!, cpuRunner, exception.Message); }
                        catch (Exception reportError) when (reportError is IOException or UnauthorizedAccessException) { }
                    }
                    PersistBattery();
                    ResetAudio();
                    cpuRunner = null;
                    frameAccumulator = 0;
                }
                finally
                {
                    if (emulationWorkStartTimestamp != 0)
                    {
                        emulationTicksSinceLastPresentation += Stopwatch.GetTimestamp() - emulationWorkStartTimestamp;
                    }
                }
            }
            else
            {
                frameAccumulator = 0;
                if (cpuRunner is not null && renderRequested) RenderGameFrame(1f);
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
                var drawStartTimestamp = Stopwatch.GetTimestamp();
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

                if (gamePaused && cpuRunner is not null)
                    DrawCenteredText(renderer, textRenderer, "PAUSED - F9 to resume", gameArea.X + gameArea.W / 2,
                        gameArea.Y + gameArea.H / 2, 20, MakeUiColor(255, 255, 255), true);
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
                        verticalSyncEnabled,
                        displayRefreshRate,
                        timingDisplay,
                        bindingMode,
                        gamepad,
                        gamepadName);
                }

                drawTicksSinceLastPresentation += Stopwatch.GetTimestamp() - drawStartTimestamp;
                if (!verticalSyncEnabled)
                    pacerWaitTicksSinceLastPresentation += WaitUntil(nextPresentationTimestamp);
                var presentStartTimestamp = Stopwatch.GetTimestamp();
                SDL.RenderPresent(renderer);

                var presentedTimestamp = Stopwatch.GetTimestamp();
                presentTicksSinceLastPresentation += presentedTimestamp - presentStartTimestamp;
                if (lastPresentedTimestamp != 0)
                {
                    var sample = new PresentationTimingSample
                    {
                        IntervalTicks = presentedTimestamp - lastPresentedTimestamp,
                        EmulationTicks = emulationTicksSinceLastPresentation,
                        GameRenderTicks = gameRenderTicksSinceLastPresentation,
                        DrawTicks = drawTicksSinceLastPresentation,
                        PresentTicks = presentTicksSinceLastPresentation,
                        WaitTicks = pacerWaitTicksSinceLastPresentation,
                    };
                    presentationSamples[nextPresentationIntervalIndex] = sample;
                    nextPresentationIntervalIndex = (nextPresentationIntervalIndex + 1) % presentationSamples.Length;
                    presentationIntervalCount = Math.Min(presentationIntervalCount + 1, presentationSamples.Length);

                    long totalIntervalTicks = 0;
                    long longestIntervalTicks = 0;
                    long totalEmulationTicks = 0;
                    long totalGameRenderTicks = 0;
                    long totalDrawTicks = 0;
                    long totalPresentTicks = 0;
                    long totalWaitTicks = 0;
                    long peakEmulationTicks = 0;
                    long peakGameRenderTicks = 0;
                    long peakPresentTicks = 0;
                    foreach (var timingSample in presentationSamples)
                    {
                        if (timingSample.IntervalTicks == 0)
                        {
                            continue;
                        }

                        totalIntervalTicks += timingSample.IntervalTicks;
                        longestIntervalTicks = Math.Max(longestIntervalTicks, timingSample.IntervalTicks);
                        totalEmulationTicks += timingSample.EmulationTicks;
                        totalGameRenderTicks += timingSample.GameRenderTicks;
                        totalDrawTicks += timingSample.DrawTicks;
                        totalPresentTicks += timingSample.PresentTicks;
                        totalWaitTicks += timingSample.WaitTicks;
                        peakEmulationTicks = Math.Max(peakEmulationTicks, timingSample.EmulationTicks);
                        peakGameRenderTicks = Math.Max(peakGameRenderTicks, timingSample.GameRenderTicks);
                        peakPresentTicks = Math.Max(peakPresentTicks, timingSample.PresentTicks);
                    }

                    measuredPresentedFrameRate = totalIntervalTicks > 0
                        ? presentationIntervalCount * (double)Stopwatch.Frequency / totalIntervalTicks
                        : 0;
                    longestPresentationIntervalMilliseconds =
                        longestIntervalTicks * 1_000.0 / Stopwatch.Frequency;
                    var sampleCount = Math.Max(1, presentationIntervalCount);
                    meanEmulationMilliseconds = ToMilliseconds(totalEmulationTicks / sampleCount);
                    meanGameRenderMilliseconds = ToMilliseconds(totalGameRenderTicks / sampleCount);
                    meanDrawMilliseconds = ToMilliseconds(totalDrawTicks / sampleCount);
                    meanPresentMilliseconds = ToMilliseconds(totalPresentTicks / sampleCount);
                    meanPacerWaitMilliseconds = ToMilliseconds(totalWaitTicks / sampleCount);
                    var classifiedTicks = totalEmulationTicks + totalGameRenderTicks + totalDrawTicks + totalPresentTicks + totalWaitTicks;
                    meanOtherMilliseconds = ToMilliseconds(Math.Max(0, (totalIntervalTicks - classifiedTicks) / sampleCount));
                    peakEmulationMilliseconds = ToMilliseconds(peakEmulationTicks);
                    peakGameRenderMilliseconds = ToMilliseconds(peakGameRenderTicks);
                    peakPresentMilliseconds = ToMilliseconds(peakPresentTicks);
                    if (presentedTimestamp >= nextTimingDisplayUpdate)
                    {
                        timingDisplay = new FrameTimingDisplay(
                            $"Output: {measuredPresentedFrameRate:0.0} FPS Â· longest gap: {longestPresentationIntervalMilliseconds:0.0} ms",
                            $"Avg ms: CPU {meanEmulationMilliseconds:0.0} Â· game {meanGameRenderMilliseconds:0.0} Â· draw {meanDrawMilliseconds:0.0}",
                            $"Present {meanPresentMilliseconds:0.0} Â· wait {meanPacerWaitMilliseconds:0.0} Â· other {meanOtherMilliseconds:0.0} ms",
                            $"Peak ms: CPU {peakEmulationMilliseconds:0.0} Â· game {peakGameRenderMilliseconds:0.0} Â· present {peakPresentMilliseconds:0.0}");
                        nextTimingDisplayUpdate = presentedTimestamp + Stopwatch.Frequency / 4;
                    }
                }
                lastPresentedTimestamp = presentedTimestamp;
                emulationTicksSinceLastPresentation = 0;
                gameRenderTicksSinceLastPresentation = 0;
                drawTicksSinceLastPresentation = 0;
                presentTicksSinceLastPresentation = 0;
                pacerWaitTicksSinceLastPresentation = 0;

                if (presentedFrameRendererStart != 0)
                {
                    var rendererToPresentTicks = presentedTimestamp - presentedFrameRendererStart;
                    smoothedRenderToPresentTicks = smoothedRenderToPresentTicks == 0
                        ? rendererToPresentTicks
                        : smoothedRenderToPresentTicks + ((rendererToPresentTicks - smoothedRenderToPresentTicks) / 8);
                }

                var updatedRenderIntervalTicks = Math.Max(1, (long)Math.Round(Stopwatch.Frequency / presentationFrameRate));
                nextPresentationTimestamp += updatedRenderIntervalTicks;
                if (!verticalSyncEnabled)
                {
                    // A small overrun can recover at the next scheduled present.
                    // Only restart the clock after a stall longer than a whole slot.
                    if (presentedTimestamp - nextPresentationTimestamp > updatedRenderIntervalTicks)
                        nextPresentationTimestamp = presentedTimestamp + updatedRenderIntervalTicks;
                }
                else if (nextPresentationTimestamp <= presentedTimestamp)
                {
                    // Skip missed deadlines while keeping the presentation cadence on its
                    // original phase. Restarting from now can cause catch-up frames to
                    // bunch together after a slow frame.
                    var overdueTicks = presentedTimestamp - nextPresentationTimestamp;
                    var missedIntervals = (overdueTicks / updatedRenderIntervalTicks) + 1;
                    nextPresentationTimestamp += missedIntervals * updatedRenderIntervalTicks;
                }
            }

            var timestampAfterPresent = Stopwatch.GetTimestamp();
            if (settingsSavePending && settingsSaveTask.IsCompleted
                && timestampAfterPresent - settingsSaveTimestamp >= SettingsSaveDelayTicks)
            {
                settingsSaveTask = settings.SaveAsync();
                settingsSavePending = false;
            }

            var timeUntilNextPresentation = nextPresentationTimestamp - timestampAfterPresent;
            if (verticalSyncEnabled && timeUntilNextPresentation > 0)
            {
                pacerWaitTicksSinceLastPresentation += WaitUntil(nextPresentationTimestamp);
            }
        }

        PersistBattery();
        settingsSaveTask.GetAwaiter().GetResult();
        settings.Save();

        if (audioStream != IntPtr.Zero)
        {
            SDL.DestroyAudioStream(audioStream);
        }

        textRenderer?.Dispose();
        if (gamepad != IntPtr.Zero)
            SDL.CloseGamepad(gamepad);
        if (gamepadSubsystemInitialized)
            SDL.QuitSubSystem(SDL.InitFlags.Gamepad);
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

    private static double ToMilliseconds(long ticks)
    {
        return ticks * 1_000.0 / Stopwatch.Frequency;
    }

    private static double GetAudioPlaybackRate(
        int queuedAudioBytes,
        double elapsedSeconds,
        ref double smoothedAudioQueueSeconds)
    {
        var queuedSeconds = queuedAudioBytes
            / (double)(AudioSampleRateHz * AudioBytesPerSampleFrame);
        var smoothingElapsed = Math.Clamp(elapsedSeconds, 0.0, 0.100);
        var smoothingAmount = smoothingElapsed / (AudioQueueSmoothingSeconds + smoothingElapsed);
        smoothedAudioQueueSeconds += (queuedSeconds - smoothedAudioQueueSeconds) * smoothingAmount;
        var playbackAdjustment = (smoothedAudioQueueSeconds - AudioTargetQueueSeconds) * 0.25;
        return Math.Clamp(
            1.0 + playbackAdjustment,
            1.0 - MaximumAudioClockAdjustment,
            1.0 + MaximumAudioClockAdjustment);
    }

    private static long WaitUntil(long deadlineTimestamp)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var remainingTicks = deadlineTimestamp - startTimestamp;
        if (remainingTicks <= 0) return 0;
        var nanoseconds = (ulong)(remainingTicks * 1_000_000_000.0 / Stopwatch.Frequency);
        SDL.DelayPrecise(nanoseconds);
        return Stopwatch.GetTimestamp() - startTimestamp;
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
        W = (panel.W - 32) / 3,
        H = 30,
    };

    private static SDL.FRect GetControlsTabButton(SDL.FRect panel) => new()
    {
        X = panel.X + 16 + ((panel.W - 32) / 3),
        Y = panel.Y + 52,
        W = (panel.W - 32) / 3,
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
        ref BindingMode bindingMode,
        DesktopSettings settings,
        IntPtr window,
        bool gamepadConnected,
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

        if (IsInside(mouseX, mouseY, GetPlayerTabButton(panel)))
        {
            settingsTab = SettingsTab.Player;
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
            if (IsInside(mouseX, mouseY, GetKeyboardBindingTab(panel)))
            {
                bindingMode = BindingMode.Keyboard;
                rebindingAction = null;
                statusMessage = "Keyboard bindings selected.";
                return true;
            }

            if (IsInside(mouseX, mouseY, GetGamepadBindingTab(panel)))
            {
                bindingMode = BindingMode.Gamepad;
                rebindingAction = null;
                statusMessage = gamepadConnected
                    ? "Gamepad bindings selected."
                    : "Connect an XInput controller to bind its buttons.";
                return true;
            }

            for (var index = 0; index < BindingRows.Length; index++)
            {
                if (IsInside(mouseX, mouseY, GetBindingRow(panel, index)))
                {
                    rebindingAction = BindingRows[index].Action;
                    statusMessage = bindingMode == BindingMode.Keyboard
                        ? $"Press a key for {GetActionName(rebindingAction.Value)}. Escape cancels."
                        : gamepadConnected
                            ? $"Press a controller button for {GetActionName(rebindingAction.Value)}. Escape cancels."
                            : "Connect an XInput controller, then press a button. Escape cancels.";
                    return true;
                }
            }

            return false;
        }

        if (settingsTab == SettingsTab.Player) return false;

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
        bool verticalSyncEnabled,
        double displayRefreshRate,
        FrameTimingDisplay timingDisplay,
        BindingMode bindingMode,
        IntPtr gamepad,
        string gamepadName)
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

        DrawSettingsTab(renderer, textRenderer, GetPlayerTabButton(panel), "PLAYER", settingsTab == SettingsTab.Player);
        if (settingsTab == SettingsTab.Player)
        {
            DrawPlayerPanel(renderer, textRenderer, panel, settings, statusMessage);
        }
        else if (settingsTab == SettingsTab.Display)
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

            DrawText(renderer, textRenderer, timingDisplay.Output,
                panel.X + 20, panel.Y + 324, 12, MakeUiColor(181, 194, 211));
            var displayRefreshText = displayRefreshRate > 0
                ? $"Display refresh: {displayRefreshRate:0.##} Hz"
                : "Display refresh: unavailable";
            DrawText(renderer, textRenderer, displayRefreshText,
                panel.X + 20, panel.Y + 344, 12, MakeUiColor(181, 194, 211));
            DrawText(renderer, textRenderer,
                timingDisplay.Work,
                panel.X + 20, panel.Y + 368, 11, MakeUiColor(181, 194, 211));
            DrawText(renderer, textRenderer,
                timingDisplay.Wait,
                panel.X + 20, panel.Y + 386, 11, MakeUiColor(181, 194, 211));
            DrawText(renderer, textRenderer,
                timingDisplay.Peak,
                panel.X + 20, panel.Y + 404, 11, MakeUiColor(181, 194, 211));
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
            DrawSettingsTab(renderer, textRenderer, GetKeyboardBindingTab(panel), "KEYBOARD",
                bindingMode == BindingMode.Keyboard);
            DrawSettingsTab(renderer, textRenderer, GetGamepadBindingTab(panel), "GAMEPAD",
                bindingMode == BindingMode.Gamepad);
            var controllerText = gamepad == IntPtr.Zero
                ? "Controller: Not connected. Connect an XInput controller."
                : $"Controller: {gamepadName} Â· D-pad and left stick move.";
            var controlsMessage = rebindingAction.HasValue ? statusMessage : controllerText;
            DrawText(renderer, textRenderer,
                Truncate(controlsMessage, Math.Max(16, (int)((panel.W - 40) / 8))),
                panel.X + 20, panel.Y + 114, 11, MakeUiColor(181, 194, 211));
            for (var index = 0; index < BindingRows.Length; index++)
            {
                var row = BindingRows[index];
                DrawBindingRow(renderer, textRenderer, GetBindingRow(panel, index), row,
                    bindingMode == BindingMode.Keyboard
                        ? GetKeyName(GetBoundKey(settings, row.Action))
                        : GetGamepadButtonName(gamepad, GetBoundGamepadButton(settings, row.Action)),
                    rebindingAction == row.Action);
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
        string bindingName,
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
        DrawSettingsButton(renderer, textRenderer, keyButton, isListening ? "PRESS INPUT" : bindingName, 11);
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
        Y = panel.Y + 136 + (index * 23),
        W = panel.W - 24,
        H = 22,
    };

    private static SDL.FRect GetKeyboardBindingTab(SDL.FRect panel) => new()
    {
        X = panel.X + 12,
        Y = panel.Y + 80,
        W = (panel.W - 28) / 2,
        H = 28,
    };

    private static SDL.FRect GetGamepadBindingTab(SDL.FRect panel) => new()
    {
        X = panel.X + 16 + ((panel.W - 28) / 2),
        Y = panel.Y + 80,
        W = (panel.W - 28) / 2,
        H = 28,
    };

    private static string GetAspectRatioName(GameAspectRatio aspectRatio)
    {
        return aspectRatio switch
        {
            GameAspectRatio.OriginalPixels => "Original pixels (8:7)",
            GameAspectRatio.Widescreen => "Extended view (16:9)",
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

            var button = GetSnesControllerButton(row.Action);

            return pressed ? currentButtons | button : currentButtons & ~button;
        }

        return currentButtons;
    }

    private static IntPtr OpenFirstGamepad(out uint instanceId, out string gamepadName)
    {
        instanceId = 0;
        gamepadName = string.Empty;
        var gamepadIds = SDL.GetGamepads(out var gamepadCount);
        if (gamepadIds is null || gamepadIds.Length == 0)
            return IntPtr.Zero;

        for (var index = 0; index < gamepadCount && index < gamepadIds.Length; index++)
        {
            var candidateId = gamepadIds[index];
            if (!SDL.IsGamepad(candidateId))
                continue;

            var gamepad = OpenGamepad(candidateId, out gamepadName);
            if (gamepad != IntPtr.Zero)
            {
                instanceId = candidateId;
                return gamepad;
            }
        }

        gamepadName = string.Empty;
        return IntPtr.Zero;
    }

    private static IntPtr OpenGamepad(uint instanceId, out string gamepadName)
    {
        gamepadName = string.Empty;
        if (!SDL.IsGamepad(instanceId))
            return IntPtr.Zero;

        var gamepad = SDL.OpenGamepad(instanceId);
        if (gamepad == IntPtr.Zero)
            return IntPtr.Zero;
        if (!SDL.GamepadConnected(gamepad))
        {
            SDL.CloseGamepad(gamepad);
            return IntPtr.Zero;
        }

        gamepadName = SDL.GetGamepadName(gamepad) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(gamepadName))
            gamepadName = "Controller";
        return gamepad;
    }

    private static SnesControllerButtons GetGamepadControllerButtons(IntPtr gamepad, DesktopSettings settings)
    {
        if (gamepad == IntPtr.Zero || !SDL.GamepadConnected(gamepad))
            return SnesControllerButtons.None;

        var buttons = SnesControllerButtons.None;
        foreach (var row in BindingRows)
        {
            if (SDL.GetGamepadButton(gamepad, GetBoundGamepadButton(settings, row.Action)))
                buttons |= GetSnesControllerButton(row.Action);
        }

        var leftX = SDL.GetGamepadAxis(gamepad, SDL.GamepadAxis.LeftX);
        var leftY = SDL.GetGamepadAxis(gamepad, SDL.GamepadAxis.LeftY);
        if (leftX < -GamepadStickDeadZone) buttons |= SnesControllerButtons.Left;
        if (leftX > GamepadStickDeadZone) buttons |= SnesControllerButtons.Right;
        if (leftY < -GamepadStickDeadZone) buttons |= SnesControllerButtons.Up;
        if (leftY > GamepadStickDeadZone) buttons |= SnesControllerButtons.Down;
        return buttons;
    }

    private static SnesControllerButtons GetSnesControllerButton(GameAction action)
    {
        return action switch
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
    }

    private static SDL.GamepadButton GetBoundGamepadButton(DesktopSettings settings, GameAction action)
    {
        return action switch
        {
            GameAction.Left => settings.MoveLeftGamepadButton,
            GameAction.Right => settings.MoveRightGamepadButton,
            GameAction.Up => settings.MoveUpGamepadButton,
            GameAction.Down => settings.MoveDownGamepadButton,
            GameAction.B => settings.ButtonBGamepadButton,
            GameAction.Y => settings.ButtonYGamepadButton,
            GameAction.A => settings.ButtonAGamepadButton,
            GameAction.X => settings.ButtonXGamepadButton,
            GameAction.Start => settings.StartGamepadButton,
            GameAction.Select => settings.SelectGamepadButton,
            GameAction.L => settings.ButtonLGamepadButton,
            GameAction.R => settings.ButtonRGamepadButton,
            _ => SDL.GamepadButton.Invalid,
        };
    }

    private static void SetGamepadButtonForAction(DesktopSettings settings, GameAction action, SDL.GamepadButton button)
    {
        switch (action)
        {
            case GameAction.Left: settings.MoveLeftGamepadButton = button; break;
            case GameAction.Right: settings.MoveRightGamepadButton = button; break;
            case GameAction.Up: settings.MoveUpGamepadButton = button; break;
            case GameAction.Down: settings.MoveDownGamepadButton = button; break;
            case GameAction.B: settings.ButtonBGamepadButton = button; break;
            case GameAction.Y: settings.ButtonYGamepadButton = button; break;
            case GameAction.A: settings.ButtonAGamepadButton = button; break;
            case GameAction.X: settings.ButtonXGamepadButton = button; break;
            case GameAction.Start: settings.StartGamepadButton = button; break;
            case GameAction.Select: settings.SelectGamepadButton = button; break;
            case GameAction.L: settings.ButtonLGamepadButton = button; break;
            case GameAction.R: settings.ButtonRGamepadButton = button; break;
        }
    }

    private static bool TryGetActionForGamepadButton(
        DesktopSettings settings,
        SDL.GamepadButton button,
        out GameAction action)
    {
        foreach (var row in BindingRows)
        {
            if (GetBoundGamepadButton(settings, row.Action) == button)
            {
                action = row.Action;
                return true;
            }
        }

        action = default;
        return false;
    }

    private static string GetGamepadButtonName(IntPtr gamepad, SDL.GamepadButton button)
    {
        if (gamepad != IntPtr.Zero)
        {
            var label = SDL.GetGamepadButtonLabel(gamepad, button).ToString();
            if (!string.IsNullOrWhiteSpace(label)
                && !label.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                && !label.Equals("Invalid", StringComparison.OrdinalIgnoreCase))
                return label.ToUpperInvariant();
        }

        return button switch
        {
            SDL.GamepadButton.South => "A",
            SDL.GamepadButton.East => "B",
            SDL.GamepadButton.West => "X",
            SDL.GamepadButton.North => "Y",
            SDL.GamepadButton.Back => "BACK",
            SDL.GamepadButton.Start => "START",
            SDL.GamepadButton.LeftShoulder => "LB",
            SDL.GamepadButton.RightShoulder => "RB",
            SDL.GamepadButton.DPadLeft => "D-PAD LEFT",
            SDL.GamepadButton.DPadRight => "D-PAD RIGHT",
            SDL.GamepadButton.DPadUp => "D-PAD UP",
            SDL.GamepadButton.DPadDown => "D-PAD DOWN",
            _ => button.ToString().ToUpperInvariant(),
        };
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
