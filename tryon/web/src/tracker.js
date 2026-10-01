import { torsoCenter2D } from './landmarks.js';
import { PoseSmoother, DefaultImageFilter, DefaultWorldFilter } from './smoother.js';
import { CalibrationStateMachine, DefaultCalibrationSettings } from './calibration.js';

/**
 * MediaPipe returns people in arbitrary order every frame. This assigns stable ids by greedy nearest-neighbour
 * matching on the torso centre, which holds for 2–4 people who do not swap places while overlapping.
 * Each track owns its own smoother and calibration.
 */
export class PersonTracker {
  constructor({
    maxMatchDistance = 0.15,
    lostTimeout = 1.0,
    imageFilter = DefaultImageFilter,
    worldFilter = DefaultWorldFilter,
    calibration = DefaultCalibrationSettings,
  } = {}) {
    Object.assign(this, { maxMatchDistance, lostTimeout, imageFilter, worldFilter, calibration });
    this.tracks = [];
    this.nextId = 1;
    this.onTrackCreated = null;
    this.onTrackLost = null;
  }

  update(people, t) {
    const pairs = [];
    this.tracks.forEach((tr, ti) => {
      const c = torsoCenter2D(tr.pose);
      people.forEach((obs, oi) => {
        const o = torsoCenter2D(obs);
        const d = Math.hypot(c[0] - o[0], c[1] - o[1]);
        if (d <= this.maxMatchDistance) pairs.push([d, ti, oi]);
      });
    });
    pairs.sort((a, b) => a[0] - b[0]);

    const usedT = new Set(), usedO = new Set();
    for (const tr of this.tracks) tr.seenThisFrame = false;
    for (const [, ti, oi] of pairs) {
      if (usedT.has(ti) || usedO.has(oi)) continue;
      usedT.add(ti); usedO.add(oi);
      this.#feed(this.tracks[ti], people[oi], t);
    }

    people.forEach((obs, oi) => {
      if (usedO.has(oi)) return;
      const tr = {
        id: this.nextId++,
        smoother: new PoseSmoother(this.imageFilter, this.worldFilter),
        calibration: new CalibrationStateMachine(this.calibration),
        raw: null,
        lastSeen: t,
        seenThisFrame: false,
        get pose() { return this.smoother.smoothed; },
      };
      this.tracks.push(tr);
      this.#feed(tr, obs, t);
      this.onTrackCreated?.(tr);
    });

    for (let i = this.tracks.length - 1; i >= 0; i--) {
      const tr = this.tracks[i];
      if (tr.seenThisFrame) continue;
      tr.calibration.tick(null, null, t);
      if (t - tr.lastSeen > this.lostTimeout) {
        this.tracks.splice(i, 1);
        this.onTrackLost?.(tr);
      }
    }
  }

  #feed(tr, obs, t) {
    tr.raw = obs;
    tr.smoother.update(obs, t);
    tr.lastSeen = t;
    tr.seenThisFrame = true;
    // Stillness is judged on the raw pose (smoothing would hide motion); lengths come from the smoothed one.
    tr.calibration.tick(obs, tr.pose, t);
  }
}
