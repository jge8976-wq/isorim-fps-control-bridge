using System;

namespace HSK.FpsBridge
{
    internal static class MinimapRules
    {
        public static (float x, float y) Position(float x, float z, int width, int height) =>
            (Math.Clamp(x / Math.Max(1, width), 0f, 1f), 1f - Math.Clamp(z / Math.Max(1, height), 0f, 1f));
        public static (float x, float y) PixelPosition(float x, float z, int mapWidth, int mapRows,
            float left, float top, float width, float height)
        {
            var normalized = Position(x, z, mapWidth, mapRows);
            return (left + normalized.x * width, top + normalized.y * height);
        }
        public static (float x, float y) Heading(float yaw)
        { double r = yaw * Math.PI / 180d; return ((float)Math.Sin(r), -(float)Math.Cos(r)); }
    }
}
