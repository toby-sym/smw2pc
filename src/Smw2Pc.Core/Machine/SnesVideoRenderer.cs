using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Smw2Pc.Core.Machine;

public static partial class SnesVideoRenderer
{
    public const int Width = 256;
    public const int Height = 224;
    public const int BytesPerPixel = 4;
    private const int MaximumInterpolatedScrollDelta = 16;
    private const int MaximumInterpolatedObjectDelta = 32;
    private const int TilePixels = 8 * 8;
    private const int TwoBppTileCount = 0x10000 / 16;
    private const int FourBppTileCount = 0x10000 / 32;
    private static readonly uint[] BrightnessColors = CreateBrightnessColors();
    [ThreadStatic]
    private static TileDecodeCache? activeTileDecodeCache;
    [ThreadStatic]
    private static TileDecodeCache? reusableTileDecodeCache;
    [ThreadStatic]
    private static BackgroundSampleCache? backgroundSampleCache;

    [ThreadStatic]
    private static VideoViewport activeViewport;
    [ThreadStatic]
    private static SmwWidescreen? activeWidescreen;

    public static void RenderFrame(SnesHardware hardware, Span<byte> rgbaPixels, float interpolationProgress = 0f,
        VideoViewport? viewport = null)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        var view = viewport ?? VideoViewport.Original;
        if (view.Width is not (Width or VideoViewport.WidescreenWidth))
            throw new ArgumentOutOfRangeException(nameof(viewport));
        var width = view.Width;
        var framePixelCount = width * Height;
        var frameByteCount = framePixelCount * BytesPerPixel;
        if (rgbaPixels.Length < frameByteCount)
        {
            throw new ArgumentException("The frame buffer is too small for the requested RGBA viewport.", nameof(rgbaPixels));
        }

        var previousViewport = activeViewport;
        var previousWidescreen = activeWidescreen;
        activeViewport = view;
        activeWidescreen = width > Width && hardware.Widescreen?.CanExtendLevel == true ? hardware.Widescreen : null;
        interpolationProgress = float.IsFinite(interpolationProgress)
            ? Math.Clamp(interpolationProgress, 0f, 1f)
            : 1f;
        var scrollInterpolationProgress = hardware.HasPreviousPpuFrameScrollOffsets
            ? interpolationProgress
            : 1f;
        if (activeWidescreen is not null)
            activeViewport = view = view with { OriginX = activeWidescreen.GetOriginX(view.Width, scrollInterpolationProgress) };
        var videoRam = hardware.VideoRam.Span;
        var colorRam = hardware.ColorRam.Span;
        var backdropColor = ReadColorValue(colorRam, 0);
        var previousTileDecodeCache = activeTileDecodeCache;
        var tileDecodeCache = reusableTileDecodeCache ??= new TileDecodeCache();
        tileDecodeCache.BeginFrame();
        activeTileDecodeCache = tileDecodeCache;
        var previousObjectCache = activeObjectCache;
        var objectCache = reusableObjectCache ??= new ObjectRenderCache();
        objectCache.Prepare(hardware, scrollInterpolationProgress);
        activeObjectCache = objectCache;
        var pixelSourcesBuffer = ArrayPool<byte>.Shared.Rent(framePixelCount);
        var pixelColorsBuffer = ArrayPool<ushort>.Shared.Rent(framePixelCount);

