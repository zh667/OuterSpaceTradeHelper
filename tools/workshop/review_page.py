# Runs on Windows. Writes local/workshop/预览.html: everything `workshop.py upload` would send, to look over before
# uploading (title, preview, the description rendered from its BBCode, tags, the files of dist/TradeHelper).
import hashlib, html, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__)); ROOT = os.path.dirname(os.path.dirname(HERE))
ITEM = json.load(open(os.path.join(HERE, "item.json"), encoding="utf-8"))
OUT = os.path.join(ROOT, "local", "workshop", "预览.html")


def bbcode(t):
    t = html.escape(t)
    for a, b in (("h1", "h1"), ("h2", "h2"), ("b", "b"), ("i", "i"), ("list", "ul")):
        t = t.replace("[%s]" % a, "<%s>" % b).replace("[/%s]" % a, "</%s>" % b)
    t = re.sub(r"\[\*\](.*)", r"<li>\1</li>", t)
    t = re.sub(r"(https://[^\s<]+)", r'<a href="\1">\1</a>', t)
    t = re.sub(r"\n(?!<)", "<br>\n", t)
    return t


def main():
    pid_file = os.path.join(HERE, "published_id.txt")
    pid = open(pid_file, encoding="utf-8").read().strip() if os.path.exists(pid_file) else ""
    desc = open(os.path.join(ROOT, ITEM["descriptionFile"]), encoding="utf-8").read()
    ver = re.search(r'Version\s*=\s*"([^"]+)"', open(os.path.join(ROOT, "src", "TradeHelperBehaviour.cs"), encoding="utf-8").read()).group(1)
    dist = os.path.join(ROOT, ITEM["content"]); rows = []
    for r, _, fs in os.walk(dist):
        for f in sorted(fs):
            p = os.path.join(r, f)
            rows.append((os.path.relpath(p, dist).replace("\\", "/"), os.path.getsize(p), hashlib.sha256(open(p, "rb").read()).hexdigest()[:16]))
    prev = os.path.join(ROOT, ITEM["preview"])
    extra = sys.argv[1] if len(sys.argv) > 1 else ""
    files = "".join("<tr><td>%s</td><td>%s 字节</td><td><code>%s</code></td></tr>" % (html.escape(a), format(b, ","), c) for a, b, c in rows)
    page = """<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>跑商助手 · 创意工坊上传前预览</title>
<style>
body{margin:0;background:#1b1a19;color:#e8e4dc;font:15px/1.65 "Microsoft YaHei",sans-serif}
main{max-width:1000px;margin:0 auto;padding:28px 18px 60px}
h1.page{font-size:22px;margin:0 0 4px}.sub{color:#9a9890;margin:0 0 22px}
section{background:#252422;border:1px solid #34322e;border-radius:6px;padding:16px 20px;margin:16px 0}
section>h2{font-size:16px;margin:0 0 10px;color:#f4b30d}
table{border-collapse:collapse;width:100%%}td,th{text-align:left;padding:5px 8px;border-bottom:1px solid #34322e;vertical-align:top}
th{color:#9a9890;font-weight:normal;width:150px}
img{max-width:100%%;border:1px solid #34322e}.desc{background:#1e1d1b;padding:6px 18px;border-radius:4px}
.desc h1{font-size:22px}.desc h2{font-size:17px;color:#f4b30d}.warn{color:#f0b85a}code{color:#b8c4cc}a{color:#7fb6e8}
</style></head><body><main>
<h1 class="page">跑商助手 · 创意工坊上传前预览</h1>
<p class="sub">还没有上传。你同意后才会运行 <code>python tools/workshop.py upload</code>。</p>
<section><h2>上传后会发生什么</h2><table>
<tr><th>物品</th><td>%(target)s</td></tr>
<tr><th>可见性</th><td>%(vis)s</td></tr>
<tr><th>标题</th><td>%(title)s</td></tr>
<tr><th>语言 / 标签</th><td>%(lang)s / %(tags)s</td></tr>
<tr><th>更新说明</th><td>%(ver)s</td></tr>
<tr><th>上传方式</th><td>用游戏自带的 Steamworks 库，通过本机已登录的 Steam（账号 zh667）上传，不需要密码。上传时 Steam 会短暂显示你“正在玩”这个游戏。</td></tr>
<tr><th>Steam 连接检查</th><td>%(check)s</td></tr>
</table></section>
<section><h2>预览图（%(pw)s）</h2><img src="preview.jpg" alt="预览图"></section>
<section><h2>页面描述（按 Steam 的 BBCode 渲染，%(dlen)d 字）</h2><div class="desc">%(desc)s</div></section>
<section><h2>上传的文件（dist/TradeHelper，版本 %(ver)s）</h2><table><tr><th>文件</th><th>大小</th><th>SHA-256 前 16 位</th></tr>%(files)s</table></section>
<section><h2>要注意</h2><ul>
<li class="warn">游戏先加载创意工坊订阅的模组，再加载本地 mods 文件夹；两者 packageId 相同（zh6657.tradehelper）时，本地那份会被忽略。如果你自己订阅了这个物品，以后改本地版本测试前要先退订，或者每次都同步上传。</li>
<li>以后更新会沿用同一个物品（编号存进 tools/workshop/published_id.txt 并提交到仓库），可见性不变，除非指定 --visibility。</li>
<li>Steam 页面上的额外截图、改公开等也可以之后在网页上手动改。</li>
</ul></section>
</main></body></html>"""
    vis = "第一次上传：仅自己可见（private），你在 Steam 页面确认后再改公开" if not pid else "保持现在的可见性"
    target = ("新建一个物品（游戏 3080070）" if not pid else "更新已有物品 %s" % pid)
    from PIL import Image
    im = Image.open(prev); pw = "%dx%d，%s KB" % (im.width, im.height, format(os.path.getsize(prev) // 1024, ","))
    s = page % dict(target=target, vis=vis, title=html.escape(ITEM["title"]), lang=ITEM.get("language", ""), tags=html.escape(", ".join(ITEM.get("tags", []))),
                    ver=ver, check=html.escape(extra) or "（未检查）", pw=pw, dlen=len(desc), desc=bbcode(desc), files=files)
    open(OUT, "w", encoding="utf-8").write(s)
    print("page", OUT)


if __name__ == "__main__":
    main()
