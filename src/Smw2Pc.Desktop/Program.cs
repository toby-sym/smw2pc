using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SDL3;
using Smw2Pc.Core.Cpu;
using Smw2Pc.Core.Machine;
using Smw2Pc.Core.Rom;

internal static class Program
{
    private const int WindowWidth = 800;
    private const int WindowHeight = 736;
    private const int InstructionsPerFrame = 18000;
    private const int MaximumCatchUpFrames = 5;

    private static readonly ConcurrentQueue<FileDialogResult> PendingFileDialogResults = new();
    private static readonly SDL.DialogFileCallback RomFileDialogCallback = OnRomFileDialogCompleted;

    private static int Main()
    {
        if (!SDL.Init(SDL.InitFlags.Video))
        {
            Console.Error.WriteLine($"SDL could not initialize: {SDL.GetError()}");
            return 1;
        }

        if (!SDL.CreateWindowAndRenderer(
                "SMW2PC",
                WindowWidth,
                WindowHeight,
                0,
                out var window,
                out var renderer))
        {
            Console.Error.WriteLine($"Could not create the window: {SDL.GetError()}");
            SDL.Quit();
            return 1;
        }

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

        SDL.SetTextureScaleMode(gameTexture, SDL.ScaleMode.Nearest);
        SDL.SetRenderVSync(renderer, 1);

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
        var gameArea = new SDL.FRect { X = 16, Y = 16, W = 768, H = 672 };
        var openButton = new SDL.FRect { X = 16, Y = 696, W = 112, H = 28 };
        var previousTimestamp = Stopwatch.GetTimestamp();
        var frameAccumulator = 0.0;

        while (running)
        {
            var currentTimestamp = Stopwatch.GetTimestamp();
            var elapsedSeconds = (double)(currentTimestamp - previousTimestamp) / Stopwatch.Frequency;
            previousTimestamp = currentTimestamp;

            while (SDL.PollEvent(out var @event))
            {
                var eventType = (SDL.EventType)@event.Type;
                if (eventType == SDL.EventType.Quit)
                {
                    running = false;
                }
                else if (eventType == SDL.EventType.MouseButtonDown
                    && @event.Button.Button == 1
                    && IsInside(@event.Button.X, @event.Button.Y, openButton))
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
                else if (eventType is SDL.EventType.KeyDown or SDL.EventType.KeyUp)
                {
                    var isDown = eventType == SDL.EventType.KeyDown;
                    var key = @event.Key.Key;
                    if (isDown && key == SDL.Keycode.F1)
                    {
                        ShowRomFileDialog(window);
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
                    elapsedSeconds = 0;
                    var regionName = loadedRom.Header.IsPalRegion ? "PAL" : "NTSC";
                    statusMessage = $"Running {loadedRom.Header.Title} at {loadedRom.Header.FrameRateHz:0.##} Hz ({regionName})";
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

            SDL.SetRenderDrawColor(renderer, 24, 28, 40, 255);
            SDL.RenderClear(renderer);

            if (cpuRunner is not null)
            {
                SDL.RenderTexture(renderer, gameTexture, IntPtr.Zero, in gameArea);
            }
            else if (loadedRom is not null)
            {
                SDL.RenderTexture(renderer, gameTexture, IntPtr.Zero, in gameArea);
            }
            else
            {
                SDL.SetRenderDrawColor(renderer, 16, 20, 30, 255);
                SDL.RenderFillRect(renderer, in gameArea);
                SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
                SDL.RenderDebugText(renderer, 32, 40, "SMW2PC");
                SDL.RenderDebugText(renderer, 32, 64, "Open a Super Nintendo ROM to start.");
            }

            SDL.SetRenderDrawColor(renderer, 52, 86, 132, 255);
            SDL.RenderFillRect(renderer, in openButton);
            SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
            SDL.RenderDebugText(renderer, 28, 704, "Open ROM");
            SDL.RenderDebugText(renderer, 144, 704, $"{statusMessage}   Arrows move · Z jump · X run · Enter start · F1 open");

            SDL.RenderPresent(renderer);
        }

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
