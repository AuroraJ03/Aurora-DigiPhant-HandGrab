# Hand-gesture elephant interaction

## Purpose

This adds a small, independent interaction layer to the existing DigiPhant
scene: a test cube is placed in front of the trunk. The original generated
fixture was 20 cm (0.2 m) per side; the currently saved scene intentionally
preserves the user's 30 cm cube. The official
MediaPipe Gesture Recognizer model supplies `open` and `fist` states for the
selected performer. The interaction owns only the head/trunk overlay while it
is active; locomotion, the travel root, camera, pose packets, and unrelated
animation channels remain outside its control.

## Gesture rules

| Left hand | Right hand | Result |
|---|---|---|
| Open | any/open | neutral; an already-held cube is released and the overlay returns smoothly |
| Fist | Open | head lowers and trunk reaches |
| Fist | Fist | reach and pick up when the trunk tip is inside the 40 cm pickup volume |
| Fist | Open while holding | release the cube, keep the reach pose |
| Fist | Fist while holding | keep carrying the cube |
| Open | Fist | neutral; right fist alone does not initiate a grab |

Unknown, low-confidence, or missing-hand frames do not release an object once
it has been picked up. The timeout still ages stale input and prevents stale
input from starting a new grab, but it is not a release timer for an existing
hold. An existing hold remains latched until a fresh, confident explicit open
palm is detected on either hand. Left/right are
anatomical hands after the bridge's performer assignment, not the viewer's
screen-left/screen-right. The `performerId` field defaults to 1 and is
configurable for performers 1–4. Pickup has a 0.12 s debounce.

## Structure and dependencies

- `Assets/DigiPhant/Runtime/DigiPhantHandGrab.cs` receives newline-free JSON
  over UDP on the existing controller port plus 3, applies smooth head/trunk
  offsets, performs the enlarged pickup-volume test, and releases the Rigidbody.
  The inward under-wrap revision uses an FK pose across the distal trunk joints
  and a measured interior coil anchor; it does not simply attach the object to the
  terminal nose tip.
- `Assets/DigiPhant/Editor/DigiPhantHandGrabSetup.cs` injects and validates the
  test object through the Unity menu.
