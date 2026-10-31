// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GraphReader.Axis;
using OpenCvSharp;

namespace GraphReader.AxisCandidateEvidence;

public sealed class AxisCandidateEvidenceRunner
{
    public const string ReportSchema = "graphreader.axis-candidate-evidence.v2";

    private static readonly string[] RequiredAxisSourcePaths =
    [
        "src/GraphReader.Axis/AxisGeometryContracts.cs",
        "src/GraphReader.Axis/AxisGeometryDetector.cs",
        "src/GraphReader.Axis/OpenCvLineCandidateProvider.cs",
        "src/GraphReader.Axis/GraphReader.Axis.csproj",
        "Directory.Packages.props",
    ];

    private static readonly string[] ToolSourcePaths =
    [
        "tools/GraphReader.AxisCandidateEvidence/GraphReader.AxisCandidateEvidence.csproj",
        "tools/GraphReader.AxisCandidateEvidence/EvidenceContracts.cs",
        "tools/GraphReader.AxisCandidateEvidence/FeatureNormalization.cs",
        "tools/GraphReader.AxisCandidateEvidence/EvidenceRunner.cs",
        "tools/GraphReader.AxisCandidateEvidence/Program.cs",
        "tools/GraphReader.AxisCandidateEvidence/input-binding.json",
    ];

    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private readonly string _projectRoot;
    private readonly AxisCandidateEvidenceInputBinding _binding;
    private readonly ILineCandidateProvider _candidateProvider;
    private readonly IAxisGeometryDetector _detector;

    public AxisCandidateEvidenceRunner(
        string projectRoot,
        AxisCandidateEvidenceInputBinding binding,
        ILineCandidateProvider? candidateProvider = null,
        IAxisGeometryDetector? detector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(binding);
        _projectRoot = Path.GetFullPath(projectRoot);
        _binding = binding;
        _candidateProvider = candidateProvider ?? new OpenCvLineCandidateProvider(binding.OpenCvOptions);
        _detector = detector ?? new AxisGeometryDetector();
    }

