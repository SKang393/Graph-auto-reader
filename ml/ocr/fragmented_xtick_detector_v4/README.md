# Fragmented X-tick detector V4 diversity experiment

V4 makes a new 288-image train-only procedural set (192 text labels, 96 graph-only backgrounds). The 12 training strings are disjoint from both the P4 train strings and the 12 strings in the existing fresh procedural probe. Text is rendered into a dedicated mask; the annotation rectangle and visible-pixel count come from that exact mask. Font families are project-local NotoSans Regular, Medium and SemiBold, at 12, 14, 16, 18, 20, 22 and 24 px. Labels vary in length, x/y location and inter-glyph gap. Graph confusers include sloped axes, ticks, polylines, and hollow marker shapes.

The training loop imports P4's spatial model, log-width-height parameterization, target assignment, fit metric and stratified sampler. V4 has its own model/recipe identity, train-only manifest admission, seeds and output. It consumes all 288 images once per epoch in 36 mixed batches of eight, with 5 or 6 positives and 2 or 3 backgrounds per batch. Run only with `tools/gpu-training/Run-GpuPython.ps1`, which is checked against exactly one RX 6600 XT before optimizer steps. The committed P4 trainer/config are unchanged.

## Commands

```powershell
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/fragmented_xtick_detector_v4/materialize.py')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('-m', 'unittest', 'ml.ocr.fragmented_xtick_detector_v4.test_v4', '-v')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/fragmented_xtick_detector_v4/train.py')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/fragmented_xtick_detector_v4/evaluate_probe.py')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/fragmented_xtick_detector_v4/train_p2.py')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/fragmented_xtick_detector_v4/evaluate_p2_probe.py')
```

The existing 96-case probe has already served as a P4 development diagnostic. Training does not read it; the separate post-training evaluator reads it unchanged. The V4 result on it is a consumed development diagnostic, not untouched acceptance. Shared canvas, grayscale rendering, NotoSans family, and graph primitives leave distribution overlap. Procedural glyphs and simple confusers do not establish performance on real or private graphs. No acceptance, production approval, or release claim follows from this experiment.

V4 P2 is a controlled follow-up to P1: it holds the P1 manifest, architecture, initialization seed, shuffle seed, optimizer, learning rate, batch size and 30-epoch schedule fixed. Its only objective change selects the 32 highest-logit non-positive cells from each image for background BCE. Positive cells use the unchanged BCE, and positive/background objectness terms retain equal weight. P2 has a distinct recipe identity/output. Admission caps reads at `limit+1`, rejects reparse/symlink ancestors, pins the manifest hash in config, and passes the exact parsed manifest-byte hash to the checkpoint and report. Failures after run reservation write a `failure.json` receipt.
