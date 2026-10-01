using ARTryOn.Calibration;
using ARTryOn.Core;
using UnityEngine;

namespace ARTryOn.Garment
{
    /// <summary>
    /// Fits a rigged garment to one tracked person.
    ///
    /// Two separate jobs:
    ///  1. <see cref="ApplyMeasurements"/> (once, after calibration): stretch bone lengths and drive
    ///     girth blend shapes so the garment's rest shape matches the person's proportions. Done by moving
    ///     bone local positions, never by non-uniform localScale, which would shear child bones and the
    ///     cloth simulation.
    ///  2. <see cref="Drive"/> (every frame): place the garment in camera space and rotate bones to follow the pose.
    ///
    /// Asset requirements: modelled in meters, in a relaxed A-pose, with the bones assigned below. Put this
    /// component on the garment's root object, rotated so the garment faces the root's +Z. The hips bone
    /// sits at the midpoint of the two hip joints.
    /// </summary>
    public sealed class GarmentFitter : MonoBehaviour
    {
        [Header("Rig")]
        public Transform hips;
        public Transform spine;
        public Transform chest;
        public Transform leftShoulder, rightShoulder;   // clavicles
        public Transform leftUpperArm, rightUpperArm;
        public Transform leftLowerArm, rightLowerArm;
        public Transform leftHand, rightHand;           // end bones (only their positions are used)

        [Header("Fit")]
        [Tooltip("Extra room so the virtual garment covers the real one underneath. x = width, y = length.")]
        public Vector2 ease = new Vector2(1.08f, 1.03f);

        [Header("Girth morphs (optional)")]
        public SkinnedMeshRenderer skinnedMesh;
        [Tooltip("Blend shape that widens chest/belly; its weight follows hip-to-shoulder proportion.")]
        public string wideBlendShape = "Wide";
        [Tooltip("Hip/shoulder ratio at which the wide blend shape reaches 100.")]
        public float wideAtRatio = 1.0f;

        [Tooltip("Called after the rest shape changes, so cloth simulation can rebuild its constraints.")]
        public UnityEngine.Events.UnityEvent restShapeChanged;

        // Rest (bind) data, captured once in Awake relative to this root's body frame
        // (identity = upright, facing +Z), so it does not depend on how the FBX importer rotated the bones.
        struct BoneRest
        {
            public Vector3 localPos;      // localPosition relative to parent
            public Quaternion rotInBody;  // world rotation relative to the body frame
            public Vector3 dirInBody;     // bone → child direction in the body frame
        }

        BoneRest _hips, _spine, _chest, _lSh, _rSh, _lUp, _rUp, _lLo, _rLo, _lHand, _rHand;
        BodyMeasurements _garment;
        Vector3 _lateralInChest; // chest-local axis from right to left shoulder, for width scaling
        int _wideIndex = -1;
        bool _fitted;

        public bool IsFitted => _fitted;

        /// <summary>Camera-space joint positions from the last <see cref="Drive"/> call (indexed by <see cref="Lm"/>).</summary>
        public readonly Vector3[] JointsCam = new Vector3[Lm.Count];
        public float LastHipDepth { get; private set; }

        void Awake()
        {
            _hips = Capture(hips, spine);
            _spine = Capture(spine, chest);
            _chest = Capture(chest, null);
            _lSh = Capture(leftShoulder, leftUpperArm);
            _rSh = Capture(rightShoulder, rightUpperArm);
            _lUp = Capture(leftUpperArm, leftLowerArm);
            _rUp = Capture(rightUpperArm, rightLowerArm);
            _lLo = Capture(leftLowerArm, leftHand);
            _rLo = Capture(rightLowerArm, rightHand);
            _lHand = Capture(leftHand, null);
            _rHand = Capture(rightHand, null);

            Vector3 lUp = leftUpperArm.position, rUp = rightUpperArm.position;
            Vector3 midShoulders = (lUp + rUp) * 0.5f;
            _garment = new BodyMeasurements
            {
                ShoulderWidth = Vector3.Distance(lUp, rUp),
                TorsoLength = Vector3.Distance(midShoulders, hips.position),
                UpperArmLength = 0.5f * (Vector3.Distance(lUp, leftLowerArm.position) +
                                         Vector3.Distance(rUp, rightLowerArm.position)),
                ForearmLength = 0.5f * (Vector3.Distance(leftLowerArm.position, leftHand.position) +
                                        Vector3.Distance(rightLowerArm.position, rightHand.position)),
            };
            _lateralInChest = chest.InverseTransformDirection(lUp - rUp).normalized;

            if (skinnedMesh != null && !string.IsNullOrEmpty(wideBlendShape))
                _wideIndex = skinnedMesh.sharedMesh.GetBlendShapeIndex(wideBlendShape);
        }

