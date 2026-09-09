// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Inference;

namespace GraphReader.App.Tests;

[TestClass]
public sealed class ProductionMarkerFrozenCandidateFactoryTests
{
    private static readonly int[] InputShape = [-1, 3, 33, 33];
    private static readonly int[] OutputShape = [-1, 4];
    private static readonly string[] InputChannels = ["ink_probability", "ocr_mask", "artifact_mask"];
    private static readonly string[] ModelFiles = ["candidate.onnx"];

    [TestMethod]
    public void ExactV24ContractCreatesOnlyUnapprovedFrozenCandidate()
    {
        using var directory = new TemporaryDirectory();
        FrozenCandidateMarkerCenterModelDescriptor descriptor = WriteDescriptor(directory.Path);

        ProductionProposalMarkerCenterAdapter adapter =
            ProductionProposalMarkerCenterAdapter.CreateForFrozenCandidateEvaluation(
                descriptor,
                new NoRunInference());

        Assert.IsFalse(adapter.IsApproved);
        Assert.AreEqual(descriptor.Identity, adapter.Model);
    }

    [TestMethod]
    public void ChangedV24PostprocessingContractIsRejected()
    {
        using var directory = new TemporaryDirectory();
        FrozenCandidateMarkerCenterModelDescriptor descriptor = WriteDescriptor(
            directory.Path,
            centerThreshold: 0.26);

        InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            ProductionProposalMarkerCenterAdapter.CreateForFrozenCandidateEvaluation(
                descriptor,
                new NoRunInference()));

        StringAssert.Contains(exception.Message, "center_threshold");
    }

    [TestMethod]
    public void ChangedFrozenPayloadIsRejectedBeforeRunnerConstruction()
    {
        using var directory = new TemporaryDirectory();
        FrozenCandidateMarkerCenterModelDescriptor descriptor = WriteDescriptor(directory.Path);
        File.AppendAllText(descriptor.Identity.FilePath, "tampered");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProductionProposalMarkerCenterAdapter.CreateForFrozenCandidateEvaluation(
                descriptor,
                new NoRunInference()));
    }

    private static FrozenCandidateMarkerCenterModelDescriptor WriteDescriptor(
        string root,
        double centerThreshold = 0.25)
    {
        string modelPath = Path.Combine(root, "candidate.onnx");
        File.WriteAllBytes(modelPath, [1, 2, 3, 4]);
        string modelSha = Hash(modelPath);
        string manifestPath = Path.Combine(root, "candidate.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            model_id = "marker-center-mask-preserving-v24-retry-test",
            model_version = "P1",
            task = "marker_center",
            sha256 = modelSha,
            files = ModelFiles,
            inputs = new[] { new { name = "candidate_patches", element_type = "float32", shape = InputShape } },
            outputs = new[] { new { name = "candidate_predictions", element_type = "float32", shape = OutputShape } },
            preprocessing = new
            {
                architecture = "scale-separated-multiscale-patch-cnn-v16",
                channels = InputChannels,
                patch_size = 33,
                proposal_stride = 4,
                ink_support_window_size = 17,
                ink_support_threshold = 0.11,
            },
            postprocessing = new
            {
                algorithm = "mask_preserving_multiradius_v24",
                center_threshold = centerThreshold,
                offset_scale = 4.0,
                minimum_radius_pixels = 2.5,
                maximum_radius_pixels = 8.0,
                mask_rejection_threshold = 0.35,
                minimum_center_separation_pixels = 5.0,
                radius_suppression_scale = 1.25,
                maximum_decoded_candidates = 100000,
            },
        }));
        return new FrozenCandidateMarkerCenterModelDescriptor(
            new ModelIdentity(
                "marker-center-mask-preserving-v24-retry-test",
                "P1",
                modelSha,
                modelPath),
            manifestPath,
            Hash(manifestPath));
    }

    private static string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class NoRunInference : IProposalMarkerInferenceRunner
    {
        public ValueTask<InferenceResponse> RunAsync(
            InferenceRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Factory validation must not run inference.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"graphreader-marker-frozen-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
