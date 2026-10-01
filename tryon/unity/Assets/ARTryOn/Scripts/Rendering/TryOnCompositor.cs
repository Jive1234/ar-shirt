using System.Collections.Generic;
using ARTryOn.Core;
using UnityEngine;

namespace ARTryOn.Rendering
{
    /// <summary>
    /// Final image = camera frame + shirts, with real clothing that sticks out from under the AR shirt hidden
    /// (see the ARTryOn/Composite shader for the per-pixel logic).
    ///
    /// The garment camera renders only the shirts and occluders into a transparent, multisampled render texture;
    /// the composite is a single Blit, so this works the same in Built-in RP and URP.
    /// </summary>
    public sealed class TryOnCompositor : MonoBehaviour
    {
        const int MaxMasks = 3;

        [SerializeField] MonoBehaviour poseSourceBehaviour; // IPoseSource
        [SerializeField] Camera garmentCamera;
        [SerializeField] Material compositeMaterial;        // ARTryOn/Composite

        [Header("Real-clothing removal")]
        [Tooltip("How far (in output pixels) the shirt colour may be extended over uncovered real clothing.")]
        [SerializeField] float edgeFillRadiusPx = 40f;
        [Tooltip("Kiosk with a fixed camera only: replace uncovered real clothing with a clean background captured " +
                 "while nobody is in view. Off by default because it also erases real arms in long sleeves that " +
                 "stick out past a short AR sleeve.")]
        [SerializeField] bool useBackgroundPlate = false;
        [SerializeField] bool debugMasks = false;

        /// <summary>The composited image. Draw it full-screen (TryOnHud does).</summary>
        public RenderTexture Output => _outRT;

        IPoseSource _source;
        RenderTexture _garmentRT, _outRT, _plate;
        bool _hasPlate;
        double _emptySince = -1;
        readonly Vector4[] _zones = new Vector4[MaxMasks];

        static readonly int GarmentTexId = Shader.PropertyToID("_GarmentTex");
        static readonly int ClassMaskId = Shader.PropertyToID("_ClassMask");
        static readonly int PlateId = Shader.PropertyToID("_PlateTex");
        static readonly int HasClassMaskId = Shader.PropertyToID("_HasClassMask");
        static readonly int HasPlateId = Shader.PropertyToID("_HasPlate");
        static readonly int MirrorId = Shader.PropertyToID("_Mirror");
        static readonly int FlipCameraYId = Shader.PropertyToID("_FlipCameraY");
        static readonly int FillRadiusId = Shader.PropertyToID("_FillRadius");
        static readonly int MaskCountId = Shader.PropertyToID("_PersonMaskCount");
        static readonly int ZonesId = Shader.PropertyToID("_Zones");
        static readonly int ZoneCountId = Shader.PropertyToID("_ZoneCount");
        static readonly int DebugId = Shader.PropertyToID("_Debug");
        static readonly int[] PersonMaskIds =
        {
            Shader.PropertyToID("_PersonMask0"), Shader.PropertyToID("_PersonMask1"), Shader.PropertyToID("_PersonMask2"),
        };

        public bool DebugMasks { get => debugMasks; set => debugMasks = value; }

        void Awake()
        {
            _source = (IPoseSource)poseSourceBehaviour;
            // Rendered manually in Render() so the shirts are drawn for exactly the frame being shown.
            garmentCamera.enabled = false;
            garmentCamera.clearFlags = CameraClearFlags.SolidColor;
            garmentCamera.backgroundColor = new Color(0, 0, 0, 0);
        }

        void OnDestroy()
        {
            Release(ref _garmentRT);
            Release(ref _outRT);
            Release(ref _plate);
        }

        /// <summary>Called by TryOnManager after the shirts are posed for this frame.</summary>
        /// <param name="zones">Viewport boxes (x0, y0, x1, y1) where real clothing may be replaced.</param>
        public void Render(PoseFrame frame, IReadOnlyList<Vector4> zones)
        {
            Texture cameraTex = frame.CameraFrame != null ? frame.CameraFrame : _source.CameraTexture;
            if (cameraTex == null) return;
            int w = cameraTex.width, h = cameraTex.height;
            Ensure(ref _garmentRT, w, h, 24, 4);
            Ensure(ref _outRT, w, h, 0, 1);

            if (useBackgroundPlate) UpdatePlate(frame, cameraTex);

            garmentCamera.targetTexture = _garmentRT;
            garmentCamera.Render(); // URP 14+: RenderPipeline.SubmitRenderRequest(garmentCamera, ...) instead
            garmentCamera.targetTexture = null;

            var m = compositeMaterial;
            m.SetTexture(GarmentTexId, _garmentRT);
            m.SetFloat(MirrorId, _source.Mirrored ? 1f : 0f);
            m.SetFloat(FlipCameraYId, _source.CameraVerticallyFlipped ? 1f : 0f);
            m.SetFloat(FillRadiusId, edgeFillRadiusPx);
            m.SetFloat(DebugId, debugMasks ? 1f : 0f);

            int count = Mathf.Min(frame.PersonMasks.Count, MaxMasks);
            for (int i = 0; i < MaxMasks; i++)
                m.SetTexture(PersonMaskIds[i], i < count ? frame.PersonMasks[i] : Texture2D.blackTexture);
            m.SetInt(MaskCountId, count);

            m.SetTexture(ClassMaskId, frame.ClassMask != null ? frame.ClassMask : Texture2D.blackTexture);
            m.SetFloat(HasClassMaskId, frame.ClassMask != null ? 1f : 0f);
            m.SetTexture(PlateId, _hasPlate ? _plate : (Texture)Texture2D.blackTexture);
            m.SetFloat(HasPlateId, _hasPlate ? 1f : 0f);

            int zc = Mathf.Min(zones.Count, MaxMasks);
            for (int i = 0; i < MaxMasks; i++) _zones[i] = i < zc ? zones[i] : Vector4.zero;
            m.SetVectorArray(ZonesId, _zones);
            m.SetInt(ZoneCountId, zc);

            Graphics.Blit(cameraTex, _outRT, m);
        }

        void UpdatePlate(PoseFrame frame, Texture cameraTex)
        {
            if (frame.People.Count > 0) { _emptySince = -1; return; }
            if (_emptySince < 0) _emptySince = frame.Timestamp;
            if (frame.Timestamp - _emptySince < 1.0) return;
            Ensure(ref _plate, cameraTex.width, cameraTex.height, 0, 1);
            Graphics.Blit(cameraTex, _plate); // refreshed every second while the scene is empty
            _emptySince = frame.Timestamp;
            _hasPlate = true;
        }

        static void Ensure(ref RenderTexture rt, int w, int h, int depth, int msaa)
        {
            if (rt != null && rt.width == w && rt.height == h) return;
            Release(ref rt);
            rt = new RenderTexture(w, h, depth, RenderTextureFormat.ARGB32)
            {
                antiAliasing = msaa, // soft shirt edges matter more than anything else for realism
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
        }

        static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Destroy(rt);
            rt = null;
        }
    }
}
