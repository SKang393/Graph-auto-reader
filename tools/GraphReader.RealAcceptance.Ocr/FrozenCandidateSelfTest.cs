// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GraphReader.RealAcceptance.Ocr;

internal static class FrozenCandidateSelfTest
{
    internal static void Run(string repositoryRoot)
    {
        string artifactsRoot = Path.Combine(repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactsRoot);
        string root = Path.Combine(artifactsRoot, $"frozen-candidate-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string writePath = Path.Combine(root, "write-new.bin");
            byte[] writeBytes = [1, 3, 5, 7, 9];
            FrozenCandidateSyntheticRunner.WriteNew(writePath, writeBytes);
            if (!File.ReadAllBytes(writePath).AsSpan().SequenceEqual(writeBytes))
            {
                throw new InvalidOperationException("FROZEN_CANDIDATE_WRITE_NEW_SELF_TEST_FAILED");
            }

            string sourcePath = Path.Combine(root, "source.png");
            byte[] sourceBytes = CreatePng(0);
            File.WriteAllBytes(sourcePath, sourceBytes);
            string sourceSha = FrozenCandidateBinding.Hash(sourceBytes);
            string relativeSource = Path.GetRelativePath(repositoryRoot, sourcePath).Replace('\\', '/');
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = FrozenCandidateSyntheticRunner.InputSchema,
                protocol_sha256 = new string('a', 64),
                split = "synthetic",
                sources = new[]
                {
                    new { relative_path = relativeSource, sha256 = sourceSha, width = 2, height = 2 },
                },
            });
            string manifestPath = Path.Combine(root, "input.json");
            File.WriteAllBytes(manifestPath, manifestBytes);
            FrozenSyntheticInput loaded = FrozenCandidateSyntheticRunner.LoadInput(
                repositoryRoot,
                manifestPath,
                FrozenCandidateBinding.Hash(manifestBytes),
                CancellationToken.None);
            if (loaded.Sources.Count != 1 || loaded.Sources[0].Width != 2 ||
                !loaded.Sources[0].Bytes.AsSpan().SequenceEqual(sourceBytes))
            {
                throw new InvalidOperationException("FROZEN_CANDIDATE_INPUT_SELF_TEST_FAILED");
            }

            sourceBytes[^1] ^= 1;
            File.WriteAllBytes(sourcePath, sourceBytes);
            bool tamperRejected = false;
            try
            {
                _ = FrozenCandidateSyntheticRunner.LoadInput(
                    repositoryRoot,
                    manifestPath,
                    FrozenCandidateBinding.Hash(manifestBytes),
                    CancellationToken.None);
            }
            catch (InvalidDataException exception) when (
                exception.Message.Contains("source checksum mismatch", StringComparison.Ordinal))
            {
                tamperRejected = true;
            }
            if (!tamperRejected)
            {
                throw new InvalidOperationException("FROZEN_CANDIDATE_INPUT_SELF_TEST_ACCEPTED_TAMPERED_SOURCE");
            }

            ValidateProtocolSourceBinding(repositoryRoot, root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ValidateProtocolSourceBinding(string repositoryRoot, string root)
    {
        string trainRoot = Path.Combine(root, "train");
        string devRoot = Path.Combine(root, "dev");
        Directory.CreateDirectory(trainRoot);
        Directory.CreateDirectory(devRoot);
        SourceFixture[] train =
        [
            WriteSource(repositoryRoot, trainRoot, "train-a.png", "train", 11, 16),
            WriteSource(repositoryRoot, trainRoot, "train-b.png", "train", 12, 32),
        ];
        SourceFixture[] dev =
        [
            WriteSource(repositoryRoot, devRoot, "dev-a.png", "validation", 21, 48),
        ];
        string trainManifestPath = Path.Combine(trainRoot, "input-manifest.json");
        string devManifestPath = Path.Combine(devRoot, "input-manifest.json");
        byte[] trainManifest = SourceManifest("train", 10, train);
        byte[] devManifest = SourceManifest("validation", 20, dev);
        File.WriteAllBytes(trainManifestPath, trainManifest);
        File.WriteAllBytes(devManifestPath, devManifest);
        string relativeTrainManifest = Path.GetRelativePath(repositoryRoot, trainManifestPath).Replace('\\', '/');
        string relativeDevManifest = Path.GetRelativePath(repositoryRoot, devManifestPath).Replace('\\', '/');
        byte[] protocolBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "graphreader.frozen-workflow-synthetic-diagnostic-protocol.v1",
            input_identity = new
            {
                train_manifest = relativeTrainManifest,
                train_manifest_sha256 = FrozenCandidateBinding.Hash(trainManifest),
                dev_manifest = relativeDevManifest,
                dev_manifest_sha256 = FrozenCandidateBinding.Hash(devManifest),
                source_count = 3,
                expected_prepared_panels_from_prior_upstream_run = 3,
                source_selection = "all sources in declared order",
            },
        });
        string protocolSha = FrozenCandidateBinding.Hash(protocolBytes);
        var protocol = new FrozenCandidateFile("protocol.json", protocolSha, protocolBytes);

