#!/usr/bin/env python3
"""Prove the three replacements for vacuous assertions can actually fail.

Replacing an assertion that cannot fail with another that cannot fail is the failure mode this
whole exercise is about, so each replacement gets its own mutation. Byte-exact restore.
"""

import io
import os
import re
import subprocess
import sys

# Derived from this file's own location, never hard-coded. The absolute path that used to sit here
# meant every run from a git worktree mutated the MAIN checkout instead -- editing files a
# concurrent session was working in, and scoring the wrong tree's assertions. The three sibling
# mutation harnesses already do it this way.
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HARDENING = os.path.join(REPO, "tests", "runtime-hardening-selftest.ps1")
APPUPDATE = os.path.join(REPO, "src", "dotNet", "AppUpdateCheck.cs")
SMOKETEST = os.path.join(REPO, "SMOKETEST.md")
SELFTESTS = os.path.join(REPO, "tests", "Invoke-SelfTests.ps1")
PETSPANE = os.path.join(REPO, "src", "Portable", "Wpf", "CompanionsPaneControl.cs")
PETSPANE_MODULES = os.path.join(REPO, "src", "Portable", "Wpf", "ModulesPaneControl.cs")
FORMPET = os.path.join(REPO, "src", "dotNet", "FormCompanion.cs")
STARTUP = os.path.join(REPO, "src", "dotNet", "StartUp.cs")
BUILDPS1 = os.path.join(REPO, "build.ps1")


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
    # The doc-count invariants. Both directions matter: the count going stale in the DOC, and the
    # count changing in the SOURCE without the doc following. One mutation each, because a check
    # written against only one side would pass while the other drifted -- which is how
    # "84 source invariants" and "18 self-tests" both survived being wrong.
    # The signing guard, with the -not dropped: every PR build would then call signtool with an
    # EMPTY thumbprint. The old form of this invariant matched the WORDS and survived exactly this.
    ("the signing guard fires on an EMPTY thumbprint",
     BUILDPS1,
     b"if (-not [string]::IsNullOrWhiteSpace($SigningCertThumbprint)) {",
     b"if ([string]::IsNullOrWhiteSpace($SigningCertThumbprint)) {",
     "guards signing on a NON-EMPTY thumbprint"),

    # The sass bypass, restored: this is the code as it shipped, calling FormCompanion.Say directly.
    ("the poke sass goes straight to a bubble again",
     STARTUP,
     b"                    if (Host == null || !Host.RaiseSpeechRequest(subject, s)) subject.Say(s);",
     b"                    subject.Say(s);",
     "the poke sass is offered to the speech responders"),

    # The consent ORDER check, mutated the way it would actually regress: the download moves AHEAD
    # of the consult. "Prefetch the payload while the user reads the prompt" is a plausible
    # optimisation, and it is precisely what the order assertion exists to forbid, because bytes on
    # disk before consent is a notification rather than a prompt.
    #
    # The first attempt at this case SURVIVED, and the reason is worth keeping: it wrapped the
    # consult in `if (false)`, which leaves the text exactly where it was, so the ORDER was still
    # correct. That mutation was aimed at REACHABILITY, which no source-text check can see -- the
    # assertion was not vacuous, the mutation was testing something else. Presence is covered by
    # the separate "consults ModulePermissionConsent at all" assertion beside it.
    (
        "the module payload is downloaded BEFORE the permission prompt",
        PETSPANE_MODULES,
        b"            ModulePermissions added = DesktopAICompanion.Plugins.ModulePermissionConsent.NewlyRequested(",
        b"            byte[] prefetched = await RemoteCatalogClient.DownloadVerifiedAsync(\n"
        b"                module.Url, module.Sha256, RemoteCatalogClient.MaximumModuleBytes, _netCts.Token);\n"
        b"            ModulePermissions added = DesktopAICompanion.Plugins.ModulePermissionConsent.NewlyRequested(",
        "BEFORE the update is downloaded",
    ),
    (
        "DOC DRIFT: SMOKETEST.md quotes the wrong invariant count",
        SMOKETEST,
        re.compile(rb"(\d+) source invariants"),
        rb"7\1 source invariants",
        "source-invariant count matches this file",
    ),
    (
        "SOURCE DRIFT: a self-test flag is added and no doc follows",
        SELFTESTS,
        b"    '--security-selftest'                = $null",
        b"    '--security-selftest'                = $null\n"
        b"    '--invented-selftest'                = $null",
        "self-test count matches Invoke-SelfTests.ps1",
    ),
    # One of three call sites made synchronous again. The count-based form is what makes this
    # detectable: a pattern-ordered check would still find a Task.Run somewhere in the file.
    (
        "a Pets-pane staleness diff goes back on the UI thread",
        PETSPANE,
        b"                List<StalePet> restale = await Task\n"
        b"                    .Run(delegate { return DiffStale(cached); }).ConfigureAwait(true);\n"
        b"                if (!IsLoaded) return;\n"
        b"                RenderUpdates(restale);",
        b"                RenderUpdates(DiffStale(cached));",
        "runs off the UI thread",
    ),
    (
        "the update card re-hashes the pet the diff already classified",
        PETSPANE,
        b"            CompanionFreshness freshness = entry.Freshness;",
        b"            CompanionFreshness freshness = FreshnessOf(entry.Pet);",
        "instead of re-hashing",
    ),
    # The fullscreen stand-down, whose two properties pull in opposite directions: the SCAN must be
    # shared (it was per companion, so one desktop-wide answer cost 16 z-order walks per cycle at
    # MAX_SHEEPS) while the ENFORCEMENT must not be throttled at all. A change that helps one and
    # breaks the other looks like an optimisation and re-ships "anything visible over a fullscreen
    # game", which is on SMOKETEST.md's regression watchlist. One mutation per direction.
    (
        "each companion walks the z-order for itself again",
        FORMPET,
        b"                blocked = Program.Mainthread != null\n"
        b"                    ? Program.Mainthread.BlockedMonitorsForStandDown()\n"
        b"                    : null;",
        b"                blocked = FullscreenScan.BlockedMonitors(\n"
        b"                    Program.Mainthread != null ? Program.Mainthread.SheepHandles() : null);",
        "shared per cycle",
    ),
    (
        "the shared cache forgets which MONITOR was blocked",
        STARTUP,
        b"            _fullscreenBlocked = blocked;\n",
        b"",
        "per MONITOR",
    ),
    (
        "a per-companion time gate is re-added ahead of the scan",
        FORMPET,
        b"            bool[] blocked;\n            try\n",
        b"            if ((DateTime.UtcNow - _lastRelocateUtc).TotalMilliseconds < 300) return;\n"
        b"            bool[] blocked;\n            try\n",
        "not re-throttled per companion",
    ),
)


