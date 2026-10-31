# Manual train-only source capture

This isolated utility prepares a source-bound manifest of positive curve-point centers from the fixed `real-dev` inventory after applying the existing study-level train/development partition. It is source preparation only. It does not call OCR, create features, train a model, or authorize use in production.

Before opening payloads, it verifies the externally supplied SHA-256 of `protocol.json`, the exact source-file set and every bound source byte. It then reads only projects whose study partition is `Train`; the 16 development project count is retained as metadata, with zero development payload reads. Every selected `.dig` is opened read-only with `FileShare.Read`, verified against the selected path through the existing handle check, size-bounded before hashing, parsed on that same stream, and hashed again before the source row is emitted. The full metadata inventory is checked again after the train reads. The success manifest binds an append-only `capture-attempt.jsonl` journal with per-source start/completion/failure events. It contains anonymized case IDs, hashes and error types, but no source paths or exception messages. Source hashes are first-observed during the run, not independently preregistered per-source.

The rows contain screen-space centers from annotated curve points. Their relationship to marker glyph pixels has not been verified. The manifest therefore does not claim glyph-aligned positives and contains no negative labels. Duplicate embedded images require grouping before any downstream split. Cross-split image overlap has not been checked. Candidate authorization and training admission remain false.

## Commands

Run from the project root after review pins `protocol.json` and gives its SHA-256:

```powershell
dotnet run --project tools/GraphReader.ManualTrainOnlyCapture/GraphReader.ManualTrainOnlyCapture.csproj -c Release -- --self-test --expected-protocol-sha256 <protocol-sha256>
```

The capture command is deliberately separate and requires a new output path under `artifacts/goal22-runs`:

```powershell
dotnet run --project tools/GraphReader.ManualTrainOnlyCapture/GraphReader.ManualTrainOnlyCapture.csproj -c Release -- --capture --expected-protocol-sha256 <protocol-sha256> --output artifacts/goal22-runs/<new-run>/manifest.json
```

No real capture command has been run as part of this implementation task.
