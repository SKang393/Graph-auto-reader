from io import BytesIO
from types import SimpleNamespace
import pytest
from ml.synthetic.marker_context_train_corpus_v5 import launcher
from ml.synthetic.marker_context_train_corpus_v5 import plan


def test_source_locking_precedes_import_and_rejects_tampering():
    good = b'fixed'; binding = {'schema': 'marker-context-train-corpus-binding-v5', 'revision': 'marker-context-train-corpus-v5',
        'training_approved': False, 'sources': {'ml/test.py': launcher.sha(good)}}
    raw = launcher.canonical(binding); streams = []; called = []
    def lock(_):
        handle = BytesIO(good); streams.append(handle); return handle
    def importer():
        assert len(streams) == 1 and not streams[0].closed; called.append(True)
        return SimpleNamespace(source_binding=lambda: binding)
    _, held = launcher.verify_then_import(raw, launcher.sha(raw), lock, importer)
    assert called
    for handle in held: handle.close()
    called.clear()
    with pytest.raises(ValueError, match='Source file changed'):
        launcher.verify_then_import(raw, launcher.sha(raw), lambda _: BytesIO(b'changed'), importer)
    assert not called


def test_postbuild_failure_accounting_requires_bound_identity_bytes():
    rows = [{'seed': 20820000 + i} for i in range(64)]
    raw = launcher.canonical({'sources': rows})
    producer = SimpleNamespace(plan=plan)
    accounting = launcher._postbuild_accounting(producer, {'identity_sha256': launcher.sha(raw)}, raw)
    assert accounting['completed_count'] == 64 and accounting['unattempted_count'] == 0
    with pytest.raises(ValueError, match='receipt differs'):
        launcher._postbuild_accounting(producer, {'identity_sha256': '0' * 64}, raw)
