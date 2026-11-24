# -*- coding: utf-8 -*-
"""
ui.py —— 点云编辑器界面（tkinter + 自绘 numpy 渲染）

启动：  python ui.py  [点云文件]
或双击：启动点云编辑器.bat

主要操作
    左键拖动      转视角          右键拖动   平移
    滚轮          缩放            空格/F     适应窗口
    工具：框选 / 点选 / 测距
    右侧「变换」把选中的组平移/旋转，可一键把车体从轨道上挪开
"""
from __future__ import annotations

import os
import sys
import time
import traceback

import numpy as np
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from PIL import Image, ImageTk

try:
    from . import core
except ImportError:                                   # 直接跑脚本
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import core

# ---------------------------------------------------------------- 主题
BG = "#15171c"
PANEL = "#1c1f26"
PANEL2 = "#23262f"
FG = "#d7dce5"
FG_DIM = "#8b93a3"
ACCENT = "#4a9eff"
BTN = "#2a2e38"
BTN_HOVER = "#343a46"
FONT = ("Microsoft YaHei UI", 9)
FONT_B = ("Microsoft YaHei UI", 9, "bold")
FONT_S = ("Microsoft YaHei UI", 8)


def mkbtn(parent, text, cmd, width=0, bg=BTN, fg=FG, font=FONT_B, pad=4):
    b = tk.Button(parent, text=text, command=cmd, bg=bg, fg=fg, font=font,
                  relief="flat", bd=0, padx=6, pady=pad, width=width,
                  activebackground=BTN_HOVER, activeforeground=FG,
                  cursor="hand2", highlightthickness=0)
    b.bind("<Enter>", lambda e: b.config(bg=BTN_HOVER))
    b.bind("<Leave>", lambda e: b.config(bg=bg))
    return b


def mkentry(parent, var, width=8):
    e = tk.Entry(parent, textvariable=var, width=width, bg="#0f1115", fg=FG,
                 insertbackground=FG, relief="flat", font=FONT,
                 highlightthickness=1, highlightbackground="#2c313c",
                 highlightcolor=ACCENT)
    return e


