// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using GraphReader.Export;

namespace GraphReader.RealAcceptance.Ocr;

internal static class EngaugeGroupedWorkflowExecutorSelfTest
{
    private static readonly WholeWorkflowEvaluationOptions Options = new(5, 0.01, 5);

    internal static object Run()
    {
        var checks = new List<string>();
        EngaugeWorkflowImageGroup shared = Group(CreatePng(32, 24), 2, 3);
        int calls = 0;
        EngaugeGroupedWorkflowAggregateReport workflowFailure = Execute(
            [shared],
            (input, _) =>
            {
                calls++;
                byte[] exposed = input.CopyImageBytes();
                exposed[0] = 0;
                return Task.FromResult<WholeWorkflowCaseOutput?>(new(
                    input.CaseKey,
                    input.SourceSha256,
                    false,
                    ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                    []));
            });
        Require(calls == 1 && workflowFailure.WorkflowInvocations == 1 &&
            workflowFailure.ImageGroups == 1 && workflowFailure.DecodedImageGroups == 1 &&
            workflowFailure.ProjectFiles == 2 &&
            workflowFailure.WorkflowFailedProjects == 2 && workflowFailure.TruthPoints == 3 &&
            workflowFailure.Evaluation.TruthPoints == 3 && workflowFailure.Evaluation.UniquePointMissing == 3 &&
            workflowFailure.ExecutionFailureKinds.GetValueOrDefault(
                ProductionWorkflowFailureCodes.DetectionEvidenceRejected) == 1 &&
            shared.CopyImageBytes()[0] == 137,
            "shared_image_runs_once_and_keeps_every_project_point");
        checks.Add("shared_image_runs_once_and_keeps_every_project_point");

        calls = 0;
        EngaugeGroupedWorkflowAggregateReport invalidImage = Execute(
            [Group(CreatePng(32, 24, corruptImageData: true), 2, 3)],
            (input, _) =>
            {
                calls++;
                return Task.FromResult<WholeWorkflowCaseOutput?>(new(
                    input.CaseKey, input.SourceSha256, true, null, []));
            });
        Require(calls == 0 && invalidImage.WorkflowInvocations == 0 &&
            invalidImage.DecodedImageGroups == 0 &&
            invalidImage.WorkflowFailedProjects == 2 && invalidImage.Evaluation.TruthPoints == 3 &&
            invalidImage.ExecutionFailureKinds.GetValueOrDefault(
                ProductionWorkflowFailureCodes.ImageImportFailed) == 1,
            "crc_valid_invalid_idat_fails_decode_without_dropping_denominator");
        checks.Add("crc_valid_invalid_idat_fails_decode_without_dropping_denominator");

        VerifyDecodeFailure(CreatePng(19, 11, decodedLengthDelta: -1),
            "valid_zlib_with_short_scanline_is_rejected");
        checks.Add("valid_zlib_with_short_scanline_is_rejected");
        VerifyDecodeFailure(CreatePng(19, 11, decodedLengthDelta: 1),
            "valid_zlib_with_excess_scanline_is_rejected");
        checks.Add("valid_zlib_with_excess_scanline_is_rejected");
        VerifyDecodeFailure(CreatePng(19, 11, truncateZlibChecksum: true),
            "truncated_zlib_checksum_is_rejected");
        checks.Add("truncated_zlib_checksum_is_rejected");

        calls = 0;
        EngaugeGroupedWorkflowAggregateReport commonPngs = Execute(
            [
                Group(CreatePng(17, 13, bitDepth: 16, colorType: 0), 1, 1),
                Group(CreatePng(18, 13, bitDepth: 8, colorType: 6), 1, 1),
                Group(CreatePng(19, 13, bitDepth: 8, colorType: 0, interlaceMethod: 1), 1, 1),
            ],
            (input, _) =>
            {
                calls++;
                return Task.FromResult<WholeWorkflowCaseOutput?>(new(
                    input.CaseKey,
                    input.SourceSha256,
                    false,
                    ProductionWorkflowFailureCodes.DetectionEvidenceRejected,
                    []));
            });
        Require(calls == 3 && commonPngs.ImageGroups == 3 &&
            commonPngs.DecodedImageGroups == 3 && commonPngs.WorkflowInvocations == 3 &&
            commonPngs.Evaluation.TruthPoints == 3,
            "common_16bit_rgba_and_adam7_pngs_decode");
        checks.Add("common_16bit_rgba_and_adam7_pngs_decode");

