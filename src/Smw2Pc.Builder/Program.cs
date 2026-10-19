using System.Collections.Concurrent;
using System.Reflection;
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
    private static string BuilderDirectory
    {
        get
        {
            var executableDirectory = new DirectoryInfo(Path.GetDirectoryName(Environment.ProcessPath)!
                ?? throw new InvalidOperationException("Cannot locate the builder executable."));
            return executableDirectory.FullName;
        }
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(["--version"])) { Console.WriteLine(Version); return 0; }
            if (args.SequenceEqual(["--verify-native"])) return NativeDependencyProof.Run();
            if (args.Length > 0)
            {
                if (args.Length is not (2 or 4) || args[0] != "--build" || (args.Length == 4 && args[2] != "--output"))
                    throw new ArgumentException("Usage: SMW2PC [--build <rom> [--output <directory>]]");
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
        if (!SDL.CreateWindowAndRenderer($"SMW2PC {Version}", 520, 260, 0, out var window, out var renderer))
            throw new InvalidOperationException(SDL.GetError());
        SDL.SetRenderVSync(renderer, 1);
        using var text = UiTextRenderer.TryCreate(renderer, BundledAssets.GetFontPath())
            ?? throw new InvalidOperationException("The bundled font could not be opened.");
        using var cancellation = new CancellationTokenSource();
        Task<string>? build = null;
        var running = true;
        var dialogOpen = false;
        var status = $"Creates {GameBuilder.ArtifactName} in this folder";
        var failed = false;
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
                while (SDL.PollEvent(out var input))
                {
                    var type = (SDL.EventType)input.Type;
                    if (type == SDL.EventType.Quit) running = false;
                    var activate = type == SDL.EventType.KeyDown && input.Key.Key is SDL.Keycode.Return or SDL.Keycode.Space;
                    activate |= type == SDL.EventType.MouseButtonDown && input.Button.Button == 1
                        && input.Button.X * scaleX >= button.X && input.Button.X * scaleX < button.X + button.W
                        && input.Button.Y * scaleY >= button.Y && input.Button.Y * scaleY < button.Y + button.H;
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
                    status = $"Building {GameBuilder.ArtifactName}...";
                    failed = false;
                    build = Task.Run(() => GameBuilder.BuildAsync(rom, BuilderDirectory, cancellationToken: cancellation.Token));
                }
                if (build?.IsCompleted == true)
                {
                    try { build.GetAwaiter().GetResult(); status = $"{GameBuilder.ArtifactName} created"; }
                    catch (Exception exception)
                    {
                        failed = true;
                        status = exception is ArgumentException ? "Select a .sfc or .smc ROM"
                            : "Build failed — see the SMW2PC build log";
                    }
                    build = null;
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
                Center(text, Version, outputWidth / 2f, 228 * scaleY, 11,
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
