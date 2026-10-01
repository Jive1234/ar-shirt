# AR Virtual Try-On (Webcam, Real-time) — แผนงาน สถาปัตยกรรม และโค้ดตั้งต้น

> **มีเวอร์ชันเว็บที่ใช้งานได้แล้ว** ที่ [`web/`](web/README.md) (MediaPipe + three.js เปิดผ่านเบราว์เซอร์)
>
> สรุปสั้น: **ทำได้ แต่ต้องยอมรับ trade-off 4 จุด** (cloth physics, การวัดรอบอก, การลบเสื้อจริง 100%, จำนวนคนพร้อมกัน)
> แนะนำ **Unity + MediaPipe Unity Plugin (Tasks API) + Magica Cloth 2 / Unity Cloth** สำหรับงานจริง
> Python เหมาะแค่ทำวิจัย/ทดลองโมเดล ไม่เหมาะเป็นตัวโปรดักชัน

---

## 1. ผลการประเมินความเป็นไปได้ (ต่อข้อกำหนด)

| # | ข้อกำหนด | ผลประเมิน | สิ่งที่ทำได้จริง / ข้อแนะนำ |
|---|---|---|---|
| 1 | Webcam 30–60 FPS | ✅ ได้ (มีเงื่อนไข) | **เรนเดอร์ 60 FPS ได้**, แต่ inference pose หลายคน + segmentation ได้ราว **25–30 Hz** บน GPU ระดับกลาง แนะนำแยก “render loop 60” กับ “inference 30” และใช้ OneEuro + การเลือกเฟรมที่ตรงกับผล (frame-history sync) ซึ่งมีในโค้ดแล้ว |
| 2 | 3D mesh + rigging + cloth physics | ✅ ได้ (มีเงื่อนไข) | ช่วงลำตัวใช้ **skinning ตามกระดูก** (แม่นและนิ่ง) ส่วนที่หลวม (ชายเสื้อ, ปลายแขนเสื้อ, เสื้อคลุม) ใช้ **cloth simulation** ส่วน “รอยยับละเอียด” ทำด้วย normal map / wrinkle map ไม่ใช่ physics จริง เพราะ physics เต็มตัวแบบออฟไลน์ (Marvelous Designer) ช้าเกินไปสำหรับ real-time |
| 3 | Auto-scale & morph ตามสรีระ | ⚠️ ได้บางส่วน | กล้องเดียว **วัดได้ดี: ความกว้างไหล่, ความยาวลำตัว, ความยาวแขน** (ใช้ปรับความยาวกระดูก) **วัดไม่ได้ตรงๆ: รอบอก/รอบเอว** (ไม่มี depth) → ประมาณจากสัดส่วนสะโพก/ไหล่ แล้วขับ blend shape “Wide” ค่าขนาดสัมบูรณ์คลาดได้ ±10–15% แต่ **ภาพที่ซ้อนยังพอดีตัว** เพราะความลึกคำนวณจากสเกลชุดเดียวกัน |
| 4 | ปิดทับเสื้อจริงมิดชิด | ⚠️ ได้เกือบหมด | “ลบ” เสื้อจริงไม่ได้ 100% แบบ real-time ใช้ 4 ชั้นรวมกัน: (1) ease ให้ชุด AR ใหญ่กว่าตัว 5–8% (2) occluder มือ/แขน/หัวด้วย depth (3) **clothes mask** จาก selfie_multiclass แล้วเติมด้วยสีชุด AR ที่ใกล้ที่สุด (4) ถ้ากล้องตั้งนิ่ง ใช้ **background plate** แทนผ้าที่โผล่ (กรณีเสื้อจริงแขนยาว ชุด AR แขนสั้น ต้องใช้ข้อ 4 หรือ AI inpainting ซึ่งยังช้า) |
| 5 | Anti-jitter | ✅ ได้ | OneEuroFilter (เลือกแทน Kalman เพราะปรับง่าย 2 ค่า, lag ต่ำตอนขยับเร็ว) + ข้าม landmark ที่ visibility ต่ำแล้ว coast ด้วยความเร็วเดิม — **ผ่านการทดสอบ ลด jitter เหลือ ~32%** |
| 6 | หลายคนพร้อมกัน | ⚠️ ได้ 2–3 คน | PoseLandmarker รองรับ `num_poses` แต่ความแม่นยำตกเมื่อคนซ้อนกัน และต้นทุนเพิ่มตามจำนวนคน มี tracker ให้ ID คงที่ + filter/calibration แยกคน **การบังกันระหว่างคน** (คนหน้าบังชุดคนหลัง) เป็นงานเฟส 2 |
| 7 | Calibration 1–2 วินาที | ✅ ได้ | State machine ตรวจ: มองเห็นครบ, หันหน้าตรง, ไหล่ระนาบ, ตัวตรง, แขน A-pose, นิ่ง แล้วเก็บค่า **median** 1.5 วิ — **ผ่านการทดสอบ ค่าคลาด < 2 ซม.** บนข้อมูลจำลอง |