        EngaugeGroupedWorkflowAggregateReport exportFailure = Execute(
            [Group(CreatePng(33, 24), 1, 1)],
            (input, _) => Task.FromResult<WholeWorkflowCaseOutput?>(new(
                input.CaseKey, input.SourceSha256, false,
                EngaugeGroupedWorkflowExecutor.ExportFailed, [])));
        Require(exportFailure.ExecutionFailureKinds.GetValueOrDefault(
                EngaugeGroupedWorkflowExecutor.ExportFailed) == 1 &&
            exportFailure.WorkflowFailedProjects == 1 && exportFailure.Evaluation.TruthPoints == 1,
            "export_failure_is_sanitized_and_retained");
        checks.Add("export_failure_is_sanitized_and_retained");

        EngaugeGroupedWorkflowAggregateReport missing = Execute(
            [Group(CreatePng(34, 24), 1, 2)],
            (_, _) => Task.FromResult<WholeWorkflowCaseOutput?>(null));
        Require(missing.ExecutionFailureKinds.GetValueOrDefault(
                EngaugeGroupedWorkflowExecutor.OutputMissing) == 1 &&
            missing.Evaluation.UniquePointMissing == 2,
            "null_output_is_full_denominator_failure");
        checks.Add("null_output_is_full_denominator_failure");

        EngaugeWorkflowImageGroup first = Group(CreatePng(35, 24), 1, 1);
        EngaugeWorkflowImageGroup second = Group(CreatePng(36, 24), 1, 2);
        string firstCase = first.TruthCase.CaseKey;
        string firstSource = first.TruthCase.SourceSha256;
        EngaugeGroupedWorkflowAggregateReport subset = Execute(
            [first, second],
            (input, _) => Task.FromResult<WholeWorkflowCaseOutput?>(
                string.Equals(input.CaseKey, firstCase, StringComparison.Ordinal)
                    ? new(input.CaseKey, input.SourceSha256, false, "unsafe-private-name", [])
                    : new(firstCase, firstSource, false, "unsafe-private-name", [])));
        Require(subset.WorkflowInvocations == 2 && subset.WorkflowFailedImageGroups == 2 &&
            subset.WorkflowFailedProjects == 2 && subset.Evaluation.TruthPoints == 3 &&
            subset.ExecutionFailureKinds.GetValueOrDefault(
                EngaugeGroupedWorkflowExecutor.ExecutionFailed) == 1 &&
            subset.ExecutionFailureKinds.GetValueOrDefault(
                EngaugeGroupedWorkflowExecutor.OutputIdentityMismatch) == 1,
            "foreign_or_subset_output_is_replaced_and_failure_text_is_not_exposed");
        checks.Add("foreign_or_subset_output_is_replaced_and_failure_text_is_not_exposed");

        EngaugeGroupedWorkflowAggregateReport emptyArtifacts = Execute(
            [Group(CreatePng(37, 24), 1, 2)],
            (input, _) => Task.FromResult<WholeWorkflowCaseOutput?>(new(
                input.CaseKey, input.SourceSha256, true, null, [])));
        Require(emptyArtifacts.WorkflowSucceededImageGroups == 0 &&
            emptyArtifacts.ExecutionFailureKinds.GetValueOrDefault(
                EngaugeGroupedWorkflowExecutor.OutputMissing) == 1 &&
            emptyArtifacts.Evaluation.UniquePointMissing == 2,
            "successful_empty_artifact_output_fails_closed");
        checks.Add("successful_empty_artifact_output_fails_closed");

        EngaugeGroupedWorkflowAggregateReport partialArtifacts = Execute(
            [Group(CreatePng(38, 24), 1, 2)],
            (input, _) => Task.FromResult<WholeWorkflowCaseOutput?>(new(
                input.CaseKey,
                input.SourceSha256,
                true,
                null,
                [new WholeWorkflowCsvArtifact(
                    "partial.csv", new string('0', 64), 1,
                    Path.Combine(Path.GetTempPath(), "not-an-actual-artifact.csv"))])));
        Require(partialArtifacts.WorkflowSucceededImageGroups == 1 &&
            partialArtifacts.Evaluation.FailedCases == 1 &&
            partialArtifacts.Evaluation.IntegrityFailureCases == 1 &&
            partialArtifacts.Evaluation.TruthPoints == 2,
            "partial_artifact_output_is_graded_as_full_denominator_failure");
        checks.Add("partial_artifact_output_is_graded_as_full_denominator_failure");