# The ONE baseline failure a branch is allowed to carry, and the reason it is safe to score
# against. The self-test compares SMOKETEST.md's "N source invariants" figure to its own
# assertion count, so a branch that ADDS an assertion cannot be green until the doc is updated --
# which happens at the merge, in a file the branch may not own.
#
# What makes tolerating it rigorous rather than convenient: the self-test runs under
# ErrorActionPreference = Stop and Assert-True throws, so it aborts at its FIRST failing
# assertion -- and the doc-count invariants are its LAST two. Seeing this text in the output is
# therefore proof that every assertion before it passed. A mutated assertion aborts the run
# earlier and prints its own text instead of this one, so the two cannot be confused.
DOC_COUNT_DRIFT = "source-invariant count matches this file"


def saw_doc_count_drift(out):
    return any(DOC_COUNT_DRIFT in line and not line.strip().startswith("PASS:")
               for line in out.splitlines())



def line_ending_variant(base, old, new):
    """Pick the (old, new) pair whose line endings match the FILE being mutated.

    Every pattern in this file is written with LF. Several target files are CRLF in the working tree,
    and a CRLF file cannot contain an LF pattern, so those cases printed "NO-OP (pattern matched 0
    times)" and covered nothing at all -- a silent loss of coverage, which the release checklist is
    explicit is worse than a failure because it looks like a result.

    Measured 2026-09-25: this accounted for ALL SEVEN no-op cases across this harness and its sibling
    (one here, six in mutate-agentflow.py). Every one of them was read as "the source moved"; none of
    them had. CompanionsPaneControl.cs, for instance, is 1014 CRLF lines and 0 bare LF.

    Restoring is unaffected either way: the loops below write back the ORIGINAL bytes they read, so a
    file's line endings are never rewritten by a mutation run.
    """
    if base.count(old) == 1:
        return old, new
    as_crlf = lambda b: b.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    crlf_old, crlf_new = as_crlf(old), as_crlf(new)
    if base.count(crlf_old) == 1:
        return crlf_old, crlf_new
    return old, new

