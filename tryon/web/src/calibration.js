import { Lm, UpperBody, UpperBodyWithWrists, allVisible, torsoCenter2D } from './landmarks.js';
import { sub, mid, dist, angleDeg, scale } from './vec.js';

export const CalibrationState = Object.freeze({
  WaitingForPerson: 'WaitingForPerson',
  WaitingForPose: 'WaitingForPose',
  Collecting: 'Collecting',
  Calibrated: 'Calibrated',
});

/** Why the pose is rejected right now; drive on-screen hints from this. */
export const PoseIssue = Object.freeze({
  None: 'None',
  NotVisible: 'NotVisible',
  NotFacingCamera: 'NotFacingCamera',
  ShouldersTilted: 'ShouldersTilted',
  Leaning: 'Leaning',
  ArmsNotRelaxed: 'ArmsNotRelaxed',
  Moving: 'Moving',
});

export const DefaultCalibrationSettings = Object.freeze({
  holdSeconds: 1.5,          // how long the pose must be held
  graceSeconds: 0.25,        // a wobble shorter than this does not restart the countdown
  minSamples: 20,
  minVisibility: 0.7,
  requireWrists: true,
  maxShoulderDepthDelta: 0.12, // m: person must face the camera
  maxShoulderTilt: 0.06,       // m
  maxTorsoLeanDeg: 15,
  armAbductionRangeDeg: [8, 70], // relaxed A-pose: upper arm angle from the torso's down axis
  maxStillSpeed: 0.08,         // torso centre speed, normalized image units per second
});

