"""Write a full dump of a frozen game process and list every thread's stack with managed method names.

python tools/freeze_dump.py <pid> <out-dir>
Managed (JIT) frames are named from Unity's pmip files, which the player writes to %TEMP% when it was started with
UNITY_MIXED_CALLSTACK=1 (tools/run_isolated.py does that). Needs WinDbg's cdb (installed from the Microsoft Store).
"""
import glob, os, re, subprocess, sys

def cdb_path():
    r = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-AppxPackage -Name Microsoft.WinDbg).InstallLocation"],
                       capture_output=True, text=True, encoding="gbk", errors="replace")
    p = os.path.join(r.stdout.strip(), "amd64", "cdb.exe")
    return p if os.path.exists(p) else None

def write_dump(pid, path):
    ps = r'''
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
public static class Dbg { [DllImport("dbghelp.dll", SetLastError=true)] public static extern bool MiniDumpWriteDump(IntPtr h, uint pid, SafeFileHandle f, uint t, IntPtr e, IntPtr u, IntPtr c); }
"@
$p = Get-Process -Id %PID%; $fs = [System.IO.File]::Create("%PATH%")
$ok = [Dbg]::MiniDumpWriteDump($p.Handle, [uint32]%PID%, $fs.SafeFileHandle, [uint32]0x2, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero); $fs.Close(); "dump ok=$ok"
'''.replace("%PID%", str(pid)).replace("%PATH%", path)
    r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True, encoding="gbk", errors="replace")
    print(r.stdout.strip(), r.stderr[-500:])

def read_shared(path):
    """The player opens its pmip file delete-on-close; reading it needs FILE_SHARE_DELETE as well."""
    import ctypes
    from ctypes import wintypes
    k = ctypes.WinDLL("kernel32", use_last_error=True)
    k.CreateFileW.restype = wintypes.HANDLE
    h = k.CreateFileW(path, 0x80000000, 0x7, None, 3, 0x80, None)   # GENERIC_READ, share read|write|delete, OPEN_EXISTING
    if h in (None, wintypes.HANDLE(-1).value):
        raise OSError(ctypes.get_last_error(), "CreateFileW failed", path)
    chunks, buf, n = [], ctypes.create_string_buffer(1 << 20), wintypes.DWORD()
    try:
        while k.ReadFile(h, buf, len(buf), ctypes.byref(n), None) and n.value:
            chunks.append(buf.raw[:n.value])
    finally:
        k.CloseHandle(h)
    return b"".join(chunks).decode("utf-8", "replace")

def load_pmip(pid):
    ranges = []
    for f in glob.glob(os.path.join(os.environ.get("TEMP", ""), "pmip_%d_*.txt" % pid)):
        for line in read_shared(f).splitlines():
            parts = line.rstrip("\n").split(";", 2)
            if len(parts) == 3:
                try: ranges.append((int(parts[0], 16), int(parts[1], 16), parts[2]))
                except ValueError: pass
    ranges.sort()
    return ranges

def name(ranges, addr):
    lo, hi = 0, len(ranges) - 1
    while lo <= hi:
        mid = (lo + hi) // 2
        s, e, n = ranges[mid]
        if addr < s: hi = mid - 1
        elif addr > e: lo = mid + 1
        else: return n
    return None

def main():
    pid, out = int(sys.argv[1]), sys.argv[2]
    os.makedirs(out, exist_ok=True)
    dump = os.path.join(out, "frozen-%d.dmp" % pid)
    write_dump(pid, dump)
    ranges = load_pmip(pid)     # before the process ends: the file is deleted with it
    print("pmip ranges:", len(ranges))
    subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    cdb = cdb_path()
    if not cdb: print("no cdb"); return
    r = subprocess.run([cdb, "-z", dump, "-c", "~*k 40; q"], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=1800)
    lines, res = r.stdout.splitlines(), []
    for l in lines:
        m = re.match(r"^\s*([0-9a-f`]{17})\s+([0-9a-f`]{17})\s+(0x[0-9a-f`]+)\s*$", l)
        if m:
            addr = int(m.group(3)[2:].replace("`", ""), 16)
            res.append(l + "    " + (name(ranges, addr) or "?"))
        else:
            res.append(l)
    text = "\n".join(res)
    open(os.path.join(out, "frozen-%d-stacks.txt" % pid), "w", encoding="utf-8").write(text)
    # Print the threads that wait on a monitor, with named frames only.
    for block in text.split("\n\n"):
        if "mono_monitor_enter" in block and ("Thread Pool" in block or block.lstrip().startswith(".  0") or " 0  Id" in block[:20]):
            head = block.strip().splitlines()[0]
            named = [l.split("    ")[-1] for l in block.splitlines() if "    " in l and not l.endswith("?")]
            print("==", head); print("\n".join("   " + n for n in named[:30]))

if __name__ == "__main__":
    main()
