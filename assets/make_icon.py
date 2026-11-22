import math, os
from PIL import Image, ImageDraw

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# rounded dark background
d.rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.22), fill=(17, 20, 26, 255))
# subtle inner border
d.rounded_rectangle([6, 6, S - 7, S - 7], radius=int(S * 0.21), outline=(46, 58, 74, 255), width=6)

cx, cy = S * 0.5, S * 0.58
# radar arcs
for k, r in enumerate([0.30, 0.42, 0.54]):
    r = r * S
    box = [cx - r, cy - r * 0.55, cx + r, cy + r * 0.55]
    style = (60, 90, 110, 210 - k * 40)
    d.arc(box, start=200, end=340, fill=style, width=8)

def hcolor(t):
    stops = [(0.0, (48, 18, 59)), (0.2, (70, 107, 227)), (0.45, (35, 190, 190)),
             (0.7, (100, 220, 90)), (0.88, (250, 200, 80)), (1.0, (240, 80, 50))]
    for i in range(len(stops) - 1):
        a, b = stops[i], stops[i + 1]
        if a[0] <= t <= b[0]:
            f = (t - a[0]) / (b[0] - a[0])
            return tuple(int(a[1][j] + (b[1][j] - a[1][j]) * f) for j in range(3))
    return stops[-1][1]

# dotted hemisphere
rings = 11
for ri in range(rings):
    el = (ri / (rings - 1.0)) * math.pi / 2
    rad = 0.52 * S * math.cos(el) * 0.62 + 0.02 * S
    yoff = -math.sin(el) * 0.30 * S
    n = max(8, int(22 * math.cos(el)) + 6)
    for k in range(n):
        ang = math.pi + (k / (n - 1.0)) * math.pi
        x = cx + rad * math.cos(ang)
        y = cy + yoff + rad * 0.55 * math.sin(ang) * 0.55
        t = ri / (rings - 1.0)
        r = max(3.2, 7.0 * (1 - 0.55 * t))
        d.ellipse([x - r, y - r, x + r, y + r], fill=hcolor(t) + (255,))

# base line
d.line([cx - 0.50 * S, cy + 0.03 * S, cx + 0.50 * S, cy + 0.03 * S], fill=(90, 130, 160, 220), width=7)

out = r"E:\文档\unilidar_l2\L2PointAI\assets\app.ico"
img.save(out, format="ICO", sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)])
png = r"E:\文档\unilidar_l2\L2PointAI\assets\app_icon.png"
img.resize((256, 256), Image.LANCZOS).save(png)
print("icon bytes:", os.path.getsize(out))
