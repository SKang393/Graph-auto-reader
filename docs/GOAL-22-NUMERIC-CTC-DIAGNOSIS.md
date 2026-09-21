<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Numeric text decoding: no improvement

The fixed prefix-beam diagnostic produces exactly the same top reading as the
existing greedy decoder on all 73 selected synthetic numeric crops. Keep the
application decoder unchanged. Another decoder-width sweep is not justified
by this result.

| Same images, crops and recognizer | Existing decoder | Prefix beam, width 8 |
| --- | ---: | ---: |
| Exact readings | 56 / 73 | 56 / 73 |
| Character edits | 17 / 174 | 17 / 174 |
| Improved or regressed readings | 0 | 0 |

Both the native crop hashes and greedy text reproduce the previous native
probe for every region. The capture uses the actual C# cropper, BGR
normalization, licensed mobile recognizer and CPU inference runtime. It saves
the output tensors before any scoring. The separate decoder sees no answers,
numeric sequence rules, lexicon, language model or character restrictions.

The algorithm sums the probabilities of paths producing each text prefix,
keeping blank and nonblank endings separate. This follows the mathematics in
[Hannun's CTC explanation](https://distill.pub/2017/ctc/); no external source
code is copied. Thirty small matrices match exhaustive enumeration of every
path. Fourteen additional assertions cover repeats, blanks, deterministic
ties, numerical stability, input validation and a case where summing paths
correctly differs from greedy decoding.

The repaired diagnostic completes in 29.26 seconds: native capture 4.71 seconds,
Python decoding 17.50 seconds. Its build has zero warnings and errors. Ten
logical threads are eligible for inference, with Idle priority and the existing
80% aggregate CPU backstop. This does not guarantee an instantaneous per-core
ceiling. One earlier build failed before inference because the probe called an
internal alphabet helper; the probe now counts Unicode runes directly. The
failed build and its original definition remain recorded.

The top three beam candidates contain the correct text in 64 cases. This is
diagnostic information, not permission to choose an answer using truth or axis
regularity. Existing review requirements for changed numeric values remain.

This fixed-width numeric subset is not full-scene or product-batch acceptance.
No private or sealed data, optimizer step, model revision, runtime setting,
production approval or packaged build changed. The [evidence record](GOAL-22-NUMERIC-CTC-DIAGNOSIS.json)
binds the definition, sources, tensors, predictions and scoring. Current full
OCR still fails the shared bars. Continue the saved marker-structure diagnosis
while the optional pretrained-model choice remains pending. Goal 22 is open;
build 433 remains 0.4.33.
