using SDL3;

internal static class NativeDependencyProof
{
    public static int Run()
    {
        Environment.SetEnvironmentVariable("SDL_VIDEO_DRIVER", "dummy");
        Environment.SetEnvironmentVariable("SDL_AUDIO_DRIVER", "dummy");
        Environment.SetEnvironmentVariable("SDL_RENDER_DRIVER", "software");
        IntPtr window = IntPtr.Zero;
        IntPtr renderer = IntPtr.Zero;
        try
        {
            if (!SDL.Init(SDL.InitFlags.Video | SDL.InitFlags.Audio))
                throw new InvalidOperationException(SDL.GetError());
            if (!SDL.CreateWindowAndRenderer("SMW2PC dependency proof", 256, 224, 0, out window, out renderer))
                throw new InvalidOperationException(SDL.GetError());
            using var text = UiTextRenderer.TryCreate(renderer, BundledAssets.GetFontPath())
                ?? throw new InvalidOperationException("Bundled SDL_ttf or UI font could not be loaded.");
            var measured = text.Measure("SMW2PC compiled player", 16);
            if (measured.Width <= 0) throw new InvalidOperationException("Bundled font did not render.");
            Console.WriteLine("Portable native dependencies passed: SDL video, audio, SDL_ttf and bundled font.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (renderer != IntPtr.Zero) SDL.DestroyRenderer(renderer);
            if (window != IntPtr.Zero) SDL.DestroyWindow(window);
            SDL.Quit();
        }
    }
}
