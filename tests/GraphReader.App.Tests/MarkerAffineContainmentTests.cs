// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Integration.MarkerExemplar;
using GraphReader.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class MarkerAffineContainmentTests
{
    [TestMethod]
    [DataRow(2d, false)]
    [DataRow(-2d, false)]
    [DataRow(0d, true)]
    public void VerticesAndEdgeMidpointsAreInsideButRoiCornerOutsideFrameIsNot(double degrees, bool shear)
    {
        MarkerExemplarAffinePlot frame = Frame(degrees, shear);
        Assert.IsTrue(MarkerExemplarGeometry.TryCreateAffineDomain(frame, 320, 240, out var domain, out var roi));
        MarkerExemplarPoint fourth = new(frame.XEnd.X + frame.YEnd.X - frame.Origin.X, frame.XEnd.Y + frame.YEnd.Y - frame.Origin.Y);
        MarkerExemplarPoint[] vertices = [frame.Origin, frame.XEnd, fourth, frame.YEnd];
        for (int i = 0; i < vertices.Length; i++)
        {
            MarkerExemplarPoint a = vertices[i], b = vertices[(i + 1) % vertices.Length];
            Assert.IsTrue(domain.Contains(a));
            Assert.IsTrue(domain.Contains(new MarkerExemplarPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2)));
        }
        MarkerExemplarPoint? outside = null;
        for (int y = roi.Y; y < roi.Y + roi.Height && outside is null; y++)
            for (int x = roi.X; x < roi.X + roi.Width; x++)
                if (MarkerExemplarGeometry.ContainsCenter(roi, new(x, y)) && !domain.Contains(x, y)) { outside = new(x, y); break; }
        Assert.IsNotNull(outside, "The enclosing rectangle must contain a demonstrably outside point.");
        Assert.IsFalse(MarkerExemplarGeometry.ContainsPlot(Request(frame, roi), outside));
    }

    [TestMethod]
    public void InvalidFramesAndFourthCornerOutsideImageAreRejected()
    {
        MarkerExemplarAffinePlot frame = Frame(2, false);
        MarkerExemplarAffinePlot[] invalid = [frame with { Origin = new(double.NaN, 185) },
            frame with { XEnd = new(double.PositiveInfinity, 185) }, frame with { YEnd = frame.XEnd },
            frame with { Origin = new(-1, 185) },
            new(new(55, 185), new(310, 180), new(80, 30))];
        foreach (MarkerExemplarAffinePlot bad in invalid)
            Assert.IsFalse(MarkerExemplarGeometry.TryCreateAffineDomain(bad, 320, 240, out _, out _));
    }

    [TestMethod]
    public void MetadataAndEchoRejectChangedFrameOrEnclosingRoi()
    {
        MarkerExemplarAffinePlot frame = Frame(2, false);
        Assert.IsTrue(MarkerExemplarGeometry.TryCreateAffineDomain(frame, 320, 240, out _, out var roi));
        MarkerExemplarRequest request = Request(frame, roi);
        Assert.IsTrue(MarkerExemplarValidation.ValidMetadata(request));
        var response = MarkerExemplarResponse.Failure(request, MarkerExemplarReasonCodes.InvalidFrame);
        Assert.IsTrue(MarkerExemplarValidation.ValidResponse(request, response));
        var changedFrame = request with { AffinePlot = frame with { Origin = frame.Origin with { X = frame.Origin.X + .25 } } };
        Assert.IsFalse(MarkerExemplarValidation.ValidResponse(request, response with { Request = changedFrame }));
        Assert.IsFalse(MarkerExemplarValidation.ValidMetadata(request with { Plot = roi with { X = roi.X + 1 } }));
    }

    private static MarkerExemplarAffinePlot Frame(double degrees, bool shear)
    {
        double angle = degrees * Math.PI / 180, c = Math.Cos(angle), s = Math.Sin(angle);
        MarkerExemplarPoint origin = new(55.25, 185.5);
        return new(origin, new(origin.X + 210 * c, origin.Y + 210 * s),
            new(origin.X + 140 * s + (shear ? 14 : 0), origin.Y - 140 * c));
    }

    private static MarkerExemplarRequest Request(MarkerExemplarAffinePlot frame, MarkerExemplarRectangle roi)
    {
        var center = new MarkerExemplarPoint((frame.XEnd.X + frame.YEnd.X) / 2, (frame.XEnd.Y + frame.YEnd.Y) / 2);
        string hash = new('a', 64);
        return new("request", hash, hash, hash, "panel", 320, 240,
            new((int)Math.Floor(center.X) - 3, (int)Math.Floor(center.Y) - 3, 7, 7), roi, "point", "series", center) { AffinePlot = frame };
    }
}
