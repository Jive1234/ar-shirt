// MediaPipe BlazePose landmark indices used by the try-on pipeline.
export const Lm = {
  Nose: 0,
  LeftShoulder: 11, RightShoulder: 12,
  LeftElbow: 13, RightElbow: 14,
  LeftWrist: 15, RightWrist: 16,
  LeftHip: 23, RightHip: 24,
  Count: 33,
};

// Left/right partner of each landmark. In a mirrored (selfie) view the reflected body is geometrically a
// different person whose left is the subject's anatomical right, so the pose source swaps sides; everything
// downstream can then treat each pose as an ordinary body facing the camera.
export const MirrorOf = [
  0, 4, 5, 6, 1, 2, 3, 8, 7, 10, 9, 12, 11, 14, 13, 16, 15,
  18, 17, 20, 19, 22, 21, 24, 23, 26, 25, 28, 27, 30, 29, 32, 31,
];

export const UpperBody = [Lm.LeftShoulder, Lm.RightShoulder, Lm.LeftElbow, Lm.RightElbow, Lm.LeftHip, Lm.RightHip];
export const UpperBodyWithWrists = [...UpperBody, Lm.LeftWrist, Lm.RightWrist];

/**
 * One person in one frame, in three.js conventions:
 *  image[i]  = [u, v]    normalized, origin bottom-left, already mirrored for selfie view
 *  world[i]  = [x, y, z] meters, origin at hip centre, x right, y up, z toward the camera
 *  vis[i]    = 0..1
 */
export function createObservation() {
  return {
    image: Array.from({ length: Lm.Count }, () => [0, 0]),
    world: Array.from({ length: Lm.Count }, () => [0, 0, 0]),
    vis: new Float32Array(Lm.Count),
    maskIndex: -1,
  };
}

export function torsoCenter2D(obs) {
  const ids = [Lm.LeftShoulder, Lm.RightShoulder, Lm.LeftHip, Lm.RightHip];
  let u = 0, v = 0;
  for (const i of ids) { u += obs.image[i][0]; v += obs.image[i][1]; }
  return [u / 4, v / 4];
}

export function allVisible(obs, ids, threshold) {
  for (const i of ids) if (obs.vis[i] < threshold) return false;
  return true;
}
