import * as THREE from 'three';
import { CONFIG } from './config.js';
import { PoseSource } from './poseSource.js';
import { PersonTracker } from './tracker.js';
import { CalibrationState, PoseIssue, measureOnce } from './calibration.js';
import { cameraSpaceJoints } from './body.js';
import { verticalFov } from './camera.js';
import { Lm } from './landmarks.js';
import { ParametricShirt } from './garment.js';
import { BodyOccluder } from './occluder.js';
import { Compositor } from './compositor.js';

// URL options (handy for demos and testing):
//   ?src=clip.webm   use a video file instead of the webcam      ?mirror=0|1  override mirroring
//   ?skipCalib=1     fit immediately without the standing pose   ?debug=1     show masks and timings
//   ?clothes=0       disable the clothes mask (faster)           ?people=N    max people (1–3)
const params = new URLSearchParams(location.search);
const opt = {
  src: params.get('src'),
  skipCalib: params.get('skipCalib') === '1',
  debug: params.get('debug') === '1',
  useClothesMask: params.get('clothes') !== '0' && CONFIG.useClothesMask,
  maxPeople: Math.min(3, Math.max(1, Number(params.get('people') ?? CONFIG.maxPeople))),
};
opt.mirrored = params.has('mirror') ? params.get('mirror') === '1' : !opt.src;

const COLORS = ['#1f6feb', '#e11d48', '#f5f5f4', '#111827', '#16a34a', '#f59e0b'];
const HINTS = {
  [PoseIssue.NotVisible]: 'ถอยห่างกล้องให้เห็นตั้งแต่หัวถึงสะโพกและมือทั้งสองข้าง',
  [PoseIssue.NotFacingCamera]: 'หันหน้าตรงเข้ากล้อง',
  [PoseIssue.ShouldersTilted]: 'ยืนให้ไหล่ทั้งสองข้างระดับเดียวกัน',
  [PoseIssue.Leaning]: 'ยืนตัวตรง',
  [PoseIssue.ArmsNotRelaxed]: 'ปล่อยแขนลงข้างตัว กางออกเล็กน้อย',
  [PoseIssue.Moving]: 'ยืนนิ่งๆ สักครู่',
  [PoseIssue.None]: 'ดีมาก ค้างไว้…',
};

const $ = (id) => document.getElementById(id);
const state = {
  color: CONFIG.shirtColor,
  sleeve: CONFIG.sleeve,
  debug: opt.debug,
  wearers: new Map(), // track id → { shirt, occluder, measurements }
};

$('start-button').addEventListener('click', start);
if (opt.src) start(); // video files need no permission prompt

async function start() {
  $('start-button').disabled = true;
  $('error').hidden = true;
  try {
    const video = await openVideo();
    const source = new PoseSource({
      wasmBase: `https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@${CONFIG.tasksVisionVersion}/wasm`,
      poseModel: CONFIG.poseModel,
      segmenterModel: CONFIG.segmenterModel,
      useClothesMask: opt.useClothesMask,
      segmenterEveryNFrames: CONFIG.segmenterEveryNFrames,
      maxPeople: opt.maxPeople,
      mirrored: opt.mirrored,
    });
    const loading = $('loading');
    loading.hidden = false;
    await source.init((step) => {
      loading.textContent = { wasm: 'กำลังโหลด MediaPipe…', pose: 'กำลังโหลดโมเดลท่าทาง…', segmenter: 'กำลังโหลดโมเดลแยกเสื้อผ้า…' }[step];
    });
    $('start').hidden = true;
    $('hud').hidden = false;
    run(video, source);
  } catch (err) {
    console.error(err);
    $('error').textContent = explainError(err);
    $('error').hidden = false;
    $('loading').hidden = true;
    $('start-button').disabled = false;
  }
}

async function openVideo() {
  const video = $('video');
  if (opt.src) {
    video.src = opt.src;
    video.loop = true;
    video.crossOrigin = 'anonymous';
  } else {
    video.srcObject = await navigator.mediaDevices.getUserMedia({
      audio: false,
      video: { width: { ideal: CONFIG.cameraWidth }, height: { ideal: CONFIG.cameraHeight }, facingMode: 'user' },
    });
  }
  await video.play();
  if (!video.videoWidth) await new Promise((r) => video.addEventListener('loadedmetadata', r, { once: true }));
  return video;
}

