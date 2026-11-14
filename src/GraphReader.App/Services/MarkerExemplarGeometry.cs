// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
namespace GraphReader.App.Services;

internal static class MarkerExemplarGeometry
{
    internal static bool ContainsCenter(MarkerExemplarRectangle rectangle, MarkerExemplarPoint point) =>
        rectangle.Width > 0 && rectangle.Height > 0 && double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        point.X >= rectangle.X && point.Y >= rectangle.Y &&
        point.X <= (long)rectangle.X + rectangle.Width - 1 &&
        point.Y <= (long)rectangle.Y + rectangle.Height - 1;
}
