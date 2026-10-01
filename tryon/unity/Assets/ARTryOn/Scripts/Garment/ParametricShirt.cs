using ARTryOn.Calibration;
using ARTryOn.Core;
using UnityEngine;

namespace ARTryOn.Garment
{
    /// <summary>
    /// A T-shirt that needs no 3D asset: every frame its surface is lofted around the body from the camera-space
    /// joints, sized by the person's calibrated measurements (adaptive sizing), and the hem and sleeve openings are
    /// simulated as Verlet cloth so they swing and lag naturally.
    ///
    /// Same idea as rigging (joints drive the surface), but the "skin weights" are analytic: each torso ring follows
    /// an interpolated hip → shoulder frame, each sleeve ring follows the arm. Port of tryon/web/src/garment.js.
    /// Swap in <see cref="GarmentFitter"/> with a rigged asset when you have one.
    /// </summary>
    public sealed class ParametricShirt : MonoBehaviour
    {
        const int Seg = 40, BodyRows = 18, YokeRows = 4, HemSimRows = 5;
        const int SleeveSeg = 20, SleeveRows = 12, SleeveSimRows = 3;

        public enum Sleeve { Short, Long }

        Sleeve _sleeve = Sleeve.Short;
        Grid _torso;
        Grid _left, _right;
        Material _bodyMat, _sleeveMat;
        Texture2D _bodyTex, _sleeveTex;
        readonly Frame[] _torsoFrames = new Frame[BodyRows];
        readonly Frame[] _sleeveFrames = new Frame[SleeveRows];

        struct Frame { public Vector3 center, across, forward; public float a, b; }

        /// <param name="template">Lit material (Standard / URP Lit). Cloned so each shirt can have its own colour.</param>
        public static ParametricShirt Create(string name, Material template, Color color, Sleeve sleeve)
        {
            var go = new GameObject(name);
            var shirt = go.AddComponent<ParametricShirt>();
            shirt._bodyMat = new Material(template);
            shirt._sleeveMat = new Material(template);
            shirt._torso = new Grid(go.transform, "Torso", BodyRows + YokeRows, Seg, shirt._bodyMat, HemSimRows, true);
            shirt._left = new Grid(go.transform, "LeftSleeve", SleeveRows, SleeveSeg, shirt._sleeveMat, SleeveSimRows, false);
            shirt._right = new Grid(go.transform, "RightSleeve", SleeveRows, SleeveSeg, shirt._sleeveMat, SleeveSimRows, false);
            shirt._sleeve = sleeve;
            shirt.SetColor(color);
            return shirt;
        }

        public void SetColor(Color color)
        {
            if (_bodyTex != null) Destroy(_bodyTex);
            if (_sleeveTex != null) Destroy(_sleeveTex);
            _bodyTex = MakeFabric(color, true);
            _sleeveTex = MakeFabric(color, false);
            _bodyMat.mainTexture = _bodyTex;
            _sleeveMat.mainTexture = _sleeveTex;
        }

        public void SetSleeve(Sleeve s)
        {
            _sleeve = s;
            _left.Reset();
            _right.Reset();
        }