        SourceFixture[] ordered = [.. train, .. dev];
        FrozenSyntheticInput complete = LoadSyntheticInput(
            repositoryRoot,
            root,
            "complete-input.json",
            protocolSha,
            ordered);
        FrozenCandidateSyntheticRunner.ValidateProtocolDefinedSources(
            repositoryRoot, protocol, complete, CancellationToken.None);

        FrozenSyntheticInput subset = LoadSyntheticInput(
            repositoryRoot,
            root,
            "subset-input.json",
            protocolSha,
            ordered[..2]);
        ExpectInvalidData(
            () => FrozenCandidateSyntheticRunner.ValidateProtocolDefinedSources(
                repositoryRoot, protocol, subset, CancellationToken.None),
            "complete protocol-defined source set");

        FrozenSyntheticInput reordered = LoadSyntheticInput(
            repositoryRoot,
            root,
            "reordered-input.json",
            protocolSha,
            [ordered[1], ordered[0], ordered[2]]);
        ExpectInvalidData(
            () => FrozenCandidateSyntheticRunner.ValidateProtocolDefinedSources(
                repositoryRoot, protocol, reordered, CancellationToken.None),
            "ordered source sequence");
    }

    private static FrozenSyntheticInput LoadSyntheticInput(
        string repositoryRoot,
        string root,
        string fileName,
        string protocolSha,
        IReadOnlyList<SourceFixture> sources)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = FrozenCandidateSyntheticRunner.InputSchema,
            protocol_sha256 = protocolSha,
            split = "synthetic",
            sources = sources.Select(source => new
            {
                relative_path = source.RelativePath,
                sha256 = source.Sha256,
                width = 2,
                height = 2,
            }).ToArray(),
        });
        string path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, bytes);
        return FrozenCandidateSyntheticRunner.LoadInput(
            repositoryRoot,
            path,
            FrozenCandidateBinding.Hash(bytes),
            CancellationToken.None);
    }

    private static SourceFixture WriteSource(
        string repositoryRoot,
        string root,
        string fileName,
        string split,
        int seed,
        byte shade)
    {
        string path = Path.Combine(root, fileName);
        byte[] bytes = CreatePng(shade);
        File.WriteAllBytes(path, bytes);
        return new SourceFixture(
            fileName,
            Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
            FrozenCandidateBinding.Hash(bytes),
            split,
            seed);
    }

    private static byte[] SourceManifest(
        string split,
        int seed,
        IReadOnlyList<SourceFixture> sources) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            contains_precomputed_masks = false,
            contains_truth = false,
            images = sources.Select(source => new
            {
                family = "self-test-family",
                height = 2,
                image = source.FileName,
                image_sha256 = source.Sha256,
                seed = source.Seed,
                split = source.Split,
                width = 2,
            }).ToArray(),
            preset = "self-test",
            schema = "graphreader.synthetic-runtime-raster-inputs.v1",
            seed,
            source = "project-owned-self-test",
            split,
        });

    private static void ExpectInvalidData(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains(message, StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException("FROZEN_CANDIDATE_PROTOCOL_SOURCE_BINDING_SELF_TEST_FAILED");
    }

    private static byte[] CreatePng(byte shade)
    {
        byte[] pixels =
        [
            shade, shade, shade, 255,
            255, 255, 255, 255,
            255, 255, 255, 255,
            0, 0, 0, 255,
        ];
        var bitmap = BitmapSource.Create(
            2, 2, 96, 96, PixelFormats.Bgra32, palette: null, pixels, stride: 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed record SourceFixture(
        string FileName,
        string RelativePath,
        string Sha256,
        string Split,
        int Seed);
}
