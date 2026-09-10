// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Reflection;
using System.Security.Cryptography;
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

        Assert.HasCount(4, snapshot.Files);
        Assert.HasCount(2, snapshot.Files.Where(file =>
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

    private sealed record RuntimeFixture(
        string Root,
        IReadOnlyList<ProductionOriginalDbRuntimeFileDescriptor> Managed,
        IReadOnlyList<ProductionOriginalDbRuntimeFileDescriptor> Native)
    {
        internal static RuntimeFixture Create()
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            string[] managedNames = ["Microsoft.ML.OnnxRuntime.dll", "OpenCvSharp.dll"];
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
