// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Text.Json;
using GraphReader.Domain;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>Model-free checks for sealed-worker admission and receipt semantics.</summary>
internal static class OriginalDbOcrSealedWorkerSelfTest
{
    private const string HashValue = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static object ResultFixture()
    {
        Dictionary<string, object?> fields = ValidFields();
        fields["source_count"] = 1;
        OriginalDbOcrSealedWorker.Request request = OriginalDbOcrSealedWorker.ParseRequest(Json(fields));
        var scorer = new OriginalDbOcrAggregateScorer();
        OriginalDbOcrGeneratorRole[] roles = Enum.GetValues<OriginalDbOcrGeneratorRole>();
        OcrRole[] runtimeRoles = [OcrRole.XTick, OcrRole.YTick, OcrRole.AxisTitle, OcrRole.PhaseHeading,
            OcrRole.LegendText, OcrRole.Participant, OcrRole.Annotation, OcrRole.PhaseHeading];
        OriginalDbOcrAggregateTruth[] truths = roles.Select((role, index) =>
            new OriginalDbOcrAggregateTruth(new(index * 20, 1, index * 20 + 10, 10), "unit", role)).ToArray();
        OriginalDbOcrAggregatePrediction[] predictions = truths.Select((truth, index) =>
            new OriginalDbOcrAggregatePrediction(truth.Box, truth.Text, runtimeRoles[index])).ToArray();
        scorer.AddSource(truths, predictions);
        using JsonDocument result = JsonDocument.Parse(OriginalDbOcrSealedWorker.SerializeResult(
            request, HashValue, 1, new(1, 1, scorer.Score())));
        return new { unsealed_fixture = true, model_inference = false, private_reads = 0,
            sealed_reads = 0, envelope = result.RootElement.Clone() };
    }

    internal static object Run()
    {
        int checks = 0;
        OriginalDbOcrSealedWorker.Request request =
            OriginalDbOcrSealedWorker.ParseRequest(Json(ValidFields()));
        Require(request.AttemptId == "fixture-attempt" && request.SourceCount == 2, "valid request");
        checks++;

        foreach ((string name, Action<Dictionary<string, object?>> mutate) in new (string, Action<Dictionary<string, object?>>)[]
        {
            ("duplicate key", fields => { }),
            ("extra field", fields => fields["extra"] = "rejected"),
            ("bad scope", fields => fields["acceptance_scope"] = "wrong.scope"),
            ("bad split", fields => fields["split"] = "test"),
            ("bad hash", fields => fields["candidate_sha256"] = "not-a-hash"),
            ("path traversal", fields => fields["candidate_path"] = "../candidate.onnx"),
            ("newline attempt id", fields => fields["attempt_id"] = "fixture\nattempt"),
            ("excessive source count", fields => fields["source_count"] = int.MaxValue),
        })
        {
            Dictionary<string, object?> fields = ValidFields();
            mutate(fields);
            if (name == "duplicate key")
            {
                string validJson = System.Text.Encoding.UTF8.GetString(Json(fields));
                ExpectInvalid(System.Text.Encoding.UTF8.GetBytes(
                    "{\"schema\":\"" + OriginalDbOcrSealedWorker.RequestSchema + "\"," + validJson[1..]));
            }
            else
            {
                ExpectInvalid(Json(fields));
            }
            checks++;
        }

        int receipts = 0;
        using (var empty = new MemoryStream())
        using (var receipt = new OriginalDbOcrReadReceiptStream(empty, () => receipts++))
        {
            byte[] buffer = new byte[1];
            Require(receipt.Read(buffer, 0, 0) == 0 && receipt.Read(buffer, 0, 1) == 0 && receipts == 0, "zero length and EOF");
        }
        checks++;

        using (var owner = new MemoryStream([7, 8]))
        {
            int callbackCount = 0;
            using (var receipt = new OriginalDbOcrReadReceiptStream(owner, () => callbackCount++))
            {
                byte[] buffer = new byte[1];
                Require(receipt.Read(buffer, 0, 0) == 0 && callbackCount == 0 && owner.Position == 0,
                    "zero length does not read a nonempty stream");
                Require(receipt.Read(buffer, 0, 1) == 1 && callbackCount == 1, "first positive read receipt");
                Require(receipt.Read(buffer, 0, 1) == 1 && callbackCount == 1, "single receipt");
            }
            Require(owner.CanRead && owner.ReadByte() == -1, "owner remains open");
        }
        checks += 3;

        using (var owner = new MemoryStream([9]))
        {
            int callbackCount = 0;
            using var receipt = new OriginalDbOcrReadReceiptStream(owner, () =>
            {
                callbackCount++;
                throw new InvalidDataException("callback failed");
            });
            byte[] buffer = new byte[1];
            try { _ = receipt.Read(buffer, 0, 1); throw new InvalidOperationException("failing callback did not throw"); }
            catch (InvalidDataException) { }
            long position = owner.Position;
            try { _ = receipt.Read(buffer, 0, 1); throw new InvalidOperationException("failed receipt retried"); }
            catch (InvalidDataException) { }
            Require(callbackCount == 1 && owner.Position == position, "failed receipt no retry");
        }
        checks++;

        return new { schema = "graphreader.original-db-ocr-sealed-worker-self-test.v1", checks, status = "passed",
            model_inference = false, file_io = false, sealed_reads = 0, private_reads = 0, production_approved = false };
    }

    private static Dictionary<string, object?> ValidFields() => new()
    {
        ["schema"] = OriginalDbOcrSealedWorker.RequestSchema,
        ["acceptance_scope"] = OriginalDbOcrSealedArchive.AcceptanceScope,
        ["split"] = "sealed",
        ["attempt_id"] = "fixture-attempt",
        ["admission_binding_sha256"] = HashValue,
        ["set_id"] = HashValue,
        ["candidate_path"] = "artifacts/candidates/candidate.json",
        ["candidate_sha256"] = HashValue,
        ["archive_path"] = "artifacts/synthetic-sealed-reserves/fixture.zip",
        ["archive_sha256"] = HashValue,
        ["archive_manifest_sha256"] = HashValue,
        ["source_count"] = 2,
        ["coverage_protocol_sha256"] = OriginalDbOcrSealedArchive.CoverageProtocolSha256,
    };

    private static byte[] Json(Dictionary<string, object?> fields) =>
        JsonSerializer.SerializeToUtf8Bytes(fields);

    private static void ExpectInvalid(byte[] payload)
    {
        try
        {
            _ = OriginalDbOcrSealedWorker.ParseRequest(payload);
            throw new InvalidOperationException("invalid request accepted");
        }
        catch (InvalidDataException error)
        {
            Require(error.Message == "OCR_SEALED_REQUEST_INVALID" && error.InnerException is null, "invalid request contract");
        }
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"OCR worker self-test failed: {description}");
    }
}
