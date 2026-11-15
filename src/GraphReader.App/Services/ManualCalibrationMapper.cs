// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Models;
using GraphReader.Axis;

namespace GraphReader.App.Services;

/// <summary>Maps original pixels through the three explicitly supplied linear-axis anchors.</summary>
internal static class ManualCalibrationMapper
{
    internal const string AffineReason = "manual_affine_original_pixels";
    // Default clicks and validated legacy records retain the prior two-pixel tolerance.
    // Explicit affine requests and geometry outside that tolerance receive no scalars.
    internal static bool IsAxisAligned(ManualCalibrationState calibration) =>
        calibration.XTransform is { } x && calibration.YTransform is { } y &&
        double.IsFinite(x.Slope) && double.IsFinite(x.Intercept) && Math.Abs(x.Slope) > 1e-12 &&
        double.IsFinite(y.Slope) && double.IsFinite(y.Intercept) && Math.Abs(y.Slope) > 1e-12 &&
        HasLegacyAlignment(calibration.Session1Y0, calibration.Session1YMaximum, calibration.SessionMaximumY0);

    internal static bool HasLegacyAlignment(PixelPoint origin, PixelPoint upper, PixelPoint final) =>
        Math.Abs(origin.X - upper.X) <= 2 && Math.Abs(origin.Y - final.Y) <= 2;

    internal static bool IsAxisAligned(PixelPoint origin, PixelPoint upper, PixelPoint final) =>
        origin.X == upper.X && origin.Y == final.Y;

    internal static bool UsesScalarMapping(ManualCalibrationRequest request) =>
        IsAxisAligned(request.Session1Y0, request.Session1YMaximum, request.SessionMaximumY0) ||
        !request.PreserveAffineGeometry && HasLegacyAlignment(request.Session1Y0, request.Session1YMaximum, request.SessionMaximumY0);

    internal static PixelPoint Map(ManualCalibrationState calibration, double pixelX, double pixelY)
    {
        if (IsAxisAligned(calibration) && calibration.XTransform is { } x && calibration.YTransform is { } y)
            return new(x.PixelToGraph(pixelX), y.PixelToGraph(pixelY));
        if (!TryCreate(calibration.Session1Y0, calibration.Session1YMaximum, calibration.SessionMaximumY0,
                calibration.FirstVisibleSession, calibration.XMaximum, calibration.YMaximum, out var map))
            throw new InvalidOperationException("The manual calibration anchors do not define an invertible linear mapping.");
        return map.Map(pixelX, pixelY);
    }

    internal static bool TryCreate(PixelPoint origin, PixelPoint upper, PixelPoint final,
        double firstX, double finalX, double maximumY, out AffineMap map)
    {
        map = default;
        if (!origin.IsFinite || !upper.IsFinite || !final.IsFinite ||
            !double.IsFinite(firstX) || !double.IsFinite(finalX) || !double.IsFinite(maximumY) ||
            finalX <= firstX || maximumY <= 0) return false;
        double xx = final.X - origin.X, xy = final.Y - origin.Y;
        double yx = upper.X - origin.X, yy = upper.Y - origin.Y;
        double determinant = xx * yy - xy * yx;
        if (!double.IsFinite(determinant) || determinant == 0) return false;
        double rangeX = finalX - firstX;
        double a = rangeX * yy / determinant, b = -rangeX * yx / determinant;
        double c = -maximumY * xy / determinant, d = maximumY * xx / determinant;
        double inverseDeterminant = a * d - b * c;
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c) || !double.IsFinite(d) ||
            !double.IsFinite(inverseDeterminant) || inverseDeterminant == 0) return false;
        map = new(origin, firstX, a, b, c, d);
        return true;
    }

    internal readonly record struct AffineMap(PixelPoint Origin, double FirstX, double A, double B, double C, double D)
    {
        internal PixelPoint Map(double pixelX, double pixelY)
        {
            double x = pixelX - Origin.X, y = pixelY - Origin.Y;
            return new(FirstX + A * x + B * y, C * x + D * y);
        }
    }
}
