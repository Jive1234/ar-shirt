// Adapter between homuler/MediaPipeUnityPlugin (Tasks API) and the engine-agnostic PoseFrame types.
// Everything plugin-specific lives in this one file.
//
// Written against MediaPipeUnityPlugin v0.16.3. It uses only the runtime package (no Sample assets): models are
// read as bytes from StreamingAssets, inference runs on the CPU in VIDEO mode on the main thread, so the result
// always belongs to the camera frame being shown (no trailing garment).

using System.Collections.Generic;
using System.IO;
using ARTryOn.Core;
using Mediapipe.Tasks.Vision.ImageSegmenter;
using Mediapipe.Tasks.Vision.PoseLandmarker;
using Mediapipe.Unity;
using Mediapipe.Unity.Experimental;
using UnityEngine;
using BaseOptions = Mediapipe.Tasks.Core.BaseOptions;
using RunningMode = Mediapipe.Tasks.Vision.Core.RunningMode;

namespace ARTryOn.Tracking
{
    public sealed class MediaPipePoseSource : MonoBehaviour, IPoseSource
    {
        public const string PoseModelFile = "pose_landmarker_full.bytes";
        public const string SegmenterModelFile = "selfie_multiclass_256x256.bytes";
        const int ClothesClass = 4; // selfie_multiclass: 0 bg, 1 hair, 2 body-skin, 3 face-skin, 4 clothes, 5 others

        [Header("Camera")]
        [Tooltip("Leave empty for the default webcam.")]
        [SerializeField] string deviceName = "";
        [SerializeField] int requestedWidth = 1280;
        [SerializeField] int requestedHeight = 720;
        [SerializeField] int requestedFps = 30;
        [Tooltip("Typical laptop webcams are 60–75° horizontal. If the shirt is too big or small for everyone, change this.")]
        [SerializeField] float horizontalFovDeg = 62f;
        [SerializeField] bool mirrored = true;

        [Header("Inference")]
        [SerializeField, Range(1, 3)] int maxPeople = 3;
        [Tooltip("Hide real clothing that sticks out past the AR shirt. Costs extra CPU time.")]
        [SerializeField] bool useClothesMask = true;
        [Tooltip("The clothes mask changes slowly; running it every Nth frame saves time.")]
        [SerializeField, Range(1, 4)] int segmenterEveryNFrames = 2;
        [Tooltip("Run inference at most this often. 0 = every new camera frame.")]
        [SerializeField] float maxInferenceHz = 0f;

        public event System.Action<PoseFrame> FrameReady;
        public Texture CameraTexture => _webcam;
        public float HorizontalFovDeg => horizontalFovDeg;
        public bool Mirrored => mirrored;
        public bool CameraVerticallyFlipped => _webcam != null && _webcam.videoVerticallyMirrored;
        public float LastInferenceMs { get; private set; }
        public string Status { get; private set; } = "Starting camera…";

        WebCamTexture _webcam;
        TextureFrame _frame;
        PoseLandmarker _pose;
        ImageSegmenter _segmenter;
        PoseLandmarkerResult _poseResult;
        ImageSegmenterResult _segResult;
        long _lastTimestampMs = -1;
        float _lastInferenceTime;
        int _frameCount;

        readonly List<Texture2D> _maskPool = new List<Texture2D>();
        float[] _maskBuffer;
        Texture2D _classMask;
        float[] _classBuffer;

        System.Collections.IEnumerator Start()
        {
            _webcam = string.IsNullOrEmpty(deviceName)
                ? new WebCamTexture(requestedWidth, requestedHeight, requestedFps)
                : new WebCamTexture(deviceName, requestedWidth, requestedHeight, requestedFps);
            _webcam.Play();
            float waited = 0f;
            while (_webcam.width <= 16 && waited < 10f) { waited += Time.unscaledDeltaTime; yield return null; }
            if (_webcam.width <= 16)
            {
                Status = "No camera found. Check that a webcam is connected and allowed.";
                Debug.LogError("[TryOn] " + Status);
                yield break;
            }
            _frame = new TextureFrame(_webcam.width, _webcam.height, TextureFormat.RGBA32);

            Status = "Loading models…";
            byte[] poseModel = LoadModel(PoseModelFile);
            if (poseModel == null) yield break;

            _pose = PoseLandmarker.CreateFromOptions(new PoseLandmarkerOptions(
                new BaseOptions(BaseOptions.Delegate.CPU, modelAssetBuffer: poseModel),
                runningMode: RunningMode.VIDEO,
                numPoses: maxPeople,
                minPoseDetectionConfidence: 0.5f,
                minPosePresenceConfidence: 0.5f,
                minTrackingConfidence: 0.5f,
                outputSegmentationMasks: true));
            _poseResult = PoseLandmarkerResult.Alloc(maxPeople, true);

            if (useClothesMask)
            {
                byte[] segModel = LoadModel(SegmenterModelFile);
                if (segModel != null)
                {
                    _segmenter = ImageSegmenter.CreateFromOptions(new ImageSegmenterOptions(
                        new BaseOptions(BaseOptions.Delegate.CPU, modelAssetBuffer: segModel),
                        runningMode: RunningMode.VIDEO,
                        outputConfidenceMasks: true,
                        outputCategoryMask: false));
                    _segResult = ImageSegmenterResult.Alloc(true);
                }
            }
            Status = "Ready";
        }

