// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Markers.Classification;
using GraphReader.Markers.Detection;
using GraphReader.MarkerContextEvidence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.MarkerContextEvidence.Tests;

[TestClass]
public sealed class MarkerContextParityTests
{
    private static readonly int[] Nchw32 = [1, 1, 32, 32];
    private static readonly int[] Nchw65 = [1, 1, 65, 65];

    [TestMethod]
    public void ProductionSamplerMatchesPythonAcrossContextsAndAffineFrameWhileKeepingMasksProvenanceOnly()
    {
        var fixture = CreateSyntheticFrame(
            width: 61,
            height: 47,
            transform: new MarkerAffineTransform(1.25, 0.10, 1.5, 0.05, 0.80, 2.25),
            marker: CreateMarker(21.2, 17.8, 3.0),
            contentBounds: null);
        MarkerContextEvidence baseline = Capture(fixture, 32, 32, 2.25, "affine-default");
        MarkerContextEvidence expanded = Capture(fixture, 32, 32, 4.5, "affine-expanded-radius");

        Assert.AreEqual(3, baseline.Input.ChannelCount);
        Assert.AreEqual(61 * 47 * 3, baseline.Input.ChannelsFirstPixels.Length);
        // These are exact provenance-snapshot checks only. Neither sampler reads either mask plane.
        Assert.AreEqual(61 * 47, baseline.Input.OcrMask.Length);
        Assert.AreEqual(61 * 47, baseline.Input.ArtifactMask.Length);
        CollectionAssert.AreEqual(fixture.Frame.ChannelsFirstPixels.ToArray(), baseline.Input.ChannelsFirstPixels);
        CollectionAssert.AreEqual(fixture.Frame.OcrMask.Values.ToArray(), baseline.Input.OcrMask);
        CollectionAssert.AreEqual(fixture.Frame.ArtifactMask.Values.ToArray(), baseline.Input.ArtifactMask);
        CollectionAssert.AreEqual(Nchw32, baseline.OutputShapeNchw);
        CollectionAssert.AreEqual(Nchw32, expanded.OutputShapeNchw);
        Assert.IsFalse(baseline.ProductionTensor.SequenceEqual(expanded.ProductionTensor),
            "Changing radius context should change this nonuniform synthetic crop.");
        AssertPythonParity(baseline);
        AssertPythonParity(expanded);
        AssertMatrixMapsMarkerCenterToPatchCenter(expanded);
    }

    [TestMethod]
    public void ExpandedSpatialTensorMatchesPythonAndRecordsEdgePaddingAndSourceMapping()
    {
        var fixture = CreateSyntheticFrame(
            width: 31,
            height: 29,
            transform: MarkerAffineTransform.Identity,
            marker: CreateMarker(2.4, 3.1, 2.0),
            contentBounds: null);
        MarkerContextEvidence evidence = Capture(fixture, 65, 65, 4.5, "identity-expanded-spatial-edge");

        CollectionAssert.AreEqual(Nchw65, evidence.OutputShapeNchw);
        Assert.IsTrue(evidence.PatchToOriginalMatrix3x3.SequenceEqual(evidence.PatchToFrameMatrix3x3));
        AssertPythonParity(evidence, minimumPaddingSamples: 1);
        AssertMatrixMapsMarkerCenterToPatchCenter(evidence);
    }

    [TestMethod]
    public void ContentBoundsBecomeWhiteInkPaddingBeforeInterpolationAndArePreserved()
    {
        MarkerContextRectangle bounds = new(14, 11, 14, 14);
        var fixture = CreateSyntheticFrame(
            width: 42,
            height: 38,
            transform: MarkerAffineTransform.Identity,
            marker: CreateMarker(20.0, 17.0, 3.0),
            contentBounds: bounds);
        MarkerContextEvidence bounded = Capture(fixture, 32, 32, 4.5, "identity-isolated-symbol");
        var unboundedFixture = fixture with { Options = fixture.Options with { OriginalPixelContentBounds = null } };
        MarkerContextEvidence unbounded = Capture(unboundedFixture, 32, 32, 4.5, "identity-unbounded-symbol");

        Assert.AreEqual(bounds, bounded.Input.Options.OriginalPixelContentBounds);
        Assert.IsFalse(bounded.ProductionTensor.SequenceEqual(unbounded.ProductionTensor),
            "Synthetic dark neighboring pixels outside the measured symbol must be removed by content bounds.");
        AssertPythonParity(bounded);
        AssertPythonParity(unbounded);
    }

    private static MarkerContextEvidence Capture(SyntheticFixture fixture, int width, int height, double radiusScale, string caseId)
    {
        MarkerPatchExtractionOptions options = fixture.Options with
        {
            Width = width,
            Height = height,
            RadiusScale = radiusScale,
        };
        return MarkerContextEvidenceCapture.Capture(caseId, fixture.Frame, fixture.Marker, options);
    }

