"""Run the mod's self-test in an isolated copy of the game and wait for its result file.

python tools/run_isolated.py [--save zh6657] [--tests all] [--limit 900] [--no-build] [--refresh-runtime]

Runtime: local/runtime (a copy of the installed game). The copy differs from the original in two places only:
  - Outer Space_Data/globalgamemanagers: product name "Outer Space" -> "OS TH Tests" (same length, one occurrence), so
    the copy keeps its saves, settings and logs in LocalLow/CatStudio/OS TH Tests instead of the player's folder;
  - steam_appid.txt next to the exe, so Steam does not restart the original game (SteamManager.RestartAppIfNecessary).
The original installation and the player's LocalLow/CatStudio/Outer Space are only read (one save is copied).
The self-test runs only when <runtime>/TradeHelperSelfTest.json exists; it writes <runtime>/selftest-result.json.
"""
import argparse, json, os, re, shutil, subprocess, sys, time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
GAME = Path(r"E:\SteamLibrary\steamapps\common\Outer Space")
LOCALLOW = Path(os.environ["USERPROFILE"]) / "AppData" / "LocalLow" / "CatStudio"
PLAYER_DATA = LOCALLOW / "Outer Space"
TEST_NAME = b"OS TH Tests"
TEST_DATA = LOCALLOW / TEST_NAME.decode()
RUNTIME = ROOT / "local" / "runtime"
APP_ID = "3080070"
SKIP_TOP = {"mods", "Outer Space_BurstDebugInformation_DoNotShip"}


def copy_game(refresh):
    """Copy the game (incremental: files with the same size and time are kept)."""
    n = 0
    for src_dir, dirs, files in os.walk(GAME):
        rel = Path(src_dir).relative_to(GAME)
        if rel.parts and rel.parts[0] in SKIP_TOP:
            dirs[:] = []
            continue
        dst_dir = RUNTIME / rel
        dst_dir.mkdir(parents=True, exist_ok=True)
        for f in files:
            s, d = Path(src_dir) / f, dst_dir / f
            if not refresh and d.exists() and d.stat().st_size == s.stat().st_size and int(d.stat().st_mtime) == int(s.stat().st_mtime):
                continue
            shutil.copy2(s, d)
            n += 1
    print("game files copied/updated:", n)


def patch_product_name():
    ggm = RUNTIME / "Outer Space_Data" / "globalgamemanagers"
    b = bytearray(ggm.read_bytes())
    old = b"\x09\x00\x00\x00CatStudio\x00\x00\x00\x0b\x00\x00\x00Outer Space"
    new = b"\x09\x00\x00\x00CatStudio\x00\x00\x00\x0b\x00\x00\x00" + TEST_NAME
    assert len(TEST_NAME) == 11
    if b.count(new) == 1:
        print("product name already patched")
        return
    assert b.count(old) == 1, "expected exactly one company/product name record"
    i = b.find(old)
    b[i:i + len(old)] = new
    ggm.write_bytes(bytes(b))
    print("product name patched at", i)


def patch_app_info():
    """Unity also takes the log folder from app.info (company, product on two lines)."""
    p = RUNTIME / "Outer Space_Data" / "app.info"
    lines = p.read_text(encoding="utf-8").splitlines()
    assert len(lines) >= 2 and lines[0] == "CatStudio", lines
    if lines[1] != TEST_NAME.decode():
        assert lines[1] == "Outer Space", lines
        lines[1] = TEST_NAME.decode()
        p.write_text("\n".join(lines), encoding="utf-8")
        print("app.info patched")


def newest_save(prefix):
    """The player's most recent save of that game (manual or autosave), by the time its .arc was written."""
    best = None
    for d in (PLAYER_DATA / "archives").iterdir():
        if not d.is_dir() or not d.name.startswith(prefix):
            continue
        arcs = list(d.glob("*.arc"))
        # Skip a save written in the last minute: the running game may still be writing it.
        if arcs and time.time() - arcs[0].stat().st_mtime < 60:
            continue
        if arcs and (best is None or arcs[0].stat().st_mtime > best[0]):
            best = (arcs[0].stat().st_mtime, d.name)
    return best[1] if best else prefix


def prepare_data(save):
    """A fresh test data folder with one copied save (the player's folder is only read)."""
    assert TEST_DATA != PLAYER_DATA and TEST_DATA.name == TEST_NAME.decode()
    archives = TEST_DATA / "archives"
    if archives.exists():
        shutil.rmtree(archives)
    archives.mkdir(parents=True)
    shutil.copytree(PLAYER_DATA / "archives" / save, archives / save)
    for name in ("mod.dat", "start_config", "userInfo", "versionCode"):
        s = PLAYER_DATA / name
        if s.is_dir():
            if (TEST_DATA / name).exists():
                shutil.rmtree(TEST_DATA / name)
            shutil.copytree(s, TEST_DATA / name)
        elif s.exists():
            shutil.copy2(s, TEST_DATA / name)
    # Audio and asset caches only make the first start faster.
    if (PLAYER_DATA / "Assets").exists() and not (TEST_DATA / "Assets").exists():
        shutil.copytree(PLAYER_DATA / "Assets", TEST_DATA / "Assets")
    th = TEST_DATA / "TradeHelper"
    if th.exists():
        shutil.rmtree(th)   # default settings, no plans: every run starts the same way
    print("test data:", TEST_DATA, "save:", save)


