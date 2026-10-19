using SDL3;

internal sealed class UiTextRenderer : IDisposable
{
    private const int MaximumCachedTextures = 256;
    private readonly IntPtr renderer;
    private readonly Dictionary<(int Size, bool Bold), IntPtr> fonts = [];
    private readonly Dictionary<TextKey, TextTexture> textures = [];
    private readonly LinkedList<TextKey> textureUsage = [];
    private bool disposed;

    private UiTextRenderer(IntPtr renderer, string fontPath)
    {
        this.renderer = renderer;
        FontPath = fontPath;
    }

    public float Scale { get; set; } = 1;

    public static UiTextRenderer? TryCreate(IntPtr renderer, string fontPath)
    {
        var initialized = false;
        try
        {
            initialized = TTF.Init();
            if (!initialized || !File.Exists(fontPath))
            {
                if (initialized)
                {
                    TTF.Quit();
                }

                return null;
            }

            var textRenderer = new UiTextRenderer(renderer, fontPath);
            if (textRenderer.GetFont(16, bold: false) == IntPtr.Zero)
            {
                textRenderer.Dispose();
                return null;
            }

            return textRenderer;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            if (initialized)
            {
                TTF.Quit();
            }

            return null;
        }
    }

    private string FontPath { get; }

    public (float Width, float Height) Measure(string text, float size, bool bold = false)
    {
        var entry = GetTexture(text, GetPixelSize(size), bold, new SDL.Color { R = 255, G = 255, B = 255, A = 255 });
        return entry is null ? (0, 0) : (entry.Width, entry.Height);
    }

    public bool Draw(string text, float x, float y, float size, SDL.Color color, bool bold = false)
    {
        var entry = GetTexture(text, GetPixelSize(size), bold, color);
        if (entry is null)
        {
            return false;
        }

        var destination = new SDL.FRect
        {
            X = MathF.Round(x),
            Y = MathF.Round(y),
            W = entry.Width,
            H = entry.Height,
        };
        return SDL.RenderTexture(renderer, entry.Texture, IntPtr.Zero, in destination);
    }

    private IntPtr GetFont(int pixelSize, bool bold)
    {
        var key = (pixelSize, bold);
        if (fonts.TryGetValue(key, out var existingFont))
        {
            return existingFont;
        }

        var font = TTF.OpenFont(FontPath, pixelSize);
        if (font == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        TTF.SetFontStyle(font, bold ? TTF.FontStyleFlags.Bold : TTF.FontStyleFlags.Normal);
        fonts.Add(key, font);
        return font;
    }

    private TextTexture? GetTexture(string text, int pixelSize, bool bold, SDL.Color color)
    {
        var key = new TextKey(text, pixelSize, bold, color.R, color.G, color.B, color.A);
        if (textures.TryGetValue(key, out var existingTexture))
        {
            textureUsage.Remove(existingTexture.UsageNode);
            textureUsage.AddLast(existingTexture.UsageNode);
            return existingTexture;
        }

        var font = GetFont(pixelSize, bold);
        if (font == IntPtr.Zero)
        {
            return null;
        }

        var surface = TTF.RenderTextBlended(font, text, UIntPtr.Zero, color);
        if (surface == IntPtr.Zero)
        {
            return null;
        }

        var texture = SDL.CreateTextureFromSurface(renderer, surface);
        SDL.DestroySurface(surface);
        if (texture == IntPtr.Zero || !SDL.GetTextureSize(texture, out var width, out var height))
        {
            if (texture != IntPtr.Zero)
            {
                SDL.DestroyTexture(texture);
            }

            return null;
        }

        SDL.SetTextureScaleMode(texture, SDL.ScaleMode.Linear);
        if (textures.Count >= MaximumCachedTextures)
        {
            var oldest = textureUsage.First!;
            textureUsage.RemoveFirst();
            if (textures.Remove(oldest.Value, out var evicted))
                SDL.DestroyTexture(evicted.Texture);
        }
        var usageNode = textureUsage.AddLast(key);
        var entry = new TextTexture(texture, width, height, usageNode);
        textures.Add(key, entry);
        return entry;
    }

    private int GetPixelSize(float size)
    {
        return Math.Max(8, (int)MathF.Round(size * Scale));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var entry in textures.Values)
        {
            SDL.DestroyTexture(entry.Texture);
        }
        textures.Clear();
        textureUsage.Clear();

        foreach (var font in fonts.Values)
        {
            TTF.CloseFont(font);
        }

        TTF.Quit();
    }

    private readonly record struct TextKey(
        string Text,
        int PixelSize,
        bool Bold,
        byte Red,
        byte Green,
        byte Blue,
        byte Alpha);

    private sealed record TextTexture(IntPtr Texture, float Width, float Height, LinkedListNode<TextKey> UsageNode);
}
