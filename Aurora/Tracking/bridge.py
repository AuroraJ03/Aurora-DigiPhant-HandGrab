"""DigiPhant camera -> local Unity UDP bridge. Run with --help for setup options."""
import argparse
import concurrent.futures
import errno
import itertools
import json
import math
from pathlib import Path
import platform
import socket
import time
import uuid

FEATURES = ('LeftHandHeight', 'RightHandHeight', 'LeftFootLift', 'RightFootLift', 'Lean', 'ArmSpread')
MODEL_URL = 'https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_full/float16/latest/pose_landmarker_full.task'


def hand_state(category):
    """Map only the two deliberate interaction gestures; every other gesture is safe/unknown."""
    if category == 'Open_Palm':
        return 'open'
    if category == 'Closed_Fist':
        return 'fist'
    return 'unknown'


def hand_packets(result, observations, assignments, sequence, max_wrist_distance=.16):
    """Associate hand results with pose wrists, not image order or another person's hand.

    Pose landmarks 15/16 define anatomical left/right. This keeps the protocol correct
    for the unmirrored preview and rejects hands that cannot be assigned unambiguously.
    """
    packets = {slot: {'version': 1, 'performerId': slot, 'sequence': sequence,
                      'left': {'state': 'unknown', 'confidence': 0.0},
                      'right': {'state': 'unknown', 'confidence': 0.0}}
               for slot in assignments}
    if result is None:
        return list(packets.values())
    for hand_landmarks, gestures in zip(result.hand_landmarks, result.gestures):
        if not hand_landmarks or not gestures:
            continue
        wrist = hand_landmarks[0]
        candidates = []
        for slot, index in assignments.items():
            pose = observations[index][3]
            for side, landmark_index in (('left', 15), ('right', 16)):
                point = pose[landmark_index]
                quality = min(point.visibility, point.presence)
                if quality >= .5:
                    candidates.append((math.dist((wrist.x, wrist.y), (point.x, point.y)), slot, side))
        candidates.sort()
        if not candidates or candidates[0][0] > max_wrist_distance:
            continue
        # A close runner-up could mean a crossing body/hand; do not guess P1's hand.
        if len(candidates) > 1 and candidates[1][0] - candidates[0][0] < .025:
            continue
        _, slot, side = candidates[0]
        category = gestures[0]
        packets[slot][side] = {'state': hand_state(category.category_name),
                               'confidence': float(category.score)}
    return list(packets.values())


def recognize_hands(recognizer, image, observations, assignments, sequence, epoch, submitted_at):
    """Worker entry: returns the pose snapshot belonging to this hand image."""
    return recognizer.recognize(image), observations, assignments, sequence, epoch, submitted_at


def extract_features(landmarks, upper_body_only=False):
    """Body-relative image measurements; Unity subtracts the calibrated neutral pose."""
    def xy(i):
        p = landmarks[i]
        return p.x, p.y
    def quality(indices):
        return min(min(landmarks[i].visibility, landmarks[i].presence) for i in indices)
    if upper_body_only:
        ls, rs = xy(11), xy(12)
        shoulder = ((ls[0] + rs[0]) / 2, (ls[1] + rs[1]) / 2)
        scale = max(math.dist(ls, rs), .03)
        left = (shoulder[1] - xy(15)[1]) / scale
        right = (shoulder[1] - xy(16)[1]) / scale
        # Shoulder tilt replaces hip-relative lean; leg channels use hand heights.
        raw = [left, right, left, right, (ls[1] - rs[1]) / scale,
               abs(xy(15)[0] - xy(16)[0]) / (2 * scale)]
        groups = [[11, 12, 15], [11, 12, 16], [11, 12, 15],
                  [11, 12, 16], [11, 12], [11, 12, 15, 16]]
        return shoulder, [max(-3, min(3, v)) for v in raw], [quality(g) for g in groups]
    ls, rs, lh, rh = (xy(i) for i in (11, 12, 23, 24))
    shoulder = ((ls[0] + rs[0]) / 2, (ls[1] + rs[1]) / 2)
    hip = ((lh[0] + rh[0]) / 2, (lh[1] + rh[1]) / 2)
    scale = max(math.dist(shoulder, hip), .03)
    base = [11, 12, 23, 24]
    groups = [base + [15], base + [16], base + [27], base + [28], base, base + [15, 16]]
    raw = [(shoulder[1] - xy(15)[1]) / scale,
           (shoulder[1] - xy(16)[1]) / scale,
           (hip[1] - xy(27)[1]) / scale,
           (hip[1] - xy(28)[1]) / scale,
           (shoulder[0] - hip[0]) / scale,
           abs(xy(15)[0] - xy(16)[0]) / (2 * scale)]
    return hip, [max(-3, min(3, v)) for v in raw], [quality(g) for g in groups]


