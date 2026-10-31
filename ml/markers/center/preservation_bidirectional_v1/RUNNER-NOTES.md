# Unregistered bidirectional runner

This directory contains a research runner foundation, not an authorized training
attempt. There is no new ledger entry, candidate configuration, protocol file,
CLI or acquisition entrypoint. The canonical admission and immutable source
snapshot must be separately reviewed and registered before the runner can pass
its admission checks. No optimizer step has been run by these tests.

The runner preserves the existing fixed mixed synthetic/real-positive loss,
component-negative upward-shift penalty, V37 frozen local model and fresh
zero-initialized residual. It adds weight 1.0 for component-positive downward
shifts and weight 1.0 for family-negative upward shifts. These draft weights
and sampling choices have not been validated for development or real data.

Eight epochs of 283 steps give exactly 2,264 optimizer steps. Each step reserves
the existing mixed and component-negative batches and one aligned 128-row batch
from each directional cohort. Views cycle in the existing identity, horizontal,
vertical and both order. Independent deterministic per-epoch permutations cover
all 3,258 component-positive and 8,230 family-negative anchor rows in each view
every epoch. Smaller cohorts wrap, so rows and reflected views are repeated;
they are not independent examples. CPU random permutations are index preparation,
not CPU model training. The optimizer and all model computation require the
RX 6600 XT `cuda:0`.

All four directional views are hash-authenticated before journal claim. Source
labels are synthetic anchor labels, not guaranteed decoded-object truth. Store
contents are loaded into process memory after hash verification; no separate
operating-system attestation of process memory is claimed. The existing durable
single-use journal records a step intent before each optimizer operation and
never authorizes resume after a crash-ambiguous step. Tests use mock optimizer
calls, synthetic fixtures and authenticated synthetic train features only.

```powershell
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('-m', 'unittest', 'ml.markers.center.preservation_bidirectional_v1.test_runner', 'ml.markers.center.preservation_bidirectional_v1.test_foundation', '-v')
```
