// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using GraphReader.Markers.Detection;

namespace GraphReader.RealAcceptance.Ocr;

internal static class EngaugeDevProposalArchiveSelfTest
{
    internal static object Run()
    {
        string root = FindRoot();
        string directory = Path.Combine(root, "artifacts", "test-temp", "archive-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "test.bin");
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        string image = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 }));
        int n = NormalizedMarkerProposalContract.ChannelCount * NormalizedMarkerProposalContract.PatchSize * NormalizedMarkerProposalContract.PatchSize;
        var batch = Batch(image, Enumerable.Repeat(0.5f, n).ToArray(), [new MarkerPoint(2, 4)]);
        try
        {
            Fail(() => EngaugeDevProposalArchiveCommand.RunAsync(
                ["--archive-engauge-dev-proposals", "unused", "unused", "unused", "unused", "unused",
                    Path.Combine(root, "outside-artifacts-key"), "--explicit-opt-in"], CancellationToken.None)
                .GetAwaiter().GetResult(), "external_key_path");
            EngaugeDevProposalArchiveReceipt receipt;
            using (var archive = new EngaugeDevProposalArchive(root, path, key))
            {
                archive.ConsumeAsync(batch, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                receipt = archive.Complete(Result(), CancellationToken.None);
            }
            byte[] original = File.ReadAllBytes(path);
            Require(receipt.Sha256 == Convert.ToHexStringLower(SHA256.HashData(original)) && receipt.BatchCount == 1 && receipt.PositiveCount == 1, "receipt");
            Verify(path, key, receipt, new string('1', 64));
            Fail(() => Verify(path, key, receipt with { RunId = new string('0', 32) }, new string('1', 64)), "cross_run");
            Fail(() => Verify(path, key, receipt, new string('2', 64)), "wrong_binding");
            Fail(() => Verify(path, new byte[32], receipt, new string('1', 64)), "wrong_key");
            string corrupt = Path.Combine(directory, "corrupt.bin");
            byte[] modified = (byte[])original.Clone(); modified[50] ^= 1;
            File.WriteAllBytes(corrupt, modified);
            Fail(() => Verify(corrupt, key, receipt with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(modified)) }, new string('1', 64)), "tamper_hmac");
            File.WriteAllBytes(corrupt, original[..^1]);
            Fail(() => Verify(corrupt, key, receipt, new string('1', 64)), "truncated");
            File.WriteAllBytes(corrupt, [.. original, 0]);
            Fail(() => Verify(corrupt, key, receipt, new string('1', 64)), "extra_bytes");
            Fail(() => { using var ignored = new EngaugeDevProposalArchive(root, path, key); }, "overwrite");
            Fail(() => { using var ignored = new EngaugeDevProposalArchive(root, Path.Combine(root, "not-private.bin"), key); }, "outside_artifacts");
            InvalidBatch(root, directory, key, Batch(image, [0.5f], [new MarkerPoint(2, 4)]));
            InvalidBatch(root, directory, key, Batch(image, Enumerable.Repeat(float.NaN, n).ToArray(), [new MarkerPoint(2, 4)]));
            InvalidBatch(root, directory, key, Batch("bad", new float[n], [new MarkerPoint(2, 4)]));
            string failedPath = Path.Combine(directory, "failed.bin");
            using (var writer = new EngaugeDevProposalArchive(root, failedPath, key))
            {
                writer.ConsumeAsync(batch, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Fail(() => writer.ConsumeAsync(batch, CancellationToken.None).AsTask().GetAwaiter().GetResult(), "duplicate");
                Fail(() => writer.Complete(Result(), CancellationToken.None), "poisoned");
            }
            Require(!File.Exists(failedPath) && Directory.GetFiles(directory, "*.partial-*").Length == 0, "partial_cleanup");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            string cancelPath = Path.Combine(directory, "cancel.bin");
            using (var writer = new EngaugeDevProposalArchive(root, cancelPath, key))
            {
                try { writer.ConsumeAsync(batch, canceled.Token).AsTask().GetAwaiter().GetResult(); throw new InvalidOperationException("cancel"); }
                catch (OperationCanceledException) { }
                Fail(() => writer.Complete(Result(), CancellationToken.None), "canceled_poison");
            }
            Require(!File.Exists(cancelPath), "cancel_cleanup");
            return new { status = "pass", scope = "synthetic-only", private_payload_reads = 0, sealed_payload_reads = 0, model_runs = 0, optimizer_steps = 0 };
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static void InvalidBatch(string root, string directory, byte[] key, EngaugeDevPositiveProposalBatch batch)
    {
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".bin");
        using var writer = new EngaugeDevProposalArchive(root, path, key);
        Fail(() => writer.ConsumeAsync(batch, CancellationToken.None).AsTask().GetAwaiter().GetResult(), "invalid_batch");
        Fail(() => writer.Complete(Result(), CancellationToken.None), "invalid_batch_poison");
    }
    private static EngaugeDevPositiveProposalBatch Batch(string image, float[] tensor, MarkerPoint[] coordinates) =>
        new(new EngaugeDevPositiveProposals(image, tensor, coordinates, 0));
    private static EngaugeDevProposalPreparationResult Result() => new(new string('1', 64), new string('2', 64), new string('3', 64), new string('4', 64), new string('5', 64), new string('6', 64), new string('7', 64), new string('8', 64), 2, 1, 1, 1, 1, 0, true, false, false, false, false, 0, 0);
    private static void Verify(string path, byte[] key, EngaugeDevProposalArchiveReceipt receipt, string binding)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        EngaugeDevProposalArchiveVerifier.Verify(file, key, receipt, binding, CancellationToken.None);
        Require(file.Position == 0, "rewind");
    }
    private static string FindRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "GraphAutoReader.slnx"))) current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar));
        return current ?? throw new InvalidOperationException("ENGAUGE_ARCHIVE_SELF_TEST_ROOT");
    }
    private static void Fail(Action action, string test) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { return; } throw new InvalidOperationException("ENGAUGE_ARCHIVE_SELF_TEST_" + test); }
    private static void Require(bool value, string test) { if (!value) throw new InvalidOperationException("ENGAUGE_ARCHIVE_SELF_TEST_" + test); }
}
