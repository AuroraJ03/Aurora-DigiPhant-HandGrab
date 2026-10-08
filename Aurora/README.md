# Aurora DigiPhant Hand-Grab Handoff

This package contains Aurora's hand-controlled timber interaction for merging
into an existing DigiPhant Unity project. It is a feature handoff, **not a
complete Unity project**. The recipient already has the shared elephant,
skeleton, Timber model, base scene, and project settings. The source uses Unity
`6000.6.3f1` and URP `17.6`; keep the recipient's `Packages/` and project
settings rather than overwriting them.

## What this package does

The hand pipeline selects one performer and maps gestures to the elephant:

- left fist: lower the head and reach the trunk;
- both fists: pick up and carry a nearby cube or timber;
- either explicit, confident open hand: release;
- missing hands, Unknown, stale packets, or low confidence do not release an
  already-held object;
- right-hand open while the left remains a fist releases the object but keeps
  the reach pose.

The grab layer is an overlay. It owns only the selected head/trunk bones while
active and does not rewrite locomotion, upper-body travel, camera, pose input,
or unrelated animation channels. Existing lifecycle cleanup still releases a
held Rigidbody when Play mode stops or the component is disabled.

## Package layout

`Import/` contains these new Unity scripts and their `.meta` files:

- `Assets/DigiPhant/Runtime/DigiPhantHandGrab.cs`
- `Assets/DigiPhant/Runtime/DigiPhantGrabbable.cs`
- `Assets/DigiPhant/Editor/DigiPhantHandGrabSetup.cs`
- `Assets/DigiPhant/Editor/DigiPhantTimberSetup.cs`
- `Assets/DigiPhant/Editor/DigiPhantHandGrabValidation.cs`
- `Assets/DigiPhant/Editor/DigiPhantTimberValidation.cs`

Import them into matching folders, preserve `.meta` files, and check GUID
conflicts first. Do not regenerate GUIDs indiscriminately.

`Tracking/` represents the project-root path `DigiPhantStarter/Tracking/`,
outside `Assets`. The `.task` model files are not Unity assets and do not need
Unity `.meta` files.
Merge the bridge changes into the recipient's shared Pose/Upper-body/Face
bridge. Do not replace that bridge wholesale: the other pipelines must remain
available and should share one camera capture and one performer/identity
assignment.

`ReferenceOnly/` contains source scene/public-controller references and setup
notes. Do not copy this folder wholesale into `Assets`, and do not overwrite
the recipient's main scene, controller, or locomotion scripts. The current
scene serializes bone references and authored grip transforms; use it to
compare and manually rebind the recipient's existing elephant bones.

The shared elephant and Timber model are intentionally not duplicated here.
The Timber interaction should be added to the recipient's existing Timber
object, preserving its model, material, position, and existing scene identity.

The source controller integration expects `DigiPhantController` to expose
`PostControlsApplied` (`Action<float,float>`) after normal controls,
`PostLocomotionEvaluated` (`Action`) after locomotion evaluation, and
`TrackingReset` (`Action`) when tracking resets. It also reads the public
`inputMode` and `port` members (`port` defaults to `5055`). Compare and merge
these interfaces without replacing controller changes for walking, drinking,
or face control.

## Timber configuration

The authored target is approximately 0.5 m diameter and 3 m long. It uses a
convex collider, Rigidbody mass around 20 kg, two cut-end child surfaces, and
the grabbable registration on the timber root. Its local long axis is Y. The
pickup test accepts a bounded surface margin of about 0.2 m; it is not a
general distant magnet. When picked up, the log is smoothly corrected to a
horizontal, crosswise orientation relative to the elephant, regardless of the
approach direction. Preserve the existing carry-body pose and smoothing
parameters.

The explicit-grabbables array, pickup collider, convex mesh collider, Rigidbody,
and cut-end children must point to the recipient's existing Timber object.

The original generated cube and its exact saved dimensions are scene data, not
the Timber model. Do not blindly rerun the setup tool if it would replace an
existing authored target or create a different cube size. Rebind the script to
the recipient's Timber object and validate the actual scene.

## Three-person merge contract

