import struct,unittest
from ml.axis.candidate_ranker_foundation_v1.bind_panelization import png_dimensions
class PngHeaderTests(unittest.TestCase):
 def test_valid_header_dimensions(self):
  self.assertEqual(png_dimensions(b'\x89PNG\r\n\x1a\n'+struct.pack('>I',13)+b'IHDR'+struct.pack('>II',1200,350)),(1200,350))
 def test_invalid_or_oversized_rejected(self):
  for w,h in [(0,100),(9000,100),(8000,8000)]:
   b=b'\x89PNG\r\n\x1a\n'+struct.pack('>I',13)+b'IHDR'+struct.pack('>II',w,h)
   with self.assertRaises(ValueError):png_dimensions(b)
  with self.assertRaises(ValueError):png_dimensions(b'not png')
if __name__=='__main__':unittest.main()
