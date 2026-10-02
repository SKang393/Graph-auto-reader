// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionOriginalDbRuntimeFilesTests
{
    [TestMethod]
    public void ExactDeploymentFilesBindActualRuntimeFiles()
    {
        RuntimeFixture fixture = RuntimeFixture.Create();

        ProductionOriginalDbRuntimeSnapshot snapshot = ProductionOriginalDbRuntimeFiles.Validate(
            fixture.Root,
            fixture.Managed,
            fixture.Native);

        Assert.HasCount(16, snapshot.Files);
        Assert.HasCount(14, snapshot.Files.Where(file =>
            file.BindingKind == ProductionOriginalDbRuntimeBindingKind.LoadedManagedAssembly));
        Assert.IsTrue(snapshot.Files.All(file => string.Equals(
            Path.GetDirectoryName(file.FullPath), fixture.Root, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void UnloadedNativeDependenciesAreReportedAsPackageBindings()
    {
        RuntimeFixture fixture = RuntimeFixture.Create();

        ProductionOriginalDbRuntimeSnapshot snapshot = ProductionOriginalDbRuntimeFiles.ValidateForTest(
            fixture.Root,
            fixture.Managed,
            fixture.Native,
            _ => null);

        Assert.HasCount(2, snapshot.Files.Where(file =>
            file.BindingKind == ProductionOriginalDbRuntimeBindingKind.PackagedNativeDependency));
    }

    [TestMethod]
    public void LoadedNativeModuleMustBeTheRuntimeRootFile()
    {
        RuntimeFixture fixture = RuntimeFixture.Create();
        string foreignPath = Path.Combine(Path.GetTempPath(), "onnxruntime.dll");

        Assert.ThrowsExactly<InvalidDataException>(() => ProductionOriginalDbRuntimeFiles.ValidateForTest(
            fixture.Root,
            fixture.Managed,
            fixture.Native,
            name => string.Equals(name, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase)
                ? foreignPath
                : null));

        ProductionOriginalDbRuntimeSnapshot snapshot = ProductionOriginalDbRuntimeFiles.ValidateForTest(
            fixture.Root,
            fixture.Managed,
            fixture.Native,
            name => string.Equals(name, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(fixture.Root, name)
                : null);
        Assert.AreEqual(
            ProductionOriginalDbRuntimeBindingKind.LoadedNativeModule,
            snapshot.Files.Single(file => file.FileName == "onnxruntime.dll").BindingKind);
    }

    [TestMethod]
    public void MissingDuplicatedAndChangedDescriptorsFailClosed()
    {
        RuntimeFixture fixture = RuntimeFixture.Create();
        Assert.ThrowsExactly<InvalidDataException>(() => ProductionOriginalDbRuntimeFiles.ValidateForTest(
            fixture.Root,
            fixture.Managed.Where(file => file.FileName != "OpenCvSharp.dll").ToArray(),
            fixture.Native,
            _ => null));

        Assert.ThrowsExactly<InvalidDataException>(() => ProductionOriginalDbRuntimeFiles.ValidateForTest(
            fixture.Root,
            fixture.Managed,
            [.. fixture.Native, fixture.Native[0]],
            _ => null));

        ProductionOriginalDbRuntimeFileDescriptor[] changed =
        [
            fixture.Native[0] with { Sha256 = new string('0', 64) },
            fixture.Native[1],
        ];
        Assert.ThrowsExactly<InvalidDataException>(() => ProductionOriginalDbRuntimeFiles.ValidateForTest(
            fixture.Root,
            fixture.Managed,
            changed,
            _ => null));
    }

    [TestMethod]
    public void ConvenientCopyCannotSubstituteForTheLoadedManagedAssembly()
    {
        RuntimeFixture fixture = RuntimeFixture.Create();
        string otherRoot = Path.Combine(Path.GetTempPath(), "GraphReader.RuntimeIdentity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherRoot);
        try
        {
            Assert.ThrowsExactly<InvalidDataException>(() => ProductionOriginalDbRuntimeFiles.ValidateForTest(
                otherRoot,
                fixture.Managed,
                fixture.Native,
                _ => null));
        }
        finally
        {
            Directory.Delete(otherRoot);
        }
    }

    [TestMethod]
    [DataRow("GraphReader.App.dll")]
    [DataRow("GraphReader.Axis.dll")]
    [DataRow("GraphReader.Domain.dll")]
    [DataRow("GraphReader.Export.dll")]
    [DataRow("GraphReader.Imaging.dll")]
    [DataRow("GraphReader.Inference.dll")]
    [DataRow("GraphReader.Legends.dll")]
    [DataRow("GraphReader.Markers.dll")]
    [DataRow("GraphReader.Ocr.dll")]
    [DataRow("GraphReader.Pdf.dll")]
    [DataRow("GraphReader.Phases.dll")]
    [DataRow("GraphReader.SuperResolution.dll")]
    public void WholeWorkflowGateRejectsEachMissingDuplicatedOrChangedProjectAssembly(string fileName)
    {
        RuntimeFixture fixture = RuntimeFixture.Create();
        byte[] Candidate(IEnumerable<ProductionOriginalDbRuntimeFileDescriptor> files) =>
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                managed_files = files.Select(file => new { file = file.FileName, sha256 = file.Sha256 }),
                native_files = fixture.Native.Select(file => new { file = file.FileName, sha256 = file.Sha256 }),
            });

        ProductionOriginalDbOcrApprovalGate.ValidateEvaluatedRuntimeDependencies(Candidate(fixture.Managed));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProductionOriginalDbOcrApprovalGate.ValidateEvaluatedRuntimeDependencies(
                Candidate(fixture.Managed.Where(file => file.FileName != fileName))));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProductionOriginalDbOcrApprovalGate.ValidateEvaluatedRuntimeDependencies(
                Candidate(fixture.Managed.Append(fixture.Managed.Single(file => file.FileName == fileName)))));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProductionOriginalDbOcrApprovalGate.ValidateEvaluatedRuntimeDependencies(
                Candidate(fixture.Managed.Select(file => file.FileName == fileName
                    ? file with { Sha256 = new string('0', 64) } : file))));
    }

    private sealed record RuntimeFixture(
        string Root,
        IReadOnlyList<ProductionOriginalDbRuntimeFileDescriptor> Managed,
        IReadOnlyList<ProductionOriginalDbRuntimeFileDescriptor> Native)
    {
        internal static RuntimeFixture Create()
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            string[] managedNames =
            [
                "GraphReader.App.dll", "GraphReader.Axis.dll", "GraphReader.Domain.dll",
                "GraphReader.Export.dll", "GraphReader.Imaging.dll", "GraphReader.Inference.dll",
                "GraphReader.Legends.dll", "GraphReader.Markers.dll", "GraphReader.Ocr.dll",
                "GraphReader.Pdf.dll", "GraphReader.Phases.dll", "GraphReader.SuperResolution.dll",
                "Microsoft.ML.OnnxRuntime.dll", "OpenCvSharp.dll",
            ];
            string[] nativeNames = ["onnxruntime.dll", "onnxruntime_providers_shared.dll"];
            foreach (string name in managedNames)
            {
                _ = Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(name)));
            }

            return new RuntimeFixture(
                root,
                managedNames.Select(name => Descriptor(Path.Combine(root, name))).ToArray(),
                nativeNames
                    .Select(name => Descriptor(Path.Combine(root, name))).ToArray());
        }

        private static ProductionOriginalDbRuntimeFileDescriptor Descriptor(string path) =>
            new(Path.GetFileName(path), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }
}