function explainError(err) {
  if (err?.name === 'NotAllowedError') return 'ไม่ได้รับอนุญาตให้ใช้กล้อง กรุณาอนุญาตในเบราว์เซอร์แล้วลองใหม่';
  if (err?.name === 'NotFoundError') return 'ไม่พบกล้องในเครื่องนี้';
  if (!window.isSecureContext) return 'ต้องเปิดผ่าน https (หรือ localhost) จึงจะใช้กล้องได้';
  return `เริ่มระบบไม่สำเร็จ: ${err?.message ?? err}`;
}

function run(video, source) {
  const canvas = $('view');
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: false, alpha: false, preserveDrawingBuffer: false });
  renderer.setPixelRatio(1);
  renderer.outputColorSpace = THREE.SRGBColorSpace;

  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(45, 16 / 9, 0.05, 20);
  scene.add(new THREE.HemisphereLight(0xffffff, 0x3a3f4a, 2.2));
  const key = new THREE.DirectionalLight(0xffffff, 1.6);
  key.position.set(0.4, 1.2, 1.0); // from above the camera, like room lighting
  scene.add(key);

  const compositor = new Compositor(renderer, video, opt.mirrored);
  compositor.setDebug(state.debug);

  const tracker = new PersonTracker();
  tracker.onTrackCreated = (track) => {
    track.calibration.onCalibrated = (m) => addWearer(track.id, m);
  };
  tracker.onTrackLost = (track) => removeWearer(track.id);

  function addWearer(id, measurements) {
    removeWearer(id);
    const shirt = new ParametricShirt({ color: state.color, sleeve: state.sleeve, printText: CONFIG.printText });
    const occluder = new BodyOccluder();
    scene.add(occluder.group, shirt.group);
    state.wearers.set(id, { shirt, occluder, measurements });
  }

  function removeWearer(id) {
    const w = state.wearers.get(id);
    if (!w) return;
    scene.remove(w.shirt.group, w.occluder.group);
    w.shirt.dispose();
    w.occluder.dispose();
    state.wearers.delete(id);
  }

  setupControls({
    onColor: (c) => { state.color = c; for (const w of state.wearers.values()) w.shirt.setColor(c, CONFIG.printText); },
    onSleeve: (s) => { state.sleeve = s; for (const w of state.wearers.values()) w.shirt.setSleeve(s); },
    onRecalibrate: () => {
      for (const t of tracker.tracks) { t.calibration.recalibrate(); removeWearer(t.id); }
    },
    onDebug: (on) => { state.debug = on; compositor.setDebug(on); $('stats').hidden = !on; },
  });
  $('stats').hidden = !state.debug;

  let emptySince = null;
  let lastT = null;
  let fps = 0, lastFrameWall = performance.now();

  const onFrame = (now) => {
    const w = video.videoWidth, h = video.videoHeight;
    if (w && h) {
      if (canvas.width !== w || canvas.height !== h) {
        renderer.setSize(w, h, false);
        compositor.resize(w, h);
        camera.aspect = w / h;
        camera.fov = verticalFov(CONFIG.horizontalFovDeg, camera.aspect);
        camera.updateProjectionMatrix();
      }

      const frame = source.detect(video, now);
      const t = frame.timestamp;
      const dt = lastT === null ? 1 / 30 : t - lastT;
      lastT = t;
      tracker.update(frame.people, t);

      // Clean background plate: refreshed every second while nobody is in view (fixed camera only).
      if (CONFIG.useBackgroundPlate && frame.people.length === 0) {
        emptySince ??= t;
        if (t - emptySince > 1) { compositor.capturePlate(); emptySince = t; }
      } else emptySince = null;

      const zones = [];
      const cam = { fov: camera.fov, aspect: camera.aspect, widthPx: w, heightPx: h };
      for (const track of tracker.tracks) {
        if (opt.skipCalib && track.calibration.state !== CalibrationState.Calibrated && track.smoother.image[0].initialized) {
          track.calibration.force(measureOnce(track.pose));
        }
        const wearer = state.wearers.get(track.id);
        if (!wearer) continue;
        if (!track.seenThisFrame) continue; // briefly lost: hold the last pose rather than vanish
        const fit = cameraSpaceJoints(track.pose, cam);
        if (!fit) continue;
        wearer.shirt.update(fit.joints, track.pose, wearer.measurements, CONFIG.ease, dt);
        wearer.occluder.update(fit.joints, track.pose);
        const box = wearer.shirt.screenBounds(camera);
        // Below the hip line, real clothing beside the shirt is trousers: leave it alone.
        const hipV = Math.min(track.pose.image[Lm.LeftHip][1], track.pose.image[Lm.RightHip][1]);
        if (box) zones.push([box[0], Math.max(box[1], hipV), box[2], box[3]]);
      }

      compositor.render(scene, camera, frame, zones);
      updateHud(tracker, state);

      const wall = performance.now();
      fps = fps * 0.9 + (1000 / Math.max(1, wall - lastFrameWall)) * 0.1;
      lastFrameWall = wall;
      if (state.debug) {
        $('stats').textContent =
          `fps ${fps.toFixed(1)}\ninference ${source.lastInferenceMs.toFixed(1)} ms (${source.delegate})\n` +
          `people ${frame.people.length}  shirts ${state.wearers.size}\nvideo ${w}×${h}  plate ${compositor.hasPlate ? 'yes' : 'no'}`;
      }
      window.__tryon = { people: frame.people.length, shirts: state.wearers.size, fps, delegate: source.delegate,
        states: tracker.tracks.map((k) => k.calibration.state) };
    }
    schedule();
  };

  // Run once per new video frame when the browser supports it, so inference and display stay in step.
  const schedule = () => ('requestVideoFrameCallback' in video
    ? video.requestVideoFrameCallback((now) => onFrame(now))
    : requestAnimationFrame((now) => onFrame(now)));
  schedule();
}

