// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.Axis;
using GraphReader.AxisCandidateEvidence;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace GraphReader.AxisCandidateEvidence.Tests;

[TestClass]
public sealed class PanelBatchEvidenceRunnerTests
{
    private static readonly double[] ExpectedSourceToPanel = [1, 0, -10, 0, 1, -15, 0, 0, 1];
    private static readonly double[] ExpectedPanelToSource = [1, 0, 10, 0, 1, 15, 0, 0, 1];

    [TestMethod]
    public async Task ProductionDecoderBatchPreservesNonidentityCropAndOrderedCandidates()
    {
        using var fixture = new PanelFixture();
        var provider = new StaticProvider(FixtureCandidates());
        AxisCandidatePanelBatchEvidenceRunner runner = fixture.LoadRunner(provider);

        AxisCandidatePanelBatchReport report = await runner.RunAsync();

        Assert.AreEqual("success", report.Status, report.Failure?.Message);
        Assert.AreEqual(1, report.SourceCount);
        Assert.AreEqual(1, report.DetectedSourceCount);
        Assert.AreEqual(0, report.FallbackSourceCount);
        Assert.AreEqual(1, report.PanelCount);
        Assert.AreEqual(1, report.SelectedPanelCount);
        Assert.AreEqual(1, report.PanelResults.Count);
        Assert.AreEqual(1, provider.CallCount);
        Assert.AreEqual(80, provider.LastWidth);
        Assert.AreEqual(70, provider.LastHeight);

        AxisCandidatePanelResult panel = report.PanelResults[0];
        Assert.AreEqual(fixture.SourceId, panel.SourceId);
        Assert.AreEqual(fixture.PanelId, panel.PanelId);
        Assert.AreEqual(fixture.SourceSha256, panel.SourceSha256);
        Assert.AreEqual(fixture.PanelSha256, panel.PanelSha256);
        Assert.AreEqual("detected_panel", panel.PanelOriginKind);
        Assert.AreEqual("panel_original_pixels", panel.CoordinateSpace);
        Assert.AreEqual(10d, panel.EncodedCrop.X);
        Assert.AreEqual(15d, panel.EncodedCrop.Y);
        CollectionAssert.AreEqual(ExpectedSourceToPanel, panel.SourceToPanelMatrix.ToArray());
        CollectionAssert.AreEqual(ExpectedPanelToSource, panel.PanelToSourceMatrix.ToArray());
        Assert.IsTrue(panel.CandidateSnapshots.Count > 0);
        for (int index = 0; index < panel.CandidateSnapshots.Count; index++)
        {
            Assert.AreEqual(index, panel.CandidateSnapshots[index].Rank);
            Assert.IsTrue(index == 0 || panel.CandidateSnapshots[index - 1].Score >= panel.CandidateSnapshots[index].Score);
        }
        Assert.IsNotNull(panel.NormalizedFeatures);
        Assert.AreEqual(panel.CandidateSnapshots.Count, panel.NormalizedFeatures.Rows.Count);
        Assert.IsNotNull(report.Runtime);
        Assert.AreEqual(new OpenCvLineCandidateOptions(), report.Runtime!.OpenCvOptions);
        Assert.AreEqual(new AxisGeometryOptions(), report.Runtime.GeometryOptions);
    }

    [TestMethod]
    public async Task PanelHashMismatchFailsBeforeCandidateProvider()
    {
        using var fixture = new PanelFixture(tamperPanelBytes: true);
        var provider = new StaticProvider(FixtureCandidates());

        AxisCandidatePanelBatchReport report = await fixture.LoadRunner(provider).RunAsync();

        Assert.AreEqual("failed", report.Status);
        Assert.AreEqual("PANEL_IMAGE_HASH_MISMATCH", report.Failure?.Code);
        Assert.AreEqual(0, provider.CallCount);
        Assert.IsEmpty(report.PanelResults);
    }

    [TestMethod]
    public async Task InvalidNonidentityTransformFailsClosed()
    {
        using var fixture = new PanelFixture(invalidTransform: true);

        AxisCandidatePanelBatchReport report = await fixture.LoadRunner(new StaticProvider(FixtureCandidates())).RunAsync();

        Assert.AreEqual("failed", report.Status);
        Assert.AreEqual("PANEL_TRANSFORM_NOT_PRODUCTION_CROP_TRANSLATION", report.Failure?.Code);
        Assert.IsEmpty(report.PanelResults);
    }

    [TestMethod]
    public async Task ChangedOpenCvOptionsAreRejectedBeforeProviderExecution()
    {
        using var fixture = new PanelFixture(nondefaultOpenCvOptions: true);
        var provider = new StaticProvider(FixtureCandidates());

        AxisCandidatePanelBatchReport report = await fixture.LoadRunner(provider).RunAsync();

        Assert.AreEqual("failed", report.Status);
        Assert.AreEqual("NON_PRODUCTION_AXIS_OPTIONS_REJECTED", report.Failure?.Code);
        Assert.AreEqual(0, provider.CallCount);
        Assert.IsEmpty(report.PanelResults);
    }

    [TestMethod]
    public async Task ChangedGeometryOptionsAreRejectedBeforeProviderExecution()
    {
        using var fixture = new PanelFixture(nondefaultGeometryOptions: true);
        var provider = new StaticProvider(FixtureCandidates());

        AxisCandidatePanelBatchReport report = await fixture.LoadRunner(provider).RunAsync();

        Assert.AreEqual("failed", report.Status);
        Assert.AreEqual("NON_PRODUCTION_AXIS_OPTIONS_REJECTED", report.Failure?.Code);
        Assert.AreEqual(0, provider.CallCount);
        Assert.IsEmpty(report.PanelResults);
    }

