using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SDL3;
using Smw2Pc.Core.Rom;

internal static class Program
{
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
                640,
                480,
                0,
                out var window,
                out var renderer))
        {
            Console.Error.WriteLine($"Could not create the window: {SDL.GetError()}");
            SDL.Quit();
            return 1;
        }

        SDL.SetRenderVSync(renderer, 1);

        var running = true;
        var statusMessage = "Select a local SNES ROM file (.sfc or .smc).";
        SnesRom? loadedRom = null;
        var openButton = new SDL.FRect { X = 24, Y = 24, W = 192, H = 48 };

        while (running)
        {
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
                    statusMessage = "ROM loaded. Game execution is not implemented yet.";
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    loadedRom = null;
                    statusMessage = exception.Message;
                }
            }

            SDL.SetRenderDrawColor(renderer, 24, 28, 40, 255);
            SDL.RenderClear(renderer);

            SDL.SetRenderDrawColor(renderer, 52, 86, 132, 255);
            SDL.RenderFillRect(renderer, in openButton);
            SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
            SDL.RenderDebugText(renderer, 40, 40, "Open ROM");
            SDL.RenderDebugText(renderer, 24, 96, statusMessage);

            if (loadedRom is not null)
            {
                SDL.RenderDebugText(renderer, 24, 136, $"File: {loadedRom.FileName}");
                SDL.RenderDebugText(renderer, 24, 160, $"Title: {loadedRom.Header.Title}");
                SDL.RenderDebugText(renderer, 24, 184, $"ROM size: {loadedRom.SizeBytes:N0} bytes");
                SDL.RenderDebugText(renderer, 24, 208, $"Map mode: 0x{loadedRom.Header.MapMode:X2}");
                SDL.RenderDebugText(
                    renderer,
                    24,
                    232,
                    loadedRom.HasCopierHeader ? "512-byte copier header removed while loading." : "No copier header detected.");
            }

            SDL.RenderPresent(renderer);
        }

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

    private static bool IsInside(float x, float y, SDL.FRect rectangle)
    {
        return x >= rectangle.X
            && x < rectangle.X + rectangle.W
            && y >= rectangle.Y
            && y < rectangle.Y + rectangle.H;
    }

    private sealed record FileDialogResult(string? Path, string? Error);
}
