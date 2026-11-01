<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Direct reread of workflow-emitted Y-tick boxes

The pinned candidate server recognizer directly reread 114 of the 115 frozen
workflow Y-tick boxes from the original source images. One box was excluded
because even the standard one-pixel padding would cross its panel boundary.
The direct cropper used horizontal orientation; the full workflow may have
used different crop geometry, orientation, batching, or postprocessing. The
images, labels, and earlier workflow readings were already exposed.

| Measurement | Full workflow | Direct standard padding | Direct zero horizontal padding |
| --- | ---: | ---: | ---: |
| Complete readable numeric ticks admitted | 104 | 104 | 104 |
| Exact and numerically correct primary readings | 102 | 104 | 103 |
| Prior workflow errors corrected | Not applicable | 2 | 2 |
| Previously correct workflow readings made numerically wrong | Not applicable | 0 | 1 |

The excluded region was the remaining complete readable numeric tick. Both
direct standard-padding reads corrected the previously observed wrong workflow
values on their boxes. This comparison does not establish the cause, prove
that full-workflow tensors were identical, or justify silently replacing an
earlier reading. The review was assistant-produced and not independently
adjudicated.

All five partial-number crops, the graph-structure crop, and the uncertain crop
still produced numeric primaries in both direct runs. Of three other-text
fragments, two produced numeric primaries with standard padding and one with
zero horizontal padding. These crops remain unscored as complete labels. The
comparison does not measure missed tick boxes, complete calibration, or release
acceptance. The recognizer remains unapproved and production settings and
review rules were not changed.

| Evidence | SHA-256 |
| --- | --- |
| Comparison protocol | `705f33cf465a75d4f5a41dc39f224f92e732d37c8e40ec4970480a72ed7fa4c5` |
| Pre-run registration | `fb835497dabee26bd65c4a174bda89a787bdb17b54687ae1df4096a61ca700ea` |
| Truth-free crop input / exclusions | `95bf2f5e3bec1ba4db708242850fc98d56a7a5b17cacf86f8e7633a1376dc5ad` / `52ffb758ee270cb9be33510fde6d25e77019e25f8efd89b706868ade4afe1ab1` |
| Runtime description before and after | `5e743332dd6e7cc95fddcbe98a4553f06d77e8a4c2348c64d0c3e53b21d420a4` |
| Standard / zero-padding reread | `0b8031b86e7849c3c8381c7357add8b994999a92b00f457a0357c0bb3349ed7c` / `e3b5eacc33c2046fd30a784e49bfdd75518e7f26adcf73d9144d6c0b1a3ca120` |
| Aggregate comparison | `2f72cea43d4dd612e8ea470b185e930413c8c3619fc61fc02fe663ef2e9581ae` |

The diagnostic Release build passed with zero warnings. Three focused
preparation and scoring tests and pyflakes passed. The runs used CPU inference,
no optimizer steps, and no sealed-payload reads. Private images, transcriptions,
and per-region readings remain project-local for the active discrepancy
investigation.
