# 生成应用图标 app.ico：深色底 + 点云 C70E 敞车轮廓 + 两条钢轨
import math, random
from PIL import Image, ImageDraw

S = 256
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# 圆角底板
def rounded(size, r, fill):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size - 1, size - 1], radius=r, fill=255)
    layer = Image.new("RGBA", (size, size), fill)
    return layer, m

layer, mask = rounded(S, 56, (10, 16, 24, 255))
img.paste(layer, (0, 0), mask)

d = ImageDraw.Draw(img)
random.seed(7)
# 两条钢轨（黄）
rail_y = [196, 208]
for y in rail_y:
    d.rounded_rectangle([18, y, S - 18, y + 6], radius=3, fill=(245, 197, 66, 255))

# 车体：点云构成的敞车侧板 + 底板（透视梯形）
def lerp(a, b, t):
    return a + (b - a) * t

top_l, top_r = (62, 82), (196, 78)      # 上沿
bot_l, bot_r = (54, 168), (204, 166)    # 下沿
pts = []
# 侧板边框点
for t in [i / 64 for i in range(65)]:
    pts.append((lerp(top_l[0], top_r[0], t), lerp(top_l[1], top_r[1], t)))
    pts.append((lerp(bot_l[0], bot_r[0], t), lerp(bot_l[1], bot_r[1], t)))
for t in [i / 26 for i in range(27)]:
    pts.append((lerp(top_l[0], bot_l[0], t), lerp(top_l[1], bot_l[1], t)))
    pts.append((lerp(top_r[0], bot_r[0], t), lerp(top_r[1], bot_r[1], t)))
# 板面点云（稀疏）
for i in range(1500):
    t = random.random()
    u = random.random()
    x = lerp(lerp(top_l[0], top_r[0], t), lerp(bot_l[0], bot_r[0], t), u)
    y = lerp(lerp(top_l[1], top_r[1], t), lerp(bot_l[1], bot_r[1], t), u)
    pts.append((x, y))

for (x, y) in pts:
    depth = (y - 78) / 90.0
    c = (
        int(34 + 60 * depth),
        int(211 - 40 * depth),
        int(238 - 20 * depth),
        235,
    )
    d.ellipse([x - 1.4, y - 1.4, x + 1.4, y + 1.4], fill=c)

# 车轮
for wx in (78, 172):
    d.ellipse([wx - 15, 172 - 15, wx + 15, 172 + 15], fill=(10, 16, 24, 255), outline=(34, 211, 238, 255), width=3)
    d.ellipse([wx - 5, 172 - 5, wx + 5, 172 + 5], fill=(34, 211, 238, 255))

img.save(r"C:\Users\Lenovo\Documents\deepseek-harness\default-workspace\desktop\app.png")
img.save(r"C:\Users\Lenovo\Documents\deepseek-harness\default-workspace\desktop\app.ico",
         sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("icon ok")
