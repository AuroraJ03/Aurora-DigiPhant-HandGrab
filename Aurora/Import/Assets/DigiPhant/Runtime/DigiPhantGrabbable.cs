using UnityEngine;

namespace DigiPhant
{
    /// <summary>
    /// Explicit opt-in for a prop that may be picked up by DigiPhantHandGrab.
    /// Keeping this component on the prop (rather than discovering scene objects)
    /// prevents a future interaction pipeline from accidentally becoming grabbable.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DigiPhantGrabbable : MonoBehaviour
    {
        public Rigidbody body;
        [Tooltip("The physical shape used to test the hand-grab margin. It may be a child collider of this Rigidbody.")]
        public Collider pickupCollider;
        [Tooltip("Maximum distance from the physical surface at which the trunk may acquire this prop.")]
        [Min(0f)] public float pickupSurfaceMargin = .20f;
        [Tooltip("When held, align this local axis with the elephant's horizontal right axis. This is useful for logs.")]
        public bool alignLongAxisHorizontal;
        public Vector3 localLongAxis = Vector3.up;

        public Rigidbody Body => body ? body : GetComponent<Rigidbody>();

        public bool IsEligibleAt(Vector3 point, out float surfaceDistance)
        {
            surfaceDistance = float.PositiveInfinity;
            if (!isActiveAndEnabled || !pickupCollider || !pickupCollider.enabled) return false;
            // ClosestPoint measures against the full finite cylinder, rather than
            // its centre. A long log can consequently be reached at either end,
            // but only within its deliberately configured surface margin.
            Vector3 closest = pickupCollider.ClosestPoint(point);
            surfaceDistance = Vector3.Distance(point, closest);
            return surfaceDistance <= pickupSurfaceMargin;
        }
    }
}