        string serialized = JsonSerializer.Serialize(subset);
        Require(!serialized.Contains("unsafe-private-name", StringComparison.Ordinal) &&
            !serialized.Contains(firstCase, StringComparison.Ordinal) &&
            !serialized.Contains(firstSource, StringComparison.Ordinal) &&
            !serialized.Contains("not-an-actual-artifact", StringComparison.Ordinal) &&
            subset.AggregateOnly && !subset.CaseLevelOutput && !subset.TruthRowsOutput &&
            !subset.PredictionOutput && !subset.PathsOutput && !subset.NamesOutput,
            "aggregate_report_contains_no_names_paths_truth_rows_or_predictions");
        checks.Add("aggregate_report_contains_no_names_paths_truth_rows_or_predictions");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            _ = EngaugeGroupedWorkflowExecutor.ExecuteAsync(
                    [shared], Options,
                    (_, _) => Task.FromResult<WholeWorkflowCaseOutput?>(null),
                    canceled.Token)
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
        checks.Add("pre_cancellation_is_propagated");

        using var midFlight = new CancellationTokenSource();
        try
        {
            _ = EngaugeGroupedWorkflowExecutor.ExecuteAsync(
                    [shared], Options,
                    (_, token) =>
                    {
                        midFlight.Cancel();
                        token.ThrowIfCancellationRequested();
                        return Task.FromResult<WholeWorkflowCaseOutput?>(null);
                    },
                    midFlight.Token)
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
        checks.Add("mid_flight_cancellation_is_propagated");

        return new
        {
            status = "pass",
            check_count = checks.Count,
            checks,
            private_reads = 0,
            sealed_reads = 0,
            model_runs = 0,
        };
    }

    private static EngaugeGroupedWorkflowAggregateReport Execute(
        IReadOnlyList<EngaugeWorkflowImageGroup> groups,
        Func<EngaugeGroupedWorkflowImageInput, CancellationToken, Task<WholeWorkflowCaseOutput?>> execute) =>
        EngaugeGroupedWorkflowExecutor.ExecuteAsync(groups, Options, execute, CancellationToken.None)
            .GetAwaiter().GetResult();

    private static void VerifyDecodeFailure(byte[] image, string label)
    {
        int calls = 0;
        EngaugeGroupedWorkflowAggregateReport report = Execute(
            [Group(image, 1, 2)],
            (input, _) =>
            {
                calls++;
                return Task.FromResult<WholeWorkflowCaseOutput?>(new(
                    input.CaseKey, input.SourceSha256, true, null, []));
            });
        Require(calls == 0 && report.DecodedImageGroups == 0 &&
            report.WorkflowInvocations == 0 && report.WorkflowFailedProjects == 1 &&
            report.Evaluation.TruthPoints == 2 && report.Evaluation.UniquePointMissing == 2 &&
            report.ExecutionFailureKinds.GetValueOrDefault(
                ProductionWorkflowFailureCodes.ImageImportFailed) == 1,
            label);
    }

    private static EngaugeWorkflowImageGroup Group(byte[] image, int projectCount, int pointCount)
    {
        string sourceHash = Convert.ToHexStringLower(SHA256.HashData(image));
        string caseKey = "image-" + sourceHash;
        WholeWorkflowTruthSeries[] series = Enumerable.Range(0, projectCount)
            .Select(index => new WholeWorkflowTruthSeries($"series-{index:D4}"))
            .ToArray();
        WholeWorkflowTruthPoint[] points = Enumerable.Range(0, pointCount)
            .Select(index => new WholeWorkflowTruthPoint(
                $"point-{index:D6}",
                series[index % series.Length].SeriesKey,
                4 + index,
                8 + index,
                1 + index,
                10 + index,
                1 + index,
                ExportMode.PrintedSession,
                null))
            .ToArray();
        var projects = new List<EngaugeWorkflowProjectIdentity>(projectCount);
        int assignedPoints = 0;
        for (int index = 0; index < projectCount; index++)
        {
            int count = pointCount / projectCount + (index < pointCount % projectCount ? 1 : 0);
            assignedPoints += count;
            projects.Add(new EngaugeWorkflowProjectIdentity(
                $"fixture-{index:D4}",
                Convert.ToHexStringLower(SHA256.HashData(
                    Encoding.UTF8.GetBytes($"project-{index:D4}"))),
                1,
                count,
                [
                    new EngaugeDigAxisAnchor(0, 23, 0, 0),
                    new EngaugeDigAxisAnchor(0, 0, 0, 100 + index),
                    new EngaugeDigAxisAnchor(31, 23, 31, 0),
                ]));
        }
        Require(assignedPoints == pointCount, "fixture_point_inventory");
        var truth = new WholeWorkflowTruthCase(
            caseKey, sourceHash, ReadWidth(image), ReadHeight(image), series, points, null);
        return new EngaugeWorkflowImageGroup(image, truth, projects, 0);
    }

