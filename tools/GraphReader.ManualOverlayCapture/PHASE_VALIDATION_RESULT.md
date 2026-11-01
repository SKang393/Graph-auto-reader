<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Assisted phase-divider validation result

The diagnostic wrapper replayed the unchanged divider detector before the
unchanged phase service on all seven assisted training panels. Panel status,
calibration validity, and accepted marker geometry and confidence matched the
preceding assisted run on all seven. The same two panels returned
`PHASE_INVALID_DIVIDER_OUTPUT`.

Both failed panels contained adjacent detected divider pairs closer than the
service's configured minimum separation: three such pairs in total. Divider
IDs, plot containment, styles, sources, confidence values, segment IDs, and
panel IDs passed the observed checks. The wrapper reports conditions, not which
physical lines are correct; close detected dividers can represent either
duplicate strokes or distinct boundaries and require careful review.

| Evidence | SHA-256 |
| --- | --- |
| Registration | `2998756377f746e8423843fd1cd3397b00ca49446f030dfc32e09a1df4510445` |
| Protocol | `fe92eb9796d8edd90691ba2a56974f8dbe7e7b73610f04f511c93911a7c48758` |
| Diagnostic wrapper | `7af757f9ab05dfa85fe9cddd694bacc0b6a46b51ce707c38fc6b7471564a3bca` |
| Runtime before and after | `1e0bd8539a5a24bfc523b00843b089b1fb4d5425aff9ea7e338ef61685888b21` |
| Assisted capture | `6764d4bc8445024ea9bd71ddb85ae41fbab808dbdbab64d4517a0161a4ccf848` |
| Aggregate comparison | `7a4fd7cc0987558209a02d5e3529a985bd99f322dac6aaad62f399206d0b4c7a` |

The Release diagnostic build, synthetic phase-validation self-check, one
scoring test and pyflakes passed. The capture returned nonzero for the two
expected phase failures. It used CPU inference, simulated manual confirmation,
no optimizer, no development or sealed payload, and no production approval.
Private per-panel diagnostics remain in the project-local run folder.
