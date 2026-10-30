# Frozen development selection for training

The metadata-only selector reuses `FrozenRealCorpusInventory` and its fixed
study-level `real-dev` assignment. The selected studies exclude all existing
`real-sealed` studies. It does not open any `.dig` payload, score a candidate,
create a training authorization, or verify embedded-image independence across
the split. Its case and study IDs are stable, non-secret pseudonyms derived
from local paths. They can be linked or guessed and are not an anonymization
or privacy guarantee.

To print the pseudonymous metadata for the selected corpus:

```powershell
dotnet run --project tools/GraphReader.RealAcceptance.Ocr/GraphReader.RealAcceptance.Ocr.csproj `
  -c Release -- --describe-frozen-dev-training-selection <corpus-root> --explicit-opt-in
```

The JSON includes only pseudonymous case and study IDs, assignment hashes, and
`candidate_authorized: false` / `sealed_source_overlap_verified: false`. Errors
are sanitized to avoid printing local source paths. A training loader must use
`EnumerateSelectedProjects` and `RequireProject` to revalidate selected paths
and study IDs; it must separately verify source contents, resolve any
cross-split overlap, register the candidate and respect the GPU-only policy.
The selector does not provide atomic protection against a file changing after
metadata validation or certify the accuracy of the manual labels.

For an explicitly authorized local corpus, `--audit-engauge-dev-source-snapshot
<corpus-root> --explicit-opt-in` reads only the selected development projects through locked
handles and emits aggregate split and duplicate counts. It does not emit source
records or create training inputs. The deterministic split, unresolved
duplicate grouping, missing runtime masks, and unverified label quality do not
authorize training. Unlike the metadata-only command, this audit reads private
project payloads.
The corpus commands require `--explicit-opt-in` and refuse to run in CI.

Run the metadata-only synthetic self-test with:

```powershell
dotnet run --project tools/GraphReader.RealAcceptance.Ocr/GraphReader.RealAcceptance.Ocr.csproj `
  -c Release -- --self-test-frozen-dev-training-selector
```
