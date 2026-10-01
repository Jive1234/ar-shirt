import * as THREE from 'three';
import { Lm } from './landmarks.js';
import { add, sub, scale, mid, norm, cross, dot, lerp, len } from './vec.js';

// Mesh resolution. The shirt is rebuilt from the joints every frame, so keep it modest.
const SEG = 40;           // vertices around the torso
const BODY_ROWS = 18;     // hem → shoulder line
const YOKE_ROWS = 4;      // shoulder line → neckline
const HEM_SIM_ROWS = 5;   // lowest torso rows that swing as cloth
const SLEEVE_SEG = 20;
const SLEEVE_ROWS = 12;
const SLEEVE_SIM_ROWS = 3; // rows at the sleeve opening that swing

/**
 * A parametric T-shirt that needs no external 3D asset: every frame its surface is lofted around the body
 * from the camera-space joints, sized by the person's calibrated measurements (adaptive sizing), and the hem
 * and sleeve openings are simulated as Verlet cloth so they swing and lag naturally.
 *
 * Same idea as rigging (joints drive the surface), but the "skin weights" are analytic: each ring of the torso
 * follows an interpolated hip→shoulder frame, each sleeve ring follows the arm.
 */
export class ParametricShirt {
  constructor({ color = '#1f6feb', sleeve = 'short', printText = '' } = {}) {
    this.sleeve = sleeve;
    this.group = new THREE.Group();
    // Front faces only: looking into the neck hole or under the hem shows the real body, not the inside back.
    const fabric = (map) => new THREE.MeshStandardMaterial({ map, roughness: 0.92, metalness: 0, side: THREE.FrontSide });
    this.texture = makeFabricTexture(color, printText);
    this.sleeveTexture = makeFabricTexture(color, '');
    this.material = fabric(this.texture);
    this.sleeveMaterial = fabric(this.sleeveTexture);

    this.torso = new Grid(BODY_ROWS + YOKE_ROWS, SEG, this.material, HEM_SIM_ROWS, 'bottom');
    this.sleeves = [
      new Grid(SLEEVE_ROWS, SLEEVE_SEG, this.sleeveMaterial, SLEEVE_SIM_ROWS, 'top'),
      new Grid(SLEEVE_ROWS, SLEEVE_SEG, this.sleeveMaterial, SLEEVE_SIM_ROWS, 'top'),
    ];
    this.group.add(this.torso.mesh, ...this.sleeves.map((s) => s.mesh));
    this.visible = false;
  }

  setColor(color, printText) {
    this.texture.dispose();
    this.sleeveTexture.dispose();
    this.texture = makeFabricTexture(color, printText);
    this.sleeveTexture = makeFabricTexture(color, '');
    this.material.map = this.texture;
    this.sleeveMaterial.map = this.sleeveTexture;
  }

  setSleeve(sleeve) {
    this.sleeve = sleeve;
    for (const s of this.sleeves) s.reset();
  }