ข้อจำกัดอื่นที่ควรรู้ก่อนเริ่ม
- **หันข้าง > ~60°** หรือหันหลัง: landmark ไหล่/สะโพกซ้อนกัน การวาง torso จะไม่นิ่ง ควรแสดงคำแนะนำให้หันเข้ากล้อง
- **การบิดแขนท่อนล่าง (pronation)** ไม่มีใน pose landmark → แขนเสื้อไม่บิดตาม ถ้าต้องการ เพิ่ม HandLandmarker (เพิ่มต้นทุน)
- **คุณภาพ asset** สำคัญกว่าโค้ด: เสื้อต้องทำเป็นหน่วยเมตร, A-pose, rig ตามรายการกระดูกในข้อ 6, มี blend shape “Wide”

---

## 2. เปรียบเทียบสถาปัตยกรรม

| เกณฑ์ | **Unity + MediaPipe Unity Plugin** (แนะนำ) | Python + OpenCV + MediaPipe/PyTorch + Pyrender/OpenGL | Web (Three.js + MediaPipe JS) |
|---|---|---|---|
| Cloth physics real-time | ✅ Magica Cloth 2 (Burst/Jobs), Unity Cloth (ฟรี) | ❌ ต้องเขียนเอง / PyBullet ช้าและไม่เหมาะเสื้อผ้า | ⚠️ ต้องเขียน verlet เอง |
| Skinning / blend shapes / shader | ✅ ครบ, มี Shader Graph | ⚠️ pyrender ทำ skinning ไม่ได้ ต้องเขียน OpenGL เอง | ✅ Three.js SkinnedMesh |
| ประสิทธิภาพ | ✅ GPU delegate, render 60 FPS | ⚠️ GIL + copy ภาพ CPU↔GPU, ได้ ~15–25 FPS | ⚠️ ขึ้นกับเบราว์เซอร์/เครื่อง |
| Multi-person + segmentation | ✅ PoseLandmarker `num_poses` + masks | ✅ ง่ายที่สุดในการทดลองโมเดล | ✅ |
| แจกจ่าย (kiosk, Windows/Mac/Android) | ✅ build เดียว | ❌ ติดตั้งยาก | ✅ เปิดลิงก์ได้เลย |
| ความเหมาะสม | **โปรดักชัน / kiosk / แอป** | **งานวิจัย, ทดลองโมเดล segmentation ใหม่** | เดโมเบาๆ, การตลาด |

