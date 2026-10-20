using System.Collections.Concurrent;
using System.Reflection;
using System.Diagnostics;
using Smw2Pc.Core.Storage;
using System.Runtime.InteropServices;
using SDL3;
using Smw2Pc.Builder;

internal static class Program
{
    private static readonly ConcurrentQueue<string> SelectedRoms = new();
    private static readonly SDL.DialogFileCallback FileDialogCallback = OnFileSelected;

    private static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];

    // IncludeAllContentForSelfExtract points AppContext.BaseDirectory into the
    // runtime cache. Outputs belong beside the executable the user opened.
    private static string BuilderDirectory => Path.GetDirectoryName(Environment.ProcessPath)!
        ?? throw new InvalidOperationException("Cannot locate the builder executable.");

    private static int Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(["--version"])) { Console.WriteLine(Version); return 0; }
            if (args.SequenceEqual(["--verify-native"])) return NativeDependencyProof.Run();
            if (args.Length > 0)
            {
                if (args.Length is not (2 or 4) || args[0] != "--build" || (args.Length == 4 && args[2] != "--output"))
                    throw new ArgumentException("Usage: SMW2PC.exe [--build <rom> [--output <directory>]]");
                GameBuilder.BuildAsync(args[1], args.Length == 4 ? args[3] : BuilderDirectory, Console.WriteLine)
                    .GetAwaiter().GetResult();
                return 0;
            }
            return RunWindow();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RunWindow()
    {
        if (!SDL.Init(SDL.InitFlags.Video)) throw new InvalidOperationException(SDL.GetError());
        if (!SDL.CreateWindowAndRenderer($"SMW2PC {Version}", 520, 440, 0, out var window, out var renderer))
            throw new InvalidOperationException(SDL.GetError());
        SDL.SetRenderVSync(renderer, 1);
        using var text = UiTextRenderer.TryCreate(renderer, BundledAssets.GetFontPath())
            ?? throw new InvalidOperationException("The bundled font could not be opened.");
        using var cancellation = new CancellationTokenSource();
        Task<string>? build = null;
        var running = true;
        var dialogOpen = false;
        var status = "Creates SMW.exe in this folder";
        var failed = false;
        var updates = new ConcurrentQueue<string>();
        var recentRoms = RecentRoms.Load();
        var reportPath = Path.Combine(BuilderDirectory, "SMW-compatibility.html");
        try
        {
            while (running)
            {
                SDL.GetWindowSize(window, out var width, out var height);
                SDL.GetCurrentRenderOutputSize(renderer, out var outputWidth, out var outputHeight);
                var scaleX = outputWidth / (float)Math.Max(1, width);
                var scaleY = outputHeight / (float)Math.Max(1, height);
                var button = new SDL.FRect { X = (outputWidth - 180 * scaleX) / 2, Y = 92 * scaleY, W = 180 * scaleX, H = 56 * scaleY };
                text.Scale = scaleY;
                var reportButton = new SDL.FRect { X = 110 * scaleX, Y = 364 * scaleY, W = 300 * scaleX, H = 28 * scaleY };
                while (SDL.PollEvent(out var input))
                {
                    var type = (SDL.EventType)input.Type;
                    if (type == SDL.EventType.Quit) running = false;
                    var activate = type == SDL.EventType.KeyDown && input.Key.Key is SDL.Keycode.Return or SDL.Keycode.Space;
                    activate |= type == SDL.EventType.MouseButtonDown && input.Button.Button == 1
                        && input.Button.X * scaleX >= button.X && input.Button.X * scaleX < button.X + button.W
                        && input.Button.Y * scaleY >= button.Y && input.Button.Y * scaleY < button.Y + button.H;
                    if (type == SDL.EventType.MouseButtonDown && input.Button.Button == 1 && build is null && !dialogOpen)
                    {
                        var x = input.Button.X * scaleX;
                        var y = input.Button.Y * scaleY;
                        for (var i = 0; i < Math.Min(3, recentRoms.Count); i++)
                            if (x >= 36 * scaleX && x < outputWidth - 36 * scaleX
                                && y >= (236 + i * 36) * scaleY && y < (268 + i * 36) * scaleY)
                                SelectedRoms.Enqueue(recentRoms[i]);
                        if (File.Exists(reportPath) && x >= reportButton.X && x < reportButton.X + reportButton.W
                            && y >= reportButton.Y && y < reportButton.Y + reportButton.H)
                        {
                            try { Process.Start(new ProcessStartInfo(reportPath) { UseShellExecute = true }); }
                            catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
                            { status = "Could not open the build report"; }
                        }
                    }
                    if (activate && build is null && !dialogOpen)
                    {
                        dialogOpen = true;
                        SDL.ShowOpenFileDialog(FileDialogCallback, IntPtr.Zero, window, null!, 0, null!, false);
                    }
                }
                while (SelectedRoms.TryDequeue(out var rom))
                {
                    dialogOpen = false;
                    if (rom.Length == 0 || build is not null) continue;
                    status = "Building SMW.exe...";
                    failed = false;
                    build = Task.Run(() => GameBuilder.BuildAsync(rom, BuilderDirectory, updates.Enqueue, cancellation.Token));
                }
                while (updates.TryDequeue(out var update))
                    status = update.Length > 62 ? update[..59] + "..." : update;
                if (build?.IsCompleted == true)
                {
                    try { build.GetAwaiter().GetResult(); status = "SMW.exe created"; }
                    catch (Exception exception)
                    {
                        failed = true;
                        status = exception is ArgumentException ? "Select a .sfc or .smc ROM"
                            : "Build failed â€” see %LOCALAPPDATA%\\SMW2PC\\build.log";
                    }
                    build = null;
                    recentRoms = RecentRoms.Load();
                }

                SDL.SetRenderDrawColor(renderer, 16, 19, 24, 255);
                SDL.RenderClear(renderer);
                SDL.SetRenderDrawColor(renderer, build is null ? (byte)235 : (byte)78, build is null ? (byte)239 : (byte)84,
                    build is null ? (byte)245 : (byte)94, 255);
                SDL.RenderFillRect(renderer, in button);
                Center(text, build is null ? "Add ROM" : "Building...", outputWidth / 2f, 107 * scaleY, 21,
                    new SDL.Color { R = 16, G = 19, B = 24, A = 255 });
                Center(text, status, outputWidth / 2f, 170 * scaleY, failed ? 12 : 14,
                    new SDL.Color { R = failed ? (byte)244 : (byte)165, G = failed ? (byte)154 : (byte)175, B = 188, A = 255 });
                Center(text, "Recent ROMs", outputWidth / 2f, 208 * scaleY, 12,
                    new SDL.Color { R = 165, G = 175, B = 188, A = 255 });
                for (var i = 0; i < Math.Min(3, recentRoms.Count); i++)
                {
                    var row = new SDL.FRect { X = 36 * scaleX, Y = (236 + i * 36) * scaleY, W = outputWidth - 72 * scaleX, H = 32 * scaleY };
                    SDL.SetRenderDrawColor(renderer, 35, 43, 55, 255);
                    SDL.RenderFillRect(renderer, in row);
                    var name = Path.GetFileName(recentRoms[i]);
                    if (name.Length > 48) name = name[..45] + "...";
                    Center(text, name, outputWidth / 2f, row.Y + 6 * scaleY, 12,
                        new SDL.Color { R = 220, G = 229, B = 241, A = 255 });
                }
                if (File.Exists(reportPath))
                    Center(text, "Open last build report", outputWidth / 2f, reportButton.Y + 4 * scaleY, 13,
                        new SDL.Color { R = 138, G = 193, B = 248, A = 255 });
                Center(text, Version, outputWidth / 2f, 410 * scaleY, 11,
                    new SDL.Color { R = 101, G = 112, B = 127, A = 255 });
                SDL.RenderPresent(renderer);
                SDL.Delay(16);
            }
        }
        finally
        {
            cancellation.Cancel();
            if (build is not null) { try { build.GetAwaiter().GetResult(); } catch (Exception) { } }
            text.Dispose();
            SDL.DestroyRenderer(renderer);
            SDL.DestroyWindow(window);
            SDL.Quit();
        }
        return 0;
    }

    private static void Center(UiTextRenderer text, string value, float centerX, float y, float size, SDL.Color color)
    {
        var measurement = text.Measure(value, size);
        text.Draw(value, centerX - measurement.Width / 2f, y, size, color);
    }

    private static void OnFileSelected(IntPtr userData, IntPtr fileList, int filter)
    {
        var file = fileList == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(fileList);
        SelectedRoms.Enqueue(file == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(file) ?? "");
    }
}
