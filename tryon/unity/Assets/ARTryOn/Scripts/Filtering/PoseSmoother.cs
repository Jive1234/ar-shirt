using ARTryOn.Core;
using UnityEngine;

namespace ARTryOn.Filtering
{
    [System.Serializable]
    public struct OneEuroParams
    {
        public float minCutoff;
        public float beta;
        public float derivativeCutoff;

        public OneEuroParams(float minCutoff, float beta, float derivativeCutoff = 1f)
        {
            this.minCutoff = minCutoff;
            this.beta = beta;
            this.derivativeCutoff = derivativeCutoff;
        }

        // Both image (0..1 per frame width) and world (meters) coordinates move at roughly 0–3 units/s
        // for a person in frame, so similar parameters work for both. Tune on your camera.
        public static OneEuroParams DefaultImage => new OneEuroParams(1.2f, 0.8f);
        public static OneEuroParams DefaultWorld => new OneEuroParams(1.0f, 1.0f);
    }

    /// <summary>
    /// Smooths all landmarks of one tracked person. Low-visibility landmarks are not fed to the filter
    /// (MediaPipe guesses them and the guesses jump); instead they coast on the filter's velocity for a
    /// short time and then freeze, so a sleeve does not fly off when an arm leaves the frame.
    /// </summary>
    public sealed class PoseSmoother
    {
        public float VisibilityThreshold = 0.5f;

        readonly OneEuroFilter3[] _image = new OneEuroFilter3[Lm.Count];
        readonly OneEuroFilter3[] _world = new OneEuroFilter3[Lm.Count];

        /// <summary>Smoothed output; same layout as <see cref="PersonObservation"/>.</summary>
        public readonly PersonObservation Smoothed = new PersonObservation();

        public PoseSmoother(OneEuroParams image, OneEuroParams world)
        {
            for (int i = 0; i < Lm.Count; i++)
            {
                _image[i] = new OneEuroFilter3(image.minCutoff, image.beta, image.derivativeCutoff);
                _world[i] = new OneEuroFilter3(world.minCutoff, world.beta, world.derivativeCutoff);
            }
        }

        public void Reset()
        {
            for (int i = 0; i < Lm.Count; i++)
            {
                _image[i].Reset();
                _world[i].Reset();
            }
        }

        public PersonObservation Update(PersonObservation raw, double t)
        {
            for (int i = 0; i < Lm.Count; i++)
            {
                bool visible = raw.Visibility[i] >= VisibilityThreshold;
                if (visible || !_image[i].Initialized)
                {
                    Vector3 img = _image[i].Filter(raw.Image[i], t);
                    Smoothed.Image[i] = new Vector2(img.x, img.y);
                    Smoothed.World[i] = _world[i].Filter(raw.World[i], t);
                }
                else
                {
                    Vector3 img = _image[i].Predict(t);
                    Smoothed.Image[i] = new Vector2(img.x, img.y);
                    Smoothed.World[i] = _world[i].Predict(t);
                }
                Smoothed.Visibility[i] = raw.Visibility[i];
            }
            Smoothed.MaskIndex = raw.MaskIndex;
            return Smoothed;
        }
    }
}
