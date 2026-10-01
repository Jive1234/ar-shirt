using System.Collections.Generic;
using ARTryOn.Calibration;
using ARTryOn.Core;
using ARTryOn.Garment;
using ARTryOn.Rendering;
using ARTryOn.Tracking;
using UnityEngine;

namespace ARTryOn
{
    /// <summary>
    /// Glue for the whole pipeline, run once per inference result:
    /// pose source → multi-person tracker (+ One Euro smoothing + calibration) → one shirt per person → compositor.
    /// A shirt appears only after that person's calibration completes, so nobody sees a badly sized shirt.
    /// </summary>
    public sealed class TryOnManager : MonoBehaviour
    {
        [SerializeField] MonoBehaviour poseSourceBehaviour; // must implement IPoseSource
        [SerializeField] Camera garmentCamera;
        [SerializeField] TryOnCompositor compositor;

        [Header("Garment")]
        [Tooltip("Lit material used for the built-in parametric shirt (Standard in Built-in RP, Lit in URP).")]
        [SerializeField] Material shirtMaterial;
        [Tooltip("Optional rigged garment. Leave empty to use the built-in parametric shirt.")]
        [SerializeField] GarmentFitter garmentPrefab;
        [SerializeField] Color shirtColor = new Color(0.12f, 0.44f, 0.92f);
        [SerializeField] ParametricShirt.Sleeve sleeve = ParametricShirt.Sleeve.Short;
        [Tooltip("x = width, y = length. Above 1 makes the shirt roomier so it covers the real one.")]
        [SerializeField] Vector2 ease = new Vector2(1.08f, 1f);
        [SerializeField] Material occluderMaterial; // ARTryOn/DepthOnlyOccluder
        [SerializeField, Range(1, 3)] int maxGarments = 3;

        [Header("Tracking")]
        [SerializeField] CalibrationSettings calibration = new CalibrationSettings();
        [Tooltip("Fit immediately on the first frames instead of waiting for the standing pose (for testing).")]
        [SerializeField] bool skipCalibration = false;

        public PersonTracker Tracker { get; } = new PersonTracker();
        public IPoseSource Source => _source;
        public int WearerCount => _wearers.Count;

        sealed class Wearer
        {
            public BodyMeasurements Measurements;
            public ParametricShirt Shirt;
            public GarmentFitter Rigged;
            public BodyOccluder Occluder;
            public readonly Vector3[] Joints = new Vector3[Lm.Count];
        }

        readonly Dictionary<int, Wearer> _wearers = new Dictionary<int, Wearer>();
        readonly List<Vector4> _zones = new List<Vector4>();
        IPoseSource _source;
        double _lastT = -1;

        void Awake()
        {
            _source = (IPoseSource)poseSourceBehaviour;
            Tracker.Calibration = calibration;
            Tracker.TrackCreated += t => t.Calibration.Calibrated += m => AddWearer(t, m);
            Tracker.TrackLost += t => RemoveWearer(t.Id);
            _source.FrameReady += OnFrame;
        }

        void OnDestroy()
        {
            if (_source != null) _source.FrameReady -= OnFrame;
        }

        void AddWearer(Track track, BodyMeasurements m)
        {
            RemoveWearer(track.Id);
            if (_wearers.Count >= maxGarments) return;
            Debug.Log($"[TryOn] person {track.Id} calibrated: {m}");

            var w = new Wearer { Measurements = m };
            if (garmentPrefab != null)
            {
                w.Rigged = Instantiate(garmentPrefab);
                w.Rigged.name = $"Garment_{track.Id}";
                w.Rigged.ApplyMeasurements(m);
            }
            else
            {
                w.Shirt = ParametricShirt.Create($"Shirt_{track.Id}", shirtMaterial, shirtColor, sleeve);
            }
            if (occluderMaterial != null)
            {
                var go = new GameObject($"Occluder_{track.Id}");
                go.SetActive(false); // set the material before Awake builds the parts
                w.Occluder = go.AddComponent<BodyOccluder>();
                w.Occluder.depthOnlyMaterial = occluderMaterial;
                go.SetActive(true);
            }
            _wearers[track.Id] = w;
        }

