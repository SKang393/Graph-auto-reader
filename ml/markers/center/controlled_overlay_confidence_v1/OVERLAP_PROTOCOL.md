<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Controlled patch overlap audit

The controlled build and internal holdout are source-grouped, but identical
input tensors were observed in both. Before any training, recompute the exact
three-plane and ink-plane SHA-256 for every verified patch. Report duplicate
counts without model predictions, reject conflicting controlled labels for the
same ink pixels, and keep the source-group boundary intact.

For a conservative exploratory split, retain the first build occurrence of
each ink-plane hash. From the internal holdout, exclude every ink plane seen
in build and retain the first remaining occurrence of each ink hash. Selection
follows the frozen dataset order and does not use model predictions. Record
all exclusions and retain the original dataset; the resulting small holdout
is not independent natural-image or release acceptance evidence.

This audit performs no optimizer steps, model inference, development or sealed
payload reads, or model approval. Keep patch hashes, group identities, and
selection indices in project-local ignored artifacts. Any later training needs
a separate fixed GPU-only recipe and evaluation.
