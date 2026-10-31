# Public tick-stroke diagnostic

The optional OCR tick-lane diagnostic records each supplied X-axis stroke's
geometry, provenance checks and existing seed-rejection flags. Ordinary OCR
recognition does not create these records. The seed predicate and recovery
behavior are unchanged.

An isolated replay of saved measurements for public synthetic source
`60539b1401df40bbbcc7d237cd85d2c1ba5b675ab3a04f8438aa24f72624401a`
reported one supplied stroke and no usable seed. Its center was 9.784841
pixels from the plot bottom, exceeding the existing 8.251931-pixel allowance;
the other recorded rejection flags were false. Its center was approximately
0.52 pixels from the plot's left edge and both endpoints were above its bottom,
which is consistent with a Y-axis segment, but does not establish its identity.

The report is `artifacts/goal22-runs/tick-stroke-public-60539b-run-v2/report.json`
with SHA-256 `4ed2019ff5ac0c2d1839ef3bc68d833a96b2327ff1d5029597df8a9b3baa8747`.
It replays saved measurements; it does not run OCR, a detector, training, or
read annotations. This single case does not support changing the tick guard or
claiming an automatic-recognition improvement.
