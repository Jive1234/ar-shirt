using ARTryOn.Core;
using UnityEngine;

namespace ARTryOn.Garment
{
    /// <summary>
    /// Camera-space joint positions for one smoothed pose.
    ///
    /// Depth comes from apparent size: the image-plane (xy) extent of shoulders + torso is measured both in the
    /// world landmarks (meters) and in pixels, so foreshortening cancels when the person turns or leans. Each joint
    /// then takes x/y from the image (so the garment sticks to the person's pixels) and depth from the hips plus the
    /// world landmarks' relative depth.
    /// </summary>
    public static class BodyFit
    {
        /// <param name="joints">Output, length <see cref="Lm.Count"/>, world-space positions in front of <paramref name="cam"/>.</param>
        /// <param name="imageWidth">Camera image width in pixels (the aspect ratio must match the camera's).</param>
        public static bool Compute(PersonObservation pose, Camera cam, int imageWidth, int imageHeight, Vector3[] joints, out float hipDepth)
        {
            var w = pose.World;
            var img = pose.Image;
            Vector3 ls = w[Lm.LeftShoulder], rs = w[Lm.RightShoulder], lh = w[Lm.LeftHip], rh = w[Lm.RightHip];
            Vector3 midS = (ls + rs) * 0.5f, midH = (lh + rh) * 0.5f;
            float worldSpan = XY(ls - rs).magnitude + XY(midS - midH).magnitude;

            Vector2 iMidS = (img[Lm.LeftShoulder] + img[Lm.RightShoulder]) * 0.5f;
            Vector2 iMidH = (img[Lm.LeftHip] + img[Lm.RightHip]) * 0.5f;
            float pxSpan = Px(img[Lm.LeftShoulder] - img[Lm.RightShoulder], imageWidth, imageHeight).magnitude
                         + Px(iMidS - iMidH, imageWidth, imageHeight).magnitude;
            hipDepth = 0f;
            if (pxSpan < 1f || worldSpan < 1e-3f) return false;

            float tanHalf = Mathf.Tan(0.5f * cam.fieldOfView * Mathf.Deg2Rad);
            float focalPx = 0.5f * imageHeight / tanHalf;
            hipDepth = focalPx * worldSpan / pxSpan;

            var t = cam.transform;
            for (int i = 0; i < Lm.Count; i++)
            {
                // World z points away from the camera, so a larger z is further away.
                float d = Mathf.Max(0.2f, hipDepth + (w[i].z - midH.z));
                var local = new Vector3((2f * img[i].x - 1f) * tanHalf * cam.aspect * d, (2f * img[i].y - 1f) * tanHalf * d, d);
                joints[i] = t.TransformPoint(local);
            }
            return true;
        }

        static Vector2 XY(Vector3 v) => new Vector2(v.x, v.y);
        static Vector2 Px(Vector2 v, int w, int h) => new Vector2(v.x * w, v.y * h);
    }
}
