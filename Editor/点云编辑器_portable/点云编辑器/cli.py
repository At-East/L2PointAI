# -*- coding: utf-8 -*-
"""
cli.py —— 命令行批量处理（不开界面，适合一次生成一堆"车体移位"的样本）

用法示例
    # 分离车体与轨道，并把车体横移 3.5 m、抬高 5 cm，另存
    python -m pcedit.cli in.csv out.csv --move 车体:0,3.5,0.05

    # 脱轨场景：车体横移 2.4 m + 落下 0.3 m + 偏转 8 度
    python -m pcedit.cli in.csv derail.csv --move 车体:0,2.4,-0.3 --rot 车体:8

    # 只导出车体（轨道丢掉），同时出前后对比图
    python -m pcedit.cli in.csv car_only.csv --hide 轨道 --shots preview/c70e

参数
    --split / --no-split      是否先自动分离（默认 split）
    --z-rail 0.001            轨面高度（不给就自动检测）
    --x-half 6.8  --y-half 1.6   车体足迹半长/半宽（不给就自动检测）
    --move 组名:dx,dy,dz      平移某个组，可重复
    --rot  组名:角度          绕 Z 旋转某个组（绕该组包围盒中心），可重复
    --reset 组名              复位某个组，可重复
    --hide 组名               导出时丢弃该组的点，可重复
    --shots 前缀              输出 <前缀>_before.png / <前缀>_after.png
"""
from __future__ import annotations

import argparse
import os
import sys

import numpy as np

try:
    from . import core
except ImportError:
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import core


def parse_pair(s):
    if ":" not in s:
        raise argparse.ArgumentTypeError("要写成 组名:数值，例如 车体:0,3.5,0")
    name, val = s.split(":", 1)
    return name.strip(), val.strip()


def parse_vec(s):
    parts = [p for p in s.replace("，", ",").split(",") if p.strip()]
    v = [float(p) for p in parts]
    while len(v) < 3:
        v.append(0.0)
    return v[:3]


def shot(cloud, path, point_size=1):
    lo, hi = cloud.pts.min(0), cloud.pts.max(0)
    cam = core.Camera(yaw=40, pitch=24)
    cam.fit(cloud.world_points(), 1400, 880)
    grid = dict(x0=lo[0] - 2, x1=hi[0] + 2, y0=lo[1] - 1.5, y1=hi[1] + 1.5, step=1.0)
    img, _ = core.render(cloud, cam, 1400, 880, point_size=point_size, grid=grid)
    os.makedirs(os.path.dirname(os.path.abspath(path)) or ".", exist_ok=True)
    img.save(path)
    print("  图", path)


def main(argv=None):
    ap = argparse.ArgumentParser(description="点云编辑器 · 命令行批量分离/移位")
    ap.add_argument("input")
    ap.add_argument("output")
    ap.add_argument("--split", dest="split", action="store_true", default=True)
    ap.add_argument("--no-split", dest="split", action="store_false")
    ap.add_argument("--z-rail", type=float, default=None)
    ap.add_argument("--y-rail", type=float, default=None)
    ap.add_argument("--x-half", type=float, default=None)
    ap.add_argument("--y-half", type=float, default=None)
    ap.add_argument("--drop", type=float, default=0.12, help="道床切深（米）")
    ap.add_argument("--move", action="append", default=[], metavar="组:dx,dy,dz")
    ap.add_argument("--rot", action="append", default=[], metavar="组:角度")
    ap.add_argument("--reset", action="append", default=[], metavar="组")
    ap.add_argument("--hide", action="append", default=[], metavar="组")
    ap.add_argument("--shots", default=None, help="输出对比图的前缀")
    ap.add_argument("--point-size", type=int, default=1)
    args = ap.parse_args(argv)

    cloud = core.load_cloud(args.input)
    print(f"读取 {args.input}: {len(cloud.pts):,} 点")

    if args.split:
        g = core.detect_geometry(cloud.pts)
        _, rep = core.auto_split(
            cloud,
            z_rail=args.z_rail if args.z_rail is not None else g["z_rail"],
            y_rail=args.y_rail if args.y_rail is not None else g["y_rail"],
            x_half=args.x_half if args.x_half is not None else g["x_half"],
            y_half=args.y_half if args.y_half is not None else g["y_half"],
            ballast_drop=args.drop)
        for name, n, _ in rep["groups"]:
            print(f"  {name}: {n:,} 点")

    if args.shots:
        shot(cloud, f"{args.shots}_before.png", args.point_size)

    idx = {g.name: i for i, g in enumerate(cloud.groups)}

    def gid(name):
        if name not in idx:
            raise SystemExit(f"没有名为「{name}」的组，现有：{list(idx)}")
        return idx[name]

    for item in args.reset:
        cloud.groups[gid(item)].reset()
    for item in args.move:
        name, val = parse_pair(item)
        d = parse_vec(val)
        cloud.groups[gid(name)].translate(d)
        print(f"  平移 {name} ({d[0]:+.2f}, {d[1]:+.2f}, {d[2]:+.2f}) m")
    for item in args.rot:
        name, val = parse_pair(item)
        gi = gid(name)
        p = cloud.group_points(gi)
        pivot = (p.min(0) + p.max(0)) / 2 if len(p) else np.zeros(3)
        cloud.groups[gi].rotate_z(float(val), pivot)
        print(f"  旋转 {name} {float(val):+.1f}°")

    hidden = set(args.hide)
    for h in hidden:
        gid(h)
        cloud.groups[idx[h]].visible = False

    keep = np.array([cloud.groups[i].name not in hidden for i in cloud.labels])
    wp = cloud.world_points()
    sub = core.Cloud(wp[keep], None if cloud.intensity is None else cloud.intensity[keep])
    core.save_cloud(args.output, sub, with_intensity=cloud.intensity is not None)
    print(f"写出 {args.output}: {int(keep.sum()):,} 点  {os.path.getsize(args.output)/1e6:.2f} MB")

    if args.shots:
        shot(cloud, f"{args.shots}_after.png", args.point_size)
    return 0


if __name__ == "__main__":
    sys.exit(main())
