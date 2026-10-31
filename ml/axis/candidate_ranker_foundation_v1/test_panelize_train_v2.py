import ast, importlib.util, json, unittest
from pathlib import Path
from unittest import mock

PATH=Path(__file__).with_name('panelize_train_v2.py')
spec=importlib.util.spec_from_file_location('isolated_panelizer_v2',PATH)
mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod)

class BoundPanelizationTests(unittest.TestCase):
    def test_no_project_imports_before_runtime_binding(self):
        tree=ast.parse(PATH.read_text(encoding='utf-8'))
        names=[n.module or '' for n in ast.walk(tree) if isinstance(n,ast.ImportFrom)]
        self.assertFalse(any(n == 'ml' or n.startswith('ml.') for n in names))

    def test_dev_pixels_scope_rejected_before_launch(self):
        doc={'schema':'graphreader.axis-ranker-panelization-binding.v2','annotations_read':False,'dev_pixels_read':True,'reserved_read':False,'training_admitted':False,'optimizer_steps':0}
        with mock.patch.object(mod.Path,'exists',return_value=False), mock.patch.object(mod,'lock'), mock.patch.object(mod,'checked',return_value=json.dumps(doc).encode()), mock.patch.object(mod.importlib.util,'spec_from_file_location') as execute:
            with self.assertRaisesRegex(ValueError,'scope differs'):mod.run('0'*64)
            execute.assert_not_called()

if __name__=='__main__':unittest.main()
