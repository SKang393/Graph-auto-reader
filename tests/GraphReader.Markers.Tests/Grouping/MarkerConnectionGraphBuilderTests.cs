// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Markers.Detection;
using GraphReader.Markers.Grouping;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Markers.Tests.Grouping;

[TestClass]
public sealed class MarkerConnectionGraphBuilderTests
{
    private static readonly string[] ExpectedConnectedMarkerIds = ["left", "right"];

    [TestMethod]
    public async Task ImageBoundaryPaddingDoesNotCreateInkConnections()
    {
        MarkerGroupingEvidence left = MarkerGroupingTestSupport.Evidence("left", 1, x: 12, y: 0);
        MarkerGroupingEvidence right = MarkerGroupingTestSupport.Evidence("right", 2, x: 48, y: 0);

        IReadOnlyList<MarkerConnection> connections = await new MarkerConnectionGraphBuilder().BuildAsync(
            Request(MarkerGroupingTestSupport.Frame(), [left, right]),
            CancellationToken.None);

        Assert.IsEmpty(connections, "A blank image must not gain a connection from out-of-frame samples.");
    }

    [TestMethod]
    public async Task RealStrokeAtImageBoundaryStillCreatesConnection()
    {
        MarkerGroupingEvidence left = MarkerGroupingTestSupport.Evidence("left", 1, x: 12, y: 0);
        MarkerGroupingEvidence right = MarkerGroupingTestSupport.Evidence("right", 2, x: 48, y: 0);
        MarkerPoint[] stroke = MarkerGroupingTestSupport.Line(
            left.Marker.Marker.Center, right.Marker.Marker.Center).ToArray();

        IReadOnlyList<MarkerConnection> connections = await new MarkerConnectionGraphBuilder().BuildAsync(
            Request(MarkerGroupingTestSupport.Frame(stroke), [left, right]),
            CancellationToken.None);

        Assert.HasCount(1, connections, "Visible ink at the image boundary remains valid connection evidence.");
    }

    [TestMethod]
    public async Task SolidConnectingStrokeCreatesOneCanonicalConnectionEdge()
    {
        MarkerGroupingEvidence left = MarkerGroupingTestSupport.Evidence("left", 1, x: 12, y: 20);
        MarkerGroupingEvidence right = MarkerGroupingTestSupport.Evidence("right", 2, x: 48, y: 20);
        MarkerPoint[] stroke = MarkerGroupingTestSupport.Line(
            left.Marker.Marker.Center,
            right.Marker.Marker.Center).ToArray();
        MarkerConnectionRequest request = Request(
            MarkerGroupingTestSupport.Frame(stroke),
            [right, left]);

        IReadOnlyList<MarkerConnection> connections = await new MarkerConnectionGraphBuilder().BuildAsync(
            request,
            CancellationToken.None);

        Assert.HasCount(1, connections);
        CollectionAssert.AreEquivalent(
            ExpectedConnectedMarkerIds,
            new[] { connections[0].FromMarkerId, connections[0].ToMarkerId });
        Assert.AreEqual(MarkerConnectionStyle.Solid, connections[0].Style);
        Assert.IsGreaterThanOrEqualTo(request.Options.MinimumInkFraction, connections[0].Confidence);
    }

    [TestMethod]
    public async Task BlankCorridorDoesNotCreateAConnection()
    {
        MarkerGroupingEvidence left = MarkerGroupingTestSupport.Evidence("left", 1, x: 12, y: 20);
        MarkerGroupingEvidence right = MarkerGroupingTestSupport.Evidence("right", 2, x: 48, y: 20);

        IReadOnlyList<MarkerConnection> connections = await new MarkerConnectionGraphBuilder().BuildAsync(
            Request(MarkerGroupingTestSupport.Frame(), [left, right]),
            CancellationToken.None);

        Assert.IsEmpty(connections);
    }

    [TestMethod]
    public async Task MaskedLegendOrAnnotationStrokeCannotBecomeAConnection()
    {
        MarkerGroupingEvidence left = MarkerGroupingTestSupport.Evidence("left", 1, x: 12, y: 20);
        MarkerGroupingEvidence right = MarkerGroupingTestSupport.Evidence("right", 2, x: 48, y: 20);
        MarkerPoint[] stroke = MarkerGroupingTestSupport.Line(
            left.Marker.Marker.Center,
            right.Marker.Marker.Center).ToArray();
        MarkerImageFrame frame = MarkerGroupingTestSupport.Frame(
            stroke,
            artifactMask: stroke);

        IReadOnlyList<MarkerConnection> connections = await new MarkerConnectionGraphBuilder().BuildAsync(
            Request(frame, [left, right]),
            CancellationToken.None);

        Assert.IsEmpty(connections, "Masked non-data strokes must not connect marker identities.");
    }

    [TestMethod]
    public async Task LineContactCanBeRecordedWithoutChangingIndependentShapeAndFillIdentity()
    {
        MarkerGroupingEvidence filled = MarkerGroupingTestSupport.Evidence(
            "filled",
            1,
            fill: GraphReader.Markers.Classification.MarkerFill.Filled,
            x: 12,
            y: 20);
        MarkerGroupingEvidence open = MarkerGroupingTestSupport.Evidence(
            "open",
            2,
            fill: GraphReader.Markers.Classification.MarkerFill.Open,
            x: 48,
            y: 20);
        MarkerPoint[] stroke = MarkerGroupingTestSupport.Line(
            filled.Marker.Marker.Center,
            open.Marker.Marker.Center).ToArray();

        IReadOnlyList<MarkerConnection> connections = await new MarkerConnectionGraphBuilder().BuildAsync(
            Request(MarkerGroupingTestSupport.Frame(stroke), [filled, open]),
            CancellationToken.None);

        Assert.HasCount(1, connections);
        Assert.AreNotEqual(filled.Marker.Fill, open.Marker.Fill);
    }

    [TestMethod]
    public async Task CancellationIsObservedBeforeConnectionSampling()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await new MarkerConnectionGraphBuilder().BuildAsync(
                Request(
                    MarkerGroupingTestSupport.Frame(),
                    [
                        MarkerGroupingTestSupport.Evidence("left", 1, x: 12),
                        MarkerGroupingTestSupport.Evidence("right", 2, x: 48),
                    ]),
                cancellation.Token));
    }

    private static MarkerConnectionRequest Request(
        MarkerImageFrame frame,
        IEnumerable<MarkerGroupingEvidence> markers) =>
        new(
            frame,
            markers,
            new MarkerConnectionOptions
            {
                MarkerExclusionRadiusScale = 1,
                CorridorHalfWidthPixels = 1.5,
                MinimumInkFraction = 0.5,
                MaximumMaskFraction = 0.2,
                MaximumHorizontalGapPixels = 100,
                MinimumSamples = 8,
            });
}
