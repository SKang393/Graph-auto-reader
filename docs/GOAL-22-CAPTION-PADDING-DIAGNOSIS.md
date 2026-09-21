<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Caption repair recognition changes traced, 2026-09-21

The two incidental text changes recorded in the caption-bracket repair are
now reproduced. Their original pixels and crop boundaries did not change.
Splitting nearby captions changed batch membership and reduced their padded
recognition input width from 405 to 360 pixels.

| Saved input width | Participant reading | Numeric reading |
| --- | --- | --- |
| 405 | `Participant 01.` | `1.00` |
| 360 | `Participant O1` | `100` |

The first diagnostic replayed each original compiled pipeline with saved
recognitions, without model inference. All saved regions across six panels
were reproduced. Of 103 common regions, 28 received different padded inputs.
Both changed readings retained identical source-crop hashes and polygons.

The second diagnostic reconstructed only the four observed input tensors.
Every full crop hash matched its capture before execution. Four CPU calls to
the same existing recognizer reproduced all four saved readings exactly.
This isolates the effect of the measured padding change. No alternative width,
model, threshold or candidate was selected from expected answers.

The initial after-replay build failed because generated C# from the completed
before build entered its source glob. Explicit source inclusion fixed the
diagnostic in a new directory; the completed before replay was retained.
The initial attempt took 14.48 seconds, its after-only retry 9.58 seconds, and
the four actual recognitions 9.13 seconds including compilation.

The [evidence record](GOAL-22-CAPTION-PADDING-DIAGNOSIS.json) binds the original
and revised tool projects, void attempt, compiled-runtime replays, exact inputs,
model identity and observations. This resolves the previously untraced cause
without changing the historical report or selecting a new OCR configuration.
The participant label remains incorrect, and numeric review/export protections
remain required. No training, private/sealed read, new dependency, model import,
production approval or packaged build occurred. Overall accuracy remains below
the Goal 22 requirements.
