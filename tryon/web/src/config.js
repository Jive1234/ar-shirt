// Settings you may want to change. Everything else adapts at runtime.
export const CONFIG = {
  // Camera
  cameraWidth: 1280,
  cameraHeight: 720,
  // Horizontal field of view of the webcam in degrees. Laptop webcams are typically 60–75°.
  // If the shirt looks too big or too small for everyone, adjust this first.
  horizontalFovDeg: 62,

  // MediaPipe
  tasksVisionVersion: '1.0.1',
  poseModel:
    'https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_full/float16/latest/pose_landmarker_full.task',
  // Multiclass segmenter supplies the "clothes" mask used to hide real clothing outside the AR shirt.
  segmenterModel:
    'https://storage.googleapis.com/mediapipe-models/image_segmenter/selfie_multiclass_256x256/float32/latest/selfie_multiclass_256x256.tflite',
  useClothesMask: true,
  segmenterEveryNFrames: 2,
  // Fill uncovered real clothing with a clean background captured while nobody is in view. Looks better for a
  // fixed kiosk camera, but erases real arms in long sleeves that stick out past a short AR sleeve, so it is off
  // by default and the shirt colour is extended instead.
  useBackgroundPlate: false, // the clothes mask changes slowly; running it every other frame saves ~30% GPU time
  maxPeople: 3,

  // Garment
  shirtColor: '#1f6feb',
  sleeve: 'short', // 'short' | 'long'
  printText: 'AR SHIRT',
  ease: { width: 1.08, length: 1.0 }, // >1 makes the AR shirt roomier so it covers the real one
};
