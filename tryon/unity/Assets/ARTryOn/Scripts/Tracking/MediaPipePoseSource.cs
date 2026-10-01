// Adapter between homuler/MediaPipeUnityPlugin (Tasks API, PoseLandmarker) and the engine-agnostic
// PoseFrame types. Everything plugin-specific lives in this one file so the rest of the pipeline
// does not change when the plugin's API does.
//
// VERIFY against the plugin version you install (written for the v0.14–v0.16 Tasks API). Compare with
// the plugin's sample "Tasks/Pose Landmark Detection/PoseLandmarkerRunner.cs" if anything fails to compile:
// names of TextureFramePool / Image constructors and the mask read-back helper move between releases.

using System.Collections.Concurrent;
using System.Collections.Generic;
using ARTryOn.Core;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Vision.PoseLandmarker;
using Mediapipe.Unity;
using UnityEngine;

namespace ARTryOn.Tracking
{
    public sealed class MediaPipePoseSource : MonoBehaviour, IPoseSource
    {
        [Header("Camera")]
        [SerializeField] int requestedWidth = 1280;
        [SerializeField] int requestedHeight = 720;
        [SerializeField] int requestedFps = 60;
        [Tooltip("Typical USB webcams are 60–78° horizontal. Measure yours for exact garment placement.")]
        [SerializeField] float horizontalFovDeg = 65f;
        [SerializeField] bool mirrored = true;

        [Header("Model")]
        [Tooltip("pose_landmarker_full.bytes is the best speed/accuracy trade-off; heavy is ~2x slower.")]
        [SerializeField] string modelAssetPath = "pose_landmarker_full.bytes";
        [SerializeField, Range(1, 4)] int maxPeople = 3;
        [SerializeField] float minDetectionConfidence = 0.5f;
        [SerializeField] float minTrackingConfidence = 0.5f;
        [SerializeField] bool useGpu = true;

        public event System.Action<PoseFrame> FrameReady;
        public Texture CameraTexture => _webcam;
        public float HorizontalFovDeg => horizontalFovDeg;
        public bool Mirrored => mirrored;

        WebCamTexture _webcam;
        Texture2D _cpuFrame;
        PoseLandmarker _landmarker;
        long _lastTimestampMs = -1;

        // MediaPipe LIVE_STREAM callbacks arrive on a worker thread; results are handed to the main thread here.
        readonly ConcurrentQueue<PoseFrame> _ready = new ConcurrentQueue<PoseFrame>();
        // Copies of submitted frames, so the result can be drawn over the image it was computed from.
        const int FrameHistory = 4;
        readonly RenderTexture[] _history = new RenderTexture[FrameHistory];
        readonly long[] _historyTs = new long[FrameHistory];
        int _historyNext;

        // Mask textures are recycled per slot to avoid allocating every frame.
        readonly List<Texture2D> _maskPool = new List<Texture2D>();
        readonly ConcurrentQueue<(int slot, float[] data, int w, int h)> _maskUploads =
            new ConcurrentQueue<(int, float[], int, int)>();

        System.Collections.IEnumerator Start()
        {
            _webcam = new WebCamTexture(requestedWidth, requestedHeight, requestedFps);
            _webcam.Play();
            yield return new WaitUntil(() => _webcam.width > 16);
            _cpuFrame = new Texture2D(_webcam.width, _webcam.height, TextureFormat.RGBA32, false);

            yield return AssetLoader.PrepareAssetAsync(modelAssetPath);

            var options = new PoseLandmarkerOptions(
                new BaseOptions(useGpu ? BaseOptions.Delegate.GPU : BaseOptions.Delegate.CPU,
                                modelAssetPath: modelAssetPath),
                runningMode: RunningMode.LIVE_STREAM,
                numPoses: maxPeople,
                minPoseDetectionConfidence: minDetectionConfidence,
                minPosePresenceConfidence: minDetectionConfidence,
                minTrackingConfidence: minTrackingConfidence,
                outputSegmentationMasks: true,
                resultCallback: OnResult);
            _landmarker = PoseLandmarker.CreateFromOptions(options, GpuManager.GpuResources);
        }

