// Run: node --test tryon/web/test
import test from 'node:test';
import assert from 'node:assert/strict';
import { Lm, MirrorOf, createObservation, torsoCenter2D } from '../src/landmarks.js';
import { OneEuroFilter } from '../src/oneEuro.js';
import { PoseSmoother } from '../src/smoother.js';
import { CalibrationStateMachine, CalibrationState, PoseIssue } from '../src/calibration.js';
import { PersonTracker } from '../src/tracker.js';

// Deterministic noise.
let seed = 1;
const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647) * 2 - 1;
const N = (s) => rnd() * s;

/** Synthetic person facing the camera in an A-pose, torso centre at image x = cx. */
function person(cx, noise, armDeg = 25, shoulderW = 0.40) {
  const p = createObservation();
  p.vis.fill(0.95);
  const a = (armDeg * Math.PI) / 180, ua = 0.30, fa = 0.26, hs = shoulderW / 2;
  const W = p.world;
  W[Lm.LeftHip] = [0.15, 0, 0]; W[Lm.RightHip] = [-0.15, 0, 0];
  W[Lm.LeftShoulder] = [hs, 0.5, 0]; W[Lm.RightShoulder] = [-hs, 0.5, 0];
  const arm = (from, sx, l) => [from[0] + sx * Math.sin(a) * l, from[1] - Math.cos(a) * l, from[2]];
  W[Lm.LeftElbow] = arm(W[Lm.LeftShoulder], 1, ua); W[Lm.RightElbow] = arm(W[Lm.RightShoulder], -1, ua);
  W[Lm.LeftWrist] = arm(W[Lm.LeftElbow], 1, fa); W[Lm.RightWrist] = arm(W[Lm.RightElbow], -1, fa);
  W[Lm.Nose] = [0, 0.75, 0.05];
  for (let i = 0; i < Lm.Count; i++) {
    W[i] = W[i].map((v) => v + N(noise));
    p.image[i] = [cx + W[i][0] * 0.5 + N(noise / 2), 0.4 + W[i][1] * 0.5 + N(noise / 2)];
  }
  return p;
}

test('MirrorOf is a 33-entry involution that pairs left and right', () => {
  assert.equal(MirrorOf.length, 33);
  MirrorOf.forEach((m, i) => assert.equal(MirrorOf[m], i));
  assert.equal(MirrorOf[Lm.LeftShoulder], Lm.RightShoulder);
  assert.equal(MirrorOf[Lm.LeftHip], Lm.RightHip);
});

test('One Euro cuts jitter when still and settles quickly on a fast move', () => {
  const f = new OneEuroFilter({ minCutoff: 1, beta: 1 });
  let t = 0, raw = 0, out = 0;
  for (let i = 0; i < 300; i++) {
    t += 1 / 30;
    const r = [0.5 + N(0.01), 0.5 + N(0.01)];
    const o = f.filter(r, t);
    if (i > 30) { raw += Math.hypot(r[0] - 0.5, r[1] - 0.5); out += Math.hypot(o[0] - 0.5, o[1] - 0.5); }
  }
  assert.ok(out < raw * 0.4, `jitter ratio ${(out / raw).toFixed(2)}`);
  let x = 0.5, o;
  for (let i = 0; i < 30; i++) { t += 1 / 30; x = Math.min(0.8, x + 2 / 30); o = f.filter([x, 0.5], t); }
  assert.ok(Math.abs(o[0] - 0.8) < 0.02);
  const g = new OneEuroFilter();
  g.filter([1], 0);
  assert.deepEqual(g.filter([5], 0), [1], 'duplicate timestamp ignored');
});

test('calibration completes in ~1.5 s with measurements within 2 cm', () => {
  const sm = new CalibrationStateMachine();
  const smooth = new PoseSmoother();
  let got = null, frames = 0, t = 0;
  sm.onCalibrated = (m) => (got = m);
  while (!got && frames < 300) { t += 1 / 30; frames++; const r = person(0.5, 0.004); sm.tick(r, smooth.update(r, t), t); }
  assert.ok(got, 'calibrated');
  assert.ok(frames >= 45 && frames <= 55, `frames ${frames}`);
  for (const [k, v] of Object.entries({ shoulder: 0.4, hip: 0.3, torso: 0.5, upperArm: 0.3, forearm: 0.26 }))
    assert.ok(Math.abs(got[k] - v) < 0.02, `${k} ${got[k]}`);
});

test('calibration rejects a T-pose and a walking person, tolerates a 1-frame wobble', () => {
  const tp = new CalibrationStateMachine();
  let t = 0;
  for (let i = 0; i < 90; i++) { t += 1 / 30; const r = person(0.5, 0.002, 90); tp.tick(r, r, t); }
  assert.equal(tp.state, CalibrationState.WaitingForPose);
  assert.equal(tp.issue, PoseIssue.ArmsNotRelaxed);

  const walk = new CalibrationStateMachine();
  t = 0;
  for (let i = 0; i < 90; i++) { t += 1 / 30; const r = person(0.3 + (0.3 * (i % 30)) / 30, 0.002); walk.tick(r, r, t); }
  assert.notEqual(walk.state, CalibrationState.Calibrated);
  assert.equal(walk.issue, PoseIssue.Moving);

  const g = new CalibrationStateMachine();
  t = 0;
  for (let i = 0; i < 30; i++) { t += 1 / 30; const r = person(0.5, 0.002); g.tick(r, r, t); }
  const before = g.progress;
  t += 1 / 30; const bad = person(0.5, 0.002, 90); g.tick(bad, bad, t);
  for (let i = 0; i < 2; i++) { t += 1 / 30; const r = person(0.5, 0.002); g.tick(r, r, t); }
  assert.equal(g.state, CalibrationState.Collecting);
  assert.ok(g.progress > before);
});

test('tracker keeps stable ids for two people in shuffled order and drops a lost one', () => {
  const tr = new PersonTracker();
  let created = 0, calibrated = 0;
  tr.onTrackCreated = (k) => { created++; k.calibration.onCalibrated = () => calibrated++; };
  let t = 0;
  for (let i = 0; i < 90; i++) {
    t += 1 / 30;
    const a = person(0.25, 0.003), b = person(0.75, 0.003, 25, 0.36);
    tr.update(i % 2 ? [b, a] : [a, b], t);
  }
  assert.equal(created, 2);
  assert.equal(tr.tracks.length, 2);
  assert.equal(calibrated, 2);
  const left = tr.tracks.find((k) => torsoCenter2D(k.pose)[0] < 0.5);
  const right = tr.tracks.find((k) => torsoCenter2D(k.pose)[0] > 0.5);
  assert.ok(Math.abs(left.calibration.result.shoulder - 0.40) < 0.02);
  assert.ok(Math.abs(right.calibration.result.shoulder - 0.36) < 0.02);
  for (let i = 0; i < 40; i++) { t += 1 / 30; tr.update([person(0.25, 0.003)], t); }
  assert.equal(tr.tracks.length, 1);
});
