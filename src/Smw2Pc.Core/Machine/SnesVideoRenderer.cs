using System.Buffers;

namespace Smw2Pc.Core.Machine;

public static class SnesVideoRenderer
{
    public const int Width = 256;
    public const int Height = 224;
    public const int BytesPerPixel = 4;
    private const int MaximumInterpolatedScrollDelta = 16;

    public static void RenderFrame(SnesHardware hardware, Span<byte> rgbaPixels, float interpolationProgress = 1f)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        var framePixelCount = Width * Height;
        var frameByteCount = framePixelCount * BytesPerPixel;
        if (rgbaPixels.Length < frameByteCount)
        {
            throw new ArgumentException("The frame buffer is too small for a 256x224 RGBA frame.", nameof(rgbaPixels));
        }

        interpolationProgress = float.IsFinite(interpolationProgress)
            ? Math.Clamp(interpolationProgress, 0f, 1f)
            : 1f;
        var scrollInterpolationProgress = hardware.HasPreviousPpuFrameScrollOffsets
            ? interpolationProgress
            : 1f;
        var videoRam = hardware.VideoRam.Span;
        var colorRam = hardware.ColorRam.Span;
        var backdropColor = ReadColorValue(colorRam, 0);
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
                var firstPixel = screenY * Width;
                for (var screenX = 0; screenX < Width; screenX++)
                {
                    var pixel = firstPixel + screenX;
                    WriteColor(rgbaPixels, pixel * BytesPerPixel, backdrop);
                    pixelColors[pixel] = backdropColor;
                }

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
            if (interpolationProgress < 1f && hardware.HasPreviousPpuFrameScrollOffsets)
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
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixelSourcesBuffer);
            ArrayPool<ushort>.Shared.Return(pixelColorsBuffer);
        }
    }

    private static void InterpolateCameraScroll(
        SnesHardware hardware,
        ReadOnlySpan<byte> sourcePixels,
        Span<byte> outputPixels,
        ReadOnlySpan<byte> pixelSources,
        float interpolationProgress)
    {
        var previousFrameWeight = 1f - interpolationProgress;
        if (previousFrameWeight <= 0f)
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

                horizontalShifts[background] = -horizontal * previousFrameWeight;
                verticalShifts[background] = -vertical * previousFrameWeight;
            }

            for (var screenX = 0; screenX < Width; screenX++)
            {
                var pixel = (screenY * Width) + screenX;
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
                var x0 = (int)MathF.Floor(sampleX);
                var y0 = (int)MathF.Floor(sampleY);
                var x1 = x0 + 1;
                var y1 = y0 + 1;
                var fractionX = sampleX - x0;
                var fractionY = sampleY - y0;
                var outputOffset = pixel * BytesPerPixel;
                var topLeft = GetBackgroundSampleOffset(x0, y0, pixelSources, outputOffset);
                var topRight = GetBackgroundSampleOffset(x1, y0, pixelSources, outputOffset);
                var bottomLeft = GetBackgroundSampleOffset(x0, y1, pixelSources, outputOffset);
                var bottomRight = GetBackgroundSampleOffset(x1, y1, pixelSources, outputOffset);

                for (var channel = 0; channel < BytesPerPixel; channel++)
                {
                    var top = Lerp(
                        sourcePixels[topLeft + channel],
                        sourcePixels[topRight + channel],
                        fractionX);
                    var bottom = Lerp(
                        sourcePixels[bottomLeft + channel],
                        sourcePixels[bottomRight + channel],
                        fractionX);
                    outputPixels[outputOffset + channel] = (byte)Math.Clamp(
                        (int)MathF.Round(Lerp(top, bottom, fractionY)),
                        0,
                        byte.MaxValue);
                }
            }
        }
    }

    private static int GetBackgroundSampleOffset(
        int x,
        int y,
        ReadOnlySpan<byte> pixelSources,
        int fallbackOffset)
    {
        if ((uint)x >= Width || (uint)y >= Height)
        {
            return fallbackOffset;
        }

        var pixel = (y * Width) + x;
        // The sample is already inside the background image. Allow it to cross between
        // visible background layers so their shared edges move continuously. Object
        // pixels remain fixed at the current frame to avoid smearing sprites.
        return pixelSources[pixel] <= 4 ? pixel * BytesPerPixel : fallbackOffset;
    }

    private static float Lerp(float start, float end, float amount)
    {
        return start + ((end - start) * amount);
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
        DrawModeSevenLayers(hardware, hardwareScanline, registers, screenEnable, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
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
                DrawObjects(hardware, registers, priority: -1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
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
        var useSubScreen = false;
        for (var scanline = 0; scanline < Height; scanline++)
        {
            var scanlineRegisters = hardware.GetPpuRegistersForScanline(scanline + 1).Span;
            if ((scanlineRegisters[0x30] & 0x02) != 0 && (scanlineRegisters[0x31] & 0x3F) != 0)
            {
                useSubScreen = true;
                break;
            }
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
                for (var pixel = 0; pixel < Width * Height; pixel++)
                {
                    subColors[pixel] = backdropColor;
                }

                for (var screenY = 0; screenY < Height; screenY++)
                {
                    var hardwareScanline = screenY + 1;
                    var scanlineRegisters = hardware.GetPpuRegistersForScanline(hardwareScanline).Span;
                    var brightness = scanlineRegisters[0] & 0x0F;
                    var forcedBlank = (scanlineRegisters[0] & 0x80) != 0;
                    var backdrop = ReadColor(backdropColor, brightness, forcedBlank);
                    var firstPixel = screenY * Width;
                    for (var screenX = 0; screenX < Width; screenX++)
                    {
                        WriteColor(subPixels, (firstPixel + screenX) * BytesPerPixel, backdrop);
                    }

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

            for (var pixel = 0; pixel < Width * Height; pixel++)
            {
                var x = pixel % Width;
                var scanline = pixel / Width;
                var hardwareScanline = scanline + 1;
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
                var inColorWindow = IsColorWindowPixel(scanlineRegisters, x);
                var offset = pixel * BytesPerPixel;
                if (IsColorMathWindowRegion(mainClipMode, inColorWindow))
                {
                    mainColors[pixel] = 0;
                    WriteColor(mainPixels, offset, ReadColor(0, brightness, forcedBlank: false));
                }

                if ((scanlineColorMath & 0x3F) == 0)
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
                if ((scanlineColorMath & layerMask) == 0)
                {
                    continue;
                }

                var subIsTransparent = IsColorMathWindowRegion(subMaskMode, inColorWindow);
                if (subIsTransparent)
                {
                    continue;
                }

                var subIsBackdrop = useLineSubScreen && (subSources is null || subSources[pixel] == 0);
                var addend = useLineSubScreen && !subIsBackdrop
                    ? subColors![pixel]
                    : hardware.GetFixedColorForScanline(hardwareScanline);
                var subtract = (scanlineColorMath & 0x80) != 0;
                var applyHalf = (scanlineColorMath & 0x40) != 0 && !subIsBackdrop;
                mainColors[pixel] = ApplyColorMath(mainColors[pixel], addend, subtract, applyHalf);
                WriteColor(mainPixels, offset, ReadColor(mainColors[pixel], brightness, forcedBlank: false));
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

    private static bool IsWindowPixel(int x, int left, int right, bool inverted)
    {
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
        if (forcedBlank)
        {
            return 0xFF000000;
        }

        var red = ScaleChannel(color & 0x1F, brightness);
        var green = ScaleChannel((color >> 5) & 0x1F, brightness);
        var blue = ScaleChannel((color >> 10) & 0x1F, brightness);
        return 0xFF000000u | ((uint)blue << 16) | ((uint)green << 8) | (uint)red;
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
        DrawObjectsIfEnabled(hardware, registers, screenEnable, priority: 0, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        if (!bg3HighPriority)
        {
            DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 2, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        }

        DrawObjectsIfEnabled(hardware, registers, screenEnable, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 1, priority: 0, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 0, priority: 0, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        DrawObjectsIfEnabled(hardware, registers, screenEnable, priority: 2, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 1, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 0, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam, interpolationProgress);
        DrawObjectsIfEnabled(hardware, registers, screenEnable, priority: 3, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
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
        ReadOnlySpan<byte> colorRam)
    {
        if ((screenEnable & 0x10) != 0)
        {
            DrawObjects(hardware, registers, priority: 0, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
            DrawObjects(hardware, registers, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
            DrawObjects(hardware, registers, priority: 2, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        }

        if ((screenEnable & 0x01) != 0)
        {
            DrawModeSevenBackground(hardware, hardwareScanline, registers, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        }

        if ((screenEnable & 0x10) != 0)
        {
            DrawObjects(hardware, registers, priority: 3, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
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

        for (var screenX = 0; screenX < Width; screenX++)
        {
            if (IsLayerWindowed(registers, 0, subScreen, screenX))
            {
                continue;
            }

            var transformedX = (mode7Settings & 0x01) != 0 ? 255 - screenX : screenX;
            var pixelX = (originX + (a * transformedX)) >> 8;
            var pixelY = (originY + (c * transformedX)) >> 8;
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
            var pixelIndex = (screenY * Width) + screenX;
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
        ReadOnlySpan<byte> colorRam)
    {
        if ((screenEnable & 0x10) != 0)
        {
            DrawObjects(hardware, registers, priority, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        }
    }

    private static void DrawObjects(
        SnesHardware hardware,
        ReadOnlySpan<byte> registers,
        int priority,
        int targetScanline,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        Span<ushort> pixelColors,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam)
    {
        var objectRam = hardware.ObjectAttributeMemory.Span;
        var objectSizeMode = (registers[1] >> 5) & 0x07;
        var tileBase = (registers[1] & 0x07) * 0x4000;
        var tileNameSelectOffset = (((registers[1] >> 3) & 0x03) + 1) * 0x2000;
        var (smallSize, largeSize) = GetObjectSizes(objectSizeMode);

        for (var objectIndex = 127; objectIndex >= 0; objectIndex--)
        {
            var objectAddress = objectIndex * 4;
            var x = (int)objectRam[objectAddress];
            var y = objectRam[objectAddress + 1];
            var tile = objectRam[objectAddress + 2];
            var attributes = objectRam[objectAddress + 3];
            if (priority >= 0 && ((attributes >> 4) & 0x03) != priority)
            {
                continue;
            }

            var highBits = (objectRam[512 + (objectIndex / 4)] >> ((objectIndex & 3) * 2)) & 0x03;
            x |= (highBits & 1) << 8;
            var large = (highBits & 2) != 0;
            var objectSize = large ? largeSize : smallSize;
            if (x >= 256)
            {
                x -= 512;
            }

            var palette = (attributes >> 1) & 0x07;
            var horizontalFlip = (attributes & 0x40) != 0;
            var verticalFlip = (attributes & 0x80) != 0;

            var objectY = (targetScanline - y) & 0xFF;
            if (objectY >= objectSize)
            {
                continue;
            }

            var sourceY = verticalFlip ? objectSize - 1 - objectY : objectY;
            for (var objectX = 0; objectX < objectSize; objectX++)
            {
                var screenX = x + objectX;
                if ((uint)screenX >= Width)
                {
                    continue;
                }

                if (IsLayerWindowed(registers, 4, subScreen, screenX))
                {
                    continue;
                }

                var sourceX = horizontalFlip ? objectSize - 1 - objectX : objectX;
                var tileNumber = (tile + ((sourceY / 8) * 16) + (sourceX / 8)) & 0xFF;
                var objectTileBase = tileBase + ((attributes & 0x01) != 0 ? tileNameSelectOffset : 0);
                var tileAddress = (objectTileBase + (tileNumber * 32)) & 0xFFFF;
                var colorIndex = ReadTilePixel(videoRam, tileAddress, 4, sourceX & 7, sourceY & 7);
                if (colorIndex == 0)
                {
                    continue;
                }

                var colorValue = ReadColorValue(colorRam, 128 + (palette * 16) + colorIndex);
                var color = ReadColor(colorValue, brightness, forcedBlank: false);
                var pixelIndex = (targetScanline * Width) + screenX;
                WriteColor(rgbaPixels, pixelIndex * BytesPerPixel, color);
                pixelColors[pixelIndex] = colorValue;
                pixelSources[pixelIndex] = palette >= 4 ? (byte)5 : (byte)6;
            }
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

        var sampleShiftX = -horizontalDelta * (1f - interpolationProgress);
        var sampleShiftY = -verticalDelta * (1f - interpolationProgress);
        var characterBaseRegister = registers[0x0B + background / 2];
        var characterBase = ((background & 1) == 0
            ? characterBaseRegister & 0x0F
            : characterBaseRegister >> 4) * 0x2000;
        var bitsPerPixel = mode == 0 || (mode == 1 && background == 2) ? 2 : 4;
        var bytesPerTile = bitsPerPixel * 8;
        var scanlineRegisters = registers;
        for (var screenX = 0; screenX < Width; screenX++)
        {
            if (IsLayerWindowed(scanlineRegisters, background, subScreen, screenX))
            {
                continue;
            }

            var sampleX = screenX + horizontalOffset + sampleShiftX;
            var sampleY = screenY + verticalOffset + sampleShiftY;
            var x0 = (int)MathF.Floor(sampleX);
            var y0 = (int)MathF.Floor(sampleY);
            var fractionX = sampleX - x0;
            var fractionY = sampleY - y0;
            var pixelIndex = (screenY * Width) + screenX;
            var destination = pixelColors[pixelIndex];
            var destinationRed = destination & 0x1F;
            var destinationGreen = (destination >> 5) & 0x1F;
            var destinationBlue = (destination >> 10) & 0x1F;
            var coverage = 0f;
            var red = 0f;
            var green = 0f;
            var blue = 0f;

            AccumulateBackgroundSample(
                x0, y0, (1f - fractionX) * (1f - fractionY),
                pixelWidth, pixelHeight, tileWidth, tileHeight, mapBase, mapWidth, characterBase,
                bytesPerTile, bitsPerPixel, priority, mode, background, videoRam, colorRam,
                ref coverage, ref red, ref green, ref blue);
            if (fractionX > 0f)
            {
                AccumulateBackgroundSample(
                    x0 + 1, y0, fractionX * (1f - fractionY),
                    pixelWidth, pixelHeight, tileWidth, tileHeight, mapBase, mapWidth, characterBase,
                    bytesPerTile, bitsPerPixel, priority, mode, background, videoRam, colorRam,
                    ref coverage, ref red, ref green, ref blue);
            }

            if (fractionY > 0f)
            {
                AccumulateBackgroundSample(
                    x0, y0 + 1, (1f - fractionX) * fractionY,
                    pixelWidth, pixelHeight, tileWidth, tileHeight, mapBase, mapWidth, characterBase,
                    bytesPerTile, bitsPerPixel, priority, mode, background, videoRam, colorRam,
                    ref coverage, ref red, ref green, ref blue);
                if (fractionX > 0f)
                {
                    AccumulateBackgroundSample(
                        x0 + 1, y0 + 1, fractionX * fractionY,
                        pixelWidth, pixelHeight, tileWidth, tileHeight, mapBase, mapWidth, characterBase,
                        bytesPerTile, bitsPerPixel, priority, mode, background, videoRam, colorRam,
                        ref coverage, ref red, ref green, ref blue);
                }
            }

            if (coverage <= 0f)
            {
                continue;
            }

            var inverseCoverage = 1f - coverage;
            var colorValue = (ushort)(
                Math.Clamp((int)MathF.Round((red + (destinationRed * inverseCoverage))), 0, 31)
                | (Math.Clamp((int)MathF.Round((green + (destinationGreen * inverseCoverage))), 0, 31) << 5)
                | (Math.Clamp((int)MathF.Round((blue + (destinationBlue * inverseCoverage))), 0, 31) << 10));
            WriteColor(rgbaPixels, pixelIndex * BytesPerPixel, ReadColor(colorValue, brightness, forcedBlank: false));
            pixelColors[pixelIndex] = colorValue;
            pixelSources[pixelIndex] = (byte)(background + 1);

        }
    }

    private static void AccumulateBackgroundSample(
        int worldX,
        int worldY,
        float weight,
        int pixelWidth,
        int pixelHeight,
        int tileWidth,
        int tileHeight,
        int mapBase,
        int mapWidth,
        int characterBase,
        int bytesPerTile,
        int bitsPerPixel,
        int priority,
        int mode,
        int background,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam,
        ref float coverage,
        ref float red,
        ref float green,
        ref float blue)
    {
        if (weight <= 0f || !TrySampleBackgroundColor(
            worldX,
            worldY,
            pixelWidth,
            pixelHeight,
            tileWidth,
            tileHeight,
            mapBase,
            mapWidth,
            characterBase,
            bytesPerTile,
            bitsPerPixel,
            priority,
            mode,
            background,
            videoRam,
            colorRam,
            out var sampledColor))
        {
            return;
        }

        coverage += weight;
        red += (sampledColor & 0x1F) * weight;
        green += ((sampledColor >> 5) & 0x1F) * weight;
        blue += ((sampledColor >> 10) & 0x1F) * weight;
    }

    private static bool TrySampleBackgroundColor(
        int worldX,
        int worldY,
        int pixelWidth,
        int pixelHeight,
        int tileWidth,
        int tileHeight,
        int mapBase,
        int mapWidth,
        int characterBase,
        int bytesPerTile,
        int bitsPerPixel,
        int priority,
        int mode,
        int background,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam,
        out ushort colorValue)
    {
        worldX %= pixelWidth;
        worldY %= pixelHeight;
        if (worldX < 0)
        {
            worldX += pixelWidth;
        }

        if (worldY < 0)
        {
            worldY += pixelHeight;
        }

        var cellX = worldX / tileWidth;
        var cellY = worldY / tileHeight;
        var localX = worldX % tileWidth;
        var localY = worldY % tileHeight;
        var mapIndex = GetMapByteIndex(mapBase, mapWidth, cellX, cellY);
        var tileEntry = (ushort)(videoRam[mapIndex] | (videoRam[mapIndex + 1] << 8));
        if (priority >= 0 && ((tileEntry & 0x2000) != 0) != (priority != 0))
        {
            colorValue = 0;
            return false;
        }

        if ((tileEntry & 0x4000) != 0)
        {
            localX = tileWidth - 1 - localX;
        }

        if ((tileEntry & 0x8000) != 0)
        {
            localY = tileHeight - 1 - localY;
        }

        var tileNumber = tileEntry & 0x03FF;
        if (tileWidth == 16)
        {
            tileNumber += ((localY / 8) * 16) + (localX / 8);
            localX &= 7;
            localY &= 7;
        }

        var tileAddress = (characterBase + (tileNumber * bytesPerTile)) & 0xFFFF;
        var paletteIndex = ReadTilePixel(videoRam, tileAddress, bitsPerPixel, localX, localY);
        if (paletteIndex == 0)
        {
            colorValue = 0;
            return false;
        }

        var paletteGroup = (tileEntry >> 10) & 0x07;
        var modeZeroPaletteBase = mode == 0 ? background * 32 : 0;
        var colorIndex = modeZeroPaletteBase + (paletteGroup * (1 << bitsPerPixel)) + paletteIndex;
        colorValue = ReadColorValue(colorRam, colorIndex);
        return true;
    }

    private static int GetMapByteIndex(int mapBase, int mapWidth, int cellX, int cellY)
    {
        var quadrant = (cellX / 32) + ((cellY / 32) * (mapWidth / 32));
        var cellInQuadrant = ((cellY & 31) * 32) + (cellX & 31);
        return (mapBase + (quadrant * 0x800) + (cellInQuadrant * 2)) & 0xFFFF;
    }

    private static int ReadTilePixel(
        ReadOnlySpan<byte> videoRam,
        int tileAddress,
        int bitsPerPixel,
        int x,
        int y)
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

        return colorIndex;
    }

    private static int ScaleChannel(int channel, int brightness)
    {
        var expanded = (channel << 3) | (channel >> 2);
        return expanded * brightness / 15;
    }

    private static void WriteColor(Span<byte> rgbaPixels, int index, uint color)
    {
        rgbaPixels[index] = (byte)color;
        rgbaPixels[index + 1] = (byte)(color >> 8);
        rgbaPixels[index + 2] = (byte)(color >> 16);
        rgbaPixels[index + 3] = 0xFF;
    }
}
