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
    /// <summary>Batch-safe behavioural tests for the hand overlay.  Every fixture is a disposable copy.</summary>
    public static class DigiPhantHandGrabValidation
    {
        const string Marker = "DIGIPHANT_HAND_GRAB_RUNTIME_VALIDATION_OK";
        static readonly MethodInfo Poll = typeof(DigiPhantHandGrab).GetMethod("PollHands", BindingFlags.Instance | BindingFlags.NonPublic);
        // Batch validation runs in edit mode, where toggling enabled on a regular
        // MonoBehaviour does not dispatch its lifecycle callback. Invoke the exact
        // private production callback so the actual OnDisable cleanup is tested.
        static readonly MethodInfo HandGrabOnDisable = typeof(DigiPhantHandGrab).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic);

        // Unity batch entry point:
        // -executeMethod DigiPhant.Editor.DigiPhantHandGrabValidation.RunAll -handGrabScene Assets/...unity
        public static void RunAll()
        {
            string source = Argument("-handGrabScene");
            if (string.IsNullOrEmpty(source) || !File.Exists(source)) throw new ArgumentException("Pass an existing -handGrabScene Assets/...unity");
            string folder = "Assets/StudentWork/HandGrabValidation";
            EnsureFolder(folder);
            string copy = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileNameWithoutExtension(source) + "_RuntimeTest.unity");
            if (!AssetDatabase.CopyAsset(source, copy)) throw new IOException("Could not copy saved scene for hand-grab validation.");
            try { Run(copy); Debug.Log(Marker + ": production gesture states, packet safety, gravity, pose overlay and locomotion isolation"); }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(copy);
                AssetDatabase.Refresh();
                // Batch validation must not leave the user's editor pointing at an
                // empty disposable scene; restore the named saved elephant scene.
                EditorSceneManager.OpenScene(source, OpenSceneMode.Single);
            }
        }

        static void Run(string path)
        {
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var hand = UnityEngine.Object.FindAnyObjectByType<DigiPhantHandGrab>(FindObjectsInactive.Include);
            Check(hand && hand.controller && hand.head && hand.trunkTip && hand.testCube, "Missing production hand-grab references.");
            Check(hand.trunk != null && hand.trunk.Length == 7, "Expected the seven production trunk bones.");
            Check(hand.pickupVolume == new Vector3(.4f, .4f, .4f), "Pickup volume must remain 40cm cubed.");
            var cubeCollider = hand.testCube.GetComponent<BoxCollider>();
            Vector3 physicalCubeSize = cubeCollider ? Vector3.Scale(cubeCollider.size, hand.testCube.transform.lossyScale) : Vector3.zero;
            Check(cubeCollider && physicalCubeSize.x > .001f && physicalCubeSize.y > .001f && physicalCubeSize.z > .001f,
                "Test pickup object must have a positive physical BoxCollider size.");
            Check(Poll != null && HandGrabOnDisable != null, "Runtime test hook is missing.");

            var c = hand.controller;
            c.SetInputMode(InputMode.TestSliders);
            c.CaptureRestPose();
            var locomotion = c.GetComponent<DigiPhantLocomotion>();
            bool locomotionWasEnabled = locomotion && locomotion.enabled;
            // Behavioural pose tests use a deliberately static base.  Idle animation
            // continues to change a correct elephant pose, so a saved frame from it
            // is not a valid "unchanged" reference.  Locomotion gets its own test.
            if (locomotion) { locomotion.StopAnimation(); locomotion.enabled = false; }
            hand.useManualGestures = true;
            hand.acquireDebounce = .12f;
            hand.handTimeout = 2f;
            hand.reachSmoothTime = .08f;
            hand.carrySmoothTime = .04f;
            hand.EnsureControllerBinding();
            foreach (var control in c.controls) { control.testValue = 0; control.current = 0; }
            float t = 10;
            Step(hand, ref t, HandGesture.Open, HandGesture.Open, 2);
            Quaternion baseHead = hand.head.localRotation;
            Quaternion baseTrunk = hand.trunk[0].localRotation;
            Vector3 originalCube = hand.testCube.position; Quaternion originalCubeRotation = hand.testCube.rotation;

            // This calls the production controller frame and the subscribed overlay code, never a test-only pose implementation.
            Step(hand, ref t, HandGesture.Open, HandGesture.Open, 12);
            Check(Quaternion.Angle(baseHead, hand.head.localRotation) < .15f && Quaternion.Angle(baseTrunk, hand.trunk[0].localRotation) < .15f,
                "Open/open must leave the base pose untouched.");
            Step(hand, ref t, HandGesture.Open, HandGesture.Fist, 12);
            Check(!hand.IsHolding && Quaternion.Angle(baseHead, hand.head.localRotation) < .15f, "Right fist alone must remain neutral.");

            Vector3 neutralTip = hand.trunkTip.position;
            float savedTrunkReach = hand.trunkReachDegrees;
            hand.trunkReachDegrees = 0;
            Step(hand, ref t, HandGesture.Fist, HandGesture.Open, 18);
            Vector3 headOnlyTip = hand.trunkTip.position;
            hand.trunkReachDegrees = savedTrunkReach;
            Step(hand, ref t, HandGesture.Open, HandGesture.Open, 30);
            Check(headOnlyTip.y < neutralTip.y - .003f,
                "The dedicated head nod did not lower the attached trunk/head chain in world space.");

            Step(hand, ref t, HandGesture.Fist, HandGesture.Open, 18);
            float headReach = Quaternion.Angle(baseHead, hand.head.localRotation);
            float trunkReach = Quaternion.Angle(baseTrunk, hand.trunk[0].localRotation);
            Vector3 reachTipBeforeGrab = hand.trunkTip.position;
            Debug.Log("DIGIPHANT_HAND_GRAB_REACH neutralTip=" + neutralTip + " headOnlyTip=" + headOnlyTip +
                      " reachTip=" + reachTipBeforeGrab + " headAngle=" + headReach + " trunkAngle=" + trunkReach);
            Check(!hand.IsHolding && headReach > .25f && trunkReach > .25f, "Left fist/right open must lower head and extend trunk.");
            Check(Mathf.Abs(Vector3.Dot((hand.head.localRotation * Vector3.up), Vector3.forward)) < .9999f,
                "Head reach appears to be an axial twist rather than a lowering rotation.");

            // At the authored, unmodified cube location, both fists must debounce before it may grab.
            hand.testCube.position = originalCube; hand.testCube.rotation = originalCubeRotation; hand.testCube.isKinematic = false;
            Step(hand, ref t, HandGesture.Fist, HandGesture.Fist, 5);
            Check(!hand.IsHolding, "Cube was acquired before the debounce interval.");
            Step(hand, ref t, HandGesture.Fist, HandGesture.Fist, 12);
            Vector3 authoredTip = hand.trunkTip.position;
            Debug.Log("DIGIPHANT_HAND_GRAB_GEOMETRY authoredCube=" + originalCube + " reachTip=" + authoredTip +
                      " pickupCenter=" + hand.PickupCenter +
                      " delta=" + hand.PickupDelta + " canPickUp=" + hand.CanPickUp() +
                      " left=" + hand.LeftGesture + " right=" + hand.RightGesture + " holding=" + hand.IsHolding);
            Check(hand.IsHolding, "Both fists could not pick up the cube at its authored location after debounce.");
            Check(hand.testCube.isKinematic, "Held cube must become kinematic.");
            // The object begins at the real acquisition contact and moves into the
            // C while the curl closes. It must never make the old magnetic-nose
            // jump, even when the terminal coil is far from the pickup point.
            Vector3 previousCube = hand.testCube.position;
            float maximumCubeStep = 0;
            for (int frame = 0; frame < 60; frame++)
            {
                hand.SetManualGestures(HandGesture.Fist, HandGesture.Fist);
                Poll.Invoke(hand, new object[] { t });
                hand.controller.ApplyControls(t, 1f / 60f); t += 1f / 60f;
                maximumCubeStep = Mathf.Max(maximumCubeStep, Vector3.Distance(previousCube, hand.testCube.position));
                previousCube = hand.testCube.position;
            }
            Check(maximumCubeStep < .12f, "Cube jumped during the reach-to-wrap transition instead of following a smooth grip.");
            Check(hand.gripAnchor, "Wrapped grip needs a visible interior anchor.");
            Check(Vector3.Distance(hand.testCube.position, hand.gripAnchor.position + Vector3.up * hand.carryLift) < .035f,
                "Held cube does not smoothly follow the interior of the curled trunk.");
            Check(hand.TryGetGripGeometry(out Vector3 gripCentre, out float measuredRadius, out float sweep),
                "Could not measure a real distal C-shaped grip from the world-space trunk pivots.");
            string pivots = string.Join(" | ", Array.ConvertAll(hand.trunk, (bone) => bone ? bone.position.ToString("F3") : "null"));
            float minCentreline = hand.MinimumGripCentrelineDistance(gripCentre);
            Debug.Log("DIGIPHANT_HAND_GRIP_GEOMETRY center=" + gripCentre + " cube=" + hand.testCube.position +
                " tip=" + hand.trunkTip.position + " radius=" + measuredRadius.ToString("F3") +
                " required=" + hand.RequiredGripClearance.ToString("F3") + " sweep=" + sweep.ToString("F1") +
                " minCentreline=" + minCentreline.ToString("F3") + " curl=" + hand.gripCurlDegrees.ToString("F1") +
                " carry=" + hand.trunkCarryDegrees.ToString("F1") + " pivots=" + pivots);
            Check(Vector3.Distance(hand.testCube.position, gripCentre) < .04f,
                "Held cube is not at the measured centre of the distal trunk curl.");
            Check(sweep >= 150f, "World-space distal pivots do not enclose the cube with a 150 degree C-shaped sweep.");
            Check(sweep <= 220f, "World-space distal pivots over-fold past a believable C-shaped grip.");
            Check(measuredRadius >= hand.RequiredGripClearance,
                "Measured curl radius lacks clearance for the current pickup object and visible trunk skin.");
            Check(hand.MaximumGripRadialError(gripCentre, measuredRadius) < .025f,
                "Distal pivots do not lie on one consistent wrapped-grip circle.");
            Check(minCentreline >= hand.RequiredGripClearance,
                "A distal trunk segment intersects the current pickup object's required clearance even though the terminal circle is wide enough.");
            Check(!hand.HasGripSelfIntersection(),
                "Distal trunk segments cross each other; this is a folded loop, not a believable wrapped C-shaped grip.");
            CheckInwardUnderWrap(hand, gripCentre);
            Check(hand.testCube.position.y > originalCube.y + .08f,
                "Holding did not visibly raise the physical cube with the trunk.");

            Step(hand, ref t, HandGesture.Fist, HandGesture.Open, 1);
            Check(!hand.IsHolding && !hand.testCube.isKinematic && Quaternion.Angle(baseHead, hand.head.localRotation) > .25f,
                "Right-hand open must drop immediately while preserving the reach pose.");
            Step(hand, ref t, HandGesture.Open, HandGesture.Open, 1);
            Check(!hand.IsHolding && Quaternion.Angle(baseHead, hand.head.localRotation) > .01f,
                "Left open must exit smoothly rather than snap the overlay.");
            Step(hand, ref t, HandGesture.Open, HandGesture.Open, 30);
            Check(Quaternion.Angle(baseHead, hand.head.localRotation) < .15f && Quaternion.Angle(baseTrunk, hand.trunk[0].localRotation) < .15f,
                "Release did not return to the current underlying base pose.");

            // A hand overlay may blend over active legacy body controls, but its
            // exit must return to those controls rather than a stale rest pose.
            var legacyHead = Array.Find(c.controls, x => x.label == "Head turn");
            var legacyTrunk = Array.Find(c.controls, x => x.label == "Trunk curl");
            if (legacyHead != null) legacyHead.testValue = .35f;
            if (legacyTrunk != null) legacyTrunk.testValue = .25f;
            // Existing controls are intentionally smoothed.  Let their production
            // smoothing converge before using them as the return-pose reference.
            Step(hand, ref t, HandGesture.Open, HandGesture.Open, 120);
            Quaternion legacyHeadPose = hand.head.localRotation, legacyTrunkPose = hand.trunk[0].localRotation;
            Step(hand, ref t, HandGesture.Fist, HandGesture.Open, 18);
            Step(hand, ref t, HandGesture.Open, HandGesture.Open, 30);
            Check(Quaternion.Angle(legacyHeadPose, hand.head.localRotation) < .15f && Quaternion.Angle(legacyTrunkPose, hand.trunk[0].localRotation) < .15f,
                "Hand overlay exit did not restore the active legacy head/trunk controls.");
            if (legacyHead != null) legacyHead.testValue = 0;
            if (legacyTrunk != null) legacyTrunk.testValue = 0;

            // The other user-requested release branch: left open releases and lets
            // the reach overlay return, even while the right fist stays closed.
            Step(hand, ref t, HandGesture.Fist, HandGesture.Open, 30);
            hand.testCube.position = hand.trunkTip.position; hand.testCube.isKinematic = false;
            Step(hand, ref t, HandGesture.Fist, HandGesture.Fist, 18);
            Check(hand.IsHolding, "Could not reacquire cube for left-open release test.");
            Step(hand, ref t, HandGesture.Open, HandGesture.Fist, 1);
            Check(!hand.IsHolding, "Left-hand open did not release a held cube.");

            if (locomotion) locomotion.enabled = locomotionWasEnabled;
            TestPacketGraceAndResets(hand, c, ref t);
            TestLocomotionIsolation(hand, c, ref t);
            TestGravityAndReachability(hand, c, ref t, originalCube, originalCubeRotation);

            hand.ResetHands(); hand.SetManualGestures(HandGesture.Open, HandGesture.Open);
            Step(hand, ref t, HandGesture.Open, HandGesture.Open, 30);
            if (locomotion) { locomotion.StopAnimation(); locomotion.enabled = false; }
            c.ApplyControls(t, 1f / 60f);
            Quaternion cleanupBase = hand.head.localRotation;
            // OnDisable is a lifecycle cleanup, unlike ordinary tracking loss.
            // Prove it releases an actual held object, then restores the base pose.
            Step(hand, ref t, HandGesture.Fist, HandGesture.Open, 24);
            hand.testCube.isKinematic = false;
            hand.testCube.position = hand.trunkTip.position;
            hand.testCube.linearVelocity = Vector3.zero;
            Step(hand, ref t, HandGesture.Fist, HandGesture.Fist, 18);
            Check(hand.IsHolding && hand.testCube.isKinematic, "Could not acquire a cube for actual OnDisable cleanup validation.");
            HandGrabOnDisable.Invoke(hand, null);
            Check(!hand.IsHolding && !hand.testCube.isKinematic, "Disable cleanup must release the cube.");
            c.ApplyControls(t, 1f / 60f);
            Check(Quaternion.Angle(cleanupBase, hand.head.localRotation) < .15f, "Disable cleanup left a hand-pose accumulation.");
        }

        static void TestPacketGraceAndResets(DigiPhantHandGrab hand, DigiPhantController c, ref float t)
        {
            hand.useManualGestures = false; c.SetInputMode(InputMode.Camera); hand.ResetHands();
            string good = "{\"version\":1,\"performerId\":1,\"sequence\":1,\"session\":\"A\",\"left\":{\"state\":\"fist\",\"confidence\":1},\"right\":{\"state\":\"fist\",\"confidence\":1}}";
            Check(hand.AcceptHandPacket(good, t), "Valid hand packet was rejected.");
            Check(!hand.AcceptHandPacket("{bad", t), "Malformed hand packet was accepted.");
            Check(!hand.AcceptHandPacket(good, t), "Duplicate hand sequence was accepted.");
            Check(!hand.AcceptHandPacket(good.Replace("\"performerId\":1", "\"performerId\":2").Replace("\"sequence\":1", "\"sequence\":2"), t), "Wrong performer packet was accepted.");
            Check(hand.AcceptHandPacket(good.Replace("\"sequence\":1", "\"sequence\":2").Replace("\"confidence\":1", "\"confidence\":0.1"), t), "Well-formed low-confidence packet should be transport-accepted.");
            Poll.Invoke(hand, new object[] { t + hand.handTimeout * .5f });
            Check(hand.LeftGesture == HandGesture.Fist && hand.RightGesture == HandGesture.Fist, "Unknown frame did not preserve the finite per-hand grace period.");
            Poll.Invoke(hand, new object[] { t + hand.handTimeout + .05f });
            Check(hand.LeftGesture == HandGesture.Unknown && hand.RightGesture == HandGesture.Unknown, "Timed-out hand data did not fail safe.");
            // A stale sampled fist is not a fresh acquire permission.  It cannot
            // grab a nearby prop after its recognizer data has expired.
            hand.testCube.isKinematic = false;
            hand.testCube.position = hand.trunkTip.position;
            Check(hand.AcceptHandPacket(Packet("StaleAcquire", 1, "fist", "fist"), t), "Fresh fist packet was rejected for stale-acquire test.");
            Poll.Invoke(hand, new object[] { t }); c.ApplyControls(t, 1f / 60f);
            Poll.Invoke(hand, new object[] { t + hand.handTimeout + .05f }); c.ApplyControls(t + hand.handTimeout + .05f, 1f / 60f);
            Check(!hand.IsHolding, "Timed-out sampled fists acquired a cube without fresh hand data.");
            Check(hand.AcceptHandPacket(good.Replace("\"A\"", "\"B\""), t + 1), "New session should reset the packet sequence.");
            // A held object is latched.  Missing packets for a full minute must
            // never turn into an implicit open palm.
            PrimeLiveHold(hand, c, ref t, "LostForMinute");
            for (int frame = 0; frame < 60 * 60; frame++)
            {
                Poll.Invoke(hand, new object[] { t });
                c.ApplyControls(t, 1f / 60f); t += 1f / 60f;
            }
            Check(hand.IsHolding && hand.testCube.isKinematic, "A held cube dropped after 60 seconds with no hand packets.");
            hand.ResetHands();
            Check(!hand.IsHolding && !hand.testCube.isKinematic, "Public ResetHands must remain a forced lifecycle/test-cleanup release.");

            // Unknown and low-confidence open are ambiguous frames, including
            // when only one anatomical hand remains detected.  None may release.
            PrimeLiveHold(hand, c, ref t, "Ambiguous");
            int sequence = 36;
            Check(!hand.AcceptHandPacket(Packet("Ambiguous", sequence + 1, "open", "fist").Replace("\"performerId\":1", "\"performerId\":2"), t),
                "Wrong-performer open packet was accepted while holding.");
            Check(hand.AcceptHandPacket(Packet("Ambiguous", ++sequence, "fist", "fist"), t), "Fresh fist packet was rejected before duplicate-open check.");
            Check(!hand.AcceptHandPacket(Packet("Ambiguous", sequence, "open", "fist"), t), "Duplicate/out-of-order open packet was accepted while holding.");
            Check(hand.IsHolding, "Rejected performer or duplicate open changed a held grab.");
            for (int frame = 0; frame < 180; frame++)
            {
                if (frame % 5 == 0)
                {
                    Check(hand.AcceptHandPacket(Packet("Ambiguous", ++sequence, "unknown", "unknown", .1f), t), "Unknown hand packet was rejected.");
                    Check(hand.AcceptHandPacket(Packet("Ambiguous", ++sequence, "open", "fist", .1f), t), "Low-confidence open packet was rejected.");
                }
                Poll.Invoke(hand, new object[] { t });
                c.ApplyControls(t, 1f / 60f); t += 1f / 60f;
            }
            Check(hand.IsHolding, "Unknown or low-confidence open released a latched grab.");
            for (int frame = 0; frame < 180; frame++)
            {
                if (frame % 5 == 0) Check(hand.AcceptHandPacket(Packet("Ambiguous", ++sequence, "unknown", "fist"), t), "One-hand-lost packet was rejected.");
                Poll.Invoke(hand, new object[] { t });
                c.ApplyControls(t, 1f / 60f); t += 1f / 60f;
            }
            Check(hand.IsHolding, "Losing one hand released a latched grab.");
            hand.ResetHands();

            // A newly observed, confident open from either anatomical hand is the
            // only gesture release.  A queued fist must not undo it that frame.
            AssertExplicitOpenReleases(hand, c, ref t, "LeftOpen", "open", "fist");
            AssertExplicitOpenReleases(hand, c, ref t, "RightOpen", "fist", "open");
            AssertExplicitOpenReleases(hand, c, ref t, "BothOpen", "open", "open");
            AssertOpenSurvivesTrackingReset(hand, c, ref t);

            // Controller tracking resets clear stale input but intentionally do
            // not drop a physical object that is already being held.
            PrimeLiveHold(hand, c, ref t, "PerformerReset"); c.SetPerformerCount(2);
            Check(hand.IsHolding && hand.LeftGesture == HandGesture.Unknown, "Performer-count reset dropped the latched object or retained stale input.");
            hand.ResetHands(); c.SetPerformerCount(1);
            PrimeLiveHold(hand, c, ref t, "ModeReset"); c.SetInputMode(InputMode.TestSliders);
            Check(hand.IsHolding && hand.LeftGesture == HandGesture.Unknown, "Input-mode reset dropped the latched object or retained stale input.");
            hand.ResetHands(); c.SetInputMode(InputMode.Camera);
            PrimeLiveHold(hand, c, ref t, "UpperReset"); c.SetUpperBodyOnly(true);
            Check(hand.IsHolding && hand.LeftGesture == HandGesture.Unknown, "Upper-body reset dropped the latched object or retained stale input.");
            hand.ResetHands(); c.SetUpperBodyOnly(false);
            PrimeLiveHold(hand, c, ref t, "ReassignReset"); c.ReassignPeople();
            Check(hand.IsHolding && hand.LeftGesture == HandGesture.Unknown, "Reassign reset dropped the latched object or retained stale input.");
            hand.ResetHands();
            c.SetUpperBodyOnly(false); c.SetPerformerCount(1); c.SetInputMode(InputMode.TestSliders); hand.useManualGestures = true;
        }

        static void AssertExplicitOpenReleases(DigiPhantHandGrab hand, DigiPhantController c, ref float t, string session, string openLeft, string openRight)
        {
            PrimeLiveHold(hand, c, ref t, session);
            int sequence = 37;
            Check(hand.AcceptHandPacket(Packet(session, sequence, openLeft, openRight), t), "Explicit-open packet was rejected for " + session + ".");
            Check(hand.AcceptHandPacket(Packet(session, ++sequence, "fist", "fist"), t), "Post-open fist packet was rejected for " + session + ".");
            Poll.Invoke(hand, new object[] { t });
            c.ApplyControls(t, 1f / 60f); t += 1f / 60f;
            Check(!hand.IsHolding && !hand.testCube.isKinematic && hand.LastReleaseReason == "Open palm",
                "A fresh " + session + " did not release immediately, or re-grabbed in the same frame.");
            Poll.Invoke(hand, new object[] { t }); c.ApplyControls(t, 1f / 60f); t += 1f / 60f;
            Check(!hand.IsHolding, "Post-open fists re-grabbed without a fresh acquire debounce for " + session + ".");
            hand.ResetHands();
        }

        static void AssertOpenSurvivesTrackingReset(DigiPhantHandGrab hand, DigiPhantController c, ref float t)
        {
            PrimeLiveHold(hand, c, ref t, "OpenBeforeReset");
            Check(hand.AcceptHandPacket(Packet("OpenBeforeReset", 37, "open", "fist"), t), "Open-before-reset packet was rejected.");
            c.ReassignPeople(); // fires the production TrackingReset event
            Poll.Invoke(hand, new object[] { t }); c.ApplyControls(t, 1f / 60f); t += 1f / 60f;
            Check(!hand.IsHolding && hand.LastReleaseReason == "Open palm",
                "A trusted open accepted before TrackingReset was not consumed as a release.");
            hand.ResetHands();
        }

        static void PrimeLiveHold(DigiPhantHandGrab hand, DigiPhantController c, ref float t, string session)
        {
            hand.useManualGestures = false; c.SetInputMode(InputMode.Camera); hand.ResetHands();
            int sequence = 0;
            // Reach first, as a real camera stream would.  The safety fixture cube
            // is then placed at the settled physical tip; it must not depend on the
            // scene's authored-cube geometry which has its own dedicated test.
            for (int i = 0; i < 24; i++)
            {
                Check(hand.AcceptHandPacket(Packet(session, ++sequence, "fist", "open"), t), "Could not prime a reach hand packet.");
                c.ApplyControls(t, 1f / 60f); t += 1f / 60f;
            }
            hand.testCube.isKinematic = false; hand.testCube.position = hand.trunkTip.position; hand.testCube.linearVelocity = Vector3.zero;
            for (int i = 0; i < 12; i++)
            {
                Check(hand.AcceptHandPacket(Packet(session, ++sequence, "fist", "fist"), t), "Could not prime a hold hand packet.");
                c.ApplyControls(t, 1f / 60f); t += 1f / 60f;
            }
            if (!hand.IsHolding)
                Debug.Log("DIGIPHANT_HAND_GRAB_PRIME_FAILED tip=" + hand.trunkTip.position + " center=" + hand.PickupCenter +
                          " delta=" + hand.PickupDelta + " canPickUp=" + hand.CanPickUp() + " left=" + hand.LeftGesture + " right=" + hand.RightGesture);
            Check(hand.IsHolding, "Could not prime an actual held cube for reset safety validation.");
        }
        static string Packet(string session, int sequence, string left, string right, float confidence = 1f) =>
            "{\"version\":1,\"performerId\":1,\"sequence\":" + sequence + ",\"session\":\"" + session + "\",\"left\":{\"state\":\"" + left + "\",\"confidence\":" + confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "},\"right\":{\"state\":\"" + right + "\",\"confidence\":" + confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";

        static void TestLocomotionIsolation(DigiPhantHandGrab hand, DigiPhantController c, ref float t)
        {
            var loco = c.GetComponent<DigiPhantLocomotion>();
            if (!loco || !loco.Initialise()) throw new Exception("Cannot validate locomotion isolation: locomotion is not complete.");
            loco.inputSmoothing = 0; loco.testForward = .4f; Vector3 before = loco.travelRoot.position;
            Step(hand, ref t, HandGesture.Fist, HandGesture.Open, 30);
            Check(Vector3.Distance(before, loco.travelRoot.position) > .4f, "Hand reach interfered with existing travel-root locomotion.");
            loco.StopMotion(); loco.ResetPosition();
        }

        static void TestGravityAndReachability(DigiPhantHandGrab hand, DigiPhantController c, ref float t, Vector3 authored, Quaternion rotation)
        {
            // Re-run a real gravity drop at separated idle-animation phases.  This
            // proves the authored cube remains reachable rather than only matching
            // one frozen frame of an animated elephant.
            for (int phase = 0; phase < 3; phase++)
            {
                hand.ResetHands(); hand.testCube.isKinematic = false; hand.testCube.position = authored; hand.testCube.rotation = rotation;
                hand.testCube.linearVelocity = Vector3.zero; hand.testCube.angularVelocity = Vector3.zero; Physics.SyncTransforms();
                var oldMode = Physics.simulationMode; Physics.simulationMode = SimulationMode.Script;
                try { for (int i = 0; i < 180; i++) Physics.Simulate(1f / 60f); }
                finally { Physics.simulationMode = oldMode; }
                Vector3 grounded = hand.testCube.position;
                Check(grounded.y > -.08f, "Dropped cube fell through the local/scene floor; no usable gravity catch exists.");
                Step(hand, ref t, HandGesture.Fist, HandGesture.Open, 24);
                Vector3 reachTip = hand.trunkTip.position;
                Debug.Log("DIGIPHANT_HAND_GRAB_GEOMETRY phase=" + phase + " groundCube=" + grounded + " reachTip=" + reachTip + " delta=" + (grounded - reachTip) + " maxDelta=.2");
                Step(hand, ref t, HandGesture.Fist, HandGesture.Fist, 18);
                Check(hand.IsHolding, "After real gravity at idle phase " + phase + ", both fists cannot pick up the authored cube without moving it.");
                Step(hand, ref t, HandGesture.Open, HandGesture.Open, 1);
                Step(hand, ref t, HandGesture.Open, HandGesture.Open, 60);
            }
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
        static void CheckInwardUnderWrap(DigiPhantHandGrab hand, Vector3 centre)
        {
            int start = Mathf.Max(0, hand.trunk.Length - Mathf.Clamp(hand.gripJointCount, 1, 7));
            Transform first = hand.trunk[start];
            Transform second = start + 1 < hand.trunk.Length ? hand.trunk[start + 1] : hand.trunkTip;
            Transform body = hand.reachReferenceRoot ? hand.reachReferenceRoot : hand.transform;
            Vector3 forward = Vector3.ProjectOnPlane(body.forward, Vector3.up);
            Check(first && second && forward.sqrMagnitude > .000001f, "Cannot measure the under-wrap orientation.");
            forward.Normalize();
            float startFront = Vector3.Dot(first.position - centre, forward);
            float terminalTowardBody = -Vector3.Dot(hand.trunkTip.position - centre, forward);
            Transform lastBone = hand.trunk[hand.trunk.Length - 1];
            Vector3 terminalTangent = hand.trunkTip.position - lastBone.position;
            Check(terminalTangent.sqrMagnitude > .000001f, "Cannot measure the terminal under-wrap direction.");
            terminalTangent.Normalize();
            float terminalInwardTangent = -Vector3.Dot(terminalTangent, forward);
            float terminalBelowCentre = centre.y - hand.trunkTip.position.y;
            float descend = first.position.y - second.position.y;
            float lowest = hand.trunkTip.position.y;
            for (int i = start; i < hand.trunk.Length; i++) lowest = Mathf.Min(lowest, hand.trunk[i].position.y);
            float passesUnder = centre.y - lowest;
            Debug.Log("DIGIPHANT_HAND_INWARD_UNDERWRAP startFront=" + startFront.ToString("F3") +
                " inward=" + terminalTowardBody.ToString("F3") + " tangentInward=" + terminalInwardTangent.ToString("F3") +
                " belowCentre=" + terminalBelowCentre.ToString("F3") + " descend=" + descend.ToString("F3") +
                " under=" + passesUnder.ToString("F3"));
            Check(startFront > .015f && terminalTowardBody > .015f && terminalInwardTangent > .08f &&
                terminalBelowCentre >= 0 && descend > .004f && passesUnder > .08f,
                "Closed grip is not an inward under-wrap: it must descend on the outside, pass under the object, and finish toward the body without an exterior upward hook.");
        }
        static string Argument(string name) { var a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        static void Check(bool condition, string message) { if (!condition) throw new Exception("Hand-grab runtime validation: " + message); }
        static void EnsureFolder(string path)
        {
            string current = "Assets"; foreach (var part in path.Substring("Assets/".Length).Split('/')) { string next = current + "/" + part; if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, part); current = next; }
        }
    }
}