    [TestMethod]
    public void FixedDevAndTrainAdmissionsUseTheirAuthenticatedRuntimeMaps()
    {
        string projectRoot = PanelFixture.FindProjectRoot(Environment.CurrentDirectory);
        const string devAdmissionRelativePath = "artifacts/goal22-runs/axis-roles-dev-measure-admission-v2/binding.json";
        const string devAdmissionSha256 = "a32b43b05c74ea2279f2a7cd90e53024c5981f356dbf8735adbc8d1653168203";
        const string trainAdmissionRelativePath = "artifacts/goal22-runs/axis-ranker-panelization-admission-v2/binding.json";
        const string trainAdmissionSha256 = "a521b39708c1cfbcb9d37bb2a099e8037f89ff848fca83f218159d20d5b3eb1a";
        const string devRuntimePrefix = "artifacts/goal22-runs/axis-roles-panelization-build-v2/";
        const int expectedDevRuntimeCount = 73;

        AssertLocalFilesPresentOrInconclusive(
            projectRoot,
            [devAdmissionRelativePath, trainAdmissionRelativePath]);
        using JsonDocument devAdmission = ReadPinnedAdmission(projectRoot, devAdmissionRelativePath, devAdmissionSha256);
        using JsonDocument trainAdmission = ReadPinnedAdmission(projectRoot, trainAdmissionRelativePath, trainAdmissionSha256);
        JsonElement devRuntimeFiles = devAdmission.RootElement.GetProperty("runtime_files");
        SortedDictionary<string, string> devRuntimeMap = ReadMap(devRuntimeFiles, devRuntimePrefix);
        Assert.AreEqual(expectedDevRuntimeCount, devRuntimeMap.Count);
        SortedDictionary<string, string> devSourceMap = ReadMap(devAdmission.RootElement.GetProperty("files"));
        SortedDictionary<string, string> trainRuntimeMap = ReadMap(trainAdmission.RootElement.GetProperty("files"));
        Assert.AreEqual(81, trainRuntimeMap.Count);
        AssertLocalFilesPresentOrInconclusive(
            projectRoot,
            devRuntimeMap.Keys.Concat(devSourceMap.Keys).Concat(trainRuntimeMap.Keys)
                .Append(devAdmissionRelativePath).Append(trainAdmissionRelativePath));
        var devRunner = CreateParserRunner(projectRoot, "public_synthetic_development", devAdmissionSha256, devRuntimeMap);
        SortedDictionary<string, string> parsedDevMap = InvokePanelizerRuntimeParser(devRunner, devAdmission.RootElement);
        AssertMapEqual(devRuntimeMap, parsedDevMap);

        SortedDictionary<string, string> malformedDevMap = new(devRuntimeMap, StringComparer.Ordinal)
        {
            [devRuntimeMap.Keys.First()] = new string('0', 64),
        };
        object malformedDevRunner = CreateParserRunner(
            projectRoot, "public_synthetic_development", devAdmissionSha256, malformedDevMap);
        TargetInvocationException devException = Assert.ThrowsExactly<TargetInvocationException>(
            () => InvokePanelizerRuntimeParser(malformedDevRunner, devAdmission.RootElement));
        StringAssert.Contains(devException.InnerException!.Message, "PANELIZER_RUNTIME_INPUT_PIN_MISMATCH");

        var trainRunner = CreateParserRunner(projectRoot, "public_synthetic_train", trainAdmissionSha256, trainRuntimeMap);
        SortedDictionary<string, string> parsedTrainMap = InvokePanelizerRuntimeParser(trainRunner, trainAdmission.RootElement);
        AssertMapEqual(trainRuntimeMap, parsedTrainMap);

        MethodInfo allowlist = typeof(AxisCandidatePanelBatchEvidenceRunner).GetMethod(
            "IsPanelizerRuntimePathAllowed",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("Panelizer runtime allowlist helper was not found.");
        bool IsAllowed(string path, string scope) =>
            (bool)(allowlist.Invoke(null, [path, scope]) ?? throw new AssertFailedException("Allowlist helper returned null."));
        Assert.IsTrue(IsAllowed("artifacts/goal22-tools/axis-roles-dev-measure-v2/job.py", "public_synthetic_train"),
            "The shared training source binding must remain valid for train scope.");
        Assert.IsTrue(IsAllowed("artifacts/goal22-tools/axis-roles-dev-measure-v2/bind.py", "public_synthetic_development"));
        Assert.IsFalse(IsAllowed("artifacts/goal22-tools/axis-roles-dev-measure-v2/bind.py", "public_synthetic_train"));
        Assert.IsFalse(IsAllowed("artifacts/goal22-tools/axis-roles-dev-measure-v2/other.py", "public_synthetic_development"));
        Assert.IsFalse(IsAllowed("artifacts/goal22-tools/axis-roles-dev-measure-v2/../bind.py", "public_synthetic_development"));
    }

    [TestMethod]
    public void FixedDevManifestPassesCompletePreflightWithExactRuntimePathCasing()
    {
        string projectRoot = PanelFixture.FindProjectRoot(Environment.CurrentDirectory);
        const string manifestPath = "artifacts/goal22-runs/axis-candidate-evidence-public-v3-dev-prep-20261031-v5/input-manifest.json";
        const string manifestSha256 = "6c8805a6b547952628eab5e3ecabf1aef467bdf084817d5e6627b8c8b5229222";
        AssertLocalFilesPresentOrInconclusive(projectRoot, [manifestPath]);
        AxisCandidatePanelBatchEvidenceRunner runner = AxisCandidatePanelBatchEvidenceRunner.Load(
            projectRoot,
            manifestPath,
            manifestSha256);

        var toolRuntime = (SortedDictionary<string, string>)InvokePrivate(runner, "ReadActualToolRuntimeBindings")!;
        Assert.AreEqual(5, toolRuntime.Count);
        AxisCandidatePanelBatchBinding binding = (AxisCandidatePanelBatchBinding)(
            typeof(AxisCandidatePanelBatchEvidenceRunner).GetField("_binding", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runner)
            ?? throw new AssertFailedException("Runner binding was not available."));
        string descriptorNativePath = binding.ToolRuntimeBindings.Keys.Single(path => path.Contains("OpenCvSharpExtern", StringComparison.Ordinal));
        string actualNativePath = toolRuntime.Keys.Single(path => path.Contains("OpenCvSharpExtern", StringComparison.Ordinal));
        Assert.IsTrue(descriptorNativePath.EndsWith("OpenCvSharpExtern.DLL", StringComparison.Ordinal),
            $"Descriptor must preserve the loaded-module path spelling, got {descriptorNativePath}");
        Assert.IsTrue(actualNativePath.EndsWith("OpenCvSharpExtern.DLL", StringComparison.Ordinal),
            $"Test host must preserve the loaded-module path spelling, got {actualNativePath}");
        Assert.AreEqual(binding.ToolRuntimeBindings[descriptorNativePath], toolRuntime[actualNativePath],
            "The executable and test host must bind identical native OpenCV bytes.");

        string caseOnlyPath = actualNativePath[..^4] + ".dll";
        var caseChangedRuntime = new SortedDictionary<string, string>(
            toolRuntime.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
        string nativeHash = caseChangedRuntime[actualNativePath];
        caseChangedRuntime.Remove(actualNativePath);
        caseChangedRuntime.Add(caseOnlyPath, nativeHash);
        AxisCandidatePanelBatchEvidenceRunner mismatchedRunner = CreateParserRunner(
            projectRoot,
            binding with { ToolRuntimeBindings = caseChangedRuntime });
        TargetInvocationException casingFailure = Assert.ThrowsExactly<TargetInvocationException>(
            () => InvokePrivate(mismatchedRunner, "VerifyToolRuntimeBindings"));
        StringAssert.Contains(casingFailure.InnerException!.Message, "TOOL_RUNTIME_BINDING_MISMATCH");

        InvokePrivate(runner, "ValidateBinding");
        object context = InvokePrivateWithMissingPrerequisiteSkip(runner, "LoadAndVerifyPanelBatchInputs")
            ?? throw new AssertFailedException("Panel preflight returned no context.");
        Assert.AreEqual(32, ReadContextCount(context, "SourceCount"));
        Assert.AreEqual(48, ReadContextCount(context, "PanelCount"));
        Assert.AreEqual(73, ReadContextMap(context, "PanelizerRuntimeBindings").Count);
        Assert.AreEqual(18, ((SortedDictionary<string, string>)InvokePrivateWithMissingPrerequisiteSkip(runner, "VerifyProductionSourceBindings")!).Count);
        Assert.AreEqual(9, ((SortedDictionary<string, string>)InvokePrivateWithMissingPrerequisiteSkip(runner, "VerifyToolSourceBindings")!).Count);
        InvokePrivateWithMissingPrerequisiteSkip(runner, "VerifyPanelizerRuntimeBindings", context);
        InvokePrivateWithMissingPrerequisiteSkip(runner, "VerifyPanelImageInventory", context);
        AxisCandidatePanelBatchEvidenceRunner actualHostRunner = CreateParserRunner(
            projectRoot,
            binding with { ToolRuntimeBindings = toolRuntime });
        Assert.AreEqual(5, ((SortedDictionary<string, string>)InvokePrivate(actualHostRunner, "VerifyToolRuntimeBindings")!).Count);
    }

    private static JsonDocument ReadPinnedAdmission(string projectRoot, string relativePath, string expectedSha256)
    {
        string fullPath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        byte[] bytes = File.ReadAllBytes(fullPath);
        Assert.AreEqual(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)), $"Fixed admission changed: {relativePath}");
        return JsonDocument.Parse(bytes);
    }

