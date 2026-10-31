# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import hashlib
from io import BytesIO
import json
import unittest
from PIL import Image
from ml.synthetic.marker_context_train_capture_v1 import builder


class BuilderTests(unittest.TestCase):
    def test_two_train_rasters_and_separate_review_only_truth(self):
        binding = hashlib.sha256(builder._json(builder.source_binding())).hexdigest()
        manifest, images, truth = builder.build(binding)
        self.assertEqual(manifest['split'], 'train')
        self.assertFalse(manifest['training_approved'])
        self.assertEqual(len(manifest['sources']), 2)
        self.assertEqual({row['scene']['seed'] for row in truth}, {20820008, 20820004})
        for source in manifest['sources']:
            self.assertEqual(set(source), {'file', 'sha256', 'width', 'height'})
            self.assertEqual(hashlib.sha256(images[source['file']]).hexdigest(), source['sha256'])
            self.assertEqual(Image.open(BytesIO(images[source['file']])).size, (source['width'], source['height']))
        self.assertEqual({row['image_sha256'] for row in truth}, {row['sha256'] for row in manifest['sources']})
        self.assertTrue(all(row['overlay']['status'] == 'review_only' for row in truth))
        self.assertEqual({row['overlay']['cue_visible'] for row in truth}, {True, False})
        self.assertNotIn('annotation', json.dumps(manifest))

    def test_output_rejects_nonproject_path_before_build(self):
        with self.assertRaises(ValueError): builder.write_new('C:/outside', '0'*64)

    def test_wrong_generator_binding_is_rejected(self):
        with self.assertRaises(ValueError): builder.build('0'*64)

if __name__ == '__main__': unittest.main()