  /**
   * @param joints       camera-space joints (meters) from body.js
   * @param pose         smoothed observation (for visibility)
   * @param m            calibrated measurements { shoulder, hip, torso, upperArm, forearm }
   * @param ease         { width, length }
   * @param dt           seconds since the last update
   */
  update(joints, pose, m, ease, dt) {
    const J = joints;
    const ls = J[Lm.LeftShoulder], rs = J[Lm.RightShoulder], lh = J[Lm.LeftHip], rh = J[Lm.RightHip];
    const midS = mid(ls, rs), midH = mid(lh, rh);
    const up = norm(sub(midS, midH));
    const acrossS = orthoAcross(sub(ls, rs), up);
    const acrossH = orthoAcross(sub(lh, rh), up);

    // Calibrated sizes (stable) rather than per-frame distances (noisy).
    const chestHalf = m.shoulder * 0.5 * 0.98 * ease.width;
    const hipHalf = Math.max(m.hip * 0.68, m.shoulder * 0.42) * ease.width;
    const waistHalf = (chestHalf + hipHalf) * 0.5 * 0.96;
    const torsoLen = len(sub(midS, midH));
    const hemDrop = 0.2 * m.torso * ease.length; // shirt hangs below the hip joints

    // ---- torso rings -------------------------------------------------------------------------------------
    const T = this.torso;
    const ringFrames = [];
    const tHem = -hemDrop / Math.max(torsoLen, 1e-3);
    for (let r = 0; r < BODY_ROWS; r++) {
      const t = tHem + ((1 - tHem) * r) / (BODY_ROWS - 1);
      const tc = Math.max(0, t);
      const center = add(midH, scale(up, t * torsoLen));
      const across = orthoAcross(lerp(acrossH, acrossS, smooth(tc)), up);
      const forward = cross(across, up); // toward the camera for a person facing it
      let a;
      if (t <= 0) a = hipHalf * (1 + 0.04 * (-t / Math.max(-tHem, 1e-3))); // slight flare at the hem
      else if (t < 0.4) a = lerpN(hipHalf, waistHalf, smooth(t / 0.4));
      else a = lerpN(waistHalf, chestHalf, smooth((t - 0.4) / 0.6));
      const b = a * lerpN(0.72, 0.62, tc);
      ringFrames.push({ center, across, forward, a, b });
      writeRing(T.target, r, SEG, center, across, forward, a, b);
    }
    // Yoke: shoulders slope in to a round neckline.
    const top = ringFrames[BODY_ROWS - 1];
    const neckA = 0.075 * (m.shoulder / 0.38), neckB = 0.065 * (m.shoulder / 0.38);
    for (let k = 1; k <= YOKE_ROWS; k++) {
      const s = k / YOKE_ROWS;
      const center = add(top.center, add(scale(up, 0.045 * Math.sin((s * Math.PI) / 2)), scale(top.forward, -0.01 * s)));
      writeRing(T.target, BODY_ROWS - 1 + k, SEG, center, top.across, top.forward,
        lerpN(top.a, neckA, s), lerpN(top.b, neckB, s));
    }
    T.step(dt, (row, p) => collideEllipse(p, ringFrames[Math.min(row, BODY_ROWS - 1)], 0.93));

    // ---- sleeves -----------------------------------------------------------------------------------------
    const long = this.sleeve === 'long';
    const rootR = Math.max(0.062, m.shoulder * 0.19) * ease.width;
    const endR = long ? 0.045 * ease.width : rootR * 0.88;
    const sides = [
      { s: ls, e: J[Lm.LeftElbow], w: J[Lm.LeftWrist], side: 1, grid: this.sleeves[0] },
      { s: rs, e: J[Lm.RightElbow], w: J[Lm.RightWrist], side: -1, grid: this.sleeves[1] },
    ];
    const forwardS = cross(acrossS, up);
    for (const { s, e, w, side, grid } of sides) {
      // Root sits slightly inside the torso so the seam is hidden by overlap.
      const root = add(s, add(scale(acrossS, -side * 0.02), scale(up, -0.015)));
      const upper = norm(sub(e, s));
      const lower = norm(sub(w, e));
      const path = long
        ? [root, add(root, scale(upper, m.upperArm)), add(add(root, scale(upper, m.upperArm)), scale(lower, m.forearm * 0.92))]
        : [root, add(root, scale(upper, m.upperArm * 0.5 * ease.length))];
      const frames = [];
      for (let r = 0; r < SLEEVE_ROWS; r++) {
        const u = r / (SLEEVE_ROWS - 1);
        const { point, dir } = alongPath(path, u);
        let n1 = cross(dir, forwardS);
        if (len(n1) < 1e-4) n1 = cross(dir, up);
        n1 = norm(n1);
        const n2 = cross(n1, dir);
        const radius = lerpN(rootR, endR, u);
        frames.push({ center: point, across: n1, forward: n2, a: radius, b: radius });
        writeRing(grid.target, r, SLEEVE_SEG, point, n1, n2, radius, radius * 0.92);
      }
      grid.step(dt, (row, p) => collideEllipse(p, frames[row], 0.8));
    }

    this.visible = true;
    this.group.visible = true;
  }