        byte[] LoadModel(string file)
        {
            string path = Path.Combine(Application.streamingAssetsPath, file);
            if (File.Exists(path)) return File.ReadAllBytes(path);
            Status = $"Model not found: Assets/StreamingAssets/{file}. Run the menu AR Try-On > Download Models.";
            Debug.LogError("[TryOn] " + Status);
            return null;
        }

        void Update()
        {
            if (_pose == null || !_webcam.didUpdateThisFrame) return;
            if (maxInferenceHz > 0f && Time.unscaledTime - _lastInferenceTime < 1f / maxInferenceHz) return;
            _lastInferenceTime = Time.unscaledTime;

            var t0 = Time.realtimeSinceStartupAsDouble;

            // MediaPipe expects the first row at the top; Unity textures start at the bottom, hence the flip.
            _frame.ReadTextureOnCPU(_webcam, false, !_webcam.videoVerticallyMirrored);

            long ts = (long)(t0 * 1000.0);
            if (ts <= _lastTimestampMs) ts = _lastTimestampMs + 1; // MediaPipe needs strictly increasing timestamps
            _lastTimestampMs = ts;

            var frame = new PoseFrame
            {
                Timestamp = ts / 1000.0,
                ImageWidth = _webcam.width,
                ImageHeight = _webcam.height,
                CameraFrame = _webcam,
                ClassMask = _classMask,
            };

            using (var image = _frame.BuildCPUImage())
            {
                if (_pose.TryDetectForVideo(image, ts, null, ref _poseResult))
                    ReadPeople(frame);
                DisposeMasks(_poseResult.segmentationMasks);

                bool runSeg = _segmenter != null && frame.People.Count > 0 && _frameCount % segmenterEveryNFrames == 0;
                if (runSeg && _segmenter.TrySegmentForVideo(image, ts, null, ref _segResult))
                {
                    var masks = _segResult.confidenceMasks;
                    if (masks != null && masks.Count > ClothesClass)
                    {
                        ReadMask(masks[ClothesClass], ref _classMask, ref _classBuffer);
                        frame.ClassMask = _classMask;
                    }
                    DisposeMasks(masks);
                }
            }
            _frameCount++;
            LastInferenceMs = (float)((Time.realtimeSinceStartupAsDouble - t0) * 1000.0);
            FrameReady?.Invoke(frame);
        }

        void ReadPeople(PoseFrame frame)
        {
            var norms = _poseResult.poseLandmarks;
            var worlds = _poseResult.poseWorldLandmarks;
            var masks = _poseResult.segmentationMasks;
            int n = norms?.Count ?? 0;
            for (int p = 0; p < n; p++)
            {
                var norm = norms[p].landmarks;
                var world = worlds[p].landmarks;
                if (norm == null || world == null || norm.Count < Lm.Count || world.Count < Lm.Count) continue;

                var person = new PersonObservation();
                for (int i = 0; i < Lm.Count; i++)
                {
                    int src = mirrored ? Lm.MirrorOf[i] : i;
                    var nl = norm[src];
                    var wl = world[src];
                    // MediaPipe image: origin top-left, y down. Here: Unity viewport, origin bottom-left.
                    person.Image[i] = new Vector2(mirrored ? 1f - nl.x : nl.x, 1f - nl.y);
                    // MediaPipe world: x right, y down, z negative toward the camera. Unity camera looks along +Z.
                    person.World[i] = new Vector3(mirrored ? -wl.x : wl.x, -wl.y, wl.z);
                    person.Visibility[i] = Mathf.Min(nl.visibility ?? 1f, nl.presence ?? 1f);
                }

                if (masks != null && p < masks.Count && masks[p] != null)
                {
                    while (_maskPool.Count <= p) _maskPool.Add(null);
                    var tex = _maskPool[p];
                    ReadMask(masks[p], ref tex, ref _maskBuffer);
                    _maskPool[p] = tex;
                    person.MaskIndex = frame.PersonMasks.Count;
                    frame.PersonMasks.Add(tex);
                }
                frame.People.Add(person);
            }
        }

        /// <summary>Float mask → reused RFloat texture in Unity orientation (bottom-left origin, unmirrored).</summary>
        static void ReadMask(Mediapipe.Image mask, ref Texture2D tex, ref float[] buffer)
        {
            int w = mask.Width(), h = mask.Height();
            if (buffer == null || buffer.Length != w * h) buffer = new float[w * h];
            // isVerticallyFlipped = false: the plugin writes rows bottom-up, which is what Texture2D expects.
            if (!mask.TryReadChannel(0, buffer)) return;
            if (tex == null || tex.width != w || tex.height != h)
            {
                if (tex != null) Destroy(tex);
                tex = new Texture2D(w, h, TextureFormat.RFloat, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            }
            tex.SetPixelData(buffer, 0);
            tex.Apply(false);
        }

        static void DisposeMasks(List<Mediapipe.Image> masks)
        {
            if (masks == null) return;
            foreach (var m in masks) m?.Dispose();
        }

        void OnDestroy()
        {
            _pose?.Close();
            _segmenter?.Close();
            _frame?.Dispose();
            if (_webcam != null) _webcam.Stop();
            foreach (var t in _maskPool) if (t != null) Destroy(t);
            if (_classMask != null) Destroy(_classMask);
        }
    }
}
