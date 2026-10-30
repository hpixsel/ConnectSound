"""Renders the ConnectSound mark (three meters on an ink key) into every icon the build needs."""
import os, sys
from PIL import Image, ImageDraw

INK, SIDE, SIGNAL = (26, 31, 41, 255), (201, 206, 214, 255), (240, 162, 2, 255)

def mark(size, rounded=True, padding=0.0):
    scale = 4
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = s * padding
    k = s - 2 * pad
    if rounded:
        d.rounded_rectangle([pad, pad, pad + k, pad + k], radius=k * 0.22, fill=INK)
    else:
        d.rectangle([0, 0, s, s], fill=INK)
    bar, gap = k * 0.14, k * 0.08
    left = pad + (k - 3 * bar - 2 * gap) / 2
    bottom = pad + k * 0.76
    for i, h in enumerate((0.32, 0.54, 0.40)):
        x = left + i * (bar + gap)
        d.rounded_rectangle([x, bottom - k * h, x + bar, bottom], radius=bar / 2, fill=SIGNAL if i == 1 else SIDE)
    return img.resize((size, size), Image.LANCZOS)

root = sys.argv[1]
ui = os.path.join(root, "src/ConnectSound.UI/Assets")
mark(256).save(os.path.join(ui, "icon.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
mark(512).save(os.path.join(ui, "icon.png"))
mark(512).save(os.path.join(root, "docs/icon.png"))

res = os.path.join(root, "src/ConnectSound.Android/Resources")
for name, px in {"mdpi": 48, "hdpi": 72, "xhdpi": 96, "xxhdpi": 144, "xxxhdpi": 192}.items():
    folder = os.path.join(res, f"mipmap-{name}")
    os.makedirs(folder, exist_ok=True)
    mark(px).save(os.path.join(folder, "appicon.png"))
# Notification icon: white silhouette of the bars.
os.makedirs(os.path.join(res, "drawable"), exist_ok=True)
n = mark(96, padding=0)
alpha = Image.new("L", n.size, 0)
px = n.load()
for y in range(n.height):
    for x in range(n.width):
        r, g, b, a = px[x, y]
        if a and not (r < 60 and g < 60 and b < 70):
            alpha.putpixel((x, y), a)
white = Image.new("RGBA", n.size, (255, 255, 255, 0))
white.putalpha(alpha)
white.save(os.path.join(res, "drawable/ic_notification.png"))
print("icons written")
