using ARTryOn.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ARTryOn.Rendering
{
    /// <summary>
    /// Final image = camera frame + garments, with the real clothes that stick out from under the virtual
    /// garment hidden (see ARTryOn/Composite shader for the per-pixel logic).
    ///
    /// The garment camera renders only the garment + occluder layers into a transparent render texture.
    /// Doing the composite ourselves (instead of drawing the webcam as a background quad) keeps the masks,
    /// the background plate and the garment alpha in one shader and works in Built-in RP and URP alike.
    /// </summary>
    public sealed class TryOnCompositor : MonoBehaviour
    {
        [SerializeField] MonoBehaviour poseSourceBehaviour; // IPoseSource
        [SerializeField] Camera garmentCamera;
        [SerializeField] Material compositeMaterial;        // ARTryOn/Composite
        [SerializeField] RawImage output;

        [Header("Real-clothing removal")]
        [Tooltip("Capture a clean background when nobody has been in frame this long. Requires a fixed camera.")]
        [SerializeField] float emptySecondsForPlate = 1.0f;
        [Tooltip("How far (in output pixels) the garment colour may be pushed into uncovered real clothing.")]
        [SerializeField] float edgeFillRadiusPx = 24f;

        IPoseSource _source;
        RenderTexture _garmentRT, _outRT, _plate;
        Texture _cameraFrame;
        bool _hasPlate;
        double _emptySince = -1;

        static readonly int CameraTexId = Shader.PropertyToID("_MainTex");
        static readonly int GarmentTexId = Shader.PropertyToID("_GarmentTex");
        static readonly int ClassMaskId = Shader.PropertyToID("_ClassMask");
        static readonly int PlateId = Shader.PropertyToID("_PlateTex");
        static readonly int HasClassMaskId = Shader.PropertyToID("_HasClassMask");
        static readonly int HasPlateId = Shader.PropertyToID("_HasPlate");
        static readonly int MirrorMaskId = Shader.PropertyToID("_MirrorMask");
        static readonly int FillRadiusId = Shader.PropertyToID("_FillRadius");
        static readonly int[] PersonMaskIds =
        {
            Shader.PropertyToID("_PersonMask0"), Shader.PropertyToID("_PersonMask1"),
            Shader.PropertyToID("_PersonMask2"), Shader.PropertyToID("_PersonMask3"),
        };
        static readonly int MaskCountId = Shader.PropertyToID("_PersonMaskCount");

        void Awake()
        {
            _source = (IPoseSource)poseSourceBehaviour;
            _source.FrameReady += OnFrame;
            // We call Render() ourselves so the garment is drawn for exactly the frame being shown.
            garmentCamera.enabled = false;
            garmentCamera.clearFlags = CameraClearFlags.SolidColor;
            garmentCamera.backgroundColor = new Color(0, 0, 0, 0);
        }

        void OnDestroy()
        {
            if (_source != null) _source.FrameReady -= OnFrame;
            Release(ref _garmentRT);
            Release(ref _outRT);
            Release(ref _plate);
        }

        // Runs after TryOnManager has posed the garments for this frame (set Script Execution Order so
        // TryOnManager comes first, or subscribe this component later).
        void OnFrame(PoseFrame frame)
        {
            _cameraFrame = frame.CameraFrame != null ? frame.CameraFrame : _source.CameraTexture;
            int w = _cameraFrame.width, h = _cameraFrame.height;
            Ensure(ref _garmentRT, w, h, 24, true);
            Ensure(ref _outRT, w, h, 0, false);

            UpdatePlate(frame);

            garmentCamera.targetTexture = _garmentRT;
            garmentCamera.Render(); // URP 14+: use RenderPipeline.SubmitRenderRequest instead

            var m = compositeMaterial;
            m.SetTexture(GarmentTexId, _garmentRT);
            m.SetFloat(MirrorMaskId, _source.Mirrored ? 1f : 0f);
            m.SetFloat(FillRadiusId, edgeFillRadiusPx);

            int count = Mathf.Min(frame.PersonMasks.Count, PersonMaskIds.Length);
            for (int i = 0; i < PersonMaskIds.Length; i++)
                m.SetTexture(PersonMaskIds[i], i < count ? frame.PersonMasks[i] : Texture2D.blackTexture);
            m.SetInt(MaskCountId, count);

            m.SetTexture(ClassMaskId, frame.ClassMask != null ? frame.ClassMask : Texture2D.blackTexture);
            m.SetFloat(HasClassMaskId, frame.ClassMask != null ? 1f : 0f);
            m.SetTexture(PlateId, _hasPlate ? _plate : (Texture)Texture2D.blackTexture);
            m.SetFloat(HasPlateId, _hasPlate ? 1f : 0f);

            Graphics.Blit(_cameraFrame, _outRT, m);
            output.texture = _outRT;
            // The webcam frame is not mirrored; the shader samples it mirrored, so the RawImage needs no flip.
        }

        void UpdatePlate(PoseFrame frame)
        {
            if (frame.People.Count > 0)
            {
                _emptySince = -1;
                return;
            }
            if (_emptySince < 0) _emptySince = frame.Timestamp;
            if (frame.Timestamp - _emptySince < emptySecondsForPlate) return;

            Ensure(ref _plate, _cameraFrame.width, _cameraFrame.height, 0, false);
            Graphics.Blit(_cameraFrame, _plate);
            _hasPlate = true;
        }

        static void Ensure(ref RenderTexture rt, int w, int h, int depth, bool msaa)
        {
            if (rt != null && rt.width == w && rt.height == h) return;
            Release(ref rt);
            rt = new RenderTexture(w, h, depth, RenderTextureFormat.ARGB32)
            {
                antiAliasing = msaa ? 4 : 1, // soft garment edges matter more than anything else for realism
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
        }

        static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.Destroy(rt);
            rt = null;
        }
    }
}