class App(tk.Tk):
    def __init__(self, initial=None):
        super().__init__()
        self.title("点云编辑器 — 车辆 / 轨道分离")
        self.geometry("1560x950")
        self.minsize(1180, 760)
        self.configure(bg=BG)

        self.cloud = None
        self.cam = core.Camera(yaw=38, pitch=24)
        self.sel = None
        self.undo_stack, self.redo_stack = [], []
        self._photo = None
        self._last_img = None
        self._drag = None
        self._box = None
        self._measure = []
        self._redraw_job = None
        self._fast = False
        self._hud = tk.StringVar(value="就绪：点「打开点云」选文件，或直接点顶栏「自动分离车/轨」")
        self._stat = tk.StringVar(value="")

        self.v_tool = tk.StringVar(value="orbit")
        self.v_color = tk.StringVar(value="group")
        self.v_step = tk.StringVar(value="0.5")
        self.v_target = tk.StringVar(value="")
        self.v_depth = tk.BooleanVar(value=True)
        self.v_psize = tk.IntVar(value=1)
        self.v_pick_r = tk.StringVar(value="0.12")
        self.v_grid = tk.BooleanVar(value=True)
        self.v_dx = tk.StringVar(value="0")
        self.v_dy = tk.StringVar(value="3.5")
        self.v_dz = tk.StringVar(value="0")
        self.v_rot = tk.StringVar(value="0")
        self.v_pivot = tk.StringVar(value="组中心")
        self.p_rail = tk.StringVar(value="0.0")
        self.p_yrail = tk.StringVar(value="0.72")
        self.p_xh = tk.StringVar(value="7.0")
        self.p_yh = tk.StringVar(value="1.6")
        self.p_drop = tk.StringVar(value="0.12")

        self._build()
        self._bind_keys()

        if initial:
            self.after(80, lambda: self.open_path(initial))
        self.protocol("WM_DELETE_WINDOW", self.destroy)

    # ------------------------------------------------------------ 布局
    def _build(self):
        # 顶栏
        bar = tk.Frame(self, bg=PANEL, height=44)
        bar.pack(side="top", fill="x")
        bar.pack_propagate(False)
        inner = tk.Frame(bar, bg=PANEL)
        inner.pack(side="left", padx=8)
        mkbtn(inner, "打开点云", self.open_file).pack(side="left", padx=(0, 4), pady=6)
        mkbtn(inner, "导出 CSV", lambda: self.export(".csv")).pack(side="left", padx=4)
        mkbtn(inner, "导出 PLY", lambda: self.export(".ply")).pack(side="left", padx=4)
        tk.Frame(inner, bg="#333844", width=1).pack(side="left", fill="y", padx=8, pady=8)
        mkbtn(inner, "自动分离车/轨", self.auto_split).pack(side="left", padx=4)
        tk.Frame(inner, bg="#333844", width=1).pack(side="left", fill="y", padx=8, pady=8)
        mkbtn(inner, "撤销", self.undo, width=5).pack(side="left", padx=4)
        mkbtn(inner, "重做", self.redo, width=5).pack(side="left", padx=4)

        right = tk.Frame(bar, bg=PANEL)
        right.pack(side="right", padx=8)
        for t, y, p in (("轴测", 38, 24), ("俯视", 0, 89), ("正视", 0, 2), ("侧视", 90, 2)):
            mkbtn(right, t, lambda y=y, p=p: self.set_view(y, p), width=4).pack(side="left", padx=2, pady=6)
        mkbtn(right, "适应", lambda: self.fit(), width=4).pack(side="left", padx=2, pady=6)

        # 状态栏：左边是最近一次操作的反馈（会一直留着），右边才是渲染统计
        st = tk.Frame(self, bg=PANEL, height=26)
        st.pack(side="bottom", fill="x")
        st.pack_propagate(False)
        tk.Label(st, textvariable=self._hud, bg=PANEL, fg="#e6ebf5", font=FONT_S,
                 anchor="w").pack(side="left", padx=10, fill="x", expand=True)
        tk.Label(st, textvariable=self._stat, bg=PANEL, fg=FG_DIM, font=FONT_S,
                 anchor="e").pack(side="right", padx=10)

        # 左面板
        left = tk.Frame(self, bg=PANEL, width=300)
        left.pack(side="left", fill="y")
        left.pack_propagate(False)
        self._build_left(left)

        # 右面板
        right_p = tk.Frame(self, bg=PANEL, width=270)
        right_p.pack(side="right", fill="y")
        right_p.pack_propagate(False)
        self._build_right(right_p)

        # 画布
        wrap = tk.Frame(self, bg=BG)
        wrap.pack(side="left", fill="both", expand=True)
        self.canvas = tk.Canvas(wrap, bg="#0e0f13", highlightthickness=0, cursor="crosshair")
        self.canvas.pack(fill="both", expand=True)
        self.canvas.bind("<ButtonPress-1>", self.on_press)
        self.canvas.bind("<B1-Motion>", self.on_motion)
        self.canvas.bind("<ButtonRelease-1>", self.on_release)
        self.canvas.bind("<ButtonPress-3>", self.on_rpress)
        self.canvas.bind("<B3-Motion>", self.on_rmotion)
        self.canvas.bind("<ButtonPress-2>", self.on_rpress)
        self.canvas.bind("<B2-Motion>", self.on_rmotion)
        self.canvas.bind("<MouseWheel>", self.on_wheel)
        self.canvas.bind("<Configure>", lambda e: self.schedule_redraw())

    def _build_left(self, p):
        tk.Label(p, text="分组", bg=PANEL, fg=FG, font=FONT_B).pack(anchor="w", padx=12, pady=(12, 4))

        style = ttk.Style(self)
        style.theme_use("clam")
        style.configure("G.Treeview", background="#12141a", fieldbackground="#12141a",
                        foreground=FG, borderwidth=0, font=FONT, rowheight=22)
        style.configure("G.Treeview.Heading", background=PANEL2, foreground=FG_DIM,
                        borderwidth=0, font=FONT_S)
        style.map("G.Treeview", background=[("selected", "#2c4a70")],
                  foreground=[("selected", "#ffffff")])

        self.tree = ttk.Treeview(p, columns=("n",), show="tree headings", height=9,
                                 style="G.Treeview", selectmode="browse")
        self.tree.heading("#0", text="名称")
        self.tree.heading("n", text="点数")
        self.tree.column("#0", width=170, anchor="w")
        self.tree.column("n", width=80, anchor="e")
        self.tree.pack(fill="x", padx=12)
        self.tree.bind("<<TreeviewSelect>>", lambda e: self._sync_target_from_tree())

        row = tk.Frame(p, bg=PANEL)
        row.pack(fill="x", padx=12, pady=6)
        mkbtn(row, "显示/隐藏", self.toggle_group_visible, font=FONT_S).pack(side="left", expand=True, fill="x")
        mkbtn(row, "重命名", self.rename_group, font=FONT_S).pack(side="left", expand=True, fill="x", padx=4)
        mkbtn(row, "删除组", self.delete_group, font=FONT_S).pack(side="left", expand=True, fill="x")

        self._sep(p)

        tk.Label(p, text="选择工具", bg=PANEL, fg=FG, font=FONT_B).pack(anchor="w", padx=12, pady=(4, 2))
        for val, label in (("orbit", "旋转视角（左键拖动）"), ("box", "框选（左键拉矩形）"),
                           ("pick", "点选（选区球半径内）"), ("measure", "测距（点两个点）")):
            tk.Radiobutton(p, text=label, value=val, variable=self.v_tool, bg=PANEL, fg=FG,
                           activebackground=PANEL, activeforeground=ACCENT, selectcolor="#0f1115",
                           font=FONT, anchor="w", relief="flat", bd=0,
                           highlightthickness=0).pack(anchor="w", padx=14)

        f = tk.Frame(p, bg=PANEL)
        f.pack(fill="x", padx=12, pady=(4, 2))
        tk.Checkbutton(f, text="框选只取可见面", variable=self.v_depth, bg=PANEL, fg=FG,
                       activebackground=PANEL, selectcolor="#0f1115", font=FONT,
                       highlightthickness=0).pack(side="left")
        f2 = tk.Frame(p, bg=PANEL)
        f2.pack(fill="x", padx=12)
        tk.Label(f2, text="点选半径 m", bg=PANEL, fg=FG_DIM, font=FONT_S).pack(side="left")
        mkentry(f2, self.v_pick_r, 5).pack(side="left", padx=6)
        mkbtn(f2, "清空选择", self.clear_sel, font=FONT_S).pack(side="left", expand=True, fill="x")

        self._sep(p)
        tk.Label(p, text="选中 → 分组", bg=PANEL, fg=FG, font=FONT_B).pack(anchor="w", padx=12, pady=(4, 2))
        mkbtn(p, "把选中点归入目标组", self.assign_to_target).pack(fill="x", padx=12, pady=3)
        mkbtn(p, "把选中点拆成新组", self.selection_to_group).pack(fill="x", padx=12, pady=3)

        self._sep(p)
        tk.Label(p, text="显示", bg=PANEL, fg=FG, font=FONT_B).pack(anchor="w", padx=12, pady=(4, 2))
        f3 = tk.Frame(p, bg=PANEL)
        f3.pack(fill="x", padx=12)
        tk.Label(f3, text="着色", bg=PANEL, fg=FG_DIM, font=FONT_S).pack(side="left")
        for val, label in (("group", "分组"), ("height", "高度"), ("intensity", "强度")):
            tk.Radiobutton(f3, text=label, value=val, variable=self.v_color, bg=PANEL, fg=FG,
                           activebackground=PANEL, selectcolor="#0f1115", font=FONT_S,
                           command=lambda: self.redraw(), highlightthickness=0).pack(side="left")
        f4 = tk.Frame(p, bg=PANEL)
        f4.pack(fill="x", padx=12, pady=4)
        tk.Label(f4, text="点大小", bg=PANEL, fg=FG_DIM, font=FONT_S).pack(side="left")
        tk.Spinbox(f4, from_=1, to=4, width=3, textvariable=self.v_psize, bg="#0f1115", fg=FG,
                   relief="flat", font=FONT, buttonbackground=BTN,
                   command=lambda: self.redraw()).pack(side="left", padx=6)
        tk.Checkbutton(f4, text="地面网格", variable=self.v_grid, bg=PANEL, fg=FG_DIM,
                       activebackground=PANEL, selectcolor="#0f1115", font=FONT_S,
                       command=lambda: self.redraw(), highlightthickness=0).pack(side="left", padx=6)

        self._sep(p)
        tk.Label(p, text="快捷键", bg=PANEL, fg=FG_DIM, font=FONT_S, justify="left",
                 anchor="w").pack(anchor="w", padx=12, pady=(2, 0))
        tk.Label(p, text="F 适应窗口 · Ctrl+Z 撤销 · Ctrl+Y 重做\n方向键平移目标组（步长见右侧）",
                 bg=PANEL, fg=FG_DIM, font=FONT_S, justify="left", anchor="w").pack(
            anchor="w", padx=12, pady=(0, 10))

    def _build_right(self, p):
        pad = dict(padx=12)
        tk.Label(p, text="目标组", bg=PANEL, fg=FG, font=FONT_B).pack(anchor="w", pady=(12, 2), **pad)
        self.cb_target = ttk.Combobox(p, textvariable=self.v_target, state="readonly", font=FONT)
        self.cb_target.pack(fill="x", **pad)
        self.cb_target.bind("<<ComboboxSelected>>", lambda e: None)

        self._sep(p)
        tk.Label(p, text="平移（米）", bg=PANEL, fg=FG, font=FONT_B).pack(anchor="w", pady=(4, 2), **pad)
        grid = tk.Frame(p, bg=PANEL)
        grid.pack(fill="x", **pad)
        for i, (lab, var) in enumerate((("ΔX", self.v_dx), ("ΔY", self.v_dy), ("ΔZ", self.v_dz))):
            tk.Label(grid, text=lab, bg=PANEL, fg=FG_DIM, font=FONT).grid(row=i, column=0, sticky="w", pady=2)
            mkentry(grid, var, 7).grid(row=i, column=1, padx=6, pady=2, sticky="w")
            mkbtn(grid, "−", lambda v=var, s=-1: self.bump(v, s), width=2, font=FONT).grid(row=i, column=2, padx=1)
            mkbtn(grid, "+", lambda v=var, s=+1: self.bump(v, s), width=2, font=FONT).grid(row=i, column=3, padx=1)
        mkbtn(p, "应用平移", self.apply_translate, bg="#2c5f9e").pack(fill="x", pady=4, **pad)

        f = tk.Frame(p, bg=PANEL)
        f.pack(fill="x", **pad)
        tk.Label(f, text="步长", bg=PANEL, fg=FG_DIM, font=FONT_S).pack(side="left")
        mkentry(f, self.v_step, 5).pack(side="left", padx=4)
        for s in ("0.1", "0.5", "1", "3.5"):
            mkbtn(f, s, lambda s=s: self.v_step.set(s), font=FONT_S, pad=2).pack(side="left", padx=1)

        quick = tk.Frame(p, bg=PANEL)
        quick.pack(fill="x", pady=(6, 2), **pad)
        for lab, fn in (("←", lambda: self.nudge(-1, 0, 0)), ("→", lambda: self.nudge(1, 0, 0)),
                        ("↑", lambda: self.nudge(0, 1, 0)), ("↓", lambda: self.nudge(0, -1, 0)),
                        ("升", lambda: self.nudge(0, 0, 1)), ("降", lambda: self.nudge(0, 0, -1))):
            mkbtn(quick, lab, fn, font=FONT).pack(side="left", expand=True, fill="x", padx=1)
        mkbtn(p, "整车上道轨（复位）", lambda: self.reset_groups(only_target=True)).pack(
            fill="x", pady=2, **pad)
        mkbtn(p, "复位全部", lambda: self.reset_groups(False)).pack(fill="x", pady=2, **pad)

        self._sep(p)
        tk.Label(p, text="旋转（绕 Z）", bg=PANEL, fg=FG, font=FONT_B).pack(anchor="w", pady=(4, 2), **pad)
        f = tk.Frame(p, bg=PANEL)
        f.pack(fill="x", **pad)
        tk.Label(f, text="角度°", bg=PANEL, fg=FG_DIM, font=FONT).pack(side="left")
        mkentry(f, self.v_rot, 6).pack(side="left", padx=6)
        tk.Label(f, text="中心", bg=PANEL, fg=FG_DIM, font=FONT).pack(side="left")
        ttk.Combobox(f, textvariable=self.v_pivot, state="readonly", width=7,
                     values=("组中心", "世界原点", "选中点"), font=FONT_S).pack(side="left", padx=4)
        mkbtn(p, "应用旋转", self.apply_rotate, bg="#2c5f9e").pack(fill="x", pady=4, **pad)

        self._sep(p)
        tk.Label(p, text="自动分离（车体 / 走行部 / 轨道）", bg=PANEL, fg=FG,
                 font=FONT_B, wraplength=240, justify="left").pack(anchor="w", pady=(4, 2), **pad)
        g = tk.Frame(p, bg=PANEL)
        g.pack(fill="x", **pad)
        rows = (("轨面 Z", self.p_rail), ("钢轨 Y", self.p_yrail),
                ("车体半长", self.p_xh), ("车体半宽", self.p_yh), ("道床切深", self.p_drop))
        self._param_entries = []
        for i, (lab, var) in enumerate(rows):
            tk.Label(g, text=lab, bg=PANEL, fg=FG_DIM, font=FONT_S).grid(row=i, column=0, sticky="w", pady=1)
            e = mkentry(g, var, 6)
            e.grid(row=i, column=1, padx=6, pady=1, sticky="w")
            self._param_entries.append(e)
        self.p_note = tk.StringVar(value="打开文件时已自动填好，一般不用改")
        tk.Label(p, textvariable=self.p_note, bg=PANEL, fg=FG_DIM, font=FONT_S,
                 justify="left", anchor="w", wraplength=240).pack(fill="x", padx=12, pady=(2, 0))
        mkbtn(p, "执行自动分离", self.auto_split, bg="#2c5f9e").pack(fill="x", pady=(4, 2), **pad)
        mkbtn(p, "重新检测参数", self.detect_params).pack(fill="x", pady=2, **pad)
        tk.Label(p, text="这一步只重算上面 5 个数字，不分组；\n分组只发生在「执行自动分离」",
                 bg=PANEL, fg=FG_DIM, font=FONT_S, justify="left", anchor="w",
                 wraplength=240).pack(fill="x", padx=12, pady=(2, 6))

        self._sep(p)
        self.info = tk.Label(p, text="", bg=PANEL, fg=FG_DIM, font=FONT_S, justify="left",
                             anchor="nw", wraplength=240)
        self.info.pack(fill="both", expand=True, padx=12, pady=(4, 10))

    def _sep(self, p):
        tk.Frame(p, bg="#2a2e38", height=1).pack(fill="x", padx=10, pady=6)

    def _bind_keys(self):
        self.bind("<Control-z>", lambda e: self.undo())
        self.bind("<Control-y>", lambda e: self.redo())
        self.bind("<Control-s>", lambda e: self.export(".csv"))
        self.bind("<Control-o>", lambda e: self.open_file())
        self.bind("f", lambda e: self.fit())
        self.bind("F", lambda e: self.fit())
        for key, d in (("Left", (-1, 0, 0)), ("Right", (1, 0, 0)),
                       ("Up", (0, 1, 0)), ("Down", (0, -1, 0)),
                       ("Prior", (0, 0, 1)), ("Next", (0, 0, -1))):
            self.bind(f"<{key}>", lambda e, d=d: self.nudge(*d))

    # ------------------------------------------------------------ 渲染
    def schedule_redraw(self, fast=False):
        self._fast = self._fast or fast
        if self._redraw_job is None:
            self._redraw_job = self.after(12, self._do_redraw)

    def _do_redraw(self):
        self._redraw_job = None
        fast, self._fast = self._fast, False
        self.redraw(fast=fast)

    def redraw(self, fast=False):
        if self.cloud is None:
            W = max(self.canvas.winfo_width(), 50)
            H = max(self.canvas.winfo_height(), 50)
            self.canvas.delete("all")
            self.canvas.create_text(W / 2, H / 2 - 18, fill="#5d6577", font=("Microsoft YaHei UI", 13),
                                    justify="center",
                                    text="还没有打开点云\n拖一个 CSV / PLY 文件到这里，或点左上角「打开点云」")
            return
        W = max(self.canvas.winfo_width(), 50)
        H = max(self.canvas.winfo_height(), 50)
        t0 = time.time()
        try:
            grid = None
            if self.v_grid.get():
                lo, hi = self.cloud.pts.min(0), self.cloud.pts.max(0)
                ex = float(max(hi[0] - lo[0], hi[1] - lo[1]))
                step = 1.0 if ex <= 40 else 5.0
                pad = ex * 0.12
                grid = dict(x0=lo[0] - pad, x1=hi[0] + pad, y0=lo[1] - pad, y1=hi[1] + pad,
                            z=0.0, step=step)
            img, info = core.render(self.cloud, self.cam, W, H,
                                    color_mode=self.v_color.get(),
                                    point_size=int(self.v_psize.get()),
                                    highlight=self.sel, grid=grid,
                                    max_points=140000 if fast else None)
        except Exception:
            _say(traceback.format_exc())
            return
        self._last_img = img
        self._photo = ImageTk.PhotoImage(img)
        self.canvas.delete("all")
        self.canvas.create_image(0, 0, anchor="nw", image=self._photo)
        self._draw_overlay(W, H)
        dt = (time.time() - t0) * 1000
        sel = int(self.sel.sum()) if self.sel is not None else 0
        self._stat.set(f"点 {len(self.cloud.pts):,} · 显示 {info['points']:,} · 选中 {sel:,} · "
                       f"渲染 {dt:.0f} ms　|　左键旋转 · 右键平移 · 滚轮缩放")

    def _draw_overlay(self, W, H):
        """坐标轴 + 文件名（网格已经画在点云图层里，会被点遮挡）。"""
        if self.cloud is None:
            return
        o = np.array([[0, 0, 0], [3, 0, 0], [0, 3, 0], [0, 0, 2.2]])
        sx, sy, sz, _ = self.cam.project(o, W, H)
        if (sz > 0).all():
            for k, c in ((1, "#e05555"), (2, "#55cc66"), (3, "#5599ee")):
                self.canvas.create_line(sx[0], sy[0], sx[k], sy[k], fill=c, width=2, arrow="last")
        self.canvas.create_text(12, 10, anchor="nw", fill=FG_DIM, font=FONT_S,
                                text=os.path.basename(self.cloud.path or "未命名"))

    def fit(self):
        if self.cloud is None:
            return
        W = self.canvas.winfo_width()
        H = self.canvas.winfo_height()
        if W < 60 or H < 60:          # 窗口还没布局好，稍后再试
            self.after(120, self.fit)
            return
        self.cam.fit(self.cloud.world_points(), W, H)
        self.schedule_redraw()

    def set_view(self, yaw, pitch):
        self.cam.yaw, self.cam.pitch = yaw, pitch
        self.schedule_redraw()

    def open_photo(self):
        return self._photo

    # ------------------------------------------------------------ 交互
    def on_press(self, e):
        if self.cloud is None:
            return
        self._drag = dict(x=e.x, y=e.y, yaw=self.cam.yaw, pitch=self.cam.pitch, moved=False)
        tool = self.v_tool.get()
        if tool == "box":
            self._box = (e.x, e.y)
            if getattr(self, "_box_id", None):
                self.canvas.delete(self._box_id)
            self._box_id = self.canvas.create_rectangle(e.x, e.y, e.x, e.y,
                                                        outline=ACCENT, width=1, dash=(4, 3))
        elif tool == "pick":
            self.do_pick(e.x, e.y, shift=bool(e.state & 0x0001))
        elif tool == "measure":
            self.do_measure(e.x, e.y)

    def on_motion(self, e):
        if not self._drag:
            return
        dx, dy = e.x - self._drag["x"], e.y - self._drag["y"]
        if abs(dx) + abs(dy) > 2:
            self._drag["moved"] = True
        tool = self.v_tool.get()
        if tool == "orbit":
            self.cam.yaw = self._drag["yaw"] - dx * 0.4
            self.cam.pitch = max(-89, min(89, self._drag["pitch"] + dy * 0.3))
            self.schedule_redraw(fast=True)
        elif tool == "box" and self._box:
            x0, y0 = self._box
            self.canvas.coords(self._box_id, x0, y0, e.x, e.y)

    def on_release(self, e):
        if not self._drag:
            return
        tool = self.v_tool.get()
        if tool == "box" and self._box:
            x0, y0 = self._box
            self._box = None
            if getattr(self, "_box_id", None):
                self.canvas.delete(self._box_id)
                self._box_id = None
            W = self.canvas.winfo_width()
            H = self.canvas.winfo_height()
            m = core.rect_select(self.cloud, self.cam, W, H, x0, y0, e.x, e.y,
                                 depth_test=self.v_depth.get())
            if e.state & 0x0001 and self.sel is not None:
                self.sel |= m
            else:
                self.sel = m
            self.schedule_redraw()
        self._drag = None

    def on_rpress(self, e):
        self._drag = dict(x=e.x, y=e.y, pan=True)

    def on_rmotion(self, e):
        if not self._drag or not self._drag.get("pan"):
            return
        self.cam.pan(e.x - self._drag["x"], e.y - self._drag["y"],
                     self.canvas.winfo_width(), self.canvas.winfo_height())
        self._drag["x"], self._drag["y"] = e.x, e.y
        self.schedule_redraw(fast=True)

    def on_wheel(self, e):
        if self.cloud is None:
            return
        self.cam.dist *= 0.88 if e.delta > 0 else 1.14
        self.cam.dist = max(0.5, min(5000, self.cam.dist))
        self.schedule_redraw(fast=True)

    def do_pick(self, mx, my, shift=False):
        idx, d = core.pick(self.cloud, self.cam, self.canvas.winfo_width(),
                           self.canvas.winfo_height(), mx, my, 10)
        if idx is None:
            self._hud.set("没点中（附近没有点）")
            return
        try:
            r = float(self.v_pick_r.get())
        except ValueError:
            r = 0.12
        m = core.sphere_select(self.cloud, idx, r)
        self.sel = m if (self.sel is None or not shift) else (self.sel | m)
        self.schedule_redraw()

    def do_measure(self, mx, my):
        idx, d = core.pick(self.cloud, self.cam, self.canvas.winfo_width(),
                           self.canvas.winfo_height(), mx, my, 12)
        if idx is None:
            self._hud.set("测距：没点中，再点一次")
            return
        self._measure.append(self.cloud.world_points()[idx])
        if len(self._measure) == 1:
            self._hud.set("测距：再点第二个点")
        else:
            a, b = self._measure[-2], self._measure[-1]
            dv = b - a
            dist = float(np.linalg.norm(dv))
            self._hud.set(f"两点距离 {dist:.3f} m   ΔX {dv[0]:+.3f}  ΔY {dv[1]:+.3f}  ΔZ {dv[2]:+.3f}")

    def clear_sel(self):
        self.sel = None
        self.schedule_redraw()

    # ------------------------------------------------------------ 文件
    def open_file(self):
        path = filedialog.askopenfilename(
            title="打开点云",
            filetypes=[("点云文件", "*.csv *.ply *.txt *.xyz"), ("全部文件", "*.*")])
        if path:
            self.open_path(path)

    def open_path(self, path):
        self._hud.set("正在读取 " + os.path.basename(path))
        self.update_idletasks()
        try:
            t0 = time.time()
            self.cloud = core.load_cloud(path)
            self.sel = None
            self.undo_stack.clear()
            self.redo_stack.clear()
            self.refresh_groups()
            self.fit()
            self.detect_params(silent=True)
            self._hud.set(f"已打开 {os.path.basename(path)}（{len(self.cloud.pts):,} 点）"
                          f"—— 下一步：点顶栏「自动分离车/轨」")
        except Exception as ex:
            messagebox.showerror("打开失败", f"{ex}\n\n{traceback.format_exc()}")

    def export(self, ext):
        if self.cloud is None:
            return
        path = filedialog.asksaveasfilename(
            title="导出点云", defaultextension=ext,
            initialfile=os.path.splitext(os.path.basename(self.cloud.path or "edited"))[0] + "_edited" + ext,
            filetypes=[("CSV", "*.csv"), ("PLY", "*.ply")])
        if not path:
            return
        try:
            core.save_cloud(path, self.cloud, with_intensity=self.cloud.intensity is not None)
            self._hud.set("已导出 " + path)
        except Exception as ex:
            messagebox.showerror("导出失败", str(ex))

    # ------------------------------------------------------------ 分组
    def refresh_groups(self):
        self.tree.delete(*self.tree.get_children())
        names = []
        if self.cloud is None:
            return
        for i, g in enumerate(self.cloud.groups):
            dot = "●" if g.visible else "○"
            self.tree.insert("", "end", iid=str(i), text=f"{dot} {g.name}", values=(f"{g.count:,}",))
        names = [g.name for g in self.cloud.groups]
        self.cb_target["values"] = names
        if self.v_target.get() not in names and names:
            self.v_target.set(names[0] if len(names) == 1 else (names[1] if len(names) > 1 else names[0]))
        self._update_info()

    def _target_gid(self):
        if self.cloud is None:
            return None
        name = self.v_target.get()
        for i, g in enumerate(self.cloud.groups):
            if g.name == name:
                return i
        return None

    def _sync_target_from_tree(self):
        sel = self.tree.selection()
        if sel and self.cloud:
            i = int(sel[0])
            if 0 <= i < len(self.cloud.groups):
                self.v_target.set(self.cloud.groups[i].name)
                self._update_info()

    def toggle_group_visible(self):
        gid = self._target_gid()
        if gid is None:
            return
        self.cloud.groups[gid].visible = not self.cloud.groups[gid].visible
        self.refresh_groups()
        self.schedule_redraw()

    def rename_group(self):
        gid = self._target_gid()
        if gid is None:
            return
        from tkinter import simpledialog
        new = simpledialog.askstring("重命名", "组名：", initialvalue=self.cloud.groups[gid].name, parent=self)
        if new:
            self.cloud.groups[gid].name = new
            self.refresh_groups()

    def delete_group(self):
        gid = self._target_gid()
        if gid is None or len(self.cloud.groups) <= 1:
            return
        self.push_undo()
        self.cloud.labels[self.cloud.labels == gid] = 0
        self.cloud.labels[self.cloud.labels > gid] -= 1
        self.cloud.groups.pop(gid)
        self.cloud._refresh()
        self.refresh_groups()
        self.schedule_redraw()

    def assign_to_target(self):
        if self.sel is None or not self.sel.any():
            self._hud.set("先框选/点选一批点")
            return
        gid = self._target_gid()
        if gid is None:
            return
        self.push_undo()
        self.cloud.assign(self.sel, gid)
        self.refresh_groups()
        self.sel = None
        self.schedule_redraw()

    def selection_to_group(self):
        if self.sel is None or not self.sel.any():
            self._hud.set("先选中一批点")
            return
        self.push_undo()
        gid = self.cloud.ensure_group(f"选择{len(self.cloud.groups)}")
        self.cloud.assign(self.sel, gid)
        self.refresh_groups()
        self.v_target.set(self.cloud.groups[gid].name)
        self.sel = None
        self.schedule_redraw()

    # ------------------------------------------------------------ 变换
    def _f(self, var, default=0.0):
        try:
            return float(str(var.get()).strip())
        except Exception:
            return default

    def bump(self, var, sign):
        step = self._f(self.v_step, 0.5) * sign
        var.set(f"{self._f(var, 0.0) + step:.3f}")

    def apply_translate(self):
        self.do_translate(self._f(self.v_dx), self._f(self.v_dy), self._f(self.v_dz))

    def do_translate(self, dx, dy, dz):
        gid = self._target_gid()
        if gid is None or self.cloud is None:
            return
        if abs(dx) + abs(dy) + abs(dz) < 1e-9:
            return
        self.push_undo()
        self.cloud.groups[gid].translate([dx, dy, dz])
        self.refresh_groups()
        self.schedule_redraw()
        self._hud.set(f"{self.cloud.groups[gid].name} 平移 ({dx:+.2f}, {dy:+.2f}, {dz:+.2f}) m")

    def nudge(self, ax, ay, az):
        s = self._f(self.v_step, 0.5)
        self.do_translate(ax * s, ay * s, az * s)

    def apply_rotate(self):
        gid = self._target_gid()
        if gid is None or self.cloud is None:
            return
        deg = self._f(self.v_rot)
        if abs(deg) < 1e-9:
            return
        piv = self.v_pivot.get()
        if piv == "世界原点":
            pivot = np.zeros(3)
        elif piv == "选中点" and self.sel is not None and self.sel.any():
            pivot = self.cloud.pts[self.sel].mean(0)
        else:
            p = self.cloud.group_points(gid)
            pivot = (p.min(0) + p.max(0)) / 2 if len(p) else np.zeros(3)
        self.push_undo()
        self.cloud.groups[gid].rotate_z(deg, pivot)
        self.refresh_groups()
        self.schedule_redraw()
        self._hud.set(f"{self.cloud.groups[gid].name} 绕 Z 旋转 {deg:+.1f}°")

    def reset_groups(self, only_target=True):
        if self.cloud is None:
            return
        self.push_undo()
        if only_target:
            gid = self._target_gid()
            if gid is not None:
                self.cloud.groups[gid].reset()
        else:
            for g in self.cloud.groups:
                g.reset()
        self.refresh_groups()
        self.schedule_redraw()

    # ------------------------------------------------------------ 自动分离
    def detect_params(self, silent=False):
        if self.cloud is None:
            self._hud.set("还没打开点云：先点左上角「打开点云」")
            return
        g = core.detect_geometry(self.cloud.pts)
        vars_ = [self.p_rail, self.p_yrail, self.p_xh, self.p_yh]
        new = [f"{g['z_rail']:.3f}", f"{g['y_rail']:.3f}",
               f"{g['x_half']:.2f}", f"{g['y_half']:.2f}"]
        changed = 0
        for v, nv in zip(vars_, new):
            if v.get().strip() != nv:
                changed += 1
            v.set(nv)
        if silent:
            return
        if changed:
            self.p_note.set(f"已按点云重算：更新了 {changed} 项，原值已覆盖")
            self._flash_params()
        else:
            self.p_note.set("已按点云重算：结果和当前值完全一样，所以数字没变")
        self._hud.set("检测结果：轨面 %.3f m · 钢轨 ±%.3f m · 车体 %.2f×%.2f m　（这一步只算数字，不分组）"
                      % (g["z_rail"], g["y_rail"], g["x_half"] * 2, g["y_half"] * 2))

    def _flash_params(self):
        for e in getattr(self, "_param_entries", []):
            try:
                e.config(bg="#2c4a70")
            except Exception:
                pass

        def restore():
            for e in getattr(self, "_param_entries", []):
                try:
                    e.config(bg="#0f1115")
                except Exception:
                    pass

        self.after(700, restore)

    def auto_split(self):
        if self.cloud is None:
            return
        self.push_undo()
        _, rep = core.auto_split(
            self.cloud,
            z_rail=self._f(self.p_rail, 0.0),
            y_rail=self._f(self.p_yrail, 0.72),
            x_half=self._f(self.p_xh, 7.0),
            y_half=self._f(self.p_yh, 1.6),
            ballast_drop=self._f(self.p_drop, 0.12))
        self.sel = None
        self.v_target.set("车体")
        self.refresh_groups()
        # 在分组树里选中「车体」，让人一眼看到这次分离动了哪一组
        for i, gp in enumerate(self.cloud.groups):
            if gp.name == self.v_target.get():
                self.tree.selection_set(str(i))
                self.tree.see(str(i))
                break
        self.schedule_redraw()
        parts = "　".join(f"{n} {c:,}" for n, c, _ in rep["groups"])
        self.p_note.set(f"已按以上参数分离：{parts}")
        self._hud.set(f"已分离：{parts}　→　右侧「目标组」已切到车体，填 ΔY 后点「应用平移」")

        # 结果自检：车体占比太小，多半是这份点云不满足"车压在轨道上"的前提
        total = len(self.cloud.pts)
        body_n = next((c for n, c, _ in rep["groups"] if n == "车体"), 0)
        if total and body_n < 0.05 * total:
            self._hud.set(f"⚠ 车体只分到 {body_n:,} 点（{body_n / total:.1%}），可能这份点云不是"
                          f"「车停在轨道上」的场景；请核对上面 5 个参数，或改用框选修")

    # ------------------------------------------------------------ 撤销
    def push_undo(self):
        if self.cloud is None:
            return
        self.undo_stack.append(self.cloud.snapshot())
        if len(self.undo_stack) > 60:
            self.undo_stack.pop(0)
        self.redo_stack.clear()

    def undo(self):
        if not self.undo_stack or self.cloud is None:
            self._hud.set("没有可撤销的操作")
            return
        self.redo_stack.append(self.cloud.snapshot())
        self.cloud.restore(self.undo_stack.pop())
        self.refresh_groups()
        self.schedule_redraw()
        self._hud.set("已撤销")

    def redo(self):
        if not self.redo_stack or self.cloud is None:
            self._hud.set("没有可重做的操作")
            return
        self.undo_stack.append(self.cloud.snapshot())
        self.cloud.restore(self.redo_stack.pop())
        self.refresh_groups()
        self.schedule_redraw()
        self._hud.set("已重做")

    def _update_info(self):
        gid = self._target_gid()
        if gid is None or self.cloud is None:
            return
        st = core.group_stats(self.cloud, gid)
        if not st:
            self.info.config(text="（空组）")
            return
        self.info.config(text=f"点数 {st['n']:,}\n尺寸 {st['size'][0]:.2f} × {st['size'][1]:.2f} × {st['size'][2]:.2f} m\n"
                              f"中心 ({st['center'][0]:.2f}, {st['center'][1]:.2f}, {st['center'][2]:.2f})")