    public async Task<AxisCandidateEvidenceReport> RunAsync(CancellationToken cancellationToken = default)
    {
        int width = 0;
        int height = 0;
        AxisCandidateEvidenceRuntime? runtime = null;
        var source = CreateSource(0, 0);
        IReadOnlyList<AxisGeometryCandidateSnapshot> snapshots = Array.Empty<AxisGeometryCandidateSnapshot>();

        try
        {
            ValidateBinding();
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<string, string> sourceBindingsBefore = VerifyAxisSourceBindings();
            IReadOnlyDictionary<string, string> toolBindings = ReadToolSourceBindings();

            string manifestPath = ResolveBoundPath(_binding.SourceManifestPath);
            byte[] manifestBytes = Program.ReadLocked(manifestPath);
            EnsureHash(manifestBytes, _binding.SourceManifestSha256, "source manifest");

            string imagePath = ResolveBoundPath(_binding.SourceImagePath);
            byte[] imageBytes = Program.ReadLocked(imagePath);
            string imageHash = Hash(imageBytes);
            if (imageBytes.LongLength != _binding.SourceImageByteLength ||
                !string.Equals(imageHash, _binding.SourceImageSha256, StringComparison.Ordinal) ||
                !string.Equals(imageHash, _binding.PanelSha256, StringComparison.Ordinal))
            {
                throw new EvidenceException("SOURCE_IMAGE_HASH_MISMATCH", typeof(InvalidDataException));
            }

            using var gray = Cv2.ImDecode(imageBytes, ImreadModes.Grayscale);
            if (gray.Empty() || gray.Width <= 0 || gray.Height <= 0)
            {
                throw new EvidenceException("SOURCE_IMAGE_DECODE_FAILED", typeof(InvalidDataException));
            }

            width = gray.Width;
            height = gray.Height;
            source = CreateSource(width, height);
            cancellationToken.ThrowIfCancellationRequested();

            int rowStride = width;
            byte[] pixels = new byte[checked(rowStride * height)];
            long nativeStride = gray.Step();
            for (int row = 0; row < height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.Copy(
                    IntPtr.Add(gray.Data, checked((int)(row * nativeStride))),
                    pixels,
                    checked(row * rowStride),
                    width);
            }

            var frame = new GrayscaleLineCandidateFrame(width, height, rowStride, pixels);
            IReadOnlyList<GeometryLineCandidate> candidates = await _candidateProvider
                .DetectLinesAsync(frame, cancellationToken)
                .ConfigureAwait(false);
            if (candidates.Count > 16384)
            {
                throw new EvidenceException("CANDIDATE_COUNT_LIMIT_EXCEEDED", typeof(InvalidDataException));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var observer = new CapturingObserver();
            var request = new AxisGeometryRequest(
                width,
                height,
                candidates,
                _binding.GeometryOptions,
                _binding.CoordinateSpace)
            {
                CandidateObserver = observer,
            };

            _ = await _detector.DetectAsync(request, cancellationToken).ConfigureAwait(false);
            snapshots = observer.Candidates ?? throw new EvidenceException("RANKED_CANDIDATES_NOT_CAPTURED", typeof(InvalidDataException));

            EnsureSameHash(manifestPath, _binding.SourceManifestSha256, "source manifest after detection");
            EnsureSameHash(imagePath, _binding.SourceImageSha256, "source image after detection");
            IReadOnlyDictionary<string, string> sourceBindingsAfter = VerifyAxisSourceBindings();
            if (!DictionaryEquals(sourceBindingsBefore, sourceBindingsAfter))
            {
                throw new EvidenceException("AXIS_SOURCE_CHANGED_DURING_CAPTURE", typeof(InvalidDataException));
            }

            IReadOnlyDictionary<string, string> toolBindingsAfter = ReadToolSourceBindings();
            if (!DictionaryEquals(toolBindings, toolBindingsAfter))
            {
                throw new EvidenceException("TOOL_SOURCE_CHANGED_DURING_CAPTURE", typeof(InvalidDataException));
            }

            runtime = CreateRuntime(sourceBindingsAfter, toolBindingsAfter);
            return new AxisCandidateEvidenceReport(
                ReportSchema,
                "success",
                source,
                runtime,
                snapshots,
                FeatureNormalization.Build(width, height, snapshots),
                null);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("cancelled", "REQUEST_CANCELLED", exception.GetType());
        }
        catch (AxisGeometryDetectionException exception)
        {
            return Failure("axis_geometry_failure", exception.Code, exception.GetType());
        }
        catch (OperationCanceledException exception)
        {
            return Failure("unexpected_cancellation", "UNEXPECTED_OPERATION_CANCELLATION", exception.GetType());
        }
        catch (EvidenceException exception)
        {
            return Failure("input_or_provenance_failure", exception.Code, exception.ExceptionType);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return Failure("processing_failure", null, exception.GetType(), exception.Message);
        }

        AxisCandidateEvidenceReport Failure(string kind, string? code, Type exceptionType, string? message = null) => new(
            ReportSchema,
            kind == "cancelled" ? "cancelled" : "failed",
            source,
            runtime,
            Array.Empty<AxisGeometryCandidateSnapshot>(),
            null,
            new AxisCandidateEvidenceFailure(kind, code, exceptionType.FullName ?? exceptionType.Name, message));
    }

    private AxisCandidateEvidenceSource CreateSource(int width, int height) => new(
        _binding.Scope,
        _binding.SourceManifestPath,
        _binding.SourceManifestSha256,
        _binding.SourceSplit,
        _binding.SourceId,
        _binding.PanelId,
        _binding.SourceImagePath,
        _binding.SourceImageSha256,
        _binding.PanelSha256,
        width,
        height,
        _binding.CoordinateSpace);

    private void ValidateBinding()
    {
        bool scopeAndSplitValid =
            (_binding.Scope == "public_synthetic_development" && _binding.SourceSplit == "validation") ||
            (_binding.Scope == "synthetic_test_fixture" && _binding.SourceSplit == "test-fixture");
        if (_binding.Schema != "graphreader.axis-candidate-evidence.input.v1" ||
            !scopeAndSplitValid ||
            _binding.CoordinateSpace != AxisGeometryCoordinateSpaces.OriginalPixels ||
            !Guid.TryParse(_binding.SourceId, out _) ||
            !Guid.TryParse(_binding.PanelId, out _) ||
            !IsHash(_binding.SourceManifestSha256) ||
            !IsHash(_binding.SourceImageSha256) ||
            !IsHash(_binding.PanelSha256) ||
            _binding.SourceImageByteLength <= 0 ||
            _binding.SourceBindings is null ||
            !_binding.SourceBindings.Keys.Order(StringComparer.Ordinal).SequenceEqual(
                RequiredAxisSourcePaths.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            _binding.Claims is null || _binding.Claims.Count != 3)
        {
            throw new EvidenceException("INPUT_BINDING_SCHEMA_INVALID", typeof(InvalidDataException));
        }

        string manifestPath = ResolveBoundPath(_binding.SourceManifestPath);
        string imagePath = ResolveBoundPath(_binding.SourceImagePath);
        _ = manifestPath;
        _ = imagePath;
        foreach ((string path, string hash) in _binding.SourceBindings)
        {
            if (!IsHash(hash))
            {
                throw new EvidenceException("SOURCE_BINDING_HASH_INVALID", typeof(InvalidDataException));
            }
        }
    }

    private string ResolveBoundPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
        {
            throw new EvidenceException("BOUND_PATH_INVALID", typeof(InvalidDataException));
        }

        string[] segments = relativePath.Split('/');
        if (segments.Any(static segment => segment is "" or "." or ".." ||
            segment.Contains("private", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("sealed", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("tests", StringComparison.OrdinalIgnoreCase)))
        {
            throw new EvidenceException("BOUND_PATH_SCOPE_REJECTED", typeof(InvalidDataException));
        }

        string fullPath = Path.GetFullPath(Path.Combine(_projectRoot, Path.Combine(segments)));
        Program.EnsureNoReparsePoints(fullPath, _projectRoot, allowMissing: false);
        return fullPath;
    }

    private SortedDictionary<string, string> VerifyAxisSourceBindings()
    {
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string relative, string expected) in _binding.SourceBindings)
        {
            string path = ResolveBoundPath(relative);
            byte[] bytes = Program.ReadLocked(path);
            string hash = Hash(bytes);
            if (!string.Equals(hash, expected, StringComparison.Ordinal))
            {
                throw new EvidenceException("AXIS_SOURCE_HASH_MISMATCH", typeof(InvalidDataException));
            }

            actual.Add(relative, hash);
        }

        return actual;
    }

    private SortedDictionary<string, string> ReadToolSourceBindings()
    {
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string relative in ToolSourcePaths)
        {
            string path = ResolveBoundPath(relative);
            actual.Add(relative, Hash(Program.ReadLocked(path)));
        }

        return actual;
    }

