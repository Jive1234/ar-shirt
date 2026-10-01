using System.Collections.Generic;
using ARTryOn.Core;
using UnityEngine;

namespace ARTryOn.Calibration
{
    /// <summary>Body proportions measured during calibration. Metric (MediaPipe world landmarks).</summary>
    [System.Serializable]
    public struct BodyMeasurements
    {
        public float ShoulderWidth;  // L shoulder ↔ R shoulder joint centres
        public float HipWidth;       // L hip ↔ R hip joint centres
        public float TorsoLength;    // mid-shoulder ↔ mid-hip
        public float UpperArmLength; // shoulder ↔ elbow, mean of both sides
        public float ForearmLength;  // elbow ↔ wrist, mean of both sides
        public int SampleCount;

        public override string ToString() =>
            $"shoulder {ShoulderWidth:F3} m, hip {HipWidth:F3} m, torso {TorsoLength:F3} m, " +
            $"upperArm {UpperArmLength:F3} m, forearm {ForearmLength:F3} m (n={SampleCount})";
    }

    [System.Serializable]
    public sealed class CalibrationSettings
    {
        [Tooltip("How long the person must hold the pose.")]
        public float HoldSeconds = 1.5f;
        [Tooltip("A brief wobble shorter than this does not restart the countdown.")]
        public float GraceSeconds = 0.25f;
        public int MinSamples = 20;

        public float MinVisibility = 0.7f;
        public bool RequireWrists = true;

        [Tooltip("Max depth difference between shoulders (m): person must face the camera.")]
        public float MaxShoulderDepthDelta = 0.12f;
        [Tooltip("Max height difference between shoulders (m).")]
        public float MaxShoulderTilt = 0.06f;
        [Tooltip("Max torso lean from vertical (deg).")]
        public float MaxTorsoLeanDeg = 15f;
        [Tooltip("Upper arm must hang between these angles from the torso's down axis (deg): a relaxed A-pose.")]
        public Vector2 ArmAbductionRangeDeg = new Vector2(8f, 70f);
        [Tooltip("Max torso-centre speed in normalized image units per second.")]
        public float MaxStillSpeed = 0.08f;
    }

    public enum CalibrationState
    {
        WaitingForPerson,
        WaitingForPose,
        Collecting,
        Calibrated,
    }

    /// <summary>Why the pose is currently rejected; drive on-screen hints from this.</summary>
    public enum PoseIssue
    {
        None,
        NotVisible,
        NotFacingCamera,
        ShouldersTilted,
        Leaning,
        ArmsNotRelaxed,
        Moving,
    }

    /// <summary>
    /// Standing-pose calibration. The person stands facing the camera in a relaxed A-pose; once the
    /// pose has been valid and still for <see cref="CalibrationSettings.HoldSeconds"/>, the median of
    /// the collected limb lengths becomes their <see cref="BodyMeasurements"/>. Median rather than
    /// mean, so a few bad frames do not skew the result.
    /// </summary>
    public sealed class CalibrationStateMachine
    {
        readonly CalibrationSettings _s;

        public CalibrationState State { get; private set; } = CalibrationState.WaitingForPerson;
        public PoseIssue Issue { get; private set; } = PoseIssue.NotVisible;
        public BodyMeasurements Result { get; private set; }
        /// <summary>0..1 while collecting; 1 once calibrated.</summary>
        public float Progress { get; private set; }

        public event System.Action<CalibrationState> StateChanged;
        public event System.Action<BodyMeasurements> Calibrated;

        readonly List<float> _shoulder = new List<float>(), _hip = new List<float>(), _torso = new List<float>(),
                             _upper = new List<float>(), _fore = new List<float>();
        double _collectStart, _lastValid, _prevT;
        Vector2 _prevCenter;
        bool _hasPrev;

        static readonly int[] RequiredWithWrists =
        {
            Lm.LeftShoulder, Lm.RightShoulder, Lm.LeftElbow, Lm.RightElbow,
            Lm.LeftWrist, Lm.RightWrist, Lm.LeftHip, Lm.RightHip
        };

        public CalibrationStateMachine(CalibrationSettings settings) => _s = settings;

        /// <summary>Forget the measurements and start over (e.g. a "Recalibrate" button).</summary>
        public void Recalibrate()
        {
            ClearSamples();
            _hasPrev = false;
            Progress = 0f;
            SetState(CalibrationState.WaitingForPerson);
        }

        /// <summary>Skip the pose check and accept these measurements (testing, or a "quick start" option).</summary>
        public void Force(BodyMeasurements m)
        {
            Result = m;
            Progress = 1f;
            SetState(CalibrationState.Calibrated);
            Calibrated?.Invoke(m);
        }

