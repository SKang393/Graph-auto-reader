// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;

namespace GraphReader.RealAcceptance.Ocr;

internal static class FrozenRealWorkflowWorkerSelfTest
{
    internal static object Run()
    {
        string[] valid =
        [
            "--run-frozen-real-workflow-worker", "--candidate-binding", "fixture.json",
            "--candidate-binding-sha256", new string('a', 64), "--corpus-root", "unused",
            "--split", "real-sealed", "--output-root", "unused-output",
            "--attempt-id", "00000000000000000000000000000001", "--explicit-opt-in",
        ];
        Dictionary<string, string> options = FrozenRealWorkflowWorker.ParseOptions(valid);
        if (options.Count != 6 || options["--split"] != "real-sealed")
            throw new InvalidOperationException("WORKER_VALID_ARGUMENTS_REJECTED");
        string[][] rejected =
        [
            valid[..^1],
            [.. valid, "--explicit-opt-in"],
            [.. valid, "--unknown", "value"],
            [.. valid, "--split", "real-dev"],
            valid.Select(static value => value == "real-sealed" ? "train" : value).ToArray(),
            valid.Select(static value => value == "00000000000000000000000000000001" ? "invalid" : value).ToArray(),
            valid[..^2],
        ];
        foreach (string[] invalid in rejected)
        {
            bool threw = false;
            try
            {
                _ = FrozenRealWorkflowWorker.ParseOptions(invalid);
            }
            catch (InvalidDataException)
            {
                threw = true;
            }
            if (!threw)
                throw new InvalidOperationException("WORKER_INVALID_ARGUMENTS_ACCEPTED");
        }
        return new { status = "pass", checks = 8, model_inference = false,
            private_reads = 0, sealed_reads = 0 };
    }
}
