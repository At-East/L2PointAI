# -*- coding: utf-8 -*-
"""
core.py —— 点云编辑器内核（不含界面）

职责：
  * 读取/保存点云（CSV / PLY / XYZ，自动识别列名）
  * 点云分组（车体 / 走行部 / 轨道 ...），每组一个 4x4 变换
  * 自动分离：按"轨面高度 + 钢轨带 + 车体足迹"把车辆和轨道拆开
  * 软件 z-buffer 渲染（numpy，无 OpenGL）
  * 框选 / 点选 / 邻域选择
  * 导出：整幅 CSV/PLY、按组导出、保存工程

界面在 ui.py，本模块可以脱离 tkinter 单独调用（便于脚本化批处理）。
"""
from __future__ import annotations

import json
import math
import os
from dataclasses import dataclass, field

import numpy as np
from PIL import Image

# ---------------------------------------------------------------- 配色
GROUP_COLORS = [
    (95, 175, 255),    # 车体
    (255, 168, 70),    # 走行部
    (120, 132, 152),   # 轨道
    (235, 95, 200),    # 未分类 / 其他
    (90, 220, 165),
    (245, 225, 95),
    (165, 125, 255),
    (205, 205, 205),
]
HL_COLOR = (255, 245, 140)      # 选中高亮
BG = (14, 15, 19)


# ---------------------------------------------------------------- 分组
@dataclass
class Group:
    name: str
    color: tuple
    count: int = 0
    visible: bool = True
    locked: bool = False
    M: np.ndarray = field(default_factory=lambda: np.eye(4, dtype=np.float64))

    def translate(self, d):
        T = np.eye(4)
        T[:3, 3] = d
        self.M = T @ self.M

    def rotate_z(self, deg, pivot):
        a = math.radians(deg)
        c, s = math.cos(a), math.sin(a)
        R = np.eye(4)
        R[0, 0], R[0, 1], R[1, 0], R[1, 1] = c, -s, s, c
        P = np.eye(4)
        P[:3, 3] = -np.asarray(pivot, float)
        Q = np.eye(4)
        Q[:3, 3] = np.asarray(pivot, float)
        self.M = Q @ R @ P @ self.M

    def reset(self):
        self.M = np.eye(4)


# ---------------------------------------------------------------- 点云
class Cloud:
    def __init__(self, pts, intensity=None, groups=None, labels=None, path=None):
        self.pts = np.asarray(pts, dtype=np.float32)
        self.intensity = None if intensity is None else np.asarray(intensity, dtype=np.float32)
        self.path = path
        n = len(self.pts)
        if groups is None:
            groups = [Group("未分类", GROUP_COLORS[0])]
        self.groups = groups
        self.labels = np.zeros(n, np.int16) if labels is None else np.asarray(labels, np.int16)
        self._refresh()

    # -- 基本
    def _refresh(self):
        c = np.bincount(np.clip(self.labels, 0, None), minlength=len(self.groups))
        for i, g in enumerate(self.groups):
            g.count = int(c[i]) if i < len(c) else 0

    def ensure_group(self, name, color=None):
        for i, g in enumerate(self.groups):
            if g.name == name:
                return i
        i = len(self.groups)
        self.groups.append(Group(name, color or GROUP_COLORS[i % len(GROUP_COLORS)]))
        return i

    def assign(self, mask, gid):
        self.labels[mask] = gid
        self._refresh()

    def counts(self):
        return [(g.name, g.count) for g in self.groups]

    # -- 坐标
    def world_points(self):
        out = np.empty_like(self.pts)
        for i, g in enumerate(self.groups):
            m = self.labels == i
            if not m.any():
                continue
            if np.allclose(g.M, np.eye(4)):
                out[m] = self.pts[m]
            else:
                out[m] = self.pts[m] @ g.M[:3, :3].T + g.M[:3, 3]
        return out

    def bbox(self, mask=None):
        p = self.pts if mask is None else self.pts[mask]
        return p.min(0), p.max(0)

    def group_points(self, gid):
        return self.pts[self.labels == gid]

    # -- 撤销用快照
    def snapshot(self):
        return (self.labels.copy(), [g.M.copy() for g in self.groups],
                [(g.name, g.color, g.visible, g.locked) for g in self.groups])

    def restore(self, snap):
        labels, mats, meta = snap
        self.labels = labels.copy()
        if len(meta) != len(self.groups):
            self.groups = [Group(n, c, visible=v, locked=l) for (n, c, v, l) in meta]
        for g, m in zip(self.groups, mats):
            g.M = m.copy()
        for g, (n, c, v, l) in zip(self.groups, meta):
            g.visible, g.locked = v, l
        self._refresh()


