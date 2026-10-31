// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Axis;

namespace GraphReader.AxisScoringVariants;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 4 || args[0] != "--output" || args[2] != "--expected-source-manifest-sha256")
                throw new ArgumentException("Use --output and --expected-source-manifest-sha256.");
            string root = Path.GetFullPath(Environment.CurrentDirectory);
            if (!string.Equals(root, @"E:\Graph-auto-reader", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(root, "AGENTS.md")))
                throw new InvalidOperationException("Run from the authorized project root on E:.");
            string allowed = Path.GetFullPath(Path.Combine(root, "artifacts", "goal22-runs")) + Path.DirectorySeparatorChar;
            string output = Path.GetFullPath(args[1]);
            if (!output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) ||
                !output.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Output must be a JSON file under artifacts/goal22-runs.");
            RejectReparseAncestors(root, Path.GetDirectoryName(output)!);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            RejectReparseAncestors(root, Path.GetDirectoryName(output)!);
            using SourceBindingLease sourceBinding = SourceBindingLease.Open(root, args[3]);
            using var axisStream = OpenProjectAssembly(root, typeof(AxisGeometryDetector).Assembly.Location);
            using var toolStream = OpenProjectAssembly(root, typeof(Program).Assembly.Location);
            string axisHash = HashStream(axisStream);
            string toolHash = HashStream(toolStream);
            VariantResult[] results = await VariantEvaluation.EvaluateAsync().ConfigureAwait(false);
            sourceBinding.Verify();
            if (axisHash != HashStream(axisStream) || toolHash != HashStream(toolStream))
                throw new InvalidOperationException("Runtime assembly changed during evaluation.");
            var report = new
            {
                schema = "graphreader.axis-scoring-synthetic-variants.v1",
                scope = "generated_line_candidates_only",
                production_approved = false,
                corpus_reads = 0,
                model_inference = 0,
                training_steps = 0,
                source_manifest_sha256 = sourceBinding.ManifestSha256,
                source_files = sourceBinding.SourceFiles,
                axis_assembly_sha256 = axisHash,
                tool_assembly_sha256 = toolHash,
                limitations = new[]
                {
                    "Generated line candidates exercise pair scoring only; they do not validate raster decoding, OpenCV line detection, marker classification, or end-to-end extraction.",
                    "Synthetic reference corners are used only after detection for measurement, never as detector input.",
                    "Results are exploratory and do not establish product accuracy or release approval.",
                    "Source hashes are verified before and after evaluation; they do not independently prove that the recorded binaries were built from those sources.",
                },
                results,
            };
            await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, report, JsonOptions).ConfigureAwait(false);
            Console.WriteLine($"variants={results.Length} completed={results.Count(static item => item.Status == "completed")} needs_review={results.Count(static item => item.NeedsReview)}");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"axis_variant_evaluation_failed={exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static FileStream OpenProjectAssembly(string root, string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Runtime assemblies must remain in the project.");
        RejectReparseAncestors(root, Path.GetDirectoryName(full)!);
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    internal static string HashStream(FileStream stream)
    {
        stream.Position = 0;
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void RejectReparseAncestors(string root, string directory)
    {
        string? current = directory;
        while (current is not null && current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Output paths containing reparse points are not supported.");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) return;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("Output must remain inside the project root.");
    }
}
