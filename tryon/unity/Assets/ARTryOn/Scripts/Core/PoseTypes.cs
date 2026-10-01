using System.Collections.Generic;
using UnityEngine;

namespace ARTryOn.Core
{
    /// <summary>MediaPipe BlazePose 33-landmark indices (only the ones the garment pipeline uses).</summary>
    public static class Lm
    {
        public const int Nose = 0;
        public const int LeftShoulder = 11, RightShoulder = 12;
        public const int LeftElbow = 13, RightElbow = 14;
        public const int LeftWrist = 15, RightWrist = 16;
        public const int LeftHip = 23, RightHip = 24;
        public const int Count = 33;

        /// <summary>Landmarks that must be visible for a garment to be fitted / calibrated.</summary>
        /// <summary>
        /// Left/right partner of each landmark. In a mirrored (selfie) view the reflected body is
        /// geometrically a different person whose left is the subject's anatomical right, so the
        /// adapter swaps sides; downstream code can then treat every pose as an ordinary body.
        /// </summary>
        public static readonly int[] MirrorOf =
        {
            0, 4, 5, 6, 1, 2, 3, 8, 7, 10, 9, 12, 11, 14, 13, 16, 15,
            18, 17, 20, 19, 22, 21, 24, 23, 26, 25, 28, 27, 30, 29, 32, 31
        };

        public static readonly int[] UpperBody =
        {
            LeftShoulder, RightShoulder, LeftElbow, RightElbow, LeftHip, RightHip
        };
    }

    /// <summary>One detected person in one camera frame, already converted to Unity conventions.</summary>
    public sealed class PersonObservation
    {
        /// <summary>Normalized image coords (0..1, origin bottom-left like Unity viewport), already mirrored if selfie view.</summary>
        public readonly Vector2[] Image = new Vector2[Lm.Count];

        /// <summary>Metric 3D landmarks (meters), origin at hip center, Unity axes (x right, y up, z away from camera).</summary>
        public readonly Vector3[] World = new Vector3[Lm.Count];

        /// <summary>Per-landmark visibility/presence in 0..1.</summary>
        public readonly float[] Visibility = new float[Lm.Count];

        /// <summary>Index of this person's mask in <see cref="PoseFrame.PersonMasks"/>, or -1 if no mask.</summary>
        public int MaskIndex = -1;

        public bool IsVisible(int lm, float threshold) => Visibility[lm] >= threshold;

        public bool AllVisible(IReadOnlyList<int> lms, float threshold)
        {
            for (int i = 0; i < lms.Count; i++)
                if (Visibility[lms[i]] < threshold) return false;
            return true;
        }

        public Vector2 TorsoCenter2D =>
            (Image[Lm.LeftShoulder] + Image[Lm.RightShoulder] + Image[Lm.LeftHip] + Image[Lm.RightHip]) * 0.25f;
    }

    /// <summary>Everything the inference stage produced for one camera frame.</summary>
    public sealed class PoseFrame
    {
        /// <summary>Capture timestamp in seconds (monotonic). Filters use this, not render time.</summary>
        public double Timestamp;
        public int ImageWidth, ImageHeight;

        /// <summary>
        /// The exact camera image this result was computed from. Display this instead of the live webcam
        /// texture: inference takes 15–40 ms, and drawing the result over a newer frame makes the garment
        /// visibly trail the body.
        /// </summary>
        public Texture CameraFrame;
        public readonly List<PersonObservation> People = new List<PersonObservation>();

        /// <summary>
        /// Per-person soft masks from PoseLandmarker (output_segmentation_masks), Unity orientation, unmirrored.
        /// Index matches People[i].MaskIndex.
        /// </summary>
        public readonly List<Texture> PersonMasks = new List<Texture>();

        /// <summary>Optional "clothes" probability from selfie_multiclass_256x256 (R channel), same layout as the person masks.</summary>
        public Texture ClassMask;
    }

    public interface IPoseSource
    {
        /// <summary>Raised on the main thread whenever a new inference result is ready.</summary>
        event System.Action<PoseFrame> FrameReady;

        /// <summary>The live camera texture the results refer to.</summary>
        Texture CameraTexture { get; }

        /// <summary>Horizontal field of view of the physical webcam in degrees (used to match the Unity camera).</summary>
        float HorizontalFovDeg { get; }

        bool Mirrored { get; }

        /// <summary>True when the camera texture is stored upside down (WebCamTexture.videoVerticallyMirrored).</summary>
        bool CameraVerticallyFlipped { get; }
    }
}