# ---------------------------------------------------------------- 读写
def load_cloud(path):
    ext = os.path.splitext(path)[1].lower()
    if ext == ".ply":
        return _load_ply(path)
    return _load_text(path)


def _load_ply(path):
    with open(path, "rb") as f:
        head = b""
        while True:
            line = f.readline()
            if not line:
                raise ValueError("PLY 头不完整")
            head += line
            if line.strip() == b"end_header":
                break
        text = head.decode("ascii", "replace")
        if "format ascii" not in text:
            raise ValueError("暂只支持 ascii 格式的 PLY（二进制请先转换）")
        n = int([l for l in text.splitlines() if l.startswith("element vertex")][0].split()[-1])
        names = [l.split()[-1] for l in text.splitlines() if l.startswith("property")]
        arr = np.loadtxt(f, max_rows=n, ndmin=2)
    idx = {n_: i for i, n_ in enumerate(names)}
    pts = np.stack([arr[:, idx["x"]], arr[:, idx["y"]], arr[:, idx["z"]]], 1)
    inten = arr[:, idx["intensity"]] if "intensity" in idx else None
    return Cloud(pts, inten, path=path)


def _is_number(s):
    try:
        float(s)
        return True
    except ValueError:
        return False


def _load_text(path):
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        header = f.readline().strip().lstrip("\ufeff")
    tokens = [t.strip().lower() for t in header.replace(";", ",").split(",") if t.strip()]
    has_header = len(tokens) > 0 and not _is_number(tokens[0])
    if has_header:
        cols = tokens
        arr = np.loadtxt(path, delimiter=",", skiprows=1, ndmin=2)
    else:
        # 无表头：按空白/逗号分隔的纯数值
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            first = f.readline()
        delim = "," if "," in first else None
        arr = np.loadtxt(path, delimiter=delim, ndmin=2)
        cols = ["x", "y", "z", "intensity", "i", "r"][: arr.shape[1]]
    idx = {c: i for i, c in enumerate(cols)}
    xi = idx.get("x", idx.get("x_m"))
    yi = idx.get("y", idx.get("y_m"))
    zi = idx.get("z", idx.get("z_m"))
    if xi is None or yi is None or zi is None:
        raise ValueError("找不到 x/y/z 列：%s" % cols)
    pts = np.stack([arr[:, xi], arr[:, yi], arr[:, zi]], 1)
    ii = None
    for key in ("intensity", "i", "reflectivity"):
        if key in idx:
            ii = arr[:, idx[key]]
            break
    return Cloud(pts, ii, path=path)


def save_cloud(path, cloud, with_intensity=True):
    """导出合并后的点云（应用所有分组的变换）。"""
    pts = cloud.world_points()
    ext = os.path.splitext(path)[1].lower()
    if ext == ".ply":
        with open(path, "w", encoding="ascii", newline="\n") as f:
            f.write("ply\nformat ascii 1.0\ncomment edited by PointCloudEditor\n")
            f.write(f"element vertex {len(pts)}\n")
            f.write("property float x\nproperty float y\nproperty float z\n")
            if with_intensity and cloud.intensity is not None:
                f.write("property uchar intensity\n")
            f.write("end_header\n")
            it = cloud.intensity if cloud.intensity is not None else np.zeros(len(pts))
            for i in range(len(pts)):
                x, y, z = pts[i]
                if with_intensity and cloud.intensity is not None:
                    f.write(f"{x:.4f} {y:.4f} {z:.4f} {int(np.clip(it[i],0,255))}\n")
                else:
                    f.write(f"{x:.4f} {y:.4f} {z:.4f}\n")
    else:
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            if with_intensity and cloud.intensity is not None:
                f.write("x,y,z,intensity\n")
                it = cloud.intensity
                for i in range(len(pts)):
                    x, y, z = pts[i]
                    f.write(f"{x:.4f},{y:.4f},{z:.4f},{int(np.clip(it[i],0,255))}\n")
            else:
                f.write("x,y,z\n")
                for x, y, z in pts:
                    f.write(f"{x:.4f},{y:.4f},{z:.4f}\n")


