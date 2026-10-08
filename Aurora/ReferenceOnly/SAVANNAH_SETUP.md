# SavannahCourse setup

This project keeps the existing DigiPhant elephant performance scene and adds a
static Savannah course around it. The course is for scene composition and
navigation testing; it does not replace the elephant controller, camera,
MediaPipe bridge, tracking scripts, or project settings.

## Structure and dependencies

- Working scene: `Assets/DigiPhant/Scenes/DigiPhant.unity`
- Imported package: `Assets/SavannahCourse/` and `Assets/SavannahCourse.meta`
- Course root in the scene: `Savannah Course [layout 4]`
- Source checkout (kept outside `Assets`):
  `SavannahCourseSource/` beside the Unity project
- Source commit: `4e6dab5271c618d1c0bfb2022c41c0d37cd8d69f`
- Unity: `6000.6.3f1`; render pipeline: URP `17.6.0`
- Layout seed: `20261005`; generated content: 18 trees and 4 landmarks

The import uses the package's supplied editor generator and preserves Unity
GUID metadata. The course contains static scenery only: no colliders, audio, or
runtime interaction logic.

## Usage

Open `Assets/DigiPhant/Scenes/DigiPhant.unity` and press Play. In the Game view,
use the **Test sliders** input-mode button to exercise the elephant parts, then
**Reset body sliders** to return to neutral. Enable locomotion, then test
**Travel** forward/back, **Turn** left/right, **Stop moving**, and **Return to
starting position**. To test MediaPipe, choose the **Camera** input-mode button,
wait for the bridge, then use **Set neutral pose** for 10 seconds before testing
the existing mapping; use **Retry** if the bridge reconnects.
The existing controls remain on `DigiPhant Controls`. Do not open
`Assets/SavannahCourse/Scenes/SavannahEnvironment.unity` as the working
elephant scene; it is the package's standalone preview.

## Verification and backup

The official import and validation logs were stored beside the Unity project as
`savannah-injection.log` and `savannah-validation.log`.
They report
`SAVANNAH_BUNDLE_OK`, `SAVANNAH_IMPORT_OK`, `SAVANNAH_VALIDATION_OK`,
`SAVANNAH_SCENE_COPY_OK`, and `SAVANNAH_INJECTION_OK`.

Before injection, the saved scene was backed up at
`Assets/DigiPhant/Scenes/DigiPhant_BeforeSavannah.unity`. The package's editor
menu is **DigiPhant > Savannah > Inject Course into Open Scene**; it creates a
new sibling backup on future injections and replaces only its recognized course
root. Review confirmed that the backup is byte-for-byte identical to the
pre-injection scene HEAD. The import retained 50 files and 28 unique GUIDs;
the only scene materialization was the original scene roots plus the
controller's default `upperBodyOnly: 0`. Existing mapping behavior was not
changed.

The approved movement-boundary update is preserved in
`Assets/DigiPhant/Scenes/DigiPhant_BeforeTravelRadius30.unity`; this backup is
the pre-30-metre version of the saved DigiPhant scene.

## Significant decisions and limitation

The course was injected into the current saved scene through the official
command-line equivalent of that menu, so the scene filename and custom setup
were not changed. The existing movement root and custom locomotion mapping were
kept. The saved scene now uses `stageRadius: 30`, matching the course's intended
travel radius.
The course's intended travel radius is 30; its finish is about 24.08 units from
the elephant start, so the saved boundary now covers the full route. The
boundary change removes the earlier 15-unit limit without changing the
upper-body mapping or travel root.

The final synthetic travel test verified holding Timber while the upper-body
controller moved the travel root 10.645 m and turned 118.5 degrees. The travel
root stayed within its 30 m clamp; the nose and log can extend beyond that root
boundary. The full automated coverage also verifies missing-hand holding,
rejection of Unknown/low-confidence and wrong-performer releases, trusted open
release from either hand, queued-open handling, stale-body movement stop with
the hold preserved, and production `OnDisable` cleanup. These are automated
synthetic-state checks, not a current GUI manual-walking or live-webcam claim.