**ข้อแนะนำ:** ใช้ Unity 2022.3 LTS หรือ Unity 6 + URP + [homuler/MediaPipeUnityPlugin](https://github.com/homuler/MediaPipeUnityPlugin) (PoseLandmarker, `pose_landmarker_full`) + Magica Cloth 2 (ถ้ามีงบ) หรือ Unity Cloth (ฟรี)

หมายเหตุ: starter MindAR ที่ทำไว้ก่อนหน้าเป็น **image-target AR** (ติดภาพบนลวดลายเสื้อ) ไม่ใช่ body tracking จึงไม่ตรงกับโจทย์นี้ ถ้าอยากได้เวอร์ชันเว็บควรเปลี่ยนเป็น MediaPipe Pose JS + Three.js แทน

---

## 3. Data Pipeline & Logic Flow

```
 [Webcam 60fps]
      │ WebCamTexture
      ▼
 ┌──────────────────────┐   เก็บสำเนาเฟรมพร้อม timestamp (frame history 4 ช่อง)
 │ MediaPipePoseSource  │── DetectAsync (LIVE_STREAM, GPU) ──► worker thread
 └──────────────────────┘                                        │
      ▲  main thread ◄── queue ◄── PoseLandmarkerResult ─────────┘
      │       • 33 image landmarks  → แปลงเป็น viewport (+mirror, สลับซ้าย/ขวา)
      │       • 33 world landmarks  → เมตร, แกน Unity
      │       • per-person mask     → Texture2D (RFloat)
      ▼
 ┌──────────────────────┐
 │ PersonTracker        │  จับคู่คนข้ามเฟรม (nearest torso center) → Track ID คงที่
 │  ├ PoseSmoother      │  OneEuro ต่อ landmark (image + world), visibility ต่ำ = coast
 │  └ CalibrationSM     │  WaitingForPerson → WaitingForPose → Collecting(1.5s) → Calibrated
 └──────────────────────┘                                   │ BodyMeasurements (median)
      ▼                                                     ▼
 ┌──────────────────────┐   ครั้งเดียวหลัง calibrate: ApplyMeasurements
 │ GarmentFitter        │    • ยืดความยาวกระดูก (ย้าย localPosition ไม่ใช้ localScale → ไม่เกิด shear)
 │                      │    • ปรับความกว้างไหล่ (เฉพาะแกนข้าง) • blend shape “Wide” • rebuild cloth
 │                      │   ทุกเฟรม: Drive
 │                      │    • ความลึก z = f · span_world / span_px
 │                      │    • ข้อต่อ = unproject(image xy, z + Δz_world)  → ชุดเกาะพิกเซลจริง
 │                      │    • หมุน hips/spine/chest จาก basis ไหล่-สะโพก, แขนด้วย FromToRotation
 └──────────────────────┘
      ▼
 ┌──────────────────────┐   Cloth sim (Magica/Unity Cloth) รันใน LateUpdate ตามกระดูกที่ถูกขยับ
 │ BodyOccluder         │   แคปซูล depth-only: ปลายแขน, มือ, หัว → บังชุดตามจริง
 └──────────────────────┘
      ▼
 ┌──────────────────────┐   garment camera → RenderTexture (alpha, MSAA 4x)
 │ TryOnCompositor      │   Composite.shader: กล้อง + ลบผ้าจริงที่โผล่ + วางชุด
 └──────────────────────┘
      ▼
   [จอ]
```

งบเวลาต่อเฟรมโดยประมาณ ยังไม่ได้วัดจริง (เป้า 30 Hz inference / 60 FPS render, GPU ระดับ GTX 1660 / M1):
inference 2 คน + mask ~12–20 ms (บน worker) · smoothing + tracking < 0.5 ms · bone fit < 0.2 ms/คน · cloth 1–3 ms/ชุด · composite < 1 ms

---

## 4. โค้ดที่ส่งมา (`unity/Assets/ARTryOn/`)

| ไฟล์ | หน้าที่ |
|---|---|
| `Scripts/Core/PoseTypes.cs` | โครงข้อมูลกลาง (`PoseFrame`, `PersonObservation`, index landmark, ตาราง mirror) แยกจาก plugin |
| `Scripts/Tracking/MediaPipePoseSource.cs` | **ดึง 3D pose + segmentation mask** จาก PoseLandmarker (หลายคน), แปลงพิกัด, frame-history sync — *ไฟล์เดียวที่ผูกกับ plugin, ต้องเทียบ API กับเวอร์ชันที่ติดตั้ง* |
| `Scripts/Filtering/OneEuroFilter.cs`, `PoseSmoother.cs` | **Smoothing filter ลดการสั่น** (scalar + vector), visibility-aware |
| `Scripts/Tracking/PersonTracker.cs` | **Multi-person** ID คงที่, หมดเวลาแล้วลบ |
| `Scripts/Calibration/CalibrationStateMachine.cs` | **State machine calibration 1.5 วิ** + เหตุผลที่ท่าไม่ผ่าน (ใช้แสดงคำแนะนำบนจอ) |
| `Scripts/Garment/ParametricShirt.cs`, `BodyFit.cs` | **เสื้อสร้างจากโค้ด** (ไม่ต้องมีโมเดล 3D) ตามขนาดที่วัดได้ + ผ้า Verlet ที่ชายเสื้อ/ปลายแขน, ตำแหน่ง 3D จากภาพ |
| `Scripts/Garment/GarmentFitter.cs` | (ทางเลือก) เสื้อ rigged: **ปรับ bone length/width + blend shape** และขับท่าทุกเฟรม |
| `Scripts/Garment/BodyOccluder.cs` | occluder มือ/แขน/หัว |
| `Scripts/Rendering/TryOnCompositor.cs` | เรนเดอร์ชุดลง RT, จับ background plate, เรียก composite |
| `Scripts/TryOnManager.cs` | ต่อทุกส่วนเข้าด้วยกัน, สร้างชุดต่อคนหลัง calibrate |
| `Scripts/Rendering/TryOnHud.cs` | แสดงภาพ + ปุ่ม/คำแนะนำภาษาไทย (ไม่ต้องมี Canvas) |
| `Editor/ARTryOnSetup.cs` | เมนู *AR Try-On → Download Models / Create Scene* ตั้งค่าฉากให้ในคลิกเดียว |
| `Shaders/DepthOnlyOccluder.shader` | เขียน depth อย่างเดียว |
| `Shaders/Composite.shader` | **occlusion / ลบผ้าจริง / วางชุด** |
| `../tests/LogicTests/` | ทดสอบ filter, calibration, tracker (รันด้วย `dotnet run` ไม่ต้องมี Unity) |

ผลทดสอบล่าสุด (`dotnet run` ใน `tests/LogicTests`): **15/15 ผ่าน** — OneEuro ลด jitter เหลือ 32% และไม่ lag ตอนขยับเร็ว, calibrate ใน 1.63 วิ ค่าคลาด < 2 ซม., ปฏิเสธ T-pose และคนที่เดิน, 2 คนสลับลำดับทุกเฟรมยังได้ ID และค่าวัดถูกคน

คอมไพล์ผ่านกับ Unity 2021.3 reference DLL และซอร์ส runtime จริงของ MediaPipeUnityPlugin v0.16.3 แล้ว แต่ยังไม่ได้รันใน Unity Editor จริง (ไม่มี Unity/กล้องในเครื่องที่เขียน) จึงยังไม่ได้ทดสอบพฤติกรรมของ `MediaPipePoseSource`, compositor, shader และ FPS จริง

---

## 5. เทคนิค Occlusion Shader (รายละเอียด)

ปัญหามี 3 แบบ ใช้คนละเทคนิค:

1. **ร่างกายจริงอยู่หน้าชุด** (มือไขว้หน้าอก, คางทับคอเสื้อ)
   → *Depth-only occluder*: แคปซูลมองไม่เห็นตามข้อต่อ วาดก่อนชุด (`Queue = Geometry-10`, `ColorMask 0`, `ZWrite On`) พิกเซลชุดข้างหลังไม่ผ่าน depth test → alpha ใน RT = 0 → เห็นภาพกล้องจริง
   รัศมีแคปซูล < รัศมีแขนเสื้อ เพื่อให้แขนเสื้อที่หุ้มแขนยังแสดงผล

2. **เสื้อจริงโผล่ออกนอกขอบชุด AR** (เสื้อจริงหลวมกว่า/ยาวกว่า)
   → *Clothes mask × person mask × (1 − garment alpha)* = พื้นที่ต้องลบ แล้วเติมด้วย
   - background plate (ถ้ากล้องนิ่งและเคยจับฉากว่างได้) ดีที่สุด หรือ
   - สีของพิกเซลชุดที่ใกล้ที่สุด (ค้นหา 8 ทิศ × 3 วง ภายใน `_FillRadius`) — “ขยายชุดออกไปทับ” ราคาถูก ไม่ต้องมี plate
   ใช้ `smoothstep` ที่ขอบ mask เพื่อไม่ให้เห็นเส้นขอบแข็ง

3. **ขอบชุดดูเหมือนแปะ**
   → MSAA 4x บน RT ของชุด แล้ว composite แบบ premultiplied (`rgb + base·(1−a)`), แสงในฉากให้ใกล้แสงจริง (ใช้ค่าเฉลี่ยสีของภาพกล้องตั้งค่า ambient), เพิ่ม contact shadow บางๆ ที่คอ/ขอบแขน

clothes mask ไม่ได้มากับ PoseLandmarker ต้องรัน **ImageSegmenter + `selfie_multiclass_256x256`** เพิ่ม (class 4 = clothes) แล้วใส่ช่อง R ของ `PoseFrame.ClassMask` ถ้าไม่ได้รัน shader จะข้ามขั้นนี้ไปเอง (ยังมีข้อ 1 และ 3)

---

## 6. ข้อกำหนด asset เสื้อ 3D

- หน่วยเมตร, หันหน้า +Z ของ root, ท่า A-pose (แขนกาง ~25°)
- กระดูก: `Hips, Spine, Chest, L/R Shoulder(clavicle), L/R UpperArm, L/R LowerArm, L/R Hand`
- Blend shape `Wide` (ลำตัวกว้าง/หนาขึ้น)
- แยก cloth: ลำตัวช่วงบน = skinning, ชายเสื้อ/ปลายแขน = cloth (pin vertex ที่ต่อกับลำตัว)
- material: double-sided (เห็นด้านในคอเสื้อ), normal + wrinkle map
- ทำ/ปรับ asset ได้จาก Marvelous Designer / CLO3D → Blender (rig, blend shape) → FBX

---

## 7. ขั้นตอนติดตั้ง (Windows)

1. ติดตั้ง [Unity Hub](https://unity.com/download) แล้วล็อกอิน จากนั้นใช้ Hub ติดตั้ง **Unity 2022.3 LTS**
2. สร้างโปรเจกต์ใหม่ด้วยเทมเพลต **3D (Built-in Render Pipeline)** (ใช้ URP ก็ได้ เพราะเมนูจะเลือก shader ให้เอง)
3. โหลด `com.github.homuler.mediapipe-0.16.3.tgz` จาก [MediaPipeUnityPlugin v0.16.3](https://github.com/homuler/MediaPipeUnityPlugin/releases/tag/v0.16.3) แล้วเปิด Window → Package Manager → `+` → *Add package from tarball…* และเลือกไฟล์นั้น
4. คัดลอกโฟลเดอร์ `tryon/unity/Assets/ARTryOn` ไปไว้ใน `Assets/` ของโปรเจกต์
5. เมนู **AR Try-On → Download Models**: โหลดโมเดล pose และ segmenter ลง `Assets/StreamingAssets`
6. เมนู **AR Try-On → Create Scene**: สร้าง material, กล้อง, แสง และ object ที่ต่อสคริปต์ไว้ครบ แล้วบันทึกเป็น `Assets/ARTryOn/TryOn.unity`
7. กด **Play** แล้วยืนตรงหน้ากล้อง ให้เห็นตั้งแต่หัวถึงสะโพก ปล่อยแขนลงข้างตัว ค้างไว้ประมาณ 1.5 วินาที เสื้อจะขึ้นเอง
   - ปุ่มบนจอ: เปลี่ยนสี, แขนสั้น/ยาว (S), วัดตัวใหม่ (R), Debug (D) ซึ่งแสดง FPS และเวลา inference
   - ถ้าเสื้อใหญ่หรือเล็กไปสำหรับทุกคน ให้ปรับ `Horizontal Fov Deg` ใน `MediaPipePoseSource`
   - ถ้า FPS ต่ำ ให้ลด `Requested Width/Height` เป็น 960×540 หรือปิด `Use Clothes Mask`
8. ส่งให้เครื่องอื่นใช้: File → Build Settings → Windows → Build แล้วส่งทั้งโฟลเดอร์ที่ได้ (exe + `_Data`)

ไม่ต้องมีโมเดล 3D: เสื้อเป็น `ParametricShirt` ที่สร้างจากโค้ดตามขนาดตัวที่วัดได้ ถ้ามีเสื้อ rigged ภายหลัง ให้ใส่ prefab ที่มี `GarmentFitter` ในช่อง `Garment Prefab` ของ `TryOnManager`

---

## 8. Roadmap ที่แนะนำ

| เฟส | เป้าหมาย | เกณฑ์ผ่าน |
|---|---|---|
| 0 (1 สัปดาห์) | รันโค้ดชุดนี้ใน Unity, 1 คน, เสื้อ skinning อย่างเดียว | ชุดเกาะตัว, ไม่สั่น, 30 FPS |
| 1 | calibration UI (แสดง `PoseIssue`), ease tuning, occluder | คนทั่วไปผ่าน calibrate ใน < 5 วิ |
| 2 | cloth sim ชายเสื้อ/แขน + clothes mask + background plate | ไม่เห็นเสื้อจริงในท่าปกติ |
| 3 | 2–3 คน, การบังกันระหว่างคน (เทียบ depth ของแต่ละคน + person mask) | 3 คนยืนเรียงกันได้ 25+ FPS |
| 4 | asset pipeline หลายชุด, การเปลี่ยนชุด, ถ่ายรูป/แชร์ | |
