# Marker candidate tensor bridge fast verifier (V1-compatible, review only)

This isolated utility is a performance experiment for one already captured synthetic train panel. It validates the frame plane and the two masks once, then verifies each candidate's local and wide tensors individually. It loads the pinned V1 bridge source into an isolated module and delegates row matching, labels, candidate ordering, and review-only decisions to that unchanged V1 core.

The fast evidence token has a different serialized-envelope SHA from V1. The output therefore declares `sampler_input_format=full_capture_panel_shared_plane_fast_v1`, records the source-closure digest and mapping, and retains the exact tensor bytes/SHA, row identities/order/statuses, label counts, and masks. Synthetic differential tests check these semantics against V1, and the opt-in one-source test exercises the same bound truth derivation and compares review labels/statuses plus all saved tensor bytes. This does not establish full-batch parity, runtime speed, or production factory/normalization parity.

`batch_runner.py` is a separate hash-bound wrapper around the V1 train-batch accounting path. It loads an isolated copy of V1 runner source and makes only two pinned transformations: inject the fast bridge clone in place of the relative bridge import, and declare the fast evidence format. The V1 runner still performs the exhaustive 64-source input validation once, writes per-source outputs serially, and records all per-source failures. The wrapper validates the report/receipt totals and adds `fast-batch-receipt.json` binding the fast wrapper, fast verifier, V1 runner protocol, base receipt, and batch report. The output remains review-only; no negative rows or training admission are created.

Run synthetic differential and malformed-input tests from the project root:

```powershell
python -m unittest tools.MarkerCandidateTensorBridgeFastV1.test_fast_verifier -v
```

The actual saved one-source comparison is opt-in with `GRAPHREADER_RUN_CAPTURE_PARITY=1`. It uses the explicitly bound synthetic train sidecars through the unchanged V1 exhaustive inventory derivation, but materializes and compares only the fixed seed-20820000 source. It does not access dev, private, or sealed inputs and constructs no optimizer. This module is not a training interface or production approval.

The full-batch CLI is intentionally not run by tests. After independent review, the invocation shape is:

```powershell
python -m tools.MarkerCandidateTensorBridgeFastV1.batch_runner `
  --descriptor artifacts/goal22-runs/<reviewed-64-source-descriptor>.json `
  --output artifacts/goal22-runs/<fresh-fast-run-name> `
  --expected-batch-protocol-sha256 <reviewed-batch-protocol-sha256>
```

The descriptor and output names must be reviewed and fresh. This package does not generate a descriptor or run the batch automatically.
