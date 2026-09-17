#!/usr/bin/env python3
"""Prove the three replacements for vacuous assertions can actually fail.

Replacing an assertion that cannot fail with another that cannot fail is the failure mode this
whole exercise is about, so each replacement gets its own mutation. Byte-exact restore.
"""

import io
import os
import subprocess
import sys

REPO = r"D:\.ai-work\projects\desktop-ai-companion"
HARDENING = os.path.join(REPO, "tests", "runtime-hardening-selftest.ps1")
APPUPDATE = os.path.join(REPO, "src", "dotNet", "AppUpdateCheck.cs")


def read(p):
    with io.open(p, "rb") as h:
        return h.read()


def write(p, data):
    with io.open(p, "wb") as h:
        h.write(data)


def run():
    proc = subprocess.run(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", HARDENING],
        capture_output=True, text=True, timeout=900)
    return (proc.returncode, (proc.stdout or "") + (proc.stderr or ""))


CASES = (
    (
        "ORDER: stamp the result before fetching it",
        APPUPDATE,
        b"                string latest = await RemoteCatalogClient.FetchAppVersionAsync(token).ConfigureAwait(false);",
        b"                string latest = null; data.SetAppUpdateResult(DateTimeOffset.UtcNow, \"\");\n"
        b"                latest = await RemoteCatalogClient.FetchAppVersionAsync(token).ConfigureAwait(false);",
        "FETCHES before it stamps",
    ),
    (
        "the sidebar sample window is empty",
        HARDENING,
        b"    $sampleX1 = [int](355.0 / 370.0 * $dialogBmp.Width)",
        b"    $sampleX1 = $sampleX0",
        "sample window is non-empty",
    ),
    (
        "the .wxs set is empty",
        HARDENING,
        b"$wxsFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'installer') -Filter '*.wxs' -File)",
        b"$wxsFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'installer') -Filter '*.nosuchext' -File)",
        "at least one .wxs",
    ),
)


def main():
    print("baseline: the hardening self-test must pass before anything is scored")
    code, out = run()
    if code != 0:
        print("BASELINE NOT GREEN, refusing to score.")
        print(out[-1200:])
        return 2
    print("  baseline PASS (%d assertions)\n" % out.count("PASS:"))

    fired = 0
    for name, path, old, new, expect in CASES:
        base = read(path)
        count = base.count(old)
        if count != 1:
            print("  %-46s NO-OP (pattern matched %d times)" % (name, count))
            continue
        write(path, base.replace(old, new))
        try:
            code, out = run()
        finally:
            write(path, base)

        if code == 0:
            print("  %-46s SURVIVED -- the assertion is still vacuous" % name)
            continue
        # Do NOT key on the word "failed.". PowerShell renders a thrown error across several lines
        # and splits the message from that word, so the assertion text and "failed." end up on
        # DIFFERENT lines -- which made this harness report WRONG for a case that fired correctly.
        # It also puts `throw "$Name failed."` (the helper's own source) in the rendering, which a
        # naive match picks up; mutate-diagnostics.py records that half of the trap.
        #
        # The baseline is green, so a non-zero exit means this mutation broke something. It broke
        # the RIGHT thing when the expected assertion text appears anywhere in the output on a line
        # that is not a PASS.
        bad = [l.strip() for l in out.splitlines()
               if expect in l and not l.strip().startswith("PASS:")]
        if bad:
            fired += 1
            print("  %-46s FIRED" % name)
            print("        %s" % bad[0][:150])
        else:
            print("  %-46s WRONG -- failed on something else" % name)
            other = [l.strip() for l in out.splitlines() if "failed" in l][:2]
            for line in other[:2]:
                print("        %s" % line[:150])

    print("\n%d/%d fired." % (fired, len(CASES)))
    return 0 if fired == len(CASES) else 1


if __name__ == "__main__":
    sys.exit(main())