        /// <param name="J">World-space joints from <see cref="BodyFit"/>.</param>
        /// <param name="ease">x = width, y = length; above 1 makes the shirt roomier so it covers the real one.</param>
        public void Drive(Vector3[] J, PersonObservation pose, BodyMeasurements m, Vector2 ease, float dt)
        {
            Vector3 ls = J[Lm.LeftShoulder], rs = J[Lm.RightShoulder], lh = J[Lm.LeftHip], rh = J[Lm.RightHip];
            Vector3 midS = (ls + rs) * 0.5f, midH = (lh + rh) * 0.5f;
            Vector3 up = (midS - midH).normalized;
            Vector3 acrossS = OrthoAcross(ls - rs, up);
            Vector3 acrossH = OrthoAcross(lh - rh, up);

            // Calibrated sizes (stable) rather than per-frame distances (noisy).
            float chestHalf = m.ShoulderWidth * 0.5f * 0.98f * ease.x;
            float hipHalf = Mathf.Max(m.HipWidth * 0.68f, m.ShoulderWidth * 0.42f) * ease.x;
            float waistHalf = (chestHalf + hipHalf) * 0.5f * 0.96f;
            float torsoLen = (midS - midH).magnitude;
            float hemDrop = 0.2f * m.TorsoLength * ease.y; // the shirt hangs below the hip joints

            // ---- torso rings ----
            float tHem = -hemDrop / Mathf.Max(torsoLen, 1e-3f);
            for (int r = 0; r < BodyRows; r++)
            {
                float t = tHem + (1f - tHem) * r / (BodyRows - 1);
                float tc = Mathf.Max(0f, t);
                Vector3 center = midH + up * (t * torsoLen);
                Vector3 across = OrthoAcross(Vector3.Lerp(acrossH, acrossS, Smooth(tc)), up);
                Vector3 forward = Vector3.Cross(up, across); // toward the camera for a person facing it
                float a;
                if (t <= 0f) a = hipHalf * (1f + 0.04f * (-t / Mathf.Max(-tHem, 1e-3f))); // slight flare at the hem
                else if (t < 0.4f) a = Mathf.Lerp(hipHalf, waistHalf, Smooth(t / 0.4f));
                else a = Mathf.Lerp(waistHalf, chestHalf, Smooth((t - 0.4f) / 0.6f));
                float b = a * Mathf.Lerp(0.72f, 0.62f, tc);
                _torsoFrames[r] = new Frame { center = center, across = across, forward = forward, a = a, b = b };
                WriteRing(_torso.Target, r, Seg, center, across, forward, a, b);
            }
            // Yoke: shoulders slope in to a round neckline.
            var top = _torsoFrames[BodyRows - 1];
            float neckA = 0.075f * (m.ShoulderWidth / 0.38f), neckB = 0.065f * (m.ShoulderWidth / 0.38f);
            for (int k = 1; k <= YokeRows; k++)
            {
                float s = (float)k / YokeRows;
                Vector3 center = top.center + up * (0.045f * Mathf.Sin(s * Mathf.PI * 0.5f)) - top.forward * (0.01f * s);
                WriteRing(_torso.Target, BodyRows - 1 + k, Seg, center, top.across, top.forward,
                          Mathf.Lerp(top.a, neckA, s), Mathf.Lerp(top.b, neckB, s));
            }
            _torso.Step(dt, (row, p) => CollideEllipse(p, _torsoFrames[Mathf.Min(row, BodyRows - 1)], 0.93f));

            // ---- sleeves ----
            bool isLong = _sleeve == Sleeve.Long;
            float rootR = Mathf.Max(0.062f, m.ShoulderWidth * 0.19f) * ease.x;
            float endR = isLong ? 0.045f * ease.x : rootR * 0.88f;
            Vector3 forwardS = Vector3.Cross(up, acrossS);
            DriveSleeve(_left, ls, J[Lm.LeftElbow], J[Lm.LeftWrist], 1f, acrossS, up, forwardS, m, ease, rootR, endR, isLong, dt);
            DriveSleeve(_right, rs, J[Lm.RightElbow], J[Lm.RightWrist], -1f, acrossS, up, forwardS, m, ease, rootR, endR, isLong, dt);
        }

        void DriveSleeve(Grid grid, Vector3 s, Vector3 e, Vector3 w, float side, Vector3 acrossS, Vector3 up, Vector3 forwardS,
                         BodyMeasurements m, Vector2 ease, float rootR, float endR, bool isLong, float dt)
        {
            // Root sits slightly inside the torso so the seam is hidden by overlap.
            Vector3 root = s - acrossS * (side * 0.02f) - up * 0.015f;
            Vector3 upper = (e - s).normalized, lower = (w - e).normalized;
            Vector3 elbow = root + upper * m.UpperArmLength;
            Vector3 end = isLong ? elbow + lower * (m.ForearmLength * 0.92f) : root + upper * (m.UpperArmLength * 0.5f * ease.y);

            for (int r = 0; r < SleeveRows; r++)
            {
                float u = (float)r / (SleeveRows - 1);
                Vector3 point, dir;
                if (!isLong) { point = Vector3.Lerp(root, end, u); dir = upper; }
                else
                {
                    float l1 = m.UpperArmLength, l2 = m.ForearmLength * 0.92f, d = u * (l1 + l2);
                    if (d <= l1)
                    {
                        point = root + upper * d;
                        float k = d / l1;
                        dir = k > 0.8f ? Vector3.Slerp(upper, lower, (k - 0.8f) / 0.4f) : upper; // bend smoothly at the elbow
                    }
                    else
                    {
                        float k = (d - l1) / l2;
                        point = elbow + lower * (d - l1);
                        dir = k < 0.2f ? Vector3.Slerp(upper, lower, 0.5f + k / 0.4f) : lower;
                    }
                }
                if (dir.sqrMagnitude < 1e-8f) dir = Vector3.down;
                dir.Normalize();
                Vector3 n1 = Vector3.Cross(dir, forwardS);
                if (n1.sqrMagnitude < 1e-8f) n1 = Vector3.Cross(dir, up);
                n1.Normalize();
                Vector3 n2 = Vector3.Cross(dir, n1);
                float radius = Mathf.Lerp(rootR, endR, u);
                _sleeveFrames[r] = new Frame { center = point, across = n1, forward = n2, a = radius, b = radius };
                WriteRing(grid.Target, r, SleeveSeg, point, n1, n2, radius, radius * 0.92f);
            }
            grid.Step(dt, (row, p) => CollideEllipse(p, _sleeveFrames[row], 0.8f));
        }

