"""Behavioural soak for a converted Shimeji: how often does it actually reach the wall and ceiling?

Models the engine's animation state machine as Animations.cs implements it, not as the XML reads:

  * <next probability="..."> values are WEIGHTS, not percentages. SetNextGeneralAnimation totals the
    weight of the ELIGIBLE edges only and picks uniformly in [0, total). A border block summing to 5
    is therefore not "a 5% chance of anything"; it is a normalised choice among whichever edges match
    the situation.
  * Eligible(only, where): only=none matches everywhere; otherwise a bitwise AND of the TOnly flags.
  * A sequence's <next> fires only when the sequence RUNS OUT of frames. An animation with a large
    repeat count can travel far enough to hit a border first, in which case the <border> block fires
    and the sequence edges never get a say. ClimbWall's repeat=111 at y=-6 is exactly this case.
  * `turn` negates the x velocities, which is how a converted skin walks right at all: it carries only
    a leftward walk and mirrors it.

Deliberately models a BARE DESKTOP: floor, two screen walls, ceiling, no windows. That is the case
being observed, and adding windows only creates more paths to the wall, never fewer.

Usage: %TOOLBOX_PYTHON% shimeji_soak.py <animations.xml> [--minutes 30] [--runs 400] [--width 1920]
"""

import io
import os
import random
import re
import sys
from collections import defaultdict

# TOnly, from Animations.cs
NONE, TASKBAR, WINDOW, HORIZONTAL = 0x7F, 0x01, 0x02, 0x04
HORIZONTAL_, VERTICAL = 0x06, 0x08
WINDOW_LEFT, WINDOW_RIGHT, WINDOW_TOP, WINDOW_BOTTOM = 0x10, 0x20, 0x40, 0x80

ONLY = {
    "": NONE, "none": NONE, "taskbar": TASKBAR, "window": WINDOW,
    "horizontal": HORIZONTAL_, "vertical": VERTICAL,
    "window-left": WINDOW_LEFT, "window-right": WINDOW_RIGHT,
    "window-top": WINDOW_TOP, "window-bottom": WINDOW_BOTTOM,
}


def eligible(only, where):
    if only == NONE:
        return True
    return (only & where) != 0


class Anim(object):
    __slots__ = ("id", "name", "x0", "x1", "y0", "y1", "i0", "i1",
                 "frames", "repeat", "seq", "border", "gravity")


def parse(path):
    s = io.open(path, encoding="utf-8-sig").read()
    s = re.sub(r"<png>[^<]*</png>", "", s)
    anims = {}
    for m in re.finditer(r'<animation id="(\d+)"[^>]*>(.*?)</animation>', s, re.S):
        aid, body = int(m.group(1)), m.group(2)
        a = Anim()
        a.id = aid
        nm = re.search(r"<name>([^<]*)</name>", body)
        a.name = nm.group(1) if nm else str(aid)

        def val(blk, tag, dflt=0):
            b = re.search(r"<%s>(.*?)</%s>" % (blk, blk), body, re.S)
            if not b:
                return dflt
            v = re.search(r"<%s>(-?\d+)</%s>" % (tag, tag), b.group(1))
            return int(v.group(1)) if v else dflt

        a.x0, a.x1 = val("start", "x"), val("end", "x")
        a.y0, a.y1 = val("start", "y"), val("end", "y")
        a.i0 = val("start", "interval", 100) or 100
        a.i1 = val("end", "interval", a.i0) or a.i0

        sq = re.search(r"<sequence([^>]*)>(.*?)</sequence>", body, re.S)
        a.frames = len(re.findall(r"<frame>\d+</frame>", sq.group(2))) if sq else 1
        a.frames = max(1, a.frames)
        a.repeat = 0
        if sq:
            r = re.search(r'repeat="(\d+)"', sq.group(1))
            a.repeat = int(r.group(1)) if r else 0

        def edges(blk):
            b = re.search(r"<%s[^>]*>(.*?)</%s>" % (blk, blk), body, re.S)
            if not b:
                return []
            out = []
            for e in re.finditer(r'<next probability="(-?\d+)"(?: only="([^"]*)")?>(\d+)</next>',
                                 b.group(1)):
                out.append((max(0, int(e.group(1))),
                            ONLY.get((e.group(2) or "none").lower(), NONE),
                            int(e.group(3))))
            return out

        a.seq = edges("sequence") if sq else []
        a.border = edges("border")
        a.gravity = edges("gravity")
        anims[aid] = a
    return anims


