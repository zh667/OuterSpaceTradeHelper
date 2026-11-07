# Runs on Windows. Draws the Workshop preview from a real screenshot of the panel (the self-test's "screens" mode
# writes local/runtime/selftest-ws-advice.png):
#   python tools/workshop/make_preview.py  ->  local/workshop/preview.jpg (under 1 MB)
import os
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = os.path.join(ROOT, "local", "runtime", "selftest-ws-advice.png")
OUT = os.path.join(ROOT, "local", "workshop", "preview.jpg")


def font(size, bold=False):
    for f in ((("msyhbd.ttc" if bold else "msyh.ttc")), "simhei.ttf"):
        p = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", f)
        if os.path.exists(p): return ImageFont.truetype(p, size)
    return ImageFont.load_default()


def main():
    shot = Image.open(SRC).convert("RGB")
    sx, sy = shot.width / 1600.0, shot.height / 900.0
    # the mod's panel down to the 10th route row (the self-test's cursor leaves a hover tip lower down)
    panel = shot.crop((int(150 * sx), int(92 * sy), int(1450 * sx), int(510 * sy)))
    W, H, top = 1280, 720, 112
    img = Image.new("RGB", (W, H), (28, 27, 25))
    g = ImageDraw.Draw(img)
    g.rectangle([0, 0, W, top], fill=(36, 35, 32))
    g.rectangle([36, 30, 44, 82], fill=(244, 179, 13))
    g.text((62, 22), "跑商助手", font=font(48, True), fill=(240, 236, 226))
    g.text((300, 40), "扣税后的跑商建议 · 商队与走私船 · 走私船出生点预测 · 结账记录", font=font(24), fill=(190, 194, 198))
    s = min((W - 40) / panel.width, (H - top - 120) / panel.height)
    p = panel.resize((round(panel.width * s), round(panel.height * s)), Image.LANCZOS)
    img.paste(p, ((W - p.width) // 2, top + 24))
    y = top + 24 + p.height + 30
    g.text((62, y), "其他页面：跑商计划 · 卖库存 · 各站市场 · 商队 · 我的库存 · 后台补算 · 结账记录", font=font(26), fill=(214, 210, 200))
    g.text((62, y + 44), "热键 F8 · 默认只读游戏数据 · 鼠标停在名词上有解释", font=font(22), fill=(150, 154, 158))
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    q = 90
    while True:
        img.save(OUT, quality=q, optimize=True)
        if os.path.getsize(OUT) < 1000 * 1024 or q <= 60: break
        q -= 5
    print("preview", OUT, img.size, os.path.getsize(OUT), "bytes, quality", q)


if __name__ == "__main__":
    main()