        BoneRest Capture(Transform bone, Transform child)
        {
            Quaternion bodyInv = Quaternion.Inverse(transform.rotation);
            return new BoneRest
            {
                localPos = bone.localPosition,
                rotInBody = bodyInv * bone.rotation,
                dirInBody = child != null ? bodyInv * (child.position - bone.position).normalized : Vector3.zero,
            };
        }

        /// <summary>Stretch the garment's rest shape to the calibrated body. Call once per calibration.</summary>
        public void ApplyMeasurements(BodyMeasurements body)
        {
            float width = Ratio(body.ShoulderWidth, _garment.ShoulderWidth) * ease.x;
            float torso = Ratio(body.TorsoLength, _garment.TorsoLength) * ease.y;
            float upper = Ratio(body.UpperArmLength, _garment.UpperArmLength) * ease.y;
            float fore = Ratio(body.ForearmLength, _garment.ForearmLength) * ease.y;

            // Torso length: the hips → spine → chest chain.
            spine.localPosition = _spine.localPos * torso;
            chest.localPosition = _chest.localPos * torso;

            // Shoulder width: scale only the lateral component of the clavicle offsets.
            leftShoulder.localPosition = ScaleAlong(_lSh.localPos, _lateralInChest, width);
            rightShoulder.localPosition = ScaleAlong(_rSh.localPos, _lateralInChest, width);
            leftUpperArm.localPosition = _lUp.localPos * width;
            rightUpperArm.localPosition = _rUp.localPos * width;

            // Sleeve lengths.
            leftLowerArm.localPosition = _lLo.localPos * upper;
            rightLowerArm.localPosition = _rLo.localPos * upper;
            leftHand.localPosition = _lHand.localPos * fore;
            rightHand.localPosition = _rHand.localPos * fore;

            // Girth: a heavier build has hips close to (or wider than) the shoulders.
            if (_wideIndex >= 0 && body.ShoulderWidth > 0f)
            {
                float hipToShoulder = body.HipWidth / body.ShoulderWidth;
                // Typical adult range is ~0.7 (athletic) to ~1.0 (wide); map to 0..100.
                float w = Mathf.InverseLerp(0.7f, wideAtRatio, hipToShoulder) * 100f;
                skinnedMesh.SetBlendShapeWeight(_wideIndex, w);
            }

            _fitted = true;
            restShapeChanged?.Invoke();
        }

        static float Ratio(float body, float garment) => garment > 1e-4f && body > 1e-4f ? body / garment : 1f;

        static Vector3 ScaleAlong(Vector3 v, Vector3 axis, float s)
        {
            Vector3 along = Vector3.Project(v, axis);
            return v - along + along * s;
        }

