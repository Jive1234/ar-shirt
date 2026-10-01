// One Euro Filter (Casiez et al., CHI 2012): a low-pass filter whose cutoff rises with speed.
// Slow movement → heavy smoothing (no jitter); fast movement → light smoothing (no lag).
// Tuning: lower minCutoff until standing still is steady, then raise beta until fast moves stop lagging.

const alpha = (cutoff, dt) => {
  const tau = 1 / (2 * Math.PI * cutoff);
  return 1 / (1 + tau / dt);
};

/** Filters an N-dimensional point; speed is measured on the vector magnitude so all axes share one cutoff. */
export class OneEuroFilter {
  constructor({ minCutoff = 1.0, beta = 0.007, dCutoff = 1.0 } = {}) {
    this.minCutoff = minCutoff;
    this.beta = beta;
    this.dCutoff = dCutoff;
    this.reset();
  }

  reset() {
    this.initialized = false;
    this.x = null;
    this.dx = null;
    this.lastT = 0;
  }

  /** @param {number[]} value  @param {number} t seconds (use the camera frame time, not render time) */
  filter(value, t) {
    if (!this.initialized) {
      this.x = value.slice();
      this.dx = value.map(() => 0);
      this.lastT = t;
      this.initialized = true;
      return this.x.slice();
    }
    const dt = t - this.lastT;
    if (dt <= 1e-6) return this.x.slice(); // duplicate or out-of-order sample
    this.lastT = t;

    const aD = alpha(this.dCutoff, dt);
    let speed = 0;
    for (let i = 0; i < value.length; i++) {
      const raw = (value[i] - this.x[i]) / dt;
      this.dx[i] += (raw - this.dx[i]) * aD;
      speed += this.dx[i] * this.dx[i];
    }
    const a = alpha(this.minCutoff + this.beta * Math.sqrt(speed), dt);
    for (let i = 0; i < value.length; i++) this.x[i] += (value[i] - this.x[i]) * a;
    return this.x.slice();
  }

  /** Constant-velocity extrapolation, used to bridge short occlusions. */
  predict(t) {
    const dt = Math.min(Math.max(t - this.lastT, 0), 0.15);
    return this.x.map((v, i) => v + this.dx[i] * dt);
  }
}
