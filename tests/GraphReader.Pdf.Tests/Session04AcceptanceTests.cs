// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace GraphReader.Pdf.Tests;

[TestClass]
public sealed class Session04AcceptanceTests
{
    private static readonly Guid RunId = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private static readonly Guid ProjectId = Guid.Parse("20000000-0000-0000-0000-000000000004");
    private static readonly Guid FigureId = Guid.Parse("30000000-0000-0000-0000-000000000004");
    private static readonly Guid PanelId = Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static readonly Guid EmbeddedImageId = Guid.Parse("50000000-0000-0000-0000-000000000004");
    private static readonly byte[] SyntheticScannedPdf = "%PDF-1.7\n% fixed scanned fixture\n"u8.ToArray();
    private static readonly byte[] SyntheticEncryptedPdf = Convert.FromBase64String(
        "JVBERi0xLjMKJeLjz9MKMSAwIG9iago8PAovUHJvZHVjZXIgPDdjYTljZTk5Y2E+Ci9UaXRsZSA8NWZhOWQwODljNDEx" +
        "NmViNzYwNGZhMDk2ZmYwYTZlZDMwNDI1ZmM4MjBmMzc0NjYyY2IwZTA3Pgo+PgplbmRvYmoKMiAwIG9iago8PAovVHlw" +
        "ZSAvUGFnZXMKL0NvdW50IDEKL0tpZHMgWyA0IDAgUiBdCj4+CmVuZG9iagozIDAgb2JqCjw8Ci9UeXBlIC9DYXRhbG9n" +
        "Ci9QYWdlcyAyIDAgUgo+PgplbmRvYmoKNCAwIG9iago8PAovVHlwZSAvUGFnZQovUmVzb3VyY2VzIDw8Cj4+Ci9NZWRp" +
        "YUJveCBbIDAuMCAwLjAgMjAwIDIwMCBdCi9QYXJlbnQgMiAwIFIKPj4KZW5kb2JqCjUgMCBvYmoKPDwKL1YgMgovUiAz" +
        "Ci9MZW5ndGggMTI4Ci9QIDQyOTQ5NjcyOTIKL0ZpbHRlciAvU3RhbmRhcmQKL08gPGFlMzc5MGQxMWRkODg4YjAzZDU2" +
        "ZjE1M2ZmZmU5NGYxM2E2ZTY1MWNjOWNhMGJmYzA3MzhjOGQzN2JmOWIxN2E+Ci9VIDxiNWQyMzZlOTJjYmIyNDBiOWU2" +
        "NGUwYjIyZGIwODg4MTI4YmY0ZTVlNGU3NThhNDE2NDAwNGU1NmZmZmEwMTA4Pgo+PgplbmRvYmoKeHJlZgowIDYKMDAw" +
        "MDAwMDAwMCA2NTUzNSBmIAowMDAwMDAwMDE1IDAwMDAwIG4gCjAwMDAwMDAxMjMgMDAwMDAgbiAKMDAwMDAwMDE4MiAw" +
        "MDAwMCBuIAowMDAwMDAwMjMxIDAwMDAwIG4gCjAwMDAwMDAzMjUgMDAwMDAgbiAKdHJhaWxlcgo8PAovU2l6ZSA2Ci9S" +
        "b290IDMgMCBSCi9JbmZvIDEgMCBSCi9JRCBbIDwzMTMxNjQzMzM3Mzk2NDMzNjYzNzM3MzY2NDM2MzYzMTYzMzgzMTY0" +
        "MzAzODYzMzEzNTM1MzE2NjMwNjUzMzY2PiA8MzEzMTY0MzMzNzM5NjQzMzY2MzczNzM2NjQzNjM2MzE2MzM4MzE2NDMw" +
        "Mzg2MzMxMzUzNTMxNjYzMDY1MzM2Nj4gXQovRW5jcnlwdCA1IDAgUgo+PgpzdGFydHhyZWYKNTQwCiUlRU9GCg==");
    private static readonly byte[] SyntheticRenderedPage = CreateBlankPng(width: 16, height: 16);
    private static readonly int[] ExpectedPanelOrders = [1, 2, 3];
    private static readonly int[] RightAngleRotations = [0, 90, 180, 270];
    private static readonly string[] ExpectedParticipantLabels =
        ["Participant Alpha", "Participant Beta", "Participant Gamma"];

    [TestMethod]
    public async Task PdfPigInspectorExtractsFixedBornDigitalImageCaptionMetadataAndVectorsDeterministically()
    {
        byte[] pdf = CreateBornDigitalPdf();
        var inspector = new PdfPigDocumentInspector();
        var request = new PdfInspectionRequest(
            new ImmutableByteBuffer(pdf),
            "fixed-born-digital.pdf");

        PdfInspectionResult first = await inspector.InspectAsync(request, CancellationToken.None);
        PdfInspectionResult second = await inspector.InspectAsync(request, CancellationToken.None);

        Assert.IsTrue(first.Succeeded, InspectionFailureSummary(first));
        Assert.IsTrue(second.Succeeded, InspectionFailureSummary(second));
        Assert.AreEqual("Fixed Synthetic Article", first.Document!.Metadata.Title);
        Assert.AreEqual("Test Author", first.Document.Metadata.Author);
        Assert.AreEqual(1, first.Document.Pages.Count);
        PdfPageSnapshot page = first.Document.Pages[0];
        Assert.AreEqual(1, page.EmbeddedImages.Count);
        Assert.AreEqual(640, page.EmbeddedImages[0].PixelWidth);
        Assert.AreEqual(480, page.EmbeddedImages[0].PixelHeight);
        Assert.AreEqual("image/png", page.EmbeddedImages[0].MediaType);
        Assert.IsTrue(page.TextBlocks.Any(static block =>
            block.Role == PdfTextRole.Caption &&
            block.Text.Contains("Instructional intervention", StringComparison.Ordinal)));
        Assert.IsTrue(page.VectorLines.Count >= 4);
        Assert.AreEqual(first.Document.DocumentSha256, second.Document!.DocumentSha256);
        Assert.AreEqual(page.EmbeddedImages[0].ImageId, second.Document.Pages[0].EmbeddedImages[0].ImageId);
        Assert.AreEqual(page.EmbeddedImages[0].Sha256, second.Document.Pages[0].EmbeddedImages[0].Sha256);
        CollectionAssert.AreEqual(
            page.TextBlocks.Select(static block => block.BlockId).ToArray(),
            second.Document.Pages[0].TextBlocks.Select(static block => block.BlockId).ToArray());
    }

    [TestMethod]
    public async Task PdfPigInspectorPreservesNonzeroCropBoxesAndRightAnglePageRotations()
    {
        PdfInspectionResult result = await new PdfPigDocumentInspector().InspectAsync(
            new PdfInspectionRequest(
                new ImmutableByteBuffer(CreateRotatedCropBoxPdf()),
                "rotated-crop-boxes.pdf"),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, InspectionFailureSummary(result));
        Assert.AreEqual(2, result.Document!.Pages.Count);
        AssertPageGeometry(
            result.Document.Pages[0],
            expectedVisibleBounds: new PdfRectD(10, 20, 200, 100),
            expectedRotation: 90,
            expectedWidth: 100,
            expectedHeight: 200);
        AssertPageGeometry(
            result.Document.Pages[1],
            expectedVisibleBounds: new PdfRectD(30, 40, 300, 150),
            expectedRotation: 270,
            expectedWidth: 150,
            expectedHeight: 300);
    }

    [TestMethod]
    public async Task PdfPigInspectorTreatsLiteralEncryptMarkerInMalformedPdfAsCorrupt()
    {
        var inspector = new PdfPigDocumentInspector();
        byte[] corruptWithEncryptMarker = "%PDF-1.7\ntrailer\n<< /Encrypt 1 0 R >>\n%%EOF\n"u8.ToArray();
        byte[] corrupt = "%PDF-1.7"u8.ToArray();

        PdfInspectionResult markerWithoutPassword = await inspector.InspectAsync(
            new PdfInspectionRequest(new ImmutableByteBuffer(corruptWithEncryptMarker), "marker.pdf"),
            CancellationToken.None);
        PdfInspectionResult markerWithPassword = await inspector.InspectAsync(
            new PdfInspectionRequest(
                new ImmutableByteBuffer(corruptWithEncryptMarker),
                "marker.pdf",
                Password: "irrelevant"),
            CancellationToken.None);
        PdfInspectionResult corruptResult = await inspector.InspectAsync(
            new PdfInspectionRequest(new ImmutableByteBuffer(corrupt), "corrupt.pdf"),
            CancellationToken.None);

        AssertStructuredInspectionError(markerWithoutPassword, PdfInspectionFailureCodes.CorruptDocument);
        AssertStructuredInspectionError(markerWithPassword, PdfInspectionFailureCodes.CorruptDocument);
        AssertStructuredInspectionError(corruptResult, PdfInspectionFailureCodes.CorruptDocument);
    }

    [TestMethod]
    public async Task PdfPigInspectorRequiresAndValidatesPasswordForActualEncryptedPdf()
    {
        var inspector = new PdfPigDocumentInspector();

        PdfInspectionResult missingPassword = await inspector.InspectAsync(
            new PdfInspectionRequest(
                new ImmutableByteBuffer(SyntheticEncryptedPdf),
                "encrypted.pdf"),
            CancellationToken.None);
        PdfInspectionResult wrongPassword = await inspector.InspectAsync(
            new PdfInspectionRequest(
                new ImmutableByteBuffer(SyntheticEncryptedPdf),
                "encrypted.pdf",
                Password: "wrong-password"),
            CancellationToken.None);
        PdfInspectionResult correctPassword = await inspector.InspectAsync(
            new PdfInspectionRequest(
                new ImmutableByteBuffer(SyntheticEncryptedPdf),
                "encrypted.pdf",
                Password: "correct-password"),
            CancellationToken.None);

        AssertStructuredInspectionError(missingPassword, PdfInspectionFailureCodes.PasswordRequired);
        AssertStructuredInspectionError(wrongPassword, PdfInspectionFailureCodes.PasswordRejected);
        Assert.IsTrue(correctPassword.Succeeded, InspectionFailureSummary(correctPassword));
        Assert.AreEqual(1, correctPassword.Document!.Pages.Count);
    }

