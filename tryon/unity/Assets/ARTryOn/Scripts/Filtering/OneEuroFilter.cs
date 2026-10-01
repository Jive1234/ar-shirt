using UnityEngine;

namespace ARTryOn.Filtering
{
    /// <summary>
    /// One Euro Filter (Casiez et al., CHI 2012): a low-pass filter whose cutoff rises with speed.
    /// Slow movement → heavy smoothing (kills jitter); fast movement → light smoothing (kills lag).
    /// Tuning: lower <c>minCutoff</c> first until standing still looks rock steady, then raise
    /// <c>beta</c> until fast arm swings no longer lag.
    /// </summary>
    public sealed class OneEuroFilter
    {
        public float MinCutoff;
        public float Beta;
        public float DerivativeCutoff;

        float _x, _dx;
        double _lastT;
        bool _initialized;

        public OneEuroFilter(float minCutoff = 1.0f, float beta = 0.007f, float derivativeCutoff = 1.0f)
        {
            MinCutoff = minCutoff;
            Beta = beta;
            DerivativeCutoff = derivativeCutoff;
        }

        public bool Initialized => _initialized;
        public float Value => _x;

        public void Reset() => _initialized = false;

        static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }

        /// <param name="t">Sample timestamp in seconds (use the camera timestamp, not render time).</param>
        public float Filter(float value, double t)
        {
            if (!_initialized)
            {
                _x = value;
                _dx = 0f;
                _lastT = t;
                _initialized = true;
                return value;
            }

            float dt = (float)(t - _lastT);
            if (dt <= 1e-6f) return _x; // duplicate or out-of-order sample
            _lastT = t;

            float rawDx = (value - _x) / dt;
            _dx = Mathf.Lerp(_dx, rawDx, Alpha(DerivativeCutoff, dt));
            float cutoff = MinCutoff + Beta * Mathf.Abs(_dx);
            _x = Mathf.Lerp(_x, value, Alpha(cutoff, dt));
            return _x;
        }
    }

    /// <summary>
    /// Vector version. Speed is measured on the vector magnitude so all axes share one cutoff,
    /// which avoids the "diagonal wobble" you get from three independent scalar filters.
    /// </summary>
    public sealed class OneEuroFilter3
    {
        public float MinCutoff, Beta, DerivativeCutoff;

        Vector3 _x, _dx;
        double _lastT;
        bool _initialized;

        public OneEuroFilter3(float minCutoff = 1.0f, float beta = 0.007f, float derivativeCutoff = 1.0f)
        {
            MinCutoff = minCutoff;
            Beta = beta;
            DerivativeCutoff = derivativeCutoff;
        }

        public bool Initialized => _initialized;
        public Vector3 Value => _x;
        public Vector3 Velocity => _dx;

        public void Reset() => _initialized = false;

        static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }

        public Vector3 Filter(Vector3 value, double t)
        {
            if (!_initialized)
            {
                _x = value;
                _dx = Vector3.zero;
                _lastT = t;
                _initialized = true;
                return value;
            }

            float dt = (float)(t - _lastT);
            if (dt <= 1e-6f) return _x;
            _lastT = t;

            Vector3 rawDx = (value - _x) / dt;
            _dx = Vector3.Lerp(_dx, rawDx, Alpha(DerivativeCutoff, dt));
            float cutoff = MinCutoff + Beta * _dx.magnitude;
            _x = Vector3.Lerp(_x, value, Alpha(cutoff, dt));
            return _x;
        }

        /// <summary>Constant-velocity extrapolation, used to bridge short occlusions.</summary>
        public Vector3 Predict(double t)
        {
            float dt = Mathf.Clamp((float)(t - _lastT), 0f, 0.15f);
            return _x + _dx * dt;
        }
    }
}
