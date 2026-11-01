# Frozen P3 text detector on stroke-seeded crops

## Outcome

The frozen `graph_text_detector_v1` P3 ONNX model did not provide a useful
confirmation gate for stroke-seeded scanner candidates. Its confidence cutoff
was selected from the fit groups only. At the frozen 0.60 cutoff, the paired
top-region gate localized 0/52 validation tick labels at IoU 0.50 and produced
8 false-positive crops. Scanner-only retained 52/52 known positive crops and
had 22 background false positives. Precision was 0.7027 for scanner-only and
0.0000 for the P3 gate; recall was 1.0000 and 0.0000, respectively. The gate
reduced false positives by 63.6 percent but failed the predeclared requirement
of at most 5 percentage points recall loss.

The result does not justify modifying or promoting P3. P3 had already failed
its fixed selection gate at 39/72 text fixtures with 29 missed fixtures and 10
false regions, and its three-candidate budget is exhausted. This diagnostic
made no optimizer call and did not change production settings.

## Data and split

The run rendered 24 new synthetic source groups with seeds 27105100–27105123,
balanced across the four renderer styles. They were not used in prior scanner
or detector experiments. Exact crop-hash and full-panel-hash-linked sources
were kept in one component before the split. There were 23 components because
one pair of sources shared a crop hash. The deterministic fit-only selection
used 20 source groups; validation contained three fresh groups:

- `stroke-seeded-xtick-v1-p3-gate-train-27105105`
- `stroke-seeded-xtick-v1-p3-gate-train-27105114`
- `stroke-seeded-xtick-v1-p3-gate-train-27105115`

Validation had 52 positive and 22 background crops. The fit split had 174
positive and 111 background crops. The generated inventory contained 432
trainable rows. Seventy-three crop rows had exact byte hashes present in the
pinned V6 training manifest and were quarantined. One additional conflicting
image hash affected 18 rows; those rows were quarantined as well. The split
scored 359 rows after quarantine. No prior consumed probe, protected split, or
private input was opened.

The scanner-only baseline is crop-level: it treats each admitted seed-centered
crop as a proposal. It is not end-to-end scanner recall on a full panel. The
positive/background labels come from the materializer's rendered glyph-mask
bounds; ambiguous, clipped and other-text rows are excluded.

## Method and limitation

The P3 ONNX weights and official DB preprocessing/postprocessing were frozen.
The model received the generated 256×96 stroke-seeded padded crops, resized by
the production preprocessor. P3 was trained on 512×192 full-panel context
patches, so these crop inputs have a different context and effective text
scale. The result supports a train-matched crop detector if this pairing is
revisited; it does not establish that the P3 network cannot help when used on
its intended full-panel context.

The threshold grid was 0.60 through 0.90 in 0.05 increments. Fit selection
maximized F1, then precision, then the higher threshold. Validation was read
only after the score pass and threshold selection. Inference used
`CPUExecutionProvider`; there was no training. This is a small synthetic
internal source-group comparison, not product acceptance.

## Reproduction and source identity

Command:

```powershell
python -m unittest ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.p3_text_gate_v1.test_p3_text_gate -v
$env:PYTHONPATH='E:\Graph-auto-reader\tools\qa-lint'
python -m pyflakes ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/p3_text_gate_v1/p3_text_gate.py ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/p3_text_gate_v1/test_p3_text_gate.py
python ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/p3_text_gate_v1/p3_text_gate.py
```

Five focused tests passed; pyflakes reported no findings. Two source-pin
preflights and one pre-inference split-admission attempt failed before model
inference; their exact exceptions and corrections are recorded in
`artifacts/stroke-seeded-xtick-v1-p3-text-gate-v1-void-receipts.json`.

| File | SHA-256 |
|---|---|
| `p3_text_gate.py` | `1ba14b99e0122eeaa8c37b22da73ed4cafe7aad2f7d6c46d53051ac24a0a13803` |
| `recipe.json` | `02590c9a615ff331d3b233f7f33f65e3f938293e217ffdd10ceea85a65a3ec79` |
| `test_p3_text_gate.py` | `c6f74a3917030f8e4ae988ab914bfe9b22a231ee58061b30e5c123a8e94daf8e` |
| P3 ONNX | `7c9450a01b9ec3f37d609bf1894f5763ef9a6ebccbe2916ea0450b96cc81fb1f` |
| P3 result | `b9628f2fea238977f982e43d30210c434b0628bce1f52b555f7744746a905f7b` |
| V6 train manifest | `12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a` |
| report | `3147c2fbda1a391ed50680157469708076347833012be4d8c84775ce5f039b8a` |
| frozen rule | `935e22167446c41fbc6703f5d223488811c7cb5959be9cc0310f5e3150b656a2` |
| execution receipt | `79ddb3e24c991341f9b1c1f41eeae6c37cb6b05eaf95f10a063cd61e36fe312f` |
| void receipts | `6400e24ec5f78882be8a86836ca428521660ad8fe7f8a91190f73365f96bdd20` |

The execution receipt binds the exact runner, recipe, model, report and
provider. The command wrapper did not capture the operating-system process
ID; the completed execution was tool session `72948`, exit code 0.