    private static void AssertPythonParity(MarkerContextEvidence evidence, int minimumPaddingSamples = 0)
    {
        string projectRoot = FindProjectRoot(Environment.CurrentDirectory);
        string script = Path.Combine(projectRoot, "tools", "GraphReader.MarkerContextEvidence", "python", "marker_context_reference.py");
        Assert.IsTrue(File.Exists(script), "Python parity reference is missing.");
        string fixtureId = Guid.NewGuid().ToString("N");
        string root = Path.Combine(projectRoot, "artifacts", "goal22-runs", "marker-context-evidence-test-fixtures", fixtureId);
        Directory.CreateDirectory(root);
        string expectedParent = Path.Combine(projectRoot, "artifacts", "goal22-runs", "marker-context-evidence-test-fixtures");
        Assert.IsTrue(root.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        string inputPath = Path.Combine(root, evidence.Input.CaseId + ".json");
        try
        {
            byte[] evidenceBytes = MarkerContextEvidenceCapture.Serialize(evidence);
            File.WriteAllBytes(inputPath, evidenceBytes);
            string evidenceHash = Convert.ToHexStringLower(SHA256.HashData(evidenceBytes));

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    WorkingDirectory = projectRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add(script);
            process.StartInfo.ArgumentList.Add("--compare");
            process.StartInfo.ArgumentList.Add(inputPath);
            if (!process.Start())
            {
                Assert.Inconclusive("Python executable could not be started for production crop parity.");
            }
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode,
                $"Python parity failed for evidence SHA-256 {evidenceHash}. stdout={output} stderr={error}");
            using JsonDocument result = JsonDocument.Parse(output);
            JsonElement rootElement = result.RootElement;
            Assert.AreEqual("graphreader.marker-context-crop-parity.v1", rootElement.GetProperty("schema").GetString());
            Assert.AreEqual("synthetic_fixture", rootElement.GetProperty("scope").GetString());
            Assert.AreEqual(evidence.Input.CaseId, rootElement.GetProperty("case_id").GetString());
            Assert.IsTrue(rootElement.GetProperty("parity").GetBoolean());
            Assert.AreEqual(0, rootElement.GetProperty("mismatched_values").GetInt32());
            Assert.IsTrue(rootElement.GetProperty("maximum_absolute_error").GetDouble() <=
                rootElement.GetProperty("absolute_tolerance").GetDouble());
            Assert.IsTrue(rootElement.GetProperty("maximum_transform_absolute_error").GetDouble() <= 1e-9);
            Assert.IsTrue(rootElement.GetProperty("padding_samples").GetInt32() >= minimumPaddingSamples);
            Assert.AreEqual(0, rootElement.GetProperty("optimizer_steps").GetInt32());
            Assert.IsFalse(rootElement.GetProperty("annotations_read").GetBoolean());
            Assert.IsFalse(rootElement.GetProperty("private_or_sealed_data_read").GetBoolean());
        }
        catch (Win32Exception exception)
        {
            Assert.Inconclusive($"Python executable is unavailable for parity: {exception.Message}");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void AssertMatrixMapsMarkerCenterToPatchCenter(MarkerContextEvidence evidence)
    {
        double[] matrix = evidence.PatchToOriginalMatrix3x3;
        double x = matrix[0] * ((evidence.OutputShapeNchw[3] - 1) / 2d) + matrix[1] * ((evidence.OutputShapeNchw[2] - 1) / 2d) + matrix[2];
        double y = matrix[3] * ((evidence.OutputShapeNchw[3] - 1) / 2d) + matrix[4] * ((evidence.OutputShapeNchw[2] - 1) / 2d) + matrix[5];
        Assert.AreEqual(evidence.Input.MarkerCenterOriginalPixels.X, x, 1e-9);
        Assert.AreEqual(evidence.Input.MarkerCenterOriginalPixels.Y, y, 1e-9);
    }

    private static SyntheticFixture CreateSyntheticFrame(
        int width,
        int height,
        MarkerAffineTransform transform,
        MarkerCenter marker,
        MarkerContextRectangle? contentBounds)
    {
        int planeSize = checked(width * height);
        var planes = new float[planeSize * 3];
        var ocr = new float[planeSize];
        var artifacts = new float[planeSize];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width) + x;
                planes[index] = (float)((x + (2 * y)) % 29) / 28f;
                planes[planeSize + index] = (float)((3 * x + y + 5) % 31) / 30f;
                planes[(2 * planeSize) + index] = (float)((x + (5 * y) + 9) % 37) / 36f;
                if (contentBounds is { } box &&
                    (x < box.X || x >= box.X + box.Width || y < box.Y || y >= box.Y + box.Height))
                {
                    planes[index] = 0.05f;
                    planes[planeSize + index] = 0.1f;
                    planes[(2 * planeSize) + index] = 0.15f;
                }
                if (x == width / 2 || y == height / 2)
                {
                    ocr[index] = 0.75f;
                }
                if (Math.Abs(x - (width / 3)) <= 1 && Math.Abs(y - (height / 3)) <= 1)
                {
                    artifacts[index] = 1f;
                }
            }
        }

        var frame = new MarkerImageFrame(
            width,
            height,
            3,
            planes,
            MarkerSourceImage.Original,
            transform,
            new MarkerMask(width, height, ocr),
            new MarkerMask(width, height, artifacts));
        IReadOnlyDictionary<string, MarkerRectangle>? bounds = contentBounds is { } rectangle
            ? new Dictionary<string, MarkerRectangle>(StringComparer.Ordinal)
            {
                [marker.MarkerId] = new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height),
            }
            : null;
        return new SyntheticFixture(frame, marker, new MarkerPatchExtractionOptions(32, 32, 1)
        {
            RadiusScale = 2.25,
            MinimumHalfExtentFramePixels = 4,
            PaddingValue = 0.125f,
            OriginalPixelContentBounds = bounds,
        });
    }

    private static MarkerCenter CreateMarker(double x, double y, double radius) => new(
        "synthetic-marker-1",
        new MarkerPoint(x, y),
        radius,
        0,
        1,
        MarkerSourceImage.Original);

    private static string FindProjectRoot(string start)
    {
        DirectoryInfo? current = new(Path.GetFullPath(start));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")) &&
                File.Exists(Path.Combine(current.FullName, "src", "GraphReader.Markers", "GraphReader.Markers.csproj")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Graph Auto Reader project root.");
    }

    private sealed record SyntheticFixture(
        MarkerImageFrame Frame,
        MarkerCenter Marker,
        MarkerPatchExtractionOptions Options);
}
