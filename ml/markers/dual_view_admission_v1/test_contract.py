# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import copy
import unittest
from collections import Counter
from ml.markers.dual_view_admission_v1 import contract as c


def h(value): return c.digest(value.encode())


def proposal(name, x, y):
    return {'proposal_id': name, 'x': x, 'y': y, 'radius': 5,
            'local_tensor_sha256': h(name+'local'), 'context_tensor_sha256': h(name+'context')}


def fixture():
    manifest = {'schema': 'dual-view-source-identities-v1', 'match_tolerance_px': 2, 'sources': [
        {'source_id': name, 'image_sha256': h(name), 'split': split, 'width': 100, 'height': 100}
        for name, split in [('a','train'), ('b','train'), ('c','train'), ('d','dev')]]}
    manifest_bytes = c.canonical(manifest)
    capture = {'schema': 'dual-view-observed-proposals-v1', 'manifest_sha256': c.digest(manifest_bytes),
               'runtime_sha256': h('runtime'), 'coordinate_space': 'original_pixels', 'sources': [
                   {'source_id':'a', 'image_sha256':h('a'), 'status':'success', 'proposals':[
                       proposal('a1',10,10),proposal('a2',30,30),proposal('a3',50,50),proposal('a4',51,50),
                       proposal('a5',80,80),proposal('a6',90,90)]},
                   {'source_id':'b', 'image_sha256':h('b'), 'status':'success', 'proposals':[proposal('b1',5,5),proposal('b2',25,25)]},
                   {'source_id':'c', 'image_sha256':h('c'), 'status':'failed', 'proposals':[]}]}
    capture_bytes = c.canonical(capture)
    truth = {'schema':'dual-view-postcapture-truth-v1', 'capture_sha256':c.digest(capture_bytes), 'sources':[
        {'source_id':'a', 'image_sha256':h('a'), 'markers':[
            {'marker_id':'m1','x':10,'y':10,'connection':'connected'},
            {'marker_id':'m2','x':30,'y':30,'connection':'isolated'},
            {'marker_id':'m3','x':50,'y':50,'connection':'connected'}],
            'overlays':[{'overlay_id':'o1','x':80,'y':80,'status':'review_only'},
                        {'overlay_id':'o2','x':70,'y':20,'status':'review_only'}]},
        {'source_id':'b', 'image_sha256':h('b'), 'markers':[
            {'marker_id':'m4','x':5,'y':5,'connection':'connected'},
            {'marker_id':'m5','x':25,'y':25,'connection':'connected'}], 'overlays':[]},
        {'source_id':'c', 'image_sha256':h('c'), 'markers':[
            {'marker_id':'m6','x':10,'y':10,'connection':'isolated'}], 'overlays':[]}]}
    return manifest, capture, truth


def freeze(manifest, capture):
    mb, cb = c.canonical(manifest), c.canonical(capture)
    return c.freeze_capture(mb, c.digest(mb), cb, c.digest(cb))


def join(manifest, capture, truth):
    frozen = freeze(manifest, capture)
    raw = c.canonical(truth)
    return c.associate(frozen, raw, c.digest(raw))


