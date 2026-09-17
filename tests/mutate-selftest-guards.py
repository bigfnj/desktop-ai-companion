#!/usr/bin/env python3
"""Prove the two C# assertion replacements can fail. Rebuilds the host, byte-exact restore."""

import io
import os
import subprocess
import sys
import time

REPO = r"D:\.ai-work\projects\desktop-ai-companion"
EXE = os.path.join(REPO, "build", "DesktopAICompanionPortable", "bin", "Release", "x64",
                   "DesktopAICompanion.exe")
CSPROJ = os.path.join(REPO, "src", "DesktopAICompanion_Portable.csproj")
HARD = os.path.join(REPO, "src", "dotNet", "RuntimeHardeningSelfTest.cs")
HOST = os.path.join(REPO, "src", "dotNet", "Plugins", "CompanionHost.cs")
TEMP = os.environ.get("TEMP", ".")


def read(p):
    with io.open(p, "rb") as h:
        return h.read()


def write(p, d):
    with io.open(p, "wb") as h:
        h.write(d)


def build():
    proc = subprocess.run(["dotnet", "build", CSPROJ, "-c", "Release", "--nologo", "-v:quiet"],
                          capture_output=True, text=True, timeout=1800)
    return proc.returncode == 0, (proc.stdout or "")


def selftest(flag, marker):
    path = os.path.join(TEMP, marker)
    try:
        os.remove(path)
    except OSError:
        pass
    subprocess.run([EXE, flag], capture_output=True, text=True, timeout=1800)
    if not os.path.isfile(path):
        return None
    with io.open(path, encoding="utf-8", errors="replace") as h:
        return h.read()


CASES = (
    ("CheckAccepts given an input that is REJECTED",
     HARD,
     b'                    CheckAccepts("exact sprite pixel budget accepted",\n'
     b'                        () => validateBudget.Invoke(null, new object[] { 32, 32, 128, 128 }));',
     b'                    CheckAccepts("exact sprite pixel budget accepted",\n'
     b'                        () => validateBudget.Invoke(null, new object[] { 41, 25, 1, 1 }));',
     "--hardening-selftest", "dp-hardening-selftest.txt", "exact sprite pixel budget accepted"),

    ("an EMPTY hotkey combo returns null instead of a no-op handle",
     HOST,
     b"            if (string.IsNullOrWhiteSpace(combo) || onPressed == null) return new Noop();",
     b"            if (string.IsNullOrWhiteSpace(combo) || onPressed == null) return null;",
     "--aibrain-selftest", "dp-aibrain-selftest.txt", "EMPTY combo"),
)


def main():
    print("baseline: build + both self-tests must pass")
    ok, out = build()
    if not ok:
        print("BASELINE BUILD FAILED"); print(out[-800:]); return 2
    for flag, marker in (("--hardening-selftest", "dp-hardening-selftest.txt"),
                         ("--aibrain-selftest", "dp-aibrain-selftest.txt")):
        rep = selftest(flag, marker)
        if rep is None or "FAIL" in rep:
            print("BASELINE NOT GREEN for", flag); return 2
    print("  baseline clean\n")

    fired = 0
    for name, path, old, new, flag, marker, expect in CASES:
        base = read(path)
        if base.count(old) != 1:
            print("  %-52s NO-OP (matched %d)" % (name, base.count(old)))
            continue
        before = os.path.getmtime(EXE)
        write(path, base.replace(old, new))
        time.sleep(1.1)
        try:
            built, berr = build()
            if not built:
                print("  %-52s BROKEN (does not compile)" % name)
                continue
            if os.path.getmtime(EXE) <= before:
                print("  %-52s BROKEN (exe not rebuilt)" % name)
                continue
            rep = selftest(flag, marker)
        finally:
            write(path, base)

        if rep is None:
            print("  %-52s BROKEN (no marker)" % name)
            continue
        hit = [l.strip() for l in rep.splitlines() if l.strip().startswith("FAIL") and expect in l]
        if hit:
            fired += 1
            print("  %-52s FIRED" % name)
            print("        %s" % hit[0][:140])
        elif "FAIL" in rep:
            print("  %-52s WRONG -- failed elsewhere" % name)
            for l in [x.strip() for x in rep.splitlines() if x.strip().startswith("FAIL")][:2]:
                print("        %s" % l[:140])
        else:
            print("  %-52s SURVIVED" % name)

    print("\nrestoring and rebuilding the clean tree")
    build()
    print("%d/%d fired." % (fired, len(CASES)))
    return 0 if fired == len(CASES) else 1


if __name__ == "__main__":
    sys.exit(main())
