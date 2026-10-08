using System;
using System.IO;
using System.Reflection;
using DigiPhant;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DigiPhant.Editor
{
    /// <summary>
    /// Disposable-scene checks for the authored Timber target.  This deliberately
    /// exercises the production gesture overlay and never writes the saved scene.
    /// </summary>
    public static class DigiPhantTimberValidation
    {
        const string Marker = "DIGIPHANT_TIMBER_VALIDATION_OK";
        const string PreviewPath = "Assets/DigiPhant/Scenes/TimberGrab_Preview.unity";
        static readonly MethodInfo Poll = typeof(DigiPhantHandGrab).GetMethod("PollHands", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Creates a disposable, camera-testable Timber layout.  The saved course
        /// scene is copied before any transform is changed; this preview is never
        /// a replacement for the authored scene.
        /// </summary>
        // -executeMethod DigiPhant.Editor.DigiPhantTimberValidation.CreatePreviewScene -handGrabScene Assets/...unity
        public static void CreatePreviewScene()
        {
            string source = Argument("-handGrabScene");
            if (string.IsNullOrEmpty(source) || !File.Exists(source)) throw new ArgumentException("Pass an existing -handGrabScene Assets/...unity");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(PreviewPath)) throw new InvalidOperationException("Preview already exists at " + PreviewPath + "; preserve it or delete it explicitly before creating another.");
            if (!AssetDatabase.CopyAsset(source, PreviewPath)) throw new IOException("Could not create the Timber preview copy.");
            EditorSceneManager.OpenScene(PreviewPath, OpenSceneMode.Single);
            var hand = UnityEngine.Object.FindAnyObjectByType<DigiPhantHandGrab>(FindObjectsInactive.Include);
            var timberTarget = FindTimber();
            if (!hand || !hand.trunkTip || !timberTarget || !timberTarget.Body)
                throw new InvalidOperationException("Preview copy is missing the hand-grab, trunk tip, or Timber target.");

            Rigidbody timber = timberTarget.Body;
            Transform root = hand.reachReferenceRoot ? hand.reachReferenceRoot : hand.transform;
            Quaternion headBeforePreview = hand.head ? hand.head.localRotation : Quaternion.identity;
            Quaternion[] trunkBeforePreview = Array.ConvertAll(hand.trunk, bone => bone ? bone.localRotation : Quaternion.identity);
            hand.EnsureControllerBinding();
            for (int frame = 0; frame < 120; frame++)
            {
                hand.SetManualGestures(HandGesture.Fist, HandGesture.Open);
                Poll.Invoke(hand, new object[] { 100f + frame / 60f });
                hand.controller.ApplyControls(100f + frame / 60f, 1f / 60f);
            }
            Vector3 settledReach = hand.trunkTip.position;
            for (int i = 0; i < hand.trunk.Length; i++) if (hand.trunk[i]) hand.trunk[i].localRotation = trunkBeforePreview[i];
            if (hand.head) hand.head.localRotation = headBeforePreview;
            hand.ResetHands();
            Vector3 crosswise = Vector3.ProjectOnPlane(root.right, Vector3.up);
            if (crosswise.sqrMagnitude < .0001f) crosswise = Vector3.right;
            Quaternion timberRotation = Quaternion.FromToRotation(timberTarget.localLongAxis.normalized, crosswise.normalized);
            // This is deliberately a preview-only physical target: horizontally
            // oriented, in the nose's current reachable window, and just below
            // it so the visual test demonstrates the lowered reach.
            Vector3 timberPosition = new Vector3(settledReach.x, .25f, settledReach.z);
            timber.transform.SetPositionAndRotation(timberPosition, timberRotation);
            timber.position = timberPosition;
            timber.rotation = timberRotation;
            timber.linearVelocity = Vector3.zero;
            timber.angularVelocity = Vector3.zero;
            timber.isKinematic = false;
            if (hand.testCube)
            {
                hand.testCube.isKinematic = false;
                Vector3 cubePosition = hand.testCube.position + root.right * 20f;
                hand.testCube.transform.position = cubePosition;
                hand.testCube.position = cubePosition;
                hand.testCube.linearVelocity = Vector3.zero;
                hand.testCube.angularVelocity = Vector3.zero;
            }
            hand.useManualGestures = true;
            hand.manualLeft = HandGesture.Open;
            hand.manualRight = HandGesture.Open;
            EditorUtility.SetDirty(timber.gameObject);
            EditorUtility.SetDirty(hand);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            Physics.SyncTransforms();
            Debug.Log("DIGIPHANT_TIMBER_PREVIEW_READY: " + PreviewPath + " (source untouched: " + source + ")");
        }

        // -executeMethod DigiPhant.Editor.DigiPhantTimberValidation.RunAll -handGrabScene Assets/...unity
        public static void RunAll()
        {
            string source = Argument("-handGrabScene");
            if (string.IsNullOrEmpty(source) || !File.Exists(source)) throw new ArgumentException("Pass an existing -handGrabScene Assets/...unity");
            const string folder = "Assets/StudentWork/HandGrabValidation";
            EnsureFolder(folder);
            string copy = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileNameWithoutExtension(source) + "_TimberRuntimeTest.unity");
            if (!AssetDatabase.CopyAsset(source, copy)) throw new IOException("Could not copy saved scene for Timber validation.");
            try { Run(copy); Debug.Log(Marker + ": Timber physics, range, multi-approach alignment and hand-overlay isolation"); }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(copy);
                AssetDatabase.Refresh();
                EditorSceneManager.OpenScene(source, OpenSceneMode.Single);
            }
        }

        static void Run(string path)
        {
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var hand = UnityEngine.Object.FindAnyObjectByType<DigiPhantHandGrab>(FindObjectsInactive.Include);
            Check(hand && hand.controller && hand.head && hand.trunkTip && Poll != null, "Missing hand-grab production references or test hook.");
            DigiPhantGrabbable timberTarget = FindTimber();
            Check(timberTarget, "Could not find the explicit Timber DigiPhantGrabbable in the current scene.");
            Rigidbody timber = timberTarget.Body;
            Check(timber, "Timber grabbable needs a Rigidbody body.");
            Collider collider = timberTarget.pickupCollider;
            Check(collider && collider.enabled, "Timber needs an enabled pickup collider.");
            var mesh = collider as MeshCollider;
            Check(!mesh || mesh.convex, "Timber MeshCollider must be convex so it can be grabbed by a Rigidbody.");
            Check(!timber.isKinematic, "Saved Timber must start as a physical Rigidbody.");
            Check(HasGrabbableRegistration(hand, timber), "DigiPhantHandGrab must register Timber without replacing existing grab targets.");
            Check(CountNamedDirectChildren(timberTarget.transform, "Cut end") == 2, "Timber must retain exactly two direct cut-end-disc children.");
            CheckCutEnds(timberTarget, longest: 0f);

            var c = hand.controller;
            c.SetInputMode(InputMode.TestSliders);
            c.CaptureRestPose();
            hand.useManualGestures = true;
            hand.acquireDebounce = .12f;
            hand.EnsureControllerBinding();
            foreach (var control in c.controls) { control.testValue = 0; control.current = 0; }
            var locomotion = c.GetComponent<DigiPhantLocomotion>();
            bool locomotionWasEnabled = locomotion && locomotion.enabled;
            if (locomotion) { locomotion.StopAnimation(); locomotion.enabled = false; }

            float t = 10f;
            Vector3 savedPosition = timber.position;
            Quaternion savedRotation = timber.rotation;
            Vector3 savedScale = timber.transform.localScale;
            Vector3 savedCubePosition = hand.testCube.position;
            Quaternion savedCubeRotation = hand.testCube.rotation;
            // The approved physical asset is a 3m horizontal log, 50cm diameter.
            Vector3 world = collider.bounds.size;
            float longest = Mathf.Max(world.x, Mathf.Max(world.y, world.z));
            float shortest = Mathf.Min(world.x, Mathf.Min(world.y, world.z));
            Check(longest > 2.7f && longest < 3.3f && shortest > .42f && shortest < .58f,
                "Timber must remain approximately 3m long and 50cm in diameter.");
            CheckCutEnds(timberTarget, longest);

            // A lowered reach must materially improve the floor pickup pose only
            // while unheld. Capture the regular reach, then test the target-aware
            // ground reach with the same target at ground level.
            // Keep the existing cube away for this assertion: it is deliberately
            // also a valid ground prop and must not supply the correction here.
            hand.testCube.position += Vector3.up * 20f;
            Step(hand, ref t, HandGesture.Fist, HandGesture.Open, 120);
            Vector3 normalReachTip = hand.trunkTip.position;
            Quaternion normalReachHead = hand.head.localRotation;
            PlaceGroundTimber(hand, timber, timberTarget, normalReachTip);
            Step(hand, ref t, HandGesture.Fist, HandGesture.Open, 30);
            Vector3 groundReachTip = hand.trunkTip.position;
            Quaternion groundReachHead = hand.head.localRotation;
            Collider groundCollider = timberTarget.pickupCollider;
            Vector3 closest = groundCollider.ClosestPoint(hand.trunkTip.position);
            float applied = PrivateFloat(hand, "groundReachDegreesApplied");
            Debug.Log("DIGIPHANT_TIMBER_GROUND_REACH rb=" + timber.position + " transform=" + timber.transform.position +
                " bounds=" + groundCollider.bounds + " closest=" + closest + " reachTip=" + hand.trunkTip.position +
                " normalTip=" + normalReachTip + " extra=" + applied.ToString("F3") +
                " headAngle=" + Quaternion.Angle(normalReachHead, groundReachHead).ToString("F3"));
            Check(groundReachTip.y < normalReachTip.y - .015f,
                "A ground Timber target did not activate the additional unheld head-lower reach.");
            Check(Quaternion.Angle(normalReachHead, groundReachHead) > .25f,
                "Ground Timber changed reach height but did not visibly lower the head.");

            // Exact boundary behaviour: selected within its enlarged collider-based
            // region, rejected when fully outside.  It must be geometric; target
            // ends must not be accepted merely because a centre is nearby.
            Check(CanAcquire(hand, timber, ref t, Vector3.zero), "Timber at the pickup centre was not eligible.");
            ReleaseAndSettle(hand, ref t);
            Check(!CanAcquire(hand, timber, ref t, new Vector3(2.0f, 0, 0)), "Far Timber was incorrectly eligible for pickup.");
            ReleaseAndSettle(hand, ref t);
            Check(!CanAcquire(hand, timber, ref t, new Vector3(0, 0, 2.0f)), "A distant Timber end was incorrectly eligible for pickup.");
            ReleaseAndSettle(hand, ref t);

            // When both targets overlap, selection must use physical surface
            // distance in comparable world units, not a hard-coded cube centre.
            hand.testCube.isKinematic = false;
            hand.testCube.position = hand.trunkTip.position + Vector3.right * .19f;
            PlaceAtPickupCenter(hand, timber, Vector3.zero);
            Step(hand, ref t, HandGesture.Fist, HandGesture.Fist, 18);
            Check(hand.IsHolding && hand.HeldBody == timber,
                "Timber surface nearest the trunk was not selected over the farther overlapping cube.");
            ReleaseAndSettle(hand, ref t);

            // Test five elephant-relative approaches.  The held object is expected
            // to finish horizontal and crosswise to the elephant's forward axis,
            // no matter which yaw the elephant uses to approach it.
            Transform root = hand.reachReferenceRoot ? hand.reachReferenceRoot : hand.transform;
            Quaternion originalRoot = root.rotation;
            Quaternion heldHeadPose = Quaternion.identity;
            Quaternion[] heldTrunkPose = null;
            foreach (float yaw in new[] { 0f, 45f, 90f, 180f, 270f })
            {
                root.rotation = Quaternion.Euler(0, yaw, 0) * originalRoot;
                Physics.SyncTransforms();
                PlaceAtPickupCenter(hand, timber, new Vector3(0, -.04f, 0));
                float maxHeadStep = 0f;
                Quaternion previousHead = hand.head.localRotation;
                for (int frame = 0; frame < 18; frame++)
                {
                    Step(hand, ref t, HandGesture.Fist, HandGesture.Fist, 1);
                    maxHeadStep = Mathf.Max(maxHeadStep, Quaternion.Angle(previousHead, hand.head.localRotation));
                    previousHead = hand.head.localRotation;
                }
                Check(hand.IsHolding, "Timber could not be grabbed at yaw " + yaw + ".");
                Check(maxHeadStep < 4.5f, "Head snapped during the ground-reach-to-held transition at yaw " + yaw + ".");
                Check(hand.IsHolding && hand.HeldBody == timber, "Timber was not the active held body at yaw " + yaw + ".");
                Step(hand, ref t, HandGesture.Fist, HandGesture.Fist, 45);
                CheckHorizontalCrosswise(timberTarget, root, yaw);
                // The target-aware extra lowering is a reach-only aid.  Once the
                // grip closes, the existing authored held pose must be unchanged.
                Debug.Log("DIGIPHANT_TIMBER_HELD_POSE yaw=" + yaw + " baseline=" + normalReachHead.eulerAngles +
                    " held=" + hand.head.localRotation.eulerAngles + " angle=" + Quaternion.Angle(normalReachHead, hand.head.localRotation).ToString("F3") +
                    " groundApplied=" + PrivateFloat(hand, "groundReachDegreesApplied").ToString("F3"));
                Check(Quaternion.Angle(normalReachHead, hand.head.localRotation) < .15f,
                    "Holding Timber changed the established head/trunk grab pose at yaw " + yaw + ".");
                if (heldTrunkPose == null)
                {
                    heldHeadPose = hand.head.localRotation;
                    heldTrunkPose = Array.ConvertAll(hand.trunk, bone => bone.localRotation);
                }
                else
                {
                    Check(Quaternion.Angle(heldHeadPose, hand.head.localRotation) < .15f,
                        "Changing approach yaw changed the elephant's closed head pose.");
                    for (int i = 0; i < hand.trunk.Length; i++)
                        Check(Quaternion.Angle(heldTrunkPose[i], hand.trunk[i].localRotation) < .15f,
                            "Changing approach yaw changed closed trunk bone " + i + ".");
                }
                Check(timber.isKinematic, "Held Timber must become kinematic at yaw " + yaw + ".");
                Quaternion releaseHead = hand.head.localRotation;
                Step(hand, ref t, HandGesture.Fist, HandGesture.Open, 1);
                Check(Quaternion.Angle(releaseHead, hand.head.localRotation) < 4.5f, "Head snapped on Timber release at yaw " + yaw + ".");
                ReleaseAndSettle(hand, ref t);
            }
            root.rotation = originalRoot;

            // Stable live-camera holds and both release branches must work for
            // Timber too.  Use fresh packets rather than the manual UI so this
            // covers the two-second tracking grace as well as an unlimited hold.
            PlaceAtPickupCenter(hand, timber, new Vector3(0, -.04f, 0));
            PrimeLiveTimberHold(hand, c, timber, ref t, "TimberLongHold");
            int sequence = 100;
            for (int i = 0; i < 60 * 60; i++)
            {
                if (i % 5 == 0) Check(hand.AcceptHandPacket(Packet("TimberLongHold", ++sequence, "fist", "fist"), t), "Fresh Timber fist packet was rejected.");
                Poll.Invoke(hand, new object[] { t }); hand.controller.ApplyControls(t, 1f / 60f); t += 1f / 60f;
            }
            Check(hand.IsHolding && hand.HeldBody == timber, "Timber was released during a continuous 60-second two-fist hold.");
            Check(hand.AcceptHandPacket(Packet("TimberLongHold", ++sequence, "fist", "open"), t), "Timber explicit-open packet was rejected.");
            Poll.Invoke(hand, new object[] { t }); hand.controller.ApplyControls(t, 1f / 60f); t += 1f / 60f;
            Check(!hand.IsHolding && !timber.isKinematic, "Right open must release Timber immediately while reach remains.");
            c.SetInputMode(InputMode.TestSliders); hand.useManualGestures = true;
            Step(hand, ref t, HandGesture.Open, HandGesture.Open, 30);

            // Holding an object must remain an overlay: it may not stop the
            // existing upper-body locomotion mapping.  This uses the same Camera
            // mode calibration and LeftHandHeight / Lean signals as the live
            // bridge, while keeping hand gestures manual so a disposable editor
            // test does not need a MediaPipe process.
            CheckHeldUpperBodyTravel(hand, c, locomotion, timberTarget, savedPosition, ref t);

            // Existing cube target must remain usable after the timber integration.
            Check(hand.testCube, "Timber integration removed the existing cube reference.");
            Vector3 parkedTimber = hand.trunkTip.position + Vector3.right * 20f;
            timber.transform.position = parkedTimber;
            timber.position = parkedTimber;
            timber.linearVelocity = Vector3.zero;
            timber.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
            Step(hand, ref t, HandGesture.Fist, HandGesture.Open, 120);
            Vector3 settledCubeReach = hand.trunkTip.position;
            hand.testCube.transform.position = settledCubeReach;
            hand.testCube.position = settledCubeReach;
            hand.testCube.isKinematic = false;
            hand.testCube.linearVelocity = Vector3.zero;
            hand.testCube.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
            Step(hand, ref t, HandGesture.Fist, HandGesture.Fist, 18);
            Debug.Log("DIGIPHANT_TIMBER_CUBE_REGRESSION cube=" + hand.testCube.position + " tip=" + hand.trunkTip.position +
                " candidate=" + (hand.ActiveCandidate ? hand.ActiveCandidate.name : "none") +
                " held=" + (hand.HeldBody ? hand.HeldBody.name : "none") + " delta=" + hand.PickupDelta);
            Check(hand.IsHolding && hand.HeldBody == hand.testCube, "Existing cube is no longer grabbable after adding Timber.");
            ReleaseAndSettle(hand, ref t);

            timber.position = savedPosition; timber.rotation = savedRotation; timber.transform.localScale = savedScale; timber.isKinematic = false;
            hand.testCube.position = savedCubePosition; hand.testCube.rotation = savedCubeRotation;
            if (locomotion) locomotion.enabled = locomotionWasEnabled;
        }

        static DigiPhantGrabbable FindTimber()
        {
            foreach (var target in UnityEngine.Object.FindObjectsByType<DigiPhantGrabbable>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (target && (target.name.IndexOf("timber", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               (target.Body && target.Body.name.IndexOf("timber", StringComparison.OrdinalIgnoreCase) >= 0))) return target;
            return null;
        }

        static bool HasGrabbableRegistration(DigiPhantHandGrab hand, Rigidbody body)
        {
            FieldInfo field = typeof(DigiPhantHandGrab).GetField("grabbables", BindingFlags.Instance | BindingFlags.Public);
            if (field == null || !(field.GetValue(hand) is Array list)) return false;
            foreach (object value in list)
            {
                if (value == null) continue;
                FieldInfo b = value.GetType().GetField("body", BindingFlags.Instance | BindingFlags.Public);
                if (b != null && b.GetValue(value) == body) return true;
            }
            return false;
        }

        static int CountNamedDirectChildren(Transform root, string part)
        {
            int count = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) count++;
            }
            return count;
        }

        static void CheckCutEnds(DigiPhantGrabbable target, float longest)
        {
            int inspected = 0;
            foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.transform.parent != target.transform || renderer.name.IndexOf("Cut end", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Check(renderer.sharedMaterial, "A Timber cut-end disc lost its material.");
                if (longest > 0)
                {
                    Vector3 axis = target.Body.transform.TransformDirection(target.localLongAxis).normalized;
                    float along = Mathf.Abs(Vector3.Dot(renderer.bounds.center - target.Body.worldCenterOfMass, axis));
                    Check(along > 1.1f && along < 1.7f, "A Timber cut-end disc is no longer at a log end after resizing.");
                }
                inspected++;
            }
            Check(longest == 0 || inspected == 2, "Expected two rendered Timber cut-end discs.");
        }

        static bool CanAcquire(DigiPhantHandGrab hand, Rigidbody timber, ref float t, Vector3 offset)
        {
            PlaceAtPickupCenter(hand, timber, offset);
            Step(hand, ref t, HandGesture.Fist, HandGesture.Fist, 18);
            return hand.IsHolding;
        }

        static void PlaceAtPickupCenter(DigiPhantHandGrab hand, Rigidbody body, Vector3 offset)
        {
            body.isKinematic = false;
            body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero;
            Vector3 position = hand.trunkTip.position + offset;
            Quaternion rotation = Quaternion.identity;
            body.transform.SetPositionAndRotation(position, rotation);
            body.position = position;
            body.rotation = rotation;
            Physics.SyncTransforms();
        }

        static void PlaceGroundTimber(DigiPhantHandGrab hand, Rigidbody body, DigiPhantGrabbable target, Vector3 regularReachTip)
        {
            body.isKinematic = false;
            body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero;
            // The imported cylinder uses local +Y as its 3m axis. Turn it
            // horizontal, then put its top surface 15cm below the normal reach
            // tip: it is close enough to be eligible while still requiring a
            // genuinely lower head/trunk pose.
            Quaternion rotation = Quaternion.FromToRotation(target.localLongAxis.normalized, Vector3.right);
            Vector3 position = regularReachTip + Vector3.down * .40f;
            body.transform.SetPositionAndRotation(position, rotation);
            body.rotation = rotation;
            body.position = position;
            Physics.SyncTransforms();
        }

        static void CheckHeldUpperBodyTravel(DigiPhantHandGrab hand, DigiPhantController controller,
            DigiPhantLocomotion locomotion, DigiPhantGrabbable timberTarget, Vector3 authoredTimberPosition, ref float t)
        {
            Check(locomotion && locomotion.travelRoot, "Missing locomotion travel root for Timber travel validation.");
            Rigidbody timber = timberTarget.Body;
            Transform root = locomotion.travelRoot;
            bool previousEnabled = locomotion.enabled;
            bool previousAllowed = locomotion.enableLocomotion;
            bool previousUpperBody = controller.upperBodyOnly;
            InputMode previousMode = controller.inputMode;
            int previousPerformerCount = controller.performerCount;

            // StartAnimation records the genuine saved starting point.  Keeping
            // the authored Timber inside this radius prevents a course prop from
            // sitting behind an invisible stage wall.
            locomotion.enabled = true;
            locomotion.enableLocomotion = true;
            locomotion.StopAnimation();
            Check(locomotion.Initialise(), "Could not initialise locomotion for upper-body travel validation.");
            Vector3 start = PrivateVector3(locomotion, "startPosition");
            Check(locomotion.stageRadius >= 29.9f && locomotion.stageRadius <= 30.1f,
                "The locomotion stage radius must be 30m, retaining a boundary while reaching the Timber.");
            Vector3 authoredOffset = authoredTimberPosition - start; authoredOffset.y = 0;
            Check(authoredOffset.magnitude > 15.05f && authoredOffset.magnitude < locomotion.stageRadius - .05f,
                "The saved Timber must be outside the old 15m wall but inside the new 30m travel area.");

            // The controller resets hand state when Camera mode begins, so acquire
            // after changing modes.  This mirrors real use: camera pose drives
            // travel while the independent hand pipeline supplies both fists.
            controller.SetPerformerCount(1);
            controller.SetUpperBodyOnly(true);
            controller.SetInputMode(InputMode.Camera);
            hand.useManualGestures = true;
            FeedPose(controller, t, 0, 0);
            Check(controller.CalibrateAt(t), "Upper-body locomotion calibration rejected a complete neutral pose.");
            // Let the usual reach pose settle first.  Positioning the target at
            // this settled nose point is what a real ground-level approach gets,
            // rather than an unblended editor-frame pose.
            for (int i = 0; i < 120; i++) DriveUpperBodyFrame(hand, controller, ref t, 0, 0, HandGesture.Fist, HandGesture.Open);
            PlaceAtPickupCenter(hand, timber, Vector3.zero);
            for (int i = 0; i < 24; i++) DriveUpperBodyFrame(hand, controller, ref t, 0, 0, HandGesture.Fist, HandGesture.Fist);
            Check(hand.IsHolding && hand.HeldBody == timber, "Timber could not be held after upper-body Camera calibration.");
            for (int i = 0; i < 45; i++) DriveUpperBodyFrame(hand, controller, ref t, 0, 0, HandGesture.Fist, HandGesture.Fist);
            Vector3 rootBeforeTravel = root.position;
            Vector3 timberBeforeTravel = timber.position;
            Quaternion headingBeforeTurn = root.rotation;

            // Keep body-pose packets fresh while deliberately sending no hand
            // packets. Hand loss must not drop a latched Timber hold, and it must
            // not block the independent upper-body forward/turn pipeline.
            hand.useManualGestures = false;
            for (int i = 0; i < 180; i++) DriveUpperBodyFrameWithLostHands(hand, controller, ref t, 1f, .7f);
            Vector3 offsetAfterTurn = root.position - start; offsetAfterTurn.y = 0;
            Check(offsetAfterTurn.magnitude > 8f, "Upper-body left-hand-height input did not move the elephant while holding Timber.");
            Check(Quaternion.Angle(headingBeforeTurn, root.rotation) > 20f,
                "Upper-body Lean input did not turn the elephant while holding Timber.");
            Check(hand.IsHolding && hand.HeldBody == timber, "Timber was dropped when hand detection was lost during upper-body forward/turn travel.");
            float rootTravel = Vector3.Distance(rootBeforeTravel, root.position);
            float timberTravel = Vector3.Distance(timberBeforeTravel, timber.position);
            float movingGripDistance = Vector3.Distance(timber.position, hand.GripPointForValidation());
            Debug.Log("DIGIPHANT_TIMBER_UPPER_BODY_MOVING root=" + rootTravel.ToString("F3") +
                " timber=" + timberTravel.ToString("F3") + " gripDistance=" + movingGripDistance.ToString("F3") +
                " rootPosition=" + root.position + " timberPosition=" + timber.position);
            // Carry uses SmoothDamp deliberately, so its local offset can be
            // transient while the elephant walks and turns.  Check the physical
            // invariant instead: the held body must travel materially with the
            // moving root, then check exact convergence once stationary below.
            Check(timberTravel > Mathf.Max(3f, rootTravel * .45f),
                "Held Timber did not travel with the elephant during upper-body movement.");
            CheckHorizontalCrosswise(timberTarget, root, root.eulerAngles.y);

            // First settle with a freshly tracked neutral pose.  Then make the
            // forward signal stale away from either stage edge and ensure motion
            // really stops, rather than letting a boundary hide continued drift.
            for (int i = 0; i < 120; i++) DriveUpperBodyFrame(hand, controller, ref t, 0, 0, HandGesture.Fist, HandGesture.Fist);
            Vector3 neutralStopStart = root.position;
            for (int i = 0; i < 90; i++) DriveUpperBodyFrame(hand, controller, ref t, 0, 0, HandGesture.Fist, HandGesture.Fist);
            Check(Vector3.Distance(neutralStopStart, root.position) < .08f,
                "Fresh neutral upper-body input did not stop elephant travel while Timber was held.");
            float settledGripDistance = Vector3.Distance(timber.position, hand.GripPointForValidation());
            Debug.Log("DIGIPHANT_TIMBER_UPPER_BODY_SETTLED gripDistance=" + settledGripDistance.ToString("F4") +
                " timberPosition=" + timber.position + " gripPoint=" + hand.GripPointForValidation());
            Check(settledGripDistance < .05f,
                "Held Timber did not converge to the grip point after upper-body movement stopped.");
            for (int i = 0; i < 30; i++) DriveUpperBodyFrame(hand, controller, ref t, 1f, 0, HandGesture.Fist, HandGesture.Fist);
            for (int i = 0; i < 120; i++)
            {
                hand.SetManualGestures(HandGesture.Fist, HandGesture.Fist);
                Poll.Invoke(hand, new object[] { t });
                controller.ApplyControls(t, 1f / 60f);
                t += 1f / 60f;
            }
            Vector3 settledAfterLoss = root.position;
            for (int i = 0; i < 90; i++)
            {
                hand.SetManualGestures(HandGesture.Fist, HandGesture.Fist);
                Poll.Invoke(hand, new object[] { t });
                controller.ApplyControls(t, 1f / 60f);
                t += 1f / 60f;
            }
            Check(Vector3.Distance(settledAfterLoss, root.position) < .12f,
                "Lost upper-body tracking continued moving the elephant while Timber was held.");

            // Restore a fresh camera stream before the boundary phase.
            for (int i = 0; i < 30; i++) DriveUpperBodyFrame(hand, controller, ref t, 0, 0, HandGesture.Fist, HandGesture.Fist);

            // Continue straight past the original 15m wall, then drive long
            // enough to prove that the 30m boundary remains active.
            for (int i = 0; i < 150; i++) DriveUpperBodyFrame(hand, controller, ref t, 1f, 0, HandGesture.Fist, HandGesture.Fist);
            Vector3 pastOldBoundary = root.position - start; pastOldBoundary.y = 0;
            Check(pastOldBoundary.magnitude > 15.1f, "Elephant was still blocked at the old 15m stage boundary while holding Timber.");
            for (int i = 0; i < 540; i++) DriveUpperBodyFrame(hand, controller, ref t, 1f, 0, HandGesture.Fist, HandGesture.Fist);
            Vector3 finalOffset = root.position - start; finalOffset.y = 0;
            Check(finalOffset.magnitude <= locomotion.stageRadius + .03f && finalOffset.magnitude >= locomotion.stageRadius - .25f,
                "The 30m stage boundary was not retained after expanding the course travel area.");
            Check(hand.IsHolding && hand.HeldBody == timber, "Timber was dropped before reaching the retained 30m boundary.");
            CheckHorizontalCrosswise(timberTarget, root, root.eulerAngles.y);
            Debug.Log("DIGIPHANT_TIMBER_UPPER_BODY_TRAVEL moved=" + pastOldBoundary.magnitude.ToString("F2") +
                " boundary=" + finalOffset.magnitude.ToString("F2") + " turn=" + Quaternion.Angle(headingBeforeTurn, root.rotation).ToString("F1") +
                " radius=" + locomotion.stageRadius.ToString("F1"));

            // A genuine trusted hand packet, rather than the manual toggle, must
            // still release after prolonged hand loss, body loss, walking and turn.
            hand.useManualGestures = false;
            hand.ResetTrackingInput();
            Check(hand.AcceptHandPacket(Packet("UpperBodyRelease", 1, "open", "fist"), t), "Trusted Timber release packet was rejected after upper-body travel.");
            Poll.Invoke(hand, new object[] { t }); controller.ApplyControls(t, 1f / 60f); t += 1f / 60f;
            Check(!hand.IsHolding, "Opening one trusted hand must release Timber after upper-body travel.");
            locomotion.ResetPosition();
            locomotion.StopAnimation();
            locomotion.enabled = previousEnabled;
            locomotion.enableLocomotion = previousAllowed;
            controller.SetPerformerCount(previousPerformerCount);
            controller.SetUpperBodyOnly(previousUpperBody);
            controller.SetInputMode(previousMode);
            hand.useManualGestures = true;
            hand.ResetHands();
        }

        static void DriveUpperBodyFrame(DigiPhantHandGrab hand, DigiPhantController controller, ref float t,
            float leftHandHeight, float lean, HandGesture left, HandGesture right)
        {
            Check(FeedPose(controller, t, leftHandHeight, lean), "Upper-body camera pose packet was rejected.");
            hand.SetManualGestures(left, right);
            Poll.Invoke(hand, new object[] { t });
            controller.ApplyControls(t, 1f / 60f);
            t += 1f / 60f;
        }

        static void DriveUpperBodyFrameWithLostHands(DigiPhantHandGrab hand, DigiPhantController controller, ref float t,
            float leftHandHeight, float lean)
        {
            Check(FeedPose(controller, t, leftHandHeight, lean), "Upper-body camera pose packet was rejected while hand detection was absent.");
            Poll.Invoke(hand, new object[] { t });
            controller.ApplyControls(t, 1f / 60f);
            t += 1f / 60f;
        }

        static bool FeedPose(DigiPhantController controller, float now, float leftHandHeight, float lean)
        {
            // Movement enum order: LeftHandHeight, RightHandHeight, LeftFootLift,
            // RightFootLift, Lean, ArmSpread.  All channels are confident so the
            // calibration checks exercised here match the live upper-body bridge.
            string values = leftHandHeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",0,0,0," +
                lean.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",0";
            return controller.AcceptPacket("{\"version\":1,\"performerCount\":1,\"upperBodyOnly\":true,\"people\":[{\"slot\":1,\"values\":[" +
                values + "],\"confidence\":[1,1,1,1,1,1]}]}", now);
        }

        static float PrivateFloat(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null && field.GetValue(instance) is float value ? value : float.NaN;
        }

        static Vector3 PrivateVector3(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null && field.GetValue(instance) is Vector3 value ? value : Vector3.positiveInfinity;
        }

        static void ReleaseAndSettle(DigiPhantHandGrab hand, ref float t)
        {
            Step(hand, ref t, HandGesture.Open, HandGesture.Open, 30);
            hand.ResetHands();
        }

        static void PrimeLiveTimberHold(DigiPhantHandGrab hand, DigiPhantController c, Rigidbody timber, ref float t, string session)
        {
            hand.ResetHands();
            hand.useManualGestures = false;
            c.SetInputMode(InputMode.Camera);
            int sequence = 0;
            for (int i = 0; i < 18; i++)
            {
                Check(hand.AcceptHandPacket(Packet(session, ++sequence, "fist", "fist"), t), "Could not prime a Timber camera fist packet.");
                Poll.Invoke(hand, new object[] { t }); c.ApplyControls(t, 1f / 60f); t += 1f / 60f;
            }
            Check(hand.IsHolding && hand.HeldBody == timber, "Camera packet stream could not acquire Timber.");
        }

        static string Packet(string session, int sequence, string left, string right) =>
            "{\"version\":1,\"performerId\":1,\"sequence\":" + sequence + ",\"session\":\"" + session + "\",\"left\":{\"state\":\"" + left + "\",\"confidence\":1},\"right\":{\"state\":\"" + right + "\",\"confidence\":1}}";

        static void CheckHorizontalCrosswise(DigiPhantGrabbable timber, Transform root, float yaw)
        {
            Vector3 longAxis = LongAxis(timber);
            Vector3 forward = Vector3.ProjectOnPlane(root.forward, Vector3.up).normalized;
            Debug.Log("DIGIPHANT_TIMBER_ALIGNMENT yaw=" + yaw + " bodyRotation=" + timber.Body.rotation.eulerAngles +
                " transformRotation=" + timber.Body.transform.rotation.eulerAngles + " axis=" + longAxis + " forward=" + forward);
            Check(Mathf.Abs(longAxis.y) < .06f, "Timber long axis was not horizontal at yaw " + yaw + ".");
            Check(Mathf.Abs(Vector3.Dot(longAxis, forward)) < .12f, "Timber was not crosswise to the elephant at yaw " + yaw + ".");
        }

        static Vector3 LongAxis(DigiPhantGrabbable timber)
        {
            // During edit-mode validation a Rigidbody configured with Interpolate
            // can expose a Transform one step behind its authoritative physics
            // rotation. Runtime alignment writes the Rigidbody, so measure it.
            Vector3 result = timber.Body.rotation * timber.localLongAxis;
            return result.sqrMagnitude < .000001f ? timber.Body.rotation * Vector3.right : result.normalized;
        }

        static void Step(DigiPhantHandGrab hand, ref float t, HandGesture left, HandGesture right, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                hand.SetManualGestures(left, right);
                Poll.Invoke(hand, new object[] { t });
                hand.controller.ApplyControls(t, 1f / 60f);
                t += 1f / 60f;
            }
        }

        static string Argument(string name) { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        static void Check(bool condition, string message) { if (!condition) throw new Exception("Timber validation: " + message); }
        static void EnsureFolder(string path)
        {
            string current = "Assets";
            foreach (string part in path.Substring("Assets/".Length).Split('/'))
            {
                string next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, part);
                current = next;
            }
        }
    }
}
