# Runs ON WINDOWS. Puts the mod on the Steam Workshop through the Steam client you are logged in to (no password).
# Same tool as the turret mod's (E:/projects/os-ef-turrets/tools/workshop.py).
#   python tools/workshop.py preview                 draw local/workshop/preview.jpg (tools/workshop/make_preview.py)
#   python tools/workshop.py check                   build the uploader, connect to Steam, show the item if there is one
#   python tools/workshop.py upload [--visibility private|friends|unlisted|public] [--note TEXT]
#       builds the mod (tools/build.py, without installing it), then uploads dist/TradeHelper with the title,
#       description, tags and preview in tools/workshop/item.json. The first upload creates the item (private unless
#       --visibility says otherwise) and writes its id to tools/workshop/published_id.txt; later uploads update that
#       item and keep its visibility unless --visibility is given. The change note defaults to the mod version.
# Steam must be running and logged in. Steam shows you as playing the game while the uploader runs.
import json, os, re, shutil, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__)); ROOT = os.path.dirname(HERE)
WS = os.path.join(HERE, "workshop")
ITEM = json.load(open(os.path.join(WS, "item.json"), encoding="utf-8"))
ID_FILE = os.path.join(WS, "published_id.txt")
EXE_DIR = os.path.join(ROOT, "bin", "workshop")
GAME = r"E:\SteamLibrary\steamapps\common\Outer Space"
MANAGED = os.path.join(GAME, "Outer Space_Data", "Managed")
STEAM_API = os.path.join(GAME, "Outer Space_Data", "Plugins", "x86_64", "steam_api64.dll")
NOWIN = 0x08000000


def published_id():
    if os.path.exists(ID_FILE):
        s = open(ID_FILE, encoding="utf-8").read().strip()
        if s: return s
    return None


def version():
    src = open(os.path.join(ROOT, "src", "TradeHelperBehaviour.cs"), encoding="utf-8").read()
    return re.search(r'Version\s*=\s*"([^"]+)"', src).group(1)


def build_mod():
    r = subprocess.run([sys.executable, os.path.join(HERE, "build.py")], capture_output=True, text=True, encoding="utf-8",
                       errors="replace", cwd=ROOT, creationflags=NOWIN)
    print(r.stdout[-1500:], r.stderr[-800:])
    if r.returncode != 0: sys.exit("mod build failed")
    return os.path.join(ROOT, "dist", "TradeHelper")


def build_uploader():
    r = subprocess.run(["dotnet", "build", os.path.join(WS, "WorkshopUpload.csproj"), "-c", "Release", "-nologo", "-v:minimal",
                        "-p:GameManaged=" + MANAGED], capture_output=True, text=True, cwd=ROOT, creationflags=NOWIN)
    print(r.stdout[-3000:], r.stderr[-2000:])
    if r.returncode != 0: sys.exit("uploader build failed")
    shutil.copy2(STEAM_API, os.path.join(EXE_DIR, "steam_api64.dll"))
    return os.path.join(EXE_DIR, "WorkshopUpload.exe")


def run(exe, args):
    print(">", " ".join(args[:3]), "...")
    r = subprocess.run([exe] + args, capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=EXE_DIR,
                       creationflags=NOWIN)
    out = r.stdout + r.stderr
    print(out)
    return r.returncode, out


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "check"
    opts = sys.argv[2:]
    if cmd == "preview":
        sys.exit(subprocess.run([sys.executable, os.path.join(WS, "make_preview.py")] + opts, cwd=ROOT).returncode)
    app = str(ITEM["appId"]); pid = published_id()
    if cmd == "check":
        exe = build_uploader()
        code, _ = run(exe, ["check", "--app", app] + (["--id", pid] if pid else []))
        sys.exit(code)
    if cmd != "upload": sys.exit("usage: workshop.py preview|check|upload [--visibility V] [--note TEXT]")

    vis = note = None
    for i, a in enumerate(opts):
        if a == "--visibility": vis = opts[i + 1]
        if a == "--note": note = opts[i + 1]
    preview = os.path.join(ROOT, ITEM["preview"])
    if not os.path.exists(preview): sys.exit("no preview: run  python tools/workshop.py preview")
    dist = build_mod()                                     # fresh dist/TradeHelper, not installed
    content = os.path.join(ROOT, ITEM["content"])
    if os.path.normcase(os.path.abspath(dist)) != os.path.normcase(os.path.abspath(content)):
        sys.exit("item.json content is not the build output " + dist)
    exe = build_uploader()
    args = ["upload", "--app", app, "--content", content, "--preview", preview, "--title", ITEM["title"],
            "--desc-file", os.path.join(ROOT, ITEM["descriptionFile"]), "--lang", ITEM.get("language", "schinese"),
            "--tags", ",".join(ITEM.get("tags", [])), "--note", note or version()]
    if pid: args += ["--id", pid]
    else: args += ["--create"]; vis = vis or "private"
    if vis: args += ["--visibility", vis]
    code, out = run(exe, args)
    m = re.search(r"PUBLISHED_ID=(\d+)", out)
    if m and not pid:
        open(ID_FILE, "w", encoding="utf-8", newline="\n").write(m.group(1) + "\n")
        print("published id", m.group(1), "written to", ID_FILE, "(commit it)")
    sys.exit(code)


if __name__ == "__main__":
    main()
