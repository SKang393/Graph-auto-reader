<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Whole-workflow project runtime identity

Whole-workflow acceptance already records all project assemblies. Production
approval previously compared only App, OCR and Inference with the current
runtime. A changed axis, marker or export module could therefore reuse evidence
from older code.

The gate now compares all twelve project assemblies with the bytes actually
loaded from the application directory. The existing two managed dependencies
and two native ONNX Runtime files remain checked. A missing, duplicated or
changed descriptor fails approval.

All **682 App tests pass**, with zero failures or skips. Both the application
test build and native evaluation-tool build have zero warnings and errors.
The guarded checks took **85.1368 seconds**. Each project assembly has regression
coverage for exact, missing, duplicate and changed descriptors.

The [evidence report](GOAL-22-WHOLE-WORKFLOW-RUNTIME-BINDING.json) records exact
commands, tested source hashes, build logs and the test result. This verifies
runtime identity enforcement, not model accuracy or acceptance. No private or
sealed input, inference, model activation or package was involved. No dependency
or external asset was added; project code and tests use Apache-2.0.

Goal22 remains **FAIL**. Composed OCR approval compatibility, accuracy gates,
real acceptance, production activation and final distributions remain open.