def pick(edges, where, rng):
    """SetNextGeneralAnimation: total the eligible weights, choose uniformly, walk the same order."""
    total = sum(w for w, only, _ in edges if eligible(only, where))
    if total <= 0:
        return None
    r = rng.randrange(total)
    cum = 0
    for w, only, tgt in edges:
        if not eligible(only, where):
            continue
        cum += w
        if r < cum:
            return tgt
    return None


def classify(name):
    """Surface a name implies, across BOTH naming conventions in the library.

    A converted Shimeji keeps the artist's CamelCase (ClimbWall, HangFromCeiling); the hand-authored
    pets use the engine's own snake_case (climb_left, climb_ceiling_left). Matching only the first set
    silently scored every hand-authored pet as zero climbs, which is how this function got written the
    second time.
    """
    n = name.lower().replace("_", "")
    if "ceiling" in n or "hang" in n or "cling" in n:
        return "ceiling"
    if "wall" in n or n.startswith("climb") or n.startswith("grab"):
        return "wall"
    return "other"


def is_wall(name):
    return classify(name) == "wall"


def is_ceiling(name):
    return classify(name) == "ceiling"


def simulate(anims, width, height, minutes, rng):
    by_name = {}
    for a in anims.values():
        by_name.setdefault(a.name.lower(), a)
    start = by_name.get("stand") or anims[min(anims)]

    budget_ms = minutes * 60_000.0
    t = 0.0
    x = width / 2.0
    y = float(height)          # on the floor
    facing = 1                 # +1 = as authored (leftward for a converted skin), -1 = mirrored
    cur = start
    frame = 0
    passes = 0

    time_in = defaultdict(float)
    wall_hits = 0              # times a vertical screen border was reached
    climbs = 0                 # ... that resolved into a wall-climb animation
    ceiling_reaches = 0        # times a ceiling animation was entered
    fell_off_wall = 0
    visits = defaultdict(int)
    guard = 0

    while t < budget_ms and guard < 8_000_000:
        guard += 1
        a = cur
        span = max(1, a.frames * (a.repeat + 1))
        prog = 0.0 if span <= 1 else float(frame) / (span - 1)
        vx = (a.x0 + (a.x1 - a.x0) * prog) * facing
        vy = a.y0 + (a.y1 - a.y0) * prog
        dt = a.i0 + (a.i1 - a.i0) * prog

        t += dt
        time_in[classify(a.name)] += dt

        # A border is raised only when the MOVE would cross it, the way FormCompanion tests
        # `PositionY + y > bottomY - Height` before committing. A stationary animation sitting against
        # a wall therefore raises nothing -- without this, standing on the floor re-detects the floor
        # every frame and `fall` loops forever against its own 100%-weighted self-edge.
        nx, ny = x + vx, y + vy
        where = 0
        if vx and nx <= 0:
            nx, where = 0.0, VERTICAL
        elif vx and nx >= width:
            nx, where = float(width), VERTICAL
        if vy < 0 and ny <= 0:
            ny, where = 0.0, HORIZONTAL
        elif vy > 0 and ny >= height:
            ny, where = float(height), TASKBAR | HORIZONTAL
        x, y = nx, ny

        nxt = None
        if where:
            if where == VERTICAL:
                wall_hits += 1
            nxt = pick(a.border, where, rng)
            if nxt is not None:
                nn = anims[nxt].name.lower()
                if where == VERTICAL and is_wall(nn):
                    climbs += 1
                if where == HORIZONTAL and is_ceiling(nn):
                    ceiling_reaches += 1
        else:
            frame += 1
            if frame >= span:
                frame = 0
                # Gravity: off the floor, not already on a surface, and this animation has an opinion.
                airborne = y < height - 1 and classify(a.name) == "other"
                if airborne and a.gravity:
                    nxt = pick(a.gravity, NONE, rng)
                if nxt is None:
                    nxt = pick(a.seq, TASKBAR if y >= height - 1 else NONE, rng)

        if nxt is not None:
            if classify(a.name) == "wall" and anims[nxt].name.lower() == "fall":
                fell_off_wall += 1
            if anims[nxt].name.lower() == "turn":
                facing = -facing
            cur = anims[nxt]
            visits[cur.name] += 1
            frame = 0
        elif where:
            # No eligible edge: the engine returns -1 and the pet keeps its animation. Nudge it off the
            # border so this does not spin forever, which is what the real thing effectively does.
            x = min(max(x, 1.0), width - 1.0)
            y = max(y, 1.0)

    return dict(time_in=dict(time_in), wall_hits=wall_hits, climbs=climbs,
                ceiling=ceiling_reaches, fell=fell_off_wall, visits=dict(visits),
                elapsed=t)


