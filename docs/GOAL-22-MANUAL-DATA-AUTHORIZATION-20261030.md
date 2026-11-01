# Manual data authorization and inventory disclosure

On 2026-10-30 the maintainer identified `data/manual data` and explicitly authorized using it for training or testing, while warning that the data is not perfect. This instruction supersedes the earlier blanket prohibition on training with this corpus. The user did not authorize publishing its originals or identifying details. The existing Git ignore remains in force; publisher-owned images, project files, point rows, and participant or study names are not release or repository assets.

The historical provenance record describes publisher-owned figures and maintainer-created digitizations; its tree hash is a 2026-09-02 inventory, not a newly verified hash. Before any training, bind exact selected source hashes and record the training purpose, split, and label-quality handling locally. Preserve the original files. The existing Engauge projects contain raster images, calibration anchors, and manually placed curve points, but those points should not be assumed perfect, exhaustive, or complete semantic artifact masks.

## Reserved real split disclosure

The existing frozen assignment contains 120 development and 51 sealed projects grouped by study. During the 2026-10-30 inventory, all 171 `.dig` payloads were opened and parsed to count curves, points, and embedded-image presence before the frozen split was checked. Samples were also inspected for XML structure. The displayed results were corpus-level counts and format information; no model inference, candidate comparison, training, or formal sealed-gate runner was executed. This inventory is not acceptance evidence, and the sealed set can no longer be described as previously unread.

Do not train on the existing sealed projects or any study assigned to the sealed split. Do not represent a later result as a pristine unread holdout without reviewing the effects of this disclosure and the existing gate's accounting. Before selecting training records, use the existing frozen study-level assignment, verify its fingerprint, and select eligible development studies only. Identical embedded images across study splits have not been ruled out. Development records used for training cannot also be presented as independent development validation; any new split must be grouped by study and documented before model selection.

Inventory-derived counts, without quality approval: 171 parsable digitizer projects in 40 study folders, 3,055 non-axis points, and 513 axis points. Loose CSV and spreadsheet exports have not been reconciled to these points. Counts alone do not establish correspondence, labeling accuracy, source licensing for redistribution, or release eligibility.

The maintainer says the participant graph images, digitizer projects, and spreadsheet or CSV exports correspond by name and target. Treat that as source context to verify, not proof that every exported row matches a particular project or that text boxes and transcriptions are labeled.

The metadata-only training selector was run on the identified folder after this disclosure. It selected 120 development projects and validated the frozen assignment SHA-256 `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`. It did not open project payloads, run inference, or start training. Source-image overlap with the sealed studies remains unverified.

A subsequent aggregate-only audit opened those 120 selected development projects using the Engauge adapter. All 120 returned `DIG_XML_INVALID`; no anchors or points were admitted. A file-format inspection identified an Engauge doctype that the adapter then rejected. The audit did not run a model or optimizer. Its metadata selector does not freeze payload bytes against concurrent replacement; a future training loader must bind and verify its actual source contents.

After the adapter was changed to allow only the empty Engauge doctype, the same selected-development aggregate audit parsed all 120 projects, yielding 360 anchors, 120 curves, and 2,004 data points. The audit ran no model or optimizer and returned no per-case results. It establishes format compatibility, not label accuracy, complete marker truth, cross-split source-image independence, or a training authorization.

The subsequent locked-source snapshot audit used the fixed study-level rule `sha256:graphreader-engauge-real-dev-study-v1:first-byte-lt-204`: 104 train projects with 1,787 labeled points and 16 development projects with 217 points. It detected two additional projects sharing exact image hashes within the train partition, no within-development duplicate images, and no exact image hash crossing those two partitions. The repeated train images require grouping before any loss weighting. The audit ran no model or optimizer and exposed only aggregate counts. Visually identical images with different encodings and source overlap with the frozen sealed studies remain unverified; runtime masks and label quality are not approved.

