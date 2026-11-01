<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Controlled-overlay confidence experiment

One candidate trained for 28 optimizer steps on AMD Radeon RX 6600 XT
`gfx1032` via HIP `cuda:0`, with no CPU training fallback. The optimizer run
took 1.127 seconds, excluding input verification and initialization. The
registered recipe, seed, exact source bundle, data receipts, and fixed budget
are in `p1.json`, `protocol.json`, and the canonical training ledger. The
frozen V37 weights were verified before and after training; only the
zero-initialized confidence residual was optimized. The journal recorded all
28 completed steps, and the checkpoint writer independently checked the
unchanged retained weights.

At the fixed 0.1 operating threshold, the small controlled diagnostic holdout
gave the same decisions for V37 and the new candidate: eight true positives,
two false negatives, zero false positives, and 14 true negatives. These 24
emitted patches came from only two source groups and share a limited set of
overlay families with training. The exact-ink split is post-hoc. A separate
52 of the original 200 images had no emitted proposal near the insertion and
are outside the patch-level counts. These observations do not establish
scene-level accuracy or generalization.

The fixed synthetic development evaluation failed the shared 95% precision
bar for both scopes:

| Scope | Candidate precision | Candidate recall | V37 precision | V37 recall | Ground-truth markers |
| --- | ---: | ---: | ---: | ---: | ---: |
| Component, 167 scenes | 92.48% | 96.91% | 92.13% | 96.96% | 2,004 |
| Family, nine scenes | 77.78% | 98.54% | 77.69% | 98.06% | 206 |

At the selected threshold the ONNX and GPU decisions agreed on all 270,236
proposal rows; maximum absolute output difference was `8.1063e-6`. Candidate
versus retained GPU geometry matched exactly; ONNX versus historical geometry
had a maximum absolute difference of `4.7684e-6`. The synthetic fixtures use
procedural masks, and source-level overlap with synthetic training has not been
verified. The separate 16-project real development partition was not run
against this already-failed candidate. The controlled and synthetic results
are not product acceptance. The candidate is unapproved, and no sealed payload
was read or budget consumed.

| Evidence | SHA-256 |
| --- | --- |
| Configuration | `4c75a9bcb8635464b4bd432d24be0e88beba000f57b470ae1e034161bbc8dd2c` |
| Protocol | `b3ef1d67dff2ac391c262f98c177c34ddfe213875899dabfc0c46f4b157e37a7` |
| Immutable training source bundle | `f4052736d21b89b9a0865e0542eb8cb269d100d14df025692aebea01ed79e467` |
| Candidate checkpoint | `d5eb62bfa4a7cafd362ae1b0e27f64081fe0f6a4b47a51c6ce6fa8932b3926b0` |
| GPU optimizer outcome | `42eb8899cfb2afa957acf61ec3dfcf23ca88f3c1b63c1c6d10d670f9b1a6da37` |
| Controlled holdout | `12246da9ac8a5b9ce35608e032b7bfc323c3f23993ef7a3cca9749969b0df79f` |
| Synthetic development report | `056aaca3479a8c77b7d5eda37ee55b6d77013282d2079082cbe887ec5956cd19` |
| Experimental ONNX | `35b50adbd6961293b888c5de3f2e5594b9cda7a9cb95d6c68b425999271a1535` |

Nine focused tests and pyflakes passed. The synthetic evaluator used GPU model
inference and CPU ONNX Runtime for parity, with no optimizer step. ROCm emitted
its existing grouped-convolution and tuning-database warnings, but the run
completed. The failed candidate and temporary predictions remain project-local
pending the currently active failure investigation; they are not package inputs.
