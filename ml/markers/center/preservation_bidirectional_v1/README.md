# Bidirectional synthetic preservation foundation

This is an unregistered research foundation. It does not authorize an optimizer
run, select a candidate, alter a threshold, or approve a model. A future protocol
must fix weights, sampling and schedule, candidate initialization, budget,
source binding and evaluation before any training. The existing component-negative
penalty and mixed synthetic/real-positive objective are separate and must not be
silently removed.

The loader authenticates the fixed V37 train-feature report, the original
feature backbone and V37 checkpoint, labels and feature hashes. It exposes only
the 3,258 component-positive anchors or 8,230 family-negative anchors per view,
from the four existing identity and reflected views. Those views are related,
not independent observations. Synthetic anchor labels are not verified decoded
marker labels, and these cohorts do not establish real-world accuracy.

For confidence residual `r = candidate_logit - V37_logit`, this foundation adds
two explicitly weighted terms: mean `relu(-r)^2` for component-positive train
anchors, and mean `relu(r)^2` for family-negative train anchors. The first
discourages downward shifts on the synthetic positives; the second discourages
upward shifts on synthetic negatives. Neither guarantees a threshold decision,
postprocessing outcome or generalization. The required weights have no default
and have not been selected.

The fixed P1-versus-V37 aggregate train diagnostic is
`artifacts/goal22-runs/preservation-train-shift-20261030-v1/report.json`
(SHA-256 `a1a826e49e4848c11aaa16d4add1bb3fc45d2e4c32c055c68dea93806cd74fba`).
It used the RX 6600 XT, the existing 0.1 cutoff, no optimizer and no development,
private or sealed reads. Its source/shape attribution remains unavailable.

Run synthetic GPU tests from the project root:

```powershell
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('-m', 'unittest', 'ml.markers.center.preservation_bidirectional_v1.test_foundation', '-v')
```
