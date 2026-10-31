# Model-free guest OpenCV diagnostic v1

This is a standalone component diagnostic. It does not invoke the canonical
clean-machine validator, consume host provenance, assert ISO origin, assert a
fresh installation, or claim a continuous offline history. It observes only
the machine state available when it runs.

It checks a supplied model-free portable layout, optionally loads the one
`OpenCvSharpExtern.dll`, and optionally runs `GraphReader.App.exe
--portable-smoke`. The resulting JSON always carries
`releaseEvidence: false`, `temporalLinkageClaimed: false`, and a diagnostic
schema distinct from the canonical release evidence schema.

The caller must manually transfer the payload and returned report. No VM,
disk, or network configuration is performed by this tool.

On the guest, extract the candidate ZIP to a new folder, compare its SHA-256
with the source ZIP before extraction and compare `GraphReader.App.exe` with
the candidate's recorded executable SHA. Then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Invoke-ModelFreeOpenCvGuestDiagnostic.ps1 `
  -PayloadRoot C:\GraphReader-Portable -OutputRoot C:\GraphReader-Diagnostic
```

Guest C: is allowed for both extraction and output. Copy the resulting
`C:\GraphReader-Diagnostic\model-free-opencv-guest-diagnostic.json` through a
user-operated route. Use `Get-FileHash -Algorithm SHA256` before and after
transfer for the ZIP, executable, and JSON report. Exit `0` means component
checks passed; `2` means a diagnostic receipt was written with failures; `3`
means deliberately skipped/incomplete. Exit `1` means setup failed before a
diagnostic output directory could safely be created, so no receipt is expected.
