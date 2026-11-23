# -*- coding: utf-8 -*-
"""
Synthetic LiDAR-like point cloud: C70E open-top gondola wagon on 60 kg/m rails.
Frame: X = along track, Y = lateral, Z = up, Z=0 at rail top. Units: metres.
Dimensions from public C70E data: length 13.976 m, bogie centres 9.21 m, max width 3.18 m,
inner height 2.15 m, floor 1.093 m above rail, coupler centre 0.88 m, wheel dia 0.84 m.
"""
import os
import numpy as np

rng = np.random.default_rng(20261123)
DENSITY = 900.0          # points per m^2 baseline
NOISE = 0.0015           # 1.5 mm scanner noise

parts = []               # list of (points Nx3, intensity per point)


def add(pts, intensity):
    if len(pts) == 0:
        return
    parts.append((np.asarray(pts, dtype=np.float64), float(intensity)))


def n_of(area, mult=1.0):
    return max(4, int(round(area * DENSITY * mult)))


def quad(p0, p1, p2, p3, n, rng):
    u = rng.random(n)[:, None]
    v = rng.random(n)[:, None]
    p0, p1, p2, p3 = map(np.asarray, (p0, p1, p2, p3))
    return (p0 * (1 - u) * (1 - v) + p1 * u * (1 - v) + p2 * u * v + p3 * (1 - u) * v)


def box(x0, x1, y0, y1, z0, z1, rng, mult=1.0, skip_bottom=False):
    """Return points on the 6 faces of an axis aligned box."""
    dx, dy, dz = x1 - x0, y1 - y0, z1 - z0
    out = []
    a_side_x = dy * dz
    a_side_y = dx * dz
    a_top = dx * dy
    for x in (x0, x1):
        out.append(quad((x, y0, z0), (x, y1, z0), (x, y1, z1), (x, y0, z1), n_of(a_side_x, mult), rng))
    for y in (y0, y1):
        out.append(quad((x0, y, z0), (x1, y, z0), (x1, y, z1), (x0, y, z1), n_of(a_side_y, mult), rng))
    out.append(quad((x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1), n_of(a_top, mult), rng))
    if not skip_bottom:
        out.append(quad((x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), n_of(a_top, mult), rng))
    return np.vstack(out)


def tube(center, axis, length, radius, rng, mult=1.0, caps=True, sides=24):
    """Cylinder surface (axis: 0=x,1=y,2=z)."""
    c = np.asarray(center, dtype=float)
    n = n_of(2 * np.pi * radius * length, mult)
    t = rng.uniform(-length / 2, length / 2, n)
    a = rng.uniform(0, 2 * np.pi, n)
    u, v = radius * np.cos(a), radius * np.sin(a)
    pts = np.zeros((n, 3))
    if axis == 0:
        pts[:, 0] = t; pts[:, 1] = u; pts[:, 2] = v
    elif axis == 1:
        pts[:, 1] = t; pts[:, 0] = u; pts[:, 2] = v
    else:
        pts[:, 2] = t; pts[:, 0] = u; pts[:, 1] = v
    pts += c
    out = [pts]
    if caps:
        for s in (-1, 1):
            m = max(4, int(n * radius / max(length, 1e-6) * 0.5))
            r = radius * np.sqrt(rng.random(m))
            a2 = rng.uniform(0, 2 * np.pi, m)
            cap = np.zeros((m, 3))
            if axis == 0:
                cap[:, 0] = s * length / 2; cap[:, 1] = r * np.cos(a2); cap[:, 2] = r * np.sin(a2)
            elif axis == 1:
                cap[:, 1] = s * length / 2; cap[:, 0] = r * np.cos(a2); cap[:, 2] = r * np.sin(a2)
            else:
                cap[:, 2] = s * length / 2; cap[:, 0] = r * np.cos(a2); cap[:, 1] = r * np.sin(a2)
            out.append(cap + c)
    return np.vstack(out)


def extrude_profile(profile_yz, x0, x1, rng, mult=1.0):
    """Extrude a closed (y,z) polygon along X; sample its lateral surface."""
    pts = []
    L = x1 - x0
    prof = np.asarray(profile_yz)
    for i in range(len(prof)):
        y0, z0 = prof[i]
        y1, z1 = prof[(i + 1) % len(prof)]
        seg = np.hypot(y1 - y0, z1 - z0)
        n = n_of(seg * L, mult)
        x = rng.uniform(x0, x1, n)
        t = rng.random(n)
        pts.append(np.column_stack([x, y0 + (y1 - y0) * t, z0 + (z1 - z0) * t]))
    return np.vstack(pts)


# ----------------------------------------------------------------- rails
RAIL_PROFILE = [
    (36.5, 0), (36.5, -38), (10.0, -52), (8.25, -70), (8.25, -120),
    (70.0, -140), (75.0, -152), (75.0, -176), (-75.0, -176), (-75.0, -152),
    (-70.0, -140), (-8.25, -120), (-8.25, -70), (-10.0, -52), (-36.5, -38), (-36.5, 0),
]
RAIL_PROFILE = [(y / 1000.0, z / 1000.0) for y, z in RAIL_PROFILE]
GAUGE = 1.435
for yc in (-GAUGE / 2, GAUGE / 2):
    prof = [(y + yc, z) for y, z in RAIL_PROFILE]
    add(extrude_profile(prof, -15.0, 15.0, rng, 1.4), 205)

