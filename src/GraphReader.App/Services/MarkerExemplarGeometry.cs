// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
namespace GraphReader.App.Services;

internal static class MarkerExemplarGeometry
{
    // Pixel n covers [n - 0.5, n + 0.5); a half-pixel tie belongs to n + 1.
    internal static bool ContainsCenter(MarkerExemplarRectangle rectangle, MarkerExemplarPoint point) =>
        rectangle.Width > 0 && rectangle.Height > 0 && double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        point.X >= rectangle.X - 0.5 && point.Y >= rectangle.Y - 0.5 &&
        point.X < (long)rectangle.X + rectangle.Width - 0.5 &&
        point.Y < (long)rectangle.Y + rectangle.Height - 0.5;
}
