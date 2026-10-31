import copy, importlib.util, unittest
from pathlib import Path

PATH=Path(__file__).with_name('train_labels_v2.py')
spec=importlib.util.spec_from_file_location('axis_train_labels_v2_test',PATH)
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)

class AnnotationInventoryTests(unittest.TestCase):
    def test_exact_inventory_and_path_traversal_rejected(self):
        rows=[{'scene_id':str(i),'path':module.ANNOTATION_DIR+str(i)+'.json','sha256':'a'*64} for i in range(64)]
        module.validate_annotation_entries(rows)
        for variant in ('dotdot','duplicate_path','duplicate_scene','missing','extra'):
            data=copy.deepcopy(rows)
            if variant=='dotdot':data[2]['path']=module.ANNOTATION_DIR+'../reserved/2.json'
            if variant=='duplicate_path':data[2]['path']=data[1]['path']
            if variant=='duplicate_scene':data[2]['scene_id']=data[1]['scene_id']
            if variant=='missing':data.pop()
            if variant=='extra':data.append(copy.deepcopy(data[0]))
            with self.subTest(variant=variant),self.assertRaises(ValueError):module.validate_annotation_entries(data)

if __name__=='__main__':unittest.main()