def save_group(path, cloud, gid):
    sub = Cloud(cloud.group_points(gid), None if cloud.intensity is None else cloud.intensity[cloud.labels == gid])
    sub.groups[0].M = cloud.groups[gid].M.copy()
    save_cloud(path, sub, with_intensity=cloud.intensity is not None)


def save_project(path, cloud):
    np.savez_compressed(path, pts=cloud.pts, labels=cloud.labels,
                        intensity=cloud.intensity if cloud.intensity is not None else np.zeros(0),
                        mats=np.stack([g.M for g in cloud.groups]),
                        meta=json.dumps([{"name": g.name, "color": list(g.color),
                                          "visible": g.visible, "locked": g.locked}
                                         for g in cloud.groups], ensure_ascii=False))


def load_project(path):
    z = np.load(path, allow_pickle=False)
    meta = json.loads(str(z["meta"]))
    groups = [Group(m["name"], tuple(m["color"]), visible=m["visible"], locked=m["locked"]) for m in meta]
    for g, M in zip(groups, z["mats"]):
        g.M = M.copy()
    inten = z["intensity"]
    c = Cloud(z["pts"], None if inten.size == 0 else inten, groups=groups, labels=z["labels"])
    return c


# ---------------------------------------------------------------- 自动分离
def _horizontal_top(pts, min_cover=0.85):
    """
    找"横贯整幅点云"的最高水平面 —— 钢轨顶面。

    做法：把点云按 (x, z) 做成占据网格，逐层看这一层的高度上有多少个
    x 栅格是"有货"的。钢轨沿 x 铺满整幅点云，覆盖率接近 100%；
    车体、车顶只占车长那一段，覆盖率不到一半，会被排除。
    这样即使车被挪开、转了个角度，也还能找准轨面，不会把车顶当轨面。
    """
    x, z = pts[:, 0], pts[:, 2]
    xlo, xhi = np.percentile(x, [0.5, 99.5])
    zlo = float(z.min())
    zhi = float(np.percentile(z, 99.9))
    if xhi - xlo < 1e-6 or zhi - zlo < 1e-6:
        return float(np.median(z))

    nx, nz = 80, 240
    ix = np.clip(((x - xlo) / (xhi - xlo) * nx).astype(np.int32), 0, nx - 1)
    iz = np.clip(((z - zlo) / (zhi - zlo) * nz).astype(np.int32), 0, nz - 1)
    grid = np.bincount(iz * nx + ix, minlength=nz * nx).reshape(nz, nx)
    cover = (grid >= 3).sum(1) / float(nx)          # 该层覆盖了多少条 x 栅格
    count = grid.sum(1)

    ok = np.flatnonzero((cover >= min_cover) & (count >= max(300, int(0.002 * len(z)))))
    k = int(ok.max()) if len(ok) else int(np.argmax(cover))
    band = z[iz == k]
    if band.size:
        return float(np.percentile(band, 90))
    return float(zlo + (k + 0.5) * (zhi - zlo) / nz)


