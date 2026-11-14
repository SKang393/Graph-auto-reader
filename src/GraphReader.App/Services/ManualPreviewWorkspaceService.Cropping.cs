// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.Workflow;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using GraphReader.Imaging;
using GraphReader.Pdf;

namespace GraphReader.App.Services;

public partial class ManualPreviewWorkspaceService
{
    private const string ManualCropKey = "manual_panel_crop";
    private const string ManualCropSchema = "graphreader.manual-panel-crop.v1";
    private sealed record ManualCropBinding(string Schema, Guid ParentPanelId, string ParentImageSha256,
        string ChildImageSha256, MarkerExemplarRectangle Rectangle);

    public bool CanCreatePanelCrops => RuntimeEnvironment == WorkflowRuntimeEnvironment.ManualPreview;

    public bool HasDependentPanelCrops(string tabId)
    {
        WorkspaceTabViewModel? parent = _tabs.SingleOrDefault(tab => tab.TabId == tabId);
        if (parent?.PanelId is null || !Guid.TryParse(parent.PanelId, out Guid parentId)) return false;
        var activeIds = _tabs.Where(tab => tab.PanelId is not null).Select(tab => tab.PanelId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return CurrentProject.Panels.Any(panel => activeIds.Contains(panel.PanelId.Value.ToString("D")) &&
            panel.Validation is { ValueKind: JsonValueKind.Object } validation &&
            validation.TryGetProperty(ManualCropKey, out JsonElement binding) && binding.ValueKind == JsonValueKind.Object &&
            binding.TryGetProperty(nameof(ManualCropBinding.ParentPanelId), out JsonElement id) &&
            id.TryGetGuid(out Guid savedParent) && savedParent == parentId);
    }

    public PanelCropContext? GetPanelCropContext(string tabId)
    {
        WorkspaceTabViewModel? tab = _tabs.SingleOrDefault(item => item.TabId == tabId);
        if (!CanCreatePanelCrops || tab?.PanelId is null ||
            !_originalPanelBytesByTab.TryGetValue(tabId, out ImmutableImageBytes? image) ||
            tab.ImageSource is not BitmapSource { IsFrozen: true } bitmap ||
            bitmap.PixelWidth != tab.PixelWidth || bitmap.PixelHeight != tab.PixelHeight) return null;
        PanelRecord? panel = CurrentProject.Panels.SingleOrDefault(item => item.PanelId.Value.ToString("D") == tab.PanelId);
        SourceReference? source = CurrentProject.Sources.SingleOrDefault(item => item.SourceId == panel?.SourceId);
        if (panel is null || source is null) return null;
        string payload = JsonSerializer.Serialize(new { CurrentProject.ProjectId, panel.PanelId, panel.SourceId,
            source.Sha256, source.LocalPath, panel.Crop, panel.Transforms, panel.PageNumber,
            tab.SourceSha256, tab.PixelWidth, tab.PixelHeight });
        return new(tabId, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            tab.PixelWidth, tab.PixelHeight) { OriginalImageIdentity = image };
    }

    public bool IsPanelCropContextCurrent(PanelCropContext context) => context is not null &&
        GetPanelCropContext(context.TabId) is { } current && current.Binding == context.Binding &&
        current.Width == context.Width && current.Height == context.Height &&
        ReferenceEquals(current.OriginalImageIdentity, context.OriginalImageIdentity);

    public async Task<WorkspaceTabViewModel> CreatePanelCropAsync(PanelCropContext context,
        MarkerExemplarRectangle rectangle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPanelCropContextCurrent(context) || !ValidPanelCrop(rectangle, context.Width, context.Height))
            throw new InvalidOperationException("The crop selection is stale or outside the original panel limits.");
        WorkspaceTabViewModel parentTab = RequireTab(context.TabId);
        PanelRecord parent = CurrentProject.Panels.Single(panel => panel.PanelId.Value.ToString("D") == parentTab.PanelId);
        SourceReference source = CurrentProject.Sources.Single(item => item.SourceId == parent.SourceId);
        string sourcePath = source.LocalPath ?? throw new InvalidOperationException("The crop source has no local path.");
        string parentImageHash = parentTab.SourceSha256 ?? throw new InvalidOperationException("The crop parent has no image checksum.");
        var bitmap = (BitmapSource)parentTab.ImageSource!;
        if ((long)rectangle.Width * rectangle.Height * Math.Max(1, (bitmap.Format.BitsPerPixel + 7) / 8) > 64 * 1024 * 1024)
            throw new InvalidOperationException("The crop decoded pixel buffer exceeds its memory limit.");
        _rasterPanelsByTab.TryGetValue(context.TabId, out RasterWorkspacePanel? parentRaster);
        if (source.Kind == SourceKind.Image && parentRaster is null &&
            (CurrentProject.Panels.Count(panel => panel.SourceId == source.SourceId) != 1 ||
             parent.Crop != new CropRectangle(0, 0, context.Width, context.Height) || parent.Transforms.Count != 0 ||
             ((ImmutableImageBytes)context.OriginalImageIdentity!).Length > 64 * 1024 * 1024))
            throw new InvalidOperationException("The legacy crop source cannot be upgraded within the retained-image limits.");
        // Pixels are the already decoded, frozen ORIGINAL image. No enhancement or resampling participates.
        var encoded = await Task.Run(async () =>
        {
            await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            string sourceHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(sourceStream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(sourceHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The source file no longer matches its imported checksum.");
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = ProductionWorkflowImportStage.EncodeIntegerCrop(bitmap,
                new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height), cancellationToken);
            if (bytes.Length > 32 * 1024 * 1024) throw new InvalidOperationException("The encoded crop exceeds its memory limit.");
            RasterSourceImageEvidence? legacySource = source.Kind == SourceKind.Image && parentRaster is null
                ? new(new WorkflowImageEvidence(sourcePath, source.Sha256, context.Width, context.Height,
                    WorkflowImageVariant.Original), new ImmutableByteBuffer(((ImmutableImageBytes)context.OriginalImageIdentity!).Copy()))
                : null;
            var imageBytes = new ImmutableImageBytes(bytes);
            return (Original: imageBytes, Bitmap: CreateBitmap(imageBytes), Hash: Convert.ToHexStringLower(SHA256.HashData(bytes)), LegacySource: legacySource);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPanelCropContextCurrent(context)) throw new InvalidOperationException("The crop source changed during preparation.");
        Guid panelId = Guid.NewGuid();
        var imported = new WorkflowImportedPanel(panelId, source.SourceId.Value, parent.DisplayName,
            new WorkflowImageEvidence(sourcePath, encoded.Hash, rectangle.Width, rectangle.Height, WorkflowImageVariant.Original),
            parent.PageNumber);
        ImmutableImageBytes immutable = encoded.Original;
        WorkspaceTabViewModel child;
        PanelRecord seed;
        if (source.Kind == SourceKind.Image)
        {
            RasterSourceImageEvidence rasterSource = parentRaster?.SourceProvenance.Source ?? encoded.LegacySource
                ?? throw new InvalidOperationException("The raster source provenance is unavailable.");
            PdfRectD parentCrop = parentRaster?.SourceProvenance.EncodedCropInSourcePixels ?? new(0, 0, context.Width, context.Height);
            if (parentRaster is not null) VerifySavedRasterPanel(parent, parentRaster);
            PdfRectD fullCrop = new(parentCrop.X + rectangle.X, parentCrop.Y + rectangle.Y, rectangle.Width, rectangle.Height);
            var provenance = new RasterPanelSourceProvenance(rasterSource, encoded.Hash, fullCrop, fullCrop);
            var raster = new RasterWorkspacePanel(imported, immutable, provenance);
            child = CreateEmptyRasterTab(source, raster, encoded.Bitmap);
            seed = CreateRasterPanelSeed(child, provenance);
            if (!parent.Transforms.Any(IsRasterCropTransform))
            {
                var identity = new RasterPanelSourceProvenance(rasterSource, parentImageHash, parentCrop, parentCrop);
                CurrentProject = CurrentProject with { Panels = CurrentProject.Panels.Select(panel => panel.PanelId == parent.PanelId
                    ? panel with { Transforms = [CreateRasterCropTransform(parent.PanelId, identity)] } : panel).ToArray() };
                _rasterPanelsByTab[parentTab.TabId] = new RasterWorkspacePanel(
                    new WorkflowImportedPanel(parent.PanelId.Value, source.SourceId.Value, parent.DisplayName,
                        new WorkflowImageEvidence(sourcePath, parentImageHash, context.Width, context.Height, WorkflowImageVariant.Original)),
                    (ImmutableImageBytes)context.OriginalImageIdentity!, identity);
            }
            _rasterPanelsByTab[child.TabId] = raster;
        }
        else if (source.Kind == SourceKind.Pdf && _pdfPanelsByTab.TryGetValue(context.TabId, out PdfWorkspacePanel? pdf))
        {
            var provenance = CropPdfProvenance(pdf.SourceProvenance, panelId, rectangle);
            var childPdf = new PdfWorkspacePanel(imported, immutable, provenance,
                CurrentProject.Panels.Count(panel => panel.SourceId == source.SourceId));
            child = CreateEmptyPdfTab(source, childPdf, encoded.Bitmap);
            _pdfPanelsByTab[child.TabId] = childPdf;
            seed = ToPanelRecord(child);
        }
        else throw new InvalidOperationException("The original source provenance is unavailable for cropping.");
        var binding = new ManualCropBinding(ManualCropSchema, parent.PanelId.Value, parentImageHash, encoded.Hash, rectangle);
        seed = seed with { Validation = AddManualCropBinding(seed.Validation, binding) };
        RegisterImportedTab(child, immutable, []);
        CurrentProject = CurrentProject with { ModifiedUtc = DateTimeOffset.UtcNow, Panels = CurrentProject.Panels.Append(seed).ToArray() };
        child.IsDirty = true;
        return child;
    }

    private static bool ValidPanelCrop(MarkerExemplarRectangle? rectangle, int width, int height) => rectangle is not null &&
        rectangle.X >= 0 && rectangle.Y >= 0 && rectangle.Width is >= PanelCropLimits.MinimumSide and <= PanelCropLimits.MaximumSide &&
        rectangle.Height is >= PanelCropLimits.MinimumSide and <= PanelCropLimits.MaximumSide &&
        (long)rectangle.Width * rectangle.Height <= PanelCropLimits.MaximumPixels &&
        (long)rectangle.X + rectangle.Width <= width && (long)rectangle.Y + rectangle.Height <= height;

    private static bool IsManualPanelCrop(PanelRecord panel) => panel.Validation is { ValueKind: JsonValueKind.Object } validation &&
        validation.TryGetProperty(ManualCropKey, out _);

    private static JsonElement AddManualCropBinding(JsonElement? validation, ManualCropBinding binding)
    {
        JsonObject value = validation is { } existing ? JsonNode.Parse(existing.GetRawText())!.AsObject() : new JsonObject();
        value[ManualCropKey] = JsonSerializer.SerializeToNode(binding);
        return JsonSerializer.SerializeToElement(value);
    }

    private static PdfPanelSourceProvenance CropPdfProvenance(PdfPanelSourceProvenance parent, Guid childId, MarkerExemplarRectangle rect)
    {
        PdfRectD crop = new(parent.EncodedCropInSourcePixels.X + rect.X, parent.EncodedCropInSourcePixels.Y + rect.Y,
            rect.Width, rect.Height);
        return new(parent.DocumentSha256, childId, parent.FigureId, parent.PageNumber, parent.FigureSourceKind,
            crop, crop, parent.SourcePixelsToPagePoints);
    }

    private static async Task RestoreManualPanelCropsAsync(ProjectDocument project,
        Dictionary<PanelId, RasterWorkspacePanel> rasters, Dictionary<PanelId, PdfWorkspacePanel> pdfs,
        Dictionary<SourceId, ImportedImage> legacy, CancellationToken token)
    {
        var pending = project.Panels.Where(IsManualPanelCrop).ToList();
        while (pending.Count > 0)
        {
            bool progressed = false;
            foreach (PanelRecord child in pending.ToArray())
            {
                token.ThrowIfCancellationRequested();
                ManualCropBinding binding = child.Validation!.Value.GetProperty(ManualCropKey).Deserialize<ManualCropBinding>()
                    ?? throw new InvalidOperationException("The saved manual crop binding is invalid.");
                PanelRecord? parent = project.Panels.SingleOrDefault(panel => panel.PanelId.Value == binding.ParentPanelId);
                if (binding.Schema != ManualCropSchema || parent is null || parent.SourceId != child.SourceId || parent.PanelId == child.PanelId)
                    throw new InvalidOperationException("The saved manual crop has no matching parent source.");
                RasterWorkspacePanel? raster = rasters.GetValueOrDefault(parent.PanelId);
                PdfWorkspacePanel? pdf = pdfs.GetValueOrDefault(parent.PanelId);
                if (raster is null && pdf is null)
                {
                    if (legacy.ContainsKey(parent.SourceId)) throw new InvalidOperationException("Manual crop parents require retained source-bound pixels.");
                    continue;
                }
                ImmutableImageBytes parentBytes = raster?.OriginalBytes ?? pdf!.OriginalBytes;
                WorkflowImportedPanel parentPanel = raster?.Panel ?? pdf!.Panel;
                if (!string.Equals(binding.ParentImageSha256, parentPanel.Original.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !ValidPanelCrop(binding.Rectangle, parentPanel.Original.Width, parentPanel.Original.Height))
                    throw new InvalidOperationException("The saved manual crop no longer matches its parent pixels.");
                byte[] bytes = await Task.Run(() => ProductionWorkflowImportStage.EncodeIntegerCrop(CreateBitmap(parentBytes),
                    new(binding.Rectangle.X, binding.Rectangle.Y, binding.Rectangle.Width, binding.Rectangle.Height), token), token).ConfigureAwait(false);
                string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (!string.Equals(hash, binding.ChildImageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The saved manual crop checksum was not reproduced.");
                var imported = new WorkflowImportedPanel(child.PanelId.Value, child.SourceId.Value, child.DisplayName,
                    new WorkflowImageEvidence(parentPanel.Original.Reference, hash, binding.Rectangle.Width, binding.Rectangle.Height,
                        WorkflowImageVariant.Original), child.PageNumber);
                var immutable = new ImmutableImageBytes(bytes);
                if (raster is not null)
                {
                    PdfRectD parentCrop = raster.SourceProvenance.EncodedCropInSourcePixels;
                    PdfRectD crop = new(parentCrop.X + binding.Rectangle.X, parentCrop.Y + binding.Rectangle.Y,
                        binding.Rectangle.Width, binding.Rectangle.Height);
                    var provenance = new RasterPanelSourceProvenance(raster.SourceProvenance.Source, hash, crop, crop);
                    var retained = new RasterWorkspacePanel(imported, immutable, provenance);
                    VerifySavedRasterPanel(child, retained);
                    rasters.Add(child.PanelId, retained);
                }
                else
                {
                    int order = Array.FindIndex(project.Panels.Where(panel => panel.SourceId == child.SourceId).ToArray(), panel => panel.PanelId == child.PanelId);
                    var retained = new PdfWorkspacePanel(imported, immutable,
                        CropPdfProvenance(pdf!.SourceProvenance, child.PanelId.Value, binding.Rectangle), order);
                    PdfPanelSourceBinding.ValidateSavedPanel(project, project.Sources.Single(source => source.SourceId == child.SourceId), child, retained);
                    pdfs.Add(child.PanelId, retained);
                }
                pending.Remove(child);
                progressed = true;
            }
            if (!progressed) throw new InvalidOperationException("The saved manual crop parent lineage is missing or cyclic.");
        }
    }
}
