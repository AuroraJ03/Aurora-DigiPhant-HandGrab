# DigiPhant setup — UnityTest

## Actual local setup

- **Project root:** the local Unity project folder
- **Operating system:** Windows
- **Unity:** 6000.6.3f1
- **Render pipeline:** Universal Render Pipeline 17.6.0
- **Starter:** `https://github.com/kommanderpi/studentstarter`, cloned as `DigiPhantStarter` beside `Assets`
- **Starter revision:** `5adcbea1f7b311b8bb15c17b3dc4742ae8fff3af`
- **Python:** 3.14.7 in the local Python installation
- **Tracking environment:** `DigiPhantStarter\Tracking\.venv`
- **Installed packages:** MediaPipe 0.10.35; OpenCV 4.14.0.94

## What was integrated

The original URP `Assets/Scenes/SampleScene.unity` has been preserved unchanged.

- Imported the starter's `Elephant` folder and `Elephant.meta` into `Assets/Elephant`.
- Imported the starter's `DigiPhant` folder and `DigiPhant.meta` into `Assets/DigiPhant`.
- Kept `DigiPhantStarter/Tracking` outside `Assets`, as required by the automatic Unity Editor launcher and recorder.
- Created the isolated Python environment used by the launcher and MP4 exporter.
- Added a lightweight Unity `.gitignore`; it excludes Unity caches, recordings, virtual environments, the starter clone, and the large shared Elephant assets. It deliberately does **not** ignore `.meta`, `.unity`, `.asset`, project settings, or adapted DigiPhant code.

Open the supplied reference scene at:

`Assets/DigiPhant/Scenes/DigiPhant.unity`

It already contains the elephant, stage, controller, local UDP receiver, preview panel, ten-second neutral-pose calibration, locomotion, and the Editor-only Game-view recorder. The expected local data path is:

`webcam -> MediaPipe bridge.py -> UDP 127.0.0.1:5055 -> DigiPhant controller -> elephant rig`

The bridge also sends a compressed preview to port 5057. Unity sends performer/mode commands from port 5055 to the bridge on port 5056. Use only one DigiPhant bridge/session on this port set at a time.

### Windows camera and UDP reliability patch

The local starter clone includes the tested `Tracking/bridge.py` update recorded in
`TrackingPatches/windows-camera-udp-robustness.patch`. It prevents the Windows
`WinError 10054` UDP reset from terminating tracking, tries DirectShow before
Media Foundation/default OpenCV capture, requires a short sustained camera
warm-up, and retries/recoveries from brief empty-frame failures. The patch is
tracked outside the ignored starter clone so a fresh
checkout can reproduce it.

## Starting a session

1. In Unity, open `Assets/DigiPhant/Scenes/DigiPhant.unity` and press **Play**.
2. The Editor should automatically run `DigiPhantStarter/Tracking/.venv/Scripts/python.exe bridge.py` with camera index 0. On Windows, allow camera access if prompted.
3. First choose **1** performer and **Seated / upper body**. Keep shoulders and hands visible, then select **Camera**.
4. Wait for a live preview and P1 assignment. Click **Set neutral pose (10 seconds)** and hold a comfortable resting pose until it succeeds.
5. Enable locomotion if you want travel. In the reference mapping, raising P1's left hand moves forward, lowering it moves backward, and shoulder tilt in seated mode steers. Use **Test sliders** before live tracking to inspect the rig safely.
6. If identities are wrong, use **Reassign people**, wait for the labels, then calibrate again. Changing person count or movement mode also clears calibration.

For three performers, the default roles are P1 front legs/travel/steering, P2 rear legs/tail, and P3 head/trunk/ears. Initial assignment is left-to-right in the unmirrored camera view.

## Recording

After a successful camera calibration, use **Record performance** and keep the Unity **Game** view visible. Stop with **Stop recording and save**. The expected output is:

`Recordings/<session>/performance.mp4`

The recorder includes the Game view and camera preview but no audio. Check the MP4 before uploading it to the team Drive folder; recording does not upload anything automatically. If encoding must be retried, run:

```powershell
& .\DigiPhantStarter\Tracking\.venv\Scripts\python.exe .\DigiPhantStarter\Tracking\encode_recording.py --session ".\Recordings\<session>"
```

## Verification performed

- Confirmed the project uses URP and a compatible Unity 6000.6 editor version.
- Confirmed the Elephant prefab, reference scene, and their `.meta` files are present.
- Confirmed every imported `Assets/Elephant` and `Assets/DigiPhant` file matches its cloned source byte-for-byte.
- Created the isolated Python 3.14 environment and installed the pinned packages successfully; `pip check` reports no broken requirements.
- Ran all Python tests successfully: **18 passed**. These cover tracking assignment and feature calculations, Windows camera backend fallback/recovery, UDP reset handling, JPEG preview sizing/transport, and MP4 frame-timing export behavior.

The starter's `asset-checksums.json` reports 87/124 mismatches, but all 87 match after CRLF→LF normalization; the pose model matches exactly. This is Windows line-ending conversion, not upstream drift. Direct source-to-destination checks for the imported files passed, but Unity Editor/live behavior is still pending below.

## Remaining manual checks

These require the actual Unity Editor, a camera, and performers, and have not been claimed as completed:

- Let Unity import/compile the new assets and confirm the Console has no errors.
- Open the reference scene and confirm its URP materials render correctly.
- Grant camera permission, verify camera index 0 or change it in the **DigiPhant Camera Preview** component.
- Run a solo seated-mode preview, neutral calibration, and one elephant movement response.
- Test tracking loss/recovery and three-person assignment/calibration.
- Record a short Game-view take, play the produced MP4, then upload the checked video to Drive.

### Runtime controls and failure behavior

- **Stop Play:** stopping Play Mode stops the bridge only when Unity launched and owns that bridge process; it also stops the Unity-side session and recorder. If the bridge was started independently in a terminal, close it separately with **Q**; Unity cannot reliably terminate a process it did not launch.
- **Retry:** the UI **Retry** action restarts/reconnects the tracking bridge. Recheck camera permission and calibration after reconnecting; calibration is intentionally cleared when the performer count or movement mode changes.
- **Tracking loss/stale data:** the controller should hold a neutral/safe state rather than inventing a new pose when landmarks or UDP packets are stale or lost. Confirm this manually with the camera covered and after recovery.

## Reconstructing this project later

Fresh-checkout reconstruction is still pending a clean end-to-end test. The intended process is:

1. Clone `https://github.com/kommanderpi/studentstarter.git` beside `Assets` as `DigiPhantStarter` and check out `5adcbea1f7b311b8bb15c17b3dc4742ae8fff3af`.
2. Restore **only** the omitted shared `Elephant` assets and any other explicitly omitted starter files into their documented destinations, preserving metadata. Do not bulk-copy the starter's `Assets` tree over the tracked/adapted `Assets/DigiPhant` folder.
3. Apply `git -C DigiPhantStarter apply ../TrackingPatches/windows-camera-udp-robustness.patch`. This restores the tested Windows camera/UDP bridge adaptation and its tests.
4. Create `DigiPhantStarter/Tracking/.venv` with Python 3.14, install `Tracking/requirements.txt`, and keep the bundled `pose_landmarker_full.task` in place.
5. Open `Assets/DigiPhant/Scenes/DigiPhant.unity`; retain future student adaptations in Git, ideally under `Assets/StudentWork`.