def install_mod(no_build):
    if not no_build:
        r = subprocess.run([sys.executable, str(ROOT / "tools" / "build.py")], capture_output=True, text=True,
                           encoding="utf-8", errors="replace")
        print(r.stdout[-1500:])
        if r.returncode:
            sys.exit("build failed")
    mods = RUNTIME / "mods"
    if mods.exists():
        shutil.rmtree(mods)
    mods.mkdir()
    # The player's other installed mods (code and manifest; their data stays with the player): a save that used them
    # loads without the game's "missing mod" question, which would stop the test.
    for other in (GAME / "mods").iterdir() if (GAME / "mods").exists() else []:
        if other.is_dir() and other.name != "TradeHelper":
            for part in ("Manifest", "v_common"):
                if (other / part).exists():
                    shutil.copytree(other / part, mods / other.name / part)
    shutil.copytree(ROOT / "dist" / "TradeHelper", mods / "TradeHelper")
    print("mods in the test copy:", sorted(p.name for p in mods.iterdir()))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--save", default="zh6657", help="a save folder name; with --newest, the prefix of the player's saves")
    ap.add_argument("--exact-save", action="store_true", help="use --save as given instead of the newest save starting with it")
    ap.add_argument("--tests", default="all")
    ap.add_argument("--limit", type=int, default=900, help="seconds; use about 1800 with --tests settle")
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--refresh-runtime", action="store_true")
    a = ap.parse_args()

    # One test copy at a time (the original game, started from Steam, is not counted).
    ps = subprocess.run(["powershell", "-NoProfile", "-Command",
                         "Get-Process -Name 'Outer Space' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Path"],
                        capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
    if str(RUNTIME).lower() in ps.lower():
        sys.exit("a test copy of the game is already running")
    copy_game(a.refresh_runtime)
    patch_product_name()
    patch_app_info()
    if not a.exact_save:
        a.save = newest_save(a.save)
    (RUNTIME / "steam_appid.txt").write_text(APP_ID, encoding="ascii")
    prepare_data(a.save)
    install_mod(a.no_build)

    res = RUNTIME / "selftest-result.json"
    if res.exists():
        res.unlink()
    (RUNTIME / "TradeHelperSelfTest.json").write_text(json.dumps({"save": a.save, "tests": a.tests}), encoding="utf-8")
    log = RUNTIME / "selftest-player.log"
    if log.exists():
        log.unlink()
    si = subprocess.STARTUPINFO()
    si.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    si.wShowWindow = 7   # minimised, not activated: the player's window keeps the focus
    env = dict(os.environ, UNITY_MIXED_CALLSTACK="1")   # pmip files name the managed frames in a dump
    proc = subprocess.Popen([str(RUNTIME / "Outer Space.exe"), "-screen-fullscreen", "0", "-screen-width", "1600",
                             "-screen-height", "900", "-logFile", str(log)], cwd=RUNTIME, startupinfo=si, env=env)
    print("started pid", proc.pid)
    t0 = time.time()
    beat = RUNTIME / "selftest-heartbeat.txt"
    if beat.exists():
        beat.unlink()
    hung = None
    while time.time() - t0 < a.limit and not res.exists() and proc.poll() is None:
        time.sleep(3)
        # A heartbeat that stops for a minute while the process is alive means the game froze.
        if beat.exists() and time.time() - beat.stat().st_mtime > 60:
            hung = beat.read_text(encoding="utf-8", errors="replace")
            print("FROZEN: no heartbeat since", time.ctime(beat.stat().st_mtime), "| last:", hung)
            r = subprocess.run([sys.executable, str(ROOT / "tools" / "freeze_dump.py"), str(proc.pid), str(ROOT / "local" / "dumps")],
                               capture_output=True, text=True, encoding="utf-8", errors="replace")
            print(r.stdout[-6000:], r.stderr[-1500:])
            break
    time.sleep(5)
    if proc.poll() is None:
        proc.kill()
    print("elapsed", round(time.time() - t0), "exit", proc.poll())
    mod_exceptions = 0
    if log.exists():
        lines = log.read_text(encoding="utf-8", errors="replace").splitlines()
        quit_at = next((i for i, l in enumerate(lines) if "Application Quit" in l), len(lines))
        exc = [i for i, l in enumerate(lines) if re.match(r"^[\w.]*Exception\b", l)]
        mod_exc = [i for i in exc if any("TradeHelper" in lines[j] for j in range(i, min(len(lines), i + 15)))]
        mod_exceptions = len(mod_exc)
        print("--- game log: %d exception stacks (%d before quit), %d with mod code ---" % (len(exc), sum(1 for i in exc if i < quit_at), len(mod_exc)))
        for i in mod_exc[:3]:
            print("\n".join(lines[i:i + 12]))
        print("\n".join(l for l in lines if "[TradeHelper]" in l)[-6000:])
    print("--- result ---")
    print(res.read_text(encoding="utf-8")[-12000:] if res.exists() else "NO RESULT")
    try:
        passed = json.loads(res.read_text(encoding="utf-8")).get("passed") is True if res.exists() else False
    except ValueError:
        passed = False
    if hung is not None:
        passed = False
        print("FAIL: the game froze")
    if mod_exceptions:
        passed = False
        print("FAIL: exceptions thrown from mod code")
    if not passed:
        sys.exit("self-test FAILED")


if __name__ == "__main__":
    main()
