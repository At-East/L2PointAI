# -*- coding: utf-8 -*-
"""Render the generated wagon cloud: coupler-end view (requested) + an iso overview."""
import os
import numpy as np
from PIL import Image, ImageDraw, ImageFont

ASSETS = r"E:\文档\unilidar_l2\L2PointAI\assets"
DOCS = r"E:\文档\unilidar_l2\L2PointAI\docs"

pts = np.load(os.path.join(ASSETS, "_rail_wagon_points.npy"))
inten = np.load(os.path.join(ASSETS, "_rail_wagon_intensity.npy"))

STOPS = [(0.00, (48, 18, 59)), (0.15, (70, 107, 227)), (0.34, (35, 190, 190)),
         (0.52, (100, 220, 90)), (0.72, (250, 200, 80)), (1.00, (240, 80, 50))]


def palette(t):
    t = np.clip(t, 0, 1)
    out = np.zeros((len(t), 3), dtype=np.uint8)
    for i in range(len(STOPS) - 1):
        a, b = STOPS[i], STOPS[i + 1]
        m = (t >= a[0]) & (t <= b[0])
        if not m.any():
            continue
        f = (t[m] - a[0]) / (b[0] - a[0])
        for c in range(3):
            out[m, c] = (a[1][c] + (b[1][c] - a[1][c]) * f).astype(np.uint8)
    return out


def render(cam, target, W, H, fov_deg=60.0, title="", subtitle="", point_size=2):
    f = np.asarray(target, float) - np.asarray(cam, float)
    f /= np.linalg.norm(f)
    up = np.array([0.0, 0.0, 1.0])
    if abs(np.dot(f, up)) > 0.99:
        up = np.array([0.0, 1.0, 0.0])
    right = np.cross(f, up)
    right /= np.linalg.norm(right)
    up2 = np.cross(right, f)

    rel = pts - np.asarray(cam, float)
    xc = rel @ right
    yc = rel @ up2
    zc = rel @ f

    near = 0.15
    ok = zc > near
    fpix = 0.5 * W / np.tan(np.radians(fov_deg) / 2)
    sx = W / 2 + fpix * xc[ok] / zc[ok]
    sy = H / 2 - fpix * yc[ok] / zc[ok]
    depth = zc[ok]

    zbuf = np.full(W * H, np.inf, dtype=np.float64)
    col = np.zeros((W * H, 3), dtype=np.uint8)
    zmin, zmax = depth.min(), depth.max()
    shade = 1.0 - 0.45 * (depth - zmin) / max(zmax - zmin, 1e-9)   # distance shading

    t = (pts[ok, 2] - pts[:, 2].min()) / (pts[:, 2].max() - pts[:, 2].min())
    rgb = palette(t).astype(np.float64) * shade[:, None]

    for dx in range(point_size):
        for dy in range(point_size):
            xi = np.clip((sx + dx).astype(int), 0, W - 1)
            yi = np.clip((sy + dy).astype(int), 0, H - 1)
            flat = yi * W + xi
            order = np.argsort(-depth)          # far first, near overwrites
            flat_s, depth_s, rgb_s = flat[order], depth[order], rgb[order]
            np.minimum.at(zbuf, flat_s, depth_s)
            hit = depth_s <= zbuf[flat_s] + 1e-6
            col[flat_s[hit]] = rgb_s[hit].astype(np.uint8)

    img = Image.fromarray(col.reshape(H, W, 3), "RGB")
    d = ImageDraw.Draw(img)
    try:
        font = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 22)
        small = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 16)
    except Exception:
        font = small = ImageFont.load_default()
    d.text((18, 14), title, fill=(235, 240, 250), font=font)
    d.text((18, 46), subtitle, fill=(150, 170, 195), font=small)
    return img


def main():
    hmin, hmax = pts[:, 2].min(), pts[:, 2].max()
    colors = ""
    # 1) requested: coupler-end, eye height 1.5 m, horizontal
    img = render(cam=(12.0, 0.0, 1.5), target=(0.0, 0.0, 1.6), W=1200, H=750,
                 fov_deg=55.0, point_size=4,
                 title="C70E 敞车 · 车钩端视角（水平）",
                 subtitle="400,000 点 · 60 kg/m 钢轨 · 轨距 1435 mm · 相机高度 1.5 m")
    p1 = os.path.join(DOCS, "rail_wagon_coupler_view.png")
    img.save(p1)
    print("saved", p1, img.size)

    # 2) bonus overview
    img2 = render(cam=(13.5, 11.0, 8.0), target=(0.0, 0.0, 1.4), W=1200, H=750,
                  fov_deg=50.0, point_size=4,
                  title="C70E 敞车 + 轨道（等轴测参考）",
                  subtitle="同一份点云，仅相机不同")
    p2 = os.path.join(DOCS, "rail_wagon_iso.png")
    img2.save(p2)
    print("saved", p2, img2.size)


if __name__ == "__main__":
    main()