    private void EnsureSameHash(string path, string expectedHash, string field)
    {
        Program.EnsureNoReparsePoints(path, _projectRoot, allowMissing: false);
        string actual = Hash(Program.ReadLocked(path));
        if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
        {
            throw new EvidenceException("BOUND_INPUT_CHANGED", typeof(InvalidDataException), field);
        }
    }

    private AxisCandidateEvidenceRuntime CreateRuntime(
        IReadOnlyDictionary<string, string> sourceBindings,
        IReadOnlyDictionary<string, string> toolBindings)
    {
        (string toolAssemblyPath, string toolAssemblyHash) = HashAssembly(Assembly.GetExecutingAssembly());
        (string axisAssemblyPath, string axisAssemblyHash) = HashAssembly(typeof(AxisGeometryDetector).Assembly);
        (string openCvAssemblyPath, string openCvAssemblyHash) = HashAssembly(typeof(Mat).Assembly);
        (string nativeModulePath, string nativeModuleHash) = HashLoadedOpenCvNativeModule();
        return new AxisCandidateEvidenceRuntime(
            toolAssemblyPath,
            toolAssemblyHash,
            axisAssemblyPath,
            axisAssemblyHash,
            openCvAssemblyPath,
            openCvAssemblyHash,
            nativeModulePath,
            nativeModuleHash,
            sourceBindings,
            toolBindings,
            _binding.OpenCvOptions,
            _binding.GeometryOptions);
    }

