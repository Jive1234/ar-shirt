using System.IO;
using ARTryOn.Rendering;
using ARTryOn.Tracking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace ARTryOn.EditorTools
{
    /// <summary>
    /// One-click setup: Unity menu "AR Try-On". Downloads the MediaPipe models into StreamingAssets and builds a
    /// ready-to-play scene with every component wired, so nothing has to be dragged around by hand.
    /// </summary>
    public static class ARTryOnSetup
    {
        const string Root = "Assets/ARTryOn";
        const string ScenePath = Root + "/TryOn.unity";

        static readonly (string url, string file)[] Models =
        {
            ("https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_full/float16/latest/pose_landmarker_full.task",
             MediaPipePoseSource.PoseModelFile),
            ("https://storage.googleapis.com/mediapipe-models/image_segmenter/selfie_multiclass_256x256/float32/latest/selfie_multiclass_256x256.tflite",
             MediaPipePoseSource.SegmenterModelFile),
        };

        [MenuItem("AR Try-On/Download Models", priority = 1)]
        public static bool DownloadModels()
        {
            string dir = Path.Combine(Application.dataPath, "StreamingAssets");
            Directory.CreateDirectory(dir);
            try
            {
                for (int i = 0; i < Models.Length; i++)
                {
                    var (url, file) = Models[i];
                    string path = Path.Combine(dir, file);
                    if (File.Exists(path) && new FileInfo(path).Length > 0) continue;
                    using (var req = UnityWebRequest.Get(url))
                    {
                        var op = req.SendWebRequest();
                        while (!op.isDone)
                        {
                            if (EditorUtility.DisplayCancelableProgressBar("AR Try-On", $"Downloading {file}…", req.downloadProgress))
                            {
                                req.Abort();
                                return false;
                            }
                            System.Threading.Thread.Sleep(50);
                        }
                        if (req.result != UnityWebRequest.Result.Success)
                        {
                            EditorUtility.DisplayDialog("AR Try-On", $"Download failed for {file}:\n{req.error}\n\nCheck the internet connection and try again.", "OK");
                            return false;
                        }
                        File.WriteAllBytes(path, req.downloadHandler.data);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            AssetDatabase.Refresh();
            return true;
        }

        [MenuItem("AR Try-On/Create Scene", priority = 2)]
        public static void CreateScene()
        {
            if (!DownloadModels()) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Materials
            if (!AssetDatabase.IsValidFolder(Root + "/Materials")) AssetDatabase.CreateFolder(Root, "Materials");
            var composite = CreateMaterial("Composite", Shader.Find("ARTryOn/Composite"));
            var occluder = CreateMaterial("Occluder", Shader.Find("ARTryOn/DepthOnlyOccluder"));
            bool urp = GraphicsSettings.currentRenderPipeline != null;
            var lit = Shader.Find(urp ? "Universal Render Pipeline/Lit" : "Standard");
            var shirt = CreateMaterial("Shirt", lit);
            if (shirt.HasProperty("_Glossiness")) shirt.SetFloat("_Glossiness", 0.08f);
            if (shirt.HasProperty("_Smoothness")) shirt.SetFloat("_Smoothness", 0.08f);

            // Lighting: soft room light from above the camera.
            var lightGo = new GameObject("Key Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.None;
            lightGo.transform.rotation = Quaternion.Euler(35f, -15f, 0f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.75f, 0.75f, 0.78f);
            RenderSettings.ambientEquatorColor = new Color(0.55f, 0.55f, 0.58f);
            RenderSettings.ambientGroundColor = new Color(0.3f, 0.3f, 0.32f);

            // Garment camera at the origin looking +Z (the pose maths assumes this). Rendered manually by the compositor.
            var camGo = new GameObject("Garment Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 20f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.allowHDR = false;

            // A camera that only clears the screen; the image itself is drawn by TryOnHud.
            var displayGo = new GameObject("Display Camera");
            var display = displayGo.AddComponent<Camera>();
            display.clearFlags = CameraClearFlags.SolidColor;
            display.backgroundColor = Color.black;
            display.cullingMask = 0;
            display.depth = -10;
            displayGo.transform.position = new Vector3(0, 0, -100);

            // Pipeline
            var root = new GameObject("AR Try-On");
            var source = root.AddComponent<MediaPipePoseSource>();
            var compositor = root.AddComponent<TryOnCompositor>();
            var manager = root.AddComponent<TryOnManager>();
            var hud = root.AddComponent<TryOnHud>();

            Wire(compositor, ("poseSourceBehaviour", source), ("garmentCamera", cam), ("compositeMaterial", composite));
            Wire(manager, ("poseSourceBehaviour", source), ("garmentCamera", cam), ("compositor", compositor),
                          ("shirtMaterial", shirt), ("occluderMaterial", occluder));
            Wire(hud, ("manager", manager), ("compositor", compositor), ("poseSource", source));

            EditorSceneManager.SaveScene(scene, ScenePath);
            var scenes = EditorBuildSettings.scenes;
            bool listed = false;
            foreach (var s in scenes) listed |= s.path == ScenePath;
            if (!listed)
            {
                var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(scenes) { new EditorBuildSettingsScene(ScenePath, true) };
                EditorBuildSettings.scenes = list.ToArray();
            }
            Selection.activeGameObject = root;
            EditorUtility.DisplayDialog("AR Try-On", "Scene created: " + ScenePath + "\n\nPress Play. Stand back so the camera sees you from head to hips, face it, arms slightly out, and hold still for 2 seconds.", "OK");
        }

        static Material CreateMaterial(string name, Shader shader)
        {
            if (shader == null) throw new System.Exception($"Shader for {name} not found. Is the ARTryOn folder complete?");
            string path = $"{Root}/Materials/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) { existing.shader = shader; return existing; }
            var mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static void Wire(Object target, params (string field, Object value)[] refs)
        {
            var so = new SerializedObject(target);
            foreach (var (field, value) in refs)
            {
                var prop = so.FindProperty(field);
                if (prop == null) throw new System.Exception($"{target.GetType().Name}.{field} not found");
                prop.objectReferenceValue = value;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
