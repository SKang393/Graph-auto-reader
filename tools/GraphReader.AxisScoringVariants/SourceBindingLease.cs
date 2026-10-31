// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;

namespace GraphReader.AxisScoringVariants;

public sealed class SourceBindingLease : IDisposable
{
    private const string ManifestPath = "tools/GraphReader.AxisScoringVariants/source-bindings-v5.json";
    private static readonly string[] RequiredSources =
    [
        "Directory.Build.props", "Directory.Packages.props", "global.json",
        "src/GraphReader.Axis/GraphReader.Axis.csproj", "src/GraphReader.Axis/AxisGeometryContracts.cs",
        "src/GraphReader.Axis/AxisGeometryDetector.cs", "src/GraphReader.Axis/CalibrationContracts.cs",
        "src/GraphReader.Axis/OpenCvLineCandidateProvider.cs", "src/GraphReader.Axis/RobustCalibration.cs",
        "src/GraphReader.Axis/SessionLattice.cs",
        "tools/GraphReader.AxisScoringVariants/GraphReader.AxisScoringVariants.csproj",
        "tools/GraphReader.AxisScoringVariants/Program.cs",
        "tools/GraphReader.AxisScoringVariants/SourceBindingLease.cs",
        "tools/GraphReader.AxisScoringVariants/VariantEvaluation.cs",
    ];

    private readonly FileStream manifest;
    private readonly SortedDictionary<string, FileStream> sources;
    public string ManifestSha256 { get; }
    public IReadOnlyDictionary<string, string> SourceFiles { get; }

    private SourceBindingLease(FileStream manifest, string hash, SortedDictionary<string, FileStream> sources,
        SortedDictionary<string, string> expected)
    {
        this.manifest = manifest;
        ManifestSha256 = hash;
        this.sources = sources;
        SourceFiles = expected;
    }

    public static SourceBindingLease Open(string root, string expectedManifestSha256)
    {
        if (expectedManifestSha256.Length != 64 || !expectedManifestSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected source manifest hash must be SHA-256.");
        FileStream? manifest = null;
        var sources = new SortedDictionary<string, FileStream>(StringComparer.Ordinal);
        try
        {
            manifest = OpenFile(root, ManifestPath);
            string digest = Program.HashStream(manifest);
            if (!string.Equals(digest, expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Source manifest hash differs from the caller's pin.");
            manifest.Position = 0;
            using JsonDocument document = JsonDocument.Parse(manifest);
            JsonElement rootElement = document.RootElement;
            if (rootElement.GetProperty("schema").GetString() != "graphreader.axis-scoring-source-binding.v1")
                throw new InvalidDataException("Unexpected source binding schema.");
            var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in rootElement.GetProperty("source_files").EnumerateObject())
            {
                string hash = property.Value.GetString() ?? string.Empty;
                if (!expected.TryAdd(property.Name, hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                    throw new InvalidDataException("Malformed or duplicate source binding.");
            }
            if (!expected.Keys.SequenceEqual(RequiredSources.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new InvalidDataException("Source binding does not contain the fixed source closure.");
            foreach ((string path, string hash) in expected)
            {
                FileStream stream = OpenFile(root, path);
                sources.Add(path, stream);
                if (!string.Equals(Program.HashStream(stream), hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Source hash differs from the pin: {path}");
            }
            return new SourceBindingLease(manifest, digest, sources, expected);
        }
        catch
        {
            foreach (FileStream stream in sources.Values) stream.Dispose();
            manifest?.Dispose();
            throw;
        }
    }

    public void Verify()
    {
        if (!string.Equals(Program.HashStream(manifest), ManifestSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Source manifest changed during evaluation.");
        foreach ((string path, FileStream stream) in sources)
            if (!string.Equals(Program.HashStream(stream), SourceFiles[path], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Source changed during evaluation: {path}");
    }

    public void Dispose()
    {
        foreach (FileStream stream in sources.Values) stream.Dispose();
        manifest.Dispose();
    }

    private static FileStream OpenFile(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source binding escaped the project.");
        string? current = path;
        while (current is not null && current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Source binding contains a reparse point.");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