On 2026-10-31 an older `--run-real-dev` OCR diagnostic was started and
interrupted before producing any aggregate or case result. That older selector
uses all 120 real-development projects, so it can overlap both parts of the
later 104/16 partition. Only ONNX initialization warnings were observed, and it
is not known whether any project payload reached inference before interruption.
Treat all 16 locked development projects as potentially exposed to that OCR
candidate, not as pristine validation for it. The old selector enumerates the
reserved sealed assignment but does not parse or infer on its 51 project
payloads. No training occurred. The partial cache remains under the ignored
project-local artifacts directory and is not acceptance evidence.

## Locked development OCR component diagnostic

A separate evaluator verified the frozen inventory and study assignment, then
opened only the 16 projects in the locked development partition. All 16
parsed: 48 axis anchors and 217 manually sampled curve points. The existing,
unapproved V8 OCR composition returned 23 usable numeric regions classified as
Y-axis ticks. Four projects supplied enough evidence for a valid Y fit and 12
did not. For the four fitted projects only, 51 sampled points had mean absolute
Y error 0.01387 graph units and 12 anchor comparisons had mean absolute error
1.8339 pixels. Graph units differ by source, and the manual points and anchors
are imperfect; these conditional figures are not an accuracy or release gate.

The evaluator used the production raster decoder, numeric parser and Y fitter,
but supplied plot bounds from the manual anchors and passed an unmasked image
to the OCR component. It did not measure automatic plot detection, the
production axis-derived mask, marker detection, or end-to-end exports. It
cannot distinguish missed tick labels from labels assigned another role. The
development partition was already potentially exposed as described above; no
training or reserved sealed project payload was read in this run.

The aggregate-only report SHA-256 is
`7c8e4ffa9c45c59af3b2731cb3aedbdd92552bfe03c010ebd8751ee2f7699e34`.
The evaluator source SHA-256 is
`2d2a75ba49445532faff5f2c782649eef831e284d75a3a558437bfdfe0774604`;
the report records project and loaded assembly hashes after inference. Build,
self-test, and split/payload preflight passed. Inference used the CPU only; no
optimizer ran and no production model was approved.

A role and geometry census on the same 16 exposed development projects found
zero eligible numeric Y-tick regions in 10 projects, one in two, and at least
two in four. Of the accepted numeric regions, 12 Y-tick regions fell both
within the oracle vertical plot extent and within 10 percent of plot width to
the left of its axis; no accepted numeric region with another role met both
conditions. The corpus has no verified tick text or boxes, so this does not
measure missed-label recall or prove why labels are absent. The census did not
count candidate proposals rejected by the OCR composition.

The aggregate census report SHA-256 is
`5305b2b5ec2cbce9ea7a688cd2f65ed607455a00ae69fef9422ff166a434771d`.
Its source SHA-256 is
`14aaf59991932d7f593a576524affbba028c26ffb3009d8f2d381fc6adb3dc82`.
Its payload and composition identities match the preceding diagnostic, but
some loaded assembly hashes differ; the runs are not claimed byte-identical.
Self-test and explicit-opt-in checks passed; this is not production acceptance.

The next census ran the exact V8 proposal detector separately and reconciled
its returned proposal IDs with the composition's outcomes on all 16 panels.
Of 110 returned proposals, 103 were accepted with recognition, seven were
rejected by the composition, and none remained unaccounted. Within the oracle
vertical extent and a left-axis band of 10 percent of plot width, there were
13 proposals: 12 accepted and one rejected. No text boxes or tick labels are
available to establish which proposals are correct. The public detector API
returns only proposals at or above its fixed 0.82 confidence floor, so the
census cannot count lower-scoring component candidates.

The source-bound aggregate report SHA-256 is
`acd38e18176153b04b7834c4a4f1c2f5f533d1988cf1bc4e24028c257edc52a4`;
the evaluator source SHA-256 is
`6b2dd111fed3b91a88d319a86e00e261fa4227c769c1e8698039f0ca4a618584`.
The self-test passed. It used the previously exposed development data and
oracle plot bounds with unmasked images; no sealed payload or training was used.
