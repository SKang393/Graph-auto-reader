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
                CaptureSelfTests.Run();
                Console.WriteLine("SMOKE_CAPTURE_SELF_TESTS=PASS; inference=NOT_STARTED; training=NOT_STARTED");
                return 0;
            }

            if (args.Length == 3 && args[0] == "--preflight")
            {
                using SmokeBinding binding = SmokeBinding.Load(args[1], args[2], CancellationToken.None);
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
                    runtime = SmokeBinding.RuntimeSummary,
                }));
                return 0;
            }

            if (args.Length == 5 && args[0] == "--capture")
            {
                using SmokeBinding binding = SmokeBinding.Load(args[1], args[2], CancellationToken.None);
                binding.RequireReviewApproval(args[3], args[4]);
                await SmokeRunner.RunAsync(binding, CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine("CAPTURE_COMPLETE; marker_classifier_model_loaded=false; training_approved=false");
                return 0;
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
