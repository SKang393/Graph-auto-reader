// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Text.Json;

namespace GraphReader.RealAcceptance.Ocr;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--self-test")
            {
                FullCaptureSelfTests.Run();
                Console.WriteLine("FULL_CAPTURE_SELF_TESTS=PASS; inference=NOT_STARTED; training=NOT_STARTED");
                return 0;
            }

            if (args.Length == 3 && args[0] == "--preflight")
            {
                using FullBinding binding = FullBinding.Load(args[1], args[2], CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "preflight_passed",
                    binding_sha256 = binding.Digest,
                    parent_binding_sha256 = binding.ParentBindingSha,
                    runtime_binding_sha256 = binding.RuntimeBindingSha,
                    input_sources = binding.Sources.Count,
                    marker_classifier_model_loaded = false,
                    model_inference_started = false,
                    training_approved = false,
                    output = binding.OutputRelativePath,
                    runtime = FullBinding.RuntimeSummary,
                }));
                return 0;
            }

            if (args.Length == 5 && args[0] == "--capture")
            {
                using FullBinding binding = FullBinding.Load(args[1], args[2], CancellationToken.None);
                binding.RequireReviewApproval(args[3], args[4]);
                using var captureTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                FullCaptureOutcome outcome = await FullRunner.RunAsync(binding, captureTimeout.Token).ConfigureAwait(false);
                if (outcome.AllSourcesSucceeded)
                {
                    Console.WriteLine("CAPTURE_ALL_SOURCES_SUCCEEDED; source_accounting_complete=true; marker_classifier_model_loaded=false; training_approved=false");
                    return 0;
                }
                Console.Error.WriteLine($"CAPTURE_ACCOUNTING_COMPLETE_WITH_FAILURES; status={outcome.Status}; source_rows={outcome.SourceRows}; completed_sources={outcome.CompletedSources}; failed_sources={outcome.FailedSources}; marker_classifier_model_loaded=false; training_approved=false");
                return outcome.SourceAccountingComplete ? 3 : 2;
            }

            Console.Error.WriteLine("Usage: --self-test | --preflight <binding-path> <binding-sha256> | --capture <binding-path> <binding-sha256> <review-receipt-path> <review-receipt-sha256>");
            return 2;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"PREFLIGHT_OR_CAPTURE_FAILED={exception.GetType().Name}: {exception.Message}");
            return 2;
        }
    }
}