def detect_geometry(pts):
    """从点云本身猜：轨面高度、钢轨中心线 y、车体足迹半长/半宽。"""
    x, y, z = pts[:, 0], pts[:, 1], pts[:, 2]
    z_rail = _horizontal_top(pts)

    # 钢轨中心线：贴着轨面那一层点的 |y| 中位数（标准轨 ≈0.7175）
    near = np.abs(z - z_rail) < 0.05
    yy = np.abs(y[near])
    yy = yy[(yy > 0.25) & (yy < 1.40)]
    y_rail = float(np.median(yy)) if len(yy) > 50 else 0.7175

    # 车体足迹：明显高于轨面的那部分的 xy 范围
    body = z > z_rail + 0.8
    if int(body.sum()) < 100:
        body = z > np.percentile(z, 90)
    x_half = float(np.percentile(np.abs(x[body]), 99.5))
    y_half = float(np.percentile(np.abs(y[body]), 99.5))

    # 道床顶面（仅用于报告）
    low = z[z < z_rail - 0.10]
    z_ballast = float(np.percentile(low, 25)) if low.size > 100 else z_rail - 0.20
    return dict(z_rail=z_rail, y_rail=y_rail, z_ballast=z_ballast,
                x_half=x_half, y_half=y_half)


def auto_split(cloud, z_rail=None, y_rail=None, x_half=None, y_half=None,
               rail_halfwidth=0.055, ballast_drop=0.12, margin=0.06, **_ignored):
    """
    把点云分成 车体 / 走行部 / 轨道 三组，返回 (labels, report)。

    规则（自下而上，先命中的赢）：
      1. z < 轨面 - 0.19            -> 轨道（道床、轨枕）
      2. 落在钢轨带里              -> 轨道（轨头、轨腰、轨底）
      3. z <= 轨面 且 在车体足迹外 -> 轨道（车体前后的空轨、道砟）
      4. z <= 轨面 且 在足迹内     -> 走行部（轮对、转向架、闸瓦）
      5. 其余                      -> 车体
    """
    g = detect_geometry(cloud.pts)
    z_rail = g["z_rail"] if z_rail is None else z_rail
    y_rail = g["y_rail"] if y_rail is None else y_rail
    x_half = g["x_half"] if x_half is None else x_half
    y_half = g["y_half"] if y_half is None else y_half

    p = cloud.pts
    z = p[:, 2]
    y = p[:, 1]
    x = p[:, 0]

    # 只有一个初始空壳组时，直接把它当"车体"，避免列表里留一个空组
    if len(cloud.groups) == 1 and cloud.groups[0].name in ("未分类", "全部点"):
        cloud.groups[0].name = "车体"

    i_body = cloud.ensure_group("车体", GROUP_COLORS[0])
    i_run = cloud.ensure_group("走行部", GROUP_COLORS[1])
    i_track = cloud.ensure_group("轨道", GROUP_COLORS[2])

    lab = np.full(len(p), i_body, np.int16)

    # 1) 道床 / 轨枕
    m_ballast = z < z_rail - ballast_drop
    lab[m_ballast] = i_track

    # 2) 钢轨带：两条 |y|≈y_rail、轨面以下的窄条
    near_rail = np.abs(np.abs(y) - y_rail) < rail_halfwidth
    m_rail = near_rail & (z <= z_rail + 0.012) & (z > z_rail - 0.22)
    lab[m_rail] = i_track
    # 轨底更宽（60kg/m 钢轨底宽 150mm）
    m_foot = (np.abs(np.abs(y) - y_rail) < 0.09) & (z <= z_rail - 0.13) & (z > z_rail - 0.22)
    lab[m_foot] = i_track

    # 3)+4) 轨面以下、车体足迹内外的点（不覆盖已经判成轨道的道床/钢轨）
    below = z <= z_rail + 0.012
    in_fp = below & (np.abs(x) <= x_half + margin) & (np.abs(y) <= y_half + 0.04)
    lab[below & ~in_fp] = i_track
    lab[in_fp & (lab == i_body)] = i_run

    cloud.labels = lab
    cloud._refresh()

    # 走行部太少（<1%）说明这份数据里没有独立可见的轮对/转向架：并回车体并删掉这个空组
    if np.count_nonzero(lab == i_run) < max(200, int(0.01 * len(lab))):
        lab[lab == i_run] = i_body
        lab[lab > i_run] -= 1                 # 后面的组索引往前挪一位
        cloud.groups.pop(i_run)
        i_track -= 1

    cloud.labels = lab
    cloud._refresh()

    def bb(gid):
        q = p[lab == gid]
        return None if len(q) == 0 else [q.min(0).round(2).tolist(), q.max(0).round(2).tolist()]

    report = dict(geom=dict(z_rail=round(z_rail, 3), y_rail=round(y_rail, 4),
                            x_half=round(x_half, 2), y_half=round(y_half, 2)),
                  groups=[(g.name, g.count, bb(i)) for i, g in enumerate(cloud.groups)])
    return lab, report


