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

        for (var pixel = 0; pixel < Width * Height; pixel++)
        {
            WriteColor(rgbaPixels, pixel * BytesPerPixel, backdrop);
        }

        if (forcedBlank)
        {
            return;
        }

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
                brightness,
                rgbaPixels,
                videoRam,
                colorRam);
        }

        if ((screenEnable & 0x10) != 0)
        {
            DrawObjects(hardware, brightness, rgbaPixels, videoRam, colorRam);
        }
    }

    private static void DrawObjects(
        SnesHardware hardware,
        int brightness,
        Span<byte> rgbaPixels,
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
                for (var objectX = 0; objectX < objectSize; objectX++)
                {
                    var screenX = x + objectX;
                    if ((uint)screenX >= Width)
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
                    WriteColor(rgbaPixels, ((screenY * Width) + screenX) * BytesPerPixel, color);
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
        int brightness,
        Span<byte> rgbaPixels,
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
            var worldY = (screenY + verticalOffset) % pixelHeight;
            var cellY = worldY / tileHeight;
            var localY = worldY % tileHeight;
            for (var screenX = 0; screenX < Width; screenX++)
            {
                var worldX = (screenX + horizontalOffset) % pixelWidth;
                var cellX = worldX / tileWidth;
                var localX = worldX % tileWidth;
                var mapIndex = GetMapByteIndex(mapBase, mapWidth, cellX, cellY);
                var tileEntry = (ushort)(videoRam[mapIndex] | (videoRam[mapIndex + 1] << 8));
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
                WriteColor(rgbaPixels, ((screenY * Width) + screenX) * BytesPerPixel, color);
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
