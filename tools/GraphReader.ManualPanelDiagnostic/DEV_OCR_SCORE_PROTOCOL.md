<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Fixed development OCR score comparisons

Before reading recognizer outputs, score the frozen assistant transcriptions
against the primary reading, preserving omitted and nonnumeric crops in the
inventory. Report mistakes and the existing positive numeric-alternative review
signal. For descriptive comparison with the earlier exposed-training report,
also tabulate numeric-alternative confidence cutoffs of 0.001 and 0.01. These
fixed comparisons do not authorize a change to the current workflow rule.
