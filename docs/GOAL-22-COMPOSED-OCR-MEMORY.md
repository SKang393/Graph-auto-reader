<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Composed OCR now runs and scores entirely in memory

The native evidence tool imports the actual source image, finds its panels and
axes, and supplies the same detector image and phase context as the application.
It captures raw text regions from that OCR call, then scores raw and finished
regions separately. There is no second detector run or truth-derived input to
inference. Source coordinates are mapped through the actual panel transform.

All 23 owned synthetic sources and 39 panels run successfully in 68.00 seconds.
Every aggregate over all 892 labels exactly matches the saved full application
run: geometry, text, character errors, roles and explicit recognition failures.
Development finished OCR retains 181/183 matched labels, 177/183 exact readings
and 174/183 correct roles. Train failures remain in their separate denominator.
This establishes adapter parity; it does not improve or approve the model.

## Verification and safeguards

Twenty-five new checks cover observation identity and single-use handling,
recovered text, image-only inference inputs, complete source accounting, source
hashes, duplicate rejection, resource limits, sanitized failures, cancellation,
split aliases, serialization and dimensions read from actual image bytes.
The aggregate and three existing corpus/mapping/worker suites also pass. The
x64 build has zero warnings or errors. The complete final job takes 151.39
seconds, including compilation and inference.

The original DB-only runtime remains a separate entry point. The composed route
requires its own exact composition identity and authenticates the bound models,
manifests, licenses and executing assemblies. It uses the existing nonpersistent
cache. Raw observations remain in memory; no case-level result is serialized.
Private and sealed inputs are not supported by the development command.

Three early plumbing attempts are retained: a fixture allocation analyzer
failure, the generator's `dev` versus scorer's `validation` naming mismatch,
and stale composition metadata in the launcher. The metadata repair reused
the already verified build. A subsequent image-dimension safeguard received
the final complete check above. No training or sealed budget was consumed.

The [evidence index](GOAL-22-COMPOSED-OCR-MEMORY.json) binds source, input,
runtime, model and output records. No dependency or model payload changes;
project code remains Apache-2.0 and existing model notices remain verified.

Next, connect this evaluator to the guarded composed sealed worker and current
prerequisite source adapters. Private acceptance, production activation and the
2.0.0 release remain unfinished. Build 433 stays 0.4.33. Goal 22 is incomplete.
