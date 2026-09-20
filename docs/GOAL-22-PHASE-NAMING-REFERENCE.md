<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Repeated-phase reference correction

The synthetic CSV reference copied raw scene labels `a,b,a,b`, while the
product specification requires `a1,b1,a2,b2` for repeated phases. The strict
evaluator was correct; its reference needed repair.

The truth builder now uses independent scene phase identities and order to
number repeated `a`, `b`, `m` and `g` labels. Existing numbered and unknown codes
stay unchanged. Duplicate identities, invalid orders, missing labels and naming
collisions are rejected. The original scene-to-CSV consistency check still uses
raw labels. Predictions never determine the reference.

All 23 builder tests pass. Rescoring the saved 23-source unknown-fill workflow
changes 175 training-point phase labels and no development labels. Correct
relational rows decrease from 28/724 to 0/724; the earlier number used the wrong
phase reference. Unique point/value matches remain 49/706, with six completed
sources and 17 failures. Output bytes, coordinates, values, series relations,
denominators and tolerances are unchanged. Earlier evidence is retained.

[Exact results and evidence hashes](GOAL-22-PHASE-NAMING-REFERENCE.json)
record the correction. No inference, training, private/sealed read, production
activation or packaged build occurred. The pending phase-geometry repair must
use this same corrected reference for both sides of its comparison.

Goal 22 remains incomplete.
