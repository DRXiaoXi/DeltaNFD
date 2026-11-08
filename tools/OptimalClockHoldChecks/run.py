"""No-load check of a real holder child: policy readback, resolution and EOF cleanup."""
import ctypes
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time

exe, mode, seconds, output = Path(sys.argv[1]).resolve(), sys.argv[2], int(sys.argv[3]), Path(sys.argv[4])
assert mode in ("normal", "preset-ignore") and 1 <= seconds <= 60

class Power(ctypes.Structure):
    _fields_ = [("version", ctypes.c_uint32), ("control", ctypes.c_uint32), ("state", ctypes.c_uint32)]

kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.GetProcessInformation.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.POINTER(Power), ctypes.c_uint32]
kernel.GetProcessInformation.restype = ctypes.c_int
query = ctypes.WinDLL("ntdll").NtQueryTimerResolution
query.argtypes = [ctypes.POINTER(ctypes.c_uint32)] * 3
query.restype = ctypes.c_int
args = [str(exe), "--optimal-clock-hold" if mode == "normal" else "--preset-ignore", "5170"]
child = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                         text=True, creationflags=subprocess.CREATE_NO_WINDOW | subprocess.BELOW_NORMAL_PRIORITY_CLASS)
record = dict(executable=str(exe), sha256=hashlib.sha256(exe.read_bytes()).hexdigest(), mode=mode,
              startedUtc=datetime.now(timezone.utc).isoformat(), pid=child.pid, samples=[])
record["assemblyHashes"] = {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                            for path in (exe.with_suffix(".dll"), exe.parent / "DeltaNFD.Execution.dll") if path.exists()}
failure = None
try:
    lines = queue.Queue()
    threading.Thread(target=lambda: lines.put(child.stdout.readline()), daemon=True).start()
    record["ready"] = lines.get(timeout=15).strip()
    assert record["ready"].startswith("HELD:true:5170:"), "holder did not become ready"
    policy = Power(1, 0, 0)
    if not kernel.GetProcessInformation(int(child._handle), 4, ctypes.byref(policy), 12):
        raise ctypes.WinError(ctypes.get_last_error())
    record["policy"] = dict(control=policy.control, state=policy.state)
    assert policy.control & 4 and not policy.state & 4, "holder has not explicitly honored timer requests"
    started = time.monotonic()
    while time.monotonic() - started < seconds:
        minimum, maximum, current = (ctypes.c_uint32() for _ in range(3))
        status = query(ctypes.byref(minimum), ctypes.byref(maximum), ctypes.byref(current))
        alive = child.poll() is None
        record["samples"].append(dict(ms=round((time.monotonic()-started)*1000), status=status,
                                      current100ns=current.value, alive=alive))
        assert status >= 0 and alive, "holder exited or system query failed"
        time.sleep(0.05)
    assert all(abs(row["current100ns"] - 5170) <= 50 for row in record["samples"]), "resolution drifted from target"
except Exception as error:
    failure = f"{type(error).__name__}: {error}"
finally:
    child.stdin.close()
    try:
        record["exitCode"] = child.wait(timeout=8)
    except subprocess.TimeoutExpired:
        child.kill()
        record["exitCode"] = child.wait(timeout=8)
        failure = failure or "holder did not exit after stdin closed"
    record["stderr"] = child.stderr.read()
    record["childExited"] = child.poll() is not None
    record["failure"] = failure
    record["counts"] = {str(value): sum(row["current100ns"] == value for row in record["samples"])
                        for value in sorted({row["current100ns"] for row in record["samples"]})}
    record["completedUtc"] = datetime.now(timezone.utc).isoformat()
    output.write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: record.get(key) for key in ("mode", "pid", "policy", "counts", "exitCode", "childExited", "failure")}, ensure_ascii=False))
sys.exit(1 if failure or record["exitCode"] != 0 else 0)
