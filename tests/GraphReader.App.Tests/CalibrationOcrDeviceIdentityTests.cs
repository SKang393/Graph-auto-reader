// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using GraphReader.App.Services;
using GraphReader.Inference;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class CalibrationOcrDeviceIdentityTests
{
    [TestMethod]
    public async Task ProfileIdentityBindsRuntimeModelHardwareAndHostWithoutLocalPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "graphreader-profile-identity-" + Guid.NewGuid().ToString("N"));
        string copy = Path.Combine(root, "relocated");
        Directory.CreateDirectory(copy);
        try
        {
            string[] names = ["GraphReader.App.dll", "GraphReader.Ocr.dll", "GraphReader.Inference.dll",
                "Microsoft.ML.OnnxRuntime.dll", "onnxruntime.dll", "en_PP-OCRv5_mobile_rec.onnx", "recognizer.json"];
            foreach (string name in names)
            {
                await File.WriteAllTextAsync(Path.Combine(root, name), name);
                await File.WriteAllTextAsync(Path.Combine(copy, name), name);
            }
            var inventory = new Inventory([new(0, "GPU", 1, 2, 3, false, 4, 5)]);
            string original = await Read(root, inventory, "host1");
            Assert.AreEqual(original, await Read(copy, inventory, "host1"));
            Assert.AreNotEqual(original, await Read(root, inventory, "host2"));
            Assert.AreNotEqual(original, await Read(root, new Inventory([new(0, "GPU", 1, 2, 30, false, 4, 5)]), "host1"));
            foreach (string name in new[] { "onnxruntime.dll", "en_PP-OCRv5_mobile_rec.onnx", "recognizer.json" })
            {
                await File.WriteAllTextAsync(Path.Combine(root, name), "changed");
                Assert.AreNotEqual(original, await Read(root, inventory, "host1"), name);
                await File.WriteAllTextAsync(Path.Combine(root, name), name);
            }
            File.Delete(Path.Combine(root, "onnxruntime.dll"));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => Read(root, inventory, "host1"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task<string> Read(string root, IDxgiAdapterInventory inventory, string host) =>
        CalibrationOcrDeviceIdentity.ReadCoreAsync(root, root, inventory, host, CancellationToken.None);

    private sealed class Inventory(IReadOnlyList<DxgiAdapter> adapters) : IDxgiAdapterInventory
    {
        public IReadOnlyList<DxgiAdapter> Enumerate() => adapters;
    }
}