        try
        {
            var pixelSources = pixelSourcesBuffer.AsSpan(0, framePixelCount);
            var pixelColors = pixelColorsBuffer.AsSpan(0, framePixelCount);
            pixelSources.Clear();
            for (var screenY = 0; screenY < Height; screenY++)
            {
                var hardwareScanline = screenY + 1;
                var registers = hardware.GetPpuRegistersForScanline(hardwareScanline).Span;
                var brightness = registers[0] & 0x0F;
                var forcedBlank = (registers[0] & 0x80) != 0;
                var backdrop = ReadColor(backdropColor, brightness, forcedBlank);
                var firstPixel = screenY * width;
                FillScanline(rgbaPixels, screenY, backdrop);
                pixelColors.Slice(firstPixel, width).Fill(backdropColor);

                if (!forcedBlank)
                {
                    DrawScreenLayers(
                        hardware,
                        hardwareScanline,
                        registers,
                        subScreen: false,
                        brightness,
                        rgbaPixels,
                        pixelSources,
                        pixelColors,
                        videoRam,
                        colorRam,
                        scrollInterpolationProgress);
                }
            }

            ApplyColorMath(
                hardware,
                rgbaPixels,
                pixelSources,
                pixelColors,
                videoRam,
                colorRam,
                scrollInterpolationProgress);
            if (interpolationProgress > 0f
                && hardware.HasPreviousPpuFrameScrollOffsets
                && HasModeSevenScanline(hardware))
            {
                var sourcePixels = ArrayPool<byte>.Shared.Rent(frameByteCount);
                try
                {
                    rgbaPixels[..frameByteCount].CopyTo(sourcePixels);
                    InterpolateCameraScroll(
                        hardware,
                        sourcePixels.AsSpan(0, frameByteCount),
                        rgbaPixels[..frameByteCount],
                        pixelSources,
                        interpolationProgress);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(sourcePixels);
                }
            }

            if (width > Width && hardware.Widescreen?.IsSupported == true && activeWidescreen is null)
            {
                var left = -VideoViewport.Widescreen.OriginX;
                for (var y = 0; y < Height; y++)
                {
                    var row = rgbaPixels.Slice(y * width * BytesPerPixel, width * BytesPerPixel);
                    MemoryMarshal.Cast<byte, uint>(row[..(left * BytesPerPixel)]).Fill(0xFF000000);
                    MemoryMarshal.Cast<byte, uint>(row[((left + Width) * BytesPerPixel)..]).Fill(0xFF000000);
                }
            }
        }
        finally
        {
            activeViewport = previousViewport;
            activeWidescreen = previousWidescreen;
            activeTileDecodeCache = previousTileDecodeCache;
            activeObjectCache = previousObjectCache;
            ArrayPool<byte>.Shared.Return(pixelSourcesBuffer);
            ArrayPool<ushort>.Shared.Return(pixelColorsBuffer);
        }
    }

    private static bool HasModeSevenScanline(SnesHardware hardware)
    {
        for (var screenY = 0; screenY < Height; screenY++)
        {
            if ((hardware.GetPpuRegistersForScanline(screenY + 1).Span[0x05] & 0x07) == 7)
            {
                return true;
            }
        }

        return false;
    }

    private static void InterpolateCameraScroll(
        SnesHardware hardware,
        ReadOnlySpan<byte> sourcePixels,
        Span<byte> outputPixels,
        ReadOnlySpan<byte> pixelSources,
        float interpolationProgress)
    {
        var width = activeViewport.Width;
        if (interpolationProgress <= 0f)
        {
            return;
        }

        Span<float> horizontalShifts = stackalloc float[4];
        Span<float> verticalShifts = stackalloc float[4];
        for (var screenY = 0; screenY < Height; screenY++)
        {
            var hardwareScanline = screenY + 1;
            if ((hardware.GetPpuRegistersForScanline(hardwareScanline).Span[0x05] & 0x07) != 7)
            {
                continue;
            }

            for (var background = 0; background < 4; background++)
            {
                var (horizontal, vertical) = hardware.GetBackgroundScrollDeltaForScanline(hardwareScanline, background);
                if (Math.Abs(horizontal) > MaximumInterpolatedScrollDelta)
                {
                    horizontal = 0;
                }

                if (Math.Abs(vertical) > MaximumInterpolatedScrollDelta)
                {
                    vertical = 0;
                }

                horizontalShifts[background] = -horizontal * (1f - interpolationProgress);
                verticalShifts[background] = -vertical * (1f - interpolationProgress);
            }

            for (var screenX = 0; screenX < width; screenX++)
            {
                var pixel = (screenY * width) + screenX;
                var source = pixelSources[pixel];
                if (source is < 1 or > 4)
                {
                    continue;
                }

                var background = source - 1;
                var shiftX = horizontalShifts[background];
                var shiftY = verticalShifts[background];
                if (MathF.Abs(shiftX) < 0.001f && MathF.Abs(shiftY) < 0.001f)
                {
                    continue;
                }

                var sampleX = screenX + shiftX;
                var sampleY = screenY + shiftY;
                var samplePixelX = (int)MathF.Round(sampleX, MidpointRounding.AwayFromZero);
                var samplePixelY = (int)MathF.Round(sampleY, MidpointRounding.AwayFromZero);
                if ((uint)samplePixelX >= width || (uint)samplePixelY >= Height)
                {
                    continue;
                }

                var samplePixel = (samplePixelY * width) + samplePixelX;
                if (pixelSources[samplePixel] > 4)
                {
                    continue;
                }

                var outputOffset = pixel * BytesPerPixel;
                var sampleOffset = samplePixel * BytesPerPixel;
                sourcePixels.Slice(sampleOffset, BytesPerPixel).CopyTo(outputPixels[outputOffset..]);
            }
        }
    }

    private static void DrawScreenLayers(
        SnesHardware hardware,
        int hardwareScanline,
        ReadOnlySpan<byte> registers,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        Span<ushort> pixelColors,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam,
        float interpolationProgress)
    {
        var screenY = hardwareScanline - 1;
        var screenEnable = registers[subScreen ? 0x2D : 0x2C];
        var mode = registers[0x05] & 0x07;
        if (mode == 7)
        {
            DrawModeSevenLayers(hardware, hardwareScanline, registers, screenEnable, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        }
        else if (mode == 1)
        {
            DrawModeOneLayers(hardware, registers, screenEnable, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        }
        else
        {
            for (var background = 3; background >= 0; background--)
            {
                if ((screenEnable & (1 << background)) == 0)
                {
                    continue;
                }

                DrawBackground(
                    hardware,
                    registers,
                    hardwareScanline,
                    background,
                    mode,
                    priority: -1,
                    screenY,
                    subScreen,
                    brightness,
                    rgbaPixels,
                    pixelSources,
                    pixelColors,
                    videoRam,
                    colorRam,
                    interpolationProgress);
            }

            if ((screenEnable & 0x10) != 0)
            {
                DrawObjects(hardware, registers, priority: -1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
            }
        }
    }

    private static void ApplyColorMath(
        SnesHardware hardware,
        Span<byte> mainPixels,
        ReadOnlySpan<byte> mainSources,
        Span<ushort> mainColors,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam,
        float interpolationProgress)
    {
        var width = activeViewport.Width;
        var hasColorMathOrMainClip = false;
        var useSubScreen = false;
        for (var scanline = 0; scanline < Height; scanline++)
        {
            var scanlineRegisters = hardware.GetPpuRegistersForScanline(scanline + 1).Span;
            var colorWindowSelect = scanlineRegisters[0x30];
            var scanlineColorMath = scanlineRegisters[0x31] & 0x3F;
            hasColorMathOrMainClip |= (colorWindowSelect & 0xC0) != 0 || scanlineColorMath != 0;
            if ((colorWindowSelect & 0x02) != 0 && scanlineColorMath != 0)
            {
                useSubScreen = true;
            }
        }

        if (!hasColorMathOrMainClip)
        {
            return;
        }

        byte[]? subPixels = null;
        byte[]? subSources = null;
        ushort[]? subColors = null;
        try
        {
            if (useSubScreen)
            {
                subPixels = ArrayPool<byte>.Shared.Rent(mainPixels.Length);
                subSources = ArrayPool<byte>.Shared.Rent(mainSources.Length);
                subColors = ArrayPool<ushort>.Shared.Rent(mainSources.Length);
                subSources.AsSpan(0, mainSources.Length).Clear();
                var backdropColor = ReadColorValue(colorRam, 0);
                subColors.AsSpan(0, width * Height).Fill(backdropColor);

                for (var screenY = 0; screenY < Height; screenY++)
                {
                    var hardwareScanline = screenY + 1;
                    var scanlineRegisters = hardware.GetPpuRegistersForScanline(hardwareScanline).Span;
                    var brightness = scanlineRegisters[0] & 0x0F;
                    var forcedBlank = (scanlineRegisters[0] & 0x80) != 0;
                    var backdrop = ReadColor(backdropColor, brightness, forcedBlank);
                    FillScanline(subPixels, screenY, backdrop);

                    if (!forcedBlank)
                    {
                        DrawScreenLayers(
                            hardware,
                            hardwareScanline,
                            scanlineRegisters,
                            subScreen: true,
                            brightness,
                            subPixels,
                            subSources,
                            subColors,
                            videoRam,
                            colorRam,
                            interpolationProgress);
                    }
                }
            }

            for (var screenY = 0; screenY < Height; screenY++)
            {
                var hardwareScanline = screenY + 1;
                var scanlineRegisters = hardware.GetPpuRegistersForScanline(hardwareScanline).Span;
                if ((scanlineRegisters[0] & 0x80) != 0)
                {
                    continue;
                }

                var brightness = scanlineRegisters[0] & 0x0F;
                var scanlineColorMath = scanlineRegisters[0x31];
                var colorWindowSelect = scanlineRegisters[0x30];
                var mainClipMode = (colorWindowSelect >> 6) & 0x03;
                var subMaskMode = (colorWindowSelect >> 4) & 0x03;
                var useLineSubScreen = (colorWindowSelect & 0x02) != 0;
                var colorMathLayers = scanlineColorMath & 0x3F;
                var needsColorWindow = mainClipMode is 1 or 2
                    || (colorMathLayers != 0 && subMaskMode is 1 or 2);
                var subtract = (scanlineColorMath & 0x80) != 0;
                var applyHalf = (scanlineColorMath & 0x40) != 0;
                var fixedColor = colorMathLayers != 0
                    ? hardware.GetFixedColorForScanline(hardwareScanline)
                    : (ushort)0;
                var clippedColor = ReadColor(0, brightness, forcedBlank: false);
                var firstPixel = screenY * width;
                for (var x = 0; x < width; x++)
                {
                    var pixel = firstPixel + x;
                    var inColorWindow = needsColorWindow && IsColorWindowPixel(scanlineRegisters, x);
                    var clipMainPixel = mainClipMode == 3
                        || ((mainClipMode is 1 or 2) && IsColorMathWindowRegion(mainClipMode, inColorWindow));
                    var offset = pixel * BytesPerPixel;
                    if (clipMainPixel)
                    {
                        mainColors[pixel] = 0;
                        WriteColor(mainPixels, offset, clippedColor);
                    }

                    if (colorMathLayers == 0)
                    {
                        continue;
                    }

                    var source = mainSources[pixel];
                    var layerMask = source switch
                    {
                        0 => 0x20,
                        5 => 0x10,
                        6 => 0,
                        _ => 1 << (source - 1),
                    };
                    if ((colorMathLayers & layerMask) == 0)
                    {
                        continue;
                    }

                    if (subMaskMode != 0 && IsColorMathWindowRegion(subMaskMode, inColorWindow))
                    {
                        continue;
                    }

                    var subIsBackdrop = useLineSubScreen && (subSources is null || subSources[pixel] == 0);
                    var addend = useLineSubScreen && !subIsBackdrop
                        ? subColors![pixel]
                        : fixedColor;
                    mainColors[pixel] = ApplyColorMath(mainColors[pixel], addend, subtract, applyHalf && !subIsBackdrop);
                    WriteColor(mainPixels, offset, ReadColor(mainColors[pixel], brightness, forcedBlank: false));
                }
            }
        }
        finally
        {
            if (subPixels is not null)
            {
                ArrayPool<byte>.Shared.Return(subPixels);
            }

            if (subSources is not null)
            {
                ArrayPool<byte>.Shared.Return(subSources);
            }

            if (subColors is not null)
            {
                ArrayPool<ushort>.Shared.Return(subColors);
            }
        }
    }

    private static bool IsColorWindowPixel(ReadOnlySpan<byte> registers, int x)
    {
        var windowSettings = registers[0x25];
        var windowOneEnabled = (windowSettings & 0x20) != 0;
        var windowTwoEnabled = (windowSettings & 0x80) != 0;
        if (!windowOneEnabled && !windowTwoEnabled)
        {
            return false;
        }

        var windowOne = IsWindowPixel(
            x,
            registers[0x26],
            registers[0x27],
            inverted: (windowSettings & 0x10) != 0);
        var windowTwo = IsWindowPixel(
            x,
            registers[0x28],
            registers[0x29],
            inverted: (windowSettings & 0x40) != 0);
        if (!windowOneEnabled)
        {
            return windowTwo;
        }

        if (!windowTwoEnabled)
        {
            return windowOne;
        }

        return ApplyWindowLogic(windowOne, windowTwo, (registers[0x2B] >> 2) & 0x03);
    }

    private static bool IsColorMathWindowRegion(int regionMode, bool inColorWindow)
    {
        return regionMode switch
        {
            1 => !inColorWindow,
            2 => inColorWindow,
            3 => true,
            _ => false,
        };
    }

    private static bool IsLayerWindowed(ReadOnlySpan<byte> registers, int layer, bool subScreen, int x)
    {
        var screenWindowRegister = registers[subScreen ? 0x2F : 0x2E];
        if ((screenWindowRegister & (1 << layer)) == 0)
        {
            return false;
        }

        var selectorRegister = layer switch
        {
            0 or 1 => registers[0x23],
            2 or 3 => registers[0x24],
            _ => registers[0x25],
        };
        var selectorShift = layer switch
        {
            0 or 2 => 0,
            1 or 3 => 4,
            _ => 0,
        };
        var windowOneEnabled = (selectorRegister & (1 << (selectorShift + 1))) != 0;
        var windowTwoEnabled = (selectorRegister & (1 << (selectorShift + 3))) != 0;
        if (!windowOneEnabled && !windowTwoEnabled)
        {
            return false;
        }

        var windowOne = IsWindowPixel(
            x,
            registers[0x26],
            registers[0x27],
            (selectorRegister & (1 << selectorShift)) != 0);
        var windowTwo = IsWindowPixel(
            x,
            registers[0x28],
            registers[0x29],
            (selectorRegister & (1 << (selectorShift + 2))) != 0);
        if (!windowOneEnabled)
        {
            return windowTwo;
        }

        if (!windowTwoEnabled)
        {
            return windowOne;
        }

        var logicRegister = layer < 4 ? registers[0x2A] : registers[0x2B];
        var logicShift = layer < 4 ? layer * 2 : 0;
        return ApplyWindowLogic(windowOne, windowTwo, (logicRegister >> logicShift) & 0x03);
    }

    private static bool HasActiveLayerWindow(ReadOnlySpan<byte> registers, int layer, bool subScreen)
    {
        var screenWindowRegister = registers[subScreen ? 0x2F : 0x2E];
        if ((screenWindowRegister & (1 << layer)) == 0)
        {
            return false;
        }

        var selectorRegister = layer switch
        {
            0 or 1 => registers[0x23],
            2 or 3 => registers[0x24],
            _ => registers[0x25],
        };
        var selectorShift = layer switch
        {
            0 or 2 => 0,
            1 or 3 => 4,
            _ => 0,
        };
        var windowOneEnabled = (selectorRegister & (1 << (selectorShift + 1))) != 0;
        var windowTwoEnabled = (selectorRegister & (1 << (selectorShift + 3))) != 0;
        return windowOneEnabled || windowTwoEnabled;
    }

    private static bool IsWindowPixel(int x, int left, int right, bool inverted)
    {
        x += activeViewport.OriginX;
        // The hardware's full-width window also covers the extended sides.
        if (activeViewport.Width > Width && left <= right)
        {
            if (left == 0) left = Math.Min(0, activeViewport.OriginX);
            if (right == 255) right = Math.Max(255, activeViewport.OriginX + activeViewport.Width - 1);
        }
        var inside = left <= right && x >= left && x <= right;
        return inverted ? !inside : inside;
    }

    private static bool ApplyWindowLogic(bool windowOne, bool windowTwo, int logic)
    {
        return logic switch
        {
            1 => windowOne && windowTwo,
            2 => windowOne ^ windowTwo,
            3 => windowOne == windowTwo,
            _ => windowOne || windowTwo,
        };
    }

    private static ushort ApplyColorMath(ushort main, ushort addend, bool subtract, bool half)
    {
        if (!subtract)
        {
            if (!half)
            {
                var sum = (uint)(main + addend);
                var carry = (sum - ((main ^ addend) & 0x0421)) & 0x8420;
                return (ushort)((sum - carry) | (carry - (carry >> 5)));
            }

            return (ushort)((main + addend - ((main ^ addend) & 0x0421)) >> 1);
        }

        var difference = (uint)main - addend + 0x8420;
        var borrow = (difference - ((main ^ addend) & 0x8420)) & 0x8420;
        if (!half)
        {
            return (ushort)((difference - borrow) & (borrow - (borrow >> 5)));
        }

        return (ushort)((((difference - borrow) & (borrow - (borrow >> 5))) & 0x7BDE) >> 1);
    }

    private static ushort ReadColorValue(ReadOnlySpan<byte> colorRam, int colorIndex)
    {
        var address = (colorIndex * 2) & 0x1FF;
        return (ushort)(colorRam[address] | ((colorRam[address + 1] & 0x7F) << 8));
    }

    private static uint ReadColor(ushort color, int brightness, bool forcedBlank)
    {
        return forcedBlank ? 0xFF000000 : BrightnessColors[(brightness << 15) | (color & 0x7FFF)];
    }

    private static uint[] CreateBrightnessColors()
    {
        // There are only 32,768 SNES colors and 16 brightness levels. This table
        // replaces three channel expansions and divisions at every visible pixel.
        var colors = new uint[16 * 0x8000];
        for (var brightness = 0; brightness < 16; brightness++)
        {
            for (var color = 0; color < 0x8000; color++)
            {
                var red = ScaleChannel(color & 0x1F, brightness);
                var green = ScaleChannel((color >> 5) & 0x1F, brightness);
                var blue = ScaleChannel((color >> 10) & 0x1F, brightness);
                colors[(brightness << 15) | color] = 0xFF000000u | ((uint)blue << 16) | ((uint)green << 8) | (uint)red;
            }
        }
        return colors;
    }

    private static void DrawModeOneLayers(
        SnesHardware hardware,
        ReadOnlySpan<byte> registers,
        byte screenEnable,
        int screenY,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        Span<ushort> pixelColors,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam,
        float interpolationProgress)
    {
        var hardwareScanline = screenY + 1;
        var bg3HighPriority = (registers[0x05] & 0x08) != 0;
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 2, priority: 0, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        DrawObjectsIfEnabled(hardware, registers, screenEnable, priority: 0, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        if (!bg3HighPriority)
        {
            DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 2, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        }

        DrawObjectsIfEnabled(hardware, registers, screenEnable, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 1, priority: 0, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 0, priority: 0, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        DrawObjectsIfEnabled(hardware, registers, screenEnable, priority: 2, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 1, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 0, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        DrawObjectsIfEnabled(hardware, registers, screenEnable, priority: 3, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        if (bg3HighPriority)
        {
            DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 2, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        }
    }

    private static void DrawModeSevenLayers(
        SnesHardware hardware,
        int hardwareScanline,
        ReadOnlySpan<byte> registers,
        byte screenEnable,
        int screenY,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        Span<ushort> pixelColors,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam,
        float interpolationProgress)
    {
        if ((screenEnable & 0x10) != 0)
        {
            DrawObjects(hardware, registers, priority: 0, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
            DrawObjects(hardware, registers, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
            DrawObjects(hardware, registers, priority: 2, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        }

        if ((screenEnable & 0x01) != 0)
        {
            DrawModeSevenBackground(hardware, hardwareScanline, registers, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        }

        if ((screenEnable & 0x10) != 0)
        {
            DrawObjects(hardware, registers, priority: 3, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        }
    }

    private static void DrawModeSevenBackground(
        SnesHardware hardware,
        int hardwareScanline,
        ReadOnlySpan<byte> registers,
        int screenY,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        Span<ushort> pixelColors,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam)
    {
        var width = activeViewport.Width;
        var screenRegister = registers[subScreen ? 0x2D : 0x2C];
        if ((screenRegister & 0x01) == 0)
        {
            return;
        }

        var parameters = hardware.GetMode7ParametersForScanline(hardwareScanline).Span;
        var a = (short)parameters[0];
        var b = (short)parameters[1];
        var c = (short)parameters[2];
        var d = (short)parameters[3];
        var centerX = SignExtendModeSeven(parameters[4]);
        var centerY = SignExtendModeSeven(parameters[5]);
        var horizontalOffset = SignExtendModeSeven(parameters[6]);
        var verticalOffset = SignExtendModeSeven(parameters[7]);
        var mode7Settings = registers[0x1A];
        var transformedY = (mode7Settings & 0x02) != 0 ? 255 - screenY : screenY;
        var originX = ((a * ClipModeSevenCoordinate(horizontalOffset - centerX)) & ~63)
                + ((b * ClipModeSevenCoordinate(verticalOffset - centerY)) & ~63)
                + ((b * transformedY) & ~63)
                + (centerX << 8);
        var originY = ((c * ClipModeSevenCoordinate(horizontalOffset - centerX)) & ~63)
                + ((d * ClipModeSevenCoordinate(verticalOffset - centerY)) & ~63)
                + ((d * transformedY) & ~63)
                + (centerY << 8);
        var repeatMode = (mode7Settings >> 6) & 0x03;
        var horizontalDirection = (mode7Settings & 0x01) != 0 ? -1 : 1;
        var sourceX = originX + (a * (horizontalDirection < 0 ? 255 - activeViewport.OriginX : activeViewport.OriginX));
        var sourceY = originY + (c * (horizontalDirection < 0 ? 255 - activeViewport.OriginX : activeViewport.OriginX));
        var horizontalStepX = a * horizontalDirection;
        var horizontalStepY = c * horizontalDirection;
        var hasActiveWindow = HasActiveLayerWindow(registers, 0, subScreen);

        for (var screenX = 0; screenX < width; screenX++, sourceX += horizontalStepX, sourceY += horizontalStepY)
        {
            if (hasActiveWindow && IsLayerWindowed(registers, 0, subScreen, screenX))
            {
                continue;
            }

            var pixelX = sourceX >> 8;
            var pixelY = sourceY >> 8;
            var outsideTilemap = pixelX < 0 || pixelX >= 1024 || pixelY < 0 || pixelY >= 1024;
            if (outsideTilemap && repeatMode == 2)
            {
                continue;
            }

            var mapX = pixelX & 0x03FF;
            var mapY = pixelY & 0x03FF;
            var tileIndex = (mapY >> 3) * 128 + (mapX >> 3);
            var tile = outsideTilemap && repeatMode == 3
                ? 0
                : videoRam[tileIndex * 2];
            var characterIndex = tile * 64 + ((mapY & 7) * 8) + (mapX & 7);
            var colorIndex = videoRam[(characterIndex * 2) + 1];
            if (colorIndex == 0)
            {
                continue;
            }

            var colorValue = ReadColorValue(colorRam, colorIndex);
            var pixelIndex = (screenY * width) + screenX;
            WriteColor(rgbaPixels, pixelIndex * BytesPerPixel, ReadColor(colorValue, brightness, forcedBlank: false));
            pixelColors[pixelIndex] = colorValue;
            pixelSources[pixelIndex] = 1;
        }
    }

    private static int SignExtendModeSeven(ushort value)
    {
        var value13 = value & 0x1FFF;
        return (value13 & 0x1000) != 0 ? value13 - 0x2000 : value13;
    }

    private static int ClipModeSevenCoordinate(int value)
    {
        return (value & 0x2000) != 0 ? value | ~0x03FF : value & 0x03FF;
    }

    private static void DrawBackgroundIfEnabled(
        SnesHardware hardware,
        ReadOnlySpan<byte> registers,
        int hardwareScanline,
        byte screenEnable,
        int background,
        int priority,
        int screenY,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        Span<ushort> pixelColors,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam,
        float interpolationProgress)
    {
        if ((screenEnable & (1 << background)) == 0)
        {
            return;
        }

        DrawBackground(hardware, registers, hardwareScanline, background, mode: 1, priority, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
    }

    private static void DrawObjectsIfEnabled(
        SnesHardware hardware,
        ReadOnlySpan<byte> registers,
        byte screenEnable,
        int priority,
        int screenY,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        Span<ushort> pixelColors,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam,
        float interpolationProgress)
    {
        if ((screenEnable & 0x10) != 0)
        {
            DrawObjects(hardware, registers, priority, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        }
    }

    private static (int Small, int Large) GetObjectSizes(int sizeMode)
    {
        return sizeMode switch
        {
            0 => (8, 16),
            1 => (8, 32),
            2 => (8, 64),
            3 => (16, 32),
            4 => (16, 64),
            5 => (32, 64),
            6 => (16, 32),
            _ => (16, 32),
        };
    }

    private static void DrawBackground(
        SnesHardware hardware,
        ReadOnlySpan<byte> registers,
        int hardwareScanline,
        int background,
        int mode,
        int priority,
        int screenY,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        Span<ushort> pixelColors,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam,
        float interpolationProgress)
    {
        var width = activeViewport.Width;
        var hud = hardware.Widescreen?.IsSupported == true && width > Width && background == 2 && screenY < 32;
        var originX = hud ? VideoViewport.Widescreen.OriginX : activeViewport.OriginX;
        var mapRegister = registers[0x07 + background];
        var mapBase = (mapRegister & 0xFC) << 9;
        var mapSize = mapRegister & 0x03;
        var mapWidth = (mapSize & 0x01) != 0 ? 64 : 32;
        var mapHeight = (mapSize & 0x02) != 0 ? 64 : 32;
        var tileWidth = (registers[0x05] & (0x10 << background)) != 0 ? 16 : 8;
        var tileHeight = tileWidth;
        var pixelWidth = mapWidth * tileWidth;
        var pixelHeight = mapHeight * tileHeight;
        var offsets = hardware.GetBackgroundOffsetsForScanline(hardwareScanline).Span;
        var horizontalOffset = offsets[background] % pixelWidth;
        var verticalOffset = offsets[background + 4] % pixelHeight;
        var (horizontalDelta, verticalDelta) = hardware.GetBackgroundScrollDeltaForScanline(hardwareScanline, background);
        if (Math.Abs(horizontalDelta) > MaximumInterpolatedScrollDelta)
        {
            horizontalDelta = 0;
        }

        if (Math.Abs(verticalDelta) > MaximumInterpolatedScrollDelta)
        {
            verticalDelta = 0;
        }

        // Sample the nearest SNES pixel between the previous and current completed PPU
        // samples. Keeping each palette color intact avoids a soft trail at tile edges.
        var sampleShiftX = -horizontalDelta * (1f - interpolationProgress);
        var sampleShiftY = -verticalDelta * (1f - interpolationProgress);
        var characterBaseRegister = registers[0x0B + background / 2];
        var characterBase = ((background & 1) == 0
            ? characterBaseRegister & 0x0F
            : characterBaseRegister >> 4) * 0x2000;
        var bitsPerPixel = mode == 0 || (mode == 1 && background == 2) ? 2 : 4;
        var bytesPerTile = bitsPerPixel * 8;
        var scanlineRegisters = registers;
        var hasActiveWindow = HasActiveLayerWindow(scanlineRegisters, background, subScreen);
        var sampleY = (int)MathF.Round(
            screenY + verticalOffset + sampleShiftY,
            MidpointRounding.AwayFromZero);
        var tileShift = tileWidth == 8 ? 3 : 4;
        var worldY = sampleY & (pixelHeight - 1);
        var cellY = worldY >> tileShift;
        var rowY = worldY & (tileHeight - 1);
        var modeZeroPaletteBase = mode == 0 ? background * 32 : 0;
        var firstPixel = screenY * width;
        var tileCache = activeTileDecodeCache!;
        var sampleCache = backgroundSampleCache ??= new BackgroundSampleCache();
        var levelMap = activeWidescreen is not null && background == 0 && tileWidth == 8;
        var levelBaseX = levelMap ? activeWidescreen!.GetLevelXOffset(horizontalOffset) - horizontalOffset : 0;
        var sampleColumns = sampleCache.GetColumns(background, horizontalOffset + originX, pixelWidth, sampleShiftX, !levelMap);
        var cachedSegment = int.MinValue;
        var drawSegment = false;
        var horizontalFlip = false;
        var paletteBase = 0;
        ReadOnlySpan<byte> tileRow = default;
        for (var screenX = 0; screenX < width; screenX++)
        {
            if (hasActiveWindow && IsLayerWindowed(scanlineRegisters, background, subScreen, screenX))
            {
                continue;
            }

            if (hud && (uint)(screenX + originX) >= Width) continue;
            var worldX = sampleColumns[screenX];
            // The original viewport retains the live VRAM tile changes, including
            // bounce-block timing. Only the added sides bypass the streaming ring.
            var extendedColumn = levelMap && (uint)(screenX + originX) >= Width;
            var segment = ((worldX >> 3) * 2) + (extendedColumn ? 1 : 0);
            if (segment != cachedSegment)
            {
                // Map entries, palette selection and tile decoding are shared by
                // an eight-pixel row. Keep exact per-pixel scroll rounding at its edges.
                cachedSegment = segment;
                var mapIndex = GetMapByteIndex(mapBase, mapWidth, (worldX & (pixelWidth - 1)) >> tileShift, cellY);
                var entry = extendedColumn
                    ? activeWidescreen!.ReadForegroundEntry(levelBaseX + worldX, sampleY)
                    : videoRam[mapIndex] | (videoRam[mapIndex + 1] << 8);
                drawSegment = entry >= 0 && (priority < 0 || ((entry >> 13) & 1) == priority);
                if (drawSegment)
                {
                    horizontalFlip = (entry & 0x4000) != 0;
                    var localX = worldX & (tileWidth - 1);
                    var localY = rowY;
                    if (horizontalFlip) localX = tileWidth - 1 - localX;
                    if ((entry & 0x8000) != 0) localY = tileHeight - 1 - localY;
                    var tileNumber = entry & 0x03FF;
                    if (tileWidth == 16)
                        tileNumber += ((localY >> 3) * 16) + (localX >> 3);
                    var address = (characterBase + tileNumber * bytesPerTile) & 0xFFFF;
                    tileRow = tileCache.ReadRow(videoRam, address, bitsPerPixel, localY & 7);
                    paletteBase = modeZeroPaletteBase + (((entry >> 10) & 7) << bitsPerPixel);
                }
            }

            if (!drawSegment) continue;
            var localPixelX = worldX & 7;
            var paletteIndex = tileRow[horizontalFlip ? 7 - localPixelX : localPixelX];
            if (paletteIndex == 0) continue;
            var sampledColor = ReadColorValue(colorRam, paletteBase + paletteIndex);
            var pixelIndex = firstPixel + screenX;
            WriteColor(rgbaPixels, pixelIndex * BytesPerPixel, ReadColor(sampledColor, brightness, forcedBlank: false));
            pixelColors[pixelIndex] = sampledColor;
            pixelSources[pixelIndex] = (byte)(background + 1);
        }
    }

    private static int GetMapByteIndex(int mapBase, int mapWidth, int cellX, int cellY)
    {
        var quadrant = (cellX >> 5) + ((cellY >> 5) * (mapWidth >> 5));
        var cellInQuadrant = ((cellY & 31) * 32) + (cellX & 31);
        return (mapBase + (quadrant * 0x800) + (cellInQuadrant * 2)) & 0xFFFF;
    }

    private sealed class BackgroundSampleCache
    {
        private readonly int[] columns = new int[4 * VideoViewport.WidescreenWidth];
        private readonly (int Offset, int PixelWidth, float Shift, int Width, bool Wrap)[] keys = new (int, int, float, int, bool)[4];

        public ReadOnlySpan<int> GetColumns(int background, int offset, int pixelWidth, float shift, bool wrap)
        {
            var width = activeViewport.Width;
            var key = (offset, pixelWidth, shift, width, wrap);
            var samples = columns.AsSpan(background * VideoViewport.WidescreenWidth, width);
            if (keys[background] != key)
            {
                for (var x = 0; x < width; x++)
                {
                    var sample = (int)MathF.Round(x + offset + shift, MidpointRounding.AwayFromZero);
                    samples[x] = wrap ? sample & (pixelWidth - 1) : sample;
                }
                keys[background] = key;
            }
            return samples;
        }
    }

    private static int ReadTilePixel(
        ReadOnlySpan<byte> videoRam,
        int tileAddress,
        int bitsPerPixel,
        int x,
        int y)
    {
        var cache = activeTileDecodeCache;
        if (cache is not null)
        {
            return cache.ReadPixel(videoRam, tileAddress, bitsPerPixel, x, y);
        }

        var bit = 0x80 >> x;
        var colorIndex = 0;
        for (var plane = 0; plane < bitsPerPixel; plane++)
        {
            var planeAddress = tileAddress + ((plane / 2) * 16) + (y * 2) + (plane & 1);
            if ((videoRam[planeAddress & 0xFFFF] & bit) != 0)
            {
                colorIndex |= 1 << plane;
            }
        }

        return colorIndex;
    }

    private sealed class TileDecodeCache
    {
        private const int MaximumTileCount = TwoBppTileCount + FourBppTileCount;
        private const int MaximumTileByteCount = 32;
        private readonly byte[] sourceTileBytes = new byte[MaximumTileCount * MaximumTileByteCount];
        private readonly byte[] decodedPixels = new byte[MaximumTileCount * TilePixels];
        private readonly bool[] decodedTiles = new bool[MaximumTileCount];
        private readonly int[] checkedFrame = new int[MaximumTileCount];
        private int frameNumber;

        public void BeginFrame()
        {
            frameNumber++;
            if (frameNumber == int.MaxValue)
            {
                Array.Clear(checkedFrame);
                frameNumber = 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadPixel(ReadOnlySpan<byte> videoRam, int tileAddress, int bitsPerPixel, int x, int y)
        {
            var offset = GetTilePixelOffset(videoRam, tileAddress, bitsPerPixel);
            return decodedPixels[offset + (y * 8) + x];
        }

        public ReadOnlySpan<byte> ReadRow(ReadOnlySpan<byte> videoRam, int tileAddress, int bitsPerPixel, int y)
        {
            var offset = GetTilePixelOffset(videoRam, tileAddress, bitsPerPixel);
            return decodedPixels.AsSpan(offset + (y * 8), 8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetTilePixelOffset(ReadOnlySpan<byte> videoRam, int tileAddress, int bitsPerPixel)
        {
            var isTwoBpp = bitsPerPixel == 2;
            var tileIndex = (tileAddress >> (isTwoBpp ? 4 : 5)) & (isTwoBpp
                ? TwoBppTileCount - 1
                : FourBppTileCount - 1);
            if (!isTwoBpp)
            {
                tileIndex += TwoBppTileCount;
            }

            var pixelOffset = tileIndex * TilePixels;
            if (checkedFrame[tileIndex] != frameNumber)
            {
                checkedFrame[tileIndex] = frameNumber;
                var tileByteCount = bitsPerPixel * 8;
                var source = videoRam.Slice(tileAddress, tileByteCount);
                var sourceOffset = tileIndex * MaximumTileByteCount;
                var cachedSource = sourceTileBytes.AsSpan(sourceOffset, tileByteCount);
                if (!decodedTiles[tileIndex] || !source.SequenceEqual(cachedSource))
                {
                    source.CopyTo(cachedSource);
                    DecodeTile(videoRam, tileAddress, bitsPerPixel, decodedPixels.AsSpan(pixelOffset, TilePixels));
                    decodedTiles[tileIndex] = true;
                }
            }

            return pixelOffset;
        }

        private static void DecodeTile(
            ReadOnlySpan<byte> videoRam,
            int tileAddress,
            int bitsPerPixel,
            Span<byte> pixels)
        {
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    var bit = 0x80 >> x;
                    var colorIndex = 0;
                    for (var plane = 0; plane < bitsPerPixel; plane++)
                    {
                        var planeAddress = tileAddress + ((plane / 2) * 16) + (y * 2) + (plane & 1);
                        if ((videoRam[planeAddress & 0xFFFF] & bit) != 0)
                        {
                            colorIndex |= 1 << plane;
                        }
                    }

                    pixels[(y * 8) + x] = (byte)colorIndex;
                }
            }
        }
    }

    private static int ScaleChannel(int channel, int brightness)
    {
        var expanded = (channel << 3) | (channel >> 2);
        return expanded * brightness / 15;
    }

    private static void WriteColor(Span<byte> rgbaPixels, int index, uint color)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(rgbaPixels[index..], color | 0xFF000000);
    }

    private static void FillScanline(Span<byte> pixels, int scanline, uint color)
    {
        var width = activeViewport.Width;
        var nativeColor = BitConverter.IsLittleEndian ? color : BinaryPrimitives.ReverseEndianness(color);
        MemoryMarshal.Cast<byte, uint>(pixels.Slice(scanline * width * BytesPerPixel, width * BytesPerPixel)).Fill(nativeColor);
    }
}
