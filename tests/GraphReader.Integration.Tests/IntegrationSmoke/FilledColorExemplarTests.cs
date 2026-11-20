// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Security.Cryptography;
using GraphReader.App.Integration.MarkerExemplar;
using GraphReader.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class FilledColorExemplarTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GreenDisksWithOtherSeriesAndAMissingMark(bool throughChild)
    {
        string? app = Environment.GetEnvironmentVariable("GRAPHREADER_MARKER_WORKER_APP");
        if (throughChild && string.IsNullOrEmpty(app))
            Assert.Inconclusive("Set GRAPHREADER_MARKER_WORKER_APP to the built Windows application.");
        await using var child = throughChild ? new MarkerExemplarWorkerClient(app!) : null;
        using var panel = new Mat(100, 224, MatType.CV_8UC3, Scalar.All(248));
        var green = new Scalar(60, 170, 35);
        var seed = new Point(26, 32);
        var expected = new[] { new Point(67, 47), new Point(119, 35) };
        Cv2.Rectangle(panel, new Rect(44, 20, 53, 47), Scalar.All(186), -1);
        Cv2.Line(panel, seed, expected[0], green, 1, LineTypes.AntiAlias);
        Cv2.Line(panel, expected[0], expected[1], green, 1, LineTypes.AntiAlias);
        foreach (Point point in expected.Prepend(seed)) Cv2.Circle(panel, point, 6, green, -1, LineTypes.AntiAlias);

        // An empty session lies on the connector; the other shapes are independently placed.
        Cv2.Circle(panel, new Point(157, 42), 6, new Scalar(45, 75, 195), -1, LineTypes.AntiAlias);
        Cv2.Circle(panel, new Point(196, 40), 6, Scalar.All(110), -1, LineTypes.AntiAlias);
        Cv2.Circle(panel, new Point(37, 77), 6, green, 2, LineTypes.AntiAlias);
        Cv2.Rectangle(panel, new Rect(79, 71, 11, 11), green, -1, LineTypes.AntiAlias);
        Cv2.Circle(panel, new Point(138, 76), 10, green, -1, LineTypes.AntiAlias);

        byte[] encoded = panel.ImEncode(".png");
        string hash = Convert.ToHexStringLower(SHA256.HashData(encoded));
        var request = new MarkerExemplarRequest("green", hash, hash, hash, "panel", panel.Width, panel.Height,
            new(seed.X - 9, seed.Y - 9, 17, 17), new(4, 4, 216, 91), "seed", "response", new(seed.X, seed.Y))
            { IsFilledCircleSeries = true };
        foreach (bool color in new[] { false, true })
        {
            var selected = request with { IsFilledCircleSeries = color };
            var response = child is null
                ? MarkerExemplarWorker.Process(selected, encoded, new(0, 0, 0, false), CancellationToken.None)
                : await child.FindAsync(selected, encoded, CancellationToken.None);
            Assert.IsNull(response.FailureCode);
            Assert.AreEqual(throughChild, response.Runtime!.CpuBudget.Verified);
            context.WriteLine($"Child={throughChild}, verified CPU budget={response.Runtime.CpuBudget.Verified}, OpenCV threads={response.Runtime.OpenCvThreads}.");
            context.WriteLine($"{response.Runtime.Algorithm} returned {string.Join(", ", response.Candidates.Select(p => $"{p.Center} {p.Similarity:F3}"))}");
            if (!color) continue;
            Assert.AreEqual(MarkerExemplarValidation.InkOverlapAlgorithm, response.Runtime.Algorithm);
            Assert.HasCount(expected.Length, response.Candidates);
            foreach (var point in expected)
                Assert.HasCount(1, response.Candidates.Where(p => Math.Abs(p.Center.X - point.X) <= 1 && Math.Abs(p.Center.Y - point.Y) <= 1));
        }
    }
}