def main():
    print("baseline: the hardening self-test must pass before anything is scored")
    code, out = run()
    degraded = code != 0 and saw_doc_count_drift(out)
    if code != 0 and not degraded:
        print("BASELINE NOT GREEN, refusing to score.")
        print(out[-1200:])
        return 2
    if degraded:
        print("  *** BASELINE DEGRADED: SMOKETEST.md's invariant count is not updated yet. ***")
        print("  Scoring continues: the self-test aborts at its FIRST failure and the doc count is")
        print("  its LAST assertion, so everything this harness scores ran and passed.")
    print("  baseline %s (%d assertions)\n"
          % ("DEGRADED" if degraded else "PASS", out.count("PASS:")))

    fired = 0
    for name, path, old, new, expect in CASES:
        base = read(path)
        # `old` may be a compiled regex. That exists for one reason: a case whose target is a number
        # the suite is SUPPOSED to change (the documented assertion count) would otherwise go no-op
        # the first time an assertion is added, print "matched 0 times", and quietly stop covering
        # anything -- which is exactly the rot mutate-diagnostics.py was carrying.
        if hasattr(old, "subn"):
            mutant, count = old.subn(new, base)
        else:
            old_v, new_v = line_ending_variant(base, old, new)
            count = base.count(old_v)
            mutant = base.replace(old_v, new_v)
        if count != 1:
            print("  %-46s NO-OP (pattern matched %d times)" % (name, count))
            continue
        write(path, mutant)
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
        # A non-zero exit means this mutation broke something -- unless the baseline was degraded,
        # in which case the doc-count failure is there whatever the mutation did. It broke the RIGHT
        # thing when the expected assertion text appears anywhere in the output on a line that is
        # not a PASS.
        bad = [l.strip() for l in out.splitlines()
               if expect in l and not l.strip().startswith("PASS:")]
        if bad:
            fired += 1
            print("  %-46s FIRED" % name)
            print("        %s" % bad[0][:150])
        elif degraded and saw_doc_count_drift(out):
            # The run reached the LAST assertion, so nothing this mutation touched was noticed.
            # Reported as SURVIVED and not as WRONG: with a degraded baseline a non-zero exit is
            # not evidence of anything on its own, and calling it "failed on something else" would
            # read as a harness fault rather than as a vacuous assertion.
            print("  %-46s SURVIVED -- only the baseline doc-count failure" % name)
        else:
            print("  %-46s WRONG -- failed on something else" % name)
            other = [l.strip() for l in out.splitlines() if "failed" in l][:2]
            for line in other[:2]:
                print("        %s" % line[:150])

    print("\n%d/%d fired." % (fired, len(CASES)))
    return 0 if fired == len(CASES) else 1


if __name__ == "__main__":
    sys.exit(main())
