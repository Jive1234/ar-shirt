using ARTryOn.Calibration;
using ARTryOn.Tracking;
using UnityEngine;

namespace ARTryOn.Rendering
{
    /// <summary>
    /// Shows the composited image full-screen plus a minimal on-screen UI (IMGUI, so the scene needs no Canvas):
    /// per-person calibration progress, a pose hint, colour / sleeve / recalibrate buttons and a debug read-out.
    /// </summary>
    public sealed class TryOnHud : MonoBehaviour
    {
        [SerializeField] TryOnManager manager;
        [SerializeField] TryOnCompositor compositor;
        [SerializeField] MediaPipePoseSource poseSource;

        static readonly Color[] Colors =
        {
            new Color(0.12f, 0.44f, 0.92f), new Color(0.88f, 0.11f, 0.28f), new Color(0.96f, 0.96f, 0.95f),
            new Color(0.07f, 0.09f, 0.15f), new Color(0.09f, 0.64f, 0.29f), new Color(0.96f, 0.62f, 0.04f),
        };

        bool _debug;
        float _fps;
        GUIStyle _label, _hint, _button;
        Texture2D _panel;

        void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.05f);
#if ENABLE_LEGACY_INPUT_MANAGER
            // Keyboard shortcuts (only with the classic Input Manager; the buttons always work).
            if (Input.GetKeyDown(KeyCode.D)) SetDebug(!_debug);
            if (Input.GetKeyDown(KeyCode.R)) manager.RecalibrateAll();
            if (Input.GetKeyDown(KeyCode.S)) manager.ToggleSleeve();
            if (Input.GetKeyDown(KeyCode.F11)) Screen.fullScreen = !Screen.fullScreen;
#endif
        }

        void SetDebug(bool on)
        {
            _debug = on;
            compositor.DebugMasks = on;
        }

        void EnsureStyles()
        {
            if (_label != null) return;
            _panel = new Texture2D(1, 1);
            _panel.SetPixel(0, 0, new Color(0.05f, 0.06f, 0.08f, 0.72f));
            _panel.Apply();
            int baseSize = Mathf.Max(14, Screen.height / 45);
            _label = new GUIStyle(GUI.skin.label) { fontSize = baseSize, normal = { textColor = Color.white, background = _panel }, padding = new RectOffset(12, 12, 8, 8), wordWrap = true };
            _hint = new GUIStyle(_label) { fontSize = Mathf.RoundToInt(baseSize * 1.3f), alignment = TextAnchor.MiddleCenter };
            _button = new GUIStyle(GUI.skin.button) { fontSize = baseSize };
        }

        void OnGUI()
        {
            EnsureStyles();
            var screen = new Rect(0, 0, Screen.width, Screen.height);

            if (compositor.Output != null)
                GUI.DrawTexture(screen, compositor.Output, ScaleMode.ScaleAndCrop, false);
            else
            {
                GUI.Label(new Rect(Screen.width / 2f - 300, Screen.height / 2f - 40, 600, 80), poseSource.Status, _hint);
                return;
            }

            // Per-person chips.
            float x = 12;
            var tracks = manager.Tracker.Tracks;
            string hint = tracks.Count == 0 ? "ยืนหน้ากล้องให้เห็นตั้งแต่หัวถึงสะโพก  (Stand in view, head to hips)" : null;
            for (int i = 0; i < tracks.Count; i++)
            {
                var c = tracks[i].Calibration;
                string text = c.State == CalibrationState.Calibrated ? $"คนที่ {i + 1}: ใส่เสื้อแล้ว"
                    : c.State == CalibrationState.Collecting ? $"คนที่ {i + 1}: กำลังวัดตัว {Mathf.RoundToInt(c.Progress * 100)}%"
                    : $"คนที่ {i + 1}: รอท่ายืน";
                GUI.Label(new Rect(x, 12, 260, 40), text, _label);
                x += 270;
                if (hint == null && c.State != CalibrationState.Calibrated) hint = Hint(c.Issue);
            }
            if (hint != null)
                GUI.Label(new Rect(Screen.width / 2f - 360, Screen.height - 150, 720, 56), hint, _hint);

            // Controls.
            float bh = _button.fontSize * 2.4f, y = Screen.height - bh - 16, bx = Screen.width / 2f - 420;
            for (int i = 0; i < Colors.Length; i++)
            {
                var prev = GUI.backgroundColor;
                GUI.backgroundColor = Colors[i];
                if (GUI.Button(new Rect(bx, y, bh, bh), "", _button)) manager.SetColor(Colors[i]);
                GUI.backgroundColor = prev;
                bx += bh + 6;
            }
            bx += 10;
            if (GUI.Button(new Rect(bx, y, 140, bh), manager.CurrentSleeve == Garment.ParametricShirt.Sleeve.Short ? "แขนสั้น (S)" : "แขนยาว (S)", _button)) manager.ToggleSleeve();
            bx += 150;
            if (GUI.Button(new Rect(bx, y, 160, bh), "วัดตัวใหม่ (R)", _button)) manager.RecalibrateAll();
            bx += 170;
            if (GUI.Button(new Rect(bx, y, 120, bh), "Debug (D)", _button)) SetDebug(!_debug);

            if (_debug)
            {
                string info = $"fps {_fps:F1}\ninference {poseSource.LastInferenceMs:F1} ms\npeople {tracks.Count}  shirts {manager.WearerCount}";
                GUI.Label(new Rect(Screen.width - 260, 12, 248, 100), info, _label);
            }
        }

        static string Hint(PoseIssue issue)
        {
            switch (issue)
            {
                case PoseIssue.NotVisible: return "ถอยห่างกล้องให้เห็นตั้งแต่หัวถึงสะโพกและมือทั้งสองข้าง";
                case PoseIssue.NotFacingCamera: return "หันหน้าตรงเข้ากล้อง";
                case PoseIssue.ShouldersTilted: return "ยืนให้ไหล่ทั้งสองข้างระดับเดียวกัน";
                case PoseIssue.Leaning: return "ยืนตัวตรง";
                case PoseIssue.ArmsNotRelaxed: return "ปล่อยแขนลงข้างตัว กางออกเล็กน้อย";
                case PoseIssue.Moving: return "ยืนนิ่งๆ สักครู่";
                default: return "ดีมาก ค้างไว้…";
            }
        }

        void OnDestroy()
        {
            if (_panel != null) Destroy(_panel);
        }
    }
}
