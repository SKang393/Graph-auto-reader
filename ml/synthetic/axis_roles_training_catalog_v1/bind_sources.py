# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Generate one reviewable, non-circular source receipt; never renders."""
from pathlib import Path
from . import catalog
def main():
 c=catalog.load_catalog();files=dict(c['v4_runtime_source_bindings'])
 for path in [Path(catalog.__file__),Path(__file__),Path(__file__).with_name('__init__.py'),Path(__file__).with_name('worker.py'),catalog.PROTOCOL]:
  catalog.v4._assert_no_reparse_ancestors(path,catalog.ROOT);files[path.relative_to(catalog.ROOT).as_posix()]=catalog._sha(path.read_bytes())
 doc={'schema':'graphreader.synthetic-axis-training-source-binding.v1','catalog_sha256':catalog.PROTOCOL_SHA256,'files':dict(sorted(files.items())),'optimizer_steps':0,'training_admitted':False,'reserved_rendered':False}
 raw=catalog.v4._canonical_json(doc)
 with catalog.SOURCE_BINDING.open('xb') as f:f.write(raw)
 print('source_binding_sha256',catalog._sha(raw),'bound_files',len(files))
if __name__=='__main__':main()