# ---------------------------------------------------------------- 连通域（按簇选择）
def voxel_components(pts, voxel=0.12, min_points=50):
    """体素连通域，返回每个点的簇编号（-1 表示小于 min_points 的碎簇）。"""
    key = np.floor(pts / voxel).astype(np.int64)
    key -= key.min(0)
    dims = key.max(0) + 1
    lin = (key[:, 0] * dims[1] + key[:, 1]) * dims[2] + key[:, 2]
    uq, inv = np.unique(lin, return_inverse=True)
    idx = {int(k): i for i, k in enumerate(uq)}
    offs = []
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for dz in (-1, 0, 1):
                if (dx, dy, dz) != (0, 0, 0):
                    offs.append(dx * dims[1] * dims[2] + dy * dims[2] + dz)
    lab = np.full(len(uq), -1, np.int32)
    n = 0
    from collections import deque
    for s in range(len(uq)):
        if lab[s] >= 0:
            continue
        lab[s] = n
        q = deque([s])
        while q:
            c = q.popleft()
            b = int(uq[c])
            for o in offs:
                j = idx.get(b + o, -1)
                if j >= 0 and lab[j] < 0:
                    lab[j] = n
                    q.append(j)
        n += 1
    plab = lab[inv]
    cnt = np.bincount(plab, minlength=n)
    small = cnt < min_points
    plab = np.where(small[plab], -1, plab).astype(np.int32)
    # 重编号
    u = np.unique(plab[plab >= 0])
    remap = {int(v): i for i, v in enumerate(u)}
    out = np.array([remap.get(int(v), -1) for v in plab], np.int32)
    return out


# ---------------------------------------------------------------- 渲染
class Camera:
    def __init__(self, target=(0, 0, 1), yaw=38.0, pitch=22.0, dist=42.0, fov=42.0):
        self.target = np.array(target, float)
        self.yaw = yaw            # 绕 Z
        self.pitch = pitch        # 仰角
        self.dist = dist
        self.fov = fov
        self.ortho = False

    def eye(self):
        y, p = math.radians(self.yaw), math.radians(self.pitch)
        d = self.dist
        return self.target + np.array([d * math.cos(p) * math.cos(y),
                                       d * math.cos(p) * math.sin(y),
                                       d * math.sin(p)])

    def basis(self):
        e = self.eye()
        f = self.target - e
        f /= np.linalg.norm(f)
        up = np.array([0, 0, 1.0]) if abs(f[2]) < 0.995 else np.array([0, 1.0, 0])
        r = np.cross(f, up)
        r /= np.linalg.norm(r)
        u = np.cross(r, f)
        return e, f, r, u

    def project(self, pts, W, H):
        e, f, r, u = self.basis()
        d = pts - e
        z = d @ f
        scale = (H * 0.5) / math.tan(math.radians(self.fov) * 0.5)
        with np.errstate(divide="ignore", invalid="ignore"):
            x = W * 0.5 + (d @ r) / z * scale
            y = H * 0.5 - (d @ u) / z * scale
        return x, y, z, scale

    def pan(self, dx_px, dy_px, W, H):
        e, f, r, u = self.basis()
        scale = (H * 0.5) / math.tan(math.radians(self.fov) * 0.5)
        k = self.dist / scale
        self.target += (-dx_px * k) * r + (dy_px * k) * u

    def fit(self, pts, W=1000, H=700, margin=0.86, iters=8):
        """把整片点云塞进画面：按投影后的屏幕包围盒迭代收敛（比按球半径估算准）。"""
        lo, hi = pts.min(0), pts.max(0)
        self.target = (lo + hi) / 2
        self.dist = max(float(np.linalg.norm(hi - lo)), 3.0)
        for _ in range(iters):
            x, y, z, _ = self.project(pts, W, H)
            ok = z > 0.05
            if not ok.any():
                self.dist *= 1.4
                continue
            fx = (x[ok].max() - x[ok].min()) / W
            fy = (y[ok].max() - y[ok].min()) / H
            f = max(fx, fy)
            if f < 1e-6:
                break
            self.dist *= f / margin
            if abs(f - margin) < 0.02:
                break
        self.dist = float(np.clip(self.dist, 1.0, 5000.0))