        /// <summary>
        /// Place and pose the garment for this frame.
        /// </summary>
        /// <param name="pose">Smoothed pose (Unity conventions, see <see cref="PersonObservation"/>).</param>
        /// <param name="cam">Camera whose FOV matches the webcam, at the origin looking +Z.</param>
        public void Drive(PersonObservation pose, Camera cam)
        {
            var w = pose.World;
            var img = pose.Image;

            // 1. Depth of the hips from apparent size. Use the image-plane (xy) extent of shoulders + torso in
            //    both spaces, so foreshortening cancels when the person turns or leans.
            Vector3 ls = w[Lm.LeftShoulder], rs = w[Lm.RightShoulder], lh = w[Lm.LeftHip], rh = w[Lm.RightHip];
            Vector3 midS = (ls + rs) * 0.5f, midH = (lh + rh) * 0.5f;
            float worldSpan = XY(ls - rs).magnitude + XY(midS - midH).magnitude;

            Vector2 iMidS = (img[Lm.LeftShoulder] + img[Lm.RightShoulder]) * 0.5f;
            Vector2 iMidH = (img[Lm.LeftHip] + img[Lm.RightHip]) * 0.5f;
            float pxSpan = Px(img[Lm.LeftShoulder] - img[Lm.RightShoulder], cam).magnitude + Px(iMidS - iMidH, cam).magnitude;
            if (pxSpan < 1f || worldSpan < 1e-3f) return;

            float focalPx = 0.5f * cam.pixelHeight / Mathf.Tan(0.5f * cam.fieldOfView * Mathf.Deg2Rad);
            float hipDepth = focalPx * worldSpan / pxSpan;

            // 2. Joint positions in camera space: x/y from the image (so the garment sticks to the person's
            //    pixels), depth from the hips plus the world landmarks' relative depth.
            LastHipDepth = hipDepth;
            for (int i = 0; i < Lm.Count; i++)
                JointsCam[i] = cam.ViewportToWorldPoint(new Vector3(img[i].x, img[i].y, hipDepth + (w[i].z - midH.z)));
            Vector3 J(int i) => JointsCam[i];
            Vector3 cLS = J(Lm.LeftShoulder), cRS = J(Lm.RightShoulder), cLH = J(Lm.LeftHip), cRH = J(Lm.RightHip);
            Vector3 cLE = J(Lm.LeftElbow), cRE = J(Lm.RightElbow), cLW = J(Lm.LeftWrist), cRW = J(Lm.RightWrist);

            // 3. Torso orientation. "across" points from the right to the left side of the body; for a person
            //    facing the camera that is +X, and forward = up × across points back at the camera.
            Quaternion hipRot = Basis(cLH - cRH, (cLS + cRS) * 0.5f - (cLH + cRH) * 0.5f);
            Quaternion chestRot = Basis(cLS - cRS, (cLS + cRS) * 0.5f - (cLH + cRH) * 0.5f);

            hips.SetPositionAndRotation((cLH + cRH) * 0.5f, hipRot * _hips.rotInBody);
            // Chest takes the shoulder orientation; spine gets half of the hip → shoulder twist.
            spine.rotation = Quaternion.Slerp(hipRot, chestRot, 0.5f) * _spine.rotInBody;
            chest.rotation = chestRot * _chest.rotInBody;
            leftShoulder.rotation = chestRot * _lSh.rotInBody;
            rightShoulder.rotation = chestRot * _rSh.rotInBody;

            // 4. Arms: swing each bone from its rest direction to the observed one. Elbow/wrist landmarks that
            //    MediaPipe is unsure about are skipped so the sleeve keeps its last (smoothed) direction.
            const float vis = 0.5f;
            if (pose.IsVisible(Lm.LeftElbow, vis)) Aim(leftUpperArm, _lUp, chestRot, cLE - cLS);
            if (pose.IsVisible(Lm.RightElbow, vis)) Aim(rightUpperArm, _rUp, chestRot, cRE - cRS);
            if (pose.IsVisible(Lm.LeftWrist, vis)) Aim(leftLowerArm, _lLo, chestRot, cLW - cLE);
            if (pose.IsVisible(Lm.RightWrist, vis)) Aim(rightLowerArm, _rLo, chestRot, cRW - cRE);
        }

        static void Aim(Transform bone, BoneRest rest, Quaternion frame, Vector3 target)
        {
            if (target.sqrMagnitude < 1e-8f) return;
            Vector3 restDir = frame * rest.dirInBody;
            bone.rotation = Quaternion.FromToRotation(restDir, target) * (frame * rest.rotInBody);
        }

        static Quaternion Basis(Vector3 across, Vector3 up)
        {
            Vector3 forward = Vector3.Cross(up, across);
            if (forward.sqrMagnitude < 1e-8f) return Quaternion.identity;
            // A person facing the camera gives forward = -Z, i.e. the body frame yawed 180°, so the garment
            // (modelled facing the body frame's +Z) ends up facing the camera with its left side on +X.
            return Quaternion.LookRotation(forward, up);
        }

        static Vector2 XY(Vector3 v) => new Vector2(v.x, v.y);

        static Vector2 Px(Vector2 viewportDelta, Camera cam) =>
            new Vector2(viewportDelta.x * cam.pixelWidth, viewportDelta.y * cam.pixelHeight);
    }
}
