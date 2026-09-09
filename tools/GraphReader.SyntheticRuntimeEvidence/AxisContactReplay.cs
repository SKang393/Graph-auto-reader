// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Axis;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>Replays one authenticated annotation-free synthetic panel inventory.</summary>
internal static class AxisContactReplay
{
    private const string ManifestSha = "3960110f02d273a87fb6e0925f1f409464378f4640fa8d766f4f3aadb4600576";
    private const string ProtocolSha = "ef900016ed45c8a83908370e6a928ecb4fff8e747abc9e4772e835351a936fcc";
    private const string NativeSha = "c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task<int> RunCommandAsync(string[] args, string repositoryRoot)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            return await RunAsync(args, repositoryRoot, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                Status = "void", Error = exception.Message, ProductionApproved = false,
            }, JsonOptions));
            return exception is OperationCanceledException ? 130 : 1;
        }
    }

    private static async Task<int> RunAsync(string[] args, string repositoryRoot, CancellationToken cancellationToken)
    {
        string artifactRoot = Path.Combine(repositoryRoot, "artifacts", "goal22-runs", "axis-contact-replay");
        string inputPath = Inside(artifactRoot, args[1]);
        string outputRoot = Inside(artifactRoot, args[3]);
        if (!string.Equals(args[2], ManifestSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only the fixed synthetic panel manifest is authorized for this replay.");
        }
        byte[] manifestBytes = ReadVerified(inputPath, ManifestSha);
        using var manifest = JsonDocument.Parse(manifestBytes);
        JsonElement root = manifest.RootElement;
        if (Text(root, "schema") != "graphreader.axis-contact-replay-input.v1" ||
            !root.GetProperty("synthetic_only").GetBoolean() ||
            root.GetProperty("private_data").GetBoolean() || root.GetProperty("sealed_data").GetBoolean() ||
            root.GetProperty("truth_included").GetBoolean() || root.GetProperty("production_approved").GetBoolean() ||
            Text(root, "protocol_sha256") != ProtocolSha || Text(root, "native_sha256") != NativeSha)
        {
            throw new InvalidDataException("Replay scope mismatch.");
        }
        ReadVerified(Inside(repositoryRoot, Path.Combine(repositoryRoot, Text(root, "protocol_path"))), ProtocolSha);
        ReadVerified(Inside(repositoryRoot, Path.Combine(repositoryRoot, Text(root, "family_binding_path"))),
            Text(root, "family_binding_sha256"));
        var inputs = new List<(JsonElement Record, byte[] Gray)>();
        foreach (JsonElement panel in root.GetProperty("panels").EnumerateArray())
        {
            byte[] gray = ReadVerified(Inside(Path.Combine(artifactRoot, "input"),
                Path.Combine(repositoryRoot, Text(panel, "gray_path"))), Text(panel, "gray_sha256"));
            int width = panel.GetProperty("width").GetInt32();
            int height = panel.GetProperty("height").GetInt32();
            int[] crop = panel.GetProperty("crop").EnumerateArray().Select(item => item.GetInt32()).ToArray();
            if (width <= 0 || height <= 0 || gray.Length != checked(width * height) || crop.Length != 4 ||
                crop[0] < 0 || crop[1] < 0 || crop[2] != width || crop[3] != height ||
                checked(crop[0] + width) > panel.GetProperty("source_width").GetInt32() ||
                checked(crop[1] + height) > panel.GetProperty("source_height").GetInt32())
            {
                throw new InvalidDataException("Panel raster or source crop dimensions mismatch.");
            }
            inputs.Add((panel, gray));
        }
        if (inputs.Count != 32 || inputs.Count(item => Text(item.Record, "split") == "train") != 23 ||
            inputs.Count(item => Text(item.Record, "split") == "validation") != 9 ||
            inputs.Select(item => Text(item.Record, "panel_id")).Distinct().Count() != 32)
        {
            throw new InvalidDataException("The complete fixed panel inventory is required.");
        }
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            throw new IOException("Use a new output directory; previous evidence is retained.");
        }
        string nativePath = Path.GetFullPath(Text(root, "native_path"));
        using var nativeLock = new FileStream(nativePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexStringLower(SHA256.HashData(nativeLock)) != NativeSha)
        {
            throw new InvalidDataException("Native runtime checksum mismatch.");
        }
        nint nativeHandle = NativeLibrary.Load(nativePath);
        NativeLibrary.SetDllImportResolver(typeof(OpenCvSharp.Mat).Assembly,
            (name, _, _) => name == "OpenCvSharpExtern" ? nativeHandle : nint.Zero);
        Directory.CreateDirectory(outputRoot);
        var total = Stopwatch.StartNew();
        var outcomes = new List<object>();
        int failed = 0;
        foreach ((JsonElement panel, byte[] gray) in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            IReadOnlyList<GeometryLineCandidate>? candidates = null;
            AxisGeometryResult? geometry = null;
            string? error = null;
            try
            {
                var frame = new GrayscaleLineCandidateFrame(
                    panel.GetProperty("width").GetInt32(), panel.GetProperty("height").GetInt32(),
                    panel.GetProperty("width").GetInt32(), gray);
                candidates = await new OpenCvLineCandidateProvider()
                    .DetectLinesAsync(frame, cancellationToken).ConfigureAwait(false);
                geometry = await new AxisGeometryDetector().DetectAsync(
                    new AxisGeometryRequest(frame.Width, frame.Height, candidates), cancellationToken).ConfigureAwait(false);
            }
            catch (AxisGeometryDetectionException exception)
            {
                error = exception.Code + ": " + exception.Message;
                failed++;
            }
            timer.Stop();
            double[] crop = panel.GetProperty("crop").EnumerateArray().Select(item => item.GetDouble()).ToArray();
            outcomes.Add(new
            {
                Input = panel, Status = geometry is null ? "geometry-not-found" : "geometry-returned",
                Error = error, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                GeometryCoordinateFrame = "immutable_saved_panel_raster_pixels",
                GeometryPanelPixels = geometry, CandidatesPanelPixels = candidates,
                SourcePolygon = geometry?.PlotPolygon.Points.Select(point => new[] { point.X + crop[0], point.Y + crop[1] }).ToArray(),
            });
        }
        cancellationToken.ThrowIfCancellationRequested();
        total.Stop();
        string reportPath = Path.Combine(outputRoot, "report.json");
        await using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, new
        {
            Schema = "graphreader.axis-contact-replay-report.v1", SyntheticOnly = true,
            PrivateReads = 0, SealedReads = 0, ModelRuns = 0, ProductionApproved = false,
            InputManifestSha256 = ManifestSha, ProtocolSha256 = ProtocolSha, NativeSha256 = NativeSha,
            Assemblies = new[] { typeof(AxisContactReplay).Assembly, typeof(AxisGeometryDetector).Assembly,
                typeof(GraphReader.App.Integration.Workflow.ProductionAxisGeometryAdapter).Assembly }
                .Select(assembly => new { Name = assembly.GetName().Name, Sha256 = Hash(File.ReadAllBytes(assembly.Location)) }),
            PanelCount = inputs.Count, FailedPanelCount = failed, ElapsedMilliseconds = total.Elapsed.TotalMilliseconds,
            SourcePolygonCoordinateFrame = "immutable_whole_source_pixels", Panels = outcomes,
        }, JsonOptions, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new { Report = reportPath, Panels = inputs.Count, FailedPanels = failed }));
        return 0;
    }

    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString()
        ?? throw new InvalidDataException("Missing string: " + key);

    private static byte[] ReadVerified(string path, string expected)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(Hash(bytes), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Replay input checksum mismatch: " + Path.GetFileName(path));
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Inside(string root, string path)
    {
        string full = Path.GetFullPath(path);
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Replay paths must remain inside the assigned artifact workspace.");
        return full;
    }
}
