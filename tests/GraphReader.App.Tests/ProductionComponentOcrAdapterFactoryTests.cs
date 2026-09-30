// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;
using GraphReader.Ocr;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionComponentOcrAdapterFactoryTests
{
    private static readonly string[] ExpectedModelTokens = ["1", "\U0001F1E9\U0001F1EA", "A", " "];

    [TestMethod]
    public void ExactModelTokensPreserveMultiScalarDictionaryEntries()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string path = WriteOfficialRecognitionManifest(root);
            JsonNode manifest = JsonNode.Parse(File.ReadAllText(path))!;
            manifest["outputs"]![0]!["alphabet"] = "1\U0001F1E9\U0001F1EAA ";
            manifest["outputs"]![0]!["alphabet_tokens"] = new JsonArray("1", "\U0001F1E9\U0001F1EA", "A", " ");
            File.WriteAllText(path, manifest.ToJsonString());
            var identity = new ModelIdentity("token-reader", "1", new string('c', 64), Path.Combine(root, "model.onnx"));
            var result = ProductionOcrAdapter.ReadRecognitionOptions(identity, path);
            CollectionAssert.AreEqual(ExpectedModelTokens, result.Recognizer.AlphabetTokens!.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("[\"a\",\"\",\"b \"]")]
    [DataRow("[\"b\",\"a\",\" \"]")]
    [DataRow("[\"ab \",null]")]
    [DataRow("[]")]
    [DataRow("null")]
    public void MalformedOrReorderedModelTokenDeclarationsFailClosed(string tokens)
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string path = WriteOfficialRecognitionManifest(root);
            JsonNode manifest = JsonNode.Parse(File.ReadAllText(path))!;
            manifest["outputs"]![0]!["alphabet_tokens"] = JsonNode.Parse(tokens);
            File.WriteAllText(path, manifest.ToJsonString());
            var identity = new ModelIdentity("token-reader", "1", new string('c', 64), Path.Combine(root, "model.onnx"));
            Assert.ThrowsExactly<InvalidDataException>(() => ProductionOcrAdapter.ReadRecognitionOptions(identity, path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ExactComponentManifestProducesReviewedRuntimeOptions()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string modelPath = Path.Combine(root, "component.onnx");
            File.WriteAllBytes(modelPath, [0x01]);
            string manifestPath = WriteManifest(root);
            var identity = new ModelIdentity(
                "graph-numeric-component-ensemble-v5",
                "0.0.21-p1",
                new string('a', 64),
                modelPath);

            (LocalOnnxComponentTextRecognizerOptions recognizer, OcrPipelineOptions pipeline) =
                ProductionComponentOcrAdapterFactory.ReadRecognitionOptions(identity, manifestPath);

            Assert.AreEqual("0123456789.-%", recognizer.Alphabet);
            Assert.AreEqual(128, recognizer.CanvasWidth);
            Assert.AreEqual(32, recognizer.CanvasHeight);
            Assert.AreEqual(20, recognizer.GlyphWidth);
            Assert.AreEqual(24, recognizer.GlyphHeight);
            Assert.AreEqual(6, recognizer.GeometryFeatureCount);
            Assert.AreEqual(8, recognizer.MaximumGlyphs);
            Assert.AreEqual(13, recognizer.RejectClassIndex);
            Assert.AreEqual(0.65f, recognizer.ConfidenceThreshold);
            Assert.AreEqual(0.75f, recognizer.StructuralRejectMinimumHeightRatio);
            Assert.AreEqual("glyphs", recognizer.InputName);
            Assert.AreEqual("logits", recognizer.OutputName);
            CollectionAssert.AreEqual(
                new[] { InferenceProvider.Cpu },
                recognizer.AllowedProviders!.ToArray());
            Assert.AreEqual(128, pipeline.CropWidth);
            Assert.AreEqual(32, pipeline.CropHeight);
            Assert.AreEqual(1d, pipeline.CropPaddingPixels);
            Assert.AreEqual(0.25d, pipeline.CropVerticalContentPaddingRatio);
            Assert.AreEqual(OcrCropResizeMode.PreserveAspectRatioPad, pipeline.CropResizeMode);
            Assert.AreEqual(1f, pipeline.CropPaddingValue);
            Assert.IsTrue(
                ProductionComponentOcrAdapterFactory.UsesComponentEnsembleManifest(manifestPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ComponentManifestRejectsAnyChangedReviewedThreshold()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string modelPath = Path.Combine(root, "component.onnx");
            File.WriteAllBytes(modelPath, [0x01]);
            string manifestPath = WriteManifest(root, confidenceThreshold: 0.64f);
            var identity = new ModelIdentity(
                "graph-numeric-component-ensemble-v5",
                "0.0.21-p1",
                new string('a', 64),
                modelPath);

            InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                ProductionComponentOcrAdapterFactory.ReadRecognitionOptions(identity, manifestPath));

            StringAssert.Contains(exception.Message, "confidence_threshold");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ComponentManifestRejectsChangedCompositionPadding()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string modelPath = Path.Combine(root, "component.onnx");
            File.WriteAllBytes(modelPath, [0x01]);
            string manifestPath = WriteManifest(root, cropVerticalContentPaddingRatio: 0.20d);
            var identity = new ModelIdentity(
                "graph-numeric-component-ensemble-v5",
                "0.0.21-p1",
                new string('a', 64),
                modelPath);

            InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                ProductionComponentOcrAdapterFactory.ReadRecognitionOptions(identity, manifestPath));

            StringAssert.Contains(exception.Message, "crop_vertical_content_padding_ratio");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void LegacyCtcManifestDoesNotSelectComponentFactory()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string manifestPath = Path.Combine(root, "manifest.json");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["postprocessing"] = new Dictionary<string, object?>
                    {
                        ["algorithm"] = "ctc_greedy_alternatives_v1",
                    },
                }));

            Assert.IsFalse(
                ProductionComponentOcrAdapterFactory.UsesComponentEnsembleManifest(manifestPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ExactDetectionManifestProducesReviewedDbRuntimeOptions()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string modelPath = Path.Combine(root, "detector.onnx");
            File.WriteAllBytes(modelPath, [0x01]);
            string manifestPath = WriteDetectionManifest(root);
            var identity = new ModelIdentity(
                "pp-ocrv5-mobile-det",
                "0.0.21-converted",
                new string('b', 64),
                modelPath);

            LocalOnnxTextRegionDetectorOptions options =
                ProductionComponentOcrAdapterFactory.ReadDetectionOptions(identity, manifestPath);

            Assert.AreEqual(960, options.MaximumSideLength);
            Assert.AreEqual(128, options.DimensionMultiple);
            Assert.AreEqual(OcrTensorColorMode.Bgr, options.InputColorMode);
            Assert.AreEqual(OcrDetectionPostprocessAlgorithm.DbPostprocessV1, options.PostprocessAlgorithm);
            Assert.AreEqual(OcrDbScoreMode.FastMiniBox, options.DbScoreMode);
            Assert.AreEqual(0.30f, options.ProbabilityThreshold);
            Assert.AreEqual(0.60f, options.BoxConfidenceThreshold);
            Assert.AreEqual(1.5, options.UnclipRatio);
            Assert.AreEqual(3, options.MinimumSideLength);
            Assert.AreEqual(1000, options.MaximumRegions);
            CollectionAssert.AreEqual(
                new[] { InferenceProvider.Cpu },
                options.AllowedProviders!.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DetectionManifestCanSelectFixedProbabilityParityTolerance()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string modelPath = Path.Combine(root, "detector.onnx");
            File.WriteAllBytes(modelPath, [0x01]);
            string manifestPath = WriteDetectionManifest(
                root,
                activation: "probability_with_1e-5_clamp");
            var identity = new ModelIdentity(
                "pp-ocrv5-mobile-det",
                "0.0.21-converted",
                new string('b', 64),
                modelPath);

            LocalOnnxTextRegionDetectorOptions options =
                ProductionComponentOcrAdapterFactory.ReadDetectionOptions(identity, manifestPath);

            Assert.AreEqual(
                OcrDetectionOutputActivation.ProbabilityWithParityTolerance,
                options.OutputActivation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DetectionManifestRejectsAnyChangedReviewedDbThreshold()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string modelPath = Path.Combine(root, "detector.onnx");
            File.WriteAllBytes(modelPath, [0x01]);
            string manifestPath = WriteDetectionManifest(root, boxConfidenceThreshold: 0.59f);
            var identity = new ModelIdentity(
                "pp-ocrv5-mobile-det",
                "0.0.21-converted",
                new string('b', 64),
                modelPath);

            InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                ProductionComponentOcrAdapterFactory.ReadDetectionOptions(identity, manifestPath));

            StringAssert.Contains(exception.Message, "box_confidence_threshold");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OfficialDynamicRecognitionManifestProducesBoundedPaddleWidthOptions()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string modelPath = Path.Combine(root, "recognizer.onnx");
            File.WriteAllBytes(modelPath, [0x01]);
            string manifestPath = WriteOfficialRecognitionManifest(root);
            var identity = new ModelIdentity(
                "pp-ocrv5-mobile-rec",
                "0.0.21-converted",
                new string('c', 64),
                modelPath);

            (LocalOnnxTextRecognizerOptions recognizer, OcrPipelineOptions pipeline) =
                ProductionOcrAdapter.ReadRecognitionOptions(identity, manifestPath);

            Assert.AreEqual(320, recognizer.InputWidth);
            Assert.AreEqual(4096, recognizer.MaximumInputWidth);
            Assert.IsTrue(recognizer.DynamicInputWidth);
            Assert.IsNull(recognizer.ExpectedTimeSteps);
            Assert.AreEqual(OcrTensorColorMode.Bgr, recognizer.InputColorMode);
            Assert.AreEqual(OcrCropWidthMode.PaddleBatchMaximumAspectRatio, pipeline.CropWidthMode);
            Assert.AreEqual(320, pipeline.CropWidth);
            Assert.AreEqual(4096, pipeline.MaximumCropWidth);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OfficialDynamicRecognitionManifestRejectsChangedMemoryBound()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string modelPath = Path.Combine(root, "recognizer.onnx");
            File.WriteAllBytes(modelPath, [0x01]);
            string manifestPath = WriteOfficialRecognitionManifest(root, maximumWidth: 4095);
            var identity = new ModelIdentity(
                "pp-ocrv5-mobile-rec",
                "0.0.21-converted",
                new string('c', 64),
                modelPath);

            InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                ProductionOcrAdapter.ReadRecognitionOptions(identity, manifestPath));

            StringAssert.Contains(exception.Message, "maximum_width");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OfficialSpacingV2ManifestBindsEveryFrozenSourceRule()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string manifestPath = WriteOfficialRecognitionManifest(
                root,
                postprocessingAlgorithm: ProductionOcrAdapter.OfficialRecognitionSpacingV2Algorithm);
            var identity = new ModelIdentity(
                "en-ppocrv5-mobile-rec-spacing-v2",
                "0.1.0",
                new string('c', 64),
                Path.Combine(root, "recognizer.onnx"));

            _ = ProductionOcrAdapter.ReadRecognitionOptions(identity, manifestPath);

            Assert.IsTrue(
                ProductionOcrAdapter.UsesOfficialRecognitionSpacingV2Manifest(manifestPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OfficialSpacingV2ManifestRejectsRuleDrift()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string manifestPath = WriteOfficialRecognitionManifest(
                root,
                postprocessingAlgorithm: ProductionOcrAdapter.OfficialRecognitionSpacingV2Algorithm,
                minimumGapPixels: 5);
            var identity = new ModelIdentity(
                "en-ppocrv5-mobile-rec-spacing-v2",
                "0.1.0",
                new string('c', 64),
                Path.Combine(root, "recognizer.onnx"));

            InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                ProductionOcrAdapter.ReadRecognitionOptions(identity, manifestPath));

            StringAssert.Contains(exception.Message, "minimum_gap_pixels");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string WriteManifest(
        string root,
        float confidenceThreshold = 0.65f,
        double cropVerticalContentPaddingRatio = 0.25d)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["inputs"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "glyphs",
                    ["element_type"] = "float32",
                    ["layout"] = "NCHW",
                    ["shape"] = new object[] { "glyph_count", 1, 24, 26 },
                    ["channels"] = new[] { "grayscale" },
                },
            },
            ["outputs"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "logits",
                    ["element_type"] = "float32",
                    ["layout"] = "NC",
                    ["shape"] = new object[] { "glyph_count", 14 },
                    ["alphabet"] = "0123456789.-%",
                    ["reject_class_index"] = 13,
                },
            },
            ["preprocessing"] = new Dictionary<string, object?>
            {
                ["algorithm"] = "component_glyph_encoding_v1",
                ["canvas_width"] = 128,
                ["canvas_height"] = 32,
                ["glyph_width"] = 20,
                ["glyph_height"] = 24,
                ["geometry_feature_count"] = 6,
                ["resampling"] = "half_pixel_bilinear_v1",
                ["crop_resize_mode"] = "preserve_aspect_ratio_pad",
                ["crop_padding_pixels"] = 1d,
                ["crop_vertical_content_padding_ratio"] = cropVerticalContentPaddingRatio,
                ["crop_padding_value"] = 1f,
                ["geometry_features"] = new[]
                {
                    "height_over_canvas_height",
                    "width_over_canvas_height",
                    "vertical_center_over_canvas_height_minus_one",
                    "foreground_over_component_area",
                    "mean_foreground_intensity",
                    "width_over_height",
                },
            },
            ["postprocessing"] = new Dictionary<string, object?>
            {
                ["algorithm"] = "component_ensemble_numeric_v1",
                ["maximum_glyphs"] = 8,
                ["confidence_threshold"] = confidenceThreshold,
                ["structural_reject_minimum_height_ratio"] = 0.75f,
                ["grammar"] = "graph_numeric_v1",
            },
        };
        string path = Path.Combine(root, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        return path;
    }

    private static string WriteDetectionManifest(
        string root,
        float boxConfidenceThreshold = 0.60f,
        string activation = "probability")
    {
        var manifest = new Dictionary<string, object?>
        {
            ["inputs"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "x",
                    ["element_type"] = "float32",
                    ["layout"] = "NCHW",
                    ["shape"] = new object[] { 1, 3, "H", "W" },
                    ["channels"] = new[] { "b", "g", "r" },
                },
            },
            ["outputs"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "fetch_name_0",
                    ["element_type"] = "float32",
                    ["layout"] = "NCHW",
                    ["shape"] = new object[] { 1, 1, "H", "W" },
                    ["channels"] = new[] { "text_probability" },
                    ["activation"] = activation,
                },
            },
            ["preprocessing"] = new Dictionary<string, object?>
            {
                ["channel_order"] = "BGR",
                ["channel_means"] = new[] { 0.485f, 0.456f, 0.406f },
                ["channel_scales"] = new[] { 1f / 0.229f, 1f / 0.224f, 1f / 0.225f },
                ["maximum_side_length"] = 960,
                ["dimension_multiple"] = 128,
            },
            ["postprocessing"] = new Dictionary<string, object?>
            {
                ["algorithm"] = "db_postprocess_v1",
                ["score_mode"] = "fast",
                ["probability_threshold"] = 0.30f,
                ["box_confidence_threshold"] = boxConfidenceThreshold,
                ["unclip_ratio"] = 1.5,
                ["minimum_side_length"] = 3,
                ["maximum_regions"] = 1000,
            },
        };
        string path = Path.Combine(root, "detection-manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        return path;
    }

    private static string WriteOfficialRecognitionManifest(
        string root,
        int maximumWidth = 4096,
        string postprocessingAlgorithm = "ctc_greedy_alternatives_v1",
        int minimumGapPixels = 4)
    {
        var postprocessing = new Dictionary<string, object?>
        {
            ["algorithm"] = postprocessingAlgorithm,
            ["maximum_alternatives"] = 3,
        };
        if (string.Equals(
                postprocessingAlgorithm,
                ProductionOcrAdapter.OfficialRecognitionSpacingV2Algorithm,
                StringComparison.Ordinal))
        {
            postprocessing["minimum_gap_pixels"] = minimumGapPixels;
            postprocessing["minimum_gap_to_ink_height_ratio"] = 0.25;
            postprocessing["minimum_source_groups"] = 3;
            postprocessing["foreground_contrast_fraction"] = 0.30;
            postprocessing["minimum_foreground_contrast"] = 10.0;
            postprocessing["capital_i_minimum_width_height_ratio"] = 0.25;
            postprocessing["capital_i_minimum_top_coverage"] = 0.75;
            postprocessing["capital_i_minimum_bottom_coverage"] = 0.75;
            postprocessing["maximum_character_count"] = 128;
            postprocessing["maximum_source_groups"] = 32;
        }
        var manifest = new Dictionary<string, object?>
        {
            ["inputs"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "x",
                    ["element_type"] = "float32",
                    ["layout"] = "NCHW",
                    ["shape"] = new object[] { "N", 3, 48, "W" },
                    ["channels"] = new[] { "b", "g", "r" },
                },
            },
            ["outputs"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "fetch_name_0",
                    ["element_type"] = "float32",
                    ["layout"] = "NTC",
                    ["shape"] = new object[] { "N", "T", "C" },
                    ["alphabet"] = "ab ",
                    ["blank_class_index"] = 0,
                },
            },
            ["preprocessing"] = new Dictionary<string, object?>
            {
                ["channel_order"] = "BGR",
                ["channel_means"] = new[] { 0.5f, 0.5f, 0.5f },
                ["channel_scales"] = new[] { 2f, 2f, 2f },
                ["width_policy"] = "paddle_batch_max_wh_ratio_v1",
                ["minimum_width"] = 320,
                ["maximum_width"] = maximumWidth,
            },
            ["postprocessing"] = postprocessing,
        };
        string path = Path.Combine(root, "recognition-manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        return path;
    }

    private static string CreateTemporaryDirectory()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "GraphReader.ProductionComponentOcrAdapterFactory",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
