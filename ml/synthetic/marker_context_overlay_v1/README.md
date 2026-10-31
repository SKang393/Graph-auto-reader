# In-plot marker context overlay prototype

This unapproved, train-only diagnostic composes an explicitly synthetic open
circle inside a plot, optionally accompanied by the visible word `note` within
a nominal 65-pixel window. The source scene, panel, split, font, image and mask
identities are checked or recorded. Only no-op degradation and identity geometry
are accepted. Overlaps with a true-marker mask or existing ink produce an
unmaterialized review-only result. In-plot placement does not imply the glyph
is a scientific point; source provenance records its synthetic non-data origin.
Presentation, series and connection metadata must agree with the recipe. Before
composition, the exact scene is rerendered with the verified project font in an
isolated renderer namespace; raster bytes, marker-mask bytes and the complete
annotation must match. The isolated namespace avoids a process-global resolver
patch, but this check does not lock source or attest the runtime environment.

All examples, including visible-cue examples, remain review-only. The cue is
artificial and is not evidence for the origin of any real image artifact.
Valid missing-edge source markers remain positive controls. Identical model
inputs and ambiguous context require review; actual production patch, mask and
runtime preprocessing parity have not been established. Scene/output hashes
are consistency checks, not independently authenticated producer receipts.
No optimizer, dev/restricted input, model or production approval is included.
