"""Encode a Unity recording session to MP4. No webcam or MediaPipe process needed."""
import argparse
import bisect
import json
import math
from pathlib import Path
import sys

import cv2
import numpy as np


def encode_session(session):
    session = Path(session).resolve()
    info = json.loads((session / 'session.json').read_text())
    fps = int(info['fps'])
    duration = float(info['duration'])
    if not 1 <= fps <= 30 or not math.isfinite(duration) or not 0 < duration <= 86400:
        raise ValueError('Invalid recording rate or duration')
    frames = [json.loads(line) for line in (session / 'frames.jsonl').read_text().splitlines() if line.strip()]
    if not frames:
        raise ValueError('No captured frames. Keep the Game view visible while recording.')
    times = [float(frame['time']) for frame in frames]
    if any(not math.isfinite(t) or t < 0 or t > duration for t in times) or times != sorted(times):
        raise ValueError('Invalid frame timestamps')
    paths = []
    for frame in frames:
        path = (session / frame['file']).resolve()
        if path.parent != session or not path.is_file():
            raise ValueError('Missing or invalid frame path')
        paths.append(path)
    first = cv2.imread(str(paths[0]))
    if first is None:
        raise ValueError('First captured frame cannot be decoded')
    h, w = first.shape[:2]
    width, height = max(2, w // 2 * 2), max(2, h // 2 * 2)
    output = session / 'performance.mp4'
    temporary = session / 'performance.partial.mp4'
    writer = cv2.VideoWriter(str(temporary), cv2.VideoWriter_fourcc(*'mp4v'), fps, (width, height))
    if not writer.isOpened():
        raise RuntimeError('MP4 encoder unavailable in this OpenCV installation. Captured frames are retained.')
    expected_frames = max(1, math.ceil(duration * fps))
    index = -1
    rendered = None
    try:
        # Hold the last observed frame during gaps so playback keeps real elapsed time.
        for sample in range(expected_frames):
            next_index = max(0, bisect.bisect_right(times, sample / fps) - 1)
            if next_index != index:
                decoded = cv2.imread(str(paths[next_index]))
                if decoded is None:
                    raise ValueError(f'Cannot decode {paths[next_index].name}')
                dh, dw = decoded.shape[:2]
                scale = min(width / dw, height / dh)
                rw, rh = max(1, round(dw * scale)), max(1, round(dh * scale))
                resized = cv2.resize(decoded, (rw, rh), interpolation=cv2.INTER_AREA)
                rendered = np.zeros((height, width, 3), dtype=np.uint8)
                x, y = (width - rw) // 2, (height - rh) // 2
                rendered[y:y + rh, x:x + rw] = resized
                index = next_index
            writer.write(rendered)
    finally:
        writer.release()
    if not temporary.exists() or temporary.stat().st_size == 0:
        raise RuntimeError('Encoder produced no video; captured frames are retained')
    check = cv2.VideoCapture(str(temporary))
    try:
        if (not check.isOpened() or int(check.get(cv2.CAP_PROP_FRAME_COUNT)) != expected_frames
                or not check.read()[0]):
            raise RuntimeError('Encoded video could not be read; captured frames are retained')
    finally:
        check.release()
    temporary.replace(output)
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--session', type=Path, help='Recording session directory')
    args = parser.parse_args()
    # Unity passes paths as JSON on stdin to avoid shell/path quoting issues.
    session = args.session or Path(json.load(sys.stdin)['session'])
    print(encode_session(session), flush=True)


if __name__ == '__main__':
    main()
