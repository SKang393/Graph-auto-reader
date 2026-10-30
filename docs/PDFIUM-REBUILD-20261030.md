# PDFium native rebuild, 2026-10-30

The pinned PDFium revision `2870fa9244b0f0f69fb743fab1e08deefcb07b2b` and depot_tools revision `d22ef3bf62a8c3c76d9c7427015bdfec7665587a` were synchronized under `artifacts/pdfium-source`. Two separate evidence-root builds produced the same nonempty renderer, SHA-256 `e61fa4e46ada9ae3e9040865674c35c3abdb5722031d5c9447a54155e483a468`. `Compare-PdfiumBuilds.ps1` passed, comparing the binaries and retained build inputs. The first renderer passed `Test-PdfiumRunner.ps1` with a 72-by-72 rendered page.

The new binary does **not** match the previously reviewed release-audit binary SHA-256 `efd13a38cf3cd8e04d8284a42fff42923267293170424153b1a2a96dbf6fe8ea`. Its source lock, target-dependency inventory, and PE-import hashes match the tracked review policy, but that does not transfer prior binary approval. Independent dependency and notice review, binary-specific approval, and current clean-machine evidence remain required before release staging.

The first configure initially selected the Windows Store Python launcher because the pinned depot_tools Python bootstrap had not been installed; Ninja stopped on an ICU data-generation command. The corrected build used the pinned, project-local Python 3.11.8. Source initialization now installs its exact CIPD bootstrap manifest without modifying pinned wrapper files. Build preflight verifies the manifest-derived Python path and rejects reparse points. The corrected preflight passed in PowerShell 7 and Windows PowerShell 5.1, and the CIPD ensure command succeeded with a clean depot_tools checkout.

The temporary build and smoke evidence remain under `artifacts/pdfium-source`. They are not a product installer, portable build, dependency approval, or clean-machine test.
