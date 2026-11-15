# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import base64
import json
import unittest
import windows_worker as worker


class Tests(unittest.TestCase):
    def test_packed_rgb_and_fixed_request_shape(self):
        request = dict(request_id="owned", width=2, height=1, rgb_base64=base64.b64encode(bytes(range(6))).decode())
        self.assertEqual(worker.decode_request(json.dumps(request).encode() + b"\n"), ("owned", 2, 1, bytes(range(6))))
        for changed in [dict(request, options={}), dict(request, width=True), dict(request, width=4097),
                        dict(request, rgb_base64="AAAA"), dict(request, request_id="bad\nidentifier")]:
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                worker.decode_request(json.dumps(changed).encode() + b"\n")

    def test_all_text_rows_retained_without_narrative(self):
        raw = b'{"scene":{"name":"not evidence"},"objects":[],"text":[{"string":"1","bbox":[1,2,3,4],"score":0.5},{"string":"+","bbox":[5,6,7,8],"score":0.3}]}'
        result = worker.response_from_raw("owned", 4, raw, 1, {})
        self.assertTrue(result["strict_json_valid"])
        self.assertIsNone(result["parse_error"])
        self.assertEqual([row["string"] for row in result["text_rows"]], ["1", "+"])
        self.assertNotIn("scene", result)
        self.assertNotIn("objects", result)
        self.assertTrue(result["requires_review"])

    def test_supplied_diagnostic_repair_cannot_validate_native_output(self):
        raw = b'{"confidence":-nan(ind),"text":[{"string":"1","bbox":[1,2,3,4],"score":null}]}'
        result = worker.response_from_raw("owned", 1, raw, 1, {})
        self.assertFalse(result["strict_json_valid"])
        self.assertEqual(result["parse_error"], "NATIVE_JSON_INVALID")
        self.assertEqual(len(result["repairs"]), 1)
        self.assertIsNone(result["text_rows"][0]["score"])

    def test_score_nan_is_diagnostic_unknown_and_bad_geometry_fails_closed(self):
        raw = b'{"text":[{"string":"1","bbox":[1,2,3,4],"score":-nan(ind)}]}'
        result = worker.response_from_raw("owned", 1, raw, 1, {})
        self.assertFalse(result["strict_json_valid"])
        self.assertEqual(result["parse_error"], "NATIVE_JSON_INVALID")
        self.assertEqual(len(result["text_rows"]), 1)
        self.assertIsNone(result["text_rows"][0]["score"])
        self.assertEqual(len(result["repairs"]), 1)
        for box in [[-1, 2, 3, 4], [1, 2, 1, 4], [1, 2, 1001, 4]]:
            result = worker.response_from_raw("owned", 1, json.dumps(dict(text=[dict(string="1", bbox=box, score=0.5)])).encode(), 1, {})
            self.assertEqual(result["parse_error"], "TEXT_BOX_INVALID")
            self.assertEqual(result["text_rows"], [])

    def test_negative_status_does_not_parse_stale_valid_json(self):
        result = worker.response_from_raw("owned", -1, b'{"text":[]}', 1, {})
        self.assertFalse(result["strict_json_valid"])
        self.assertEqual(result["parse_error"], "NATIVE_ERROR")
        self.assertEqual(result["text_rows"], [])


if __name__ == "__main__":
    unittest.main()
