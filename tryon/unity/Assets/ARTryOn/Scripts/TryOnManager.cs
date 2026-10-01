using System.Collections.Generic;
using ARTryOn.Calibration;
using ARTryOn.Core;
using ARTryOn.Garment;
using ARTryOn.Tracking;
using UnityEngine;

namespace ARTryOn
{
    /// <summary>
    /// Glue for the whole pipeline:
    /// pose source → multi-person tracker (+ One Euro smoothing + calibration) → one garment per person.
    /// A garment appears only after that person's calibration completes, so nobody sees a badly sized shirt.
    /// </summary>
    public sealed class TryOnManager : MonoBehaviour
    {
        [SerializeField] MonoBehaviour poseSourceBehaviour; // must implement IPoseSource
        [SerializeField] Camera garmentCamera;
        [SerializeField] GarmentFitter garmentPrefab;
        [SerializeField] BodyOccluder occluderPrefab;
        [SerializeField, Range(1, 4)] int maxGarments = 3;

        [Header("Tracking")]
        [SerializeField] CalibrationSettings calibration = new CalibrationSettings();

        public PersonTracker Tracker { get; } = new PersonTracker();

        sealed class Wearer
        {
            public GarmentFitter Garment;
            public BodyOccluder Occluder;
        }

        readonly Dictionary<int, Wearer> _wearers = new Dictionary<int, Wearer>();
        IPoseSource _source;

        void Awake()
        {
            _source = (IPoseSource)poseSourceBehaviour;
            Tracker.Calibration = calibration;
            Tracker.TrackCreated += OnTrackCreated;
            Tracker.TrackLost += OnTrackLost;
            _source.FrameReady += OnFrame;
        }

        void OnDestroy()
        {
            if (_source != null) _source.FrameReady -= OnFrame;
        }

        void OnTrackCreated(Track track)
        {
            track.Calibration.Calibrated += m => OnCalibrated(track, m);
        }

        void OnCalibrated(Track track, BodyMeasurements m)
        {
            if (_wearers.Count >= maxGarments) return;
            Debug.Log($"[TryOn] person {track.Id} calibrated: {m}");

            var w = new Wearer
            {
                Garment = Instantiate(garmentPrefab),
                Occluder = occluderPrefab != null ? Instantiate(occluderPrefab) : null,
            };
            w.Garment.name = $"Garment_{track.Id}";
            w.Garment.ApplyMeasurements(m);
            _wearers[track.Id] = w;
        }

        void OnTrackLost(Track track)
        {
            if (!_wearers.TryGetValue(track.Id, out var w)) return;
            Destroy(w.Garment.gameObject);
            if (w.Occluder != null) Destroy(w.Occluder.gameObject);
            _wearers.Remove(track.Id);
        }

        void OnFrame(PoseFrame frame)
        {
            float aspect = frame.ImageWidth > 0 ? (float)frame.ImageWidth / frame.ImageHeight : garmentCamera.aspect;
            garmentCamera.aspect = aspect;
            garmentCamera.fieldOfView = Camera.HorizontalToVerticalFieldOfView(_source.HorizontalFovDeg, aspect);

            Tracker.Update(frame);

            foreach (var track in Tracker.Tracks)
            {
                if (!_wearers.TryGetValue(track.Id, out var w)) continue;
                // While a person is briefly lost the garment holds its last pose instead of vanishing.
                if (!track.SeenThisFrame) continue;
                w.Garment.Drive(track.Pose, garmentCamera);
                if (w.Occluder != null) w.Occluder.UpdateFrom(w.Garment.JointsCam, track.Pose);
            }
        }

        /// <summary>Hook to a UI button: everyone stands still again and gets re-measured.</summary>
        public void RecalibrateAll()
        {
            foreach (var track in Tracker.Tracks)
            {
                track.Calibration.Recalibrate();
                OnTrackLost(track);
            }
        }
    }
}
