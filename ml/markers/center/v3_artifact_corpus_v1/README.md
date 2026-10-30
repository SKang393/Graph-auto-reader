# V3 train-scene artifact capability audit

`audit_train_scene(scene, split="train")` renders an in-memory scene with the
existing V3 physical renderer, records a pixel hash, and provides capability
entries for all nine prohibited-structure categories. It retains semantic
records where the renderer supplies them, but does not enumerate line
intersections. It also keeps
actual hard-negative placement records and V3 text-support audits. It accepts
only a caller declaration of the `train` split and does not read or write a
dataset. Split membership and independence remain the caller's responsibility.

The output is **not an artifact mask, dataset, evaluator, gate, or approved
training input**. The annotation lines and boxes describe source geometry, not
an exhaustive per-class final-pixel attribution. Arrow components share an
annotation record; line crossings are not exhaustively annotated. Text support
is not proof of legibility or visible ownership where ink overlaps. This audit
always reports incomplete masks and unapproved training inputs. It does not
read private or sealed data, run model inference, or train.