def _jet(t):
    t = np.clip(t, 0, 1)
    stops = [0.0, 0.25, 0.45, 0.62, 0.80, 1.0]
    cols = np.array([[20, 30, 100], [0, 100, 235], [0, 215, 220],
                     [60, 205, 65], [250, 205, 40], [235, 45, 35]], float)
    return np.stack([np.interp(t, stops, cols[:, k]) for k in range(3)], 1).astype(np.uint8)


def _draw_grid(img, cam, W, H, x0, x1, y0, y1, z=0.0, step=1.0, color=(30, 34, 42)):
    """把地面参考网格直接画进图像底层（点云随后覆盖上去，天然被遮挡）。"""
    segs = []
    for x in np.arange(np.floor(x0 / step) * step, x1 + step, step):
        segs.append(((x, y0, z), (x, y1, z)))
    for y in np.arange(np.floor(y0 / step) * step, y1 + step, step):
        segs.append(((x0, y, z), (x1, y, z)))
    col = np.array(color, np.uint8)
    for a, b in segs:
        a = np.asarray(a, float)
        b = np.asarray(b, float)
        n = max(2, int(np.linalg.norm(b - a) / 0.15))
        t = np.linspace(0, 1, n)[:, None]
        pts = a + (b - a) * t
        sx, sy, sz, _ = cam.project(pts, W, H)
        ok = sz > 0.05
        px = np.rint(sx[ok]).astype(np.int64)
        py = np.rint(sy[ok]).astype(np.int64)
        m = (px >= 0) & (px < W) & (py >= 0) & (py < H)
        img[py[m], px[m]] = col


