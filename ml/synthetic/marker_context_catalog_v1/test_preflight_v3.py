# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import copy
import unittest
from ml.synthetic.marker_context_catalog_v1 import catalog, preflight_v3 as p


class CoverageTests(unittest.TestCase):
    def rows(self):
        return [{'design': d, 'connection_mode': mode,
                 'counts': {'connected_positive' if mode == 'connected' else 'isolated_positive': 1,
                            'contextual_negative': 1}, 'visible_legend_glyphs': 1}
                for mode in p.MODES for d in p.DESIGNS]

    def test_fixed_train_grid_unique_seeds_and_full_design_mode_coverage(self):
        train = catalog.recipes('train')
        selected = [train[i] for i in p.INDICES]
        self.assertEqual(len({r['seed'] for r in selected}), 8)
        self.assertEqual({(r['design'], r['connection_mode']) for r in selected},
                         {(d, mode) for d in p.DESIGNS for mode in p.MODES})
        self.assertTrue(all(r['split'] == 'train' for r in selected))
        self.assertEqual(len(p.design_mode_coverage(self.rows())), 8)

    def test_missing_duplicate_empty_or_unverified_cell_rejected(self):
        rows = self.rows()
        invalid = [rows[:-1], rows[:-1] + [rows[0]]]
        empty = copy.deepcopy(rows); empty[0]['counts']['connected_positive'] = 0
        unverified = copy.deepcopy(rows); unverified[0]['visible_legend_glyphs'] = 0
        invalid.extend((empty, unverified))
        for candidate in invalid:
            with self.subTest(candidate=candidate), self.assertRaises(ValueError):
                p.design_mode_coverage(candidate)

    def test_overall_missing_bins_rejected(self):
        for key in p.REQUIRED:
            with self.assertRaises(ValueError):
                p.require_coverage({name: 1 for name in p.REQUIRED if name != key})

if __name__ == '__main__': unittest.main()
