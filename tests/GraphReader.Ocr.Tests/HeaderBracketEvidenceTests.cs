// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class HeaderBracketEvidenceTests
{
    [TestMethod]
    [DataRow(1, 0)]
    [DataRow(2, 15)]
    public void DetectsTwoDownwardHooksWithoutChangingOriginalPixels(int scale, int offset)
    {
        var fixture = Fixture(scale, offset);
        byte[] before = fixture.Image.Pixels.ToArray();
        var evidence = new HeaderBracketEvidence(fixture.Image);
        Assert.IsTrue(evidence.HasBracketBelow(fixture.Label, offset + 72 * scale));
        CollectionAssert.AreEqual(before, fixture.Image.Pixels.ToArray());
    }

    [TestMethod]
    [DataRow("underline")]
    [DataRow("one-hook")]
    [DataRow("upward")]
    [DataRow("short-line")]
    [DataRow("far-below")]
    public void DoesNotTreatAnUnderlineOrIncompleteBracketAsEvidence(string defect)
    {
        var fixture = Fixture(defect: defect);
        Assert.IsFalse(new HeaderBracketEvidence(fixture.Image).HasBracketBelow(fixture.Label, 72));
    }

    [TestMethod]
    public void HooksMustFitAboveTheCorroboratedHeadingRow()
    {
        var fixture = Fixture();
        Assert.IsFalse(new HeaderBracketEvidence(fixture.Image).HasBracketBelow(fixture.Label, 63));
    }

    [TestMethod]
    public void InvalidImagesBoundsAndCancellationFailClosed()
    {
        var fixture = Fixture();
        Assert.ThrowsExactly<ArgumentException>(() => new HeaderBracketEvidence(fixture.Image with { SourceImage = OcrSourceImage.Enhanced }));
        Assert.ThrowsExactly<ArgumentException>(() => new HeaderBracketEvidence(fixture.Image with { OriginalToImage = new(2, 2, 0, 0) }));
        var evidence = new HeaderBracketEvidence(fixture.Image);
        Assert.ThrowsExactly<ArgumentException>(() => evidence.HasBracketBelow(new(-1, 1, 20, 10), 72));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => new HeaderBracketEvidence(fixture.Image, cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => evidence.HasBracketBelow(fixture.Label, 72, cancellation.Token));
    }

    private static (OcrImage Image, OcrRectangle Label) Fixture(int scale = 1, int offset = 0, string? defect = null)
    {
        int width = 240 * scale + offset, height = 140 * scale + offset, stride = width + 5;
        byte[] pixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
        void Stroke(int x0, int y0, int x1, int y1)
        {
            for (int y = offset + y0 * scale; y < offset + (y1 + 1) * scale; y++)
            for (int x = offset + x0 * scale; x < offset + (x1 + 1) * scale; x++) pixels[y * stride + x] = 0;
        }
        int top = defect == "far-below" ? 80 : 61;
        int left = defect == "short-line" ? 90 : 60, right = defect == "short-line" ? 110 : 170;
        Stroke(left, top, right, top);
        if (defect == "upward") { Stroke(left, top - 7, left, top); Stroke(right, top - 7, right, top); }
        else if (defect != "underline")
        {
            Stroke(left, top, left, top + 6);
            if (defect != "one-hook") Stroke(right, top, right, top + 6);
        }
        return (new OcrImage(width, height, stride, pixels, OcrSourceImage.Original, OcrFrameTransform.Identity),
            new OcrRectangle(offset + 85 * scale, offset + 45 * scale, 60 * scale, 15 * scale));
    }
}
