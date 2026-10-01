# AR Shirt 👕

เว็บ AR สำหรับเสื้อ: เปิดกล้องมือถือเล็งไปที่ลายบนเสื้อ แล้วจะมีโมเดล 3D และข้อความลอยขึ้นมาบนลายนั้น
ใช้ [MindAR](https://hiukim.github.io/mind-ar-js-doc/) + [three.js](https://threejs.org/) เป็นเว็บธรรมดา ไม่ต้องลงแอป ไม่ต้อง build

A web AR page for a shirt: point a phone camera at the shirt print and a 3D model plus a text label appear on it.
Plain static site (MindAR + three.js), no app install and no build step.

> 🧪 Real-time webcam virtual try-on plan, Unity boilerplate and a working web version: [tryon/README.md](tryon/README.md)

## ลองเลย / Try it

1. เปิดรูปตัวอย่าง [card.png](https://cdn.jsdelivr.net/gh/hiukim/mind-ar-js@1.2.5/examples/image-tracking/assets/card-example/card.png) บนจอคอมหรือพิมพ์ออกมา (ใช้แทนลายเสื้อไปก่อน)
   Open or print the sample image above (stand-in for the shirt print).
2. เปิดหน้าเว็บบนมือถือผ่าน **https** (ดูหัวข้อ "เผยแพร่" ด้านล่าง) กด **เริ่ม / Start** แล้วอนุญาตกล้อง
   Open the page on a phone over **https**, tap **Start**, allow the camera.
3. เล็งกล้องไปที่รูป จะเห็นโมเดล 3D หมุนอยู่บนรูป
   Aim at the image and the 3D model appears.

## รันบนเครื่อง / Run locally

```bash
npx serve .          # หรือ / or: python3 -m http.server 8000
```

เปิด `http://localhost:3000` บนคอมได้เลย (localhost ใช้กล้องได้)
ถ้าจะทดสอบบนมือถือต้องเป็น https เช่นใช้ GitHub Pages หรือ `npx localtunnel --port 3000`
Camera access needs https on phones; localhost is fine on a computer.

## เผยแพร่ด้วย GitHub Pages / Publish with GitHub Pages

Settings → Pages → Source: *Deploy from a branch* → เลือก branch และโฟลเดอร์ `/ (root)` → Save
จะได้ลิงก์ `https://<username>.github.io/ar-shirt/` เอาไปทำ QR code ติดบนเสื้อได้

## เปลี่ยนลายเสื้อ / Use your own shirt print

1. เตรียมรูปลายเสื้อ (ลายที่มีรายละเอียดเยอะ มุมคม สีตัดกัน จะจับได้ดีกว่าลายเรียบ ๆ)
   Prepare the print image (detailed, high-contrast designs track best).
2. เข้า [MindAR Image Target Compiler](https://hiukim.github.io/mind-ar-js-doc/tools/compile) อัปโหลดรูป กด Start แล้วดาวน์โหลดไฟล์ `targets.mind`
   Compile it with the MindAR compiler and download `targets.mind`.
3. วางไฟล์ไว้ที่ `targets/shirt.mind` แล้วแก้ใน `config.js`:
   ```js
   imageTargetSrc: "./targets/shirt.mind",
   ```

## เปลี่ยนโมเดล 3D / Use your own 3D model

วางไฟล์ `.glb` ไว้ใน `models/` แล้วแก้ใน `config.js` (หาโมเดลฟรีได้จาก Sketchfab, Poly Pizza):
Put a `.glb` file in `models/` and set it in `config.js`:

```js
modelSrc: "./models/mascot.glb",
modelScale: 0.5,   // ปรับขนาด / adjust size
label: "ข้อความของคุณ",
```

## โครงสร้างไฟล์ / Files

| ไฟล์ / File | หน้าที่ / Purpose |
| --- | --- |
| `index.html` | หน้าเว็บและปุ่มเริ่ม / page and start button |
| `main.js` | ตั้งค่า AR, โหลดโมเดล, แอนิเมชัน / AR setup, model, animation |
| `config.js` | ลายเสื้อ โมเดล ข้อความ (แก้ไฟล์นี้) / target, model, label (edit this) |
| `style.css` | หน้าตา / styles |
| `targets/` | ไฟล์ `.mind` ของลายเสื้อ / compiled targets |
| `models/` | ไฟล์ `.glb` / 3D models |
