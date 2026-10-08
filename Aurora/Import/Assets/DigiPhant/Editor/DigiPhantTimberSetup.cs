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
    /// <summary>One-time, idempotent setup for the supplied course Timber prop.</summary>
    public static class DigiPhantTimberSetup
    {
        const string TimberName = "Timber";
        const string CutEndName = "Cut end";

        [MenuItem("DigiPhant/Hand Grab/Make Timber Grabbable in Open Scene")]
        public static void MakeTimberGrabbableInOpenScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("Save the elephant scene before preparing Timber.");
            var hand = UnityEngine.Object.FindAnyObjectByType<DigiPhantHandGrab>(FindObjectsInactive.Include);
            if (!hand) throw new InvalidOperationException("Open the existing DigiPhant Hand Grab scene first.");

            Transform timber = FindExact(scene, TimberName);
            // On the first run they are siblings; after a successful run they are
            // direct Timber children. Looking in both locations makes rerunning
            // this setup safe rather than failing after it has made its own
            // intentional hierarchy change.
            var cutEnds = Resources.FindObjectsOfTypeAll<Transform>()
                .Where(t => t && t.gameObject.scene == scene && t.name == CutEndName &&
                    (t.parent == timber.parent || t.parent == timber)).Distinct().ToArray();
            if (cutEnds.Length != 2)
                throw new InvalidOperationException("Expected exactly two sibling Cut end discs beside Timber; found " + cutEnds.Length + ".");

            string backup = AssetDatabase.GenerateUniqueAssetPath(Path.ChangeExtension(scene.path, null) + "_BeforeTimberGrab.unity");
            if (!AssetDatabase.CopyAsset(scene.path, backup)) throw new IOException("Could not create Timber scene backup.");

            // These are part of this one log only. Preserve their world position,
            // rotation, materials, and meshes while making them descendants so the
            // two cut faces follow rigidbody motion with the wood.
            foreach (var end in cutEnds) if (end.parent != timber) end.SetParent(timber, true);

            // The course cylinder mesh has a unit diameter. Keep its 3m local-Y length and
            // existing transform/orientation; only reduce its radial X/Z diameter.
            Vector3 scale = timber.localScale;
            timber.localScale = new Vector3(.5f, scale.y, .5f);

            var filter = timber.GetComponent<MeshFilter>();
            if (!filter || !filter.sharedMesh) throw new InvalidOperationException("Timber needs its existing cylinder mesh.");
            // Unity destroyed-component wrappers are not CLR null, so do not use
            // `??` here: a stale serialized MeshCollider must be replaced.
            var collider = timber.GetComponent<MeshCollider>();
            if (!collider) collider = timber.gameObject.AddComponent<MeshCollider>();
            collider.sharedMesh = filter.sharedMesh;
            collider.convex = true;
            var body = timber.GetComponent<Rigidbody>();
            if (!body) body = timber.gameObject.AddComponent<Rigidbody>();
            body.mass = 20f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var grabbable = timber.GetComponent<DigiPhantGrabbable>();
            if (!grabbable) grabbable = timber.gameObject.AddComponent<DigiPhantGrabbable>();
            grabbable.body = body;
            grabbable.pickupCollider = collider;
            grabbable.pickupSurfaceMargin = .20f;
            grabbable.alignLongAxisHorizontal = true;
            grabbable.localLongAxis = Vector3.up;

            var existing = hand.grabbables ?? Array.Empty<DigiPhantGrabbable>();
            if (!existing.Contains(grabbable)) hand.grabbables = existing.Concat(new[] { grabbable }).ToArray();
            EditorUtility.SetDirty(timber.gameObject);
            EditorUtility.SetDirty(hand);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = timber.gameObject;
            Debug.Log("DIGIPHANT_TIMBER_GRAB_READY: " + scene.path + " backup=" + backup + " diameter=0.50m length=" + scale.y.ToString("0.00") + "m cutEnds=" + cutEnds.Length);
        }

        static Transform FindExact(Scene scene, string name)
        {
            var matches = Resources.FindObjectsOfTypeAll<Transform>()
                .Where(t => t && t.gameObject.scene == scene && t.name == name).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Expected exactly one '" + name + "' in the saved scene; found " + matches.Length + ".");
            return matches[0];
        }

        // Unity batch entry point:
        // -executeMethod DigiPhant.Editor.DigiPhantTimberSetup.MakeTimberGrabbableFromCommandLine
        // -handGrabScene Assets/DigiPhant/Scenes/DigiPhant.unity
        public static void MakeTimberGrabbableFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-handGrabScene");
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Pass -handGrabScene Assets/...unity");
            EditorSceneManager.OpenScene(args[index + 1], OpenSceneMode.Single);
            MakeTimberGrabbableInOpenScene();
        }
    }
}
