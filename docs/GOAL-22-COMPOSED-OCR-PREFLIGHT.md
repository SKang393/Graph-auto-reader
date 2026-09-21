<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Composed OCR development authentication, 2026-09-21

The sealed-evaluation controller now accepts the composed OCR pipeline through
an explicit, separate evidence schema. It authenticates the current candidate,
the fixed owned development inventory, every runtime DLL/EXE/JSON file, and the
development request. It then reruns native development inference and requires
exact agreement in every metric and input identity. Elapsed time is descriptive.
Input and runtime hashes are rechecked after the replay.

The five shared OCR bars apply to assembled OCR, the boundary used by the
application. Raw detector geometry remains separately reported. Legacy detector
reports retain their own schema and meaning. Request creation, worker selection,
first-read authorization, aggregate validation, closure and recovery all preserve
this distinction. Neither a changed report nor a cross-route schema can supply
sealed admission.

Validation passes 145 Python tests, including real admission-accounting fixtures
for both worker modes and tamper checks for inputs, runtime files and reports.
One initial test-collection error is retained and repaired. The existing native
worker build remains unchanged and passed without warnings or errors.

The actual current worker runs twice over 23 owned synthetic sources, 39 panels
and 892 labels: first to record current development evidence, then to authenticate
that evidence. Every metric equals the prior native workflow. The two runs plus
authentication take 130.86 seconds. All 37 runtime files are bound.

| Held-out development measure | Result | Shared bar |
| --- | ---: | ---: |
| Text region precision | 181/189 = 95.77% | at least 95% |
| Text region recall | 181/183 = 98.91% | at least 95% |
| Exact text | 177/183 = 96.72% | at least 95% |
| Character error rate | 16/1019 = 1.57% | at most 5% |
| Correct text role | 174/183 = 95.08% | at least 95% |

Role accuracy has only 0.08 percentage points of margin. These are development
results, not production or real-data approval. Training-side diagnostics remain
weaker, particularly on damaged text, and are preserved in the aggregate report.

[The evidence index](GOAL-22-COMPOSED-OCR-PREFLIGHT.json) records measured source
and result hashes. No private or sealed data was read, no optimizer step ran,
and no model was activated. No new dependency or model payload was introduced;
existing manifests, checksums and license inputs were authenticated by the native
runtime. Build 433 remains 0.4.33. The worker checkpoint `ddf4b19` passed CI run
35565412024. All four Goal 22 outcomes remain incomplete.

Next is a separately preregistered evaluation of the frozen composition, using
the existing one-read sealed budget and leaving two unused reserves. It requires
no retraining. Marker/calibration repairs and genuine real acceptance remain.
