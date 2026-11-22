import math, os
rows = []
# room 6 x 5 x 2.6 m with a box, subsampled to ~20k points
def add(x, y, z, i): rows.append((x, y, z, i))
step = 0.09
px = 0.0
while px <= 6.0:
    py = 0.0
    while py <= 5.0:
        add(px, py, 0.0, 120 + 40 * math.sin(px))
        py += step
    px += step
pz = 0.0
while pz <= 2.6:
    px = 0.0
    while px <= 6.0:
        add(px, 0.0, pz, 200); add(px, 5.0, pz, 200)
        px += step
    py = 0.0
    while py <= 5.0:
        add(0.0, py, pz, 180); add(6.0, py, pz, 180)
        py += step
    pz += step
bx = by = bz = 0.0
while bx <= 1.2:
    by = 0.0
    while by <= 1.1:
        add(3.0 + bx, 2.0 + by, 0.85, 240)
        by += 0.06
    bx += 0.06
while bz <= 0.85:
    bx = 0.0
    while bx <= 1.2:
        add(3.0 + bx, 2.0, bz, 230); add(3.0 + bx, 3.1, bz, 230)
        bx += 0.06
    bz += 0.06

out = r"E:\文档\unilidar_l2\L2PointAI\assets\sample_room.csv"
with open(out, "w", encoding="utf-8-sig", newline="") as f:
    f.write("frame,seq,stamp_unix,x_m,y_m,z_m,intensity,time_s\n")
    for k, (x, y, z, i) in enumerate(rows):
        f.write("1,0,1730724860.5,%.4f,%.4f,%.4f,%d,0\n" % (x, y, z, int(i)))
print("points:", len(rows), "bytes:", os.path.getsize(out))
