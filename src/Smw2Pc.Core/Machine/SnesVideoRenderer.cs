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

        var registers = hardware.PpuRegisters.Span;
        var videoRam = hardware.VideoRam.Span;
        var colorRam = hardware.ColorRam.Span;
        var forcedBlank = (registers[0] & 0x80) != 0;
        var brightness = registers[0] & 0x0F;
        var screenEnable = registers[0x2C];
        var mode = registers[0x05] & 0x07;
        var backdrop = ReadColor(colorRam, 0, brightness, forcedBlank);
        var pixelSources = new byte[Width * Height];

        for (var pixel = 0; pixel < Width * Height; pixel++)
        {
            WriteColor(rgbaPixels, pixel * BytesPerPixel, backdrop);
        }

        if (forcedBlank)
        {
            return;
        }

        DrawScreenLayers(
            hardware,
            screenEnable,
            mode,
            subScreen: false,
            brightness,
            rgbaPixels,
            pixelSources,
            videoRam,
            colorRam);

        ApplyColorMath(hardware, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
    }

    private static void DrawScreenLayers(
        SnesHardware hardware,
        byte screenEnable,
        int mode,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam)
    {
        if (mode == 1)
        {
            DrawModeOneLayers(hardware, screenEnable, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
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
                    background,
                    mode,
                    priority: -1,
                    subScreen,
                    brightness,
                    rgbaPixels,
                    pixelSources,
                    videoRam,
                    colorRam);
            }

            if ((screenEnable & 0x10) != 0)
            {
                DrawObjects(hardware, priority: -1, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
            }
        }
    }

    private static void ApplyColorMath(
        SnesHardware hardware,
        int brightness,
        Span<byte> mainPixels,
        ReadOnlySpan<byte> mainSources,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam)
    {
        var registers = hardware.PpuRegisters.Span;
        var colorMath = registers[0x31];
        var useSubScreen = (registers[0x30] & 0x02) != 0;
        byte[]? subPixels = null;
        byte[]? subSources = null;

        if ((colorMath & 0x3F) != 0 && useSubScreen)
        {
            subPixels = new byte[mainPixels.Length];
            subSources = new byte[mainSources.Length];
            var backdrop = ReadColor(colorRam, 0, brightness, forcedBlank: false);
            for (var pixel = 0; pixel < Width * Height; pixel++)
            {
                WriteColor(subPixels, pixel * BytesPerPixel, backdrop);
            }

            DrawScreenLayers(
                hardware,
                registers[0x2D],
                registers[0x05] & 0x07,
                subScreen: true,
                brightness,
                subPixels,
                subSources,
                videoRam,
                colorRam);
        }

        for (var pixel = 0; pixel < Width * Height; pixel++)
        {
            var x = pixel % Width;
            var scanline = pixel / Width;
            var scanlineRegisters = hardware.GetPpuRegistersForScanline(scanline).Span;
            var scanlineColorMath = scanlineRegisters[0x31];
            var colorWindowSelect = scanlineRegisters[0x30];
            var mainClipMode = (colorWindowSelect >> 6) & 0x03;
            var subMaskMode = (colorWindowSelect >> 4) & 0x03;
            var useLineSubScreen = (colorWindowSelect & 0x02) != 0;
            var inColorWindow = IsColorWindowPixel(scanlineRegisters, x);
            var offset = pixel * BytesPerPixel;
            if (IsColorMathWindowRegion(mainClipMode, inColorWindow))
            {
                mainPixels[offset] = 0;
                mainPixels[offset + 1] = 0;
                mainPixels[offset + 2] = 0;
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

            var subIsTransparent = useLineSubScreen && IsColorMathWindowRegion(subMaskMode, inColorWindow);
            if (subIsTransparent)
            {
                continue;
            }

            var addend = useLineSubScreen
                ? (uint)(subPixels![offset] | (subPixels[offset + 1] << 8) | (subPixels[offset + 2] << 16))
                : ReadColorWord(hardware.GetFixedColorForScanline(scanline), brightness);
            var mainRed = mainPixels[offset];
            var mainGreen = mainPixels[offset + 1];
            var mainBlue = mainPixels[offset + 2];
            var addRed = (byte)addend;
            var addGreen = (byte)(addend >> 8);
            var addBlue = (byte)(addend >> 16);
            var subtract = (scanlineColorMath & 0x80) != 0;
            var applyHalf = (scanlineColorMath & 0x40) != 0;

            mainPixels[offset] = ApplyMathChannel(mainRed, addRed, subtract, applyHalf);
            mainPixels[offset + 1] = ApplyMathChannel(mainGreen, addGreen, subtract, applyHalf);
            mainPixels[offset + 2] = ApplyMathChannel(mainBlue, addBlue, subtract, applyHalf);
        }
    }

    private static bool IsColorWindowPixel(ReadOnlySpan<byte> registers, int x)
    {
        var windowSettings = registers[0x25];
        var windowOneEnabled = (windowSettings & 0x10) != 0;
        var windowTwoEnabled = (windowSettings & 0x40) != 0;
        if (!windowOneEnabled && !windowTwoEnabled)
        {
            return false;
        }

        var windowOne = IsWindowPixel(
            x,
            registers[0x26],
            registers[0x27],
            inverted: (windowSettings & 0x20) != 0);
        var windowTwo = IsWindowPixel(
            x,
            registers[0x28],
            registers[0x29],
            inverted: (windowSettings & 0x80) != 0);
        if (!windowOneEnabled)
        {
            return windowTwo;
        }

        if (!windowTwoEnabled)
        {
            return windowOne;
        }

        return ApplyWindowLogic(windowOne, windowTwo, (registers[0x2B] >> 4) & 0x03);
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
        var windowOneEnabled = (selectorRegister & (1 << selectorShift)) != 0;
        var windowTwoEnabled = (selectorRegister & (1 << (selectorShift + 2))) != 0;
        if (!windowOneEnabled && !windowTwoEnabled)
        {
            return false;
        }

        var windowOne = IsWindowPixel(
            x,
            registers[0x26],
            registers[0x27],
            (selectorRegister & (1 << (selectorShift + 1))) != 0);
        var windowTwo = IsWindowPixel(
            x,
            registers[0x28],
            registers[0x29],
            (selectorRegister & (1 << (selectorShift + 3))) != 0);
        if (!windowOneEnabled)
        {
            return windowTwo;
        }

        if (!windowTwoEnabled)
        {
            return windowOne;
        }

        return ApplyWindowLogic(windowOne, windowTwo, (registers[0x2A] >> (layer * 2)) & 0x03);
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

    private static byte ApplyMathChannel(byte main, byte addend, bool subtract, bool half)
    {
        var result = subtract
            ? Math.Max(0, main - addend)
            : Math.Min(byte.MaxValue, main + addend);
        return (byte)(half ? result / 2 : result);
    }

    private static uint ReadColorWord(ushort color, int brightness)
    {
        var red = ScaleChannel(color & 0x1F, brightness);
        var green = ScaleChannel((color >> 5) & 0x1F, brightness);
        var blue = ScaleChannel((color >> 10) & 0x1F, brightness);
        return 0xFF000000u | ((uint)blue << 16) | ((uint)green << 8) | (uint)red;
    }

    private static void DrawModeOneLayers(
        SnesHardware hardware,
        byte screenEnable,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam)
    {
        var bg3HighPriority = (hardware.PpuRegisters.Span[0x05] & 0x08) != 0;
        DrawBackgroundIfEnabled(hardware, screenEnable, 2, priority: 0, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
        DrawObjectsIfEnabled(hardware, screenEnable, priority: 0, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
        if (!bg3HighPriority)
        {
            DrawBackgroundIfEnabled(hardware, screenEnable, 2, priority: 1, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
        }

        DrawObjectsIfEnabled(hardware, screenEnable, priority: 1, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
        DrawBackgroundIfEnabled(hardware, screenEnable, 1, priority: 0, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
        DrawBackgroundIfEnabled(hardware, screenEnable, 0, priority: 0, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
        DrawObjectsIfEnabled(hardware, screenEnable, priority: 2, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
        DrawBackgroundIfEnabled(hardware, screenEnable, 1, priority: 1, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
        DrawBackgroundIfEnabled(hardware, screenEnable, 0, priority: 1, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
        DrawObjectsIfEnabled(hardware, screenEnable, priority: 3, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
        if (bg3HighPriority)
        {
            DrawBackgroundIfEnabled(hardware, screenEnable, 2, priority: 1, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
        }
    }

    private static void DrawBackgroundIfEnabled(
        SnesHardware hardware,
        byte screenEnable,
        int background,
        int priority,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam)
    {
        if ((screenEnable & (1 << background)) == 0)
        {
            return;
        }

        DrawBackground(hardware, background, mode: 1, priority, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
    }

    private static void DrawObjectsIfEnabled(
        SnesHardware hardware,
        byte screenEnable,
        int priority,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam)
    {
        if ((screenEnable & 0x10) != 0)
        {
            DrawObjects(hardware, priority, subScreen, brightness, rgbaPixels, pixelSources, videoRam, colorRam);
        }
    }

    private static void DrawObjects(
        SnesHardware hardware,
        int priority,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam)
    {
        var registers = hardware.PpuRegisters.Span;
        var objectRam = hardware.ObjectAttributeMemory.Span;
        var objectSizeMode = (registers[1] >> 5) & 0x07;
        var tileBase = (registers[1] & 0x07) * 0x4000;
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

            var palette = attributes & 0x07;
            var horizontalFlip = (attributes & 0x40) != 0;
            var verticalFlip = (attributes & 0x80) != 0;

            for (var objectY = 0; objectY < objectSize; objectY++)
            {
                var screenY = (y + objectY) & 0xFF;
                if (screenY >= Height)
                {
                    continue;
                }

                var sourceY = verticalFlip ? objectSize - 1 - objectY : objectY;
                var scanlineRegisters = hardware.GetPpuRegistersForScanline(screenY).Span;
                for (var objectX = 0; objectX < objectSize; objectX++)
                {
                    var screenX = x + objectX;
                    if ((uint)screenX >= Width)
                    {
                        continue;
                    }

                    if (IsLayerWindowed(scanlineRegisters, 4, subScreen, screenX))
                    {
                        continue;
                    }

                    var sourceX = horizontalFlip ? objectSize - 1 - objectX : objectX;
                    var tileNumber = (tile + ((sourceY / 8) * 16) + (sourceX / 8)) & 0xFF;
                    var tileAddress = (tileBase + (tileNumber * 32)) & 0xFFFF;
                    var colorIndex = ReadTilePixel(videoRam, tileAddress, 4, sourceX & 7, sourceY & 7);
                    if (colorIndex == 0)
                    {
                        continue;
                    }

                    var color = ReadColor(colorRam, 128 + (palette * 16) + colorIndex, brightness, forcedBlank: false);
                    var pixelIndex = (screenY * Width) + screenX;
                    WriteColor(rgbaPixels, pixelIndex * BytesPerPixel, color);
                    pixelSources[pixelIndex] = palette >= 4 ? (byte)5 : (byte)6;
                }
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
        int background,
        int mode,
        int priority,
        bool subScreen,
        int brightness,
        Span<byte> rgbaPixels,
        Span<byte> pixelSources,
        ReadOnlySpan<byte> videoRam,
        ReadOnlySpan<byte> colorRam)
    {
        var registers = hardware.PpuRegisters.Span;
        var mapRegister = registers[0x07 + background];
        var mapBase = (mapRegister & 0xFC) << 9;
        var mapSize = mapRegister & 0x03;
        var mapWidth = (mapSize & 0x01) != 0 ? 64 : 32;
        var mapHeight = (mapSize & 0x02) != 0 ? 64 : 32;
        var tileWidth = (registers[0x05] & (0x10 << background)) != 0 ? 16 : 8;
        var tileHeight = tileWidth;
        var pixelWidth = mapWidth * tileWidth;
        var pixelHeight = mapHeight * tileHeight;
        var horizontalOffset = hardware.GetBackgroundHorizontalOffset(background) % pixelWidth;
        var verticalOffset = hardware.GetBackgroundVerticalOffset(background) % pixelHeight;
        var characterBaseRegister = registers[0x0B + background / 2];
        var characterBase = ((background & 1) == 0
            ? characterBaseRegister & 0x0F
            : characterBaseRegister >> 4) * 0x2000;
        var bitsPerPixel = mode == 0 || (mode == 1 && background == 2) ? 2 : 4;
        var bytesPerTile = bitsPerPixel * 8;
        for (var screenY = 0; screenY < Height; screenY++)
        {
            var scanlineRegisters = hardware.GetPpuRegistersForScanline(screenY).Span;
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
                    localY = tileHeight - 1 - localY;
                }

                var tileNumber = tileEntry & 0x03FF;
                if (tileWidth == 16)
                {
                    tileNumber += (localY / 8 * 16) + (localX / 8);
                    localX &= 7;
                    localY &= 7;
                }

                var tileAddress = (characterBase + (tileNumber * bytesPerTile)) & 0xFFFF;
                var paletteIndex = ReadTilePixel(videoRam, tileAddress, bitsPerPixel, localX, localY);
                if (paletteIndex == 0)
                {
                    continue;
                }

                var paletteGroup = (tileEntry >> 10) & 0x07;
                var colorIndex = (paletteGroup * (1 << bitsPerPixel)) + paletteIndex;
                var color = ReadColor(colorRam, colorIndex, brightness, forcedBlank: false);
                var pixelIndex = (screenY * Width) + screenX;
                WriteColor(rgbaPixels, pixelIndex * BytesPerPixel, color);
                pixelSources[pixelIndex] = (byte)(background + 1);
            }
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

    private static uint ReadColor(ReadOnlySpan<byte> colorRam, int colorIndex, int brightness, bool forcedBlank)
    {
        if (forcedBlank)
        {
            return 0xFF000000;
        }

        var address = (colorIndex * 2) & 0x1FF;
        var color = colorRam[address] | ((colorRam[address + 1] & 0x7F) << 8);
        var red = ScaleChannel(color & 0x1F, brightness);
        var green = ScaleChannel((color >> 5) & 0x1F, brightness);
        var blue = ScaleChannel((color >> 10) & 0x1F, brightness);
        return 0xFF000000u | ((uint)blue << 16) | ((uint)green << 8) | (uint)red;
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
