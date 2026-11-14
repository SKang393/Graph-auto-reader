// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class StartupArgumentsTests
{
    [TestMethod]
    public void OcrWorkerRequiresAnExclusiveAbsoluteAssetDirectory()
    {
        string assetRoot = Path.GetFullPath("review-assistance/ocr");
        StartupArguments parsed = StartupArguments.Parse(["--calibration-ocr-worker", assetRoot]);
        Assert.AreEqual(assetRoot, parsed.CalibrationOcrAssetsRoot);
        Assert.IsFalse(parsed.PortableSmoke);
        Assert.IsNull(parsed.OpenImagePath);
        Assert.AreEqual("Cpu", parsed.CalibrationOcrDevice!.Provider);
        StartupArguments gpu = StartupArguments.Parse(["--calibration-ocr-worker", assetRoot, "--directml", "2", "-42", "Fixture GPU"]);
        Assert.AreEqual("DirectMl", gpu.CalibrationOcrDevice!.Provider);
        Assert.AreEqual(2, gpu.CalibrationOcrDevice.AdapterIndex);
        Assert.AreEqual(-42L, gpu.CalibrationOcrDevice.AdapterLuid);
        Assert.ThrowsExactly<ArgumentException>(() => StartupArguments.Parse(["--calibration-ocr-worker", assetRoot, "--directml", "-1", "2", "GPU"]));
        Assert.ThrowsExactly<ArgumentException>(() => StartupArguments.Parse(["--calibration-ocr-worker", assetRoot, "--directml", "0", "bad", "GPU"]));
        Assert.ThrowsExactly<ArgumentException>(() => StartupArguments.Parse(["--calibration-ocr-worker", assetRoot, "--directml", "0", "2", "GPU", "--portable-smoke"]));
        Assert.ThrowsExactly<ArgumentException>(() => StartupArguments.Parse(["--calibration-ocr-worker"]));
        Assert.ThrowsExactly<ArgumentException>(() => StartupArguments.Parse(["--calibration-ocr-worker", "relative/assets"]));
        Assert.ThrowsExactly<ArgumentException>(() => StartupArguments.Parse(["--calibration-ocr-worker", assetRoot, "--portable-smoke"]));
        Assert.ThrowsExactly<ArgumentException>(() => StartupArguments.Parse(["--open-image", assetRoot, "--calibration-ocr-worker", assetRoot]));
    }

    [TestMethod]
    public void ParseAcceptsPortableSmokeAndAbsoluteImagePath()
    {
        string imagePath = Path.GetFullPath(Path.Combine("images", "Chandler graph.png"));

        StartupArguments result = StartupArguments.Parse(
            [
                "--portable-smoke",
                "--production-runtime-smoke",
                "--require-packaged-pdfium",
                "--open-image",
                imagePath,
            ]);

        Assert.IsTrue(result.PortableSmoke);
        Assert.IsTrue(result.ProductionRuntimeSmoke);
        Assert.IsTrue(result.RequirePackagedPdfium);
        Assert.AreEqual(imagePath, result.OpenImagePath);
    }

    [TestMethod]
    public void ParseRejectsMissingImagePath()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => StartupArguments.Parse(["--open-image"]));
    }

    [TestMethod]
    public void ParseRejectsDuplicateImageArguments()
    {
        string imagePath = Path.GetFullPath("graph.png");

        Assert.ThrowsExactly<ArgumentException>(
            () => StartupArguments.Parse(
                ["--open-image", imagePath, "--open-image", imagePath]));
    }

    [TestMethod]
    public void ParseRejectsRelativeImagePath()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => StartupArguments.Parse(["--open-image", "graph.png"]));
    }
}
