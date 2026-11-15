// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Security.Cryptography;
using System.IO;
using GraphReader.Axis;
using OpenCvSharp;

namespace GraphReader.CalibrationOcrProbe;

internal static class OwnedNativeCandidateCheck
{
    private sealed class SnapshotObserver : IAxisGeometryCandidateObserver
    {
        internal IReadOnlyList<AxisGeometryCandidateSnapshot>? Candidates { get; private set; }

        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) =>
            Candidates = candidates;
    }

    private readonly record struct RoundedPlot(int Left, int Bottom, int Right, int Top);

    internal static async Task<object> RunAsync(CpuBudgetJob budget, CancellationToken cancellationToken)
    {
        if (budget.MaximumLogicalCores is <= 0 or > 0.5 || !budget.SilentBreakawayVerified)
            throw new InvalidDataException();
        Cv2.SetNumThreads(1);
        if (Cv2.GetNumThreads() != 1) throw new InvalidOperationException("OpenCV thread limit was not applied.");

        var results = new List<object>();
        foreach (string name in new[] { "black_axes_marker", "gray_axes_marker", "two_panels", "gray_two_pixel_plot" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            (int width, int height, byte[] pixels) = name == "gray_two_pixel_plot"
                ? ThickGrayPlot()
                : OwnedGrayAxisCheck.Fixture(name);
            var frame = new GrayscaleLineCandidateFrame(width, height, width, pixels);
            IReadOnlyList<GeometryLineCandidate>? lines = null;
            string? providerError = null;
            try
            {
                lines = await new OpenCvLineCandidateProvider()
                    .DetectLinesAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (OutOfMemoryException) { throw; }
            catch (Exception error) { providerError = error.GetType().Name; }
            var observer = new SnapshotObserver();
            string? detectorError = null;
            if (lines is not null)
            {
                try
                {
                    _ = await new AxisGeometryDetector().DetectAsync(
                        new AxisGeometryRequest(width, height, lines)
                        {
                            CandidateObserver = observer,
                            MaximumExaminedPairs = 128,
                        }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (OutOfMemoryException) { throw; }
                catch (AxisGeometryDetectionException error) { detectorError = error.Code; }
                catch (Exception error) { detectorError = error.GetType().Name; }
            }

            AxisGeometryCandidateSnapshot[] candidates = observer.Candidates?.ToArray() ?? [];
            RoundedPlot[] rounded = candidates.Select(Round).Distinct().ToArray();
            var roundedCandidateChecks = candidates.Select(candidate => new
            {
                candidate.Rank,
                rounded = Round(candidate),
                satisfies_native_geometry_checks = SatisfiesNativeGeometryChecks(candidate, width, height),
            }).ToArray();
            results.Add(new
            {
                name,
                width,
                height,
                image_sha256 = Convert.ToHexStringLower(SHA256.HashData(pixels)),
                line_candidate_count = lines?.Count,
                ranked_candidate_count = candidates.Length,
                distinct_rounded_plot_geometry_count = rounded.Length,
                rounded_plot_geometries = rounded,
                provider_error = providerError,
                detector_error = detectorError,
                rounded_candidate_checks = roundedCandidateChecks,
                candidates = candidates.Select(candidate => new
                {
                    candidate.Rank,
                    candidate.Score,
                    candidate.Intersection,
                    candidate.XAxisLine,
                    candidate.YAxisLine,
                    candidate.BottomRight,
                    candidate.TopRight,
                    candidate.TopLeft,
                    candidate.XSupportingCandidateIds,
                    candidate.YSupportingCandidateIds,
                }).ToArray(),
            });
        }

        return new
        {
            schema = "graphreader.owned-native-candidate-identity-check.v1",
            scope = "owned_synthetic_geometry_only",
            review_only = true,
            private_reads = 0,
            accuracy_claim = false,
            budget.LogicalProcessors,
            budget.Rate,
            budget.MaximumLogicalCores,
            cases = results,
        };
    }

    private static RoundedPlot Round(AxisGeometryCandidateSnapshot candidate) => new(
        (int)Math.Round(candidate.Intersection.X),
        (int)Math.Round(candidate.Intersection.Y),
        (int)Math.Round(candidate.BottomRight.X),
        (int)Math.Round(candidate.TopLeft.Y));

    private static bool SatisfiesNativeGeometryChecks(AxisGeometryCandidateSnapshot candidate, int width, int height)
    {
        PixelPoint[] corners = [candidate.Intersection, candidate.BottomRight, candidate.TopRight, candidate.TopLeft];
        if (corners.Any(c => !double.IsFinite(c.X) || !double.IsFinite(c.Y) || c.X < 0 || c.Y < 0 || c.X >= width || c.Y >= height) ||
            Math.Abs(candidate.Intersection.Y - candidate.BottomRight.Y) > 1 ||
            Math.Abs(candidate.TopLeft.Y - candidate.TopRight.Y) > 1 ||
            Math.Abs(candidate.Intersection.X - candidate.TopLeft.X) > 1 ||
            Math.Abs(candidate.BottomRight.X - candidate.TopRight.X) > 1 ||
            !double.IsFinite(candidate.Score)) return false;
        RoundedPlot plot = Round(candidate);
        return plot.Left >= 0 && plot.Top >= 0 && plot.Right < width && plot.Bottom < height &&
            plot.Left < plot.Right && plot.Top < plot.Bottom;
    }

    private static (int Width, int Height, byte[] Pixels) ThickGrayPlot()
    {
        const int width = 128, height = 128;
        const byte gray = 192;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int x = 20; x <= 115; x++)
        {
            pixels[99 * width + x] = gray;
            pixels[100 * width + x] = gray;
        }
        for (int y = 10; y <= 100; y++)
        {
            pixels[y * width + 20] = gray;
            pixels[y * width + 21] = gray;
        }
        return (width, height, pixels);
    }
}
