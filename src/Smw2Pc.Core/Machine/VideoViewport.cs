namespace Smw2Pc.Core.Machine;

/// <summary>A presentation viewport in the game's original pixel coordinates.</summary>
public readonly record struct VideoViewport(int Width, int OriginX)
{
    public const int WidescreenWidth = 342;
    public static VideoViewport Original => new(256, 0);
    // Retains the television pixel proportions: 256 * (16/9) / (4/3).
    public static VideoViewport Widescreen => new(WidescreenWidth, -(WidescreenWidth - 256) / 2);
}
