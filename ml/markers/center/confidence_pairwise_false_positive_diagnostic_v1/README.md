# V39 train-only hard-negative diagnostic

This diagnostic compares the already-trained V39 confidence head with its frozen V27 confidence row on the authenticated V37 `:identity` training feature groups. It uses the fixed `>= 0.1` operating threshold and reports high-scoring label-0 training rows so their learned failure modes can be characterized. It does not create labels, train a model, select a threshold, or read development/private/sealed payloads.

The result is strictly in-sample. A row called a hard negative here is only a hash-authenticated synthetic training row whose existing label is zero and whose V39 score is high. It is not an independent product false positive. These feature rows have group and local-row indexes, not scene IDs, and the recorded split metadata does not verify source-level train/dev disjointness. The tool does not run geometric matching, NMS, or the production marker pipeline.

## Checks

Run the standard-library tests with the project GPU environment. They do not create an optimizer or load the training arrays except for hash-bound metadata preflight:

```powershell
$env:PYTHONDONTWRITEBYTECODE = '1'
& tools\gpu-training\venv\Scripts\python.exe -B -m ml.markers.center.confidence_pairwise_false_positive_diagnostic_v1.test_diagnostic
```

Before execution, record the protocol and ordered source-bundle hashes:

```powershell
$protocol = (Get-FileHash -Algorithm SHA256 ml\markers\center\confidence_pairwise_false_positive_diagnostic_v1\protocol.json).Hash.ToLowerInvariant()
$bundle = & tools\gpu-training\venv\Scripts\python.exe -B -m ml.markers.center.confidence_pairwise_false_positive_diagnostic_v1.diagnostic --print-source-bundle
```

The run command is:

```powershell
& tools\gpu-training\venv\Scripts\python.exe -B -m ml.markers.center.confidence_pairwise_false_positive_diagnostic_v1.diagnostic --protocol-sha256 $protocol --source-bundle-sha256 $bundle
```

It performs CPU-only inference in row chunks over the five identity groups and creates the fixed fresh output directory named in `protocol.json`. It never creates an optimizer. Existing outputs are rejected. Keep the diagnostic output only through review of the hard-negative summary, then follow `docs/ARTIFACT-RETENTION.md` for disposition. It is not a promotion or release artifact.