    [TestMethod]
    public void MissingLocalEvidenceIsInconclusiveButPresentFilesAreNotSkipped()
    {
        string projectRoot = PanelFixture.FindProjectRoot(Environment.CurrentDirectory);
        string missing = $"artifacts/goal22-runs/test-only-missing-{Guid.NewGuid():N}/binding.json";
        AssertLocalFilesPresentOrInconclusive(projectRoot, ["AGENTS.md"]);
        Assert.ThrowsExactly<AssertInconclusiveException>(
            () => AssertLocalFilesPresentOrInconclusive(projectRoot, [missing]));
        Assert.ThrowsExactly<AssertFailedException>(
            () => AssertLocalFilesPresentOrInconclusive(projectRoot, ["artifacts"]));
    }

    [TestMethod]
    public void DanglingLocalEvidenceLinkFailsInsteadOfSkippingWhenSupported()
    {
        string projectRoot = PanelFixture.FindProjectRoot(Environment.CurrentDirectory);
        string fixtureId = Guid.NewGuid().ToString("N");
        string fixtureRoot = Path.Combine(projectRoot, "artifacts", "goal22-runs", "axis-candidate-evidence-test-fixtures", fixtureId);
        string expectedParent = Path.Combine(projectRoot, "artifacts", "goal22-runs", "axis-candidate-evidence-test-fixtures");
        Assert.IsTrue(fixtureRoot.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Evidence path test fixture escaped its dedicated fixture root.");
        Directory.CreateDirectory(fixtureRoot);
        string link = Path.Combine(
            "artifacts", "goal22-runs", "axis-candidate-evidence-test-fixtures", fixtureId, "dangling.json");
        string target = Path.Combine(projectRoot, "artifacts", "goal22-runs", "missing-link-target.json");
        string fullLink = Path.Combine(projectRoot, link);
        try
        {
            try
            {
                File.CreateSymbolicLink(fullLink, target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Inconclusive($"Symbolic links are unavailable on this host: {exception.Message}");
            }
            Assert.ThrowsExactly<AssertFailedException>(
                () => AssertLocalFilesPresentOrInconclusive(projectRoot, [link.Replace('\\', '/')]));
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }

    private static void AssertLocalFilesPresentOrInconclusive(string projectRoot, IEnumerable<string> relativePaths)
    {
        string root = Path.GetFullPath(projectRoot);
        var missing = new List<string>();
        foreach (string relative in relativePaths.Distinct(StringComparer.Ordinal))
        {
            string fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            string rootRelative = Path.GetRelativePath(root, fullPath);
            if (Path.IsPathRooted(rootRelative) || rootRelative == ".." ||
                rootRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                Assert.Fail($"Required local evidence path escaped the project root: {relative}");
            }

            string current = root;
            string[] segments = rootRelative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < segments.Length; index++)
            {
                current = Path.Combine(current, segments[index]);
                bool isFinal = index == segments.Length - 1;
                FileSystemInfo info = isFinal ? new FileInfo(current) : new DirectoryInfo(current);
                if (info.LinkTarget is not null)
                {
                    Assert.Fail($"Present local evidence path contains a symbolic link: {relative}");
                }

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (FileNotFoundException)
                {
                    missing.Add(relative);
                    break;
                }
                catch (DirectoryNotFoundException)
                {
                    missing.Add(relative);
                    break;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Assert.Fail($"Present local evidence path contains a reparse point: {relative}");
                }
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((isFinal && isDirectory) || (!isFinal && !isDirectory))
                {
                    Assert.Fail($"Present local evidence path is not a regular file: {relative}");
                }
            }
        }
        if (missing.Count > 0)
        {
            Assert.Inconclusive("Locally materialized Goal 22 evidence is absent: " + string.Join(", ", missing.Take(4)));
        }
    }

    private static object? InvokePrivateWithMissingPrerequisiteSkip(object instance, string name, params object?[] arguments)
    {
        try
        {
            return InvokePrivate(instance, name, arguments);
        }
        catch (TargetInvocationException exception) when (
            exception.InnerException is FileNotFoundException or DirectoryNotFoundException)
        {
            Assert.Inconclusive("Locally materialized Goal 22 evidence referenced by the pinned manifest is absent: " +
                exception.InnerException.Message);
            return null;
        }
    }

    private static SortedDictionary<string, string> ReadMap(JsonElement files, string? prefix = null)
    {
        Assert.AreEqual(JsonValueKind.Object, files.ValueKind);
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in files.EnumerateObject())
        {
            if (prefix is null || property.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                map.Add(property.Name, property.Value.GetString() ?? throw new AssertFailedException("Admission hash is null."));
            }
        }
        return map;
    }

    private static object CreateParserRunner(
        string projectRoot,
        string scope,
        string admissionSha256,
        IReadOnlyDictionary<string, string> runtimeMap)
    {
        var binding = new AxisCandidatePanelBatchBinding(
            "graphreader.axis-candidate-panel-batch-input.v1",
            scope,
            scope == "public_synthetic_train" ? "train" : "dev",
            "admission.json",
            admissionSha256,
            "receipt.json",
            new string('1', 64),
            "run",
            64,
            61,
            3,
            0,
            96,
            Array.Empty<AxisCandidatePanelSelection>(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            runtimeMap,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new OpenCvLineCandidateOptions(),
            new AxisGeometryOptions());
        ConstructorInfo constructor = typeof(AxisCandidatePanelBatchEvidenceRunner).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(string), typeof(string), typeof(string), typeof(AxisCandidatePanelBatchBinding), typeof(ILineCandidateProvider), typeof(IAxisGeometryDetector)],
            modifiers: null)
            ?? throw new AssertFailedException("Private parser test constructor was not found.");
        return constructor.Invoke([projectRoot, "", "", "", binding, null, null]);
    }

