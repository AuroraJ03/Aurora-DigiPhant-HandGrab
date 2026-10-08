using System;
using System.IO;
using System.Linq;
using DigiPhant;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DigiPhant.Editor
{
    public static class DigiPhantHandGrabSetup
    {
        const string RootName = "DigiPhant Hand Grab Test";
        [MenuItem("DigiPhant/Hand Grab/Inject into Open Scene")]
        public static void InjectIntoOpenScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("Save the elephant scene before injecting Hand Grab.");
            var controller = UnityEngine.Object.FindObjectsByType<DigiPhantController>(FindObjectsInactive.Include, FindObjectsSortMode.None).SingleOrDefault();
            if (!controller) throw new InvalidOperationException("Open the saved scene containing DigiPhant Controls.");
            string backup = AssetDatabase.GenerateUniqueAssetPath(Path.ChangeExtension(scene.path, null) + "_BeforeHandGrab.unity");
            if (!AssetDatabase.CopyAsset(scene.path, backup)) throw new IOException("Could not create scene backup.");
            var old = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(x => x.name == RootName && x.scene == scene);
            if (old) Undo.DestroyObjectImmediate(old);
            var root = new GameObject(RootName); Undo.RegisterCreatedObjectUndo(root, "Add hand grab test");
            var interaction = root.AddComponent<DigiPhantHandGrab>(); interaction.controller = controller;
            CreateGripAnchor(root.transform, interaction);
            // Controls is a sibling of the elephant travel hierarchy. Existing mapped
            // bones are the reliable integration point, rather than assuming hierarchy.
            interaction.head = controller.controls.FirstOrDefault(c => c.label == "Head turn")?.bones?.FirstOrDefault();
            var trunkControl = controller.controls.FirstOrDefault(c => c.label == "Trunk curl");
            interaction.trunk = trunkControl?.bones?.Where(x => x).ToArray() ?? Array.Empty<Transform>();
            interaction.trunkTip = interaction.trunk.LastOrDefault()?.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(x => x.name == "elephant_Trunk7-nub_bone") ?? interaction.trunk.LastOrDefault();
            if (trunkControl != null) interaction.trunkReachAxis = trunkControl.localAxis;
            // "Head turn" is yaw on this rig.  The interaction needs a dedicated
            // local nod axis; local Z is verified against the terminal trunk height
            // below rather than reusing the unrelated head-turn control.
            interaction.headLowerAxis = Vector3.forward;
            interaction.trunkReachDirection = -1;
            ConfigureWrappedGripDefaults(interaction);
            if (!interaction.head || interaction.trunk.Length != 7 || !interaction.trunkTip)
                throw new InvalidOperationException("Could not find the elephant head/trunk bones.");
            interaction.headReachReference = interaction.head.localRotation;
            interaction.trunkReachReference = interaction.trunk.Select(x => x.localRotation).ToArray();
            var locomotion = controller.GetComponent<DigiPhantLocomotion>();
            interaction.reachReferenceRoot = locomotion && locomotion.travelRoot ? locomotion.travelRoot : controller.transform;
            interaction.headReachWorldRelative = Quaternion.Inverse(interaction.reachReferenceRoot.rotation) * interaction.head.rotation;
            interaction.headLowerDirection = EvaluateTip(interaction, -1, 0).y < EvaluateTip(interaction, 1, 0).y ? -1 : 1;
            // Measure the real rig in its stable authored reach pose.  Do not assume world forward or
            // a generic elephant hierarchy: this makes the authored cube reachable by
            // this exact scene's terminal trunk bone.
            Vector3 reachTip = EvaluateTip(interaction, interaction.headLowerDirection, 0);
            float upDirection = EvaluateTip(interaction, interaction.headLowerDirection, 1).y >= EvaluateTip(interaction, interaction.headLowerDirection, -1).y ? 1 : -1;
            interaction.trunkCarryDirection = upDirection;
            Vector3 carryTip = EvaluateTip(interaction, interaction.headLowerDirection, upDirection);
            if (carryTip.y <= reachTip.y + .08f) throw new InvalidOperationException("The authored holding curl does not lift the trunk tip enough to pick up the test cube.");
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.name = "Hand Grab Cube (20cm)"; cube.transform.SetParent(root.transform, true);
            cube.transform.localScale = Vector3.one * .2f;
            Vector3 trunkOutward = reachTip - interaction.trunk.Last().position;
            if (trunkOutward.sqrMagnitude < .0001f) trunkOutward = controller.transform.forward;
            cube.transform.position = reachTip + trunkOutward.normalized * .06f;
            var cubeRenderer = cube.GetComponent<MeshRenderer>();
            cubeRenderer.sharedMaterial = GetOrCreateMaterial("HandGrabCubeOrange", new Color(1f, .34f, .04f, 1));
            var body = cube.AddComponent<Rigidbody>(); body.mass = .2f; body.interpolation = RigidbodyInterpolation.Interpolate;
            interaction.testCube = body;
            // Now that the fresh test object exists, fit the real rig to its actual
            // physical clearance rather than leaving the provisional default pose.
            ConfigureWrappedGripDefaults(interaction);
            // The course floor is below the elephant's raised trunk.  This compact,
            // visible pickup pad keeps the physical cube inside the authored 40cm
            // volume after gravity, so the untouched default test remains grab-able.
            var pad = GameObject.CreatePrimitive(PrimitiveType.Cube); pad.name = "Hand Grab Pickup Pad"; pad.transform.SetParent(root.transform, true);
            float restingCenterY = reachTip.y;
            pad.transform.position = new Vector3(cube.transform.position.x, restingCenterY - .125f, cube.transform.position.z);
            pad.transform.localScale = new Vector3(.8f, .05f, .8f);
            pad.GetComponent<MeshRenderer>().sharedMaterial = GetOrCreateMaterial("HandGrabPadBlue", new Color(.04f, .22f, .45f, 1));
            EditorUtility.SetDirty(root); EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = root;
            Debug.Log("DIGIPHANT_HAND_GRAB_INJECTED: " + scene.path + " backup=" + backup);
        }

        [MenuItem("DigiPhant/Hand Grab/Upgrade Wrapped Grip in Open Scene")]
        public static void UpgradeWrappedGripInOpenScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("Save the elephant scene before upgrading Hand Grab.");
            var interaction = UnityEngine.Object.FindAnyObjectByType<DigiPhantHandGrab>(FindObjectsInactive.Include);
            if (!interaction) throw new InvalidOperationException("Open the saved scene which already has DigiPhant Hand Grab.");
            string backup = AssetDatabase.GenerateUniqueAssetPath(Path.ChangeExtension(scene.path, null) + "_BeforeWrappedGrip.unity");
            if (!AssetDatabase.CopyAsset(scene.path, backup)) throw new IOException("Could not create scene backup.");
            if (!interaction.controller) interaction.controller = UnityEngine.Object.FindAnyObjectByType<DigiPhantController>(FindObjectsInactive.Include);
            if (!interaction.trunkTip && interaction.trunk != null && interaction.trunk.Length > 0)
                interaction.trunkTip = interaction.trunk.Last().GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == "elephant_Trunk7-nub_bone") ?? interaction.trunk.Last();
            CreateGripAnchor(interaction.transform, interaction);
            ConfigureWrappedGripDefaults(interaction);
            EditorUtility.SetDirty(interaction); EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = interaction.gameObject;
            Debug.Log("DIGIPHANT_HAND_GRAB_WRAPPED_GRIP_UPGRADED: " + scene.path + " backup=" + backup);
        }

        [MenuItem("DigiPhant/Hand Grab/Upgrade Inward Under-Wrap in Open Scene")]
        public static void UpgradeInwardUnderWrapInOpenScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("Save the elephant scene before upgrading Hand Grab.");
            var interaction = UnityEngine.Object.FindAnyObjectByType<DigiPhantHandGrab>(FindObjectsInactive.Include);
            if (!interaction) throw new InvalidOperationException("Open the saved scene which already has DigiPhant Hand Grab.");
            if (interaction.trunk == null || interaction.trunk.Length < 4 || !interaction.trunkTip)
                throw new InvalidOperationException("The existing Hand Grab trunk rig is incomplete.");

            // This is deliberately animation-only.  In particular, do not call
            // ConfigureWrappedGripDefaults here: users may have tuned packet grace,
            // carry speed, or other interaction behaviour after the original setup.
            string backup = AssetDatabase.GenerateUniqueAssetPath(Path.ChangeExtension(scene.path, null) + "_BeforeInwardGrip.unity");
            if (!AssetDatabase.CopyAsset(scene.path, backup)) throw new IOException("Could not create scene backup.");
            CreateGripAnchor(interaction.transform, interaction);
            BuildClosedGripPose(interaction);
            EditorUtility.SetDirty(interaction); EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = interaction.gameObject;
            Debug.Log("DIGIPHANT_HAND_GRAB_INWARD_UNDERWRAP_UPGRADED: " + scene.path + " backup=" + backup);
        }

        static void CreateGripAnchor(Transform root, DigiPhantHandGrab interaction)
        {
            if (interaction.gripAnchor) return;
            var existing = root.Find("Hand Grab Grip Anchor");
            if (existing) { interaction.gripAnchor = existing; return; }
            var anchor = new GameObject("Hand Grab Grip Anchor");
            anchor.transform.SetParent(root, false);
            interaction.gripAnchor = anchor.transform;
        }

        static void ConfigureWrappedGripDefaults(DigiPhantHandGrab interaction)
        {
            // Missing recognitions are common when the performer rotates a fist.
            // Two seconds prevents accidental drops while preserving explicit opens.
            interaction.handTimeout = 2f;
            interaction.trunkCarryDegrees = 12f;
            interaction.gripJointCount = SelectGripJointCount(interaction);
            // Preserve a student's resized test object. A fresh injected 20cm cube
            // uses .20m; an existing larger cube expands the required wrap radius.
            interaction.gripRadius = interaction.testCube ? interaction.RequiredGripClearance : .20f;
            if (interaction.testCube) BuildClosedGripPose(interaction);
        }

        static void BuildClosedGripPose(DigiPhantHandGrab hand)
        {
            if (hand.trunk == null || hand.trunk.Length < 4 || !hand.trunkTip) throw new InvalidOperationException("The wrapped grip needs a terminal trunk chain.");
            float radius = hand.RequiredGripClearance + .05f;
            var saved = hand.trunk.ToDictionary(b => b, b => b.localRotation);
            Quaternion savedHead = hand.head.localRotation;
            int savedGripJointCount = hand.gripJointCount;
            int selectedGripJointCount = savedGripJointCount;
            try
            {
                hand.head.localRotation = hand.headReachReference * Quaternion.AngleAxis(hand.headLowerDegrees * hand.headLowerDirection, hand.headLowerAxis.normalized);
                for (int i = 0; i < hand.trunk.Length; i++) hand.trunk[i].localRotation = hand.trunkReachReference[i] * Quaternion.AngleAxis(hand.trunkReachDegrees * hand.trunkReachDirection + hand.trunkCarryDegrees * hand.trunkCarryDirection, hand.trunkReachAxis.normalized);
                var reachPose = hand.trunk.ToDictionary(b => b, b => b.localRotation);

                // Make an actual constant-radius centreline, joint by joint.  The
                // former implementation rotated the tangent vector by theta and
                // used that as a chord: that straightened/offset the nose instead
                // of placing its pivots on a circle.  This computes the next chord
                // endpoint on the circle and rotates only the selected trunk bone
                // to meet that point, so it is stable for a future cylindrical log.
                GripPose best = default;
                bool found = false;
                for (int count = 4; count <= Mathf.Min(6, hand.trunk.Length); count++)
                {
                    int start = hand.trunk.Length - count;
                    float sweep = ExpectedSweep(hand, start, radius);
                    if (sweep < 150f || sweep > 220f) continue;
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        // Geometry helpers use the serialized selected-joint count.
                        // Set it for this candidate so a five-joint proposal is not
                        // accidentally judged using yesterday's six-joint subset.
                        hand.gripJointCount = count;
                        RestorePose(reachPose);
                        ApplyExactCircle(hand, start, radius, sign, out Vector3 centre);
                        if (!hand.TryGetGripGeometry(out _, out float measuredRadius, out float measuredSweep) ||
                            measuredRadius < hand.RequiredGripClearance || measuredSweep < 150f || measuredSweep > 220f ||
                            hand.MinimumGripCentrelineDistance(centre) < hand.RequiredGripClearance || hand.HasGripSelfIntersection()) continue;
                        if (!IsInwardUnderWrap(hand, start, centre, out float orientationScore)) continue;
                        // The reference is an under-wrap, not an upward hook: the
                        // distal chain comes down on the outside/front of the object,
                        // travels under it, and ends on the body-facing side.  This
                        // uses travelRoot's basis because Controls is a sibling of
                        // the animated elephant hierarchy and is not a body basis.
                        float score = orientationScore - Mathf.Abs(measuredRadius - radius);
                        if (!found || score > best.score)
                        {
                            found = true;
                            best = new GripPose { score = score, count = count, centre = centre, rotations = hand.trunk.Select(b => b.localRotation).ToArray() };
                        }
                    }
                }
                if (!found) throw new InvalidOperationException("The supplied trunk does not have a safe 150–220 degree wrapped grip for this object.");
                for (int i = 0; i < hand.trunk.Length; i++) hand.trunk[i].localRotation = best.rotations[i];
                int bestStart = hand.trunk.Length - best.count;
                hand.trunkGripReference = best.rotations;
                hand.gripAnchorRoot = hand.trunk[bestStart];
                hand.gripAnchorLocalPosition = hand.gripAnchorRoot.InverseTransformPoint(best.centre);
                selectedGripJointCount = best.count;
                hand.gripJointCount = selectedGripJointCount; hand.gripRadius = radius;
                Debug.Log("DIGIPHANT_HAND_CLOSED_GRIP_BUILT radius=" + radius.ToString("F3") + " start=" + bestStart + " centre=" + best.centre + " count=" + best.count);
            }
            finally { RestorePose(saved); hand.head.localRotation = savedHead; hand.gripJointCount = selectedGripJointCount; }
        }

        struct GripPose { public float score; public int count; public Vector3 centre; public Quaternion[] rotations; }

        static bool IsInwardUnderWrap(DigiPhantHandGrab hand, int start, Vector3 centre, out float score)
        {
            score = float.NegativeInfinity;
            Transform first = hand.trunk[start];
            Transform second = start + 1 < hand.trunk.Length ? hand.trunk[start + 1] : hand.trunkTip;
            if (!first || !second || !hand.trunkTip) return false;

            Transform body = hand.reachReferenceRoot ? hand.reachReferenceRoot : hand.transform;
            Vector3 forward = Vector3.ProjectOnPlane(body.forward, Vector3.up);
            if (forward.sqrMagnitude < .000001f) return false;
            forward.Normalize();

            Vector3 outerStart = first.position - centre;
            Vector3 terminal = hand.trunkTip.position - centre;
            Transform lastBone = hand.trunk[hand.trunk.Length - 1];
            Vector3 terminalTangent = hand.trunkTip.position - lastBone.position;
            if (terminalTangent.sqrMagnitude < .000001f) return false;
            terminalTangent.Normalize();
            float startFront = Vector3.Dot(outerStart, forward);
            float terminalTowardBody = -Vector3.Dot(terminal, forward);
            float terminalInwardTangent = -Vector3.Dot(terminalTangent, forward);
            float terminalBelowCentre = centre.y - hand.trunkTip.position.y;
            float descend = first.position.y - second.position.y;

            float lowest = float.PositiveInfinity;
            for (int i = start; i < hand.trunk.Length; i++) lowest = Mathf.Min(lowest, hand.trunk[i].position.y);
            lowest = Mathf.Min(lowest, hand.trunkTip.position.y);
            float passesUnder = centre.y - lowest;

            // Small thresholds tolerate animation blending and this particular
            // elephant's skin/nub offset, while excluding an exterior upturned hook.
            if (startFront <= .015f || terminalTowardBody <= .015f || terminalInwardTangent <= .08f ||
                terminalBelowCentre < 0 || descend <= .004f || passesUnder <= .08f) return false;

            score = startFront * 12f + terminalTowardBody * 12f + descend * 8f +
                    passesUnder * 8f + terminalInwardTangent * 6f + terminalBelowCentre * 3f;
            return true;
        }
        static void RestorePose(System.Collections.Generic.Dictionary<Transform, Quaternion> rotations)
        {
            foreach (var pair in rotations) if (pair.Key) pair.Key.localRotation = pair.Value;
        }
        static float ExpectedSweep(DigiPhantHandGrab hand, int start, float radius)
        {
            float degrees = 0;
            for (int i = start; i < hand.trunk.Length; i++)
            {
                Transform next = i + 1 < hand.trunk.Length ? hand.trunk[i + 1] : hand.trunkTip;
                float chord = Vector3.Distance(hand.trunk[i].position, next.position);
                if (chord >= radius * 2f) return float.PositiveInfinity;
                degrees += 2f * Mathf.Asin(chord / (2f * radius)) * Mathf.Rad2Deg;
            }
            return degrees;
        }
        static void ApplyExactCircle(DigiPhantHandGrab hand, int start, float radius, int curveSign, out Vector3 centre)
        {
            Transform first = hand.trunk[start];
            Transform previous = start > 0 ? hand.trunk[start - 1] : null;
            Transform following = start + 1 < hand.trunk.Length ? hand.trunk[start + 1] : hand.trunkTip;
            Vector3 normal = first.TransformDirection(hand.trunkReachAxis);
            if (normal.sqrMagnitude < .000001f) normal = first.up;
            normal.Normalize();
            Vector3 tangent = previous ? first.position - previous.position : following.position - first.position;
            tangent = Vector3.ProjectOnPlane(tangent, normal);
            if (tangent.sqrMagnitude < .000001f) tangent = Vector3.ProjectOnPlane(following.position - first.position, normal);
            tangent.Normalize();
            centre = first.position + Vector3.Cross(normal, tangent).normalized * radius * curveSign;
            Vector3 radial = first.position - centre;
            for (int i = start; i < hand.trunk.Length; i++)
            {
                Transform bone = hand.trunk[i];
                Transform next = i + 1 < hand.trunk.Length ? hand.trunk[i + 1] : hand.trunkTip;
                float chord = Vector3.Distance(bone.position, next.position);
                float theta = 2f * Mathf.Asin(Mathf.Clamp(chord / (2f * radius), -.9999f, .9999f)) * Mathf.Rad2Deg;
                Vector3 desiredNext = centre + Quaternion.AngleAxis(curveSign * theta, normal) * radial;
                Vector3 current = next.position - bone.position;
                Vector3 desired = desiredNext - bone.position;
                if (current.sqrMagnitude > .000001f && desired.sqrMagnitude > .000001f)
                    bone.rotation = Quaternion.FromToRotation(current, desired) * bone.rotation;
                radial = desiredNext - centre;
            }
        }

        static int SelectGripJointCount(DigiPhantHandGrab interaction)
        {
            if (interaction.trunk == null || interaction.trunk.Length < 2 || !interaction.trunkTip) return 6;
            // Include as many distal joints as needed for a 150° arc at the actual
            // required radius, up to six so the head/proximal trunk remain owned by
            // the reach overlay rather than the grip.
            for (int count = 4; count <= 7; count++)
            {
                int start = Mathf.Max(0, interaction.trunk.Length - count); float arc = 0;
                for (int i = start; i < interaction.trunk.Length; i++)
                {
                    Transform next = i + 1 < interaction.trunk.Length ? interaction.trunk[i + 1] : interaction.trunkTip;
                    if (interaction.trunk[i] && next) arc += Vector3.Distance(interaction.trunk[i].position, next.position);
                }
                if (arc / Mathf.Max(.14f, interaction.gripRadius) * Mathf.Rad2Deg >= 150f) return count;
            }
            return 6;
        }

        static Vector3 EvaluateTip(DigiPhantHandGrab interaction, float headDirection, float carryDirection)
        {
            var rotations = new System.Collections.Generic.Dictionary<Transform, Quaternion>();
            if (interaction.head) rotations[interaction.head] = interaction.head.localRotation;
            foreach (var bone in interaction.trunk) if (bone) rotations[bone] = bone.localRotation;
            try
            {
                if (interaction.head)
                {
                    Quaternion headBase = interaction.headReachReference;
                    if (interaction.head.parent && interaction.reachReferenceRoot)
                        headBase = Quaternion.Inverse(interaction.head.parent.rotation) * (interaction.reachReferenceRoot.rotation * interaction.headReachWorldRelative);
                    interaction.head.localRotation = headBase * Quaternion.AngleAxis(interaction.headLowerDegrees * headDirection, interaction.headLowerAxis.normalized);
                }
                for (int i = 0; i < interaction.trunk.Length; i++)
                    if (interaction.trunk[i])
                    {
                        float degrees = interaction.trunkReachDegrees * interaction.trunkReachDirection +
                            interaction.trunkCarryDegrees * interaction.trunkCarryDirection * carryDirection;
                        interaction.trunk[i].localRotation = interaction.trunkReachReference[i] * Quaternion.AngleAxis(degrees, interaction.trunkReachAxis.normalized);
                    }
                return interaction.trunkTip.position;
            }
            finally { foreach (var pair in rotations) if (pair.Key) pair.Key.localRotation = pair.Value; }
        }

        // Unity batch entry point for rig measurements. It is deliberately read-only:
        // this lets us tune a future stump radius against the actual supplied rig.
        public static void DiagnoseGripGeometry()
        {
            var hand = UnityEngine.Object.FindAnyObjectByType<DigiPhantHandGrab>(FindObjectsInactive.Include);
            if (!hand || hand.trunk == null || hand.trunk.Length < 3 || !hand.trunkTip) throw new InvalidOperationException("Hand Grab rig is incomplete.");
            var values = hand.trunk.Select((bone, i) => "b" + i + "=" + bone.position);
            Debug.Log("DIGIPHANT_HAND_GRIP_RIG targetRadius=" + hand.gripRadius.ToString("0.000") + " tip=" + hand.trunkTip.position + " " + string.Join(" | ", values));
        }

        // Unity batch entry point: -executeMethod DigiPhant.Editor.DigiPhantHandGrabSetup.UpgradeWrappedGripFromCommandLine
        // -handGrabScene Assets/...unity
        public static void UpgradeWrappedGripFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-handGrabScene");
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Pass -handGrabScene Assets/...unity");
            EditorSceneManager.OpenScene(args[index + 1], OpenSceneMode.Single);
            UpgradeWrappedGripInOpenScene();
        }

        // Unity batch entry point: -executeMethod DigiPhant.Editor.DigiPhantHandGrabSetup.UpgradeInwardUnderWrapFromCommandLine
        // -handGrabScene Assets/...unity
        public static void UpgradeInwardUnderWrapFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-handGrabScene");
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Pass -handGrabScene Assets/...unity");
            EditorSceneManager.OpenScene(args[index + 1], OpenSceneMode.Single);
            UpgradeInwardUnderWrapInOpenScene();
        }

        static Material GetOrCreateMaterial(string materialName, Color color)
        {
            const string folder = "Assets/DigiPhant/Generated";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/DigiPhant", "Generated");
            string path = folder + "/" + materialName + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!material)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (!shader) throw new InvalidOperationException("No compatible lit shader was found for the hand-grab test material.");
                material = new Material(shader); AssetDatabase.CreateAsset(material, path);
            }
            material.color = color; EditorUtility.SetDirty(material); return material;
        }

        // Unity batch entry point: -executeMethod DigiPhant.Editor.DigiPhantHandGrabSetup.InjectFromCommandLine
        // -handGrabScene Assets/DigiPhant/Scenes/DigiPhant.unity
        public static void InjectFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-handGrabScene");
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Pass -handGrabScene Assets/...unity");
            string path = args[index + 1];
            if (!File.Exists(path)) throw new FileNotFoundException("Hand Grab scene was not found", path);
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            InjectIntoOpenScene();
        }

        [MenuItem("DigiPhant/Hand Grab/Validate Open Scene")]
        public static void ValidateOpenScene()
        {
            var hand = UnityEngine.Object.FindAnyObjectByType<DigiPhantHandGrab>();
            if (!hand || !hand.controller || !hand.head || hand.trunk == null || hand.trunk.Length != 7 || !hand.trunkTip || !hand.testCube)
                throw new Exception("Hand Grab scene setup is incomplete.");
            if (hand.pickupVolume != new Vector3(.4f, .4f, .4f)) throw new Exception("Pickup volume must be 40cm cubed.");
            var collider = hand.testCube.GetComponent<BoxCollider>();
            Vector3 cubeSize = collider ? Vector3.Scale(collider.size, hand.testCube.transform.lossyScale) : Vector3.zero;
            if (!collider || cubeSize.x <= .001f || cubeSize.y <= .001f || cubeSize.z <= .001f)
                throw new Exception("Pickup cube must have a positive BoxCollider size.");
            if (!hand.AcceptHandPacket("{\"version\":1,\"performerId\":1,\"sequence\":1,\"session\":\"validation\",\"left\":{\"state\":\"fist\",\"confidence\":1},\"right\":{\"state\":\"open\",\"confidence\":1}}", 0)) throw new Exception("Reach packet rejected.");
            hand.SetManualGestures(HandGesture.Open, HandGesture.Open); // this is a deterministic safe reset after validating wire format
            Debug.Log("DIGIPHANT_HAND_GRAB_VALIDATION_OK: references, packet protocol, preserved cube=" + cubeSize + ", 40cm pickup volume");
        }
    }
}