const median = (arr) => {
  if (arr.length === 0) return 0;
  const s = [...arr].sort((a, b) => a - b);
  const n = s.length;
  return n % 2 ? s[(n - 1) / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
};

/**
 * Standing-pose calibration. The person faces the camera in a relaxed A-pose; once the pose has been valid and
 * still for holdSeconds, the median of the collected limb lengths becomes their body measurements (metric,
 * from MediaPipe world landmarks). Median rather than mean, so a few bad frames do not skew the result.
 */
export class CalibrationStateMachine {
  constructor(settings = DefaultCalibrationSettings) {
    this.s = { ...DefaultCalibrationSettings, ...settings };
    this.state = CalibrationState.WaitingForPerson;
    this.issue = PoseIssue.NotVisible;
    this.progress = 0;
    this.result = null;
    this.onStateChanged = null;
    this.onCalibrated = null;
    this.samples = { shoulder: [], hip: [], torso: [], upperArm: [], forearm: [] };
    this.collectStart = 0;
    this.lastValid = 0;
    this.prevCenter = null;
    this.prevT = 0;
  }

  recalibrate() {
    this.#clear();
    this.prevCenter = null;
    this.progress = 0;
    this.result = null;
    this.#setState(CalibrationState.WaitingForPerson);
  }

  /** Skip the pose check and accept the given measurements (used by ?skipCalib=1 and for tests). */
  force(measurements) {
    this.result = measurements;
    this.progress = 1;
    this.#setState(CalibrationState.Calibrated);
    this.onCalibrated?.(measurements);
  }

  /**
   * @param raw       unsmoothed observation (stillness test) or null when the person was not detected
   * @param smoothed  smoothed observation (measurements) or null
   * @param t         seconds
   */
  tick(raw, smoothed, t) {
    if (this.state === CalibrationState.Calibrated) return;

    if (!raw || !smoothed) {
      this.prevCenter = null;
      if (this.state === CalibrationState.Collecting && t - this.lastValid > this.s.graceSeconds) this.#restart();
      if (this.state !== CalibrationState.Collecting) this.#setState(CalibrationState.WaitingForPerson);
      this.issue = PoseIssue.NotVisible;
      return;
    }

    if (this.state === CalibrationState.WaitingForPerson) this.#setState(CalibrationState.WaitingForPose);

    this.issue = this.#evaluate(raw, t);
    if (this.issue !== PoseIssue.None) {
      if (this.state === CalibrationState.Collecting && t - this.lastValid > this.s.graceSeconds) this.#restart();
      return;
    }

    this.lastValid = t;
    if (this.state === CalibrationState.WaitingForPose) {
      this.#clear();
      this.collectStart = t;
      this.#setState(CalibrationState.Collecting);
    }

    this.#addSample(smoothed);
    const held = t - this.collectStart;
    this.progress = Math.min(1, held / this.s.holdSeconds);

    if (held >= this.s.holdSeconds && this.samples.shoulder.length >= this.s.minSamples) {
      this.result = measureFromSamples(this.samples);
      this.progress = 1;
      this.#setState(CalibrationState.Calibrated);
      this.onCalibrated?.(this.result);
    }
  }

  #evaluate(p, t) {
    // Stillness first, because it needs the previous frame regardless of the other results.
    const c = torsoCenter2D(p);
    let speed = 0;
    const hadPrev = this.prevCenter !== null;
    if (hadPrev && t > this.prevT) speed = Math.hypot(c[0] - this.prevCenter[0], c[1] - this.prevCenter[1]) / (t - this.prevT);
    this.prevCenter = c;
    this.prevT = t;

    const required = this.s.requireWrists ? UpperBodyWithWrists : UpperBody;
    if (!allVisible(p, required, this.s.minVisibility)) return PoseIssue.NotVisible;

    const w = p.world;
    const ls = w[Lm.LeftShoulder], rs = w[Lm.RightShoulder], lh = w[Lm.LeftHip], rh = w[Lm.RightHip];
    if (Math.abs(ls[2] - rs[2]) > this.s.maxShoulderDepthDelta) return PoseIssue.NotFacingCamera;
    if (Math.abs(ls[1] - rs[1]) > this.s.maxShoulderTilt) return PoseIssue.ShouldersTilted;

    const up = sub(mid(ls, rs), mid(lh, rh));
    if (angleDeg(up, [0, 1, 0]) > this.s.maxTorsoLeanDeg) return PoseIssue.Leaning;

    const down = scale(up, -1);
    const aL = angleDeg(sub(w[Lm.LeftElbow], ls), down);
    const aR = angleDeg(sub(w[Lm.RightElbow], rs), down);
    const [lo, hi] = this.s.armAbductionRangeDeg;
    if (aL < lo || aL > hi || aR < lo || aR > hi) return PoseIssue.ArmsNotRelaxed;

    if (!hadPrev || speed > this.s.maxStillSpeed) return PoseIssue.Moving;
    return PoseIssue.None;
  }

  #addSample(p) {
    const m = measureOnce(p);
    for (const k of Object.keys(this.samples)) this.samples[k].push(m[k]);
  }

  #restart() {
    this.#clear();
    this.progress = 0;
    this.#setState(CalibrationState.WaitingForPose);
  }

  #clear() {
    for (const k of Object.keys(this.samples)) this.samples[k].length = 0;
  }

  #setState(s) {
    if (this.state === s) return;
    this.state = s;
    this.onStateChanged?.(s);
  }
}

/** Body measurements from a single observation (meters). */
export function measureOnce(p) {
  const w = p.world;
  const ls = w[Lm.LeftShoulder], rs = w[Lm.RightShoulder], lh = w[Lm.LeftHip], rh = w[Lm.RightHip];
  return {
    shoulder: dist(ls, rs),
    hip: dist(lh, rh),
    torso: dist(mid(ls, rs), mid(lh, rh)),
    upperArm: (dist(ls, w[Lm.LeftElbow]) + dist(rs, w[Lm.RightElbow])) / 2,
    forearm: (dist(w[Lm.LeftElbow], w[Lm.LeftWrist]) + dist(w[Lm.RightElbow], w[Lm.RightWrist])) / 2,
  };
}

function measureFromSamples(s) {
  return {
    shoulder: median(s.shoulder),
    hip: median(s.hip),
    torso: median(s.torso),
    upperArm: median(s.upperArm),
    forearm: median(s.forearm),
    sampleCount: s.shoulder.length,
  };
}