- `DigiPhantStarter/Tracking/gesture_recognizer.task` is Google's MediaPipe
  Gesture Recognizer model (SHA-256
  `97952348CF6A6A4915C2EA1496B4B37EBABC50CBBF80571435643C455F2B0482`). See
  the [official Gesture Recognizer documentation](https://ai.google.dev/edge/mediapipe/solutions/vision/gesture_recognizer).
  The existing pose bridge and camera remain the source of capture; no pose
  model or camera setting is replaced.

## Setup and use

1. Open the saved elephant scene (`Assets/DigiPhant/Scenes/DigiPhant.unity`)
   and leave Play mode.
2. Choose **DigiPhant > Hand Grab > Inject into Open Scene**. This creates a
   sibling `DigiPhant_BeforeHandGrab.unity` backup (or a uniquely suffixed
   equivalent if that name already exists), then adds `DigiPhant Hand Grab
   Test` and saves the scene. Re-running the menu replaces the generated test
   root; preserve the newest backup you need. The original pre-feature backup
   is `Assets/DigiPhant/Scenes/DigiPhant_BeforeHandGrab.unity`.
3. For a deterministic editor test, enable the component's
   `useManualGestures` field, or use the in-game **Test hand gestures
   (camera-free)** toggle. Leave it disabled for the UDP hand pipeline. Start
   the tracking bridge, select the intended performer, and ensure the camera
   can see both hands and their fingers.
4. Choose **DigiPhant > Hand Grab > Validate Open Scene** for the lightweight
   wiring/protocol check. For a live test,
   use open/open, left fist/right open, then both fists while the trunk tip is
   over the cube. Open the left hand to drop and reset; open only the right
   hand to drop while retaining the reach pose.

The scene has already been injected in the current project, so normally open
`DigiPhant.unity` and press Play; reinjection is only needed after deliberately
restoring an older backup or rebuilding the generated test root.

To upgrade an existing injected scene to the inward under-wrap action, use
**DigiPhant > Hand Grab > Upgrade Inward Under-Wrap in Open Scene**. This creates
`DigiPhant_BeforeInwardGrip.unity` as a backup (or a unique suffix) before
changing the generated animation pose. The current baseline backup has
SHA-256 `93091686...C508`. The animation sends the trunk down in front of the
object, passes underneath it, then turns the terminal joints inward toward the
elephant's body. It avoids the previous upward hook while preserving the
measured interior grip anchor. The current cube remains unchanged; this pose
is intended to transfer later to a cylindrical stump or log. This is a
pose/overlay integration, not a replacement IK solver. The FK pose stores a
per-joint local-rotation reference and blends smoothly into the under-wrap pose.

The original default orange cube is 0.2 m on each side; the current saved
scene uses a preserved 0.3 m cube. It rests on a visible blue pickup platform,
and the pickup volume remains a full 0.4 m × 0.4 m × 0.4 m box
(not 0.4 m added to each side). `reachSmoothTime`, `carrySmoothTime`, gesture
confidence, timeout, and `performerId` are exposed on the generated component.
The inward under-wrap revision keeps the full 0.4 m pickup volume and 2.0 s
tracking grace setting. Behavioural validation should be rerun after the
upgrade, especially the authored cube lift and the actual animated trunk
geometry; the setup menu's wiring check is not a substitute for that runtime
test. The tracking bridge's regression tests remain separate from live camera
testing. The automated long-hold check uses synthetic hand packets to verify
that fresh fist packets do not expire; it is not a 60-second webcam claim.
Real user fists/open palms still need to be demonstrated with the bridge and
camera running.

The Timber, locomotion, and cube automated suites pass. Coverage includes a
60-second missing-hand hold, no release for Unknown/low-confidence or the
wrong performer, release from a trusted open hand on either side, and a queued
open followed by a fist without an accidental re-grab. It also verifies that
fresh body input plus lost hands can move Timber, while stale body input stops
movement but preserves the hold; production `OnDisable` cleanup is tested in
EditMode. The travel test moves the root 10.645 m and turns 118.5 degrees
inside the 30 m clamp. These are automated synthetic-state checks, not a
live-webcam verification.

The overlay compensates the head/trunk target in world orientation relative to
the moving travel ancestors, so idle animation does not shake the authored
reach/carry action. Only the selected head/trunk bones are owned while the
overlay is active; locomotion and unrelated animation channels remain free for
future integration. Tracking loss still stops movement through the existing
upper-body controller, but does not drop a held object. Play-stop or component
disable cleanup still releases held physics normally.

## Timber interaction

The scene's existing `Timber` object is also a grabbable target. Its appearance,
material, position, and approximately 3 m length are preserved; the interaction
changes its diameter from approximately 0.8 m to 0.5 m while preserving its
length, position, and material. Pickup accepts a bounded 0.2 m surface margin,
rather than a distant magnet. Its collider and registration remain on the timber, so
the same gesture pipeline can select either the cube or the log.

When the trunk reaches the timber from any approach direction, the held log is
reoriented smoothly so its long axis is horizontal and crosswise to the
elephant. This is a target-orientation correction, not a change to locomotion
or the existing hand rules. Pickup and carry use the same smooth transition
settings as the cube, and release restores the timber's physics.

The reach phase adds a deeper head nod to make ground-level objects reachable;
it does not add a separate trunk-extension angle. Once a target is picked up,
the established grab/carry body pose is unchanged. The overlay still owns only
the selected head/trunk bones; upper-body movement, locomotion, camera, pose
tracking, and other animation channels remain available for later pipeline
integration.

Upper-body locomotion remains unchanged: forward travel is driven by the
calibrated left-hand height, while steering uses the shoulder-height difference.
A fist by itself does not request forward motion, and the grab overlay does not
consume or rewrite those upper-body signals.

The Timber setup preserves the original scene in
`Assets/DigiPhant/Scenes/DigiPhant_BeforeTimberGrab.unity`. An optional preview
scene, `TimberGrab_Preview`, is available for inspecting the authored wood
placement without changing the source scene.

## Significant decisions

The grab logic is an overlay subscribed after the controller's normal controls,
so existing locomotion continues to own travel and locomotion channels. While
the interaction is active, the selected head/trunk bones are the intentional
exclusive ownership boundary; all other animation channels continue normally.
The held Rigidbody becomes kinematic and follows the interior coil/grip-center
anchor with smoothing; release restores physics and lets it fall. Once held,
the Rigidbody remains latched across missing, unknown, and low-confidence
hand packets; only a fresh confident open-hand event releases it. A larger axis-aligned volume is
intentional for accessibility and robust tracking. The bridge uses a separate
UDP port (`controller.port + 3`) to avoid changing the existing pose packet.

## Troubleshooting

If gestures remain `Unknown`, check camera framing, hand visibility, model
availability, performer selection, confidence (default 0.65), and that the
bridge is sending version-1 packets containing both hands. The hand worker
uses the same camera frame as pose tracking and sends its independent stream on
`controller.port + 3`; it does not open a competing camera. If the elephant
does not move, first enable **Test hand gestures (camera-free)** in the game
overlay and exercise the manual states. The overlay also displays the last
release reason. It should be an explicit `Open palm` (or lifecycle cleanup);
missing, unknown, and timeout input are not release events. This separates
Unity pose setup from camera/model tracking.
Live camera testing requires the bridge to be running and cannot be replaced by
the manual toggle.