    private static int ReadWidth(byte[] png) => BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
    private static int ReadHeight(byte[] png) => BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));

    private static byte[] CreatePng(
        int width,
        int height,
        bool corruptImageData = false,
        int bitDepth = 8,
        int colorType = 0,
        int interlaceMethod = 0,
        int decodedLengthDelta = 0,
        bool truncateZlibChecksum = false)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> dimensions = stackalloc byte[13];
        dimensions.Clear();
        BinaryPrimitives.WriteInt32BigEndian(dimensions[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(dimensions.Slice(4, 4), height);
        dimensions[8] = checked((byte)bitDepth);
        dimensions[9] = checked((byte)colorType);
        dimensions[12] = checked((byte)interlaceMethod);
        WriteChunk(output, "IHDR"u8, dimensions);
        byte[] imageData;
        if (corruptImageData)
        {
            imageData = [1, 2, 3, 4];
        }
        else
        {
            byte[] pixels = CreateDecodedPngBytes(
                width, height, bitDepth, colorType, interlaceMethod);
            if (decodedLengthDelta < 0)
            {
                pixels = pixels[..checked(pixels.Length + decodedLengthDelta)];
            }
            else if (decodedLengthDelta > 0)
            {
                Array.Resize(ref pixels, checked(pixels.Length + decodedLengthDelta));
            }
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(pixels);
            }
            imageData = compressed.ToArray();
            if (truncateZlibChecksum)
            {
                imageData = imageData[..^4];
            }
        }
        WriteChunk(output, "IDAT"u8, imageData);
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static byte[] CreateDecodedPngBytes(
        int width,
        int height,
        int bitDepth,
        int colorType,
        int interlaceMethod)
    {
        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(colorType)),
        };
        (int StartX, int StartY, int StepX, int StepY)[] passes = interlaceMethod == 0
            ? [(0, 0, 1, 1)]
            : [
                (0, 0, 8, 8),
                (4, 0, 8, 8),
                (0, 4, 4, 8),
                (2, 0, 4, 4),
                (0, 2, 2, 4),
                (1, 0, 2, 2),
                (0, 1, 1, 2),
            ];
        using var decoded = new MemoryStream();
        foreach ((int startX, int startY, int stepX, int stepY) in passes)
        {
            int passWidth = width <= startX ? 0 : (width - startX + stepX - 1) / stepX;
            int passHeight = height <= startY ? 0 : (height - startY + stepY - 1) / stepY;
            if (passWidth == 0 || passHeight == 0)
            {
                continue;
            }
            int pixelBytes = checked((passWidth * channels * bitDepth + 7) / 8);
            for (int row = 0; row < passHeight; row++)
            {
                decoded.WriteByte(0);
                for (int index = 0; index < pixelBytes; index++)
                {
                    decoded.WriteByte(byte.MaxValue);
                }
            }
        }
        return decoded.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        output.Write(length);
        output.Write(type);
        output.Write(payload);
        byte[] checksumInput = new byte[type.Length + payload.Length];
        type.CopyTo(checksumInput);
        payload.CopyTo(checksumInput.AsSpan(type.Length));
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, Crc32(checksumInput));
        output.Write(checksum);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xedb88320u & unchecked((uint)-(int)(crc & 1)));
            }
        }
        return ~crc;
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Engauge grouped workflow executor self-test failed: " + label);
        }
    }
}