# ----------------------------------------------------------------- sleepers + ballast
SL_TOP, SL_THK, SL_W, SL_SP = -0.176, 0.18, 0.26, 0.60
for x in np.arange(-15.0, 15.0, SL_SP):
    add(box(x - SL_W / 2, x + SL_W / 2, -1.25, 1.25, SL_TOP - SL_THK, SL_TOP, rng, 0.8, skip_bottom=True), 105)

# ballast: trapezoid, top -0.356, half width 2.0 -> 3.2 at -0.75
zb_top, zb_bot = SL_TOP - SL_THK, -0.75
for side in (-1, 1):
    slope = quad((x0 := -15.0, side * 2.0, zb_top), (15.0, side * 2.0, zb_top),
                 (15.0, side * 3.2, zb_bot), (-15.0, side * 3.2, zb_bot),
                 n_of(30 * 1.0, 0.8), rng)
    add(slope, 70)
add(quad((-15, -2.0, zb_top), (15, -2.0, zb_top), (15, 2.0, zb_top), (-15, 2.0, zb_top), n_of(30 * 4.0, 0.55), rng), 78)

# ----------------------------------------------------------------- wagon body
BX, BY = 6.5, 1.445           # inner half length / half width
TH = 0.06                     # plate thickness
Z_FLOOR = 1.093
Z_TOP = 3.253
add(box(-BX, BX, -BY, BY, Z_FLOOR - 0.02, Z_FLOOR, rng, 0.9, skip_bottom=True), 112)   # floor plate
for sy in (-1, 1):                                                                     # side walls
    y0, y1 = sy * BY, sy * (BY + TH)
    add(box(-BX, BX, min(y0, y1), max(y0, y1), Z_FLOOR, Z_TOP, rng, 1.2), 122)
    # vertical ribs (outside)
    for x in np.arange(-BX + 0.35, BX, 0.98):
        add(box(x - 0.075, x + 0.075, sy * (BY + TH), sy * (BY + TH + 0.09), Z_FLOOR + 0.03, Z_TOP - 0.05, rng, 1.6), 132)
    # top rail (140x100 RHS)
    add(box(-BX, BX, sy * (BY - 0.01), sy * (BY + 0.16), Z_TOP - 0.10, Z_TOP, rng, 1.3), 140)
    # side door frames: 4 doors per side
    for x in np.arange(-BX + 0.6, BX - 1.0, 2.2):
        add(box(x, x + 1.7, sy * (BY + TH), sy * (BY + TH + 0.02), Z_FLOOR + 0.05, Z_FLOOR + 0.95, rng, 1.8), 150)
for sx in (-1, 1):                                                                     # end walls
    x0, x1 = sx * BX, sx * (BX + TH)
    add(box(min(x0, x1), max(x0, x1), -BY - TH, BY + TH, Z_FLOOR, Z_TOP, rng, 1.2), 122)
    # end wall horizontal ribs
    for z in np.arange(Z_FLOOR + 0.35, Z_TOP - 0.1, 0.42):
        add(box(sx * (BX + TH), sx * (BX + TH + 0.08), -BY, BY, z, z + 0.10, rng, 1.6), 132)
    # corner posts
    for sy in (-1, 1):
        add(box(sx * (BX - 0.02), sx * (BX + TH + 0.10), sy * (BY - 0.02), sy * (BY + TH + 0.10),
                Z_FLOOR, Z_TOP + 0.02, rng, 1.6), 140)

# underframe: centre sill + side sills + cross bearers
add(box(-BX, BX, -0.16, 0.16, Z_FLOOR - 0.33, Z_FLOOR - 0.02, rng, 0.8), 92)
for sy in (-1, 1):
    add(box(-BX, BX, sy * (BY - 0.18), sy * (BY - 0.02), Z_FLOOR - 0.26, Z_FLOOR - 0.02, rng, 0.7), 92)
for x in np.arange(-BX + 0.8, BX - 0.5, 1.5):
    add(box(x - 0.09, x + 0.09, -BY, BY, Z_FLOOR - 0.22, Z_FLOOR - 0.02, rng, 0.8), 88)

