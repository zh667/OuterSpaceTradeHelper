# Runs ON WINDOWS (via ~/tools/winjob/runjob.py). Builds TradeHelper.dll against the installed game's
# Managed DLLs and lays out the mod folder in dist/TradeHelper. Pass "install" to also copy that folder
# to <game>/mods/TradeHelper (a new folder only; game files are not touched).
import os, shutil, subprocess, sys, hashlib, time
ROOT = r"E:\projects\outer-space-trade-mod"
GAME = r"E:\SteamLibrary\steamapps\common\Outer Space"
MANAGED = os.path.join(GAME, "Outer Space_Data", "Managed")
args = sys.argv[1:] if len(sys.argv) > 1 else os.environ.get("TH_ARGS", "").split()
r = subprocess.run(["dotnet", "build", os.path.join(ROOT, "OuterSpaceTradeHelper.csproj"), "-c", "Release",
                    "-p:GameManaged=" + MANAGED, "-nologo", "-v:minimal"],
                   capture_output=True, text=True, cwd=ROOT, creationflags=0x08000000)
print(r.stdout[-6000:], r.stderr[-3000:])
print("build rc", r.returncode)
if r.returncode != 0:
    sys.exit(1)
dll = os.path.join(ROOT, "bin", "Release", "TradeHelper.dll")
dist = os.path.join(ROOT, "dist", "TradeHelper")
shutil.rmtree(dist, ignore_errors=True)
os.makedirs(os.path.join(dist, "Manifest"))
os.makedirs(os.path.join(dist, "v_common", "Assemblies"))
shutil.copy2(os.path.join(ROOT, "Manifest", "manifest.xml"), os.path.join(dist, "Manifest", "manifest.xml"))
shutil.copy2(dll, os.path.join(dist, "v_common", "Assemblies", "TradeHelper.dll"))
h = hashlib.sha256(open(dll, "rb").read()).hexdigest()
print("dll", os.path.getsize(dll), h)
if "install" in args:
    # Replace only the mod's own files (Manifest, v_common); anything else in the folder, such as the
    # player's data folder, is left alone.
    target = os.path.join(GAME, "mods", "TradeHelper")
    os.makedirs(target, exist_ok=True)
    bak = os.path.join(ROOT, "dist", "replaced-" + time.strftime("%Y%m%d-%H%M%S"))
    for part in ("Manifest", "v_common"):
        cur = os.path.join(target, part)
        if os.path.isdir(cur):
            shutil.copytree(cur, os.path.join(bak, part))
            shutil.rmtree(cur)
        shutil.copytree(os.path.join(dist, part), cur)
    if os.path.isdir(bak):
        print("previous install backed up to", bak)
    print("installed to", target, "kept:", sorted(set(os.listdir(target)) - {"Manifest", "v_common"}))