        void RemoveWearer(int id)
        {
            if (!_wearers.TryGetValue(id, out var w)) return;
            if (w.Shirt != null) Destroy(w.Shirt.gameObject);
            if (w.Rigged != null) Destroy(w.Rigged.gameObject);
            if (w.Occluder != null) Destroy(w.Occluder.gameObject);
            _wearers.Remove(id);
        }

        void OnFrame(PoseFrame frame)
        {
            float aspect = frame.ImageHeight > 0 ? (float)frame.ImageWidth / frame.ImageHeight : garmentCamera.aspect;
            garmentCamera.aspect = aspect;
            garmentCamera.fieldOfView = Camera.HorizontalToVerticalFieldOfView(_source.HorizontalFovDeg, aspect);

            float dt = _lastT < 0 ? 1f / 30f : (float)(frame.Timestamp - _lastT);
            _lastT = frame.Timestamp;

            Tracker.Update(frame);

            _zones.Clear();
            foreach (var track in Tracker.Tracks)
            {
                if (skipCalibration && track.Calibration.State != CalibrationState.Calibrated && track.SeenThisFrame)
                    track.Calibration.Force(MeasureNow(track.Pose));

                if (!_wearers.TryGetValue(track.Id, out var w)) continue;
                if (!track.SeenThisFrame) continue; // briefly lost: hold the last pose instead of vanishing
                if (!BodyFit.Compute(track.Pose, garmentCamera, frame.ImageWidth, frame.ImageHeight, w.Joints, out _)) continue;

                if (w.Shirt != null)
                {
                    w.Shirt.Drive(w.Joints, track.Pose, w.Measurements, ease, dt);
                    if (w.Shirt.ViewportBounds(garmentCamera, out var box))
                    {
                        // Below the hip line, real clothing beside the shirt is trousers: leave it alone.
                        float hipV = Mathf.Min(track.Pose.Image[Lm.LeftHip].y, track.Pose.Image[Lm.RightHip].y);
                        _zones.Add(new Vector4(box.x, Mathf.Max(box.y, hipV), box.z, box.w));
                    }
                }
                else if (w.Rigged != null)
                {
                    w.Rigged.Drive(track.Pose, garmentCamera);
                }
                if (w.Occluder != null) w.Occluder.UpdateFrom(w.Joints, track.Pose);
            }

            if (compositor != null) compositor.Render(frame, _zones);
        }

        static BodyMeasurements MeasureNow(PersonObservation p)
        {
            var w = p.World;
            Vector3 ls = w[Lm.LeftShoulder], rs = w[Lm.RightShoulder], lh = w[Lm.LeftHip], rh = w[Lm.RightHip];
            return new BodyMeasurements
            {
                ShoulderWidth = Vector3.Distance(ls, rs),
                HipWidth = Vector3.Distance(lh, rh),
                TorsoLength = Vector3.Distance((ls + rs) * 0.5f, (lh + rh) * 0.5f),
                UpperArmLength = 0.5f * (Vector3.Distance(ls, w[Lm.LeftElbow]) + Vector3.Distance(rs, w[Lm.RightElbow])),
                ForearmLength = 0.5f * (Vector3.Distance(w[Lm.LeftElbow], w[Lm.LeftWrist]) + Vector3.Distance(w[Lm.RightElbow], w[Lm.RightWrist])),
                SampleCount = 1,
            };
        }

        // ---- UI hooks ----

        public void SetColor(Color c)
        {
            shirtColor = c;
            foreach (var w in _wearers.Values) if (w.Shirt != null) w.Shirt.SetColor(c);
        }

        public void ToggleSleeve()
        {
            sleeve = sleeve == ParametricShirt.Sleeve.Short ? ParametricShirt.Sleeve.Long : ParametricShirt.Sleeve.Short;
            foreach (var w in _wearers.Values) if (w.Shirt != null) w.Shirt.SetSleeve(sleeve);
        }

        public ParametricShirt.Sleeve CurrentSleeve => sleeve;

        /// <summary>Everyone stands still again and gets re-measured.</summary>
        public void RecalibrateAll()
        {
            foreach (var track in Tracker.Tracks)
            {
                track.Calibration.Recalibrate();
                RemoveWearer(track.Id);
            }
        }
    }
}