def _say(s):
    """pythonw 下 sys.stdout 是 None，直接 print 会崩；这里统一兜住。"""
    try:
        if sys.stdout is not None:
            sys.stdout.write(str(s) + "\n")
            sys.stdout.flush()
    except Exception:
        pass


def check_file(path, out=None):
    """无界面自检：读点云 -> 检测几何 -> 分离 -> 打印结果（打包后用来验证运行环境）"""
    c = core.load_cloud(path)
    g = core.detect_geometry(c.pts)
    _, rep = core.auto_split(c, z_rail=round(g["z_rail"], 3), y_rail=round(g["y_rail"], 3),
                             x_half=round(g["x_half"], 2), y_half=round(g["y_half"], 2))
    lines = [f"文件 {path}",
             f"点数 {len(c.pts):,}",
             "检测 轨面 %.3f m · 钢轨 ±%.3f m · 车体 %.2f×%.2f m" % (
                 g["z_rail"], g["y_rail"], g["x_half"] * 2, g["y_half"] * 2)]
    lines += [f"{n}: {k:,}" for n, k, _ in rep["groups"]]
    txt = "\n".join(lines)
    _say(txt)
    if out:
        with open(out, "w", encoding="utf-8") as f:
            f.write(txt + "\n")
    return 0


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    if argv and argv[0] == "--check":
        if len(argv) < 2:
            _say("用法: --check 点云文件 [--out 结果.txt]")
            return 2
        out = None
        if "--out" in argv:
            out = argv[argv.index("--out") + 1]
        return check_file(argv[1], out)
    app = App(initial=argv[0] if argv else None)
    app.mainloop()


if __name__ == "__main__":
    main()
