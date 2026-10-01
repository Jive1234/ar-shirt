import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { MindARThree } from "mindar-image-three";
import { CONFIG } from "./config.js";

const startScreen = document.getElementById("start-screen");
const startButton = document.getElementById("start-button");
const errorText = document.getElementById("error");
const hint = document.getElementById("hint");

// ป้ายข้อความทำจาก canvas / Text label drawn on a canvas
function makeLabel(text) {
  const canvas = document.createElement("canvas");
  canvas.width = 512;
  canvas.height = 128;
  const ctx = canvas.getContext("2d");
  ctx.fillStyle = "rgba(0,0,0,0.55)";
  ctx.beginPath();
  ctx.roundRect(0, 0, 512, 128, 40);
  ctx.fill();
  ctx.fillStyle = "#fff";
  ctx.font = "bold 56px system-ui, sans-serif";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillText(text, 256, 64);
  const texture = new THREE.CanvasTexture(canvas);
  const mesh = new THREE.Mesh(
    new THREE.PlaneGeometry(1, 0.25),
    new THREE.MeshBasicMaterial({ map: texture, transparent: true })
  );
  return mesh;
}

// รูปทรงเริ่มต้นเมื่อไม่มีไฟล์ .glb / Default shape when no .glb is set
function makeDefaultModel() {
  return new THREE.Mesh(
    new THREE.TorusKnotGeometry(0.2, 0.06, 128, 16),
    new THREE.MeshStandardMaterial({ color: 0xff5c8a, metalness: 0.3, roughness: 0.3 })
  );
}

async function loadModel() {
  if (!CONFIG.modelSrc) return makeDefaultModel();
  const gltf = await new GLTFLoader().loadAsync(CONFIG.modelSrc);
  gltf.scene.scale.setScalar(CONFIG.modelScale);
  return gltf.scene;
}

async function start() {
  startButton.disabled = true;
  errorText.hidden = true;

  const mindarThree = new MindARThree({
    container: document.getElementById("ar-container"),
    imageTargetSrc: CONFIG.imageTargetSrc,
    uiScanning: "no",
    uiLoading: "no",
  });
  const { renderer, scene, camera } = mindarThree;

  scene.add(new THREE.HemisphereLight(0xffffff, 0x444444, 2));

  // anchor 0 = รูปแรกในไฟล์ .mind / the first image in the .mind file
  const anchor = mindarThree.addAnchor(0);

  const model = await loadModel();
  model.position.z = 0.25;
  anchor.group.add(model);

  const label = makeLabel(CONFIG.label);
  label.position.set(0, 0.65, 0.1);
  anchor.group.add(label);

  anchor.onTargetFound = () => { hint.hidden = true; };
  anchor.onTargetLost = () => { hint.hidden = false; };

  await mindarThree.start();
  startScreen.hidden = true;
  startScreen.style.display = "none";
  hint.hidden = false;

  const clock = new THREE.Clock();
  renderer.setAnimationLoop(() => {
    const t = clock.getElapsedTime();
    model.rotation.y = t;
    model.position.z = 0.25 + Math.sin(t * 2) * 0.05;
    renderer.render(scene, camera);
  });
}

startButton.addEventListener("click", () => {
  start().catch((err) => {
    console.error(err);
    startButton.disabled = false;
    errorText.hidden = false;
    errorText.textContent =
      "เปิดกล้องไม่ได้ ตรวจสอบว่าเปิดผ่าน https และอนุญาตกล้องแล้ว / Could not start the camera. Use https and allow camera access.";
  });
});
