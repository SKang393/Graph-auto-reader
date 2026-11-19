# Marker classifier patch context

The approved classifier consumes a 32 × 32 ink-probability patch. The workflow accepts an artifact probability below 0.5. A source proposal can contain the correct marker and still fail classification when a connecting line shifts within its crop.

## Owned raster controls

The [control runner](../tools/GraphReader.ManualOverlayCapture/marker_classifier_context_control.py) draws 32 filled-marker placements and 92 structural placements. The drawings include detached and connected diamonds and circles, crosses, dashes, corners, bends, arrowheads, brackets and a numeral. Four predetermined proposal offsets use the production bilinear sampler and the approved classifier. The test compares four views of each placement.

The centered view moves the sampler to the centroid of the single connected group of thickest ink pixels within the proposal radius. The bounded view whites out pixels outside a box extending one proposal radius from that center and retains the original sampling position. The fourth view applies both changes. Where the ink has competing centers, all four views retain the original patch. All views use the same model, radius and admission threshold.

| Patch view | Markers admitted / 32 | Structures admitted / 92 |
| --- | ---: | ---: |
| Original | 27 | 11 |
| Centered, full context | 32 | 19 |
| Bounded, original sampling position | 32 | 52 |
| Bounded and centered | 32 | 75 |

A two-pixel horizontal displacement on a connected diamond changed its artifact probability from 0.121 to 0.998. Removing the trace from the drawing and keeping the identical displacement gave a probability below 0.001. Recentering recovered all five original positive refusals. It also overturned seven arrowhead refusals and one dash refusal. Cropping away context overturned 42 structural refusals at the original sampling position and 64 after recentering.

To reproduce the owned comparison, supply the approved local classifier payload. The runner verifies its checksum and prints probabilities in the declared view and offset order.

```text
python tools/GraphReader.ManualOverlayCapture/marker_classifier_context_control.py <classifier.onnx>
```

## Nonsealed development observation

The image-only workflow ran on the original raster. The unmodified classifier-patch replay reproduced all 22 application artifact probabilities exactly. Image-only output, the alternate-view results and a separate native assisted-export receipt were hashed before independent reference scoring.

All 16 reference centers had a proposal within five source pixels before classification. The classifier rejected four at probabilities from 0.777 to 1.000. Under full-context recentering, two of the four crossed the existing cutoff. The two others still scored above 0.998. A radius-bounded crop put all four below the cutoff. Of the three rejected proposals without a reference match, one crossed on recentering and two crossed with both changes. Both of those two already had text-exclusion evidence in this graph. Owned arrow and dash controls show why that exclusion cannot establish the safety of generic recropping. The existing filled-marker vertical measurement produced no supported shift for any of the seven rejects.

The production workflow retained 15 centers. One-to-one scoring yielded 12 matches, three extras relative to the sampled reference and four missing. Image-only calibration required review for one disputed X reading, three disputed Y readings and one unassociated Y label. It produced zero numeric rows in 3.93 seconds.

The nonactivating native WPF run imported the image, exercised the unchanged image-only refusal, then used three manual calibration anchors. Review supplied one horizontal origin correction, six series reassignments, a role change withholding the separate right-hand trace, one shared-baseline link, one point acceptance and acknowledgment of 55 warnings. It supplied no vertical edits, manually placed points or phase labels. Save/reopen, scoped CSV and both audit formats preserved all 15 original Y estimates and their confidence for the saved points, and all 12 exported rows agreed with the reopened points. The two phase labels remained unconfirmed.

The assisted export matched 12 sessions, missed four and added zero. Ten Y values were within one reference pixel and two outside (tolerance 0.027335 graph units, mean absolute error 0.023951, maximum 0.084220). The native round trip took 11.1 seconds from creation of its isolated UI directory, or 19.2 seconds including candidate runtime initialization. These are single-run observations on one exposed development graph.

The comparison supports retaining the current admission rule. The unresolved classifier defect is discrimination between a filled symbol on a sloping trace and a structural junction after a modest proposal displacement. A replacement classifier or input rule needs independent development evidence on both kinds of ink.
