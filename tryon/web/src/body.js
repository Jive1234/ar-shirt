import { Lm } from './landmarks.js';
import { unproject } from './camera.js';
import { mid, sub } from './vec.js';

/**
 * Camera-space joint positions for one smoothed pose.
 *
 * Depth comes from apparent size: the image-plane (xy) extent of shoulders + torso is measured both in the
 * world landmarks (meters) and in pixels, so foreshortening cancels when the person turns or leans. Each joint
 * then takes x/y from the image (so the garment sticks to the person's pixels) and depth from the hips plus the
 * world landmarks' relative depth.
 *
 * @param pose smoothed observation
 * @param cam  { fov (vertical deg), aspect, widthPx, heightPx }
 * @returns {{ joints: number[][], hipDepth: number } | null}
 */
export function cameraSpaceJoints(pose, cam) {
  const w = pose.world, img = pose.image;
  const ls = w[Lm.LeftShoulder], rs = w[Lm.RightShoulder], lh = w[Lm.LeftHip], rh = w[Lm.RightHip];
  const midS = mid(ls, rs), midH = mid(lh, rh);
  const xy = (a) => Math.hypot(a[0], a[1]);
  const worldSpan = xy(sub(ls, rs)) + xy(sub(midS, midH));

  const px = (a, b) => Math.hypot((a[0] - b[0]) * cam.widthPx, (a[1] - b[1]) * cam.heightPx);
  const iMidS = [(img[Lm.LeftShoulder][0] + img[Lm.RightShoulder][0]) / 2, (img[Lm.LeftShoulder][1] + img[Lm.RightShoulder][1]) / 2];
  const iMidH = [(img[Lm.LeftHip][0] + img[Lm.RightHip][0]) / 2, (img[Lm.LeftHip][1] + img[Lm.RightHip][1]) / 2];
  const pxSpan = px(img[Lm.LeftShoulder], img[Lm.RightShoulder]) + px(iMidS, iMidH);
  if (pxSpan < 1 || worldSpan < 1e-3) return null;

  const focalPx = (0.5 * cam.heightPx) / Math.tan((cam.fov * Math.PI) / 360);
  const hipDepth = (focalPx * worldSpan) / pxSpan;

  // world z points toward the camera, so a joint with larger z is closer (smaller distance).
  const joints = new Array(Lm.Count);
  for (let i = 0; i < Lm.Count; i++) {
    const d = Math.max(0.2, hipDepth - (w[i][2] - midH[2]));
    joints[i] = unproject(img[i][0], img[i][1], d, cam);
  }
  return { joints, hipDepth };
}
