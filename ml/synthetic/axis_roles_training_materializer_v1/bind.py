# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Record exact public train-source inputs before any materialization."""
from pathlib import Path
from . import materialize as m
def main():
 c=m.catalog.load_catalog();files=[Path(m.__file__),Path(__file__),Path(__file__).with_name('__init__.py')]
 for p in files:m.catalog.v4._assert_no_reparse_ancestors(p,m.ROOT)
 m.catalog.v4._assert_no_reparse_ancestors(m.A.parent,m.ROOT)
 if m.A.exists():raise ValueError('admission exists')
 if m._sha(m.catalog.SOURCE_BINDING.read_bytes())!=m.SOURCE_SHA:raise ValueError('reviewed renderer source binding differs')
 # Verify listed historical manifests only; their scene bytes are checked at materialization.
 for name,h in m.HISTORY.items():
  if m._sha((m.ROOT/'artifacts'/name/'manifest.json').read_bytes())!=h:raise ValueError('historical manifest differs')
 doc={'schema':'graphreader.axis-training-source-admission.v1','catalog_sha256':m.catalog.PROTOCOL_SHA256,'source_binding_sha256':m.SOURCE_SHA,'materializer_sources':{p.relative_to(m.ROOT).as_posix():m._sha(p.read_bytes()) for p in files},'recipes':c['recipes'],'historical_manifests':m.HISTORY,'reserved_read':False,'optimizer_steps':0,'training_admitted':False,'limits':['This binds synthetic source generation only, not training admission.','Development and reserved groups share renderer/font; no family-independence claim.']}
 raw=m._json(doc);m.A.mkdir(parents=True)
 with (m.A/'binding.json').open('xb') as f:f.write(raw)
 print('admission_sha256',m._sha(raw),'recipes',len(c['recipes']))
if __name__=='__main__':main()