# ----------------------------------------------------------------- couplers (both ends)
for sx in (-1, 1):
    x_start = sx * (BX + TH)
    # draft gear housing
    add(box(min(x_start, sx * (BX + TH + 0.42)), max(x_start, sx * (BX + TH + 0.42)),
            -0.20, 0.20, 0.70, 1.06, rng, 1.2), 165)
    # shank (coupler body), centreline 0.88
    add(box(min(sx * (BX + 0.40), sx * 6.78), max(sx * (BX + 0.40), sx * 6.78),
            -0.075, 0.075, 0.805, 0.955, rng, 1.3), 175)
    # head + knuckle
    add(box(min(sx * 6.78, sx * 6.99), max(sx * 6.78, sx * 6.99),
            -0.145, 0.145, 0.73, 1.03, rng, 1.6), 180)
    add(tube((sx * 6.90, 0.10, 0.88), 2, 0.20, 0.055, rng, 0.6), 180)
    # coupler carrier / yoke
    add(box(min(sx * (BX + 0.05), sx * (BX + 0.45)), max(sx * (BX + 0.05), sx * (BX + 0.45)),
            -0.24, 0.24, 0.62, 0.72, rng, 1.0), 150)

# ----------------------------------------------------------------- bogies
BOGIE_X = 9.21 / 2          # 4.605
AXLE_DX = 1.83 / 2          # 0.915
WHEEL_R = 0.42
for sx in (-1, 1):
    bx = sx * BOGIE_X
    for sy in (-1, 1):      # side frames
        add(box(bx - 1.15, bx + 1.15, sy * 0.97, sy * 1.10, 0.62, 0.90, rng, 1.3), 155)
        for dx in (-AXLE_DX, AXLE_DX):   # axle boxes / springs
            add(box(bx + dx - 0.13, bx + dx + 0.13, sy * 0.95, sy * 1.13, 0.90, 1.16, rng, 1.5), 150)
            add(tube((bx + dx, sy * 1.04, 1.02), 2, 0.20, 0.085, rng, 0.9), 145)
    add(box(bx - 0.22, bx + 0.22, -1.02, 1.02, 0.90, 1.14, rng, 1.2), 150)   # bolster
    for dx in (-AXLE_DX, AXLE_DX):                                          # axles + wheels
        add(tube((bx + dx, 0.0, WHEEL_R), 1, 1.40, 0.07, rng, 0.9), 185)
        for sy in (-1, 1):
            add(tube((bx + dx, sy * GAUGE / 2, WHEEL_R), 1, 0.135, WHEEL_R, rng, 1.8), 190)
            add(tube((bx + dx, sy * (GAUGE / 2 - 0.09), WHEEL_R), 1, 0.02, WHEEL_R + 0.022, rng, 1.2), 195)

# ----------------------------------------------------------------- assemble
pts_all = np.vstack([p for p, _ in parts])
int_all = np.concatenate([np.full(len(p), i) for p, i in parts])

# scanner-like noise
pts_all += rng.normal(0, NOISE, pts_all.shape)

TARGET = 400_000
if len(pts_all) > TARGET:
    idx = rng.choice(len(pts_all), TARGET, replace=False)
    pts_all, int_all = pts_all[idx], int_all[idx]
elif len(pts_all) < TARGET:
    extra = rng.choice(len(pts_all), TARGET - len(pts_all), replace=True)
    pts_all = np.vstack([pts_all, pts_all[extra] + rng.normal(0, NOISE, (len(extra), 3))])
    int_all = np.concatenate([int_all, int_all[extra]])

# mild intensity variation (reflectance noise)
int_all = np.clip(int_all + rng.normal(0, 6, len(int_all)), 0, 255)

order = np.argsort(pts_all[:, 0], kind="stable")
pts_all, int_all = pts_all[order], int_all[order]

out_dir = r"E:\文档\unilidar_l2\L2PointAI\assets"
os.makedirs(out_dir, exist_ok=True)
ply_path = os.path.join(out_dir, "rail_wagon_c70e.ply")
csv_path = os.path.join(out_dir, "rail_wagon_c70e.csv")

with open(ply_path, "w", encoding="ascii", newline="\n") as f:
    f.write("ply\nformat ascii 1.0\ncomment C70E gondola wagon on 60kg/m rails, coupler-end view\n")
    f.write("element vertex %d\n" % len(pts_all))
    f.write("property float x\nproperty float y\nproperty float z\nproperty float intensity\nend_header\n")
    for (x, y, z), i in zip(pts_all, int_all):
        f.write("%.4f %.4f %.4f %.0f\n" % (x, y, z, i))

with open(csv_path, "w", encoding="utf-8-sig", newline="") as f:
    f.write("x,y,z,intensity\n")
    for (x, y, z), i in zip(pts_all, int_all):
        f.write("%.4f,%.4f,%.4f,%.0f\n" % (x, y, z, i))

np.save(os.path.join(out_dir, "_rail_wagon_points.npy"), pts_all)
np.save(os.path.join(out_dir, "_rail_wagon_intensity.npy"), int_all)
print("points:", len(pts_all))
print("bbox x %.2f..%.2f  y %.2f..%.2f  z %.2f..%.2f" % (
    pts_all[:, 0].min(), pts_all[:, 0].max(), pts_all[:, 1].min(), pts_all[:, 1].max(),
    pts_all[:, 2].min(), pts_all[:, 2].max()))
print("ply:", ply_path, os.path.getsize(ply_path) // 1024, "KB")
print("csv:", csv_path, os.path.getsize(csv_path) // 1024, "KB")
