# 从点云 CSV 重新生成 data.js（内嵌数据：int16 量化 1 mm 的位置 + 强度 + 初始分类）
# 用法：改下面 CSV 路径后运行 python gen_data2.py
import numpy as np, base64, io, json

CSV = r"D:\111\东西\rail_wagon_c70e.csv"
OUT = r"C:\Users\Lenovo\Documents\deepseek-harness\default-workspace\cloud-editor\data.js"

raw = open(CSV, "rb").read().decode("utf-8-sig").splitlines()
rows = np.loadtxt(io.StringIO("\n".join(raw[1:])), delimiter=",", dtype=np.float32)
xyz = rows[:, :3]
inten = rows[:, 3]
n = len(xyz)
x, y, z = xyz.T

cls = np.zeros(n, dtype=np.uint8)      # 0 其他 / 1 地面 / 2 钢轨 / 3 车体
wagon = (np.abs(y) <= 1.65) & (z >= 0.18) & (np.abs(x) <= 7.05)
rail = (~wagon) & (np.abs(y) >= 0.60) & (np.abs(y) <= 0.86) & (z >= -0.5) & (z < 0.25)
ground = (~wagon) & (~rail) & (((z < 0.25) & (np.abs(y) <= 2.6)) | (z < -0.5))
cls[wagon] = 3
cls[rail] = 2
cls[ground] = 1
print("n=%d counts=%s" % (n, [int((cls == k).sum()) for k in range(4)]))

mn = xyz.min(0)
q = np.round((xyz - mn) / 0.001).astype(np.int16)

def b64(a):
    return base64.b64encode(a.tobytes()).decode()

js = "window.RAIL_CLOUD=" + json.dumps({
    "n": int(n),
    "min": [float(v) for v in mn],
    "step": 0.001,
    "pos": b64(q),
    "inten": b64(np.clip(inten, 0, 255).astype(np.uint8)),
    "cls": b64(cls),
}, ensure_ascii=False) + ";"
open(OUT, "w", encoding="utf-8").write(js)
print("data.js bytes", len(js))
