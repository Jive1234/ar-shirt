import * as THREE from 'three';

const MAX_MASKS = 3;

/**
 * Final image = camera frame + shirts, with real clothing that sticks out from under the AR shirt hidden.
 *
 * Pass 1: shirts and occluders render into a transparent, multisampled render target.
 * Pass 2: a full-screen shader combines the video, that render target, the per-person masks, the clothes mask
 *         and (if captured) a clean background plate. See the fragment shader for the per-pixel logic.
 */
export class Compositor {
  constructor(renderer, video, mirrored) {
    this.renderer = renderer;
    this.mirrored = mirrored;
    this.videoTex = new THREE.VideoTexture(video);
    this.videoTex.colorSpace = THREE.SRGBColorSpace;
    this.width = 0;
    this.height = 0;

    this.garmentTarget = null;
    this.plateTarget = null;
    this.hasPlate = false;

    this.maskTextures = [];
    this.classTexture = null;
    const black = new THREE.DataTexture(new Uint8Array([0]), 1, 1, THREE.RedFormat);
    black.needsUpdate = true;
    this.black = black;

    this.material = new THREE.ShaderMaterial({
      uniforms: {
        uVideo: { value: this.videoTex },
        uGarment: { value: null },
        uGarmentTexel: { value: new THREE.Vector2() },
        uMasks: { value: [black, black, black] },
        uMaskCount: { value: 0 },
        uClass: { value: black },
        uHasClass: { value: 0 },
        uPlate: { value: black },
        uHasPlate: { value: 0 },
        uMirror: { value: mirrored ? 1 : 0 },
        uZones: { value: [new THREE.Vector4(), new THREE.Vector4(), new THREE.Vector4()] },
        uZoneCount: { value: 0 },
        uFillRadius: { value: 40 },
        uClothesThreshold: { value: 0.5 },
        uSoftness: { value: 0.15 },
        uDebug: { value: 0 },
      },
      vertexShader: /* glsl */ `
        varying vec2 vUv;
        void main() { vUv = uv; gl_Position = vec4(position.xy, 0.0, 1.0); }
      `,
      fragmentShader: /* glsl */ `
        uniform sampler2D uVideo, uGarment, uClass, uPlate;
        uniform sampler2D uMasks[${MAX_MASKS}];
        uniform vec2 uGarmentTexel;
        uniform int uMaskCount, uZoneCount;
        uniform vec4 uZones[${MAX_MASKS}];
        uniform float uHasClass, uHasPlate, uMirror, uFillRadius, uClothesThreshold, uSoftness, uDebug;
        varying vec2 vUv;

        float personCoverage(vec2 m) {
          float p = 0.0;
          if (uMaskCount > 0) p = max(p, texture2D(uMasks[0], m).r);
          if (uMaskCount > 1) p = max(p, texture2D(uMasks[1], m).r);
          if (uMaskCount > 2) p = max(p, texture2D(uMasks[2], m).r);
          return p;
        }

        // Real clothing is only replaced inside a shirt's (padded) screen box, so trousers stay untouched.
        float inZone(vec2 uv) {
          for (int i = 0; i < ${MAX_MASKS}; i++) {
            if (i >= uZoneCount) break;
            vec4 z = uZones[i];
            // Soft edges so the boundary of the replaced area never shows as a straight line.
            float e = 0.02;
            float w = smoothstep(z.x - e, z.x, uv.x) * (1.0 - smoothstep(z.z, z.z + e, uv.x)) *
                      smoothstep(z.y - e, z.y + e, uv.y) * (1.0 - smoothstep(z.w, z.w + e, uv.y));
            if (w > 0.0) return w;
          }
          return 0.0;
        }

        // Colour of the nearest opaque shirt pixel within uFillRadius (8 directions x 3 rings).
        // Alpha fades with distance so the fill blends into the real image instead of ending in a hard edge.
        vec4 nearestGarment(vec2 uv) {
          vec2 dirs[8];
          dirs[0] = vec2(1, 0); dirs[1] = vec2(-1, 0); dirs[2] = vec2(0, 1); dirs[3] = vec2(0, -1);
          dirs[4] = vec2(0.7071, 0.7071); dirs[5] = vec2(-0.7071, 0.7071);
          dirs[6] = vec2(0.7071, -0.7071); dirs[7] = vec2(-0.7071, -0.7071);
          for (int ring = 1; ring <= 3; ring++) {
            float r = uFillRadius * float(ring) / 3.0;
            for (int k = 0; k < 8; k++) {
              vec4 g = texture2D(uGarment, uv + dirs[k] * r * uGarmentTexel);
              if (g.a > 0.9) return vec4(g.rgb / g.a, 1.0 - 0.3 * float(ring - 1) / 2.0);
            }
          }
          return vec4(0.0);
        }

        void main() {
          vec2 uv = vUv;                                            // display space (mirrored in selfie view)
          vec2 camUv = vec2(uMirror > 0.5 ? 1.0 - uv.x : uv.x, uv.y);
          vec2 maskUv = vec2(camUv.x, 1.0 - camUv.y);               // MediaPipe masks: top-left origin

          vec3 base = texture2D(uVideo, camUv).rgb;
          vec4 garment = texture2D(uGarment, uv);

          // 1. Hide real clothing that the AR shirt does not cover.
          float uncovered = 0.0;
          if (uHasClass > 0.5) {
            float clothes = smoothstep(uClothesThreshold - uSoftness, uClothesThreshold + uSoftness,
                                       texture2D(uClass, maskUv).r);
            float person = clamp(personCoverage(maskUv) * 2.0, 0.0, 1.0); // only on tracked people
            uncovered = clothes * person * inZone(uv) * (1.0 - garment.a);
            if (uncovered > 0.01) {
              if (uHasPlate > 0.5) {
                base = mix(base, texture2D(uPlate, camUv).rgb, uncovered);
              } else {
                vec4 fill = nearestGarment(uv);
                base = mix(base, fill.rgb, uncovered * fill.a);
              }
            }
          }

          // 2. Shirt over (the resolved MSAA target is effectively premultiplied).
          vec3 rgb = garment.rgb + base * (1.0 - garment.a);

          if (uDebug > 0.5) {
            float p = personCoverage(maskUv);
            rgb = mix(rgb, vec3(0.0, 1.0, 0.3), 0.25 * p);
            rgb = mix(rgb, vec3(1.0, 0.1, 0.6), 0.6 * uncovered);
          }
          gl_FragColor = vec4(rgb, 1.0);
          #include <colorspace_fragment>
        }
      `,
      depthTest: false,
      depthWrite: false,
    });
    this.quadScene = new THREE.Scene();
    this.quadCamera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
    this.quadScene.add(new THREE.Mesh(new THREE.PlaneGeometry(2, 2), this.material));

    // For capturing the background plate.
    this.copyScene = new THREE.Scene();
    this.copyScene.add(new THREE.Mesh(new THREE.PlaneGeometry(2, 2), new THREE.ShaderMaterial({
      uniforms: { uVideo: { value: this.videoTex } },
      vertexShader: 'varying vec2 vUv; void main(){ vUv = uv; gl_Position = vec4(position.xy, 0.0, 1.0); }',
      fragmentShader: 'uniform sampler2D uVideo; varying vec2 vUv; void main(){ gl_FragColor = texture2D(uVideo, vUv); }',
      depthTest: false,
      depthWrite: false,
    })));
  }

