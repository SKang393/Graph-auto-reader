# GPU-only V5 numeric and reject trainer

This is an unregistered, experimental training engine. The checked-in
`registration.json` is blocked, and the recipe grants no experiment or
production approval. The package tests never construct a model or optimizer.

## Admission contract

Training accepts only a separately reviewed registration that pins all three
inputs: the public V3 positive corpus, a separate positive V5 crop-adapter
artifact, and the separate V1 nonnumeric reject corpus. Each entry binds an
E-project `artifacts/` root, exact `manifest.json`, and SHA-256. A review receipt
must also bind the recipe, registration, engine source bundle, all manifests,
and adapter implementation hash. The one-shot output directory is fixed by
that registration; an existing or incomplete run consumes its budget and cannot
be replayed.

The V3 positive corpus currently stores variable-size RGB bounding-box crops.
That is not the V5 production `128x32` grayscale input. The trainer therefore
requires a separate versioned `graphreader.v5-component-crop-adapter.v1`
manifest that binds every original V3 sample ID and RGB crop hash to an exact
V5-sized grayscale raster. Its registration must also list the production
crop-source hashes and a reviewed transform contract covering source/output
modes, output dimensions, resize filter, crop origin, and padding. The trainer
does not resize or convert V3 crops by assumption. Until such an adapter and
both corpora are independently registered, training fails closed before model
or optimizer construction.

An eventual review receipt binds a canonical digest of every registration
field except `review_receipt_sha256`; the receipt's byte hash is then stored in
the registration. This acyclic order binds the receipt to the selected inputs
without requiring two documents to hash each other recursively.

For positive rows, the exact frozen V5 `isolate_glyphs` implementation must
produce one component per target character. Percent labels are excluded from
this numeric/reject experiment. For reject rows, the crop must be wholly
nonnumeric text, and saved little-endian component tensors must byte-match a
fresh replay through the frozen V5 component encoder. Crops with no components,
more than eight components, or any component at the V5 structural-height
short-circuit are excluded before class-13 supervision. Train and validation
must be nonempty and disjoint by source family and font.

## Fixed implementation recipe

The bounded code path uses a fixed seed, `ComponentEnsembleGlyphNet`, fourteen
classes, class 13 for reject supervision, AdamW, batch size 128, and at most 256
optimizer steps. The only allowed device is HIP `cuda:0`, device name
`AMD Radeon RX 6600 XT`, architecture `gfx1032`. There is no CPU or alternate
GPU fallback. Inputs, model parameters/buffers, gradients, and optimizer state
are checked on the selected device. AdamW uses `capturable=True` so its step
state stays on that device.

The journal reserves each step before optimizer work. A crash that leaves an
in-progress slot without a hash-matching checkpoint consumes that slot and
blocks resumption. The output path is a one-shot registry value, preventing a
fresh directory from resetting the same registered budget. The report marks
training execution separately from training approval; it never approves
production or promotion. Validation loss and component accuracy are diagnostic
only. No acceptance metric is registered here.

## Current limitations

- No corpus pair or V5 positive-crop adapter is registered.
- The public reject corpus remains independently unadmitted by this engine.
- Train/validation distributions are still synthetic and do not establish
  historical benchmark independence or real-chart generalization.
- This implementation has not executed inference, optimizer construction, or
  training. A future change to registration requires independent review and a
  new pinned recipe/source receipt.