        void Update()
        {
            if (_landmarker != null && _webcam.didUpdateThisFrame)
                Submit();

            while (_maskUploads.TryDequeue(out var m))
                UploadMask(m.slot, m.data, m.w, m.h);

            // Only deliver the newest result; stale frames would make the filters see time going backwards.
            PoseFrame latest = null;
            while (_ready.TryDequeue(out var f)) latest = f;
            if (latest != null)
            {
                latest.CameraFrame = FindHistory((long)System.Math.Round(latest.Timestamp * 1000.0)) ?? (Texture)_webcam;
                for (int i = 0; i < latest.People.Count; i++)
                    if (latest.People[i].MaskIndex >= 0 && latest.People[i].MaskIndex < _maskPool.Count)
                        latest.PersonMasks.Add(_maskPool[latest.People[i].MaskIndex]);
                FrameReady?.Invoke(latest);
            }
        }

        void Submit()
        {
            // CPU path is the most portable. For 60 FPS on desktop, switch to the plugin's TextureFramePool
            // GPU path (see the plugin sample) to avoid the GetPixels32 read-back.
            _cpuFrame.SetPixels32(_webcam.GetPixels32());
            _cpuFrame.Apply(false);

            long ts = (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
            if (ts <= _lastTimestampMs) ts = _lastTimestampMs + 1; // MediaPipe requires strictly increasing timestamps
            _lastTimestampMs = ts;

            var slot = _history[_historyNext];
            if (slot == null || slot.width != _webcam.width || slot.height != _webcam.height)
            {
                if (slot != null) slot.Release();
                slot = _history[_historyNext] = new RenderTexture(_webcam.width, _webcam.height, 0);
            }
            Graphics.Blit(_webcam, slot);
            _historyTs[_historyNext] = ts;
            _historyNext = (_historyNext + 1) % FrameHistory;

            using var image = new Mediapipe.Image(_cpuFrame);
            _landmarker.DetectAsync(image, ts);
        }

        // Worker thread. Do not touch Unity objects here.
        void OnResult(PoseLandmarkerResult result, Mediapipe.Image image, long timestampMs)
        {
            var frame = new PoseFrame
            {
                Timestamp = timestampMs / 1000.0,
                ImageWidth = image.Width(),
                ImageHeight = image.Height(),
            };

            int n = result.poseLandmarks?.Count ?? 0;
            for (int p = 0; p < n; p++)
            {
                var norm = result.poseLandmarks[p].landmarks;
                var world = result.poseWorldLandmarks[p].landmarks;
                var person = new PersonObservation();
                for (int i = 0; i < Lm.Count && i < norm.Count; i++)
                {
                    int src = mirrored ? Lm.MirrorOf[i] : i;
                    var nl = norm[src];
                    var wl = world[src];
                    // MediaPipe image: origin top-left, y down. Unity viewport: origin bottom-left, y up.
                    float u = mirrored ? 1f - nl.x : nl.x;
                    person.Image[i] = new Vector2(u, 1f - nl.y);
                    // MediaPipe world: x right, y down, z toward camera is negative. Unity camera looks along +z.
                    person.World[i] = new Vector3(mirrored ? -wl.x : wl.x, -wl.y, wl.z);
                    person.Visibility[i] = Mathf.Min(nl.visibility ?? 1f, nl.presence ?? 1f);
                }

                if (result.segmentationMasks != null && p < result.segmentationMasks.Count)
                {
                    person.MaskIndex = p;
                    var mask = result.segmentationMasks[p];
                    int w = mask.Width(), h = mask.Height();
                    var data = new float[w * h];
                    // VERIFY: helper name differs between plugin versions (TryReadChannelNormalized / TryReadPixelData).
                    if (mask.TryReadChannelNormalized(0, data))
                        _maskUploads.Enqueue((p, data, w, h));
                }
                frame.People.Add(person);
            }
            _ready.Enqueue(frame);
        }

        Texture FindHistory(long ts)
        {
            for (int i = 0; i < FrameHistory; i++)
                if (_history[i] != null && _historyTs[i] == ts) return _history[i];
            return null;
        }

        void UploadMask(int slot, float[] data, int w, int h)
        {
            while (_maskPool.Count <= slot)
                _maskPool.Add(new Texture2D(w, h, TextureFormat.RFloat, false) { wrapMode = TextureWrapMode.Clamp });
            var tex = _maskPool[slot];
            if (tex.width != w || tex.height != h) tex.Reinitialize(w, h);
            tex.SetPixelData(data, 0);
            tex.Apply(false);
            // The mask is in MediaPipe image space (top-left origin, unmirrored); the composite shader flips it.
        }

        void OnDestroy()
        {
            _landmarker?.Close();
            foreach (var rt in _history) if (rt != null) rt.Release();
            if (_webcam != null) _webcam.Stop();
        }
    }
}
