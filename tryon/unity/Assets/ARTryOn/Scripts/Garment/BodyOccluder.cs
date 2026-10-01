using ARTryOn.Core;
using UnityEngine;

namespace ARTryOn.Garment
{
    /// <summary>
    /// Invisible proxy geometry for the parts of the real body that must appear IN FRONT of the garment:
    /// forearms and hands crossing the torso, and the head/neck over the collar. Rendered with
    /// ARTryOn/DepthOnlyOccluder before the garment, so it writes depth but no colour; garment pixels
    /// behind it fail the depth test and the camera image shows through.
    ///
    /// Keep the capsule radius smaller than the sleeve radius: then the sleeve wrapped around the forearm
    /// still draws (it is in front of the capsule), while the torso behind the forearm is hidden.
    /// </summary>
    public sealed class BodyOccluder : MonoBehaviour
    {
        public Material depthOnlyMaterial;
        public float forearmRadius = 0.035f;
        public float handRadius = 0.05f;
        public float headRadius = 0.10f;
        [Tooltip("Hand tip is extrapolated this far past the wrist along the forearm (m).")]
        public float handLength = 0.09f;

        Transform _lFore, _rFore, _lHand, _rHand, _head;

        void Awake()
        {
            _lFore = Make("L_Forearm", PrimitiveType.Capsule);
            _rFore = Make("R_Forearm", PrimitiveType.Capsule);
            _lHand = Make("L_Hand", PrimitiveType.Sphere);
            _rHand = Make("R_Hand", PrimitiveType.Sphere);
            _head = Make("Head", PrimitiveType.Sphere);
        }

        Transform Make(string name, PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.GetComponent<MeshRenderer>().sharedMaterial = depthOnlyMaterial;
            go.layer = gameObject.layer;
            go.transform.SetParent(transform, false);
            return go.transform;
        }

        /// <param name="jointsCam">Camera-space joints, e.g. <see cref="GarmentFitter.JointsCam"/>.</param>
        public void UpdateFrom(Vector3[] jointsCam, PersonObservation pose)
        {
            const float vis = 0.5f;
            Segment(_lFore, jointsCam[Lm.LeftElbow], jointsCam[Lm.LeftWrist], forearmRadius,
                    pose.IsVisible(Lm.LeftElbow, vis) && pose.IsVisible(Lm.LeftWrist, vis));
            Segment(_rFore, jointsCam[Lm.RightElbow], jointsCam[Lm.RightWrist], forearmRadius,
                    pose.IsVisible(Lm.RightElbow, vis) && pose.IsVisible(Lm.RightWrist, vis));
            Ball(_lHand, Extrapolate(jointsCam[Lm.LeftElbow], jointsCam[Lm.LeftWrist]), handRadius,
                 pose.IsVisible(Lm.LeftWrist, vis));
            Ball(_rHand, Extrapolate(jointsCam[Lm.RightElbow], jointsCam[Lm.RightWrist]), handRadius,
                 pose.IsVisible(Lm.RightWrist, vis));
            Ball(_head, jointsCam[Lm.Nose], headRadius, pose.IsVisible(Lm.Nose, vis));
        }

        Vector3 Extrapolate(Vector3 elbow, Vector3 wrist)
        {
            Vector3 d = wrist - elbow;
            return d.sqrMagnitude > 1e-8f ? wrist + d.normalized * (handLength * 0.5f) : wrist;
        }

        static void Segment(Transform t, Vector3 a, Vector3 b, float r, bool on)
        {
            t.gameObject.SetActive(on);
            if (!on) return;
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-4f) { t.gameObject.SetActive(false); return; }
            t.position = (a + b) * 0.5f;
            t.rotation = Quaternion.FromToRotation(Vector3.up, d / len);
            // Unity capsule: height 2, radius 0.5 at scale 1.
            t.localScale = new Vector3(r * 2f, len * 0.5f + r, r * 2f);
        }

        static void Ball(Transform t, Vector3 p, float r, bool on)
        {
            t.gameObject.SetActive(on);
            if (!on) return;
            t.position = p;
            t.localScale = Vector3.one * (r * 2f);
        }
    }
}
