# Full v5 train association diagnostics

This package supports summary-only association of the authenticated public synthetic v5 train capture. It does not create trainable or negative training rows, train, or approve a model. The fixed association rule is inclusive Euclidean distance within 2.0 original-source pixels.

The capture validator checks the fixed full-capture descriptor, runtime and protocol; 64 source identities; source and panel provenance; event hashes; center coordinates; sampler inputs; and little-endian float32 tensor hashes. The production sampler emits 32×32 single-channel local and wide arrays. The bound capture has 64 completed panels and 2,022 candidates. No dev, private, or sealed inputs are admitted.

The v14 fake-sidecar integration exercised the same `truth_adapter.associate_after_freeze` sequence as the real caller: exclusive freeze creation and `fsync`, then a fake reader callback, byte/source validation, and summary-only association. Its generated sidecar contained no markers and no real annotation data. Receipt: `artifacts/goal22-runs/marker-context-train-association-v14-real-review-pending/receipt.json`, SHA-256 `5defcbbee9e7a791f92f35f14fed3ee8eb5e29163e7dd74711c294d04a6fb845`. It validated 64 sources and 2,022 candidates, recorded no real sidecar read, no trainable or negative rows, and zero optimizer steps.

The active protocol `real-association-protocol-v6.json` is SHA-256 `80dbb6e0f662e60032d845875c2bd43b053f194b68cd1a811c6d144e7a57dc47`, with status `reviewed_and_authorized`. Its real-truth mode has not been executed in this turn. The v15 output path is fresh at `artifacts/goal22-runs/marker-context-train-association-v15-real-reviewed`. Hashes bind files and inputs; they do not independently attest source-to-binary equivalence or establish model acceptance.

## Validation

```powershell
python -m pytest tools\MarkerContextFullAssociationV1\test_association.py tools\MarkerContextFullAssociationV1\test_runner_contract.py tools\MarkerContextFullAssociationV1\test_truth_association.py -q --basetemp artifacts\goal22-runs\marker-context-full-association-tests-v27
powershell -ExecutionPolicy Bypass -NoProfile -File tools\MarkerContextFullAssociationV1\launch-association.ps1 -SelfTest -LaunchProtocolSha256 376de189176cc9383091d855962ae4dd2046e13f6a95fc40f7c5b0860c0f33bf
```
