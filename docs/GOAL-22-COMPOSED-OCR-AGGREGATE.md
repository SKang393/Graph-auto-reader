<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native aggregate scoring for composed OCR

The native evidence tool now measures raw detector regions and finished OCR
regions separately. Assembly can merge fragments or recover missed text; it
must not subtract those different region counts to invent recognition failures.
The frozen geometry and text metrics remain unchanged. An invalid source
invalidates its counter, preventing a passing score on the remaining subset.

Sixteen checks cover merging, recovery, explicit recognition failure, source
isolation, invalid inputs, poisoned counters, aggregate-only output and replay
scope. Four existing aggregate, corpus, mapping and worker self-tests also pass.
The native tool builds with zero warnings or errors. Validation takes 56.91
seconds. The first build's CA1869 analyzer failure remains recorded; caching
serializer options fixes it without changing inference or metrics.

An authenticated replay of all 23 owned synthetic sources, 39 panels and 892
text labels exactly matches the independent Python scorer, including every
raw/final geometry, text and role metric. Development finished OCR retains
181/183 matched labels, 177/183 exact readings and 174/183 correct roles.
No model inference, private or sealed read is performed by this replay.

The new command accepts only explicitly identified owned synthetic train/dev
observations with an exact input checksum. It produces aggregate results and
grants no admission or production approval. Existing sealed paths are unchanged.
The next required work is a composed in-memory source evaluator and authentic
stage evidence. This metric bridge alone does not satisfy those requirements.

The [evidence index](GOAL-22-COMPOSED-OCR-AGGREGATE.json) binds all source and
report files. No new dependency or model is introduced. Code remains Apache-2.0.
Build 433 stays 0.4.33. All four Goal 22 outcomes remain incomplete.