class PerformerTracker:
    """Small-group nearest-position assignment with ambiguity rejection.

    This is not biometric identity. Crossings/long occlusions may need manual reset.
    """
    def __init__(self, count, max_distance=.20):
        if count not in (1, 2, 3, 4):
            raise ValueError("Choose 1, 2, 3, or 4 people")
        self.count = count
        self.max_distance = max_distance
        self.positions = None

    def reset(self):
        self.positions = None

    def assign(self, centers):
        if self.positions is None:
            if len(centers) < self.count:
                return {}
            order = sorted(range(len(centers)), key=lambda i: centers[i][0])[:self.count]
            self.positions = [centers[i] for i in order]
            return {slot + 1: index for slot, index in enumerate(order)}
        candidates = []
        for assignment in itertools.product(range(-1, len(centers)), repeat=self.count):
            used = [i for i in assignment if i >= 0]
            if len(set(used)) != len(used):
                continue
            distances = [math.dist(self.positions[s], centers[i]) for s, i in enumerate(assignment) if i >= 0]
            if any(d > self.max_distance for d in distances):
                continue
            candidates.append((-len(used), sum(distances), assignment))
        candidates.sort()
        best = candidates[0]
        # Avoid arbitrary role swaps when two assignments are almost equally plausible.
        if len(candidates) > 1 and candidates[1][0] == best[0] and candidates[1][1] - best[1] < .015:
            return {}
        result = {s + 1: i for s, i in enumerate(best[2]) if i >= 0}
        for slot, index in result.items():
            self.positions[slot - 1] = centers[index]
        return result


def requested_count(data):
    """Validate Unity's local count request without changing tracking on bad input."""
    try:
        value = json.loads(data)
        if isinstance(value, dict) and value.get('version') == 1:
            count = value.get('performerCount')
            if type(count) is int and 1 <= count <= 4:
                return count
    except (ValueError, UnicodeError):
        pass
    return None


