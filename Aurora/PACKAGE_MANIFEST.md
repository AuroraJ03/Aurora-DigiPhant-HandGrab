# Aurora DigiPhant Hand-Grab Package Manifest

This package is deliberately a feature handoff, not a complete Unity project.
Every path below is relative to this `Aurora/` folder.

## Import into the receiving Unity project

- `Import/Assets/DigiPhant/Runtime/DigiPhantHandGrab.cs`
- `Import/Assets/DigiPhant/Runtime/DigiPhantGrabbable.cs`
- `Import/Assets/DigiPhant/Editor/DigiPhantHandGrabSetup.cs`
- `Import/Assets/DigiPhant/Editor/DigiPhantTimberSetup.cs`
- `Import/Assets/DigiPhant/Editor/DigiPhantHandGrabValidation.cs`
- `Import/Assets/DigiPhant/Editor/DigiPhantTimberValidation.cs`
- `Import/Assets/DigiPhant/Generated/HandGrabCubeOrange.mat`
- `Import/Assets/DigiPhant/Generated/HandGrabPadBlue.mat`

Each Unity asset above includes its matching `.meta` file. Keep the asset and
its `.meta` file together when copying it into the receiver's project.

## Hand tracking payload

- `Tracking/bridge.py`
- `Tracking/gesture_recognizer.task`
- `Tracking/pose_landmarker_full.task`
- `Tracking/requirements.txt`
- `Tracking/test_bridge.py`
- `Tracking/encode_recording.py`
- `Tracking/test_preview.py`
- `Tracking/test_recording.py`

`Tracking/` intentionally excludes `.venv/`, `__pycache__/`, recordings, and
other generated files. Recreate the Python environment on the receiving Mac.

## Reference only — do not overwrite blindly

- `ReferenceOnly/Assets/DigiPhant/Runtime/`
- `ReferenceOnly/Assets/DigiPhant/Scenes/DigiPhant.unity`
- `ReferenceOnly/Packages/`
- `ReferenceOnly/ProjectSettings/ProjectVersion.txt`
- `ReferenceOnly/*_SETUP.md`

These files document the source integration and scene configuration. They are
provided for comparison while merging the shared controller, locomotion,
camera, scene, and package settings.

For portability, `ReferenceOnly/SAVANNAH_SETUP.md` and
`ReferenceOnly/DIGIPHANT_SETUP.md` have been adapted from the source notes:
their machine-specific Windows paths were replaced with project-relative or
portable descriptions. All other copied files are byte-identical to their
source counterparts; `SHA256SUMS.txt` verifies the packaged copies.

## Intentionally omitted

The shared elephant assets, skeleton, Timber model, other Timber materials,
FBX files, Unity Library/Temp/Logs folders, and Windows Python environment are
not included. The receiver already has the shared elephant and Timber assets.

See `SHA256SUMS.txt` to verify file integrity after transfer.
