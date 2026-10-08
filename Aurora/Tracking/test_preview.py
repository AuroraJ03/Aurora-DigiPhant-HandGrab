import socket
import unittest

try:
    import cv2
    import numpy as np
except ImportError:
    cv2 = np = None

from bridge import encode_preview


@unittest.skipIf(cv2 is None, "Install Tracking requirements for JPEG tests")
class PreviewTests(unittest.TestCase):
    def test_jpeg_fits_datagram_and_preserves_landscape_and_portrait(self):
        for height, width in ((720, 1280), (1280, 720), (240, 320)):
            pixels = np.random.default_rng(42).integers(0, 256, (height, width, 3), dtype=np.uint8)
            data = encode_preview(pixels, cv2)
            self.assertIsNotNone(data)
            self.assertLessEqual(len(data), 8000)
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as receiver, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                receiver.bind(('127.0.0.1', 0))
                receiver.settimeout(1)
                sender.sendto(data, receiver.getsockname())
                self.assertEqual(receiver.recvfrom(8192)[0], data)
            decoded = cv2.imdecode(np.frombuffer(data, dtype=np.uint8), cv2.IMREAD_COLOR)
            h, w = decoded.shape[:2]
            self.assertLessEqual(w, 320)
            self.assertLessEqual(h, 240)
            self.assertAlmostEqual(w / h, width / height, delta=.01)


if __name__ == '__main__':
    unittest.main()
