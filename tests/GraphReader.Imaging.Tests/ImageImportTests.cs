// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Imaging.Tests;

[TestClass]
public sealed class ImageImportTests
{
    private static readonly string[] CancelledPaths = ["unused.png"];

    public static IEnumerable<object[]> SupportedFormats =>
        Enum.GetValues<ImageFileFormat>().Select(static format => new object[] { format });

    [TestMethod]
    [DynamicData(nameof(SupportedFormats))]
    public async Task ImportAsyncDecodesEverySupportedFormat(ImageFileFormat format)
    {
        string directory = TestImageFixtures.CreateDirectory();
        try
        {
            string path = TestImageFixtures.Write(directory, format);
            var service = new ImageImportService();

            ImageImportResult result = await service.ImportAsync(path, CancellationToken.None);

            Assert.IsTrue(result.IsSuccess, result.Error?.TechnicalMessage);
            Assert.IsNotNull(result.Image);
            Assert.AreEqual(format, result.Image.Metadata.Format);
            Assert.AreEqual(TestImageFixtures.Width, result.Image.Metadata.Width);
            Assert.AreEqual(TestImageFixtures.Height, result.Image.Metadata.Height);
            Assert.AreEqual(8, result.Image.Metadata.BitsPerChannel);
            Assert.AreEqual(64, result.Image.Sha256.Length);
            Assert.AreEqual(new FileInfo(path).Length, result.Image.Metadata.ByteLength);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportAsyncPreservesPrivateOriginalAndComputesExactHash()
    {
        string directory = TestImageFixtures.CreateDirectory();
        try
        {
            string path = TestImageFixtures.Write(directory, ImageFileFormat.Png);
            byte[] expected = await File.ReadAllBytesAsync(path);
            var service = new ImageImportService();

            ImageImportResult result = await service.ImportAsync(path, CancellationToken.None);
            Assert.IsNotNull(result.Image);
            byte[] callerCopy = result.Image.OriginalBytes.Copy();
            callerCopy[0] ^= 0xff;

            CollectionAssert.AreEqual(expected, result.Image.OriginalBytes.Copy());
            Assert.IsFalse(result.Image.OriginalBytes.OpenRead().CanWrite);
            Assert.AreEqual(
                Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(),
                result.Image.Sha256);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportBytesAsyncUsesFileValidationWithoutReadingSourceReference()
    {
        string directory = TestImageFixtures.CreateDirectory();
        try
        {
            string path = TestImageFixtures.Write(directory, ImageFileFormat.Png);
            byte[] bytes = await File.ReadAllBytesAsync(path);
            var service = new ImageImportService();
            ImageImportResult fileResult = await service.ImportAsync(path, CancellationToken.None);
            string sourceReference = Path.Combine(directory, "does-not-exist.png");

            ImageImportResult memoryResult = await service.ImportBytesAsync(
                sourceReference,
                new ImmutableImageBytes(bytes),
                CancellationToken.None);

            Assert.IsTrue(memoryResult.IsSuccess, memoryResult.Error?.TechnicalMessage);
            Assert.IsNotNull(memoryResult.Image);
            Assert.AreEqual(sourceReference, memoryResult.Image.SourcePath);
            Assert.AreEqual(fileResult.Image?.Sha256, memoryResult.Image.Sha256);
            Assert.AreEqual(fileResult.Image?.Metadata, memoryResult.Image.Metadata);
            CollectionAssert.AreEqual(bytes, memoryResult.Image.OriginalBytes.Copy());
            Assert.IsFalse(File.Exists(sourceReference));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportBytesAsyncKeepsCallerAndReturnedCopiesImmutable()
    {
        string directory = TestImageFixtures.CreateDirectory();
        try
        {
            string path = TestImageFixtures.Write(directory, ImageFileFormat.Png);
            byte[] expected = await File.ReadAllBytesAsync(path);
            byte[] callerBytes = (byte[])expected.Clone();
            var immutable = new ImmutableImageBytes(callerBytes);
            callerBytes[0] ^= 0xff;

            ImageImportResult result = await new ImageImportService().ImportBytesAsync(
                "memory-source.png",
                immutable,
                CancellationToken.None);

            Assert.IsNotNull(result.Image);
            byte[] returned = result.Image.OriginalBytes.Copy();
            returned[1] ^= 0xff;
            CollectionAssert.AreEqual(expected, result.Image.OriginalBytes.Copy());
            Assert.IsFalse(result.Image.OriginalBytes.OpenRead().CanWrite);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportBytesAsyncReturnsStructuredDecodeFailureAndObservesCancellation()
    {
        var service = new ImageImportService();
        ImageImportResult corrupt = await service.ImportBytesAsync(
            "corrupt.png",
            new ImmutableImageBytes([1, 2, 3, 4, 5]),
            CancellationToken.None);

        Assert.AreEqual(ImageImportErrorCode.CorruptImage, corrupt.Error?.Code);
        Assert.AreEqual("Errors.ImageCorrupt", corrupt.Error?.UserMessageKey);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.ImportBytesAsync(
            "cancelled.png",
            new ImmutableImageBytes([1, 2, 3]),
            new CancellationToken(canceled: true)));
    }

    [TestMethod]
    public async Task ImportBatchAsyncIsOrderedAndFindsHashDuplicates()
    {
        string directory = TestImageFixtures.CreateDirectory();
        try
        {
            string first = TestImageFixtures.Write(directory, ImageFileFormat.Png, "z.png");
            string middle = TestImageFixtures.Write(directory, ImageFileFormat.Bmp, "a.bmp");
            string duplicate = Path.Combine(directory, "copy.png");
            File.Copy(first, duplicate);
            var service = new ImageImportService();

            BatchImportResult result = await service.ImportBatchAsync(
                new[] { first, middle, duplicate },
                CancellationToken.None);

            CollectionAssert.AreEqual(new[] { first, middle, duplicate }, result.Items.Select(static item => item.SourcePath).ToArray());
            Assert.AreEqual(3, result.SuccessfulCount);
            Assert.AreEqual(1, result.DuplicateCount);
            Assert.AreEqual(0, result.Items[2].Image?.DuplicateOfInputIndex);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportBatchAsyncObservesCancellation()
    {
        var cancellation = new CancellationToken(canceled: true);
        var service = new ImageImportService();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.ImportBatchAsync(CancelledPaths, cancellation));
    }

    [TestMethod]
    public async Task ImportAsyncObservesDeterministicStageCancellation()
    {
        string directory = TestImageFixtures.CreateDirectory();
        try
        {
            string path = TestImageFixtures.Write(directory, ImageFileFormat.Png);
            using var cancellation = new CancellationTokenSource();
            var service = new ImageImportService(new CancelAtStageObserver(ImageImportStage.BeforeMetadata, cancellation));

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => service.ImportAsync(path, cancellation.Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportAsyncPrefersCancellationAfterFailingMetadataDecode()
    {
        string directory = TestImageFixtures.CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "corrupt.png");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
            using var cancellation = new CancellationTokenSource();
            var service = new ImageImportService(new CancelAtStageObserver(ImageImportStage.AfterMetadataAttempt, cancellation));

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => service.ImportAsync(path, cancellation.Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportAsyncReturnsStructuredErrorsForCorruptAndUnsupportedInput()
    {
        string directory = TestImageFixtures.CreateDirectory();
        try
        {
            string corrupt = Path.Combine(directory, "bad.png");
            await File.WriteAllBytesAsync(corrupt, [1, 2, 3, 4, 5]);
            string unsupported = Path.Combine(directory, "image.gif");
            await File.WriteAllBytesAsync(unsupported, Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=="));
            var service = new ImageImportService();

            ImageImportResult corruptResult = await service.ImportAsync(corrupt, CancellationToken.None);
            ImageImportResult unsupportedResult = await service.ImportAsync(unsupported, CancellationToken.None);

            Assert.AreEqual(ImageImportErrorCode.CorruptImage, corruptResult.Error?.Code);
            Assert.AreEqual(ImageErrorSeverity.Error, corruptResult.Error?.Severity);
            Assert.AreEqual(ImageSuggestedAction.Retry, corruptResult.Error?.SuggestedAction);
            Assert.AreEqual("Errors.ImageCorrupt", corruptResult.Error?.UserMessageKey);
            Assert.AreEqual(ImageImportErrorCode.UnsupportedFormat, unsupportedResult.Error?.Code);
            Assert.AreEqual(ImageErrorSeverity.Error, unsupportedResult.Error?.Severity);
            Assert.AreEqual(ImageSuggestedAction.SelectManualMode, unsupportedResult.Error?.SuggestedAction);
            Assert.AreEqual("Errors.ImageFormatUnsupported", unsupportedResult.Error?.UserMessageKey);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CancelAtStageObserver : IImageImportStageObserver
    {
        private readonly ImageImportStage targetStage;
        private readonly CancellationTokenSource cancellation;

        public CancelAtStageObserver(ImageImportStage targetStage, CancellationTokenSource cancellation)
        {
            this.targetStage = targetStage;
            this.cancellation = cancellation;
        }

        public void Observe(ImageImportStage stage, string path, CancellationToken cancellationToken)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(path));
            if (stage == targetStage)
            {
                cancellation.Cancel();
            }
        }
    }
}
