<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Orientation-disagreement review verification

The unapproved candidate now records an orientation-disagreement reason when a
vertical component crop is replaced with a wide horizontal model crop. A
resulting tick reading receives a region-specific calibration-review warning;
other recognized text receives a role-review warning. The candidate has a new
cache and adapter identity. The default detector composition is unchanged.

On the same 20 exposed training images, the candidate produced three new tick
orientation warnings across two panels. Both reviewed regions that had lost a
region-specific warning in the previous candidate regained one. Among the 115
previously reviewed Y-tick regions, no region lost its prior review signal.
All 21 panels retained the same tick-region records, other calibration failure
reasons, marker observations, accepted markers, status, and validity as the
preceding candidate. All 21 panels still required calibration review.

This is an exposed-sample comparison, not independent accuracy or release
acceptance. It does not establish behavior for undetected or unrecognized text,
resolve the remaining nearly square orientation error, or approve the model.

| Evidence | SHA-256 |
| --- | --- |
| Protocol | `20663fe256a0a079cc5c92b81f78749ffd99fe6a6171d89556ba35b989020c13` |
| Pre-run registration | `292a318fc17b27c129263c2ac1b4ec73b38243e718b847d2f99c6bf848d8ed7b` |
| Runtime description before and after | `5c421b80bef6d808cf3ecb44b93a0ada8e9a3eab856d7c29e3a03c4a138aee40` |
| Candidate workflow capture | `1fb3a30c5d57ecd0ec802ba4394a5077e106f81b5d2229550ca0f90bc481a909` |
| Aggregate review comparison | `6a87dfa1857bd2a12a959beca75ed54d9b05f7ae15844b573c63f2c84ff72b15` |

The OCR suite passed 850 tests with 13 skipped; the application suite passed
776 tests, and integration passed 170 with seven skipped. Three comparison
tests and pyflakes passed. The diagnostic Release build had zero warnings.
The first new regression failed before the safeguard and passed afterward,
including a cache-hit check. The capture returned exit code 1 because all 20
workflows required review. CPU inference was used; no optimizer steps or sealed
payload reads occurred. Per-region data remain project-local.

## Unreadable or non-tick result follow-up

The next candidate revision also requests calibration review when an
orientation-overridden region lies in a possible tick lane but recognition
fails or classifies it as non-tick text. It keeps any successful neighboring
reading. A new mixed-recognition regression failed before the change; both the
unreadable and non-tick variants pass afterward. The application regression
also confirms that this warning blocks automatic export. This revision has a
new cache and adapter identity and remains unapproved. The 20-image figures
above describe the preceding candidate; the new revision has not received a
separate real-image comparison.

After the code change, 851 OCR tests passed with 13 skipped, 777 application
tests passed, and 170 integration tests passed with seven skipped. A subsequent
focused run passed both unreadable and non-tick cases. The diagnostic Release
build succeeded without warnings. No model training or release approval was
performed.
