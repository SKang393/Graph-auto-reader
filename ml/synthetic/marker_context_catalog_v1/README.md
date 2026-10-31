# Marker context catalog, unapproved prerequisite

This package defines 64 train and 32 development recipes with disjoint seed
ranges and balanced design/connection modes. It changes the schema-valid scene
presentation, series and connection metadata together, then classifies only
already-rendered markers and validated native legend context. It does not
produce a corpus or authorize optimization.

The renderer's split field is checked against a caller-supplied expected split;
that check alone is **not** split authentication. The separately invoked
`preflight.py` additionally binds a fixed train-only recipe selection and source
inventory to the externally supplied SHA-256 of `preflight_binding_v2.json`.
It checks train/dev seed and predicted scene identities without rendering dev,
and reports the actual train scene, annotation, image and mask digests. This
is a preflight binding, not a signed receipt or training-data admission.
The two splits share generator families, and historical or external source
independence has not been proved. Empty label bins remain explicit. Native
legend text/geometry checks do not independently prove glyph visibility.

`mark_local_collisions` compares caller-provided 33-pixel float32 patch bytes.
Cross-label byte-identical patches are marked review-only while retaining their
source labels. This is not a verified production proposal preprocessor or a
claim that all ambiguous cases have been identified. Production mask planes,
crop parity, public-font provenance, GPU-only trainer admission, dev acceptance,
and release gates remain separate prerequisites.

The real-schema test renders two temporary in-memory scenes with a verified
project-local font through the existing renderer. It is a narrow test, not a
corpus, historical-disjointness check, or training approval. Its temporary font
resolver patch is scoped to the single test process and restored afterward.

The four-scene train preflight produced 48 connected positives, 37 isolated
positives and 3 contextual negatives. For each negative it removes only the
matching legend glyph on a second in-memory render and checks that visible pixels
disappear within its inclusive Pillow box. The first binding failed because its
pixel-box check excluded the inclusive right and bottom edges; the second uses
the corrected rule. No dev pixels were rendered. Source files are checked before
and after, not locked against concurrent modification; Python, native libraries,
and operating system are not fully attested. Image-level train/dev disjointness,
production crop and mask parity, and broader contextual-negative coverage remain
unverified. No raster corpus or optimizer output is materialized by this tool.

The independently repeated preflight output is recorded at
`artifacts/goal22-runs/marker-context-train-preflight-20261031-v2.log`
(SHA-256 `942628b8f687a66d2197941e8a64fecf6dff06b77a79cf5497c34731ac2b25b2`).