    private static SortedDictionary<string, string> InvokePanelizerRuntimeParser(object runner, JsonElement admission)
    {
        MethodInfo parser = typeof(AxisCandidatePanelBatchEvidenceRunner).GetMethod(
            "ReadPanelizerRuntimeBindings", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("Panelizer runtime parser was not found.");
        return (SortedDictionary<string, string>)(parser.Invoke(runner, [admission])
            ?? throw new AssertFailedException("Panelizer runtime parser returned null."));
    }

    private static object? InvokePrivate(object instance, string name, params object?[] arguments)
    {
        MethodInfo method = typeof(AxisCandidatePanelBatchEvidenceRunner).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException($"Private preflight method was not found: {name}");
        return method.Invoke(instance, arguments);
    }

    private static int ReadContextCount(object context, string propertyName)
    {
        object value = context.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(context)
            ?? throw new AssertFailedException($"Preflight context count was missing: {propertyName}");
        return (int)value;
    }

    private static SortedDictionary<string, string> ReadContextMap(object context, string propertyName) =>
        (SortedDictionary<string, string>)(context.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(context)
            ?? throw new AssertFailedException($"Preflight context map was missing: {propertyName}"));

    private static AxisCandidatePanelBatchEvidenceRunner CreateParserRunner(
        string projectRoot,
        AxisCandidatePanelBatchBinding binding)
    {
        ConstructorInfo constructor = typeof(AxisCandidatePanelBatchEvidenceRunner).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(string), typeof(string), typeof(string), typeof(AxisCandidatePanelBatchBinding), typeof(ILineCandidateProvider), typeof(IAxisGeometryDetector)],
            modifiers: null)
            ?? throw new AssertFailedException("Private parser test constructor was not found.");
        return (AxisCandidatePanelBatchEvidenceRunner)constructor.Invoke([projectRoot, "", "", "", binding, null, null]);
    }

    private static void AssertMapEqual(SortedDictionary<string, string> expected, SortedDictionary<string, string> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        foreach ((string key, string value) in expected)
        {
            Assert.IsTrue(actual.TryGetValue(key, out string? actualValue), $"Runtime path missing: {key}");
            Assert.AreEqual(value, actualValue, $"Runtime hash mismatch: {key}");
        }
    }

    [TestMethod]
    public async Task FullImageFallbackRemainsDistinctAndFailedSourceIsAccounted()
    {
        using var fallback = new PanelFixture(fullImageFallback: true);
        AxisCandidatePanelBatchReport fallbackReport = await fallback.LoadRunner(new StaticProvider(FixtureCandidates())).RunAsync();
        Assert.AreEqual("success", fallbackReport.Status, fallbackReport.Failure?.Message);
        Assert.AreEqual(1, fallbackReport.FallbackSourceCount);
        Assert.AreEqual("full_image_review_fallback", fallbackReport.PanelResults.Single().PanelOriginKind);
        Assert.AreEqual("panel_original_pixels", fallbackReport.PanelResults.Single().CoordinateSpace);

        using var failed = new PanelFixture(failedSource: true);
        AxisCandidatePanelBatchReport failedReport = await failed.LoadRunner(new StaticProvider(FixtureCandidates())).RunAsync();
        Assert.AreEqual("success", failedReport.Status, failedReport.Failure?.Message);
        Assert.AreEqual(1, failedReport.FailedSourceCount);
        Assert.AreEqual(0, failedReport.PanelCount);
        Assert.AreEqual(0, failedReport.SelectedPanelCount);
        Assert.IsEmpty(failedReport.PanelResults);
        Assert.AreEqual("failed", failedReport.SourceSummaries.Single().SourceStatus);
    }

    private static IReadOnlyList<GeometryLineCandidate> FixtureCandidates() =>
    [
        new("fixture-x", new GeometryLineSegment(new PixelPoint(10, 55), new PixelPoint(70, 55)), LineCandidateSource.RecordedFixture),
        new("fixture-y", new GeometryLineSegment(new PixelPoint(10, 55), new PixelPoint(10, 5)), LineCandidateSource.RecordedFixture),
        new("fixture-x-alt", new GeometryLineSegment(new PixelPoint(15, 58), new PixelPoint(68, 58)), LineCandidateSource.RecordedFixture),
        new("fixture-y-alt", new GeometryLineSegment(new PixelPoint(15, 58), new PixelPoint(15, 7)), LineCandidateSource.RecordedFixture),
    ];

    private sealed class StaticProvider(IReadOnlyList<GeometryLineCandidate> candidates) : ILineCandidateProvider
    {
        public int CallCount { get; private set; }
        public int LastWidth { get; private set; }
        public int LastHeight { get; private set; }

        public ValueTask<IReadOnlyList<GeometryLineCandidate>> DetectLinesAsync(
            GrayscaleLineCandidateFrame frame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastWidth = frame.Width;
            LastHeight = frame.Height;
            return ValueTask.FromResult(candidates);
        }
    }

    private sealed class PanelFixture : IDisposable
    {
        private static readonly string[] RuntimeFiles =
        [
            "artifacts/goal22-runs/axis-roles-panelization-build-v2/Panelization.deps.json",
            "artifacts/goal22-runs/axis-roles-panelization-build-v2/Panelization.dll",
            "artifacts/goal22-runs/axis-roles-panelization-build-v2/Panelization.runtimeconfig.json",
            "artifacts/goal22-runs/axis-roles-panelization-build-v2/GraphReader.App.dll",
            "artifacts/goal22-runs/axis-roles-panelization-build-v2/GraphReader.Axis.dll",
            "artifacts/goal22-runs/axis-roles-panelization-build-v2/GraphReader.Domain.dll",
            "artifacts/goal22-runs/axis-roles-panelization-build-v2/GraphReader.Imaging.dll",
            "artifacts/goal22-runs/axis-roles-panelization-build-v2/GraphReader.Pdf.dll",
            "artifacts/goal22-runs/axis-roles-panelization-build-v2/OpenCvSharp.dll",
            "artifacts/goal22-runs/axis-roles-panelization-build-v2/runtimes/win-x64/native/OpenCvSharpExtern.dll",
        ];

        private static readonly string[] ProductionSources =
        [
            "artifacts/goal22-tools/axis-roles-production-panelization-v2/Panelization.csproj",
            "artifacts/goal22-tools/axis-roles-production-panelization-v2/Program.cs",
            "src/GraphReader.App/GraphReader.App.csproj",
            "src/GraphReader.App/Integration/Workflow/ProductionRasterFrameDecoder.cs",
            "src/GraphReader.App/Integration/Workflow/ProductionWorkflowDetectionStage.cs",
            "src/GraphReader.App/Integration/Workflow/ProductionWorkflowImportStage.cs",
            "src/GraphReader.App/Integration/Workflow/ProductionWorkflowPanelStore.cs",
            "src/GraphReader.App/Integration/Workflow/ProductionRasterPanelizer.cs",
            "src/GraphReader.App/Integration/Workflow/RasterPanelSourceProvenance.cs",
            "src/GraphReader.App/Integration/Workflow/WorkflowContracts.cs",
            "src/GraphReader.Axis/AxisGeometryContracts.cs",
            "src/GraphReader.Axis/AxisGeometryDetector.cs",
            "src/GraphReader.Axis/GraphReader.Axis.csproj",
            "src/GraphReader.Axis/OpenCvLineCandidateProvider.cs",
            "src/GraphReader.Imaging/GraphReader.Imaging.csproj",
            "src/GraphReader.Imaging/ImageImportService.cs",
            "Directory.Packages.props",
        ];

        private static readonly string[] ToolSources =
        [
            "tools/GraphReader.AxisCandidateEvidence/GraphReader.AxisCandidateEvidence.csproj",
            "tools/GraphReader.AxisCandidateEvidence/EvidenceContracts.cs",
            "tools/GraphReader.AxisCandidateEvidence/EvidenceRunner.cs",
            "tools/GraphReader.AxisCandidateEvidence/FeatureNormalization.cs",
            "tools/GraphReader.AxisCandidateEvidence/GlobalUsings.cs",
            "tools/GraphReader.AxisCandidateEvidence/PanelBatchEvidenceContracts.cs",
            "tools/GraphReader.AxisCandidateEvidence/PanelBatchEvidenceRunner.cs",
            "tools/GraphReader.AxisCandidateEvidence/Program.cs",
            "tools/GraphReader.AxisCandidateEvidence/input-binding.json",
        ];
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly string _fixtureRoot;
        private readonly bool _failedSource;
        private readonly bool _tamperPanelBytes;
        private readonly bool _invalidTransform;
        private readonly bool _fullImageFallback;
        private readonly bool _nondefaultOpenCvOptions;
        private readonly bool _nondefaultGeometryOptions;
        private string _inputManifestPath = string.Empty;
        private string _inputManifestSha = string.Empty;

        public PanelFixture(
            bool tamperPanelBytes = false,
            bool invalidTransform = false,
            bool fullImageFallback = false,
            bool failedSource = false,
            bool nondefaultOpenCvOptions = false,
            bool nondefaultGeometryOptions = false)
        {
            ProjectRoot = FindProjectRoot(Environment.CurrentDirectory);
            AssertLocalFilesPresentOrInconclusive(ProjectRoot, RuntimeFiles);
            Id = Guid.NewGuid().ToString("N");
            _fixtureRoot = Path.Combine(ProjectRoot, "artifacts", "goal22-runs", "axis-candidate-evidence-test-fixtures", Id);
            Directory.CreateDirectory(_fixtureRoot);
            _failedSource = failedSource;
            _tamperPanelBytes = tamperPanelBytes;
            _invalidTransform = invalidTransform;
            _fullImageFallback = fullImageFallback;
            _nondefaultOpenCvOptions = nondefaultOpenCvOptions;
            _nondefaultGeometryOptions = nondefaultGeometryOptions;
            SourceId = Guid.NewGuid().ToString();
            PanelId = Guid.NewGuid().ToString();
            PanelizationRunRoot = $"artifacts/goal22-runs/axis-candidate-evidence-test-fixtures/{Id}/panel-run";

            (byte[] sourceBytes, byte[] panelBytes, AxisCandidatePanelRect crop) = CreateImages(fullImageFallback);
            SourceBytes = sourceBytes;
            PanelBytes = panelBytes;
            Crop = crop;
            SourceSha256 = Hash(sourceBytes);
            PanelSha256 = Hash(panelBytes);

            string sourceRelativePath = $"artifacts/goal22-runs/axis-candidate-evidence-test-fixtures/{Id}/source.png";
            string sourcePath = Path.Combine(ProjectRoot, sourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllBytes(sourcePath, sourceBytes);

            string panelDirectory = Path.Combine(ProjectRoot, PanelizationRunRoot, SourceId, "panelization");
            Directory.CreateDirectory(panelDirectory);
            string panelFileName = $"{SourceId}-{PanelId}.png";
            string panelRelativePath = $"{PanelizationRunRoot}/{SourceId}/panelization/{panelFileName}";
            string panelPath = Path.Combine(ProjectRoot, panelRelativePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllBytes(panelPath, tamperPanelBytes ? MakeTamperedPanel(panelBytes) : panelBytes);

            var panel = new Dictionary<string, object?>
            {
                ["panel_id"] = PanelId,
                ["sha256"] = PanelSha256,
                ["path"] = panelFileName,
                ["width"] = (int)crop.Width,
                ["height"] = (int)crop.Height,
                ["warnings"] = Array.Empty<string>(),
                ["requested_crop"] = crop,
                ["encoded_crop"] = crop,
                ["source_to_panel"] = new[] { 1, 0, -crop.X, 0, 1, -crop.Y, 0, 0, 1 },
                ["panel_to_source"] = _invalidTransform
                    ? new[] { 1, 0, crop.X + 1, 0, 1, crop.Y, 0, 0, 1 }
                    : new[] { 1, 0, crop.X, 0, 1, crop.Y, 0, 0, 1 },
            };

            string reportPath = Path.Combine(panelDirectory, "panelization.json");
            string reportSha = string.Empty;
            if (!failedSource)
            {
                var panelizationReport = new Dictionary<string, object?>
                {
                    ["schema"] = "graphreader.public-axis-roles-production-panelization.v1",
                    ["truth_read"] = false,
                    ["sources"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = SourceId,
                            ["source_sha256"] = SourceSha256,
                            ["status"] = fullImageFallback ? "no_panel_fallback" : "detected",
                            ["warnings"] = Array.Empty<string>(),
                            ["panel_count"] = 1,
                            ["panels"] = new[] { panel },
                        },
                    },
                };
                byte[] reportBytes = Serialize(panelizationReport);
                File.WriteAllBytes(reportPath, reportBytes);
                reportSha = Hash(reportBytes);
            }

            var runtimeMap = RuntimeFiles.ToDictionary(
                static relative => relative,
                relative => Hash(File.ReadAllBytes(Path.Combine(ProjectRoot, relative.Replace('/', Path.DirectorySeparatorChar)))),
                StringComparer.Ordinal);
            var admission = new Dictionary<string, object?>
            {
                ["schema"] = "graphreader.axis-ranker-panelization-binding.v2",
                ["annotations_read"] = false,
                ["dev_pixels_read"] = false,
                ["reserved_read"] = false,
                ["training_admitted"] = false,
                ["optimizer_steps"] = 0,
                ["sources"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = SourceId,
                        ["path"] = sourceRelativePath,
                        ["sha256"] = SourceSha256,
                        ["width"] = fullImageFallback ? 120 : 120,
                        ["height"] = fullImageFallback ? 100 : 100,
                    },
                },
                ["files"] = runtimeMap,
            };
            byte[] admissionBytes = Serialize(admission);
            string admissionRelativePath = $"artifacts/goal22-runs/axis-candidate-evidence-test-fixtures/{Id}/admission.json";
            string admissionPath = Path.Combine(ProjectRoot, admissionRelativePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllBytes(admissionPath, admissionBytes);
            string admissionSha = Hash(admissionBytes);

            var receiptSource = new Dictionary<string, object?>
            {
                ["id"] = SourceId,
                ["source_sha256"] = SourceSha256,
                ["source_status"] = failedSource ? "failed" : fullImageFallback ? "no_panel_fallback" : "detected",
                ["process_status"] = failedSource ? "failed" : "completed",
                ["exit_code"] = failedSource ? 1 : 0,
                ["panel_count"] = failedSource ? 0 : 1,
                ["panelization_sha256"] = failedSource ? null : reportSha,
                ["warnings"] = Array.Empty<string>(),
                ["exception_type"] = failedSource ? "System.IO.IOException" : null,
                ["panels"] = failedSource ? Array.Empty<object>() : new[] { panel },
            };
            var receipt = new Dictionary<string, object?>
            {
                ["schema"] = "graphreader.axis-ranker-training-panelization-receipt.v2",
                ["binding_sha256"] = admissionSha,
                ["truth_read"] = false,
                ["dev_read"] = false,
                ["reserved_read"] = false,
                ["training_admitted"] = false,
                ["optimizer_steps"] = 0,
                ["sources"] = new[] { receiptSource },
            };
            byte[] receiptBytes = Serialize(receipt);
            string receiptRelativePath = $"artifacts/goal22-runs/axis-candidate-evidence-test-fixtures/{Id}/receipt.json";
            File.WriteAllBytes(Path.Combine(ProjectRoot, receiptRelativePath.Replace('/', Path.DirectorySeparatorChar)), receiptBytes);
            string receiptSha = Hash(receiptBytes);

            string toolBin = Path.GetDirectoryName(typeof(AxisCandidatePanelBatchEvidenceRunner).Assembly.Location)!;
            var toolRuntime = new Dictionary<string, string>(StringComparer.Ordinal);
            AddAssembly(toolRuntime, typeof(AxisCandidatePanelBatchEvidenceRunner).Assembly);
            AddAssembly(toolRuntime, typeof(GraphReader.App.Integration.Workflow.ProductionRasterFrameDecoder).Assembly);
            AddAssembly(toolRuntime, typeof(AxisGeometryDetector).Assembly);
            AddAssembly(toolRuntime, typeof(Mat).Assembly);
            AddNativeOpenCv(toolRuntime);

            var manifest = new Dictionary<string, object?>
            {
                ["schema"] = "graphreader.axis-candidate-panel-batch-input.v1",
                ["scope"] = "synthetic_test_fixture",
                ["split"] = "test-fixture",
                ["panelizationAdmissionBindingPath"] = admissionRelativePath,
                ["panelizationAdmissionBindingSha256"] = admissionSha,
                ["panelizationReceiptPath"] = receiptRelativePath,
                ["panelizationReceiptSha256"] = receiptSha,
                ["panelizationRunRoot"] = PanelizationRunRoot,
                ["expectedSourceCount"] = 1,
                ["expectedDetectedSourceCount"] = failedSource || fullImageFallback ? 0 : 1,
                ["expectedFallbackSourceCount"] = fullImageFallback ? 1 : 0,
                ["expectedFailedSourceCount"] = failedSource ? 1 : 0,
                ["expectedPanelCount"] = failedSource ? 0 : 1,
                ["selectedPanels"] = failedSource ? Array.Empty<object>() : new[] { new Dictionary<string, object?> { ["source_id"] = SourceId, ["panel_id"] = PanelId } },
                ["productionSourceBindings"] = HashMap(ProductionSources),
                ["panelizerRuntimeBindings"] = runtimeMap,
                ["toolSourceBindings"] = HashMap(ToolSources),
                ["toolRuntimeBindings"] = toolRuntime,
                ["openCvOptions"] = new Dictionary<string, object?>
                {
                    ["useLineSegmentDetector"] = true,
                    ["useProbabilisticHough"] = true,
                    ["cannyLowThreshold"] = _nondefaultOpenCvOptions ? 39 : 40,
                    ["cannyHighThreshold"] = 120,
                    ["houghThreshold"] = 24,
                    ["houghMinimumLineLengthPixels"] = 12,
                    ["houghMaximumLineGapPixels"] = 4,
                    ["lsdRefinement"] = "RefineStd",
                },
                ["geometryOptions"] = new Dictionary<string, object?>
                {
                    ["maximumAxisDeviationDegrees"] = 15,
                    ["mergeAngleToleranceDegrees"] = 4,
                    ["mergeDistancePixels"] = _nondefaultGeometryOptions ? 3.1 : 3,
                    ["minimumCandidateLengthPixels"] = 1,
                    ["minimumAxisSpanFraction"] = 0.25,
                    ["tickMaximumLengthFraction"] = 0.08,
                    ["tickAxisDistancePixels"] = 4,
                    ["dividerMinimumSpanFraction"] = 0.45,
                    ["dividerMinimumCoverageFraction"] = 0.06,
                    ["dottedDividerMinimumSegments"] = 3,
                    ["dottedDividerMaximumSegmentFraction"] = 0.18,
                    ["plotEdgeExclusionFraction"] = 0.025,
                    ["gridSpacingCoefficientOfVariation"] = 0.12,
                    ["gridMinimumAlignedLines"] = 3,
                    ["needsReviewConfidenceThreshold"] = 0.55,
                },
            };
            byte[] manifestBytes = Serialize(manifest);
            _inputManifestPath = Path.Combine(_fixtureRoot, "input-manifest.json");
            File.WriteAllBytes(_inputManifestPath, manifestBytes);
            _inputManifestSha = Hash(manifestBytes);
        }

        public string ProjectRoot { get; }
        public string Id { get; }
        public string SourceId { get; }
        public string PanelId { get; }
        public string SourceSha256 { get; }
        public string PanelSha256 { get; }
        public byte[] SourceBytes { get; }
        public byte[] PanelBytes { get; }
        public AxisCandidatePanelRect Crop { get; }
        public string PanelizationRunRoot { get; }

        public AxisCandidatePanelBatchEvidenceRunner LoadRunner(ILineCandidateProvider provider) =>
            AxisCandidatePanelBatchEvidenceRunner.Load(
                ProjectRoot,
                Path.GetRelativePath(ProjectRoot, _inputManifestPath).Replace('\\', '/'),
                _inputManifestSha,
                provider);

        public void Dispose()
        {
            string expectedParent = Path.Combine(ProjectRoot, "artifacts", "goal22-runs", "axis-candidate-evidence-test-fixtures");
            if (!_fixtureRoot.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Panel test cleanup path escaped its dedicated fixture root.");
            }
            if (Directory.Exists(_fixtureRoot))
            {
                Directory.Delete(_fixtureRoot, recursive: true);
            }
        }

        private static (byte[] Source, byte[] Panel, AxisCandidatePanelRect Crop) CreateImages(bool fullImageFallback)
        {
            using var source = new Mat(100, 120, MatType.CV_8UC3, Scalar.All(255));
            Cv2.Line(source, new Point(10, 70), new Point(110, 70), Scalar.Black, 2);
            Cv2.Line(source, new Point(10, 70), new Point(10, 10), Scalar.Black, 2);
            Rect crop = fullImageFallback ? new Rect(0, 0, 120, 100) : new Rect(10, 15, 80, 70);
            using var panel = new Mat(source, crop).Clone();
            Cv2.ImEncode(".png", source, out byte[] sourceBytes);
            Cv2.ImEncode(".png", panel, out byte[] panelBytes);
            var rect = new AxisCandidatePanelRect(crop.X, crop.Y, crop.Width, crop.Height, crop.Right, crop.Bottom, crop.Width * crop.Height, true);
            return (sourceBytes, panelBytes, rect);
        }

        private static byte[] MakeTamperedPanel(byte[] bytes)
        {
            using Mat panel = Cv2.ImDecode(bytes, ImreadModes.Color);
            Cv2.Line(panel, new Point(1, 1), new Point(10, 10), Scalar.Red, 2);
            Cv2.ImEncode(".png", panel, out byte[] tampered);
            return tampered;
        }

        private Dictionary<string, string> HashMap(IEnumerable<string> paths) =>
            paths.ToDictionary(
                static relative => relative,
                relative => Hash(File.ReadAllBytes(Path.Combine(ProjectRoot, relative.Replace('/', Path.DirectorySeparatorChar)))),
                StringComparer.Ordinal);

        private void AddAssembly(Dictionary<string, string> hashes, Assembly assembly)
        {
            string path = assembly.Location;
            string relative = Path.GetRelativePath(ProjectRoot, path).Replace('\\', '/');
            hashes[relative] = Hash(File.ReadAllBytes(path));
        }

        private void AddNativeOpenCv(Dictionary<string, string> hashes)
        {
            using Process process = Process.GetCurrentProcess();
            ProcessModule module = process.Modules.Cast<ProcessModule>().FirstOrDefault(static candidate =>
                string.Equals(Path.GetFileName(candidate.FileName), "OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("OpenCV native module was not loaded for test fixture.");
            string relative = Path.GetRelativePath(ProjectRoot, module.FileName).Replace('\\', '/');
            hashes[relative] = Hash(File.ReadAllBytes(module.FileName));
        }

        private static byte[] Serialize(object value) =>
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        public static string FindProjectRoot(string start)
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
            throw new DirectoryNotFoundException("Could not locate the project root for the batch fixture.");
        }
    }
}
