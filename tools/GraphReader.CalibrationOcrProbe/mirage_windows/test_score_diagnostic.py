# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import hashlib
import json
import unittest
from score_diagnostic import parse_score_diagnostic


class Tests(unittest.TestCase):
    def test_only_observed_text_score_becomes_unknown(self):
        raw = b'{"text":[{"string":"10","bbox":[1,2,3,4],"score":0.605},{"string":"t","bbox":[972,0,1000,23],"score":-nan(ind)}]}'
        result = parse_score_diagnostic(raw)
        self.assertEqual(result["payload"]["text"][0]["score"], 0.605)
        self.assertIsNone(result["payload"]["text"][1]["score"])
        self.assertEqual(result["raw_sha256"], hashlib.sha256(raw).hexdigest())
        self.assertEqual(result["repairs"][0]["offset"], raw.index(b"-nan(ind)"))
        self.assertEqual(result["repairs"][0]["byte_offset"], raw.index(b"-nan(ind)"))
        self.assertEqual(result["repairs"][0]["text_row_index"], 1)
        self.assertTrue(result["requires_review"])
        self.assertFalse(result["production_approved"])

    def test_strings_and_unicode_offsets_are_preserved(self):
        original = 'é "score":-nan(ind)'
        raw = ('{"text":[{"string":' + json.dumps(original, ensure_ascii=False) + ',"bbox":[1,2,3,4],"score":-nan(ind)}]}').encode()
        result = parse_score_diagnostic(raw)
        self.assertEqual(result["payload"]["text"][0]["string"], original)
        self.assertEqual(len(result["repairs"]), 1)
        self.assertEqual(result["repairs"][0]["byte_offset"], raw.rindex(b"-nan(ind)"))
        self.assertNotEqual(result["repairs"][0]["offset"], result["repairs"][0]["byte_offset"])

    def test_bbox_other_fields_and_overflow_are_rejected(self):
        for raw in [b'{"text":[{"bbox":[-nan(ind),2,3,4],"score":-nan(ind)}]}',
                    b'{"x":-nan(ind),"text":[{"score":-nan(ind)}]}',
                    b'{"confidence":NaN,"text":[{"score":-nan(ind)}]}',
                    b'{"x":1e999,"text":[{"score":-nan(ind)}]}',
                    b'{"text":[{"score":Infinity}]}',
                    b'{"objects":[{"score":-nan(ind)}],"text":[]}',
                    b'{"text":[{"score":-nan(ind),"score":0.5}]}']:
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                parse_score_diagnostic(raw)

    def test_marker_collision_cannot_rewrite_original_score_strings(self):
        raw = b'{"text":[{"score":"__MIRAGE_UNKNOWN_SCORE_DIAGNOSTIC_0__"},{"score":-nan(ind)}]}'
        with self.assertRaises(ValueError):
            parse_score_diagnostic(raw)


if __name__ == "__main__":
    unittest.main()