        /// <param name="raw">Unsmoothed observation (used for the stillness test), or null if the person was not detected.</param>
        /// <param name="smoothed">Smoothed observation (used for measurements), or null if not detected.</param>
        public void Tick(PersonObservation raw, PersonObservation smoothed, double t)
        {
            if (State == CalibrationState.Calibrated) return; // keep the result until Recalibrate()

            if (raw == null || smoothed == null)
            {
                _hasPrev = false;
                if (State == CalibrationState.Collecting && t - _lastValid > _s.GraceSeconds) Restart();
                if (State != CalibrationState.Collecting) SetState(CalibrationState.WaitingForPerson);
                Issue = PoseIssue.NotVisible;
                return;
            }

            if (State == CalibrationState.WaitingForPerson) SetState(CalibrationState.WaitingForPose);

            Issue = Evaluate(raw, t);

            if (Issue != PoseIssue.None)
            {
                if (State == CalibrationState.Collecting && t - _lastValid > _s.GraceSeconds) Restart();
                return;
            }

            _lastValid = t;
            if (State == CalibrationState.WaitingForPose)
            {
                ClearSamples();
                _collectStart = t;
                SetState(CalibrationState.Collecting);
            }

            AddSample(smoothed);
            float held = (float)(t - _collectStart);
            Progress = Mathf.Clamp01(held / _s.HoldSeconds);

            if (held >= _s.HoldSeconds && _shoulder.Count >= _s.MinSamples)
            {
                Result = new BodyMeasurements
                {
                    ShoulderWidth = Median(_shoulder),
                    HipWidth = Median(_hip),
                    TorsoLength = Median(_torso),
                    UpperArmLength = Median(_upper),
                    ForearmLength = Median(_fore),
                    SampleCount = _shoulder.Count,
                };
                Progress = 1f;
                SetState(CalibrationState.Calibrated);
                Calibrated?.Invoke(Result);
            }
        }

        PoseIssue Evaluate(PersonObservation p, double t)
        {
            // Stillness first, because it needs the previous frame regardless of the other results.
            Vector2 center = p.TorsoCenter2D;
            float speed = 0f;
            if (_hasPrev && t > _prevT) speed = (center - _prevCenter).magnitude / (float)(t - _prevT);
            _prevCenter = center;
            _prevT = t;
            bool hadPrev = _hasPrev;
            _hasPrev = true;

            var required = _s.RequireWrists ? RequiredWithWrists : Lm.UpperBody;
            if (!p.AllVisible(required, _s.MinVisibility)) return PoseIssue.NotVisible;

            var w = p.World;
            Vector3 ls = w[Lm.LeftShoulder], rs = w[Lm.RightShoulder];
            Vector3 lh = w[Lm.LeftHip], rh = w[Lm.RightHip];

            if (Mathf.Abs(ls.z - rs.z) > _s.MaxShoulderDepthDelta) return PoseIssue.NotFacingCamera;
            if (Mathf.Abs(ls.y - rs.y) > _s.MaxShoulderTilt) return PoseIssue.ShouldersTilted;

            Vector3 up = ((ls + rs) - (lh + rh)) * 0.5f;
            if (Vector3.Angle(up, Vector3.up) > _s.MaxTorsoLeanDeg) return PoseIssue.Leaning;

            Vector3 down = -up.normalized;
            float aL = Vector3.Angle(w[Lm.LeftElbow] - ls, down);
            float aR = Vector3.Angle(w[Lm.RightElbow] - rs, down);
            Vector2 range = _s.ArmAbductionRangeDeg;
            if (aL < range.x || aL > range.y || aR < range.x || aR > range.y) return PoseIssue.ArmsNotRelaxed;

            if (!hadPrev || speed > _s.MaxStillSpeed) return PoseIssue.Moving;
            return PoseIssue.None;
        }

        void AddSample(PersonObservation p)
        {
            var w = p.World;
            Vector3 ls = w[Lm.LeftShoulder], rs = w[Lm.RightShoulder];
            Vector3 lh = w[Lm.LeftHip], rh = w[Lm.RightHip];
            _shoulder.Add(Vector3.Distance(ls, rs));
            _hip.Add(Vector3.Distance(lh, rh));
            _torso.Add(Vector3.Distance((ls + rs) * 0.5f, (lh + rh) * 0.5f));
            _upper.Add(0.5f * (Vector3.Distance(ls, w[Lm.LeftElbow]) + Vector3.Distance(rs, w[Lm.RightElbow])));
            _fore.Add(0.5f * (Vector3.Distance(w[Lm.LeftElbow], w[Lm.LeftWrist]) +
                              Vector3.Distance(w[Lm.RightElbow], w[Lm.RightWrist])));
        }

        void Restart()
        {
            ClearSamples();
            Progress = 0f;
            SetState(CalibrationState.WaitingForPose);
        }

        void ClearSamples()
        {
            _shoulder.Clear(); _hip.Clear(); _torso.Clear(); _upper.Clear(); _fore.Clear();
        }

        void SetState(CalibrationState s)
        {
            if (State == s) return;
            State = s;
            StateChanged?.Invoke(s);
        }

        static float Median(List<float> v)
        {
            var copy = new List<float>(v);
            copy.Sort();
            int n = copy.Count;
            if (n == 0) return 0f;
            return n % 2 == 1 ? copy[n / 2] : 0.5f * (copy[n / 2 - 1] + copy[n / 2]);
        }
    }
}
