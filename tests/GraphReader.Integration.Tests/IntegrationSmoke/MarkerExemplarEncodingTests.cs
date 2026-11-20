// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.MarkerExemplar;
using GraphReader.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace GraphReader.Integration.Tests.IntegrationSmoke;

[TestClass]
public sealed class MarkerExemplarEncodingTests
{
    [TestMethod]
    public void HollowSquaresHaveIdenticalMatchesAcrossEquivalentEncodings()
    {
        using var gray = DrawPanel();
        using var rgb = gray.CvtColor(ColorConversionCodes.GRAY2BGR);

        // Black ink with opacity 255 - g composites to the same gray sample g on paper.
        using var transparent = new Mat(gray.Size(), MatType.CV_8UC4, Scalar.All(0));
        using var opacity = new Mat();
        Cv2.BitwiseNot(gray, opacity);
        Cv2.InsertChannel(opacity, transparent, 3);

        var expected = Find(gray);
        var match = expected.Single();
        Assert.AreEqual(new MarkerExemplarPoint(64.25, 40.5), match.Center);
        Assert.IsTrue(match.Similarity is >= .9 and < 1);
        CollectionAssert.AreEqual(expected, Find(rgb));
        CollectionAssert.AreEqual(expected, Find(transparent));
    }

    [TestMethod]
    public void MixedRgbPanelKeepsTheExistingColorToGrayConversion()
    {
        using var gray = DrawPanel();
        using var rgb = gray.CvtColor(ColorConversionCodes.GRAY2BGR);
        // A color annotation at the end must preserve the established conversion
        // for the preceding neutral markers as well as for the colored pixel.
        rgb.Set(rgb.Height - 1, rgb.Width - 1, new Vec3b(30, 110, 200));
        byte[] encoded = rgb.ImEncode(".png");
        using var stream = new MemoryStream(encoded);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
        byte[] pixels = new byte[rgb.Width * rgb.Height];
        converted.CopyPixels(pixels, rgb.Width, 0);
        var request = Request(rgb, encoded);
        var expected = MarkerExemplarMatcher.Find(request, pixels, CancellationToken.None).ToArray();
        Assert.AreEqual(new MarkerExemplarPoint(64.25, 40.5), expected.Single().Center);
        CollectionAssert.AreEqual(expected, Find(rgb));
    }

    private static Mat DrawPanel()
    {
        var gray = new Mat(80, 128, MatType.CV_8UC1, Scalar.All(239));
        foreach (int x in new[] { 24, 64 })
            Cv2.Rectangle(gray, new Rect(x - 4, 36, 9, 9), Scalar.All(35), 2);
        // Give the target a lighter top edge so equality also checks a non-perfect score.
        Cv2.Line(gray, new Point(60, 36), new Point(68, 36), Scalar.All(112));
        Cv2.Rectangle(gray, new Rect(96, 36, 9, 9), Scalar.All(35), -1);
        Cv2.Line(gray, new Point(40, 62), new Point(52, 62), Scalar.All(35), 2);
        Cv2.Line(gray, new Point(46, 56), new Point(46, 68), Scalar.All(35), 2);
        return gray;
    }

    private static MarkerExemplarCandidate[] Find(Mat image)
    {
        byte[] encoded = image.ImEncode(".png");
        var response = MarkerExemplarWorker.Process(Request(image, encoded), encoded, new(0, 0, 0, false), CancellationToken.None);
        Assert.IsNull(response.FailureCode);
        return response.Candidates.ToArray();
    }

    private static MarkerExemplarRequest Request(Mat image, byte[] encoded)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(encoded));
        return new("hollow-square", hash, hash, hash, "panel", image.Width, image.Height,
            new(15, 31, 17, 17), new(4, 4, 120, 72), "seed", "series", new(24.25, 40.5));
    }
}