def render(cloud, cam, W, H, color_mode="group", point_size=1, highlight=None,
           max_points=None, bg=BG, grid=None):
    """返回 (PIL.Image, 统计dict)。"""
    wp = cloud.world_points()
    labels = cloud.labels
    n = len(wp)

    if color_mode == "group":
        pal = np.array([g.color for g in cloud.groups], np.uint8)
        colors = pal[np.clip(labels, 0, len(pal) - 1)]
    elif color_mode == "height":
        colors = _jet((wp[:, 2] - wp[:, 2].min()) / max(np.ptp(wp[:, 2]), 1e-6))
    elif color_mode == "intensity" and cloud.intensity is not None:
        it = cloud.intensity.astype(np.float32)
        lo, hi = np.percentile(it, 2), np.percentile(it, 98)
        colors = _jet((it - lo) / max(hi - lo, 1e-6))
    else:
        colors = _jet((wp[:, 2] - wp[:, 2].min()) / max(np.ptp(wp[:, 2]), 1e-6))

    vis = np.ones(n, bool)
    for i, g in enumerate(cloud.groups):
        if not g.visible:
            vis &= labels != i
    if highlight is not None:
        colors = colors.copy()
        colors[highlight] = HL_COLOR

    idx = np.flatnonzero(vis)
    if max_points and len(idx) > max_points:
        idx = idx[:: max(1, len(idx) // max_points)]

    p = wp[idx]
    c = colors[idx]
    xs, ys, zs, _ = cam.project(p, W, H)
    ok = zs > 0.06
    idx2 = np.flatnonzero(ok)
    px = np.rint(xs[idx2]).astype(np.int32)
    py = np.rint(ys[idx2]).astype(np.int32)
    m = (px >= 0) & (px < W) & (py >= 0) & (py < H)
    px, py, zz, cc = px[m], py[m], zs[idx2][m], c[idx2][m]

    order = np.argsort(-zz)
    px, py, cc = px[order], py[order], cc[order]
    zz = zz[order]

    # 深度遮蔽：远处点压暗，近处保持原色（点云看起来更有层次）
    z0, z1 = float(zz.min()), float(np.percentile(zz, 98))
    shade = 0.42 + 0.58 * (1.0 - np.clip((zz - z0) / max(z1 - z0, 1e-6), 0, 1))
    cc = np.clip(cc.astype(np.float32) * shade[:, None], 0, 255).astype(np.uint8)

    img = np.empty((H, W, 3), np.uint8)
    img[:] = bg
    if grid:
        g = dict(x0=-5, x1=5, y0=-5, y1=5, z=0.0, step=1.0)
        g.update(grid)
        _draw_grid(img, cam, W, H, g["x0"], g["x1"], g["y0"], g["y1"],
                   g["z"], g["step"], g.get("color", (30, 34, 42)))
    for dx in range(point_size):
        for dy in range(point_size):
            xx = px + dx
            yy = py + dy
            k = (xx < W) & (yy < H)
            img[yy[k], xx[k]] = cc[k]
    return Image.fromarray(img), dict(points=int(len(px)))


def pick(cloud, cam, W, H, mx, my, radius=9, only_visible=True):
    """屏幕拾取：返回 (index, distance_px) 或 (None, None)。"""
    wp = cloud.world_points()
    xs, ys, zs, _ = cam.project(wp, W, H)
    m = zs > 0.06
    if only_visible:
        for i, g in enumerate(cloud.groups):
            if not g.visible:
                m &= cloud.labels != i
    d2 = (xs - mx) ** 2 + (ys - my) ** 2
    d2 = np.where(m, d2, np.inf)
    j = int(np.argmin(d2))
    if not np.isfinite(d2[j]) or d2[j] > radius ** 2:
        return None, None
    return j, math.sqrt(float(d2[j]))


def rect_select(cloud, cam, W, H, x0, y0, x1, y1, depth_test=True, only_visible=True):
    """屏幕矩形框选，返回布尔掩码。depth_test=True 时只选最前面那层点。"""
    wp = cloud.world_points()
    xs, ys, zs, _ = cam.project(wp, W, H)
    lo_x, hi_x = min(x0, x1), max(x0, x1)
    lo_y, hi_y = min(y0, y1), max(y0, y1)
    m = (xs >= lo_x) & (xs <= hi_x) & (ys >= lo_y) & (ys <= hi_y) & (zs > 0.06)
    if only_visible:
        for i, g in enumerate(cloud.groups):
            if not g.visible:
                m &= cloud.labels != i
    if depth_test and m.any():
        # 把框内点投到像素网格，每个像素只保留最近的点
        ix = np.rint(xs[m]).astype(np.int64)
        iy = np.rint(ys[m]).astype(np.int64)
        key = iy * (W + 2) + ix
        ord_ = np.argsort(key * 1e6 + zs[m])       # 同像素内按深度升序
        k2 = key[ord_]
        first = np.ones(len(k2), bool)
        first[1:] = k2[1:] != k2[:-1]
        sel = np.zeros(len(wp), bool)
        sel[np.flatnonzero(m)[ord_][first]] = True
        m = sel
    return m


def sphere_select(cloud, idx, radius=0.12):
    """以某个点为中心的三维球邻域选择。"""
    p = cloud.pts[idx]
    d = np.linalg.norm(cloud.pts - p, axis=1)
    return d <= radius


# ---------------------------------------------------------------- 统计
def cloud_stats(pts):
    lo, hi = pts.min(0), pts.max(0)
    return dict(n=len(pts), size=(hi - lo).round(3).tolist(),
                lo=lo.round(3).tolist(), hi=hi.round(3).tolist())


def group_stats(cloud, gid):
    p = cloud.group_points(gid)
    if len(p) == 0:
        return None
    lo, hi = p.min(0), p.max(0)
    return dict(n=len(p), lo=lo.round(3).tolist(), hi=hi.round(3).tolist(),
                size=(hi - lo).round(3).tolist(), center=((lo + hi) / 2).round(3).tolist())
