import { Lm, createObservation } from './landmarks.js';
import { OneEuroFilter } from './oneEuro.js';

// Image (0..1 of frame) and world (meters) coordinates both move at ~0–3 units/s for a person in frame,
// so similar parameters work for both. Tune on the target camera.
export const DefaultImageFilter = { minCutoff: 1.2, beta: 0.8, dCutoff: 1.0 };
export const DefaultWorldFilter = { minCutoff: 1.0, beta: 1.0, dCutoff: 1.0 };

/**
 * Smooths all landmarks of one tracked person. Low-visibility landmarks are not fed to the filter (MediaPipe
 * guesses them and the guesses jump); they coast on the filter velocity briefly and then freeze, so a sleeve
 * does not fly off when an arm leaves the frame.
 */
export class PoseSmoother {
  constructor(imageParams = DefaultImageFilter, worldParams = DefaultWorldFilter, visibilityThreshold = 0.5) {
    this.visibilityThreshold = visibilityThreshold;
    this.image = Array.from({ length: Lm.Count }, () => new OneEuroFilter(imageParams));
    this.world = Array.from({ length: Lm.Count }, () => new OneEuroFilter(worldParams));
    this.smoothed = createObservation();
  }

  reset() {
    for (const f of this.image) f.reset();
    for (const f of this.world) f.reset();
  }

  update(raw, t) {
    const out = this.smoothed;
    for (let i = 0; i < Lm.Count; i++) {
      const visible = raw.vis[i] >= this.visibilityThreshold;
      if (visible || !this.image[i].initialized) {
        out.image[i] = this.image[i].filter(raw.image[i], t);
        out.world[i] = this.world[i].filter(raw.world[i], t);
      } else {
        out.image[i] = this.image[i].predict(t);
        out.world[i] = this.world[i].predict(t);
      }
      out.vis[i] = raw.vis[i];
    }
    out.maskIndex = raw.maskIndex;
    return out;
  }
}
