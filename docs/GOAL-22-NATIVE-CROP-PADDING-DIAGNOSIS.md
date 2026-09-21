<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native numeric crop diagnosis, 2026-09-21

The native crop and recognition code confirms that different amounts of right
padding can change a reading from the same source pixels. This explains some
sensitivity, but the tested settings do not provide a consistent repair.

The probe covers 73 horizontal numeric regions on the 21 open owned sources.
One of the earlier 74 regions was excluded because the caption repair changed
its detector and refined geometry. All included source crops, with a two-pixel
margin, match the previous fixture exactly. Eight earlier exclusions remain.
Truth is in a separate scoring file and is not supplied to inference.

| Crop geometry | Width 320 | Width 480 | Width 640 |
| --- | ---: | ---: | ---: |
| Refined text bounds | 56/73 exact | 56/73 exact | 55/73 exact |
| Original detector bounds | 59/73 exact | 58/73 exact | 57/73 exact |

These are sensitivity measurements, not additional acceptance gates. Original
detector bounds help some labels and harm others. There is no per-case choice
using truth, and no crop, batch size, confidence threshold or product setting
changes. Wider padding is not a demonstrated accuracy fix. The current models
remain below the shared acceptance bars.

The probe uses the actual C# crop and recognizer implementation, with the same
existing English recognizer and all ten eligible logical threads. Historical
full-stage evidence used a different batch context and one inference thread;
this is not a claim of bit-exact parity with that execution. Compilation has
zero warnings or errors. Inference took 32.32 seconds, and the complete check
took 47.17 seconds.

See [aggregate measurements and input hashes](GOAL-22-NATIVE-CROP-PADDING-DIAGNOSIS.json).
No new model, dependency, license, training, private/sealed read, production
approval, package or release is involved. All Goal 22 outcomes remain open.