  /**
   * Screen-space bounding box of the shirt in display UV (0..1, origin bottom-left), used by the compositor to
   * limit real-clothing removal to the upper body (so trousers below the hem are left alone).
   */
  screenBounds(camera) {
    const t = Math.tan((camera.fov * Math.PI) / 360);
    let x0 = 1, y0 = 1, x1 = 0, y1 = 0;
    for (const g of [this.torso, ...this.sleeves]) {
      const P = g.pos;
      for (let i = 0; i < P.length; i += 3) {
        const z = -P[i + 2];
        if (z <= 1e-3) continue;
        const u = (P[i] / (z * t * camera.aspect) + 1) / 2, v = (P[i + 1] / (z * t) + 1) / 2;
        if (u < x0) x0 = u; if (u > x1) x1 = u; if (v < y0) y0 = v; if (v > y1) y1 = v;
      }
    }
    return x1 > x0 ? [x0, y0, x1, y1] : null;
  }

  hide() {
    this.group.visible = false;
  }

  dispose() {
    this.torso.dispose();
    for (const s of this.sleeves) s.dispose();
    this.material.dispose();
    this.sleeveMaterial.dispose();
    this.texture.dispose();
    this.sleeveTexture.dispose();
  }
}

// ---------------------------------------------------------------------------------------------------------
// A ring-structured surface (rows × (segs+1) vertices, the last column duplicates the first for the UV seam)
// with Verlet cloth on the rows at one end.

const GRAVITY = [0, -9.8, 0];

class Grid {
  constructor(rows, segs, material, simRows, simEnd) {
    this.rows = rows;
    this.cols = segs + 1;
    this.simRows = simRows;
    this.simEnd = simEnd; // 'bottom' → rows 0..simRows-1 swing; 'top' → the last simRows rows swing
    const n = rows * this.cols;
    this.target = new Float32Array(n * 3);
    this.pos = new Float32Array(n * 3);
    this.prev = new Float32Array(n * 3);
    this.initialized = false;

    const geo = new THREE.BufferGeometry();
    this.positionAttr = new THREE.BufferAttribute(this.pos, 3);
    this.positionAttr.setUsage(THREE.DynamicDrawUsage);
    geo.setAttribute('position', this.positionAttr);
    const uv = new Float32Array(n * 2);
    for (let r = 0; r < rows; r++)
      for (let c = 0; c < this.cols; c++) {
        uv[(r * this.cols + c) * 2] = c / segs;
        uv[(r * this.cols + c) * 2 + 1] = r / (rows - 1);
      }
    geo.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
    const idx = [];
    for (let r = 0; r < rows - 1; r++)
      for (let c = 0; c < segs; c++) {
        const a = r * this.cols + c, b = a + 1, d = a + this.cols, e = d + 1;
        // Rows advance along the body/arm and θ advances around it (writeRing), so this order faces outward.
        idx.push(a, d, b, b, d, e);
      }
    geo.setIndex(idx);
    this.geometry = geo;
    this.mesh = new THREE.Mesh(geo, material);
    this.mesh.frustumCulled = false; // bounds change every frame
  }

  isSim(row) {
    return this.simEnd === 'bottom' ? row < this.simRows : row >= this.rows - this.simRows;
  }

  reset() {
    this.initialized = false;
  }