The proposed roles are P1 = upper-body walking and drinking, P2 = hand grab,
and P3 = face-controlled eating. The current grab component defaults to
`performerId = 1`; set it to the agreed grab performer (normally 2) in the
recipient's scene. Setting the bridge count to 3 does not automatically assign
these roles. All three pipelines must use the same stable performer identity
and coordinate-space convention; do not treat “first detected person” as a
stable identity.

Drinking, grabbing, and eating can all affect the head/trunk region. Before
merging, agree on an explicit arbitration policy and implement one owner or a
priority/state machine. A drink/eat animation must not silently release an
existing timber hold; only the trusted open-hand release event or lifecycle
cleanup should do that. Locomotion should remain independent of the hand grab
state so the elephant can walk while carrying the timber.

## Tracking and platform setup

The existing communication ports are Pose `5055`, controller `5056`, camera
preview `5057`, and Hand `5058` (`controller.port + 3`). Allocate a separate,
documented Face port if needed; avoid collisions. The bridge should capture the
camera once and fan out frames to Pose, Hand, and Face workers.

Hand packets are newline-free JSON containing `version`, `session`, `sequence`,
`performerId`, `left`, `right`, and confidence values. Gesture states are
`open`, `fist`, or `unknown`. Preserve session/sequence checks and reject
malformed, stale, or wrong-performer packets.

On Apple Silicon Macs (including M5), create a native Python environment on
the Mac. Do not copy a Windows `.venv`:

```text
cd DigiPhantStarter/Tracking
python3 -m venv .venv
.venv/bin/python -m pip install --upgrade pip
.venv/bin/python -m pip install -r requirements.txt
.venv/bin/python test_bridge.py
```

Use the repository's pinned requirements and a supported Python 3.11 or 3.12
installation. Grant camera permission to the application that actually opens
the camera. Avoid Windows-only DirectShow flags or hard-coded `C:\` paths;
the camera preview should resolve the bridge relative to the project on Mac.

The official MediaPipe Gesture Recognizer model is included for convenience;
its source documentation is at
https://ai.google.dev/edge/mediapipe/solutions/vision/gesture_recognizer.
The upstream course/source project is
https://github.com/kommanderpi/studentstarter. Check the upstream and model
licenses before redistribution; this package does not grant a blanket MIT
license to third-party assets.

## Unity integration and validation

1. Back up the recipient's working scene and confirm Unity `6000.6.3f1`.
2. Import only the new scripts/model from `Import/` and `Tracking/`, with
   matching `.meta` files. Resolve any duplicate GUID before opening the scene.
3. Add the hand-grab component to the existing elephant/controller setup and
   rebind its head/trunk references to the recipient's elephant. The authored
   scene contains hidden grip-reference transforms, trunk root/anchor data,
   arrays, and reference quaternions; rebind by bone/object, never by literal
   Unity file IDs.
4. Register the existing Timber root as grabbable and preserve its collider,
   Rigidbody, cut ends, and local axis.
5. Configure the agreed performer ID and keep the recipient's walking/drinking
   and face/eating components enabled.
6. Test with manual gestures first, then the shared live camera: reach, pick up,
   walk while carrying, lose hand tracking, recover tracking, and release by
   explicitly opening either hand.

The source project includes editor validation for cube/timber wiring and
synthetic packet behavior. Those checks do not prove Mac camera permissions,
the merged face pipeline, or live-webcam identity assignment. Run the
recipient's merged tests after integration; do not report them as passed based
only on this handoff package.

The exact source editor menus are **DigiPhant > Hand Grab > Inject into Open
Scene**, **Upgrade Inward Under-Wrap in Open Scene**, and **Make Timber
Grabbable in Open Scene**. Use
them only after backing up and inspecting the recipient scene; manual
configuration is preferred when custom animation or Timber objects already
exist.

The main authored values are confidence `0.65`, pickup debounce `0.12 s`,
tracking grace `2.0 s` (not a hold-expiry timer), head hold `18`, extra ground
reach `8`, reach smoothing `0.2`, carry smoothing `0.08`, and a `0.4 m`
pickup volume.

For detailed gesture behavior and source-scene notes, see
`ReferenceOnly/HAND_GRAB_SETUP.md`. Keep the recipient's project documentation updated with
the final performer IDs, port assignments, arbitration policy, and any Mac
installation changes.