        /// <summary>Screen-space box of the shirt in viewport coordinates (0..1), for the compositor.</summary>
        public bool ViewportBounds(Camera cam, out Vector4 box)
        {
            float x0 = 1, y0 = 1, x1 = 0, y1 = 0;
            foreach (var g in new[] { _torso, _left, _right })
                foreach (var p in g.Pos)
                {
                    var v = cam.WorldToViewportPoint(p);
                    if (v.z <= 0f) continue;
                    x0 = Mathf.Min(x0, v.x); x1 = Mathf.Max(x1, v.x);
                    y0 = Mathf.Min(y0, v.y); y1 = Mathf.Max(y1, v.y);
                }
            box = new Vector4(x0, y0, x1, y1);
            return x1 > x0;
        }

        void OnDestroy()
        {
            _torso?.Dispose(); _left?.Dispose(); _right?.Dispose();
            if (_bodyMat != null) Destroy(_bodyMat);
            if (_sleeveMat != null) Destroy(_sleeveMat);
            if (_bodyTex != null) Destroy(_bodyTex);
            if (_sleeveTex != null) Destroy(_sleeveTex);
        }

        // -----------------------------------------------------------------------------------------------------
        // Ring-structured surface: rows × (segs+1) vertices; the last column duplicates the first for the UV seam.
        // Rows at one end (bottom for the torso, the opening for sleeves) are Verlet cloth.

        sealed class Grid
        {
            public readonly Vector3[] Target, Pos;
            readonly Vector3[] _prev;
            readonly int _rows, _cols, _simRows;
            readonly bool _simAtStart;
            readonly Mesh _mesh;
            bool _initialized;

            public Grid(Transform parent, string name, int rows, int segs, Material mat, int simRows, bool simAtStart)
            {
                _rows = rows; _cols = segs + 1; _simRows = simRows; _simAtStart = simAtStart;
                int n = rows * _cols;
                Target = new Vector3[n]; Pos = new Vector3[n]; _prev = new Vector3[n];

                var uv = new Vector2[n];
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < _cols; c++)
                        uv[r * _cols + c] = new Vector2((float)c / segs, (float)r / (rows - 1));
                var tris = new int[(rows - 1) * segs * 6];
                int k = 0;
                for (int r = 0; r < rows - 1; r++)
                    for (int c = 0; c < segs; c++)
                    {
                        int a = r * _cols + c, b = a + 1, d = a + _cols, e = d + 1;
                        // Rows advance along the body/arm, columns around it (WriteRing); with Unity's clockwise
                        // front faces this order makes the outside visible.
                        tris[k++] = a; tris[k++] = b; tris[k++] = d;
                        tris[k++] = b; tris[k++] = e; tris[k++] = d;
                    }

                _mesh = new Mesh { name = name };
                _mesh.MarkDynamic();
                _mesh.vertices = Pos;
                _mesh.uv = uv;
                _mesh.triangles = tris;
                _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f); // rebuilt every frame; never cull

                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = _mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            public void Reset() => _initialized = false;

            bool IsSim(int row) => _simAtStart ? row < _simRows : row >= _rows - _simRows;

