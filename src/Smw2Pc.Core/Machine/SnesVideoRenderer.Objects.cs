namespace Smw2Pc.Core.Machine;

public static partial class SnesVideoRenderer
{
    [ThreadStatic]
    private static ObjectRenderCache? activeObjectCache;
    [ThreadStatic]
    private static ObjectRenderCache? reusableObjectCache;

    private readonly record struct PreparedObject(int X, int Y, byte Tile, byte Attributes, bool Large);

    private sealed class ObjectRenderCache
    {
        private const int ObjectCount = 128;
        private const int PriorityBucketCount = 5;
        private readonly byte[] visibleObjects = new byte[Height * PriorityBucketCount * ObjectCount];
        private readonly int[] visibleCounts = new int[Height * PriorityBucketCount];
        public readonly PreparedObject[] Objects = new PreparedObject[ObjectCount];

        public void Prepare(SnesHardware hardware, float interpolationProgress)
        {
            var current = hardware.CurrentFrameObjectAttributeMemory.Span;
            var previous = hardware.PreviousObjectAttributeMemory.Span;
            var interpolate = hardware.HasPreviousObjectAttributeFrame && interpolationProgress < 1f;
            for (var index = 0; index < ObjectCount; index++)
            {
                var address = index * 4;
                var highShift = (index & 3) * 2;
                var high = (current[512 + (index >> 2)] >> highShift) & 3;
                var rawX = current[address] | ((high & 1) << 8);
                var x = (float)(rawX >= 256 ? rawX - 512 : rawX);
                var y = (float)current[address + 1];
                var attributes = current[address + 3];
                if (interpolate)
                {
                    var previousHigh = (previous[512 + (index >> 2)] >> highShift) & 3;
                    var sameObject = ((previous[address + 3] ^ attributes) & 0x3F) == 0
                        && ((previousHigh ^ high) & 2) == 0;
                    if (sameObject)
                    {
                        var previousRawX = previous[address] | ((previousHigh & 1) << 8);
                        var previousX = previousRawX >= 256 ? previousRawX - 512 : previousRawX;
                        var deltaX = x - previousX;
                        if (Math.Abs(deltaX) <= MaximumInterpolatedObjectDelta)
                            x = previousX + (deltaX * interpolationProgress);
                        var previousY = previous[address + 1];
                        var deltaY = (int)y - previousY;
                        if (deltaY > 128) deltaY -= 256;
                        else if (deltaY < -128) deltaY += 256;
                        if (Math.Abs(deltaY) <= MaximumInterpolatedObjectDelta)
                            y = previousY + (deltaY * interpolationProgress);
                    }
                }

                // Positions are snapped once. Every sample then has full coverage and
                // can use an integer palette lookup without the old bilinear machinery.
                Objects[index] = new PreparedObject(
                    (int)MathF.Round(x, MidpointRounding.AwayFromZero),
                    (int)MathF.Round(y, MidpointRounding.AwayFromZero),
                    current[address + 2], attributes, (high & 2) != 0);
            }

            Array.Clear(visibleCounts);
            for (var y = 0; y < Height; y++)
            {
                var registers = hardware.GetPpuRegistersForScanline(y + 1).Span;
                if ((registers[0] & 0x80) != 0 || ((registers[0x2C] | registers[0x2D]) & 0x10) == 0)
                    continue;
                var (smallSize, largeSize) = GetObjectSizes((registers[1] >> 5) & 7);
                for (var index = ObjectCount - 1; index >= 0; index--)
                {
                    var obj = Objects[index];
                    var size = obj.Large ? largeSize : smallSize;
                    if (obj.X >= Width || obj.X + size <= 0)
                        continue;
                    var sourceY = ObjectSourceY(y, obj.Y, size);
                    if ((uint)sourceY >= size)
                        continue;
                    AddVisible(y, (obj.Attributes >> 4) & 3, index);
                    AddVisible(y, 4, index);
                }
            }
        }

        private void AddVisible(int scanline, int priority, int objectIndex)
        {
            var bucket = scanline * PriorityBucketCount + priority;
            visibleObjects[bucket * ObjectCount + visibleCounts[bucket]++] = (byte)objectIndex;
        }

        public ReadOnlySpan<byte> GetVisible(int scanline, int priority)
        {
            var bucket = scanline * PriorityBucketCount + (priority < 0 ? 4 : priority);
            return visibleObjects.AsSpan(bucket * ObjectCount, visibleCounts[bucket]);
        }
    }

    private static int ObjectSourceY(int scanline, int objectY, int objectSize)
    {
        var sourceY = scanline - objectY;
        if (sourceY < -objectSize) sourceY += 256;
        else if (sourceY > objectSize) sourceY -= 256;
        return sourceY;
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
        ReadOnlySpan<byte> colorRam,
        float interpolationProgress)
    {
        var cache = activeObjectCache!;
        var hasActiveWindow = HasActiveLayerWindow(registers, 4, subScreen);
        var tileBase = (registers[1] & 7) * 0x4000;
        var tileNameSelectOffset = (((registers[1] >> 3) & 3) + 1) * 0x2000;
        var (smallSize, largeSize) = GetObjectSizes((registers[1] >> 5) & 7);
        foreach (var objectIndex in cache.GetVisible(targetScanline, priority))
        {
            var obj = cache.Objects[objectIndex];
            var size = obj.Large ? largeSize : smallSize;
            var sourceY = ObjectSourceY(targetScanline, obj.Y, size);
            if ((obj.Attributes & 0x80) != 0) sourceY = size - 1 - sourceY;
            var horizontalFlip = (obj.Attributes & 0x40) != 0;
            var palette = (obj.Attributes >> 1) & 7;
            var paletteBase = 128 + palette * 16;
            var objectTileBase = tileBase + ((obj.Attributes & 1) != 0 ? tileNameSelectOffset : 0);
            var tileRow = obj.Tile + ((sourceY >> 3) * 16);
            var firstX = Math.Max(0, obj.X);
            var lastX = Math.Min(Width, obj.X + size);
            for (var screenX = firstX; screenX < lastX; screenX++)
            {
                if (hasActiveWindow && IsLayerWindowed(registers, 4, subScreen, screenX)) continue;
                var sourceX = screenX - obj.X;
                if (horizontalFlip) sourceX = size - 1 - sourceX;
                var tileNumber = (tileRow + (sourceX >> 3)) & 0xFF;
                var tileAddress = (objectTileBase + tileNumber * 32) & 0xFFFF;
                var colorIndex = ReadTilePixel(videoRam, tileAddress, 4, sourceX & 7, sourceY & 7);
                if (colorIndex == 0) continue;
                var color = ReadColorValue(colorRam, paletteBase + colorIndex);
                var pixel = targetScanline * Width + screenX;
                WriteColor(rgbaPixels, pixel * BytesPerPixel, ReadColor(color, brightness, forcedBlank: false));
                pixelColors[pixel] = color;
                pixelSources[pixel] = palette >= 4 ? (byte)5 : (byte)6;
            }
        }
    }
}
