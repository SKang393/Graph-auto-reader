// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphReader.Axis;

namespace GraphReader.AxisCandidateEvidence;

internal static class Program
{
    private const string ExpectedBindingSha256 = "667c1ec361e89487d4b5656d3a0e263a65fd7b981bbfe664a036b31c3991ac6e";
    private const string BindingFileName = "input-binding.json";
    private static readonly JsonSerializerOptions InputJsonOptions = CreateInputJsonOptions();

    private static async Task<int> Main(string[] args)
    {
        try
        {
            string root = FindProjectRoot(Environment.CurrentDirectory);
            if (args.Length >= 1 && string.Equals(args[0], "--panel-input-manifest", StringComparison.Ordinal))
            {
                return await RunPanelBatchAsync(root, args).ConfigureAwait(false);
            }

            string outputPath = ParseOutputPath(args);
            AxisCandidateEvidenceInputBinding binding = LoadBinding(root);
            var runner = new AxisCandidateEvidenceRunner(root, binding);
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            AxisCandidateEvidenceReport report = await runner.RunAsync(cancellation.Token).ConfigureAwait(false);
            string outputSha256 = AxisCandidateEvidenceReportWriter.WriteNewReport(root, outputPath, report);
            Console.WriteLine($"status={report.Status}");
            Console.WriteLine($"report={outputPath.Replace('\\', '/')}");
            Console.WriteLine($"report_sha256={outputSha256}");
            Console.WriteLine($"candidate_count={report.CandidateSnapshots.Count}");
            return report.Status == "success" ? 0 : report.Status == "cancelled" ? 130 : 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"axis_candidate_evidence_failed={exception.GetType().Name}");
            return 2;
        }
    }

    private static async Task<int> RunPanelBatchAsync(string root, string[] args)
    {
        if (args.Length != 6 ||
            !string.Equals(args[2], "--panel-input-sha256", StringComparison.Ordinal) ||
            !string.Equals(args[4], "--output", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Usage: GraphReader.AxisCandidateEvidence --panel-input-manifest artifacts/goal22-runs/<binding>.json --panel-input-sha256 <sha256> --output artifacts/goal22-runs/<fresh>/report.json");
        }

        var runner = AxisCandidatePanelBatchEvidenceRunner.Load(root, args[1], args[3]);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        AxisCandidatePanelBatchReport report = await runner.RunAsync(cancellation.Token).ConfigureAwait(false);
        string outputSha256 = AxisCandidatePanelBatchReportWriter.WriteNew(root, args[5], report);
        Console.WriteLine($"status={report.Status}");
        Console.WriteLine($"report={args[5].Replace('\\', '/')}");
        Console.WriteLine($"report_sha256={outputSha256}");
        Console.WriteLine($"source_count={report.SourceCount}");
        Console.WriteLine($"panel_count={report.PanelCount}");
        Console.WriteLine($"selected_panel_count={report.SelectedPanelCount}");
        Console.WriteLine($"processed_panel_count={report.PanelResults.Count}");
        return report.Status == "success" ? 0 : report.Status == "cancelled" ? 130 : 2;
    }

    private static AxisCandidateEvidenceInputBinding LoadBinding(string root)
    {
        string path = Path.Combine(AppContext.BaseDirectory, BindingFileName);
        EnsureNoReparsePoints(path, root, allowMissing: false);
        byte[] bytes = ReadLocked(path);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, ExpectedBindingSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Pinned public input binding bytes differ.");
        }

        AxisCandidateEvidenceInputBinding? binding = JsonSerializer.Deserialize<AxisCandidateEvidenceInputBinding>(bytes, InputJsonOptions);
        return binding ?? throw new InvalidDataException("Public input binding is empty.");
    }

    private static string ParseOutputPath(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--output", StringComparison.Ordinal))
        {
            throw new ArgumentException("Usage: GraphReader.AxisCandidateEvidence --output artifacts/goal22-runs/<fresh-name>/report.json");
        }

        return args[1];
    }

    private static string FindProjectRoot(string start)
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")) &&
                File.Exists(Path.Combine(current.FullName, "src", "GraphReader.Axis", "GraphReader.Axis.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Graph Auto Reader project root.");
    }

    internal static byte[] ReadLocked(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 32 * 1024 * 1024)
        {
            throw new InvalidDataException("Bound input exceeds the diagnostic size limit.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static JsonSerializerOptions CreateInputJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    internal static void EnsureNoReparsePoints(string path, string root, bool allowMissing)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Bound path escapes the project root.");
        }

        string current = fullRoot;
        RejectReparse(current);
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            try
            {
                RejectReparse(current);
            }
            catch (FileNotFoundException) when (allowMissing)
            {
                continue;
            }
            catch (DirectoryNotFoundException) when (allowMissing)
            {
                continue;
            }
        }

        static void RejectReparse(string candidate)
        {
            FileAttributes attributes = File.GetAttributes(candidate);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Reparse point is not permitted: {candidate}");
            }
        }
    }
}
