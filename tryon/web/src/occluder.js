import * as THREE from 'three';
import { Lm } from './landmarks.js';
import { add, sub, scale, norm, len, mid } from './vec.js';

/**
 * Invisible proxies for body parts that must appear IN FRONT of the shirt (forearms and hands crossing the
 * torso, the head over the collar). They write depth but no colour and are drawn before the shirt, so shirt
 * pixels behind them fail the depth test and the composite shows the real camera pixel instead.
 *
 * The forearm radius is smaller than a long sleeve's radius, so the sleeve wrapped around the forearm still
 * draws while the torso behind the forearm is hidden.
 */
export class BodyOccluder {
  constructor({ forearmRadius = 0.035, handRadius = 0.055, headRadius = 0.1 } = {}) {
    this.group = new THREE.Group();
    this.material = new THREE.MeshBasicMaterial({ colorWrite: false });
    const capsule = new THREE.CapsuleGeometry(1, 1, 4, 12); // scaled per frame
    const sphere = new THREE.SphereGeometry(1, 16, 12);
    const make = (geo) => {
      const m = new THREE.Mesh(geo, this.material);
      m.renderOrder = -1; // depth before the shirt
      m.frustumCulled = false;
      this.group.add(m);
      return m;
    };
    this.parts = {
      lFore: make(capsule), rFore: make(capsule),
      lHand: make(sphere), rHand: make(sphere),
      head: make(sphere), neck: make(capsule),
    };
    Object.assign(this, { forearmRadius, handRadius, headRadius });
  }

  update(J, pose) {
    const vis = (i) => pose.vis[i] >= 0.5;
    const { lFore, rFore, lHand, rHand, head, neck } = this.parts;
    segment(lFore, J[Lm.LeftElbow], J[Lm.LeftWrist], this.forearmRadius, vis(Lm.LeftElbow) && vis(Lm.LeftWrist));
    segment(rFore, J[Lm.RightElbow], J[Lm.RightWrist], this.forearmRadius, vis(Lm.RightElbow) && vis(Lm.RightWrist));
    ball(lHand, handCenter(J[Lm.LeftElbow], J[Lm.LeftWrist]), this.handRadius, vis(Lm.LeftWrist));
    ball(rHand, handCenter(J[Lm.RightElbow], J[Lm.RightWrist]), this.handRadius, vis(Lm.RightWrist));

    // The head centre sits ~8 cm behind the nose (away from the camera at the origin).
    const nose = J[Lm.Nose];
    const headC = add(nose, scale(norm(nose), 0.08));
    ball(head, headC, this.headRadius, vis(Lm.Nose));
    // Neck: from the head down to just above the shoulder line, thin enough to stay behind the collar front.
    const neckBase = add(mid(J[Lm.LeftShoulder], J[Lm.RightShoulder]), scale(norm(nose), 0.06));
    segment(neck, headC, neckBase, 0.05, vis(Lm.Nose));
  }

  dispose() {
    for (const m of Object.values(this.parts)) m.geometry.dispose();
    this.material.dispose();
  }
}

function handCenter(elbow, wrist) {
  const d = sub(wrist, elbow);
  return len(d) > 1e-6 ? add(wrist, scale(norm(d), 0.06)) : wrist;
}

const UP = new THREE.Vector3(0, 1, 0);
const tmp = new THREE.Vector3();

function segment(mesh, a, b, r, on) {
  const d = sub(b, a);
  const l = len(d);
  mesh.visible = on && l > 1e-4;
  if (!mesh.visible) return;
  mesh.position.set((a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2);
  mesh.quaternion.setFromUnitVectors(UP, tmp.set(d[0] / l, d[1] / l, d[2] / l));
  // CapsuleGeometry(1, 1): radius 1, straight length 1 → total height 3 at scale 1.
  mesh.scale.set(r, l / 3, r);
}

function ball(mesh, p, r, on) {
  mesh.visible = on;
  if (!on) return;
  mesh.position.set(p[0], p[1], p[2]);
  mesh.scale.setScalar(r);
}
