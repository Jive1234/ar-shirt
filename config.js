// ตั้งค่าโปรเจกต์ / Project settings
// แก้ไฟล์นี้ไฟล์เดียวเพื่อเปลี่ยนลายเสื้อและโมเดล 3D
// Edit only this file to swap the shirt image and the 3D model.
export const CONFIG = {
  // ไฟล์ .mind ที่คอมไพล์จากรูปลายเสื้อ (ดู README)
  // Compiled .mind file of the shirt print (see README).
  // ค่าเริ่มต้นคือรูปตัวอย่างของ MindAR เพื่อให้ลองได้ทันที
  // The default is MindAR's sample card so it works out of the box.
  imageTargetSrc:
    "https://cdn.jsdelivr.net/gh/hiukim/mind-ar-js@1.2.5/examples/image-tracking/assets/card-example/card.mind",
  // ตัวอย่างไฟล์ของตัวเอง / your own file, e.g. "./targets/shirt.mind"

  // โมเดล .glb (ไม่บังคับ) ถ้าเว้นว่างจะแสดงรูปทรง 3D ที่สร้างในโค้ด
  // Optional .glb model. Leave empty to show the built-in 3D shape.
  modelSrc: "", // e.g. "./models/mascot.glb"
  modelScale: 0.5,

  // ข้อความที่ลอยเหนือลายเสื้อ / Text floating above the print
  label: "Hello AR Shirt!",
};
