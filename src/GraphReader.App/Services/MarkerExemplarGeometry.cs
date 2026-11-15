// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Models;

namespace GraphReader.App.Services;

internal static class MarkerExemplarGeometry
{
    // Pixel n covers [n - 0.5, n + 0.5); a half-pixel tie belongs to n + 1.
    internal static bool ContainsCenter(MarkerExemplarRectangle rectangle, MarkerExemplarPoint point) =>
        rectangle.Width > 0 && rectangle.Height > 0 && double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        point.X >= rectangle.X - 0.5 && point.Y >= rectangle.Y - 0.5 &&
        point.X < (long)rectangle.X + rectangle.Width - 0.5 &&
        point.Y < (long)rectangle.Y + rectangle.Height - 0.5;

    internal static bool ContainsPlot(MarkerExemplarRequest request, MarkerExemplarPoint point) =>
        ContainsCenter(request.Plot, point) && (request.AffinePlot is null ||
            TryCreateAffineDomain(request.AffinePlot, request.Width, request.Height, out var domain, out _) && domain.Contains(point));

    internal static bool TryCreatePlot(ManualCalibrationState calibration, int width, int height,
        out MarkerExemplarRectangle plot, out MarkerExemplarAffinePlot? frame)
    {
        plot = null!; frame = null;
        var origin = calibration.Session1Y0; var upper = calibration.Session1YMaximum; var right = calibration.SessionMaximumY0;
        if (!ManualCalibrationMapper.IsAxisAligned(calibration))
        {
            frame = new(new(origin.X, origin.Y), new(right.X, right.Y), new(upper.X, upper.Y));
            return TryCreateAffineDomain(frame, width, height, out _, out plot);
        }
        // Preserve the existing aligned/legacy two-pixel path and its integer plot cells.
        if (!origin.IsFinite || !upper.IsFinite || !right.IsFinite || origin.X < 0 || upper.Y < 0 ||
            right.X >= width || origin.Y >= height || right.X <= origin.X || upper.Y >= origin.Y) return false;
        int left = (int)Math.Ceiling(origin.X), top = (int)Math.Ceiling(upper.Y);
        int end = (int)Math.Floor(right.X), bottom = (int)Math.Floor(origin.Y);
        plot = new(left, top, end - left + 1, bottom - top + 1);
        return plot.Width > 0 && plot.Height > 0;
    }

    internal static bool TryCreateAffineDomain(MarkerExemplarAffinePlot frame, int width, int height,
        out AffineDomain domain, out MarkerExemplarRectangle enclosing)
    {
        domain = default; enclosing = null!;
        if (frame.Origin is not { } origin || frame.XEnd is not { } x || frame.YEnd is not { } y ||
            !Inside(origin) || !Inside(x) || !Inside(y)) return false;
        var fourth = new MarkerExemplarPoint(x.X + y.X - origin.X, x.Y + y.Y - origin.Y);
        if (!Inside(fourth)) return false;
        double xx = x.X - origin.X, xy = x.Y - origin.Y, yx = y.X - origin.X, yy = y.Y - origin.Y;
        double determinant = xx * yy - xy * yx;
        if (!double.IsFinite(determinant) || determinant == 0) return false;
        domain = new(origin.X, origin.Y, yy / determinant, -yx / determinant, -xy / determinant, xx / determinant, x, y, fourth);
        if (!domain.IsFinite) return false;
        int left = (int)Math.Floor(Math.Min(Math.Min(origin.X, x.X), Math.Min(y.X, fourth.X)));
        int top = (int)Math.Floor(Math.Min(Math.Min(origin.Y, x.Y), Math.Min(y.Y, fourth.Y)));
        int right = (int)Math.Ceiling(Math.Max(Math.Max(origin.X, x.X), Math.Max(y.X, fourth.X)));
        int bottom = (int)Math.Ceiling(Math.Max(Math.Max(origin.Y, x.Y), Math.Max(y.Y, fourth.Y)));
        // This rectangle is only a native search ROI. Eligibility uses the true frame below.
        enclosing = new(left, top, right - left + 1, bottom - top + 1);
        return true;

        bool Inside(MarkerExemplarPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
            point.X >= 0 && point.Y >= 0 && point.X <= width - 1 && point.Y <= height - 1;
    }

    internal readonly record struct AffineDomain(double OriginX, double OriginY, double Ux, double Uy, double Vx, double Vy,
        MarkerExemplarPoint XEnd, MarkerExemplarPoint YEnd, MarkerExemplarPoint Fourth)
    {
        internal bool IsFinite => double.IsFinite(Ux) && double.IsFinite(Uy) && double.IsFinite(Vx) && double.IsFinite(Vy);
        internal bool Contains(MarkerExemplarPoint point)
            => Contains(point.X, point.Y);
        internal bool Contains(double pixelX, double pixelY)
        {
            if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY)) return false;
            if (pixelX == OriginX && pixelY == OriginY || pixelX == XEnd.X && pixelY == XEnd.Y ||
                pixelX == YEnd.X && pixelY == YEnd.Y || pixelX == Fourth.X && pixelY == Fourth.Y) return true;
            double x = pixelX - OriginX, y = pixelY - OriginY;
            double u = x * Ux + y * Uy, v = x * Vx + y * Vy;
            if (double.IsFinite(u) && double.IsFinite(v) && u is >= 0 and <= 1 && v is >= 0 and <= 1) return true;
            return OnEdge(pixelX, pixelY, OriginX, OriginY, XEnd.X, XEnd.Y) ||
                OnEdge(pixelX, pixelY, OriginX, OriginY, YEnd.X, YEnd.Y) ||
                OnEdge(pixelX, pixelY, XEnd.X, XEnd.Y, Fourth.X, Fourth.Y) ||
                OnEdge(pixelX, pixelY, YEnd.X, YEnd.Y, Fourth.X, Fourth.Y);
        }

        private static bool OnEdge(double x, double y, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double t = Math.Abs(dx) >= Math.Abs(dy) ? (x - ax) / dx : (y - ay) / dy;
            if (!double.IsFinite(t) || t is < 0 or > 1) return false;
            // Binary64 arithmetic error only: gamma(8) covers the subtraction,
            // division and interpolation operations, including construction of an edge point.
            // This is not a pixel margin; no coordinate is snapped or modified.
            const double unitRoundoff = 1d / 9_007_199_254_740_992d;
            const double gamma8 = 8 * unitRoundoff / (1 - 8 * unitRoundoff);
            double errorX = gamma8 * (Math.Abs(ax) + Math.Abs(bx) + Math.Abs(x));
            double errorY = gamma8 * (Math.Abs(ay) + Math.Abs(by) + Math.Abs(y));
            return Math.Abs(x - (ax + t * dx)) <= errorX && Math.Abs(y - (ay + t * dy)) <= errorY;
        }
    }
}
