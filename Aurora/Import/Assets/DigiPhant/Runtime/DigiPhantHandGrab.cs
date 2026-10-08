using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using UnityEngine;

namespace DigiPhant
{
    public enum HandGesture { Unknown, Open, Fist }

    [Serializable] public class HandSample { public string state; public float confidence; }
    [Serializable] public class HandFrame
    {
        public int version;
        public int performerId;
        public int sequence;
        public string session;
        public HandSample left;
        public HandSample right;
    }

    /// <summary>
    /// A small, post-puppet overlay. It intentionally never writes the travel root
    /// or Animator: locomotion and the six pose channels still own the base pose.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DigiPhantHandGrab : MonoBehaviour
    {
        [Header("References")]
        public DigiPhantController controller;
        public Transform head;
        public Transform[] trunk = Array.Empty<Transform>();
        public Transform trunkTip;
        [Tooltip("A generated point inside the curled distal trunk. Held objects follow this point, rather than sticking to the visible nose tip.")]
        public Transform gripAnchor;
        public Rigidbody testCube;
        [Tooltip("Explicit extra props which may be acquired. No scene-wide object search is performed.")]
        public DigiPhantGrabbable[] grabbables = Array.Empty<DigiPhantGrabbable>();
        [Tooltip("Full width/height/depth of the pickup volume. A 20cm cube defaults to a 40cm volume.")]
        public Vector3 pickupVolume = new Vector3(.4f, .4f, .4f);
        [Tooltip("Stable authored reach bases. At zero hand-overlay weight, the current locomotion/legacy pose remains untouched.")]
        [HideInInspector] public Quaternion headReachReference;
        [HideInInspector] public Quaternion[] trunkReachReference = Array.Empty<Quaternion>();
        [HideInInspector] public Quaternion[] trunkGripReference = Array.Empty<Quaternion>();
        [HideInInspector] public Transform gripAnchorRoot;
        [HideInInspector] public Vector3 gripAnchorLocalPosition;
        [HideInInspector] public Transform reachReferenceRoot;
        [HideInInspector] public Quaternion headReachWorldRelative;

        [Header("Gesture input")]
        [Range(1, 4)] public int performerId = 1;
        [Range(0, 1)] public float minimumConfidence = .65f;
        [Tooltip("Seconds before missing/uncertain input ages to Unknown. It prevents stale acquisition but never releases an existing hold; only a confident open palm does.")]
        [Min(.05f)] public float handTimeout = 2f;
        [Min(.05f)] public float acquireDebounce = .12f;
        public bool useManualGestures;
        public HandGesture manualLeft = HandGesture.Open;
        public HandGesture manualRight = HandGesture.Open;

        [Header("Smooth overlay")]
        [Range(0, 45)] public float headLowerDegrees = 18;
        [Tooltip("Additional downward nod used only while reaching for a nearby low prop. Holding still uses the authored 18 degree pose.")]
        [Range(0, 15)] public float groundReachExtraLowerDegrees = 8;
        public Vector3 headLowerAxis = Vector3.forward;
        [Range(-1, 1)] public float headLowerDirection = 1;
        [Range(0, 30)] public float trunkReachDegrees = 12;
        public Vector3 trunkReachAxis = Vector3.forward;
        [Range(-1, 1)] public float trunkReachDirection = -1;
        [Tooltip("Base curl used while lifting. The stronger wrapped grip is distributed across the last trunk joints.")]
        [Range(0, 40)] public float trunkCarryDegrees = 16;
        [Range(-1, 1)] public float trunkCarryDirection = 1;
        [Header("Wrapped grip")]
        [Tooltip("Extra curl at the terminal trunk joint. Earlier distal joints receive progressively less, making a C-shaped grip.")]
        [Range(0, 120)] public float gripCurlDegrees = 70;
        [Range(-1, 1)] public float gripCurlDirection = 1;
        [Range(1, 7)] public int gripJointCount = 6;
        [Tooltip("Target radius used by the editor upgrade to calculate distal curl for a future thicker stump. Runtime follows the measured physical circle centre.")]
        [Range(.14f, .35f)] public float gripRadius = .20f;
        [Min(.05f)] public float reachSmoothTime = .20f;
        [Min(.05f)] public float carrySmoothTime = .08f;
        [Min(0)] public float carryLift = 0f;

        public string Status { get; private set; } = "Hands waiting";
        public bool IsHolding => held;
        public Rigidbody HeldBody => heldBody;
        public Rigidbody ActiveCandidate => activeCandidate;
        public string CurrentTargetName => heldBody ? heldBody.name : activeCandidate ? activeCandidate.name : "";
        public HandGesture LeftGesture => left;
        public HandGesture RightGesture => right;
        public string LastReleaseReason => lastReleaseReason;
        /// <summary>Measured world-space C geometry for validation and future stump tuning.</summary>
        public bool TryGetGripGeometry(out Vector3 centre, out float radius, out float sweepDegrees)
        {
            centre = Vector3.zero; radius = 0; sweepDegrees = 0;
            if (trunk == null || trunk.Length < 2 || !trunkTip ||
                !TryCircumcenter(trunk[trunk.Length - 2].position, trunk[trunk.Length - 1].position, trunkTip.position, out centre)) return false;
            radius = Vector3.Distance(centre, trunkTip.position);
            if (radius < .001f) return false;
            int first = Mathf.Max(0, trunk.Length - Mathf.Clamp(gripJointCount, 1, 7));
            Vector3 normal = Vector3.zero;
            for (int i = first; i < trunk.Length - 1; i++) normal += Vector3.Cross(trunk[i].position - centre, trunk[i + 1].position - centre);
            normal += Vector3.Cross(trunk[trunk.Length - 1].position - centre, trunkTip.position - centre);
            if (normal.sqrMagnitude < .000001f) return false;
            normal.Normalize();
            Vector3 previous = trunk[first].position - centre;
            if (previous.sqrMagnitude < .000001f) return false;
            for (int i = first + 1; i <= trunk.Length; i++)
            {
                Vector3 current = (i == trunk.Length ? trunkTip : trunk[i]).position - centre;
                if (current.sqrMagnitude < .000001f) continue;
                sweepDegrees += Mathf.Abs(Vector3.SignedAngle(previous, current, normal));
                previous = current;
            }
            return sweepDegrees > .01f;
        }
        // Rigidbody.position is authoritative during a physics interaction.  In
        // edit-mode validation it can intentionally differ from Transform.position
        // until Unity's next physics sync, so never derive pickup distance from a
        // potentially stale Transform here.
        public Vector3 PickupCenter => !testCube ? Vector3.positiveInfinity :
            testCube.position + testCube.rotation * Vector3.Scale(testCube.centerOfMass, testCube.transform.lossyScale);
        public Vector3 PickupDelta => !testCube || !trunkTip ? Vector3.positiveInfinity : PickupCenter - trunkTip.position;
        public float RequiredGripClearance
        {
            get
            {
                var box = testCube ? testCube.GetComponent<BoxCollider>() : null;
                if (!box) return .14f;
                Vector3 size = Vector3.Scale(box.size, testCube.transform.lossyScale);
                return size.magnitude * .5f + .03f; // half diagonal plus visible skin clearance
            }
        }

        UdpClient receiver;
        int receiverPort = -1;
        int lastSequence = -1;
        string session;
        float lastPacket = -1000;
        float lastReliableLeft = -1000, lastReliableRight = -1000;
        float bothFistsSince = -1;
        float reachWeight, reachVelocity, carryPoseWeight, carryPoseVelocity;
        float groundReachDegreesApplied, groundReachVelocity;
        HandGesture left = HandGesture.Unknown, right = HandGesture.Unknown;
        bool held;
        Rigidbody heldBody;
        Rigidbody activeCandidate;
        DigiPhantGrabbable heldGrabbable;
        string lastReleaseReason = "Waiting for hand tracking";
        Vector3 lastGripDirection;
        bool hasGripDirection;
        public float LastGripCircleRadius { get; private set; }
        Vector3 carryVelocity;
        float heldLongAxisSign = 1f;
        // The object must not snap from its acquisition point to the middle of the
        // finished curl.  This is stored relative to the travel root so it also
        // remains coherent if another pipeline moves the elephant while wrapping.
        Transform grabContactRoot;
        Vector3 grabContactLocalPosition;
        readonly Dictionary<Transform, Quaternion> locomotionBase = new Dictionary<Transform, Quaternion>();
        DigiPhantController subscribedController;
        int previousPerformer;
        bool previousManual;
        // This is deliberately an event rather than a derived "not fist" state.
        // Once an object has been acquired, occlusion and low-confidence frames are
        // allowed to age to Unknown without changing the hold.  A fresh, trusted
        // open-palm observation is the sole gesture release request.
        bool pendingExplicitOpen;

        void Reset()
        {
            controller = GetComponent<DigiPhantController>();
        }
        void OnEnable()
        {
            EnsureControllerBinding();
            previousPerformer = performerId; previousManual = useManualGestures;
        }
        void OnDisable()
        {
            RemoveControllerBinding();
            StopReceiver();
            ForceResetHands("Hand grab disabled");
        }
        /// <summary>Idempotent hook used by normal scene activation and batch validation.</summary>
        public void EnsureControllerBinding()
        {
            if (!controller) controller = GetComponent<DigiPhantController>();
            if (subscribedController == controller) return;
            RemoveControllerBinding();
            if (!controller) return;
            subscribedController = controller;
            subscribedController.PostControlsApplied += ApplyOverlay;
            subscribedController.PostLocomotionEvaluated += CaptureLocomotionBase;
            subscribedController.TrackingReset += ResetTrackingInput;
        }
        void RemoveControllerBinding()
        {
            if (!subscribedController) return;
            subscribedController.PostControlsApplied -= ApplyOverlay;
            subscribedController.PostLocomotionEvaluated -= CaptureLocomotionBase;
            subscribedController.TrackingReset -= ResetTrackingInput;
            subscribedController = null;
        }
        void Update()
        {
            EnsureControllerBinding();
            if (!controller || !controller.isActiveAndEnabled) { ForceResetHands("Controller inactive"); return; }
            if (previousPerformer != performerId || previousManual != useManualGestures)
            { previousPerformer = performerId; previousManual = useManualGestures; ResetTrackingInput(); }
            EnsureReceiver();
            PollHands(Time.realtimeSinceStartup);
        }
        void EnsureReceiver()
        {
            if (!controller) return;
            int desired = controller.port + 3;
            if (receiver != null && receiverPort == desired) return;
            StopReceiver();
            try
            {
                receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, desired));
                receiver.Client.Blocking = false;
                receiverPort = desired;
            }
            catch (Exception e) when (e is SocketException || e is ArgumentOutOfRangeException)
            { Status = "Hand receiver: " + e.Message; StopReceiver(); }
        }
        void StopReceiver() { receiver?.Close(); receiver = null; receiverPort = -1; }
        public bool AcceptHandPacket(string json, float now)
        {
            HandFrame frame;
            try { frame = JsonUtility.FromJson<HandFrame>(json); }
            catch (ArgumentException) { return false; }
            if (frame == null || frame.version != 1 || string.IsNullOrEmpty(frame.session) || frame.performerId != performerId ||
                !Valid(frame.left) || !Valid(frame.right)) return false;
            if (session != frame.session) { session = frame.session; lastSequence = -1; }
            if (frame.sequence <= lastSequence) return false;
            var incomingLeft = Parse(frame.left.state, frame.left.confidence);
            var incomingRight = Parse(frame.right.state, frame.right.confidence);
            // Queue the release before updating the sampled state.  Multiple UDP
            // frames can be read in one Update; an Open followed by Fist must still
            // release a held object, rather than having the later fist erase it.
            if (held && (incomingLeft == HandGesture.Open || incomingRight == HandGesture.Open))
                pendingExplicitOpen = true;
            // Unknown is an occlusion/ambiguity, not an implicit open. Keep the last
            // reliable anatomical hand briefly, then age it to Unknown. A hold is
            // intentionally not released by that aging.
            if (incomingLeft != HandGesture.Unknown) { left = incomingLeft; lastReliableLeft = now; }
            if (incomingRight != HandGesture.Unknown) { right = incomingRight; lastReliableRight = now; }
            lastSequence = frame.sequence;
            lastPacket = now;
            return true;
        }
        bool Valid(HandSample hand) => hand != null && hand.confidence >= 0 && hand.confidence <= 1 &&
            !float.IsNaN(hand.confidence) && !float.IsInfinity(hand.confidence) &&
            (hand.state == "open" || hand.state == "fist" || hand.state == "unknown");
        HandGesture Parse(string state, float confidence)
        {
            if (confidence < minimumConfidence) return HandGesture.Unknown;
            return state == "open" ? HandGesture.Open : state == "fist" ? HandGesture.Fist : HandGesture.Unknown;
        }
        void PollHands(float now)
        {
            if (controller && controller.inputMode != InputMode.Camera && !useManualGestures) { ResetTrackingInput(); return; }
            if (useManualGestures)
            {
                left = manualLeft; right = manualRight; lastPacket = now;
                if (held && (left == HandGesture.Open || right == HandGesture.Open)) pendingExplicitOpen = true;
                return;
            }
            if (receiver != null)
            {
                try
                {
                    var sender = new IPEndPoint(IPAddress.Loopback, 0);
                    // Process every queued packet (bounded to keep Update cheap), not
                    // merely the last one. This preserves a short, valid Open event
                    // that is followed by a Fist in the same rendered frame.
                    for (int i = 0; i < 32 && receiver.Available > 0; i++)
                    {
                        byte[] packet = receiver.Receive(ref sender);
                        if (packet != null && packet.Length < 2048) AcceptHandPacket(Encoding.UTF8.GetString(packet), now);
                    }
                }
                catch (SocketException) { }
            }
            if (now - lastPacket > handTimeout)
            {
                left = right = HandGesture.Unknown;
            }
            if (!useManualGestures)
            {
                if (now - lastReliableLeft > handTimeout) left = HandGesture.Unknown;
                if (now - lastReliableRight > handTimeout) right = HandGesture.Unknown;
            }
        }
        void ApplyOverlay(float now, float dt)
        {
            // Consume the latched event before calculating acquisition. In
            // particular, an Open then Fist sequence received during one Update
            // releases without immediately re-grabbing the same prop.
            bool releasedByExplicitOpen = held && pendingExplicitOpen;
            pendingExplicitOpen = false;
            if (releasedByExplicitOpen)
            {
                Release("Open palm");
                bothFistsSince = -1;
            }
            // Left fist owns reach regardless of the right hand. Right-open therefore
            // releases an object but deliberately remains in the reach pose.
            bool reach = left == HandGesture.Fist;
            bool bothFists = left == HandGesture.Fist && right == HandGesture.Fist;
            if (!releasedByExplicitOpen && bothFists)
            {
                if (bothFistsSince < 0) bothFistsSince = now;
            }
            else bothFistsSince = -1;

            float target = reach || held ? 1 : 0;
            reachWeight = Mathf.SmoothDamp(reachWeight, target, ref reachVelocity, reachSmoothTime, Mathf.Infinity, Mathf.Max(.0001f, dt));
            carryPoseWeight = Mathf.SmoothDamp(carryPoseWeight, held ? 1 : 0, ref carryPoseVelocity, reachSmoothTime, Mathf.Infinity, Mathf.Max(.0001f, dt));
            // Do not bake this into the holding pose: it is a reach-only correction
            // for a prop whose surface is actually lower than the current nose.
            // This has its own damper. At grab time the regular reach overlay is
            // already at full weight, so directly removing a ground correction
            // would visibly pop the head up into the established carry pose.
            float requestedGroundLower = reach && !held ? GroundReachExtraDegrees() : 0f;
            groundReachDegreesApplied = Mathf.SmoothDamp(groundReachDegreesApplied, requestedGroundLower, ref groundReachVelocity, reachSmoothTime, Mathf.Infinity, Mathf.Max(.0001f, dt));
            ApplyBoneOverlay(head, HeadReachReference(), headLowerAxis, (headLowerDegrees + groundReachDegreesApplied) * headLowerDirection, reachWeight);
            if (trunk != null)
            {
                for (int i = 0; i < trunk.Length; i++)
                {
                    float reachDegrees = trunkReachDegrees * trunkReachDirection;
                    float liftDegrees = trunkCarryDegrees * trunkCarryDirection * carryPoseWeight;
                    Quaternion reachTarget = ReachReference(trunk[i], i) * Quaternion.AngleAxis(reachDegrees + liftDegrees, trunkReachAxis.normalized);
                    Quaternion boneTarget = trunkGripReference != null && i < trunkGripReference.Length && HasRotation(trunkGripReference[i])
                        ? Quaternion.Slerp(reachTarget, trunkGripReference[i], carryPoseWeight) : reachTarget;
                    ApplyBoneOverlayTarget(trunk[i], boneTarget, reachWeight);
                }
            }
            // The controller resets bones to its current base at the start of every
            // frame.  Test the volume only after this frame's reach overlay has
            // moved the real trunk tip, not against the prior neutral pose.
            activeCandidate = FindCandidate(out _, out _);
            if (!releasedByExplicitOpen && bothFists && !held && now - bothFistsSince >= acquireDebounce && activeCandidate) Grab(activeCandidate, FindGrabbable(activeCandidate));
            UpdateGripAnchor();
            if (held) CarryHeldObject(dt);
            Status = held ? "Holding " + heldBody.name + " — tracking may reconnect; open either hand to release" : reach ? "Reaching — close right hand to grab" : "Hands neutral";
        }
        void CaptureLocomotionBase()
        {
            locomotionBase.Clear();
            if (head) locomotionBase[head] = head.localRotation;
            if (trunk != null) foreach (var bone in trunk) if (bone) locomotionBase[bone] = bone.localRotation;
        }
        Quaternion ReachReference(Transform bone, int trunkIndex)
        {
            if (!bone) return Quaternion.identity;
            Quaternion configured = trunkIndex < 0 ? headReachReference :
                (trunkReachReference != null && trunkIndex < trunkReachReference.Length ? trunkReachReference[trunkIndex] : default);
            if (configured.x * configured.x + configured.y * configured.y + configured.z * configured.z + configured.w * configured.w > .5f)
                return configured;
            return locomotionBase.TryGetValue(bone, out var current) ? current : bone.localRotation;
        }
        Quaternion HeadReachReference()
        {
            if (head && head.parent && reachReferenceRoot && HasRotation(headReachWorldRelative))
            {
                // The idle graph can swing an ancestor of the head.  Preserve the
                // authored world orientation relative to the travel root while
                // leaving that root, locomotion, and every unowned joint untouched.
                Quaternion desiredWorld = reachReferenceRoot.rotation * headReachWorldRelative;
                return Quaternion.Inverse(head.parent.rotation) * desiredWorld;
            }
            return ReachReference(head, -1);
        }
        static bool HasRotation(Quaternion rotation) =>
            rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w > .5f;
        void ApplyBoneOverlay(Transform bone, Quaternion reachBase, Vector3 localAxis, float degrees, float weight)
        {
            if (!bone || weight <= .0001f || localAxis.sqrMagnitude < .0001f) return;
            Quaternion sourceRotation = bone.localRotation; // final legacy/locomotion pose for this frame
            Quaternion target = reachBase * Quaternion.AngleAxis(degrees, localAxis.normalized);
            // Blend out any legacy curl/head input rather than popping it off when reach starts.
            bone.localRotation = Quaternion.Slerp(sourceRotation, target, weight);
        }
        void ApplyBoneOverlayTarget(Transform bone, Quaternion target, float weight)
        {
            if (!bone || weight <= .0001f) return;
            bone.localRotation = Quaternion.Slerp(bone.localRotation, target, weight);
        }
        public bool CanPickUp() => FindCandidate(out _, out _) != null;
        Rigidbody FindCandidate(out DigiPhantGrabbable grabbable, out float score)
        {
            grabbable = null; score = float.PositiveInfinity;
            if (!trunkTip) return null;
            Rigidbody best = null;
            // Preserve the original cube's deliberately fixed 40cm box. It is an
            // existing teaching fixture, not a generic candidate discovered here.
            if (IsCubeInPickupVolume(out Vector3 delta))
            { best = testCube; score = delta.magnitude; }
            if (grabbables != null) foreach (var candidate in grabbables)
            {
                if (!candidate || !candidate.Body || !candidate.Body.gameObject.activeInHierarchy || candidate.Body == testCube) continue;
                if (!candidate.IsEligibleAt(trunkTip.position, out float distance)) continue;
                if (distance < score) { best = candidate.Body; grabbable = candidate; score = distance; }
            }
            return best;
        }
        bool IsCubeInPickupVolume(out Vector3 delta)
        {
            delta = PickupDelta;
            if (!testCube || !trunkTip) return false;
            Vector3 half = pickupVolume * .5f;
            return Mathf.Abs(delta.x) <= half.x && Mathf.Abs(delta.y) <= half.y && Mathf.Abs(delta.z) <= half.z;
        }
        DigiPhantGrabbable FindGrabbable(Rigidbody body)
        {
            if (!body || grabbables == null) return null;
            foreach (var candidate in grabbables) if (candidate && candidate.Body == body) return candidate;
            return null;
        }
        float GroundReachExtraDegrees()
        {
            if (!trunkTip) return 0;
            // The existing cube sits on its purpose-built raised blue pad and its
            // 40cm test volume was authored for the original 18 degree reach.
            // It is not a ground target; never let a nearby Timber correction pull
            // the nose away from an already reachable cube.
            if (IsCubeInPickupVolume(out _)) return 0;
            float mostNeeded = 0;
            // A low target close in the horizontal plane asks for a little more
            // nod. This is continuous, bounded, and evaluated only during reach,
            // so the established 18 degree held pose remains exactly unchanged.
            Action<Collider> check = collider =>
            {
                if (!collider || !collider.enabled) return;
                Vector3 point = collider.ClosestPoint(trunkTip.position);
                Vector3 horizontal = Vector3.ProjectOnPlane(point - trunkTip.position, Vector3.up);
                if (horizontal.sqrMagnitude > .75f * .75f) return;
                mostNeeded = Mathf.Max(mostNeeded, Mathf.Clamp01((trunkTip.position.y - point.y) / .30f));
            };
            if (grabbables != null) foreach (var candidate in grabbables) if (candidate) check(candidate.pickupCollider);
            return groundReachExtraLowerDegrees * mostNeeded;
        }
        void Grab(Rigidbody body, DigiPhantGrabbable grabbable)
        {
            if (!body || !trunkTip) return;
            held = true;
            heldBody = body;
            heldGrabbable = grabbable;
            lastReleaseReason = "";
            hasGripDirection = false;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
            grabContactRoot = reachReferenceRoot ? reachReferenceRoot : transform;
            grabContactLocalPosition = grabContactRoot.InverseTransformPoint(body.position);
            if (grabbable && grabbable.alignLongAxisHorizontal)
            {
                Vector3 right = HorizontalRight();
                Vector3 longAxis = body.rotation * grabbable.localLongAxis.normalized;
                heldLongAxisSign = Vector3.Dot(longAxis, right) >= 0 ? 1f : -1f;
            }
        }
        void CarryHeldObject(float dt)
        {
            if (!heldBody || !trunkTip) { Release("Missing held prop or trunk tip"); return; }
            // Blend from the physical acquisition point into the closed C rather
            // than "vacuuming" the object to the nose centre on the first frame.
            Vector3 contact = grabContactRoot ? grabContactRoot.TransformPoint(grabContactLocalPosition) : heldBody.position;
            Vector3 target = Vector3.Lerp(contact, GripPoint() + Vector3.up * carryLift, carryPoseWeight);
            heldBody.position = Vector3.SmoothDamp(heldBody.position, target, ref carryVelocity, carrySmoothTime, Mathf.Infinity, Mathf.Max(.0001f, dt));
            Quaternion targetRotation = heldBody.rotation;
            if (heldGrabbable && heldGrabbable.alignLongAxisHorizontal && heldGrabbable.localLongAxis.sqrMagnitude > .0001f)
            {
                Vector3 desiredAxis = HorizontalRight() * heldLongAxisSign;
                Vector3 currentAxis = heldBody.rotation * heldGrabbable.localLongAxis.normalized;
                targetRotation = Quaternion.FromToRotation(currentAxis, desiredAxis) * heldBody.rotation;
            }
            else targetRotation = trunkTip.rotation;
            heldBody.rotation = Quaternion.Slerp(heldBody.rotation, targetRotation, 1 - Mathf.Exp(-12 * dt));
        }
        Vector3 HorizontalRight()
        {
            Transform root = reachReferenceRoot ? reachReferenceRoot : transform;
            Vector3 right = Vector3.ProjectOnPlane(root.right, Vector3.up);
            return right.sqrMagnitude < .000001f ? Vector3.right : right.normalized;
        }
        Vector3 GripPoint()
        {
            if (gripAnchorRoot && trunkGripReference != null && trunkGripReference.Length == (trunk == null ? 0 : trunk.Length))
                return gripAnchorRoot.TransformPoint(gripAnchorLocalPosition);
            if (!trunkTip) return transform.position;
            int count = trunk == null ? 0 : trunk.Length;
            // Three distal pivots define the circle made by the wrapped trunk. Its
            // centre is the physical "inside" of the C, unlike the old nose-tip
            // attachment. Use the editable radius along that measured direction so
            // a future stump can widen the curl without changing input behaviour.
            // Use the final two consecutive trunk pivots plus the nub. These are
            // the actual terminal arc; skipping the last pivot would centre an
            // imaginary earlier curve and makes the cube appear attached to a nub.
            if (count >= 2 && TryCircumcenter(trunk[count - 2].position, trunk[count - 1].position, trunkTip.position, out var centre))
            {
                Vector3 radial = centre - trunkTip.position;
                if (radial.sqrMagnitude > .0001f)
                {
                    LastGripCircleRadius = radial.magnitude;
                    Vector3 measuredDirection = radial.normalized;
                    // A terminal chain starts almost straight and its circle is
                    // numerically ambiguous. Retaining the first reliable inward
                    // direction prevents an anchor branch flip midway through curl.
                    lastGripDirection = hasGripDirection ? Vector3.Slerp(lastGripDirection, measuredDirection, .18f) : measuredDirection;
                    hasGripDirection = true;
                    return trunkTip.position + lastGripDirection * LastGripCircleRadius;
                }
            }
            Transform distal = count > 1 ? trunk[count - 2] : null;
            Vector3 inward = distal ? distal.position - trunkTip.position : -trunkTip.forward;
            if (inward.sqrMagnitude < .0001f) inward = -trunkTip.forward;
            LastGripCircleRadius = gripRadius;
            return trunkTip.position + inward.normalized * gripRadius;
        }
        static bool TryCircumcenter(Vector3 a, Vector3 b, Vector3 c, out Vector3 centre)
        {
            Vector3 ab = b - a, ac = c - a;
            Vector3 normal = Vector3.Cross(ab, ac);
            float denom = 2f * normal.sqrMagnitude;
            if (denom < .0000001f) { centre = Vector3.zero; return false; }
            centre = a + (Vector3.Cross(normal, ab) * ac.sqrMagnitude + Vector3.Cross(ac, normal) * ab.sqrMagnitude) / denom;
            return !float.IsNaN(centre.x) && !float.IsInfinity(centre.x);
        }
        public float MinimumGripCentrelineDistance(Vector3 point)
        {
            if (trunk == null || trunk.Length == 0 || !trunkTip) return 0;
            int first = Mathf.Max(0, trunk.Length - Mathf.Clamp(gripJointCount, 1, 7));
            float minimum = float.PositiveInfinity;
            for (int i = first; i < trunk.Length; i++)
            {
                Vector3 a = trunk[i].position;
                Vector3 b = i + 1 < trunk.Length ? trunk[i + 1].position : trunkTip.position;
                Vector3 ab = b - a;
                float fraction = ab.sqrMagnitude < .000001f ? 0 : Mathf.Clamp01(Vector3.Dot(point - a, ab) / ab.sqrMagnitude);
                minimum = Mathf.Min(minimum, Vector3.Distance(point, a + ab * fraction));
            }
            return float.IsPositiveInfinity(minimum) ? 0 : minimum;
        }
        public float MaximumGripPivotDistance(Vector3 point)
        {
            if (trunk == null || trunk.Length == 0 || !trunkTip) return 0;
            int first = Mathf.Max(0, trunk.Length - Mathf.Clamp(gripJointCount, 1, 7));
            float maximum = Vector3.Distance(trunkTip.position, point);
            for (int i = first; i < trunk.Length; i++) if (trunk[i]) maximum = Mathf.Max(maximum, Vector3.Distance(trunk[i].position, point));
            return maximum;
        }
        /// <summary>Largest departure of the wrapped distal pivots from its measured circle.</summary>
        public float MaximumGripRadialError(Vector3 centre, float expectedRadius)
        {
            if (trunk == null || trunk.Length == 0 || !trunkTip) return float.PositiveInfinity;
            int first = Mathf.Max(0, trunk.Length - Mathf.Clamp(gripJointCount, 1, 7));
            float maximum = Mathf.Abs(Vector3.Distance(trunkTip.position, centre) - expectedRadius);
            for (int i = first; i < trunk.Length; i++)
                if (trunk[i]) maximum = Mathf.Max(maximum, Mathf.Abs(Vector3.Distance(trunk[i].position, centre) - expectedRadius));
            return maximum;
        }
        public bool HasGripSelfIntersection()
        {
            if (trunk == null || trunk.Length < 4 || !trunkTip) return false;
            int first = Mathf.Max(0, trunk.Length - Mathf.Clamp(gripJointCount, 1, 7));
            var points = new List<Vector3>();
            for (int i = first; i < trunk.Length; i++) points.Add(trunk[i].position);
            points.Add(trunkTip.position);
            Vector3 normal = Vector3.zero;
            for (int i = 0; i + 2 < points.Count && normal.sqrMagnitude < .000001f; i++) normal = Vector3.Cross(points[i + 1] - points[i], points[i + 2] - points[i]);
            if (normal.sqrMagnitude < .000001f) return false;
            normal.Normalize(); Vector3 axisX = (points[1] - points[0]).normalized;
            if (axisX.sqrMagnitude < .000001f) return false;
            Vector3 axisY = Vector3.Cross(normal, axisX);
            var flat = points.ConvertAll(p => new Vector2(Vector3.Dot(p, axisX), Vector3.Dot(p, axisY)));
            for (int i = 0; i + 1 < flat.Count; i++)
                for (int j = i + 2; j + 1 < flat.Count; j++)
                    if (SegmentsIntersect(flat[i], flat[i + 1], flat[j], flat[j + 1])) return true;
            return false;
        }
        static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float abC = Cross2(b - a, c - a), abD = Cross2(b - a, d - a);
            float cdA = Cross2(d - c, a - c), cdB = Cross2(d - c, b - c);
            return ((abC > .00001f && abD < -.00001f) || (abC < -.00001f && abD > .00001f)) &&
                   ((cdA > .00001f && cdB < -.00001f) || (cdA < -.00001f && cdB > .00001f));
        }
        static float Cross2(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        // Editor-only setup/validation calls this read-only point helper; gameplay
        // always follows the hidden gripAnchor updated by ApplyOverlay.
        public Vector3 GripPointForValidation() => GripPoint();
        void UpdateGripAnchor()
        {
            if (!gripAnchor) return;
            gripAnchor.position = GripPoint();
            if (trunkTip) gripAnchor.rotation = trunkTip.rotation;
        }
        public void Release(string reason = "Released")
        {
            if (!held) return;
            held = false; carryVelocity = Vector3.zero;
            hasGripDirection = false;
            grabContactRoot = null;
            lastReleaseReason = reason;
            var released = heldBody;
            heldBody = null;
            heldGrabbable = null;
            if (released) { released.isKinematic = false; released.WakeUp(); }
        }
        public void SetManualGestures(HandGesture newLeft, HandGesture newRight)
        { useManualGestures = true; manualLeft = newLeft; manualRight = newRight; }
        /// <summary>
        /// Clear recognizer/session state after a camera, performer, or input-mode
        /// reset. This intentionally preserves a physical hold: loss of tracking is
        /// not a release gesture.
        /// </summary>
        public void ResetTrackingInput()
        {
            left = right = HandGesture.Unknown; lastSequence = -1; session = null;
            lastPacket = lastReliableLeft = lastReliableRight = -1000;
            // Let any reach-only ground correction fade with the normal overlay
            // after a tracking reset; zeroing it here would create a head pop.
            groundReachVelocity = 0;
            bothFistsSince = -1;
            // A trusted open event that was already accepted must survive a
            // controller reset long enough to be consumed by ApplyOverlay.
            if (!held) pendingExplicitOpen = false;
        }
        // Existing editor setup and validation helpers use ResetHands as their
        // explicit cleanup operation. Preserve that public, destructive contract;
        // controller TrackingReset is wired to ResetTrackingInput instead.
        public void ResetHands() => ForceResetHands("Hand grab reset");
        /// <summary>
        /// Lifecycle cleanup for disabling the component or ending Play mode. Unlike
        /// <see cref="ResetTrackingInput"/>, this deliberately drops a held prop.
        /// </summary>
        public void ForceResetHands(string reason = "Hand grab reset")
        {
            ResetTrackingInput();
            pendingExplicitOpen = false;
            Release(reason);
        }
        void OnDrawGizmosSelected()
        {
            if (!trunkTip) return;
            Gizmos.color = new Color(1f, .55f, .05f, .28f);
            Gizmos.matrix = Matrix4x4.TRS(trunkTip.position, Quaternion.identity, Vector3.one);
            Gizmos.DrawCube(Vector3.zero, pickupVolume);
            Gizmos.color = new Color(1f, .55f, .05f, .95f);
            Gizmos.DrawWireCube(Vector3.zero, pickupVolume);
        }
        public void DrawControls(float width)
        {
            GUILayout.Space(6);
            float now = Time.realtimeSinceStartup;
            string ages = useManualGestures ? "manual" : "L " + Mathf.Max(0, now - lastReliableLeft).ToString("0.0") + "s · R " + Mathf.Max(0, now - lastReliableRight).ToString("0.0") + "s";
            GUILayout.Label("Hand grab | " + Status, GUILayout.Width(width));
            GUILayout.Label("Tracking age: " + ages + (string.IsNullOrEmpty(lastReleaseReason) ? "" : " | Last release: " + lastReleaseReason), GUILayout.Width(width));
            bool manual = GUILayout.Toggle(useManualGestures, "Test hand gestures (camera-free)");
            if (manual != useManualGestures) useManualGestures = manual;
            if (!useManualGestures) return;
            manualLeft = (HandGesture)GUILayout.Toolbar((int)manualLeft, new[] { "Unknown", "Open", "Fist" });
            GUILayout.Label("Left hand", GUILayout.Width(width));
            manualRight = (HandGesture)GUILayout.Toolbar((int)manualRight, new[] { "Unknown", "Open", "Fist" });
            GUILayout.Label("Right hand", GUILayout.Width(width));
            GUILayout.Label("Left fist = reach; both fists = grab nearby cube or Timber; open releases.", GUILayout.Width(width));
        }
    }
}