class ContractTests(unittest.TestCase):
    def test_postcapture_association_counts_and_source_balanced_weights(self):
        manifest, capture, truth = fixture(); report = join(manifest, capture, truth)
        self.assertFalse(report['training_approved']); self.assertEqual(report['negative_training_rows'], 0)
        self.assertEqual(report['source_count'], 3); self.assertEqual(report['source_failures'], 1)
        self.assertEqual(report['proposal_counts'], {'connected_positive_pending':3,'review_isolated_positive':1,
                         'review_ambiguous':2,'review_overlay':1,'review_unmatched':1})
        self.assertEqual(report['truth_counts'], {'matched_connected':3,'matched_isolated_review':1,'review_ambiguous':1,'source_failed':1})
        a, b, failed = report['sources']; self.assertEqual(Counter(o['status'] for o in a['overlays']), {'matched_review':1,'unmatched_review':1})
        self.assertEqual([r['source_balanced_weight'] for r in a['proposals'] if r['status']=='connected_positive_pending'], [0.5])
        self.assertEqual([r['source_balanced_weight'] for r in b['proposals'] if r['status']=='connected_positive_pending'], [0.25,0.25])
        self.assertEqual(sum(p['source_balanced_weight'] for source in report['sources'] for p in source['proposals']),1.0)

    def test_truth_cannot_enter_capture_and_overlay_cannot_become_negative(self):
        manifest, capture, truth = fixture()
        altered = copy.deepcopy(capture); altered['sources'][0]['proposals'][0]['label'] = 0
        with self.assertRaises(ValueError): freeze(manifest, altered)
        altered_truth = copy.deepcopy(truth); altered_truth['sources'][0]['overlays'][0]['status'] = 'admitted_negative'
        with self.assertRaises(ValueError): join(manifest, capture, altered_truth)

    def test_binding_and_split_and_failure_guards(self):
        manifest, capture, truth = fixture()
        mb, cb = c.canonical(manifest), c.canonical(capture)
        with self.assertRaises(ValueError): c.freeze_capture(mb, '0'*64, cb, c.digest(cb))
        with self.assertRaises(ValueError): c.freeze_capture(mb, c.digest(mb), cb+b' ', c.digest(cb+b' '))
        overlap = copy.deepcopy(manifest); overlap['sources'][-1]['image_sha256'] = h('a')
        capture_overlap = copy.deepcopy(capture); capture_overlap['manifest_sha256'] = c.digest(c.canonical(overlap))
        with self.assertRaises(ValueError): freeze(overlap, capture_overlap)
        changed = copy.deepcopy(manifest); changed['match_tolerance_px'] = 3
        with self.assertRaises(ValueError): freeze(changed, capture)
        invalid = copy.deepcopy(manifest); invalid['match_tolerance_px'] = 6
        invalid_capture = copy.deepcopy(capture); invalid_capture['manifest_sha256'] = c.digest(c.canonical(invalid))
        with self.assertRaises(ValueError): freeze(invalid, invalid_capture)
        failed = copy.deepcopy(capture); failed['sources'][2]['proposals'] = [proposal('illegal', 1, 1)]
        with self.assertRaises(ValueError): freeze(manifest, failed)
        missing = copy.deepcopy(capture); missing['sources'].pop()
        with self.assertRaises(ValueError): freeze(manifest, missing)
        altered_truth = copy.deepcopy(truth); altered_truth['capture_sha256'] = h('wrong')
        with self.assertRaises(ValueError): join(manifest, capture, altered_truth)

    def test_duplicate_and_identical_review_tensors_never_promoted(self):
        manifest, capture, truth = fixture()
        capture['sources'][0]['proposals'][3]['x'] = 50
        # A positive and an overlay candidate can have identical paired inputs.
        capture['sources'][0]['proposals'][0]['local_tensor_sha256'] = capture['sources'][0]['proposals'][4]['local_tensor_sha256']
        capture['sources'][0]['proposals'][0]['context_tensor_sha256'] = capture['sources'][0]['proposals'][4]['context_tensor_sha256']
        truth['capture_sha256'] = c.digest(c.canonical(capture))
        report = join(manifest, capture, truth)
        self.assertEqual(report['proposal_counts']['review_duplicate'],2)
        self.assertEqual(report['proposal_counts']['review_conflicting_input'],1)
        self.assertEqual(report['truth_counts']['review_ambiguous'],2)
        self.assertFalse(report['training_approved'])

    def test_one_proposal_with_two_possible_truths_is_review_only(self):
        manifest, capture, truth = fixture()
        truth['sources'][0]['markers'].append({'marker_id':'m7','x':11,'y':10,'connection':'connected'})
        report = join(manifest, capture, truth)
        candidate = report['sources'][0]['proposals'][0]
        self.assertEqual(candidate['status'], 'review_ambiguous')
        self.assertIsNone(candidate['marker_id'])
        self.assertEqual(candidate['source_balanced_weight'], 0)
        self.assertEqual(report['truth_counts']['review_ambiguous'], 3)

    def test_truth_must_include_failed_sources_and_same_capture(self):
        manifest, capture, truth = fixture()
        truth['sources'].pop()
        with self.assertRaisesRegex(ValueError, 'including failures'): join(manifest, capture, truth)

if __name__ == '__main__': unittest.main()
