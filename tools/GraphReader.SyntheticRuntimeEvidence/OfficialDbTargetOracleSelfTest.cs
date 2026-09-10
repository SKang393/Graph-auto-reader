// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.IO;

namespace GraphReader.SyntheticRuntimeEvidence;

internal static class OfficialDbTargetOracleSelfTest
{
    public static object Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "graphreader-db-target-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        try
        {
            int checks = 0;
            string request = Path.Combine(root, "request.json");
            File.WriteAllText(request, "{}");
            string output = Path.Combine(root, "artifacts", "oracle-output");
            string[] parsed = OfficialDbTargetOracle.ValidateCommand(
            [
                OfficialDbTargetOracle.Command,
                "request.json",
                new string('a', 64),
                output,
            ], root);
            Require(parsed[0] == request && parsed[1] == new string('a', 64) && parsed[2] == output,
                "command boundary");
            checks++;

            ExpectFailure(() => OfficialDbTargetOracle.ValidateCommand(
                [OfficialDbTargetOracle.Command, "request.json", "short", output], root), "SHA-256");
            checks++;
            ExpectFailure(() => OfficialDbTargetOracle.ValidateCommand(
                [OfficialDbTargetOracle.Command, "request.json", new string('a', 64), Path.Combine(root, "foreign")], root),
                "artifacts");
            checks++;

            byte[] target = new byte[4 * sizeof(float)];
            BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(0, 4), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(4, 4), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(8, 4), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(12, 4), 0f);
            Require(OfficialDbTargetOracle.DecodeTarget(target, 2, 2).SequenceEqual([0f, 1f, 1f, 0f]),
                "binary float32 target");
            checks++;

            byte[] nonBinary = (byte[])target.Clone();
            BinaryPrimitives.WriteSingleLittleEndian(nonBinary.AsSpan(4, 4), 0.5f);
            ExpectFailure(() => OfficialDbTargetOracle.DecodeTarget(nonBinary, 2, 2), "finite binary");
            checks++;
            byte[] nonFinite = (byte[])target.Clone();
            BinaryPrimitives.WriteSingleLittleEndian(nonFinite.AsSpan(4, 4), float.NaN);
            ExpectFailure(() => OfficialDbTargetOracle.DecodeTarget(nonFinite, 2, 2), "finite binary");
            checks++;
            ExpectFailure(() => OfficialDbTargetOracle.DecodeTarget(target[..^1], 2, 2), "byte count");
            checks++;

            return new
            {
                Status = "passed",
                CheckCount = checks,
                ModelInference = false,
                TruthRead = false,
                PrivateData = false,
                SealedData = false,
                ProductionApproved = false,
            };
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ExpectFailure(Action action, string expected)
    {
        try
        {
            action();
            throw new InvalidOperationException("Expected validation failure was not raised.");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            if (!exception.Message.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Expected failure containing '{expected}', got '{exception.Message}'.", exception);
            }
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Self-test failed: " + label);
        }
    }
}