    private (string RelativePath, string Sha256) HashAssembly(Assembly assembly)
    {
        string path = assembly.Location;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new EvidenceException("RUNTIME_ASSEMBLY_PATH_MISSING", typeof(InvalidDataException));
        }

        Program.EnsureNoReparsePoints(path, _projectRoot, allowMissing: false);
        return (Path.GetRelativePath(_projectRoot, path).Replace('\\', '/'), HashPath(path));
    }

    private (string RelativePath, string Sha256) HashLoadedOpenCvNativeModule()
    {
        using Process process = Process.GetCurrentProcess();
        ProcessModule? module = process.Modules.Cast<ProcessModule>().FirstOrDefault(static item =>
            string.Equals(Path.GetFileName(item.FileName), "OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase));
        if (module is null || string.IsNullOrWhiteSpace(module.FileName) || !File.Exists(module.FileName))
        {
            throw new EvidenceException("OPEN_CV_NATIVE_MODULE_NOT_FOUND", typeof(InvalidDataException));
        }

        Program.EnsureNoReparsePoints(module.FileName, _projectRoot, allowMissing: false);
        return (Path.GetRelativePath(_projectRoot, module.FileName).Replace('\\', '/'), HashPath(module.FileName));
    }

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out string? value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static bool IsHash(string value) => value is not null && Sha256Pattern.IsMatch(value);

    private static void EnsureHash(byte[] bytes, string expected, string name)
    {
        if (!string.Equals(Hash(bytes), expected, StringComparison.Ordinal))
        {
            throw new EvidenceException("BOUND_HASH_MISMATCH", typeof(InvalidDataException), name);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string HashPath(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsNonFatal(Exception exception) => exception is not
        OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    private sealed class CapturingObserver : IAxisGeometryCandidateObserver
    {
        public IReadOnlyList<AxisGeometryCandidateSnapshot>? Candidates { get; private set; }

        public void OnCandidatesRanked(IReadOnlyList<AxisGeometryCandidateSnapshot> candidates) =>
            Candidates = candidates;
    }

    private sealed class EvidenceException(string code, Type exceptionType, string? detail = null)
        : Exception(detail is null ? code : $"{code}: {detail}")
    {
        public string Code { get; } = code;

        public Type ExceptionType { get; } = exceptionType;
    }
}

public static class AxisCandidateEvidenceReportWriter
{
    private const string OutputRoot = "artifacts/goal22-runs";
    private static readonly System.Text.Json.JsonSerializerOptions ReportJsonOptions = CreateReportJsonOptions();

    public static string WriteNewReport(
        string projectRoot,
        string relativeOutputPath,
        AxisCandidateEvidenceReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeOutputPath);
        ArgumentNullException.ThrowIfNull(report);
        string root = Path.GetFullPath(projectRoot);
        string normalized = relativeOutputPath.Replace('\\', '/');
        string prefix = OutputRoot + "/";
        if (Path.IsPathRooted(relativeOutputPath) ||
            !normalized.StartsWith(prefix, StringComparison.Ordinal) ||
            normalized.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("Output must be a fresh project-relative path under artifacts/goal22-runs.");
        }

        string fullPath = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        Program.EnsureNoReparsePoints(fullPath, root, allowMissing: true);
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null)
        {
            throw new InvalidDataException("Output parent directory is missing.");
        }

        Directory.CreateDirectory(parent);
        Program.EnsureNoReparsePoints(fullPath, root, allowMissing: true);
        byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, ReportJsonOptions);
        using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static System.Text.Json.JsonSerializerOptions CreateReportJsonOptions()
    {
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return options;
    }
}
