import json
from pathlib import Path
import tempfile
import unittest

import cv2
import numpy as np

from encode_recording import encode_session


class RecordingTests(unittest.TestCase):
    def test_video_preserves_duration_and_holds_frames_across_gaps(self):
        with tempfile.TemporaryDirectory() as temporary:
            session = Path(temporary)
            (session / 'session.json').write_text(json.dumps({'fps': 10, 'duration': 1.5}))
            frames = []
            for i, (time, shape, colour) in enumerate(((0.0, (120, 160, 3), (0, 0, 255)),
                                                       (1.0, (240, 160, 3), (0, 255, 0)))):
                filename = f'{i:06}.jpg'
                cv2.imwrite(str(session / filename), np.full(shape, colour, dtype=np.uint8))
                frames.append(json.dumps({'file': filename, 'time': time}))
            (session / 'frames.jsonl').write_text('\n'.join(frames))
            output = encode_session(session)
            video = cv2.VideoCapture(str(output))
            try:
                self.assertTrue(video.isOpened())
                self.assertEqual(int(video.get(cv2.CAP_PROP_FRAME_COUNT)), 15)
                self.assertEqual(int(video.get(cv2.CAP_PROP_FPS)), 10)
                for index in range(15):
                    ok, image = video.read()
                    self.assertTrue(ok)
                    self.assertEqual(image.shape[:2], (120, 160))
                    self.assertEqual(int(np.argmax(image[60, 80])), 2 if index < 10 else 1)
            finally:
                video.release()
            self.assertTrue((session / '000000.jpg').exists())
            self.assertFalse((session / 'performance.partial.mp4').exists())

    def test_no_frames_does_not_claim_success(self):
        with tempfile.TemporaryDirectory() as temporary:
            session = Path(temporary)
            (session / 'session.json').write_text('{"fps":10,"duration":1}')
            (session / 'frames.jsonl').write_text('')
            with self.assertRaisesRegex(ValueError, 'No captured frames'):
                encode_session(session)
            self.assertFalse((session / 'performance.mp4').exists())

    def test_missing_frame_is_reported(self):
        with tempfile.TemporaryDirectory() as temporary:
            session = Path(temporary)
            (session / 'session.json').write_text('{"fps":10,"duration":1}')
            (session / 'frames.jsonl').write_text('{"file":"missing.jpg","time":0}')
            with self.assertRaisesRegex(ValueError, 'Missing or invalid'):
                encode_session(session)


if __name__ == '__main__':
    unittest.main()
