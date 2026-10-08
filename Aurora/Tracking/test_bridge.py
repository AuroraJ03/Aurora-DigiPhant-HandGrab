import errno
import unittest
from types import SimpleNamespace
from bridge import (PerformerTracker, camera_backend_candidates, extract_features,
                    is_nonfatal_udp_reset, open_camera, read_camera_frame,
                    recover_camera, requested_count, warm_camera, hand_state, hand_packets)


class FakeCamera:
    def __init__(self, frames, opened=True):
        self.frames = list(frames)
        self.opened = opened
        self.released = False
        self.settings = []

    def isOpened(self):
        return self.opened

    def read(self):
        return self.frames.pop(0) if self.frames else (False, None)

    def release(self):
        self.released = True

    def set(self, key, value):
        self.settings.append((key, value))


class FakeCv2:
    CAP_DSHOW = 700
    CAP_MSMF = 1400
    CAP_PROP_BUFFERSIZE = 38

    def __init__(self, cameras):
        self.cameras = list(cameras)
        self.calls = []

    def VideoCapture(self, *args):
        self.calls.append(args)
        return self.cameras.pop(0)


class TrackingTests(unittest.TestCase):
    def test_hand_gesture_mapping_is_deliberately_limited(self):
        self.assertEqual(hand_state('Open_Palm'), 'open')
        self.assertEqual(hand_state('Closed_Fist'), 'fist')
        self.assertEqual(hand_state('Victory'), 'unknown')

    def test_hand_packets_use_pose_anatomy_and_reject_ambiguous_hands(self):
        def point(x, y): return SimpleNamespace(x=x, y=y, presence=1., visibility=1.)
        pose = [point(.5, .5) for _ in range(33)]
        pose[15], pose[16] = point(.2, .5), point(.8, .5)
        hand = [point(.21, .5)]
        result = SimpleNamespace(hand_landmarks=[hand], gestures=[[SimpleNamespace(category_name='Closed_Fist', score=.9)]])
        packets = hand_packets(result, [(None, None, None, pose)], {1: 0}, 7)
        self.assertEqual(packets[0]['left']['state'], 'fist')
        self.assertEqual(packets[0]['right']['state'], 'unknown')
        ambiguous = [point(.5, .5)]
        self.assertEqual(hand_packets(SimpleNamespace(hand_landmarks=[ambiguous], gestures=[[SimpleNamespace(category_name='Open_Palm', score=.9)]]), [(None,None,None,pose)], {1:0}, 8)[0]['left']['state'], 'unknown')

    def test_hand_packets_keep_unknown_when_recognition_has_no_result(self):
        packets = hand_packets(None, [], {1: 0, 2: 1}, 4)
        self.assertEqual([(p['left']['state'], p['right']['state']) for p in packets],
                         [('unknown', 'unknown'), ('unknown', 'unknown')])

    def test_hand_packets_assign_multiple_performers_without_image_order(self):
        def point(x, y, quality=1.): return SimpleNamespace(x=x, y=y, presence=quality, visibility=quality)
        left_pose = [point(.5, .5) for _ in range(33)]
        right_pose = [point(.5, .5) for _ in range(33)]
        left_pose[15], left_pose[16] = point(.15, .4), point(.35, .4)
        right_pose[15], right_pose[16] = point(.65, .4), point(.85, .4)
        hands = [[point(.84, .4)], [point(.16, .4)]]  # deliberately reverse body order
        gestures = [[SimpleNamespace(category_name='Open_Palm', score=.9)],
                    [SimpleNamespace(category_name='Closed_Fist', score=.9)]]
        packets = hand_packets(SimpleNamespace(hand_landmarks=hands, gestures=gestures),
                               [(None, None, None, left_pose), (None, None, None, right_pose)],
                               {1: 0, 2: 1}, 9)
        self.assertEqual(packets[0]['left']['state'], 'fist')
        self.assertEqual(packets[1]['right']['state'], 'open')

    def test_hand_packets_reject_low_quality_pose_wrist(self):
        def point(x, y, quality=1.): return SimpleNamespace(x=x, y=y, presence=quality, visibility=quality)
        pose = [point(.5, .5) for _ in range(33)]
        pose[15] = point(.2, .5, .2)
        hand = [point(.2, .5)]
        result = SimpleNamespace(hand_landmarks=[hand], gestures=[[SimpleNamespace(category_name='Closed_Fist', score=.9)]])
        self.assertEqual(hand_packets(result, [(None, None, None, pose)], {1: 0}, 10)[0]['left']['state'], 'unknown')

    def test_hand_packets_reject_hand_outside_pose_association_radius(self):
        def point(x, y): return SimpleNamespace(x=x, y=y, presence=1., visibility=1.)
        pose = [point(.5, .5) for _ in range(33)]
        pose[15], pose[16] = point(.2, .5), point(.8, .5)
        result = SimpleNamespace(hand_landmarks=[[point(.5, .1)]],
                                 gestures=[[SimpleNamespace(category_name='Closed_Fist', score=.9)]])
        packets = hand_packets(result, [(None, None, None, pose)], {1: 0}, 11)
        self.assertEqual(packets[0]['left']['state'], 'unknown')
    def test_roles_survive_detection_reordering_and_dropout(self):
        t = PerformerTracker(3)
        self.assertEqual(t.assign([(.8, .5), (.2, .5)]), {})
        self.assertEqual(t.assign([(.8, .5), (.2, .5), (.5, .5)]), {1: 1, 2: 2, 3: 0})
        self.assertEqual(t.assign([(.51, .5), (.81, .5), (.21, .5)]), {1: 2, 2: 0, 3: 1})
        self.assertEqual(t.assign([(.52, .5), (.22, .5)]), {1: 1, 2: 0})
        self.assertEqual(t.assign([(.82, .5)]), {3: 0})

    def test_ambiguous_assignment_is_rejected(self):
        t = PerformerTracker(3)
        t.assign([(.2, .5), (.4, .5), (.8, .5)])
        self.assertEqual(t.assign([(.3, .5), (.8, .5)]), {})
        t.reset()
        self.assertIsNone(t.positions)

    def test_one_and_two_people_with_extra_bystanders(self):
        for count in (1, 2):
            t = PerformerTracker(count)
            assigned = t.assign([(.8, .5), (.2, .5), (.5, .5)])
            self.assertEqual(len(assigned), count)
            self.assertEqual(assigned[1], 1)
            self.assertEqual(t.assign([(.21, .5)]), {1: 0})

    def test_count_request_validation(self):
        for count in (1, 2, 3, 4):
            self.assertEqual(requested_count('{"version":1,"performerCount":%d}' % count), count)
        for data in ('null', '[]', 'bad', '{"version":1,"performerCount":0}',
                     '{"version":1,"performerCount":5}', '{"version":1,"performerCount":true}',
                     '{"version":1,"performerCount":"2"}', '{"version":2,"performerCount":2}'):
            self.assertIsNone(requested_count(data))

    def test_four_people(self):
        t = PerformerTracker(4)
        self.assertEqual(len(t.assign([(.1, .5), (.35, .5), (.6, .5), (.85, .5)])), 4)
        self.assertEqual(t.assign([]), {})

    def test_upper_body_works_with_no_visible_lower_body(self):
        points = [SimpleNamespace(x=.5, y=.5, presence=0., visibility=0.) for _ in range(33)]
        for i, xy in {11:(.4,.3), 12:(.6,.3), 15:(.3,.5), 16:(.7,.5)}.items():
            points[i] = SimpleNamespace(x=xy[0], y=xy[1], presence=1., visibility=1.)
        center, baseline, confidence = extract_features(points, True)
        self.assertEqual(center, (.5, .3))
        self.assertEqual(confidence, [1.] * 6)
        self.assertEqual(extract_features(points)[2][4], 0.)
        points[15].y -= .1
        _, raised, _ = extract_features(points, True)
        self.assertGreater(raised[0], baseline[0])
        self.assertEqual(raised[0], raised[2])
        self.assertEqual(raised[1], raised[3])
        points[11].y -= .05
        self.assertLess(extract_features(points, True)[1][4], baseline[4])
        points[15].visibility = 0.
        _, _, confidence = extract_features(points, True)
        self.assertEqual(confidence, [0., 1., 0., 1., 1., 0.])

    def test_features_are_translation_and_scale_invariant(self):
        points = [SimpleNamespace(x=.5, y=.5, presence=1., visibility=1.) for _ in range(33)]
        for i, xy in {11:(.4,.3),12:(.6,.3),23:(.4,.6),24:(.6,.6),15:(.2,.1),16:(.8,.2),27:(.4,.9),28:(.6,.9)}.items():
            points[i].x, points[i].y = xy
        _, first, _ = extract_features(points)
        for p in points:
            p.x = p.x * .5 + .1
            p.y = p.y * .5 + .2
        _, second, _ = extract_features(points)
        for a, b in zip(first, second):
            self.assertAlmostEqual(a, b)
        points[15].visibility = .1
        _, _, confidence = extract_features(points)
        self.assertEqual(confidence[0], .1)
        self.assertEqual(confidence[1], 1.)

    def test_windows_camera_backends_prefer_directshow(self):
        self.assertEqual(camera_backend_candidates(FakeCv2, 'Windows'),
                         [(700, 'DirectShow'), (1400, 'Media Foundation'), (None, 'default')])
        self.assertEqual(camera_backend_candidates(FakeCv2, 'Darwin'), [(None, 'default')])

    def test_camera_open_falls_back_after_empty_startup_frames(self):
        failed = FakeCamera([(False, None), (False, None)])
        working_frame = object()
        working = FakeCamera([(False, None), (True, working_frame)])
        cv2 = FakeCv2([failed, working])
        camera, frame, backend = open_camera(cv2, 0, 'Windows', warmup_reads=2,
                                              warmup_successes=1, sleep=lambda _: None)
        self.assertIs(camera, working)
        self.assertIs(frame, working_frame)
        self.assertEqual(backend, 'Media Foundation')
        self.assertEqual(cv2.calls, [(0, 700), (0, 1400)])
        self.assertTrue(failed.released)
        self.assertEqual(working.settings, [(38, 1)])

    def test_one_frame_then_stall_is_rejected_and_next_backend_is_used(self):
        one_frame_then_stall = FakeCamera([(True, object()), (False, None), (False, None), (False, None)])
        sustained = FakeCamera([(True, object()), (True, object()), (True, object())])
        cv2 = FakeCv2([one_frame_then_stall, sustained])
        camera, frame, backend = open_camera(cv2, 0, 'Windows', warmup_reads=4,
                                              warmup_successes=3, sleep=lambda _: None)
        self.assertIs(camera, sustained)
        self.assertIsNotNone(frame)
        self.assertEqual(backend, 'Media Foundation')
        self.assertTrue(one_frame_then_stall.released)

    def test_recovery_can_skip_a_failed_backend(self):
        recovered = FakeCamera([(True, object()), (True, object()), (True, object())])
        cv2 = FakeCv2([recovered])
        camera, _, backend = open_camera(cv2, 0, 'Windows', skip_backends=(FakeCv2.CAP_DSHOW,),
                                         sleep=lambda _: None)
        self.assertIs(camera, recovered)
        self.assertEqual(backend, 'Media Foundation')
        self.assertEqual(cv2.calls, [(0, FakeCv2.CAP_MSMF)])

    def test_exhausted_recovery_keeps_the_runtime_error_after_cleanup(self):
        failed_current = FakeCamera([])
        media_foundation = FakeCamera([], opened=False)
        default = FakeCamera([], opened=False)
        cv2 = FakeCv2([media_foundation, default])
        with self.assertRaisesRegex(RuntimeError, 'every backend'):
            recover_camera(cv2, 0, failed_current, 'DirectShow', set(), 'Windows', sleep=lambda _: None)
        self.assertTrue(failed_current.released)
        self.assertTrue(media_foundation.released)
        self.assertTrue(default.released)

    def test_warm_camera_requires_consecutive_frames(self):
        camera = FakeCamera([(True, object()), (False, None), (True, object()), (True, object())])
        self.assertIsNone(warm_camera(camera, reads=4, required_successes=3, sleep=lambda _: None))

    def test_frame_retries_and_udp_reset_are_nonfatal(self):
        frame = object()
        camera = FakeCamera([(False, None), (True, frame)])
        self.assertIs(read_camera_frame(camera, retries=2, sleep=lambda _: None), frame)
        self.assertTrue(is_nonfatal_udp_reset(ConnectionResetError(10054, 'reset')))
        self.assertTrue(is_nonfatal_udp_reset(OSError(errno.ECONNRESET, 'reset')))
        self.assertTrue(is_nonfatal_udp_reset(OSError(10054, 'reset')))
        self.assertFalse(is_nonfatal_udp_reset(OSError(errno.EPERM, 'permission denied')))


if __name__ == '__main__':
    unittest.main()
