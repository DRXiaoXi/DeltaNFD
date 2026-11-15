#!/usr/bin/env python3
"""Build unsigned AMX and run it with fake kernel natives, never a driver."""
import argparse
import hashlib
import json
import pathlib
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("pawncc", "modules-include", "pawnpp", "cl", "vc-include", "vc-lib", "sdk-include", "sdk-lib", "output"):
        parser.add_argument("--" + name, required=True, type=pathlib.Path)
    parser.add_argument("--omit-oldnames", action="store_true", help="For a standalone CRT subset without oldnames.lib")
    args = parser.parse_args()
    source = pathlib.Path(__file__).resolve().parent
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    commands = []

    def run(name, command):
        command = [str(x) for x in command]
        commands.append({"name": name, "argv": command})
        result = subprocess.run(command, cwd=out, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        (out / (name + ".log")).write_bytes(result.stdout)
        # All successful tool output is ASCII; retain raw diagnostics in the log.
        print(result.stdout.decode("utf-8", errors="replace").encode("ascii", errors="backslashreplace").decode("ascii").strip())
        if result.returncode:
            raise RuntimeError(f"{name} failed ({result.returncode}); see its log")
        if b"warning" in result.stdout.lower():
            raise RuntimeError(f"{name} emitted a warning; see its log")
        return result.stdout

    amx = out / "TimerResolution.amx"
    run("module-build", [args.pawncc.resolve(), source / "TimerResolution.p", "-C64", "-;+", "-(+", "-p",
                         "-i" + str(args.modules_include.resolve()), "-o" + str(amx)])
    exe = out / "timer_vm_test.exe"
    command = [args.cl.resolve(), "/nologo", "/std:c++20", "/EHsc", "/MT", "/O2", "/W4", "/WX",
               "/I" + str(args.vc_include.resolve()), "/I" + str(args.sdk_include.resolve() / "ucrt"),
               "/I" + str(args.sdk_include.resolve() / "shared"), "/I" + str(args.sdk_include.resolve() / "um"),
               "/I" + str(args.pawnpp.resolve()), source / "tests/timer_vm_test.cpp", "/Fe" + str(exe), "/link",
               "/LIBPATH:" + str(args.vc_lib.resolve()), "/LIBPATH:" + str(args.sdk_lib.resolve() / "ucrt/x64"),
               "/LIBPATH:" + str(args.sdk_lib.resolve() / "um/x64")]
    if args.omit_oldnames:
        command += ["/NODEFAULTLIB:OLDNAMES.lib"]
    run("test-build", command)
    result = run("vm-tests", [exe, amx])
    report = {
        "status": "passed",
        "moduleSigned": False,
        "realKernelCalls": False,
        "driverInstalledOrLoaded": False,
        "summary": result.decode("ascii").strip(),
        "commands": commands,
        "files": [{"name": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                  for path in (source / "TimerResolution.p", source / "tests/timer_vm_test.cpp", amx, exe)],
    }
    (out / "test-result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
