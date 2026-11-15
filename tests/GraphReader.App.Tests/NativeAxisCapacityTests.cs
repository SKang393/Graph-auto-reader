// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using GraphReader.App.Integration.NativeAxis;
using GraphReader.App.Services;
using GraphReader.Axis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class NativeAxisCapacityTests
{
    [TestMethod]
    [DataRow(129)]
    [DataRow(256)]
    public async Task NormalCompleteSearchReturnsGlobalBestAfterFirst128(int pairs)
    {
        GeometryLineCandidate[] lines = Lines(pairs);
        AxisGeometryResult reference = await new AxisGeometryDetector().DetectAsync(new(4096, 4096, lines));
        RustAxisProposalResult actual = await NativeAxisWorker.ProcessLinesAsync(lines, 4096, 4096, null, CancellationToken.None);
        Assert.IsNull(actual.ErrorCode);
        Assert.IsNotNull(actual.Geometry);
        Assert.AreEqual("opencv-review", actual.Provider);
        // The leftmost vertical is deliberately last. No first-128 prefix
        // contains this true global winner.
        Assert.IsFalse(lines.Skip(1).Take(128).Any(line => line.Segment.Start.X == 100));
        Assert.AreEqual(100d, reference.PlotPolygon.BottomLeft.X);
        Assert.AreEqual((int)Math.Round(reference.PlotPolygon.BottomLeft.X), actual.Geometry.Left);
        Assert.AreEqual((int)Math.Round(reference.PlotPolygon.BottomLeft.Y), actual.Geometry.Bottom);
        Assert.AreEqual((int)Math.Round(reference.PlotPolygon.BottomRight.X), actual.Geometry.Right);
        Assert.AreEqual((int)Math.Round(reference.PlotPolygon.TopLeft.Y), actual.Geometry.Top);
        Assert.AreEqual(reference.Confidence, actual.Geometry.Confidence);
        CollectionAssert.Contains(actual.ReviewReasons.ToArray(), "native_multiple_ranked_candidates");
        Assert.IsEmpty(actual.Geometry.XTickPositions);
        Assert.IsEmpty(actual.Geometry.YTickPositions);
        Assert.IsNull(actual.Geometry.FirstTickX);
        Assert.IsNull(actual.Geometry.LastTickX);
    }

    [TestMethod]
    public async Task ExplicitChooserKeeps128LimitAndRefusesSame129WithoutTruncation()
    {
        NativeAxisCandidateReviewObservation? observed = null;
        AxisCandidateReviewResult actual = await NativeAxisCandidateReview.ProcessLinesAsync(Lines(129), 4096, 4096,
            value => observed = value, CancellationToken.None);
        Assert.AreEqual("OPENCV_AXIS_AMBIGUOUS", actual.ErrorCode);
        Assert.IsEmpty(actual.Candidates);
        Assert.IsNotNull(observed);
        Assert.AreEqual("native_pair_budget_exceeded", observed.FailureDetail);
        Assert.IsFalse(observed.EnumerationCompleted);
        Assert.AreEqual(0, observed.RawSnapshotCount);
        Assert.IsNotNull(observed.Work);
        Assert.AreEqual(128L, observed.Work.ExactPairAttempts);
        Assert.AreEqual(129L, observed.Work.ExactPairRequests);
    }

    private static GeometryLineCandidate[] Lines(int pairs)
    {
        var lines = new GeometryLineCandidate[pairs + 1];
        lines[0] = new("baseline", new(new(100, 3000), new(4000, 3000)), LineCandidateSource.RecordedFixture);
        for (int i = 0; i < pairs; i++)
        {
            int x = 100 + 10 * (pairs - 1 - i);
            lines[i + 1] = new($"vertical-{i:D3}", new(new(x, 3000), new(x, 1000)), LineCandidateSource.RecordedFixture);
        }
        return lines;
    }
}
