# Axis-role development fixtures

These generators create synthetic graph images for development of axis and
text-role handling. They do not provide model or release approval. The V3
development corpus contains 32 scenes and keeps 16 separate recipes unrendered
for a later validation run. Its development scenes include grid and frame
underlays and both full and numeric-only participant labels.

V1 and V2 reuse seeds and nested entity identifiers. Treat V2 as a standalone
corpus; combining V1 and V2 in a registry keyed by seed and text ID causes
collisions. V3 uses different seeds and verifies nested identifier disjointness
from the named prior public corpora. Distinct seeds do not establish independent
rendering families or real-world accuracy.

The V3 development manifest is recorded at
`artifacts/axis-roles-dev-corpus-v3-20261031/manifest.json` with SHA-256
`c3c735a38dac5aa893331958ba09319e7fcfac534f8122738d8d07db37f507b1`.
It binds the source, font, images, scenes, annotations, overlay records and
reserved recipes. The generator and font are shared across splits, and reserved
images have not been rendered or evaluated.
