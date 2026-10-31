# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""CPU-only authenticated synthetic mask derivation; never optimizes."""
from hashlib import sha256
import json
from pathlib import Path
import sys

from . import source_derivation as derivation

ROOT = Path(__file__).resolve().parents[3]


def generate(output: Path):
    output = output.resolve()
    if not output.is_relative_to((ROOT / "artifacts").resolve()) or output.exists():
        raise ValueError("Mask cache must be a new project artifacts directory")
    bound = derivation.load_bound_source()
    output.mkdir(parents=True, exist_ok=False)
    rows = []
    for index, sample in enumerate(bound.samples):
        payload = sample.mask.tobytes()
        if sha256(payload).hexdigest() != derivation.MASK_SHA[index]:
            raise ValueError("Derived localized mask differs")
        name = f"train-{index}.localized-mask.f32"
        with (output / name).open("xb") as stream:
            stream.write(payload)
        rows.append({"file": name, "sha256": derivation.MASK_SHA[index], "shape": list(sample.mask.shape), "dtype": "float32-le"})
    report = {"schema": "graphreader.synthetic-tick-localized-mask-cache.v1", "masks": rows,
              "preflight_sha256": bound.preflight_sha256, "model_sha256": bound.model_sha256,
              "mask_policy_sha256": bound.mask_policy_sha256,
              "derivation_source_sha256": sha256(Path(derivation.__file__).read_bytes()).hexdigest(),
              "builder_source_sha256": sha256(Path(__file__).read_bytes()).hexdigest(),
              "source_hashes_are_descriptive": True, "training_approved": False, "private_reads": 0,
              "sealed_reads": 0, "holdout_raster_or_label_reads": 0, "optimizer_steps": 0}
    payload = (json.dumps(report, sort_keys=True, indent=2, allow_nan=False) + "\n").encode()
    with (output / "manifest.json").open("xb") as stream:
        stream.write(payload)
    return sha256(payload).hexdigest()


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Expected a new project artifacts output directory")
    print(json.dumps({"status": "created_unapproved_masks", "manifest_sha256": generate(Path(sys.argv[1]))}))
