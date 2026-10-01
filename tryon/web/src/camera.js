// Pinhole camera helpers that match the three.js PerspectiveCamera at the origin looking down -Z.

/** Point in camera space for normalized viewport (u, v; origin bottom-left) at `distance` meters in front. */
export function unproject(u, v, distance, cam) {
  const t = Math.tan((cam.fov * Math.PI) / 360);
  return [(2 * u - 1) * t * cam.aspect * distance, (2 * v - 1) * t * distance, -distance];
}

export function verticalFov(horizontalFovDeg, aspect) {
  const h = (horizontalFovDeg * Math.PI) / 180;
  return (2 * Math.atan(Math.tan(h / 2) / aspect) * 180) / Math.PI;
}
