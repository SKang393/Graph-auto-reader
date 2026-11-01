# V39 train false-positive attribution

This separate no-training diagnostic inspects only the authenticated V37 identity training groups used by the V39 fit. At the fixed `>= 0.1` threshold, it partitions negative rows into candidate false positives, false positives unique to V39 relative to frozen V27, candidate true negatives, and baseline false positives prevented by V39. It decomposes the trained confidence logit into the 64 hidden-unit output contributions and reports the units with the largest average contribution increase for false positives relative to candidate true negatives.

This is descriptive in-sample attribution, not a causal or human-readable explanation. Feature rows have group-local indexes and no source scene identity. It does not read development payloads, tune thresholds, change labels, construct an optimizer, train, or score the full production pipeline. Per-scene train/dev overlap remains unverified.

Run the focused tests:

```powershell
$env:PYTHONDONTWRITEBYTECODE = '1'
& tools\gpu-training\venv\Scripts\python.exe -B -m ml.markers.center.confidence_pairwise_fp_attribution_v1.test_attribution
```

Print the ordered source-bundle digest and hash `protocol.json` before running. Execution uses the fixed fresh output path in that protocol. The run is CPU-only and rejects an existing output.
