using SDL3;

internal static class Program
{
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
        while (running)
        {
            while (SDL.PollEvent(out var @event))
            {
                if ((SDL.EventType)@event.Type == SDL.EventType.Quit)
                {
                    running = false;
                }
            }

            SDL.SetRenderDrawColor(renderer, 24, 28, 40, 255);
            SDL.RenderClear(renderer);
            SDL.SetRenderDrawColor(renderer, 255, 255, 255, 255);
            SDL.RenderDebugText(renderer, 32, 32, "Hello World");
            SDL.RenderPresent(renderer);
        }

        SDL.DestroyRenderer(renderer);
        SDL.DestroyWindow(window);
        SDL.Quit();
        return 0;
    }
}
