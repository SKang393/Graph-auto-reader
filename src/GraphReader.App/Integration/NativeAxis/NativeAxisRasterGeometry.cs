// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.Axis;

namespace GraphReader.App.Integration.NativeAxis;

/// <summary>Resolves a nearby observed far-end transition without moving an axis origin.</summary>
internal static class NativeAxisRasterGeometry
{
    internal static RustAxisGeometry Adjust(RustAxisGeometry geometry, byte[] pixels, int width, int height, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        int left = geometry.Left, bottom = geometry.Bottom, right = geometry.Right, top = geometry.Top;
        if (pixels.Length != checked(width * height) || left < 0 || top < 0 || right >= width || bottom >= height ||
            left >= right || top >= bottom || pixels[bottom * width + left] >= 200) return geometry;
        bool Horizontal(int x) => pixels[bottom * width + x] < 200;
        bool Vertical(int y) => pixels[y * width + left] < 200;
        bool Connected(int start, int end, Func<int, bool> dark)
        {
            for (int position = start; position <= end; position++)
            {
                if ((position & 255) == 0) token.ThrowIfCancellationRequested();
                if (!dark(position)) return false;
            }
            return true;
        }
        int startX = right - 2;
        if (startX > left && Connected(left, startX, Horizontal))
        {
            int x = startX;
            while (x < width - 1 && x < right + 2 && Horizontal(x + 1)) x++;
            if ((x == width - 1 || !Horizontal(x + 1)) && x > left) right = x;
        }
        int startY = top + 2;
        if (startY < bottom && Connected(startY, bottom, Vertical))
        {
            int y = startY;
            while (y > 0 && y > top - 2 && Vertical(y - 1)) y--;
            if ((y == 0 || !Vertical(y - 1)) && y < bottom) top = y;
        }
        return geometry with { Right = right, Top = top };
    }
}