  /** Advance one frame: pinned rows snap to the target, cloth rows integrate + relax toward it. */
  step(dt, collide) {
    const { target: K, pos: P, prev: Q, rows, cols } = this;
    dt = Math.min(Math.max(dt, 1 / 120), 1 / 20);

    // Teleport guard: on first frame or a tracking jump, start the cloth at rest.
    if (!this.initialized || maxDelta(K, P) > 0.35) {
      P.set(K);
      Q.set(K);
      this.initialized = true;
    }

    const damping = 0.96;
    const pull = 1 - Math.pow(1 - 0.12, dt * 60); // spring back toward the designed shape (frame-rate independent)
    const g = GRAVITY.map((v) => v * dt * dt * 0.35); // softened gravity: real fabric is supported by the body
    for (let r = 0; r < rows; r++) {
      const sim = this.isSim(r);
      for (let c = 0; c < cols; c++) {
        const i = (r * cols + c) * 3;
        if (!sim) {
          Q[i] = P[i]; Q[i + 1] = P[i + 1]; Q[i + 2] = P[i + 2];
          P[i] = K[i]; P[i + 1] = K[i + 1]; P[i + 2] = K[i + 2];
          continue;
        }
        for (let k = 0; k < 3; k++) {
          const v = (P[i + k] - Q[i + k]) * damping;
          Q[i + k] = P[i + k];
          let x = P[i + k] + v + g[k];
          x += (K[i + k] - x) * pull;
          P[i + k] = x;
        }
      }
    }

    // Distance constraints (rest lengths taken from the current target, so sizing changes propagate).
    for (let it = 0; it < 3; it++) {
      for (let r = 0; r < rows; r++) {
        if (!this.isSim(r)) continue;
        const rOther = this.simEnd === 'bottom' ? r + 1 : r - 1; // neighbour toward the pinned side
        for (let c = 0; c < cols - 1; c++) {
          const i = r * cols + c;
          relax(P, K, i, rOther * cols + c, !this.isSim(rOther));
          relax(P, K, i, i + 1, false);
        }
        // Close the seam: last column mirrors the first.
        const f = r * cols * 3, l = (r * cols + cols - 1) * 3;
        P[l] = P[f]; P[l + 1] = P[f + 1]; P[l + 2] = P[f + 2];
      }
      if (collide) {
        for (let r = 0; r < rows; r++) {
          if (!this.isSim(r)) continue;
          for (let c = 0; c < cols; c++) {
            const i = (r * cols + c) * 3;
            const p = [P[i], P[i + 1], P[i + 2]];
            const q = collide(r, p);
            if (q) { P[i] = q[0]; P[i + 1] = q[1]; P[i + 2] = q[2]; }
          }
        }
      }
    }

    this.positionAttr.needsUpdate = true;
    this.geometry.computeVertexNormals();
  }

  dispose() {
    this.geometry.dispose();
  }
}

function relax(P, K, i, j, jPinned) {
  const ia = i * 3, ja = j * 3;
  const rest = Math.hypot(K[ja] - K[ia], K[ja + 1] - K[ia + 1], K[ja + 2] - K[ia + 2]);
  const dx = P[ja] - P[ia], dy = P[ja + 1] - P[ia + 1], dz = P[ja + 2] - P[ia + 2];
  const d = Math.hypot(dx, dy, dz);
  if (d < 1e-6) return;
  const diff = (d - rest) / d;
  const wi = jPinned ? 1 : 0.5, wj = jPinned ? 0 : 0.5;
  P[ia] += dx * diff * wi; P[ia + 1] += dy * diff * wi; P[ia + 2] += dz * diff * wi;
  P[ja] -= dx * diff * wj; P[ja + 1] -= dy * diff * wj; P[ja + 2] -= dz * diff * wj;
}

function maxDelta(A, B) {
  let m = 0;
  for (let i = 0; i < A.length; i += 3) {
    const d = Math.abs(A[i] - B[i]) + Math.abs(A[i + 1] - B[i + 1]) + Math.abs(A[i + 2] - B[i + 2]);
    if (d > m) m = d;
  }
  return m;
}

/** Keep a point outside the body's elliptical cross-section (scaled by `inner`). */
function collideEllipse(p, f, inner) {
  const rel = sub(p, f.center);
  const x = dot(rel, f.across), z = dot(rel, f.forward);
  const e = Math.sqrt((x / (f.a * inner)) ** 2 + (z / (f.b * inner)) ** 2);
  if (e >= 1 || e < 1e-6) return null;
  const k = 1 / e;
  return add(p, add(scale(f.across, x * (k - 1)), scale(f.forward, z * (k - 1))));
}