  resize(width, height) {
    if (width === this.width && height === this.height) return;
    this.width = width;
    this.height = height;
    this.garmentTarget?.dispose();
    this.plateTarget?.dispose();
    this.garmentTarget = new THREE.WebGLRenderTarget(width, height, { samples: 4 });
    this.plateTarget = new THREE.WebGLRenderTarget(width, height);
    this.hasPlate = false;
    this.material.uniforms.uGarment.value = this.garmentTarget.texture;
    this.material.uniforms.uGarmentTexel.value.set(1 / width, 1 / height);
  }

  capturePlate() {
    const r = this.renderer;
    r.setRenderTarget(this.plateTarget);
    r.render(this.copyScene, this.quadCamera);
    r.setRenderTarget(null);
    this.hasPlate = true;
  }

  setDebug(on) {
    this.material.uniforms.uDebug.value = on ? 1 : 0;
  }

  /** @param zones up to 3 display-UV boxes [x0, y0, x1, y1] where real clothing may be replaced */
  render(garmentScene, garmentCamera, frame, zones = []) {
    const r = this.renderer;
    const u = this.material.uniforms;
    const pad = 0.015;
    u.uZoneCount.value = Math.min(zones.length, MAX_MASKS);
    zones.slice(0, MAX_MASKS).forEach((z, i) => u.uZones.value[i].set(z[0] - pad, z[1], z[2] + pad, z[3] + pad));

    // Masks
    const masks = frame?.personMasks ?? [];
    const count = Math.min(masks.length, MAX_MASKS);
    for (let i = 0; i < MAX_MASKS; i++) {
      if (i < count) {
        this.maskTextures[i] = syncMaskTexture(this.maskTextures[i], masks[i]);
        u.uMasks.value[i] = this.maskTextures[i];
      } else u.uMasks.value[i] = this.black;
    }
    u.uMaskCount.value = count;
    if (frame?.classMask) {
      this.classTexture = syncMaskTexture(this.classTexture, frame.classMask);
      u.uClass.value = this.classTexture;
      u.uHasClass.value = 1;
    } else u.uHasClass.value = 0;
    u.uPlate.value = this.hasPlate ? this.plateTarget.texture : this.black;
    u.uHasPlate.value = this.hasPlate ? 1 : 0;

    // Pass 1: shirts into the transparent target.
    r.setRenderTarget(this.garmentTarget);
    r.setClearColor(0x000000, 0);
    r.clear(true, true, true);
    r.render(garmentScene, garmentCamera);
    r.setRenderTarget(null);

    // Pass 2: composite to screen.
    r.render(this.quadScene, this.quadCamera);
  }
}

/** Upload a mask buffer from PoseSource into a (reused) single-channel texture. */
function syncMaskTexture(tex, mask) {
  if (!tex || tex.image.width !== mask.width || tex.image.height !== mask.height) {
    tex?.dispose();
    tex = new THREE.DataTexture(mask.data, mask.width, mask.height, THREE.RedFormat, THREE.UnsignedByteType);
    tex.minFilter = THREE.LinearFilter;
    tex.magFilter = THREE.LinearFilter;
    tex.unpackAlignment = 1;
    tex.userData.version = -1;
  }
  if (tex.userData.version !== mask.version) {
    tex.image.data = mask.data;
    tex.needsUpdate = true;
    tex.userData.version = mask.version;
  }
  return tex;
}
