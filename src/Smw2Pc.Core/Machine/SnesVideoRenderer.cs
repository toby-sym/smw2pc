namespace Smw2Pc.Core.Machine;

public static class SnesVideoRenderer
{
    public const int Width = 256;
    public const int Height = 224;
    public const int BytesPerPixel = 4;

    public static void RenderFrame(SnesHardware hardware, Span<byte> rgbaPixels)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        if (rgbaPixels.Length < Width * Height * BytesPerPixel)
        {
            throw new ArgumentException("The frame buffer is too small for a 256x224 RGBA frame.", nameof(rgbaPixels));
        }

        var videoRam = hardware.VideoRam.Span;
        var colorRam = hardware.ColorRam.Span;
        var backdropColor = ReadColorValue(colorRam, 0);
        var pixelSources = new byte[Width * Height];
        var pixelColors = new ushort[Width * Height];

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
                    colorRam);
            }
        }

        ApplyColorMath(hardware, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
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
        ReadOnlySpan<byte> colorRam)
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
            DrawModeOneLayers(hardware, registers, screenEnable, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
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
                    colorRam);
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
        ReadOnlySpan<byte> colorRam)
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

        if (useSubScreen)
        {
            subPixels = new byte[mainPixels.Length];
            subSources = new byte[mainSources.Length];
            subColors = new ushort[mainSources.Length];
            var backdropColor = ReadColorValue(colorRam, 0);
            for (var pixel = 0; pixel < Width * Height; pixel++)
            {
                subColors![pixel] = backdropColor;
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
                        colorRam);
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
        ReadOnlySpan<byte> colorRam)
    {
        var hardwareScanline = screenY + 1;
        var bg3HighPriority = (registers[0x05] & 0x08) != 0;
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 2, priority: 0, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        DrawObjectsIfEnabled(hardware, registers, screenEnable, priority: 0, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        if (!bg3HighPriority)
        {
            DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 2, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        }

        DrawObjectsIfEnabled(hardware, registers, screenEnable, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 1, priority: 0, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 0, priority: 0, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        DrawObjectsIfEnabled(hardware, registers, screenEnable, priority: 2, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 1, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 0, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        DrawObjectsIfEnabled(hardware, registers, screenEnable, priority: 3, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
        if (bg3HighPriority)
        {
            DrawBackgroundIfEnabled(hardware, registers, hardwareScanline, screenEnable, 2, priority: 1, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
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
        ReadOnlySpan<byte> colorRam)
    {
        if ((screenEnable & (1 << background)) == 0)
        {
            return;
        }

        DrawBackground(hardware, registers, hardwareScanline, background, mode: 1, priority, screenY, subScreen, brightness, rgbaPixels, pixelSources, pixelColors, videoRam, colorRam);
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
        ReadOnlySpan<byte> colorRam)
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
        var characterBaseRegister = registers[0x0B + background / 2];
        var characterBase = ((background & 1) == 0
            ? characterBaseRegister & 0x0F
            : characterBaseRegister >> 4) * 0x2000;
        var bitsPerPixel = mode == 0 || (mode == 1 && background == 2) ? 2 : 4;
        var bytesPerTile = bitsPerPixel * 8;
        var scanlineRegisters = registers;
        var worldY = (screenY + verticalOffset) % pixelHeight;
        var cellY = worldY / tileHeight;
        var localY = worldY % tileHeight;
        for (var screenX = 0; screenX < Width; screenX++)
        {
            if (IsLayerWindowed(scanlineRegisters, background, subScreen, screenX))
            {
                continue;
            }

            var worldX = (screenX + horizontalOffset) % pixelWidth;
            var cellX = worldX / tileWidth;
            var localX = worldX % tileWidth;
            var pixelLocalY = localY;
            var mapIndex = GetMapByteIndex(mapBase, mapWidth, cellX, cellY);
            var tileEntry = (ushort)(videoRam[mapIndex] | (videoRam[mapIndex + 1] << 8));
            if (priority >= 0 && ((tileEntry & 0x2000) != 0) != (priority != 0))
            {
                continue;
            }

            var horizontalFlip = (tileEntry & 0x4000) != 0;
            var verticalFlip = (tileEntry & 0x8000) != 0;
            if (horizontalFlip)
            {
                localX = tileWidth - 1 - localX;
            }

            if (verticalFlip)
            {
                pixelLocalY = tileHeight - 1 - pixelLocalY;
            }

            var tileNumber = tileEntry & 0x03FF;
            if (tileWidth == 16)
            {
                tileNumber += (pixelLocalY / 8 * 16) + (localX / 8);
                localX &= 7;
                pixelLocalY &= 7;
            }

            var tileAddress = (characterBase + (tileNumber * bytesPerTile)) & 0xFFFF;
            var paletteIndex = ReadTilePixel(videoRam, tileAddress, bitsPerPixel, localX, pixelLocalY);
            if (paletteIndex == 0)
            {
                continue;
            }

            var paletteGroup = (tileEntry >> 10) & 0x07;
            var modeZeroPaletteBase = mode == 0 ? background * 32 : 0;
            var colorIndex = modeZeroPaletteBase + (paletteGroup * (1 << bitsPerPixel)) + paletteIndex;
            var colorValue = ReadColorValue(colorRam, colorIndex);
            var color = ReadColor(colorValue, brightness, forcedBlank: false);
            var pixelIndex = (screenY * Width) + screenX;
            WriteColor(rgbaPixels, pixelIndex * BytesPerPixel, color);
            pixelColors[pixelIndex] = colorValue;
            pixelSources[pixelIndex] = (byte)(background + 1);
        }
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