function setupControls({ onColor, onSleeve, onRecalibrate, onDebug }) {
  const sw = $('swatches');
  for (const c of COLORS) {
    const b = document.createElement('button');
    b.className = 'swatch';
    b.style.background = c;
    b.title = c;
    b.setAttribute('aria-pressed', String(c === state.color));
    b.addEventListener('click', () => {
      for (const x of sw.children) x.setAttribute('aria-pressed', String(x === b));
      onColor(c);
    });
    sw.appendChild(b);
  }
  const sleeve = $('sleeve');
  const label = () => (sleeve.textContent = state.sleeve === 'short' ? 'แขนสั้น' : 'แขนยาว');
  label();
  sleeve.addEventListener('click', () => { onSleeve(state.sleeve === 'short' ? 'long' : 'short'); label(); });
  $('recalibrate').addEventListener('click', onRecalibrate);
  $('debug').addEventListener('click', () => onDebug(!state.debug));
}

function updateHud(tracker, st) {
  const people = $('people');
  const tracks = tracker.tracks;
  while (people.children.length > tracks.length) people.lastChild.remove();
  while (people.children.length < tracks.length) {
    const chip = document.createElement('div');
    chip.className = 'chip';
    chip.innerHTML = '<span></span><div class="bar"><i></i></div>';
    people.appendChild(chip);
  }
  let hint = '';
  tracks.forEach((t, i) => {
    const chip = people.children[i];
    const c = t.calibration;
    const done = c.state === CalibrationState.Calibrated && st.wearers.has(t.id);
    chip.classList.toggle('ok', done);
    chip.querySelector('span').textContent = done
      ? `คนที่ ${i + 1}: ใส่เสื้อแล้ว`
      : c.state === CalibrationState.Collecting
        ? `คนที่ ${i + 1}: กำลังวัดตัว ${Math.round(c.progress * 100)}%`
        : `คนที่ ${i + 1}: รอท่ายืน`;
    chip.querySelector('i').style.width = `${Math.round((done ? 1 : c.progress) * 100)}%`;
    if (!done && !hint) hint = HINTS[c.issue] ?? '';
  });
  if (tracks.length === 0) hint = 'ยืนหน้ากล้องให้เห็นตั้งแต่หัวถึงสะโพก';
  $('hint').textContent = hint;
}