/** Write one ring of vertices. θ = 0 is on the `across` side, θ = π/2 faces `forward`. */
function writeRing(out, row, segs, center, across, forward, a, b) {
  const cols = segs + 1;
  for (let c = 0; c < cols; c++) {
    const th = (2 * Math.PI * c) / segs;
    const ca = Math.cos(th) * a, sb = Math.sin(th) * b;
    const i = (row * cols + c) * 3;
    out[i] = center[0] + across[0] * ca + forward[0] * sb;
    out[i + 1] = center[1] + across[1] * ca + forward[1] * sb;
    out[i + 2] = center[2] + across[2] * ca + forward[2] * sb;
  }
}

function alongPath(path, u) {
  const segLens = [];
  let total = 0;
  for (let i = 0; i < path.length - 1; i++) { const l = len(sub(path[i + 1], path[i])); segLens.push(l); total += l; }
  let d = u * total;
  for (let i = 0; i < segLens.length; i++) {
    if (d <= segLens[i] || i === segLens.length - 1) {
      const t = segLens[i] > 0 ? Math.min(1, d / segLens[i]) : 0;
      // Blend directions near the elbow so the long sleeve bends smoothly.
      let dir = norm(sub(path[i + 1], path[i]));
      if (i + 1 < segLens.length && t > 0.8) dir = norm(lerp(dir, norm(sub(path[i + 2], path[i + 1])), (t - 0.8) / 0.4));
      if (i > 0 && t < 0.2) dir = norm(lerp(norm(sub(path[i], path[i - 1])), dir, 0.5 + t / 0.4));
      return { point: lerp(path[i], path[i + 1], t), dir };
    }
    d -= segLens[i];
  }
  return { point: path[path.length - 1], dir: [0, -1, 0] };
}

function orthoAcross(across, up) {
  const a = sub(across, scale(up, dot(across, up)));
  return len(a) > 1e-6 ? norm(a) : [1, 0, 0];
}

const smooth = (t) => { const x = Math.min(1, Math.max(0, t)); return x * x * (3 - 2 * x); };
const lerpN = (a, b, t) => a + (b - a) * t;

/** Knit-like fabric with an optional chest print. u = 0.25 is the centre front (see writeRing). */
function makeFabricTexture(color, printText) {
  const W = 1024, H = 512;
  const canvas = document.createElement('canvas');
  canvas.width = W; canvas.height = H;
  const g = canvas.getContext('2d');
  g.fillStyle = color;
  g.fillRect(0, 0, W, H);

  // Fine knit: alternating light/dark columns plus noise, so the fabric catches light.
  const img = g.getImageData(0, 0, W, H);
  for (let y = 0; y < H; y++)
    for (let x = 0; x < W; x++) {
      const i = (y * W + x) * 4;
      const n = (x % 3 === 0 ? -10 : 4) + (Math.random() - 0.5) * 10;
      img.data[i] += n; img.data[i + 1] += n; img.data[i + 2] += n;
    }
  g.putImageData(img, 0, 0);

  // Ribbed hem and collar bands.
  g.fillStyle = 'rgba(0,0,0,0.12)';
  g.fillRect(0, H - 18, W, 18);
  g.fillRect(0, 0, W, 14);

  if (printText) {
    // Going around the ring, u increases from the wearer's left to right as seen by the camera,
    // so draw the print mirrored to read correctly on the chest.
    g.save();
    g.translate(W * 0.25, H * 0.30);
    g.scale(-1, 1);
    g.fillStyle = 'rgba(255,255,255,0.92)';
    g.font = `bold ${Math.round(H * 0.09)}px system-ui, sans-serif`;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(printText, 0, 0);
    g.restore();
  }

  const tex = new THREE.CanvasTexture(canvas);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.anisotropy = 4;
  return tex;
}