def main():
    path = sys.argv[1]
    def arg(flag, dflt, cast=int):
        return cast(sys.argv[sys.argv.index(flag) + 1]) if flag in sys.argv else dflt
    minutes = arg("--minutes", 30)
    runs = arg("--runs", 400)
    width = arg("--width", 1920)
    height = arg("--height", 1032)

    anims = parse(path)
    rng = random.Random(20260909)

    tot = defaultdict(float)
    agg = defaultdict(float)
    visits = defaultdict(int)
    runs_with_wall = runs_with_ceiling = 0

    for _ in range(runs):
        r = simulate(anims, width, height, minutes, rng)
        for k, v in r["time_in"].items():
            tot[k] += v
        agg["wall_hits"] += r["wall_hits"]
        agg["climbs"] += r["climbs"]
        agg["ceiling"] += r["ceiling"]
        agg["fell"] += r["fell"]
        if r["climbs"]:
            runs_with_wall += 1
        if r["ceiling"]:
            runs_with_ceiling += 1
        for k, v in r["visits"].items():
            visits[k] += v

    grand = sum(tot.values()) or 1.0
    print("%s" % os.path.basename(os.path.dirname(path)))
    print("%d runs x %d simulated minutes, %dx%d work area\n" % (runs, minutes, width, height))

    print("Share of time by surface")
    for k in ("other", "wall", "ceiling"):
        print("   %-8s %6.2f%%" % (k, 100.0 * tot.get(k, 0.0) / grand))

    print("\nWall and ceiling events, per %d-minute run" % minutes)
    print("   reached a side wall      %8.1f" % (agg["wall_hits"] / runs))
    print("   ... and climbed it       %8.1f   (%.1f%% of wall arrivals)"
          % (agg["climbs"] / runs,
             100.0 * agg["climbs"] / max(1.0, agg["wall_hits"])))
    print("   reached the ceiling      %8.1f   (%.1f%% of climbs)"
          % (agg["ceiling"] / runs,
             100.0 * agg["ceiling"] / max(1.0, agg["climbs"])))
    print("   fell off mid-climb       %8.1f" % (agg["fell"] / runs))

    print("\nRuns that saw it at all")
    print("   climbed a wall           %6.1f%%  (%d of %d)"
          % (100.0 * runs_with_wall / runs, runs_with_wall, runs))
    print("   touched the ceiling      %6.1f%%  (%d of %d)"
          % (100.0 * runs_with_ceiling / runs, runs_with_ceiling, runs))

    print("\nMost-entered animations")
    for nm, c in sorted(visits.items(), key=lambda kv: -kv[1])[:12]:
        print("   %-18s %8.1f per run" % (nm, float(c) / runs))


if __name__ == "__main__":
    main()