            public void Step(float dt, System.Func<int, Vector3, Vector3?> collide)
            {
                dt = Mathf.Clamp(dt, 1f / 120f, 1f / 20f);
                if (!_initialized || MaxDelta() > 0.35f) // first frame or a tracking jump: start at rest
                {
                    System.Array.Copy(Target, Pos, Pos.Length);
                    System.Array.Copy(Target, _prev, Pos.Length);
                    _initialized = true;
                }

                const float damping = 0.96f;
                float pull = 1f - Mathf.Pow(1f - 0.12f, dt * 60f); // spring toward the designed shape, frame-rate independent
                Vector3 g = new Vector3(0f, -9.8f * 0.35f, 0f) * (dt * dt); // softened: real fabric rests on the body

                for (int r = 0; r < _rows; r++)
                {
                    bool sim = IsSim(r);
                    for (int c = 0; c < _cols; c++)
                    {
                        int i = r * _cols + c;
                        if (!sim) { _prev[i] = Pos[i]; Pos[i] = Target[i]; continue; }
                        Vector3 v = (Pos[i] - _prev[i]) * damping;
                        _prev[i] = Pos[i];
                        Vector3 x = Pos[i] + v + g;
                        Pos[i] = x + (Target[i] - x) * pull;
                    }
                }

                for (int it = 0; it < 3; it++)
                {
                    for (int r = 0; r < _rows; r++)
                    {
                        if (!IsSim(r)) continue;
                        int rOther = _simAtStart ? r + 1 : r - 1; // neighbour toward the pinned side
                        bool otherPinned = !IsSim(rOther);
                        for (int c = 0; c < _cols - 1; c++)
                        {
                            int i = r * _cols + c;
                            Relax(i, rOther * _cols + c, otherPinned);
                            Relax(i, i + 1, false);
                        }
                        Pos[r * _cols + _cols - 1] = Pos[r * _cols]; // close the seam
                    }
                    for (int r = 0; r < _rows; r++)
                    {
                        if (!IsSim(r)) continue;
                        for (int c = 0; c < _cols; c++)
                        {
                            int i = r * _cols + c;
                            var q = collide(r, Pos[i]);
                            if (q.HasValue) Pos[i] = q.Value;
                        }
                    }
                }

                _mesh.vertices = Pos;
                _mesh.RecalculateNormals();
            }

            void Relax(int i, int j, bool jPinned)
            {
                float rest = (Target[j] - Target[i]).magnitude;
                Vector3 d = Pos[j] - Pos[i];
                float len = d.magnitude;
                if (len < 1e-6f) return;
                Vector3 corr = d * ((len - rest) / len);
                if (jPinned) Pos[i] += corr;
                else { Pos[i] += corr * 0.5f; Pos[j] -= corr * 0.5f; }
            }

            float MaxDelta()
            {
                float m = 0f;
                for (int i = 0; i < Pos.Length; i++) m = Mathf.Max(m, (Target[i] - Pos[i]).sqrMagnitude);
                return Mathf.Sqrt(m);
            }

            public void Dispose() { if (_mesh != null) Object.Destroy(_mesh); }
        }

        /// <summary>Keep a point outside the body's elliptical cross-section (scaled by <paramref name="inner"/>).</summary>
        static Vector3? CollideEllipse(Vector3 p, Frame f, float inner)
        {
            Vector3 rel = p - f.center;
            float x = Vector3.Dot(rel, f.across), z = Vector3.Dot(rel, f.forward);
            float e = Mathf.Sqrt(Sq(x / (f.a * inner)) + Sq(z / (f.b * inner)));
            if (e >= 1f || e < 1e-6f) return null;
            float k = 1f / e - 1f;
            return p + f.across * (x * k) + f.forward * (z * k);
        }

        /// <summary>One ring: θ = 0 on the <paramref name="across"/> side, θ = π/2 facing <paramref name="forward"/>.</summary>
        static void WriteRing(Vector3[] output, int row, int segs, Vector3 center, Vector3 across, Vector3 forward, float a, float b)
        {
            int cols = segs + 1;
            for (int c = 0; c < cols; c++)
            {
                float th = 2f * Mathf.PI * c / segs;
                output[row * cols + c] = center + across * (Mathf.Cos(th) * a) + forward * (Mathf.Sin(th) * b);
            }
        }

        static Vector3 OrthoAcross(Vector3 across, Vector3 up)
        {
            Vector3 a = across - up * Vector3.Dot(across, up);
            return a.sqrMagnitude > 1e-12f ? a.normalized : Vector3.right;
        }

        static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }
        static float Sq(float v) => v * v;

        /// <summary>Knit-like fabric with ribbed hem/collar bands and, on the body, two chest stripes (u = 0.25 is centre front).</summary>
        static Texture2D MakeFabric(Color color, bool chestStripes)
        {
            const int W = 512, H = 256;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            var px = new Color32[W * H];
            var rng = new System.Random(7);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float n = (x % 3 == 0 ? -0.04f : 0.015f) + ((float)rng.NextDouble() - 0.5f) * 0.04f;
                    float v = y / (float)(H - 1);
                    if (v < 0.035f || v > 0.97f) n -= 0.06f; // ribbed bands at the hem (v = 0) and collar (v = 1)
                    Color c = color + new Color(n, n, n, 0f);
                    if (chestStripes)
                    {
                        float du = Mathf.Abs(x / (float)W - 0.25f);
                        if (du < 0.11f && ((v > 0.62f && v < 0.65f) || (v > 0.67f && v < 0.70f)))
                            c = Color.Lerp(c, Color.white, 0.85f);
                    }
                    c.a = 1f;
                    px[y * W + x] = c;
                }
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }
    }
}