def encode_preview(frame, cv2):
    """Fit one low-latency JPEG into a local UDP datagram, preserving aspect ratio."""
    height, width = frame.shape[:2]
    scale = min(320 / width, 240 / height)
    preview = cv2.resize(frame, (max(1, round(width * scale)), max(1, round(height * scale))))
    # Stay below macOS's commonly configured 9216-byte UDP send limit.
    # JPEG size depends on scene detail, so reduce quality and then dimensions.
    for _ in range(4):
        for quality in (65, 45, 25):
            ok, encoded = cv2.imencode('.jpg', preview, [cv2.IMWRITE_JPEG_QUALITY, quality])
            if ok and len(encoded) <= 8000:
                return encoded.tobytes()
        h, w = preview.shape[:2]
        preview = cv2.resize(preview, (max(1, w // 2), max(1, h // 2)))
    return None


def camera_backend_candidates(cv2, system_name=None):
    """Return camera backends in a stable order for the current platform.

    OpenCV's Windows default (Media Foundation) can report a successful open
    while immediately returning empty frames.  DirectShow is often more
    reliable for USB and built-in cameras, so try it first there.  Other
    platforms retain OpenCV's normal default backend.
    """
    if (system_name or platform.system()) != 'Windows':
        return [(None, 'default')]
    candidates = []
    for attribute, label in (('CAP_DSHOW', 'DirectShow'),
                             ('CAP_MSMF', 'Media Foundation')):
        backend = getattr(cv2, attribute, None)
        if backend is not None and backend not in [item[0] for item in candidates]:
            candidates.append((backend, label))
    candidates.append((None, 'default'))
    return candidates


def read_camera_frame(camera, retries=1, delay_seconds=.05, sleep=time.sleep):
    """Read a frame, tolerating brief startup/dropout failures."""
    for attempt in range(retries):
        ok, frame = camera.read()
        if ok and frame is not None:
            return frame
        if attempt + 1 < retries:
            sleep(delay_seconds)
    return None


def warm_camera(camera, reads=12, required_successes=3, delay_seconds=.05, sleep=time.sleep):
    """Require consecutive frames so a one-frame camera startup is rejected."""
    successes = 0
    latest_frame = None
    for attempt in range(reads):
        frame = read_camera_frame(camera, sleep=sleep)
        if frame is None:
            successes = 0
        else:
            latest_frame = frame
            successes += 1
            if successes >= required_successes:
                return latest_frame
        if attempt + 1 < reads:
            sleep(delay_seconds)
    return None


def open_camera(cv2, index, system_name=None, warmup_reads=12, warmup_successes=3,
                skip_backends=(), sleep=time.sleep):
    """Open a camera and prove it can sustain frames before returning it.

    The returned first frame avoids discarding the successful warm-up read.
    """
    for backend, label in camera_backend_candidates(cv2, system_name):
        if backend in skip_backends:
            continue
        camera = cv2.VideoCapture(index) if backend is None else cv2.VideoCapture(index, backend)
        if not camera.isOpened():
            camera.release()
            continue
        buffer_size = getattr(cv2, 'CAP_PROP_BUFFERSIZE', None)
        if buffer_size is not None:
            camera.set(buffer_size, 1)
        first_frame = warm_camera(camera, warmup_reads, warmup_successes, sleep=sleep)
        if first_frame is not None:
            return camera, first_frame, label
        camera.release()
    return None, None, None


def is_nonfatal_udp_reset(error):
    """Windows reports an ICMP UDP port reset as WinError 10054 on recvfrom."""
    return isinstance(error, ConnectionResetError) or error.errno in (errno.ECONNRESET, 10054)


def recover_camera(cv2, index, camera, backend, failed_backends, system_name=None, sleep=time.sleep):
    """Release a failed backend and move on without masking the useful error."""
    failed_backends.add(next(key for key, label in camera_backend_candidates(cv2, system_name)
                             if label == backend))
    if camera is not None:
        camera.release()
    recovered, first_frame, recovered_backend = open_camera(
        cv2, index, system_name=system_name, skip_backends=failed_backends, sleep=sleep)
    if recovered is None:
        raise RuntimeError('Camera stopped delivering frames after retries on every backend. Close other camera apps and click Retry.')
    return recovered, first_frame, recovered_backend


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--people', type=int, choices=(1, 2, 3, 4), default=3)
    parser.add_argument('--no-window', action='store_true', help='Preview is displayed inside Unity instead')
    parser.add_argument('--upper-body-only', action='store_true', help='Use shoulders and hands; feet and hips may be out of frame')
    parser.add_argument('--camera', type=int, default=0)
    parser.add_argument('--port', type=int, default=5055)
    parser.add_argument('--model', type=Path, default=Path(__file__).parent / 'pose_landmarker_full.task')
    parser.add_argument('--gesture-model', type=Path, default=Path(__file__).parent / 'gesture_recognizer.task',
                        help='Official MediaPipe Gesture Recognizer task file (optional; pose continues if unavailable)')
    parser.add_argument('--disable-hands', action='store_true', help='Do not run the optional hand gesture pipeline')
    parser.add_argument('--download-model', action='store_true', help='Download the official model if missing')
    args = parser.parse_args()
    # The independent hand stream uses port + 3.
    if not 1024 <= args.port <= 65532:
        parser.error('--port must be between 1024 and 65532')
    if not args.model.exists():
        if not args.download_model:
            parser.error('Model missing. Run again with --download-model (internet required).')
        import urllib.request
        args.model.parent.mkdir(parents=True, exist_ok=True)
        temporary = args.model.with_suffix('.download')
        print('Downloading the official MediaPipe pose model...')
        urllib.request.urlretrieve(MODEL_URL, temporary)
        temporary.replace(args.model)
    import cv2
    import mediapipe as mp
    from mediapipe.tasks import python
    from mediapipe.tasks.python import vision

    options = vision.PoseLandmarkerOptions(
        base_options=python.BaseOptions(model_asset_path=str(args.model)),
        running_mode=vision.RunningMode.VIDEO, num_poses=4,
        min_pose_detection_confidence=.5, min_pose_presence_confidence=.5,
        min_tracking_confidence=.5)
    gesture = None
    gesture_pool = None
    hand_future = None
    latest_hands = None
    last_hand_submit = 0.0
    hand_sequence = 0
    hand_epoch = 0
    hand_session = uuid.uuid4().hex
    if not args.disable_hands:
        if not args.gesture_model.exists():
            print('Hand gestures disabled: gesture_recognizer.task is missing. Pose tracking is unaffected.')
        else:
            try:
                gesture_options = vision.GestureRecognizerOptions(
                    base_options=python.BaseOptions(model_asset_path=str(args.gesture_model)),
                    running_mode=vision.RunningMode.IMAGE,
                    # The selected Unity performer may be one of four people.  Two
                    # global detections could be consumed by bystanders before pose
                    # association gets a chance to select the intended user.
                    num_hands=8, min_hand_detection_confidence=.5,
                    min_hand_presence_confidence=.5, min_tracking_confidence=.5)
                gesture = vision.GestureRecognizer.create_from_options(gesture_options)
                gesture_pool = concurrent.futures.ThreadPoolExecutor(max_workers=1)
                print('Hand gesture recognition enabled (independent, rate-limited pipeline).')
            except Exception as error:
                print(f'Hand gestures disabled: {error}. Pose tracking is unaffected.')
                if gesture is not None:
                    gesture.close()
                gesture = None
    tracker = PerformerTracker(args.people)
    camera, pending_frame, backend = open_camera(cv2, args.camera)
    if camera is None:
        raise SystemExit('Camera could not deliver frames. Close other camera apps, check permissions, or try --camera 1.')
    print(f'Camera {args.camera} ready via {backend}.')
    sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sender.bind(('127.0.0.1', args.port + 1))
        sender.setblocking(False)
    except OSError:
        camera.release()
        sender.close()
        raise SystemExit('Another camera bridge is already running. Close its preview with Q first.')
    edges = [(11, 12), (11, 23), (12, 24), (23, 24), (11, 13), (13, 15),
             (12, 14), (14, 16), (23, 25), (25, 27), (24, 26), (26, 28),
             (27, 29), (29, 31), (27, 31), (28, 30), (30, 32), (28, 32),
             (0, 7), (0, 8), (7, 11), (8, 12)]
    colors = [(100, 230, 100), (255, 180, 80), (100, 160, 255), (220, 100, 230)]
    previous_timestamp = -1
    last_preview = 0.0
    failed_backends = set()
    print('Stand side by side, all visible. IDs start left-to-right in the unmirrored preview.')
    print('R: reset identities (then recalibrate in Unity). Q: quit.')
    try:
        with vision.PoseLandmarker.create_from_options(options) as detector:
            while True:
                for _ in range(32):
                    try:
                        data, address = sender.recvfrom(1024)
                    except BlockingIOError:
                        break
                    except OSError as error:
                        # Unity may stop/restart while this socket is bound. On
                        # Windows that can surface as WinError 10054 (an ICMP UDP
                        # reset), which is not a tracking or camera failure.
                        if is_nonfatal_udp_reset(error):
                            break
                        raise
                    if address == ('127.0.0.1', args.port):
                        try:
                            command = json.loads(data)
                            if isinstance(command, dict) and command.get('version') == 1:
                                upper = command.get('upperBodyOnly')
                                if type(upper) is bool and upper != args.upper_body_only:
                                    args.upper_body_only = upper
                                    tracker.reset()
                                    latest_hands = None
                                    hand_epoch += 1
                                    print('Movement mode changed. Set neutral pose in Unity.')
                                if command.get('reset') is True:
                                    tracker.reset()
                                    latest_hands = None
                                    hand_epoch += 1
                        except (ValueError, UnicodeError):
                            pass
                    count = requested_count(data) if address == ('127.0.0.1', args.port) else None
                    if count is not None and count != args.people:
                        args.people = count
                        tracker = PerformerTracker(count)
                        latest_hands = None
                        hand_epoch += 1
                        print(f'Unity selected {count} people. Reassigning; set neutral pose in Unity.')
                frame = pending_frame
                pending_frame = None
                if frame is None:
                    frame = read_camera_frame(camera, retries=3)
                if frame is None:
                    camera, pending_frame, backend = recover_camera(
                        cv2, args.camera, camera, backend, failed_backends)
                    print(f'Camera recovered via {backend}.')
                    continue
                rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                timestamp = max(previous_timestamp + 1, time.monotonic_ns() // 1_000_000)
                previous_timestamp = timestamp
                result = detector.detect_for_video(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb), timestamp)
                observations = []
                for landmarks in result.pose_landmarks:
                    center, values, confidence = extract_features(landmarks, args.upper_body_only)
                    if confidence[4] >= .5:
                        observations.append((center, values, confidence, landmarks))
                assignments = tracker.assign([o[0] for o in observations])
                packet = {'version': 1, 'performerCount': args.people, 'upperBodyOnly': args.upper_body_only, 'people': [dict(slot=slot, values=observations[i][1], confidence=observations[i][2])
                                                 for slot, i in assignments.items()]}
                sender.sendto(json.dumps(packet, allow_nan=False).encode(), ('127.0.0.1', args.port))
                # Gesture recognition is deliberately submitted to a separate one-worker
                # queue. Pose frames never wait for it and are still sent at camera rate.
                if gesture is not None:
                    if hand_future is not None and hand_future.done():
                        try:
                            latest_hands = hand_future.result()
                        except Exception as error:
                            print(f'Hand gesture frame ignored: {error}')
                        hand_future = None
                    monotonic = time.monotonic()
                    if hand_future is None and monotonic - last_hand_submit >= 1 / 12:
                        # Copy so the capture buffer may safely be reused while the worker runs.
                        hand_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb.copy())
                        hand_sequence += 1
                        hand_future = gesture_pool.submit(recognize_hands, gesture, hand_image,
                                                         observations, assignments, hand_sequence,
                                                         hand_epoch, monotonic)
                        last_hand_submit = monotonic
                    # Never relabel a newer pose frame with stale hand landmarks. A result
                    # expires quickly, carries its own pose association and is invalidated
                    # whenever count/mode/reassignment changes.
                    if latest_hands is not None:
                        result_hands, hand_observations, hand_assignments, sequence, epoch, submitted = latest_hands
                        if epoch == hand_epoch and monotonic - submitted <= .30:
                            packets = hand_packets(result_hands, hand_observations, hand_assignments, sequence)
                        else:
                            packets = []
                    else:
                        packets = []
                    for hand_packet in packets:
                        hand_packet['session'] = hand_session
                        sender.sendto(json.dumps(hand_packet, allow_nan=False).encode(), ('127.0.0.1', args.port + 3))
                h, w = frame.shape[:2]
                # Draw every detection, even while waiting for the full group or
                # rejecting an ambiguous identity. Only assigned bodies drive Unity.
                slots = {id(observations[i][3]): slot for slot, i in assignments.items()}
                thickness = max(2, round(max(w / 320, h / 240) * 2))
                radius = max(3, round(thickness * 1.5))
                for landmarks in result.pose_landmarks:
                    slot = slots.get(id(landmarks))
                    color = colors[slot - 1] if slot is not None else (180, 180, 180)
                    points = {}
                    for index, landmark in enumerate(landmarks):
                        if (min(landmark.visibility, landmark.presence) >= .5
                                and math.isfinite(landmark.x) and math.isfinite(landmark.y)
                                and 0 <= landmark.x <= 1 and 0 <= landmark.y <= 1):
                            points[index] = (min(w - 1, int(landmark.x * w)),
                                             min(h - 1, int(landmark.y * h)))
                    for a, b in edges:
                        if a in points and b in points:
                            cv2.line(frame, points[a], points[b], (20, 20, 20), thickness + 2, cv2.LINE_AA)
                            cv2.line(frame, points[a], points[b], color, thickness, cv2.LINE_AA)
                    for point in points.values():
                        cv2.circle(frame, point, radius + 1, (20, 20, 20), -1, cv2.LINE_AA)
                        cv2.circle(frame, point, radius, color, -1, cv2.LINE_AA)
                    anchor = points.get(11, next(iter(points.values()), None))
                    if anchor is not None:
                        label = f'P{slot}' if slot is not None else 'Unassigned'
                        font_scale = max(.6, thickness * .3)
                        cv2.putText(frame, label, anchor, cv2.FONT_HERSHEY_SIMPLEX, font_scale,
                                    (20, 20, 20), thickness + 2, cv2.LINE_AA)
                        cv2.putText(frame, label, anchor, cv2.FONT_HERSHEY_SIMPLEX, font_scale,
                                    color, thickness, cv2.LINE_AA)
                message = f'{len(assignments)}/{args.people} assigned' if args.no_window else f'{len(assignments)}/{args.people} assigned | R: reassign | Q: quit'
                if tracker.positions is None:
                    message = f'Waiting for {args.people} visible people' if args.no_window else f'Stand side by side: waiting for {args.people} visible people | Q: quit'
                cv2.putText(frame, message, (12, 26), cv2.FONT_HERSHEY_SIMPLEX, .55, (255, 255, 255), 2)
                now = time.monotonic()
                if now - last_preview >= .1:
                    jpeg = encode_preview(frame, cv2)
                    if jpeg is not None:
                        try:
                            sender.sendto(jpeg, ('127.0.0.1', args.port + 2))
                        except OSError as error:
                            # A dropped preview must not stop performer tracking.
                            if error.errno not in (errno.EMSGSIZE, errno.EAGAIN, errno.EWOULDBLOCK, errno.ENOBUFS):
                                raise
                    last_preview = now
                if not args.no_window:
                    cv2.imshow('DigiPhant tracking (unmirrored)', frame)
                key = (cv2.waitKey(1) & 0xff) if not args.no_window else -1
                if key == ord('q'):
                    break
                if key == ord('r'):
                    tracker.reset()
                    # A manual identity reset invalidates both completed and queued
                    # hand associations just like Unity's reset/count/mode commands.
                    latest_hands = None
                    hand_epoch += 1
    finally:
        sender.sendto(b'{"version":1,"people":[]}', ('127.0.0.1', args.port))
        sender.close()
        if camera is not None:
            camera.release()
        if hand_future is not None and not hand_future.done() and gesture is not None:
            # GestureRecognizer is native-backed.  Do not close it while its one
            # worker is still inferring; arrange deterministic cleanup when that
            # frame completes instead of blocking application shutdown.
            recognizer_to_close = gesture
            def close_after_hand_frame(_):
                try:
                    recognizer_to_close.close()
                except Exception:
                    pass
            hand_future.add_done_callback(close_after_hand_frame)
            gesture = None
        elif hand_future is not None:
            try:
                hand_future.result()
            except Exception:
                pass
        if gesture_pool is not None:
            # Do not cancel the in-flight task: its callback owns native cleanup.
            gesture_pool.shutdown(wait=False, cancel_futures=False)
        if gesture is not None:
            gesture.close()
        cv2.destroyAllWindows()


if __name__ == '__main__':
    main()