    [TestMethod]
    public async Task PdfPigInspectorPropagatesPreCancellationWithoutReturningPartialEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = await new PdfPigDocumentInspector().InspectAsync(
                new PdfInspectionRequest(
                    new ImmutableByteBuffer(CreateBornDigitalPdf()),
                    "cancelled.pdf"),
                cancellation.Token);
            Assert.Fail("A pre-cancelled inspection should propagate cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    [TestMethod]
    public async Task PanelizationFindsThreeVerticallyStackedPanelsInStableTopToBottomOrder()
    {
        var engine = new PanelizationEngine();
        PdfPanelizationInput input = ThreeStackedPanelInput();

        PdfPanelizationResult first = await engine.ProposeAsync(input, CancellationToken.None);
        PdfPanelizationResult second = await engine.ProposeAsync(input, CancellationToken.None);

        Assert.IsTrue(first.Succeeded, PanelizationFailureSummary(first));
        Assert.AreEqual(1, first.Figures.Count);
        Assert.AreEqual(PdfFigureSourceKind.VectorPageRegion, first.Figures[0].SourceKind);
        Assert.AreEqual(3, first.Panels.Count);
        CollectionAssert.AreEqual(ExpectedPanelOrders, first.Panels.Select(static panel => panel.Order).ToArray());
        Assert.IsTrue(first.Panels.Zip(first.Panels.Skip(1), static (upper, lower) =>
            upper.BoundsPagePixels.Bottom <= lower.BoundsPagePixels.Y).All(static ordered => ordered));
        CollectionAssert.AreEqual(
            ExpectedParticipantLabels,
            first.Panels.Select(static panel => panel.ParticipantLabel).ToArray());
        Assert.IsTrue(first.Panels.All(static panel =>
            panel.Caption ==
                "Figure 1. Dependent variable: task completion; independent variable: instructional support."));
        Assert.IsTrue(first.Panels.All(static panel =>
            panel.SemanticSuggestions.Count >= 2 &&
            panel.SemanticSuggestions.All(static suggestion =>
                suggestion.ReviewState == PdfSuggestionReviewState.Suggested)));

        CollectionAssert.AreEqual(
            PanelFingerprints(first),
            PanelFingerprints(second),
            "Repeated fixed inputs must produce identical figure and panel identities and geometry.");
    }

    [TestMethod]
    public async Task PanelCropsMapExactlyBackToPageCoordinatesAndNeverOverlap()
    {
        PdfPanelizationInput input = ThreeStackedPanelInput();
        PdfPanelizationResult result = await new PanelizationEngine().ProposeAsync(
            input,
            CancellationToken.None);
        var transform = new PdfPageCoordinateTransform(
            input.Page.WidthPoints,
            input.Page.HeightPoints,
            PixelWidth: 1200,
            PixelHeight: 1800);

        Assert.IsTrue(result.Succeeded, PanelizationFailureSummary(result));
        foreach (PdfPanelRecord panel in result.Panels)
        {
            AssertRectEqual(
                transform.PagePointsToPixels(panel.BoundsPagePoints),
                panel.BoundsPagePixels,
                tolerance: 0.000001);
            Assert.AreEqual(panel.BoundsPagePixels, panel.CropInSourcePixels);
            Assert.IsTrue(new PdfRectD(0, 0, 1200, 1800).Contains(panel.BoundsPagePixels));
        }

        for (var left = 0; left < result.Panels.Count; left++)
        {
            for (var right = left + 1; right < result.Panels.Count; right++)
            {
                Assert.IsFalse(Overlaps(
                    result.Panels[left].BoundsPagePixels,
                    result.Panels[right].BoundsPagePixels));
            }
        }
    }

    [TestMethod]
    public async Task PanelizationRejectsEmbeddedNonGraphFalseFigureAtFixedThreshold()
    {
        var image = new PdfEmbeddedImage(
            EmbeddedImageId,
            new PdfRectD(72, 216, 468, 360),
            pixelWidth: 936,
            pixelHeight: 720,
            mediaType: "image/png",
            new ImmutableByteBuffer(SyntheticRenderedPage),
            new string('e', 64));
        var page = new PdfPageSnapshot(1, 612, 792, [], [image], []);
        var input = new PdfPanelizationInput(
            new string('f', 64),
            page,
            RenderedPage: null,
            new PdfPanelizationOptions());

        PdfPanelizationResult result = await new PanelizationEngine().ProposeAsync(
            input,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, PanelizationFailureSummary(result));
        Assert.AreEqual(0, result.Figures.Count);
        Assert.AreEqual(0, result.Panels.Count);
        Assert.IsTrue(result.Warnings.Any(static warning =>
            warning.Contains("Rejected 1 false or low-confidence", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PanelizationPreservesSlightlyOverlappingFiguresButSuppressesEightyPercentDuplicates()
    {
        var engine = new PanelizationEngine();
        PdfPanelizationResult slightOverlap = await engine.ProposeAsync(
            OverlappingEmbeddedFiguresInput(
                new PdfRectD(50, 220, 250, 300),
                new PdfRectD(275, 220, 250, 300)),
            CancellationToken.None);
        PdfPanelizationResult duplicateOverlap = await engine.ProposeAsync(
            OverlappingEmbeddedFiguresInput(
                new PdfRectD(50, 220, 250, 300),
                new PdfRectD(75, 235, 250, 300)),
            CancellationToken.None);

        Assert.IsTrue(slightOverlap.Succeeded, PanelizationFailureSummary(slightOverlap));
        Assert.AreEqual(2, slightOverlap.Figures.Count);
        Assert.AreEqual(2, slightOverlap.Panels.Count);
        Assert.IsTrue(Overlaps(
            slightOverlap.Figures[0].BoundsPagePixels,
            slightOverlap.Figures[1].BoundsPagePixels));

        Assert.IsTrue(duplicateOverlap.Succeeded, PanelizationFailureSummary(duplicateOverlap));
        Assert.AreEqual(1, duplicateOverlap.Figures.Count);
        Assert.AreEqual(1, duplicateOverlap.Panels.Count);
    }

    [TestMethod]
    public async Task ScannedRenderedPageDetectsThreeStackedRasterPanelsWithoutPdfVectorEvidence()
    {
        var page = new PdfPageSnapshot(
            pageNumber: 1,
            widthPoints: 320,
            heightPoints: 450,
            textBlocks: [],
            embeddedImages: [],
            vectorLines: []);
        var rendered = new PdfRenderedPage(
            new ImmutableByteBuffer(CreateStackedGraphPng(width: 640, height: 900)),
            Width: 640,
            Height: 900);
        var input = new PdfPanelizationInput(
            new string('2', 64),
            page,
            rendered,
            new PdfPanelizationOptions());

        PdfPanelizationResult result = await new PanelizationEngine().ProposeAsync(
            input,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, PanelizationFailureSummary(result));
        Assert.AreEqual(1, result.Figures.Count);
        Assert.AreEqual(PdfFigureSourceKind.RenderedPage, result.Figures[0].SourceKind);
        Assert.AreEqual(3, result.Panels.Count);
        CollectionAssert.AreEqual(ExpectedPanelOrders, result.Panels.Select(static panel => panel.Order).ToArray());
    }

    [TestMethod]
    public async Task ScannedRenderedPageUsesPointMinimumAtRasterDpiAndRejectsShortLegendFrameAxis()
    {
        const int width = 1200;
        const int height = 350;
        var page = new PdfPageSnapshot(
            pageNumber: 1,
            widthPoints: width / 2d,
            heightPoints: height / 2d,
            textBlocks: [],
            embeddedImages: [],
            vectorLines: []);
        var rendered = new PdfRenderedPage(
            new ImmutableByteBuffer(CreateGraphWithShortLegendFramePng(width, height)),
            Width: width,
            Height: height);

        PdfPanelizationResult result = await new PanelizationEngine().ProposeAsync(
            new PdfPanelizationInput(
                new string('8', 64),
                page,
                rendered,
                new PdfPanelizationOptions(RenderDpi: 144)),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, PanelizationFailureSummary(result));
        Assert.AreEqual(1, result.Figures.Count,
            "A 34-pixel legend corner is shorter than the 48-pixel rendering of the 24-point axis minimum.");
        Assert.AreEqual(1, result.Panels.Count);
        PdfRectD crop = result.Panels[0].CropInSourcePixels;
        Assert.IsTrue(crop.Contains(new PdfRectD(105, 96, 855, 164)),
            "The retained proposal must preserve the complete graph plot.");
    }

    [TestMethod]
    public async Task BlankScannedRenderedPageIsRejectedInsteadOfAcceptedAsAFigure()
    {
        var page = new PdfPageSnapshot(
            pageNumber: 1,
            widthPoints: 320,
            heightPoints: 450,
            textBlocks: [],
            embeddedImages: [],
            vectorLines: []);
        var rendered = new PdfRenderedPage(
            new ImmutableByteBuffer(CreateBlankPng(width: 640, height: 900)),
            Width: 640,
            Height: 900);

        PdfPanelizationResult result = await new PanelizationEngine().ProposeAsync(
            new PdfPanelizationInput(
                new string('3', 64),
                page,
                rendered,
                new PdfPanelizationOptions()),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, PanelizationFailureSummary(result));
        Assert.AreEqual(0, result.Figures.Count);
        Assert.AreEqual(0, result.Panels.Count);
    }

    [TestMethod]
    public async Task ManualSplitAndAdjacentMergePreserveDeterministicPageMappedGeometry()
    {
        var engine = new PanelizationEngine();
        PdfPanelizationResult automatic = await engine.ProposeAsync(
            ThreeStackedPanelInput(),
            CancellationToken.None);
        PdfFigureCandidate figure = automatic.Figures.Single();
        double firstBoundary = figure.BoundsPagePixels.Y + (figure.BoundsPagePixels.Height / 3d);
        double secondBoundary = figure.BoundsPagePixels.Y + ((figure.BoundsPagePixels.Height * 2d) / 3d);

        PdfPanelizationResult split = engine.ApplySplit(
            automatic,
            new PdfManualSplitCommand(figure.FigureId, [firstBoundary, secondBoundary]));

        Assert.IsTrue(split.Succeeded, PanelizationFailureSummary(split));
        Assert.AreEqual(3, split.Panels.Count);
        Assert.IsTrue(split.Panels.All(static panel => panel.Evidence.Any(static item =>
            item.Kind == PdfPanelEvidenceKind.Manual)));
        PdfPanelRecord[] adjacent = split.Panels.OrderBy(static panel => panel.Order).Take(2).ToArray();
        PdfRectD expectedMergedBounds = new(
            adjacent[0].BoundsPagePixels.X,
            adjacent[0].BoundsPagePixels.Y,
            adjacent[0].BoundsPagePixels.Width,
            adjacent[1].BoundsPagePixels.Bottom - adjacent[0].BoundsPagePixels.Y);

        PdfPanelizationResult merged = engine.ApplyMerge(
            split,
            new PdfManualMergeCommand(adjacent.Select(static panel => panel.PanelId).ToArray()));

        Assert.IsTrue(merged.Succeeded, PanelizationFailureSummary(merged));
        Assert.AreEqual(2, merged.Panels.Count);
        PdfPanelRecord mergedPanel = merged.Panels.Single(panel =>
            panel.BoundsPagePixels == expectedMergedBounds);
        Assert.IsTrue(mergedPanel.Evidence.Any(static item => item.Kind == PdfPanelEvidenceKind.Manual));
        Assert.AreEqual(1, mergedPanel.Order);
    }

    [TestMethod]
    public void ManualMergePreservesConfirmedAndRejectedSuggestionStates()
    {
        PdfFigureCandidate figure = Figure(PdfFigureSourceKind.EmbeddedImage);
        var confirmed = new PdfSemanticSuggestion(
            PdfSemanticField.IndependentVariable,
            "Instructional support",
            "user-confirmed value",
            0.25,
            PdfSuggestionReviewState.ConfirmedByUser);
        var suggestedDuplicate = confirmed with
        {
            SourceText = "automatic suggestion",
            Confidence = 0.99,
            ReviewState = PdfSuggestionReviewState.Suggested,
        };
        var rejected = new PdfSemanticSuggestion(
            PdfSemanticField.DependentVariable,
            "Task completion",
            "user-rejected value",
            0.20,
            PdfSuggestionReviewState.RejectedByUser);
        var confirmedDuplicate = rejected with
        {
            SourceText = "later confirmed duplicate",
            Confidence = 0.98,
            ReviewState = PdfSuggestionReviewState.ConfirmedByUser,
        };
        PdfPanelRecord upper = ReviewPanel(
            Guid.Parse("80000000-0000-0000-0000-000000000001"),
            order: 1,
            new PdfRectD(144, 288, 936, 360),
            [confirmed, rejected]);
        PdfPanelRecord lower = ReviewPanel(
            Guid.Parse("80000000-0000-0000-0000-000000000002"),
            order: 2,
            new PdfRectD(144, 648, 936, 360),
            [suggestedDuplicate, confirmedDuplicate]);
        var current = new PdfPanelizationResult([figure], [upper, lower]);

        PdfPanelizationResult merged = new PanelizationEngine().ApplyMerge(
            current,
            new PdfManualMergeCommand([upper.PanelId, lower.PanelId]));

        Assert.IsTrue(merged.Succeeded, PanelizationFailureSummary(merged));
        PdfPanelRecord panel = merged.Panels.Single();
        Assert.AreEqual(
            PdfSuggestionReviewState.ConfirmedByUser,
            panel.SemanticSuggestions.Single(static suggestion =>
                suggestion.Field == PdfSemanticField.IndependentVariable).ReviewState);
        Assert.AreEqual(
            PdfSuggestionReviewState.RejectedByUser,
            panel.SemanticSuggestions.Single(static suggestion =>
                suggestion.Field == PdfSemanticField.DependentVariable).ReviewState);
    }

    [TestMethod]
    public async Task PanelizationPropagatesPreCancellationWithoutPartialResults()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = await new PanelizationEngine().ProposeAsync(
                ThreeStackedPanelInput(),
                cancellation.Token);
            Assert.Fail("A pre-cancelled panelization should propagate cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    [TestMethod]
    public async Task BornDigitalEmbeddedFigureIsPreferredAndCarriesUnconfirmedArticleSuggestions()
    {
        PdfDocumentSnapshot document = BornDigitalDocument();
        var inspector = new FixedInspector(document);
        var panelization = new EmbeddedPanelizationEngine();
        var backend = new FakeReviewedPdfiumBackend(SyntheticRenderedPage, width: 16, height: 16);
        var service = new PdfImportService(
            inspector,
            panelization,
            new PdfiumPageRendererAdapter(backend));

        PdfImportResult result = await service.ImportAsync(ImportRequest(), CancellationToken.None);

        Assert.IsTrue(result.Succeeded, ImportFailureSummary(result));
        Assert.AreEqual("Fixed Synthetic Article", result.Document!.Metadata.Title);
        Assert.AreEqual("Test Author", result.Document.Metadata.Author);
        Assert.AreEqual(1, result.Figures.Count);
        Assert.AreEqual(PdfFigureSourceKind.EmbeddedImage, result.Figures[0].SourceKind);
        Assert.AreEqual(EmbeddedImageId, result.Figures[0].EmbeddedImageId);
        Assert.AreEqual("Figure 1. Instructional intervention and task completion.", result.Panels[0].Caption);
        Assert.AreEqual(0, backend.CallCount, "Born-digital extraction must not rasterize the page.");
        Assert.AreEqual(1, panelization.CallCount);
        Assert.AreEqual(2, result.Panels[0].SemanticSuggestions.Count);
        Assert.IsTrue(result.Panels[0].SemanticSuggestions.All(static suggestion =>
            suggestion.ReviewState == PdfSuggestionReviewState.Suggested));
        CollectionAssert.AreEquivalent(
            new[] { PdfSemanticField.DependentVariable, PdfSemanticField.IndependentVariable },
            result.Panels[0].SemanticSuggestions.Select(static suggestion => suggestion.Field).ToArray());
    }

    [TestMethod]
    public async Task ActualInspectorPanelizerAndImporterPreferEmbeddedSourceWithoutRendering()
    {
        byte[] pdf = CreateBornDigitalPdf();
        var backend = new FakeReviewedPdfiumBackend(SyntheticRenderedPage, width: 16, height: 16);
        var service = new PdfImportService(
            new PdfPigDocumentInspector(),
            new PanelizationEngine(),
            new PdfiumPageRendererAdapter(backend));
        var request = new PdfImportRequest(
            RunId,
            ProjectId,
            new ImmutableByteBuffer(pdf),
            "fixed-born-digital.pdf",
            Password: null,
            new PdfPanelizationOptions());

        PdfImportResult result = await service.ImportAsync(request, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, ImportFailureSummary(result));
        Assert.AreEqual("Fixed Synthetic Article", result.Document!.Metadata.Title);
        Assert.AreEqual(1, result.Figures.Count);
        Assert.AreEqual(PdfFigureSourceKind.EmbeddedImage, result.Figures[0].SourceKind);
        Assert.AreEqual(1, result.Panels.Count);
        Assert.AreEqual(result.Figures[0].FigureId, result.Panels[0].FigureId);
        Assert.AreEqual(0, backend.CallCount, "Direct embedded extraction must bypass raster fallback.");
    }

    [TestMethod]
    public async Task ActualInspectorAndPanelizerPropagateParticipantAndUnconfirmedSemanticSuggestions()
    {
        byte[] pdf = CreateBornDigitalPdf(includeSemanticMetadata: true);
        var backend = new FakeReviewedPdfiumBackend(SyntheticRenderedPage, width: 16, height: 16);
        var service = new PdfImportService(
            new PdfPigDocumentInspector(),
            new PanelizationEngine(),
            new PdfiumPageRendererAdapter(backend));
        var request = new PdfImportRequest(
            RunId,
            ProjectId,
            new ImmutableByteBuffer(pdf),
            "semantic-born-digital.pdf",
            Password: null,
            new PdfPanelizationOptions());

        PdfImportResult result = await service.ImportAsync(request, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, ImportFailureSummary(result));
        PdfPanelRecord panel = result.Panels.Single();
        Assert.AreEqual("Participant 1", panel.ParticipantLabel);
        Assert.AreEqual(2, panel.SemanticSuggestions.Count);
        Assert.IsTrue(panel.SemanticSuggestions.All(static suggestion =>
            suggestion.ReviewState == PdfSuggestionReviewState.Suggested));
        Assert.AreEqual(
            "instructional support",
            panel.SemanticSuggestions.Single(static suggestion =>
                suggestion.Field == PdfSemanticField.IndependentVariable).Value);
        Assert.AreEqual(
            "task completion",
            panel.SemanticSuggestions.Single(static suggestion =>
                suggestion.Field == PdfSemanticField.DependentVariable).Value);
        Assert.AreEqual(0, backend.CallCount);
    }

    [TestMethod]
    public async Task ScannedPageFallsBackToReviewedPdfiumRendererBeforePanelization()
    {
        PdfDocumentSnapshot document = ScannedDocument();
        var inspector = new FixedInspector(document);
        var panelization = new RenderFallbackPanelizationEngine();
        var backend = new FakeReviewedPdfiumBackend(SyntheticRenderedPage, width: 16, height: 16);
        var service = new PdfImportService(
            inspector,
            panelization,
            new PdfiumPageRendererAdapter(backend));

        PdfImportResult result = await service.ImportAsync(ImportRequest(), CancellationToken.None);

        Assert.IsTrue(result.Succeeded, ImportFailureSummary(result));
        Assert.AreEqual(1, backend.CallCount);
        Assert.AreEqual(2, panelization.CallCount);
        Assert.IsNull(panelization.Inputs[0].RenderedPage);
        Assert.IsNotNull(panelization.Inputs[1].RenderedPage);
        Assert.AreEqual(PdfFigureSourceKind.RenderedPage, result.Figures.Single().SourceKind);
        Assert.AreEqual(PanelId, result.Panels.Single().PanelId);
    }

    [TestMethod]
    public async Task ImportServiceConvertsThrowingCollaboratorsToStructuredFailures()
    {
        PdfImportResult inspectionFailure = await new PdfImportService(
                new ThrowingInspector(),
                new EmptyPanelizationEngine())
            .ImportAsync(ImportRequest(), CancellationToken.None);
        PdfImportResult panelizationFailure = await new PdfImportService(
                new FixedInspector(ScannedDocument()),
                new ThrowingPanelizationEngine())
            .ImportAsync(ImportRequest(), CancellationToken.None);
        PdfImportResult renderFailure = await new PdfImportService(
                new FixedInspector(ScannedDocument()),
                new EmptyPanelizationEngine(),
                new ThrowingRenderer())
            .ImportAsync(ImportRequest(), CancellationToken.None);

        AssertImportFailure(inspectionFailure, "PDF_INSPECTION_UNEXPECTED");
        AssertImportFailure(panelizationFailure, "PDF_PANELIZATION_UNEXPECTED");
        AssertImportFailure(renderFailure, "PDF_RENDER_UNEXPECTED");
    }

    [TestMethod]
    public async Task ImportServicePropagatesCancellationFromEveryCollaborator()
    {
        using var inspectorCancellation = new CancellationTokenSource();
        await AssertOperationCanceledAsync(
            () => new PdfImportService(
                    new CancelingInspector(inspectorCancellation),
                    new EmptyPanelizationEngine())
                .ImportAsync(ImportRequest(), inspectorCancellation.Token));

        using var panelizerCancellation = new CancellationTokenSource();
        await AssertOperationCanceledAsync(
            () => new PdfImportService(
                    new FixedInspector(ScannedDocument()),
                    new CancelingPanelizationEngine(panelizerCancellation))
                .ImportAsync(ImportRequest(), panelizerCancellation.Token));

        using var rendererCancellation = new CancellationTokenSource();
        await AssertOperationCanceledAsync(
            () => new PdfImportService(
                    new FixedInspector(ScannedDocument()),
                    new EmptyPanelizationEngine(),
                    new CancelingRenderer(rendererCancellation))
                .ImportAsync(ImportRequest(), rendererCancellation.Token));
    }

    [TestMethod]
    public void PageCoordinateCropMappingRoundTripsBetweenPdfPointsAndTopLeftPixels()
    {
        var transform = new PdfPageCoordinateTransform(
            PageWidthPoints: 612,
            PageHeightPoints: 792,
            PixelWidth: 1224,
            PixelHeight: 1584);
        var pagePointCrop = new PdfRectD(X: 72, Y: 504, Width: 468, Height: 216);

        PdfRectD pagePixelCrop = transform.PagePointsToPixels(pagePointCrop);
        PdfRectD roundTrip = transform.PagePixelsToPoints(pagePixelCrop);

        Assert.AreEqual(new PdfRectD(X: 144, Y: 144, Width: 936, Height: 432), pagePixelCrop);
        Assert.AreEqual(pagePointCrop, roundTrip);
        Assert.IsTrue(new PdfRectD(0, 0, 1224, 1584).Contains(pagePixelCrop));
    }

    [TestMethod]
    public void PageCoordinateTransformsRoundTripNonzeroOriginsAtEveryRightAngleRotation()
    {
        var visibleBounds = new PdfRectD(10, 20, 200, 100);
        var source = new PdfQuadrilateralD(
            new PdfPointD(50, 40),
            new PdfPointD(130, 40),
            new PdfPointD(130, 70),
            new PdfPointD(50, 70));

        foreach (int rotation in RightAngleRotations)
        {
            int pixelWidth = rotation is 90 or 270 ? 200 : 400;
            int pixelHeight = rotation is 90 or 270 ? 400 : 200;
            var transform = new PdfPageCoordinateTransform(
                visibleBounds,
                rotation,
                pixelWidth,
                pixelHeight);

            PdfQuadrilateralD pixels = transform.PagePointsToPixels(source);
            PdfQuadrilateralD roundTrip = transform.PagePixelsToPoints(pixels);

            AssertQuadrilateralEqual(source, roundTrip, tolerance: 0.000001);
            AssertRectEqual(
                new PdfRectD(0, 0, pixelWidth, pixelHeight),
                transform.PagePointsToPixels(visibleBounds),
                tolerance: 0.000001);
        }
    }

    [TestMethod]
    public async Task MirroredRotatedEmbeddedImageCropQuadrilateralRoundTripsThroughPagePixels()
    {
        var sourceToPage = new PdfAffineTransform(
            A: 0,
            B: 0.5,
            C: 2,
            D: 0,
            E: 100,
            F: 200);
        var image = new PdfEmbeddedImage(
            EmbeddedImageId,
            new PdfRectD(100, 200, 200, 100),
            pixelWidth: 200,
            pixelHeight: 100,
            mediaType: "image/png",
            new ImmutableByteBuffer(CreateBlankPng(width: 200, height: 100)),
            new string('9', 64),
            sourceToPage);
        PdfTextBlock caption = TextBlock(
            "Figure 2. Synthetic rotated image.",
            new PdfRectD(100, 175, 200, 18),
            PdfTextRole.Caption,
            suffix: 9);
        var input = new PdfPanelizationInput(
            new string('8', 64),
            new PdfPageSnapshot(1, 600, 800, [caption], [image], []),
            RenderedPage: null,
            new PdfPanelizationOptions());

        PdfPanelizationResult result = await new PanelizationEngine().ProposeAsync(
            input,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, PanelizationFailureSummary(result));
        PdfFigureCandidate figure = result.Figures.Single();
        PdfPanelRecord panel = result.Panels.Single();
        AssertRectEqual(
            new PdfRectD(0, 0, 200, 100),
            panel.CropInSourcePixelsQuadrilateral.Bounds,
            tolerance: 0.000001);
        PdfQuadrilateralD pagePoints = figure.SourcePixelsToPagePoints.Transform(
            panel.CropInSourcePixelsQuadrilateral);
        PdfQuadrilateralD pagePixels = figure.PagePointsToPagePixels.Transform(pagePoints);
        PdfQuadrilateralD expectedPagePixels = figure.PagePointsToPagePixels.Transform(
            figure.PagePixelsToPagePoints.Transform(panel.BoundsPagePixels));
        AssertQuadrilateralEqual(expectedPagePixels, pagePixels, tolerance: 0.000001);
        Assert.AreNotEqual(
            panel.CropInSourcePixelsQuadrilateral.TopLeft.X,
            panel.CropInSourcePixelsQuadrilateral.BottomLeft.X,
            "Rotation and mirroring must remain visible in the source-crop quadrilateral ordering.");
    }

    [TestMethod]
    public async Task ReviewedPdfiumBackendRendersScannedPageLocallyWithRecordedProvenance()
    {
        var backend = new FakeReviewedPdfiumBackend(SyntheticRenderedPage, width: 16, height: 16);
        var renderer = new PdfiumPageRendererAdapter(backend);

        PdfPageRenderResult result = await renderer.RenderAsync(
            new PdfPageRenderRequest(SyntheticScannedPdf, pageNumber: 1, dpi: 300),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, FailureSummary(result));
        Assert.AreEqual(PdfPageRenderStatus.Succeeded, result.Status);
        Assert.AreEqual(1, backend.CallCount);
        Assert.AreEqual(16, result.Page!.Width);
        Assert.AreEqual(16, result.Page.Height);
        CollectionAssert.AreEqual(SyntheticRenderedPage, result.Page.PngBytes.ToArray());
        Assert.AreEqual("reviewed-pdfium-test-backend", result.Metadata!.RendererId);
        Assert.AreEqual(300, result.Metadata.Dpi);
        Assert.AreEqual(PdfPageRenderCacheDisposition.Miss, result.Metadata.CacheDisposition);
        Assert.IsTrue(backend.Provenance.ReviewApproved);
        Assert.IsTrue(backend.Capabilities.IsLocalOnly);
    }

    [TestMethod]
    public async Task RendererRejectsTruncatedPngBeforeCaching()
    {
        byte[] truncated = SyntheticRenderedPage[..^5];

        await AssertRendererOutputRejectedBeforeCacheAsync(truncated, width: 16, height: 16);
    }

    [TestMethod]
    public async Task RendererRejectsPngIhdrDimensionMismatchBeforeCaching()
    {
        await AssertRendererOutputRejectedBeforeCacheAsync(
            SyntheticRenderedPage,
            width: 17,
            height: 16);
    }

    [TestMethod]
    public async Task RendererRejectsPngCrcCorruptionBeforeCaching()
    {
        byte[] corruptCrc = CorruptFirstIdatByte(SyntheticRenderedPage);

        await AssertRendererOutputRejectedBeforeCacheAsync(corruptCrc, width: 16, height: 16);
    }

    [TestMethod]
    public async Task RendererRejectsOversizedPngBeforeCaching()
    {
        var safetyLimits = new PdfPageRenderSafetyLimits(
            MaximumWidth: 8,
            MaximumHeight: 32,
            MaximumPixelCount: 256,
            MaximumEncodedBytes: 4096,
            MaximumDecodedBytes: 4096,
            MaximumChunkBytes: 4096,
            MaximumChunkCount: 16);

        await AssertRendererOutputRejectedBeforeCacheAsync(
            SyntheticRenderedPage,
            width: 16,
            height: 16,
            safetyLimits);
    }

    [TestMethod]
    public async Task RendererRejectsIncompatibleLicenseAndMissingNoticeProvenanceBeforeBackendCall()
    {
        PdfiumBackendProvenance reviewed = ReviewedBackendProvenance();
        var incompatible = new FakeReviewedPdfiumBackend(
            SyntheticRenderedPage,
            width: 16,
            height: 16,
            provenance: reviewed with { LicenseSpdx = "GPL-3.0-only" });
        var missingNotice = new FakeReviewedPdfiumBackend(
            SyntheticRenderedPage,
            width: 16,
            height: 16,
            provenance: reviewed with { NoticePath = "n/a" });

        await AssertRendererProvenanceRejectedBeforeBackendCallAsync(incompatible);
        await AssertRendererProvenanceRejectedBeforeBackendCallAsync(missingNotice);
    }

    [TestMethod]
    public async Task ConcurrentIdenticalPageRendersCoalesceAndThenHitDeterministicCache()
    {
        var backend = new FakeReviewedPdfiumBackend(
            SyntheticRenderedPage,
            width: 16,
            height: 16,
            waitForRelease: true);
        var renderer = new PdfiumPageRendererAdapter(backend);
        var request = new PdfPageRenderRequest(SyntheticScannedPdf, pageNumber: 2, dpi: 240);

        Task<PdfPageRenderResult>[] pending = Enumerable.Range(0, 8)
            .Select(_ => renderer.RenderAsync(request, CancellationToken.None))
            .ToArray();
        await backend.WaitUntilEnteredAsync();
        backend.Release();

        PdfPageRenderResult[] concurrent = await Task.WhenAll(pending);
        PdfPageRenderResult cached = await renderer.RenderAsync(request, CancellationToken.None);

        Assert.AreEqual(1, backend.CallCount);
        Assert.IsTrue(concurrent.All(static result => result.Succeeded));
        Assert.AreEqual(1, concurrent.Count(static result =>
            result.Metadata!.CacheDisposition == PdfPageRenderCacheDisposition.Miss));
        Assert.AreEqual(7, concurrent.Count(static result =>
            result.Metadata!.CacheDisposition == PdfPageRenderCacheDisposition.Coalesced));
        Assert.AreEqual(PdfPageRenderCacheDisposition.MemoryHit, cached.Metadata!.CacheDisposition);
        Assert.IsTrue(cached.Metadata.CacheHit);
        Assert.AreEqual(1, renderer.CachedEntryCount);
        Assert.AreEqual(SyntheticRenderedPage.Length, renderer.CachedEncodedBytes);
        Assert.IsTrue(concurrent.Append(cached).All(result =>
            string.Equals(
                concurrent[0].Metadata!.CacheKey,
                result.Metadata!.CacheKey,
                StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PageCacheIsMutationResistantAndSeparatesPageDpiAndContractVersion()
    {
        byte[] mutablePdf = (byte[])SyntheticScannedPdf.Clone();
        byte[] mutableRenderedPage = (byte[])SyntheticRenderedPage.Clone();
        var backend = new FakeReviewedPdfiumBackend(mutableRenderedPage, width: 16, height: 16);
        var renderer = new PdfiumPageRendererAdapter(backend);
        var firstRequest = new PdfPageRenderRequest(mutablePdf, pageNumber: 1, dpi: 300, contractVersion: 1);
        mutablePdf[0] = 0;

        PdfPageRenderResult first = await renderer.RenderAsync(firstRequest, CancellationToken.None);
        mutableRenderedPage[0] = 0;
        byte[] callerCopy = first.Page!.PngBytes.ToArray();
        callerCopy[1] = 0;
        PdfPageRenderResult hit = await renderer.RenderAsync(firstRequest, CancellationToken.None);
        PdfPageRenderResult otherPage = await renderer.RenderAsync(
            new PdfPageRenderRequest(SyntheticScannedPdf, pageNumber: 2, dpi: 300, contractVersion: 1),
            CancellationToken.None);
        PdfPageRenderResult otherDpi = await renderer.RenderAsync(
            new PdfPageRenderRequest(SyntheticScannedPdf, pageNumber: 1, dpi: 240, contractVersion: 1),
            CancellationToken.None);
        PdfPageRenderResult otherContract = await renderer.RenderAsync(
            new PdfPageRenderRequest(SyntheticScannedPdf, pageNumber: 1, dpi: 300, contractVersion: 2),
            CancellationToken.None);

        Assert.AreEqual(4, backend.CallCount);
        Assert.AreEqual(PdfPageRenderCacheDisposition.MemoryHit, hit.Metadata!.CacheDisposition);
        CollectionAssert.AreEqual(SyntheticRenderedPage, hit.Page!.PngBytes.ToArray());
        Assert.AreEqual(first.Metadata!.CacheKey, hit.Metadata.CacheKey);
        Assert.AreNotEqual(first.Metadata.CacheKey, otherPage.Metadata!.CacheKey);
        Assert.AreNotEqual(first.Metadata.CacheKey, otherDpi.Metadata!.CacheKey);
        Assert.AreNotEqual(first.Metadata.CacheKey, otherContract.Metadata!.CacheKey);
    }

    [TestMethod]
    public async Task PageCacheEvictsLeastRecentlyUsedEntryWithinFixedBounds()
    {
        var backend = new FakeReviewedPdfiumBackend(SyntheticRenderedPage, width: 16, height: 16);
        var renderer = new PdfiumPageRendererAdapter(
            backend,
            new PdfPageRenderCacheOptions(MaximumEntries: 2, MaximumEncodedBytes: 1024));

        PdfPageRenderRequest first = new(SyntheticScannedPdf, pageNumber: 1, dpi: 200);
        PdfPageRenderRequest second = new(SyntheticScannedPdf, pageNumber: 2, dpi: 200);
        PdfPageRenderRequest third = new(SyntheticScannedPdf, pageNumber: 3, dpi: 200);
        _ = await renderer.RenderAsync(first, CancellationToken.None);
        _ = await renderer.RenderAsync(second, CancellationToken.None);
        _ = await renderer.RenderAsync(first, CancellationToken.None);
        _ = await renderer.RenderAsync(third, CancellationToken.None);
        PdfPageRenderResult rerenderedSecond = await renderer.RenderAsync(second, CancellationToken.None);

        Assert.AreEqual(4, backend.CallCount);
        Assert.AreEqual(PdfPageRenderCacheDisposition.Miss, rerenderedSecond.Metadata!.CacheDisposition);
        Assert.AreEqual(2, renderer.CachedEntryCount);
        Assert.IsTrue(renderer.CachedEncodedBytes <= 1024);
    }

    [TestMethod]
    public async Task PageRendererHonorsCancellationWithoutLeakingACompletedCacheEntry()
    {
        var backend = new FakeReviewedPdfiumBackend(
            SyntheticRenderedPage,
            width: 16,
            height: 16,
            waitForRelease: true);
        var renderer = new PdfiumPageRendererAdapter(backend);
        using var cancellation = new CancellationTokenSource();

        Task<PdfPageRenderResult> pending = renderer.RenderAsync(
            new PdfPageRenderRequest(SyntheticScannedPdf, pageNumber: 1, dpi: 300),
            cancellation.Token);
        await backend.WaitUntilEnteredAsync();
        cancellation.Cancel();

        PdfPageRenderResult result = await pending;

        Assert.AreEqual(PdfPageRenderStatus.Cancelled, result.Status);
        Assert.AreEqual(PdfPageRenderFailureCodes.Cancelled, result.Failure!.Code);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, renderer.CachedEntryCount);
    }

    private static string FailureSummary(PdfPageRenderResult result) =>
        result.Failure is null
            ? $"Unexpected status {result.Status}."
            : $"{result.Failure.Code}: {result.Failure.TechnicalMessage}";

    private static string ImportFailureSummary(PdfImportResult result) =>
        string.Join(" | ", result.Failures.Select(static failure =>
            $"{failure.Code}: {failure.TechnicalMessage}"));

    private static string InspectionFailureSummary(PdfInspectionResult result) =>
        string.Join(" | ", result.Failures.Select(static failure =>
            $"{failure.Code}: {failure.TechnicalMessage}"));

    private static string PanelizationFailureSummary(PdfPanelizationResult result) =>
        string.Join(" | ", result.Failures.Select(static failure =>
            $"{failure.Code}: {failure.TechnicalMessage}"));

    private static async Task AssertRendererOutputRejectedBeforeCacheAsync(
        byte[] pngBytes,
        int width,
        int height,
        PdfPageRenderSafetyLimits? safetyLimits = null)
    {
        var backend = new FakeReviewedPdfiumBackend(pngBytes, width, height);
        var renderer = new PdfiumPageRendererAdapter(
            backend,
            PdfiumCompatibleLicensePolicy.Default,
            cacheOptions: null,
            safetyLimits);

        PdfPageRenderResult result = await renderer.RenderAsync(
            new PdfPageRenderRequest(SyntheticScannedPdf, pageNumber: 1, dpi: 300),
            CancellationToken.None);

        Assert.AreEqual(PdfPageRenderStatus.Failed, result.Status);
        Assert.AreEqual(PdfPageRenderFailureCodes.BackendOutputInvalid, result.Failure!.Code);
        Assert.AreEqual(1, backend.CallCount);
        Assert.AreEqual(0, renderer.CachedEntryCount);
        Assert.AreEqual(0, renderer.CachedEncodedBytes);
    }

    private static async Task AssertRendererProvenanceRejectedBeforeBackendCallAsync(
        FakeReviewedPdfiumBackend backend)
    {
        var renderer = new PdfiumPageRendererAdapter(backend);

        PdfPageRenderResult result = await renderer.RenderAsync(
            new PdfPageRenderRequest(SyntheticScannedPdf, pageNumber: 1, dpi: 300),
            CancellationToken.None);

        Assert.AreEqual(PdfPageRenderStatus.Failed, result.Status);
        Assert.AreEqual(PdfPageRenderFailureCodes.BackendProvenanceRejected, result.Failure!.Code);
        Assert.AreEqual(0, backend.CallCount);
        Assert.AreEqual(0, renderer.CachedEntryCount);
    }

    private static void AssertImportFailure(PdfImportResult result, string expectedCode)
    {
        Assert.IsFalse(result.Succeeded);
        PdfFailure failure = result.Failures.Single(item => item.Code == expectedCode);
        Assert.AreEqual(PdfFailureSeverity.Error, failure.Severity);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.UserMessageKey));
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.TechnicalMessage));
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.SuggestedAction));
    }

    private static async Task AssertOperationCanceledAsync(Func<Task<PdfImportResult>> operation)
    {
        try
        {
            _ = await operation();
            Assert.Fail("Collaborator cancellation should propagate from the import service.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static PdfiumBackendProvenance ReviewedBackendProvenance() => new(
        RendererId: "reviewed-pdfium-test-backend",
        RendererVersion: "1.0.0",
        BinarySha256: new string('a', 64),
        Source: "synthetic in-memory test double",
        SourceRevision: "fixed-test-revision",
        LicenseSpdx: "Apache-2.0",
        NoticePath: "THIRD_PARTY_NOTICES.md",
        ReviewApproved: true,
        RedistributionApproved: true,
        IsBundled: false);

    private static byte[] CorruptFirstIdatByte(byte[] pngBytes)
    {
        byte[] corrupt = (byte[])pngBytes.Clone();
        var offset = 8;
        while (offset + 12 <= corrupt.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(corrupt.AsSpan(offset, sizeof(int)));
            if (corrupt.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8) && length > 0)
            {
                corrupt[offset + 8] ^= 0x01;
                return corrupt;
            }

            offset += 12 + length;
        }

        Assert.Fail("The valid synthetic PNG must contain a non-empty IDAT chunk.");
        return corrupt;
    }

    private static PdfPanelizationInput OverlappingEmbeddedFiguresInput(
        PdfRectD firstBounds,
        PdfRectD secondBounds)
    {
        var first = new PdfEmbeddedImage(
            Guid.Parse("50000000-0000-0000-0000-000000000011"),
            firstBounds,
            pixelWidth: 16,
            pixelHeight: 16,
            mediaType: "image/png",
            new ImmutableByteBuffer(SyntheticRenderedPage),
            new string('1', 64));
        var second = new PdfEmbeddedImage(
            Guid.Parse("50000000-0000-0000-0000-000000000012"),
            secondBounds,
            pixelWidth: 16,
            pixelHeight: 16,
            mediaType: "image/png",
            new ImmutableByteBuffer(SyntheticRenderedPage),
            new string('2', 64));
        PdfTextBlock caption = TextBlock(
            "Figure 7. Synthetic overlap threshold fixture.",
            new PdfRectD(50, 190, 475, 18),
            PdfTextRole.Caption,
            suffix: 11);

        return new PdfPanelizationInput(
            new string('7', 64),
            new PdfPageSnapshot(1, 600, 800, [caption], [first, second], []),
            RenderedPage: null,
            new PdfPanelizationOptions());
    }

    private static PdfPanelRecord ReviewPanel(
        Guid panelId,
        int order,
        PdfRectD boundsPagePixels,
        IEnumerable<PdfSemanticSuggestion> suggestions)
    {
        var crop = new PdfRectD(
            boundsPagePixels.X - 144,
            boundsPagePixels.Y - 288,
            boundsPagePixels.Width,
            boundsPagePixels.Height);
        var boundsPoints = new PdfRectD(
            72 + ((boundsPagePixels.X - 144) / 2d),
            (1584 - boundsPagePixels.Bottom) / 2d,
            boundsPagePixels.Width / 2d,
            boundsPagePixels.Height / 2d);
        return new PdfPanelRecord(
            panelId,
            FigureId,
            pageNumber: 1,
            order,
            crop,
            boundsPagePixels,
            boundsPoints,
            participantLabel: "Participant A",
            caption: "Figure 1. Review-state merge fixture.",
            suggestions,
            [new PdfPanelEvidence(PdfPanelEvidenceKind.Manual, 1d, "review fixture")],
            confidence: 1d);
    }

    private static PdfPanelizationInput ThreeStackedPanelInput()
    {
        const double pageWidth = 600;
        const double pageHeight = 900;
        var textBlocks = new[]
        {
            TextBlock(
                "Figure 1. Dependent variable: task completion; independent variable: instructional support.",
                new PdfRectD(52, 825, 500, 24),
                PdfTextRole.Caption,
                1),
            TextBlock("Participant Alpha", new PdfRectD(505, 720, 80, 18), PdfTextRole.ParticipantLabel, 2),
            TextBlock("Participant Beta", new PdfRectD(505, 490, 80, 18), PdfTextRole.ParticipantLabel, 3),
            TextBlock("Participant Gamma", new PdfRectD(505, 260, 80, 18), PdfTextRole.ParticipantLabel, 4),
        };
        var lines = new List<PdfVectorLine>();
        AddPlot(lines, baselineY: 630, topY: 800);
        AddPlot(lines, baselineY: 400, topY: 570);
        AddPlot(lines, baselineY: 170, topY: 340);
        var page = new PdfPageSnapshot(
            pageNumber: 1,
            pageWidth,
            pageHeight,
            textBlocks,
            embeddedImages: [],
            lines);
        return new PdfPanelizationInput(
            new string('1', 64),
            page,
            RenderedPage: null,
            new PdfPanelizationOptions(
                RenderDpi: 144,
                MinimumFigureConfidence: 0.55,
                MinimumWhitespaceFraction: 0.035,
                MaximumPanelsPerFigure: 12));
    }

    private static PdfTextBlock TextBlock(
        string text,
        PdfRectD bounds,
        PdfTextRole role,
        int suffix) =>
        new(
            Guid.Parse($"70000000-0000-0000-0000-{suffix:D12}"),
            text,
            bounds,
            role,
            0.95);

    private static void AddPlot(List<PdfVectorLine> lines, double baselineY, double topY)
    {
        lines.Add(new PdfVectorLine(new PdfPointD(100, baselineY), new PdfPointD(500, baselineY), 1.5));
        lines.Add(new PdfVectorLine(new PdfPointD(100, baselineY), new PdfPointD(100, topY), 1.5));
        lines.Add(new PdfVectorLine(new PdfPointD(140, baselineY + 30), new PdfPointD(220, baselineY + 70), 0.75));
        lines.Add(new PdfVectorLine(new PdfPointD(220, baselineY + 70), new PdfPointD(300, baselineY + 45), 0.75));
        lines.Add(new PdfVectorLine(new PdfPointD(300, baselineY + 45), new PdfPointD(380, baselineY + 100), 0.75));
        lines.Add(new PdfVectorLine(new PdfPointD(380, baselineY + 100), new PdfPointD(460, baselineY + 80), 0.75));
    }

    private static string[] PanelFingerprints(PdfPanelizationResult result) =>
        result.Figures.Select(static figure =>
                FormattableString.Invariant(
                    $"F|{figure.FigureId}|{figure.BoundsPagePixels}|{figure.BoundsPagePoints}|{figure.SourceKind}"))
            .Concat(result.Panels.Select(static panel =>
                FormattableString.Invariant(
                    $"P|{panel.PanelId}|{panel.Order}|{panel.CropInSourcePixels}|{panel.BoundsPagePixels}|{panel.BoundsPagePoints}")))
            .ToArray();

    private static void AssertRectEqual(PdfRectD expected, PdfRectD actual, double tolerance)
    {
        Assert.AreEqual(expected.X, actual.X, tolerance);
        Assert.AreEqual(expected.Y, actual.Y, tolerance);
        Assert.AreEqual(expected.Width, actual.Width, tolerance);
        Assert.AreEqual(expected.Height, actual.Height, tolerance);
    }

    private static void AssertQuadrilateralEqual(
        PdfQuadrilateralD expected,
        PdfQuadrilateralD actual,
        double tolerance)
    {
        AssertPointEqual(expected.TopLeft, actual.TopLeft, tolerance);
        AssertPointEqual(expected.TopRight, actual.TopRight, tolerance);
        AssertPointEqual(expected.BottomRight, actual.BottomRight, tolerance);
        AssertPointEqual(expected.BottomLeft, actual.BottomLeft, tolerance);
    }

    private static void AssertPointEqual(PdfPointD expected, PdfPointD actual, double tolerance)
    {
        Assert.AreEqual(expected.X, actual.X, tolerance);
        Assert.AreEqual(expected.Y, actual.Y, tolerance);
    }

    private static void AssertPageGeometry(
        PdfPageSnapshot page,
        PdfRectD expectedVisibleBounds,
        int expectedRotation,
        double expectedWidth,
        double expectedHeight)
    {
        Assert.AreEqual(expectedVisibleBounds, page.OriginalVisibleBoundsPoints);
        Assert.AreEqual(expectedRotation, page.RotationDegrees);
        Assert.AreEqual(expectedWidth, page.WidthPoints, 0.000001);
        Assert.AreEqual(expectedHeight, page.HeightPoints, 0.000001);
        var normalized = new PdfQuadrilateralD(
            new PdfPointD(5, 7),
            new PdfPointD(expectedWidth - 8, 7),
            new PdfPointD(expectedWidth - 8, expectedHeight - 9),
            new PdfPointD(5, expectedHeight - 9));
        PdfQuadrilateralD original = page.NormalizedToOriginalPagePoints.Transform(normalized);
        PdfQuadrilateralD roundTrip = page.OriginalToNormalizedPagePoints.Transform(original);
        AssertQuadrilateralEqual(normalized, roundTrip, tolerance: 0.000001);
    }

    private static bool Overlaps(PdfRectD left, PdfRectD right) =>
        Math.Min(left.Right, right.Right) - Math.Max(left.X, right.X) > 0d &&
        Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y) > 0d;

    private static void AssertStructuredInspectionError(PdfInspectionResult result, string expectedCode)
    {
        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Document);
        PdfFailure failure = result.Failures.Single(item => item.Severity == PdfFailureSeverity.Error);
        Assert.AreEqual(expectedCode, failure.Code);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.UserMessageKey));
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.TechnicalMessage));
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.SuggestedAction));
    }

    private static byte[] CreateRotatedCropBoxPdf()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/CropBox [10 20 210 120] /Rotate 90 /Resources << >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/CropBox [30 40 330 190] /Rotate 270 /Resources << >> >>",
        ];
        using var pdf = new MemoryStream();
        AppendAscii(pdf, "%PDF-1.4\n% deterministic public synthetic fixture\n");
        var offsets = new long[objects.Length + 1];
        for (var index = 0; index < objects.Length; index++)
        {
            offsets[index + 1] = pdf.Position;
            AppendAscii(
                pdf,
                FormattableString.Invariant($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"));
        }

        long crossReferenceOffset = pdf.Position;
        AppendAscii(pdf, FormattableString.Invariant($"xref\n0 {objects.Length + 1}\n"));
        AppendAscii(pdf, "0000000000 65535 f \n");
        for (var index = 1; index < offsets.Length; index++)
        {
            AppendAscii(pdf, FormattableString.Invariant($"{offsets[index]:D10} 00000 n \n"));
        }

        AppendAscii(
            pdf,
            FormattableString.Invariant(
                $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{crossReferenceOffset}\n%%EOF\n"));
        return pdf.ToArray();
    }

    private static void AppendAscii(Stream target, string value) =>
        target.Write(Encoding.ASCII.GetBytes(value));

    private static byte[] CreateBornDigitalPdf(bool includeSemanticMetadata = false)
    {
        using var builder = new PdfDocumentBuilder
        {
            DocumentInformation = new PdfDocumentBuilder.DocumentInformationBuilder
            {
                Title = "Fixed Synthetic Article",
                Author = "Test Author",
                Subject = "Synthetic local acceptance fixture",
                Keywords = "single-case design",
                Creator = "GraphReader.Pdf.Tests",
                Producer = "PdfPig",
            },
        };
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(width: 612, height: 792);
        page.AddText(
            includeSemanticMetadata
                ? "Figure 1. Dependent variable: task completion; independent variable: instructional support."
                : "Figure 1. Instructional intervention and task completion.",
            fontSize: 11,
            new PdfPoint(72, 264),
            font);
        page.AddPng(CreateGraphPng(width: 640, height: 480), new PdfRectangle(72, 288, 540, 648));
        if (includeSemanticMetadata)
        {
            page.AddText("Participant 1", fontSize: 10, new PdfPoint(420, 620), font);
        }
        page.DrawLine(new PdfPoint(96, 312), new PdfPoint(96, 624), lineWidth: 1.5);
        page.DrawLine(new PdfPoint(96, 312), new PdfPoint(516, 312), lineWidth: 1.5);
        page.DrawLine(new PdfPoint(96, 420), new PdfPoint(516, 420), lineWidth: 0.75);
        page.DrawLine(new PdfPoint(96, 528), new PdfPoint(516, 528), lineWidth: 0.75);
        return builder.Build();
    }

    private static byte[] CreateGraphPng(int width, int height)
    {
        byte[] scanlines = CreateWhiteScanlines(width, height);

        for (var y = 48; y < height - 48; y++)
        {
            scanlines[(y * (width + 1)) + 64] = 0;
        }

        for (var x = 64; x < width - 32; x++)
        {
            scanlines[((height - 48) * (width + 1)) + x] = 0;
        }

        for (var x = 96; x < width - 64; x += 48)
        {
            int y = height - 96 - ((x / 48) % 5 * 40);
            for (var offsetY = -3; offsetY <= 3; offsetY++)
            {
                for (var offsetX = -3; offsetX <= 3; offsetX++)
                {
                    scanlines[((y + offsetY) * (width + 1)) + x + offsetX] = 0;
                }
            }
        }

        return EncodeGrayscalePng(width, height, scanlines);
    }

    private static byte[] CreateStackedGraphPng(int width, int height)
    {
        byte[] scanlines = CreateWhiteScanlines(width, height);
        int[] baselines = [270, 530, 790];
        foreach (int baseline in baselines)
        {
            DrawHorizontal(scanlines, width, height, xMin: 80, xMax: 580, y: baseline, thickness: 2);
            DrawVertical(scanlines, width, height, x: 80, yMin: baseline - 180, yMax: baseline, thickness: 2);
            DrawHorizontal(scanlines, width, height, xMin: 120, xMax: 220, y: baseline - 60, thickness: 1);
            DrawHorizontal(scanlines, width, height, xMin: 220, xMax: 340, y: baseline - 110, thickness: 1);
            DrawHorizontal(scanlines, width, height, xMin: 340, xMax: 460, y: baseline - 80, thickness: 1);
            DrawVertical(scanlines, width, height, x: 300, yMin: baseline - 150, yMax: baseline, thickness: 1);
        }

        return EncodeGrayscalePng(width, height, scanlines);
    }

    private static byte[] CreateGraphWithShortLegendFramePng(int width, int height)
    {
        byte[] scanlines = CreateWhiteScanlines(width, height);
        DrawHorizontal(scanlines, width, height, xMin: 105, xMax: 960, y: 260, thickness: 2);
        DrawVertical(scanlines, width, height, x: 105, yMin: 96, yMax: 260, thickness: 2);
        DrawHorizontal(scanlines, width, height, xMin: 180, xMax: 370, y: 220, thickness: 1);
        DrawHorizontal(scanlines, width, height, xMin: 420, xMax: 650, y: 180, thickness: 1);

        // At 144 DPI this 34-pixel frame edge is only 17 points tall. It must
        // not become a second graph proposal that overlaps the real plot crop.
        DrawHorizontal(scanlines, width, height, xMin: 978, xMax: 1128, y: 104, thickness: 1);
        DrawHorizontal(scanlines, width, height, xMin: 978, xMax: 1128, y: 138, thickness: 1);
        DrawVertical(scanlines, width, height, x: 978, yMin: 104, yMax: 138, thickness: 1);
        DrawVertical(scanlines, width, height, x: 1128, yMin: 104, yMax: 138, thickness: 1);
        return EncodeGrayscalePng(width, height, scanlines);
    }

    private static byte[] CreateBlankPng(int width, int height) =>
        EncodeGrayscalePng(width, height, CreateWhiteScanlines(width, height));

    private static byte[] CreateWhiteScanlines(int width, int height)
    {
        byte[] scanlines = new byte[height * (width + 1)];
        Array.Fill(scanlines, byte.MaxValue);
        for (var y = 0; y < height; y++)
        {
            scanlines[y * (width + 1)] = 0;
        }

        return scanlines;
    }

    private static void DrawHorizontal(
        byte[] scanlines,
        int width,
        int height,
        int xMin,
        int xMax,
        int y,
        int thickness)
    {
        for (var offset = 0; offset < thickness; offset++)
        {
            int row = Math.Clamp(y + offset, 0, height - 1);
            for (int x = Math.Max(0, xMin); x <= Math.Min(width - 1, xMax); x++)
            {
                scanlines[(row * (width + 1)) + x + 1] = 0;
            }
        }
    }

    private static void DrawVertical(
        byte[] scanlines,
        int width,
        int height,
        int x,
        int yMin,
        int yMax,
        int thickness)
    {
        for (var offset = 0; offset < thickness; offset++)
        {
            int column = Math.Clamp(x + offset, 0, width - 1);
            for (int y = Math.Max(0, yMin); y <= Math.Min(height - 1, yMax); y++)
            {
                scanlines[(y * (width + 1)) + column + 1] = 0;
            }
        }
    }

    private static byte[] EncodeGrayscalePng(int width, int height, byte[] scanlines)
    {
        byte[] compressed;
        using (var compressedStream = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(scanlines);
            }

            compressed = compressedStream.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 0;
        WritePngChunk(png, "IHDR", header);
        WritePngChunk(png, "IDAT", compressed);
        WritePngChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WritePngChunk(Stream target, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        target.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        target.Write(typeBytes);
        target.Write(data);

        byte[] checksumInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(checksumInput, 0);
        data.CopyTo(checksumInput.AsSpan(typeBytes.Length));
        Span<byte> checksum = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ComputePngCrc32(checksumInput));
        target.Write(checksum);
    }

    private static uint ComputePngCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                uint mask = unchecked((uint)-(int)(crc & 1));
                crc = (crc >> 1) ^ (0xedb88320u & mask);
            }
        }

        return ~crc;
    }

    private static PdfImportRequest ImportRequest() => new(
        RunId,
        ProjectId,
        new ImmutableByteBuffer(SyntheticScannedPdf),
        "fixed-synthetic.pdf",
        Password: null,
        new PdfPanelizationOptions());

    private static PdfDocumentSnapshot BornDigitalDocument()
    {
        var embedded = new PdfEmbeddedImage(
            EmbeddedImageId,
            new PdfRectD(72, 288, 468, 360),
            pixelWidth: 936,
            pixelHeight: 720,
            mediaType: "image/png",
            new ImmutableByteBuffer(SyntheticRenderedPage),
            new string('b', 64));
        var caption = new PdfTextBlock(
            Guid.Parse("60000000-0000-0000-0000-000000000004"),
            "Figure 1. Instructional intervention and task completion.",
            new PdfRectD(72, 252, 468, 24),
            PdfTextRole.Caption,
            0.98);
        return new PdfDocumentSnapshot(
            new string('c', 64),
            new PdfDocumentMetadata(
                "Fixed Synthetic Article",
                "Test Author",
                "Synthetic local acceptance fixture",
                "single-case design",
                null,
                null),
            [new PdfPageSnapshot(1, 612, 792, [caption], [embedded], [])]);
    }

    private static PdfDocumentSnapshot ScannedDocument() => new(
        new string('d', 64),
        new PdfDocumentMetadata("Fixed Scanned Article", null, null, null, null, null),
        [new PdfPageSnapshot(1, 612, 792, [], [], [])]);

    private static PdfFigureCandidate Figure(PdfFigureSourceKind sourceKind) => new(
        FigureId,
        pageNumber: 1,
        sourceKind,
        sourceKind == PdfFigureSourceKind.EmbeddedImage ? EmbeddedImageId : null,
        new PdfRectD(144, 288, 936, 720),
        new PdfRectD(72, 288, 468, 360),
        sourcePixelWidth: 936,
        sourcePixelHeight: 720,
        new ImmutableByteBuffer(SyntheticRenderedPage),
        "image/png",
        "Figure 1. Instructional intervention and task completion.",
        [
            new PdfPanelEvidence(PdfPanelEvidenceKind.HorizontalAxis, 0.25, "fixed horizontal axes"),
            new PdfPanelEvidence(PdfPanelEvidenceKind.VerticalAxis, 0.25, "fixed vertical axes"),
            new PdfPanelEvidence(PdfPanelEvidenceKind.CaptionProximity, 0.25, "fixed caption"),
        ],
        confidence: 0.93);

    private static PdfPanelRecord Panel(PdfFigureSourceKind sourceKind) => new(
        PanelId,
        FigureId,
        pageNumber: 1,
        order: 0,
        new PdfRectD(0, 0, 936, 720),
        new PdfRectD(144, 288, 936, 720),
        new PdfRectD(72, 288, 468, 360),
        participantLabel: "Participant A",
        caption: "Figure 1. Instructional intervention and task completion.",
        [
            new PdfSemanticSuggestion(
                PdfSemanticField.DependentVariable,
                "Task completion",
                "task completion",
                0.88),
            new PdfSemanticSuggestion(
                PdfSemanticField.IndependentVariable,
                "Instructional intervention",
                "instructional intervention",
                0.82),
        ],
        [new PdfPanelEvidence(
            sourceKind == PdfFigureSourceKind.EmbeddedImage
                ? PdfPanelEvidenceKind.EmbeddedImage
                : PdfPanelEvidenceKind.DenseLineStructure,
            0.75,
            "fixed fixture evidence")],
        confidence: 0.92);

    private sealed class FixedInspector(PdfDocumentSnapshot document) : IPdfDocumentInspector
    {
        public Task<PdfInspectionResult> InspectAsync(
            PdfInspectionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PdfInspectionResult(
                document,
                [],
                new PdfInspectionTiming(0, 0, 0)));
        }
    }

    private sealed class ThrowingInspector : IPdfDocumentInspector
    {
        public Task<PdfInspectionResult> InspectAsync(
            PdfInspectionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<PdfInspectionResult>(
                new InvalidOperationException("synthetic inspector fault"));
    }

    private sealed class CancelingInspector(CancellationTokenSource cancellation) : IPdfDocumentInspector
    {
        public Task<PdfInspectionResult> InspectAsync(
            PdfInspectionRequest request,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class EmptyPanelizationEngine : IPdfPanelizationEngine
    {
        public Task<PdfPanelizationResult> ProposeAsync(
            PdfPanelizationInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PdfPanelizationResult([], []));
        }

        public PdfPanelizationResult ApplySplit(
            PdfPanelizationResult current,
            PdfManualSplitCommand command) =>
            throw new NotSupportedException();

        public PdfPanelizationResult ApplyMerge(
            PdfPanelizationResult current,
            PdfManualMergeCommand command) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingPanelizationEngine : IPdfPanelizationEngine
    {
        public Task<PdfPanelizationResult> ProposeAsync(
            PdfPanelizationInput input,
            CancellationToken cancellationToken) =>
            Task.FromException<PdfPanelizationResult>(
                new InvalidOperationException("synthetic panelizer fault"));

        public PdfPanelizationResult ApplySplit(
            PdfPanelizationResult current,
            PdfManualSplitCommand command) =>
            throw new NotSupportedException();

        public PdfPanelizationResult ApplyMerge(
            PdfPanelizationResult current,
            PdfManualMergeCommand command) =>
            throw new NotSupportedException();
    }

    private sealed class CancelingPanelizationEngine(CancellationTokenSource cancellation)
        : IPdfPanelizationEngine
    {
        public Task<PdfPanelizationResult> ProposeAsync(
            PdfPanelizationInput input,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }

        public PdfPanelizationResult ApplySplit(
            PdfPanelizationResult current,
            PdfManualSplitCommand command) =>
            throw new NotSupportedException();

        public PdfPanelizationResult ApplyMerge(
            PdfPanelizationResult current,
            PdfManualMergeCommand command) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingRenderer : IPdfPageRenderingService
    {
        public Task<PdfPageRenderResult> RenderAsync(
            PdfPageRenderRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<PdfPageRenderResult>(
                new InvalidOperationException("synthetic renderer fault"));
    }

    private sealed class CancelingRenderer(CancellationTokenSource cancellation)
        : IPdfPageRenderingService
    {
        public Task<PdfPageRenderResult> RenderAsync(
            PdfPageRenderRequest request,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class EmbeddedPanelizationEngine : IPdfPanelizationEngine
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<PdfPanelizationResult> ProposeAsync(
            PdfPanelizationInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            Assert.IsNull(input.RenderedPage);
            return Task.FromResult(new PdfPanelizationResult(
                [Figure(PdfFigureSourceKind.EmbeddedImage)],
                [Panel(PdfFigureSourceKind.EmbeddedImage)]));
        }

        public PdfPanelizationResult ApplySplit(
            PdfPanelizationResult current,
            PdfManualSplitCommand command) =>
            throw new NotSupportedException();

        public PdfPanelizationResult ApplyMerge(
            PdfPanelizationResult current,
            PdfManualMergeCommand command) =>
            throw new NotSupportedException();
    }

    private sealed class RenderFallbackPanelizationEngine : IPdfPanelizationEngine
    {
        private readonly List<PdfPanelizationInput> _inputs = [];

        public int CallCount => _inputs.Count;

        public List<PdfPanelizationInput> Inputs => _inputs;

        public Task<PdfPanelizationResult> ProposeAsync(
            PdfPanelizationInput input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _inputs.Add(input);
            return Task.FromResult(input.RenderedPage is null
                ? new PdfPanelizationResult([], [])
                : new PdfPanelizationResult(
                    [Figure(PdfFigureSourceKind.RenderedPage)],
                    [Panel(PdfFigureSourceKind.RenderedPage)]));
        }

        public PdfPanelizationResult ApplySplit(
            PdfPanelizationResult current,
            PdfManualSplitCommand command) =>
            throw new NotSupportedException();

        public PdfPanelizationResult ApplyMerge(
            PdfPanelizationResult current,
            PdfManualMergeCommand command) =>
            throw new NotSupportedException();
    }

    private sealed class FakeReviewedPdfiumBackend : IPdfiumPageRendererBackend
    {
        private static readonly PdfiumBackendCapabilities LocalCapabilities = new(
            SupportsPageRendering: true,
            SupportsCancellation: true,
            SupportsPngEncoding: true,
            IsLocalOnly: true,
            MinimumDpi: 72,
            MaximumDpi: 600);

        private readonly byte[] _renderedPage;
        private readonly int _width;
        private readonly int _height;
        private readonly bool _waitForRelease;
        private readonly PdfiumBackendProvenance _provenance;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public FakeReviewedPdfiumBackend(
            byte[] renderedPage,
            int width,
            int height,
            bool waitForRelease = false,
            PdfiumBackendProvenance? provenance = null)
        {
            _renderedPage = (byte[])renderedPage.Clone();
            _width = width;
            _height = height;
            _waitForRelease = waitForRelease;
            _provenance = provenance ?? ReviewedBackendProvenance();
        }

        public PdfiumBackendProvenance Provenance => _provenance;

        public PdfiumBackendCapabilities Capabilities => LocalCapabilities;

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<PdfiumBackendRenderResult> RenderPageAsync(
            PdfiumBackendRenderRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            _entered.TrySetResult();
            if (_waitForRelease)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            return PdfiumBackendRenderResult.Success((byte[])_renderedPage.Clone(), _width, _height);
        }

        public Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _release.TrySetResult();
    }
}
