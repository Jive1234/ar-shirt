import { FilesetResolver, PoseLandmarker, ImageSegmenter } from '@mediapipe/tasks-vision';
import { Lm, MirrorOf, createObservation } from './landmarks.js';

const CLOTHES_CLASS = 4; // selfie_multiclass_256x256: 0 bg, 1 hair, 2 body-skin, 3 face-skin, 4 clothes, 5 others

/**
 * Runs MediaPipe PoseLandmarker (multi-person, with per-person segmentation masks) and, optionally, the
 * multiclass segmenter for a "clothes" mask. Results are converted to three.js conventions:
 * image coords bottom-left origin, mirrored (with left/right swapped) for the selfie view; world coords
 * x right, y up, z toward the camera.
 */
export class PoseSource {
  constructor({ wasmBase, poseModel, segmenterModel, useClothesMask, segmenterEveryNFrames, maxPeople, mirrored }) {
    Object.assign(this, { wasmBase, poseModel, segmenterModel, useClothesMask, segmenterEveryNFrames, maxPeople, mirrored });
    this.pose = null;
    this.segmenter = null;
    this.delegate = null;
    this.frameCount = 0;
    this.lastTimestampMs = -1;
    this.lastInferenceMs = 0;
    // Reused mask buffers, one per person slot plus the clothes mask.
    this.personMasks = [];
    this.classMask = null;
  }

  async init(onProgress = () => {}) {
    onProgress('wasm');
    const fileset = await FilesetResolver.forVisionTasks(this.wasmBase);
    // GPU is 2–4x faster; fall back to CPU where WebGL inference is unavailable.
    for (const delegate of ['GPU', 'CPU']) {
      try {
        onProgress('pose');
        this.pose = await PoseLandmarker.createFromOptions(fileset, {
          baseOptions: { modelAssetPath: this.poseModel, delegate },
          runningMode: 'VIDEO',
          numPoses: this.maxPeople,
          minPoseDetectionConfidence: 0.5,
          minPosePresenceConfidence: 0.5,
          minTrackingConfidence: 0.5,
          outputSegmentationMasks: true,
        });
        if (this.useClothesMask) {
          onProgress('segmenter');
          this.segmenter = await ImageSegmenter.createFromOptions(fileset, {
            baseOptions: { modelAssetPath: this.segmenterModel, delegate },
            runningMode: 'VIDEO',
            outputConfidenceMasks: true,
            outputCategoryMask: false,
          });
        }
        this.delegate = delegate;
        return;
      } catch (err) {
        console.warn(`[PoseSource] ${delegate} delegate failed`, err);
        this.pose?.close();
        this.segmenter?.close();
        this.pose = this.segmenter = null;
      }
    }
    throw new Error('MediaPipe could not start on GPU or CPU');
  }

  /** Run inference on the current video frame. Returns a PoseFrame-like object. */
  detect(video, nowMs) {
    let ts = Math.round(nowMs);
    if (ts <= this.lastTimestampMs) ts = this.lastTimestampMs + 1; // MediaPipe needs strictly increasing timestamps
    this.lastTimestampMs = ts;

    const t0 = performance.now();
    const result = this.pose.detectForVideo(video, ts);
    const frame = {
      timestamp: ts / 1000,
      width: video.videoWidth,
      height: video.videoHeight,
      people: [],
      personMasks: [],
      classMask: this.classMask, // reused between segmenter runs
    };

    const n = result.landmarks?.length ?? 0;
    for (let p = 0; p < n; p++) {
      const norm = result.landmarks[p];
      const world = result.worldLandmarks[p];
      const obs = createObservation();
      for (let i = 0; i < Lm.Count; i++) {
        const src = this.mirrored ? MirrorOf[i] : i;
        const nl = norm[src], wl = world[src];
        // MediaPipe image: origin top-left, y down. Here: origin bottom-left, y up.
        obs.image[i] = [this.mirrored ? 1 - nl.x : nl.x, 1 - nl.y];
        // MediaPipe world: x right, y down, z negative toward the camera. Here: y up, z positive toward the camera.
        obs.world[i] = [this.mirrored ? -wl.x : wl.x, -wl.y, -wl.z];
        obs.vis[i] = Math.min(nl.visibility ?? 1, nl.presence ?? 1);
      }
      const mask = result.segmentationMasks?.[p];
      if (mask) {
        obs.maskIndex = frame.personMasks.length;
        frame.personMasks.push(this.#copyMask(mask, p));
      }
      frame.people.push(obs);
    }
    result.close(); // frees the masks

    if (this.segmenter && this.frameCount % this.segmenterEveryNFrames === 0 && n > 0) {
      const seg = this.segmenter.segmentForVideo(video, ts);
      const clothes = seg.confidenceMasks?.[CLOTHES_CLASS];
      if (clothes) {
        this.classMask = this.#copyMask(clothes, 'class');
        frame.classMask = this.classMask;
      }
      seg.close();
    }
    this.frameCount++;
    this.lastInferenceMs = performance.now() - t0;
    return frame;
  }

  /** Float mask → reused Uint8 buffer (top-left origin, unmirrored; the compositor handles orientation). */
  #copyMask(mask, slot) {
    const src = mask.getAsFloat32Array();
    let buf = slot === 'class' ? this.classMask : this.personMasks[slot];
    if (!buf || buf.width !== mask.width || buf.height !== mask.height) {
      buf = { width: mask.width, height: mask.height, data: new Uint8Array(mask.width * mask.height), version: 0 };
      if (slot === 'class') this.classMask = buf; else this.personMasks[slot] = buf;
    }
    const dst = buf.data;
    for (let i = 0; i < src.length; i++) dst[i] = src[i] * 255;
    buf.version++;
    return buf;
  }

  close() {
    this.pose?.close();
    this.segmenter?.close();
  }
}
