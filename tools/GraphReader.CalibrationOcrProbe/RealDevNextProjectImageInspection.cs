// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.RealAcceptance.Ocr;

namespace GraphReader.CalibrationOcrProbe;

internal static partial class RealDevCalibrationCheck
{
    internal static NextProjectOutcome RunNextProjectImageInspection(string corpus, string locator, string output,
        CpuBudgetJob budget, CancellationToken token)
    {
        string[] names = ["preflight", "locator", "selection", "source_read", "identity", "write", "preservation"];
        var stages = names.ToDictionary(name => name, _ => "unattempted", StringComparer.Ordinal);
        var timings = new Dictionary<string, double>(StringComparer.Ordinal);
        var total = Stopwatch.StartNew(); var step = Stopwatch.StartNew();
        string stage = "preflight"; string? failure = null, imageHash = null;
        int sourceAttempts = 0, sourceCompleted = 0, writeAttempts = 0; bool written = false, preserved = false;
        int? width = null, height = null; FileStream? original = null;
        void Next(string next) { stages[stage] = "completed"; timings[stage] = step.Elapsed.TotalMilliseconds; stage = next; step.Restart(); }
        try
        {
            token.ThrowIfCancellationRequested();
            if (budget.MaximumLogicalCores is <= 0 or > .5 || !Path.IsPathFullyQualified(corpus) ||
                !Path.IsPathFullyQualified(locator) || !Path.IsPathFullyQualified(output)) throw new InvalidDataException("INSPECTION_PREFLIGHT");
            string artifacts = Path.GetFullPath("artifacts").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string target = Path.GetFullPath(output), corpusRoot = Path.GetFullPath(corpus).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string artifactPrefix = artifacts + Path.DirectorySeparatorChar, corpusPrefix = corpusRoot + Path.DirectorySeparatorChar;
            if (!target.StartsWith(artifactPrefix, StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(corpusPrefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(target), ".png", StringComparison.OrdinalIgnoreCase) || File.Exists(target) || Directory.Exists(target))
                throw new InvalidDataException("INSPECTION_OUTPUT_PATH");
            string? parent = Path.GetDirectoryName(target);
            if (parent is null || !Directory.Exists(parent)) throw new InvalidDataException("INSPECTION_OUTPUT_PARENT");
            for (DirectoryInfo? directory = new(parent); directory is not null; directory = directory.Parent)
            {
                if (string.Equals(directory.Name, "Data", StringComparison.OrdinalIgnoreCase) ||
                    (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("INSPECTION_OUTPUT_REPARSE_OR_DATA");
            }
            Next("locator");
            JsonElement saved = ParseNextWorkLocator(Program.ReadBounded(locator, 4096));
            Next("selection");
            FrozenDevTrainingSelector selector = FrozenDevTrainingSelector.Load(corpus, token);
            FrozenRealCorpusProject selected = SelectNextProject(selector.Describe(), selector.EnumerateSelectedProjects(token));
            if (selected.AnonymizedCaseId != saved.GetProperty("anonymized_case_id").GetString()) throw new InvalidDataException("INSPECTION_SELECTION");
            _ = selector.RequireProject(selected.GetProjectPath(), selected.AnonymizedCaseId, selected.AnonymizedStudyId, token);
            Next("source_read"); sourceAttempts++;
            original = new FileStream(selected.GetProjectPath(), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(original, selected.GetProjectPath());
            if (original.Length is <= 0 or > EngaugeDigWholeWorkflowTruthAdapter.MaximumProjectBytes) throw new InvalidDataException("INSPECTION_SOURCE_SIZE");
            string projectHash = HashStream(original, token);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            ImageOnlyPayload image = ReadLockedImageOnly(selector, selected, digest, ResolveEmbeddedImageReader(), token);
            sourceCompleted++; imageHash = Program.Hash(image.Image); width = image.Width; height = image.Height;
            string sourceDigest = Convert.ToHexStringLower(digest.GetHashAndReset());
            Next("identity");
            JsonElement panel = saved.GetProperty("panels")[0], crop = panel.GetProperty("crop");
            if (projectHash != saved.GetProperty("project_sha256").GetString() || imageHash != saved.GetProperty("embedded_image_sha256").GetString() ||
                sourceDigest != saved.GetProperty("single_source_identity_digest").GetString() || imageHash != panel.GetProperty("image_sha256").GetString() ||
                image.Width != panel.GetProperty("width").GetInt32() || image.Height != panel.GetProperty("height").GetInt32() ||
                crop.GetProperty("x").GetDouble() != 0 || crop.GetProperty("y").GetDouble() != 0 ||
                crop.GetProperty("width").GetDouble() != image.Width || crop.GetProperty("height").GetDouble() != image.Height)
                throw new InvalidDataException("INSPECTION_SOURCE_OR_PANEL_IDENTITY");
            original.Position = 0;
            if (HashStream(original, token) != projectHash || SelectNextProject(selector.Describe(), selector.EnumerateSelectedProjects(token)).AnonymizedCaseId != selected.AnonymizedCaseId)
                throw new InvalidDataException("INSPECTION_FINAL_IDENTITY");
            EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(original, selected.GetProjectPath());
            token.ThrowIfCancellationRequested();
            Next("write"); writeAttempts++;
            // Recheck the existing output chain immediately before creating the file.
            for (DirectoryInfo? directory = new(parent); directory is not null; directory = directory.Parent)
                if (string.Equals(directory.Name, "Data", StringComparison.OrdinalIgnoreCase) ||
                    (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("INSPECTION_OUTPUT_REPARSE_OR_DATA");
            using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { destination.Write(image.Image); destination.Flush(flushToDisk: true); }
            written = true;
            Next("preservation");
            using (var copy = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (HashStream(copy, token) != imageHash) throw new InvalidDataException("INSPECTION_COPY_IDENTITY");
            original.Position = 0;
            preserved = HashStream(original, token) == projectHash && Program.Hash(image.Image) == imageHash &&
                SelectNextProject(selector.Describe(), selector.EnumerateSelectedProjects(token)).AnonymizedCaseId == selected.AnonymizedCaseId;
            EngaugeDevSourceMaterializer.ValidateOpenedSourcePath(original, selected.GetProjectPath());
            if (!preserved) throw new InvalidDataException("INSPECTION_SOURCE_CHANGED");
            stages[stage] = "completed"; timings[stage] = step.Elapsed.TotalMilliseconds;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = error is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : TraceExceptionCategory(error);
            stages[stage] = "failed"; timings[stage] = step.Elapsed.TotalMilliseconds;
        }
        finally { original?.Dispose(); }
        bool success = failure is null && written && preserved;
        return new(new { schema = "graphreader.real-dev-next-project-image-inspection.v1", status = success ? "completed" : "refused_or_failed",
            failure, stages, timings, declared_sources = 1, source_attempted = sourceAttempts, source_completed = sourceCompleted,
            source_failed = sourceAttempts - sourceCompleted, source_unattempted = 1 - sourceAttempts,
            png_write_attempted = writeAttempts, png_write_unattempted = 1 - writeAttempts, png_written = written,
            original_preserved = preserved, width, height, image_sha256 = imageHash,
            locator_sha256 = NextWorkLocatorHash, elapsed_milliseconds = total.Elapsed.TotalMilliseconds, budget.MaximumLogicalCores,
            other_project_body_reads = 0, sealed_reads = 0, truth_reads = 0, geometry_calls = 0, rust_calls = 0, ocr_calls = 0, model_calls = 0 }, success);
    }
}
