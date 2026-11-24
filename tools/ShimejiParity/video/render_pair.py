"""Render a paired parity run as one side-by-side video: the real Shimeji-EE runtime's trace on the left, this app's
engine on the right, one frame per logical tick (40 ms, so 25 frames a second plays at the runtimes' own speed).

    render_pair.py <scenario.json> <pack dir> <oracle trace.jsonl> <host trace.jsonl> <out.mp4>
                   [--panel-width 640] [--full] [--slow 1] [--ffmpeg ffmpeg] [--sheet out.png]
    render_pair.py --self-test

Each panel shows the same region of the scenario's screen, scaled to --panel-width (at most 4x): by default the region
everything either runtime draws on any tick, padded and widened to 16:9; --full shows the whole screen. Both panels
always share one region and one scale, so a position difference shows as one. A panel holds the work area's floor, the scenario's active window (moved and minimized
by THAT runtime's own window events) and every companion the trace shows on that tick, drawn from the pack's own image
at the trace's `origin`, mirrored when it faces right without an `imageRight`, as the source's image pair does. Nothing
is inferred: a companion that is not `visible`, or has no `pose`, is not drawn. A tick whose drawn picture differs
between the two traces (any companion's image, facing, visibility or origin) is marked with a red bar and counted; the
bar is a reading aid, the comparer (tools/ShimejiParity/comparer/compare.py) is the verdict.

The traces, the scenario and the pack are inputs only; the video is written to <out.mp4>, which must not exist. Videos of
private packs stay with the private evidence; this tool carries no art of its own.
"""
import copy
import json
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HEADER = 44
FOOTER = 14
GAP = 8
BG = (38, 42, 48)
FLOOR = (120, 130, 140)
WINDOW = (90, 110, 150)
DIFF = (220, 50, 50)
TEXT = (235, 235, 235)


def load_trace(path):
    """Header, {tick: {sid: last state line}}, {tick: [events]} from a shimeji-parity-trace/2 JSONL file."""
    header, states, events = None, {}, {}
    with open(path, encoding="utf-8") as f:
        for raw in f:
            raw = raw.strip()
            if not raw:
                continue
            line = json.loads(raw)
            kind = line.get("type")
            if kind == "header":
                header = line
            elif kind == "state":
                states.setdefault(line["tick"], {})[line["sid"]] = line
            elif kind == "event":
                events.setdefault(line["tick"], []).append(line)
    if header is None:
        raise ValueError(f"{path}: no header line")
    return header, states, events


def drawn(state):
    """What a state line puts on screen, or None when it draws nothing."""
    if not state or not state.get("visible") or not state.get("pose") or not state["pose"].get("image"):
        return None
    pose = state["pose"]
    return (pose["image"], pose.get("imageRight"), bool(state.get("lookRight")), tuple(state.get("origin") or ()))


def picture(states, tick):
    """The sorted drawn tuples of every companion on a tick: two runtimes show the same picture iff these are equal."""
    return tuple(sorted((sid, drawn(s)) for sid, s in states.get(tick, {}).items() if drawn(s) is not None))


def window_timeline(scenario, events, ticks):
    """Per tick, the active window as [left, top, right, bottom] (the scenario's and the trace's Win32 RECT form) or None:
    the scenario's own timeline, then THIS runtime's moves and minimizes applied from the tick they happen."""
    timeline = sorted(scenario.get("activeWindow") or [], key=lambda w: w["tick"])
    rects, current, minimized, at = [], None, False, 0
    for tick in range(ticks + 1):
        while at < len(timeline) and timeline[at]["tick"] <= tick:
            w = timeline[at]
            current = list(w["rect"]) if w.get("visible", True) and w.get("rect") else None
            minimized = False
            at += 1
        for e in events.get(tick, []):
            if e.get("kind") == "window-move" and isinstance(e.get("to"), list) and len(e["to"]) == 4:
                current = list(e["to"])
            elif e.get("kind") == "window-minimize":
                minimized = True
        rects.append(None if (current is None or minimized) else list(current))
    return rects


class Sprites:
    def __init__(self, pack, scale):
        self.pack, self.scale, self.cache = Path(pack), scale, {}

    def get(self, image, image_right, look_right):
        key = (image, image_right, look_right)
        if key not in self.cache:
            name = image_right if (look_right and image_right) else image
            img = Image.open(self.pack / name).convert("RGBA")
            if look_right and not image_right:
                img = img.transpose(Image.FLIP_LEFT_RIGHT)
            if self.scale != 1:
                img = img.resize((max(1, round(img.width * self.scale)), max(1, round(img.height * self.scale))),
                                 Image.NEAREST)
            self.cache[key] = img
        return self.cache[key]


def font(size):
    for name in ("arial.ttf", "DejaVuSans.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


def viewport(screen, state_sets, rect_sets, sizes, full=False, pad=48, aspect=16 / 9):
    """The screen region both panels show: everything either runtime draws on any tick (sprites and the active window),
    padded and widened to `aspect`, clamped to the screen; the whole screen when `full` or when nothing is drawn."""
    sw, sh = screen["w"], screen["h"]
    boxes = []
    for states in state_sets:
        for per_tick in states.values():
            for s in per_tick.values():
                what = drawn(s)
                if what is not None:
                    w, h = sizes(what[0], what[1], what[2])
                    boxes.append((what[3][0], what[3][1], what[3][0] + w, what[3][1] + h))
    for rects in rect_sets:
        boxes.extend(tuple(r) for r in rects if r)
    if full or not boxes:
        return 0, 0, sw, sh
    x0, y0 = min(b[0] for b in boxes) - pad, min(b[1] for b in boxes) - pad
    x1, y1 = max(b[2] for b in boxes) + pad, max(b[3] for b in boxes) + pad
    w, h = max(x1 - x0, 320), max(y1 - y0, 180)
    if w / h < aspect:
        w = h * aspect
    else:
        h = w / aspect
    w, h = min(w, sw), min(h, sh)
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    vx = min(max(cx - w / 2, 0), sw - w)
    vy = min(max(cy - h / 2, 0), sh - h)
    return round(vx), round(vy), round(w), round(h)


def render_panel(scenario, states, rects, tick, sprites, view, size):
    vx, vy, s = view
    panel = Image.new("RGB", size, BG)
    d = ImageDraw.Draw(panel)

    def at(x, y):
        return round((x - vx) * s), round((y - vy) * s)

    wa = scenario.get("workArea")
    if wa:
        d.line([at(wa["x"], wa["y"] + wa["h"]), at(wa["x"] + wa["w"], wa["y"] + wa["h"])], fill=FLOOR, width=2)
    rect = rects[tick] if tick < len(rects) else None
    if rect:
        d.rectangle([at(rect[0], rect[1]), at(rect[2], rect[3])], outline=WINDOW, width=2)
    for sid, st in sorted(states.get(tick, {}).items()):
        what = drawn(st)
        if what is None:
            continue
        image, image_right, look_right, origin = what
        img = sprites.get(image, image_right, look_right)
        panel.paste(img, at(origin[0], origin[1]), img)
    return panel


def render(scenario_path, pack, oracle_path, host_path, out, panel_width=640, slow=1, ffmpeg="ffmpeg", sheet=None,
           full=False):
    out = Path(out)
    if out.exists():
        raise SystemExit(f"refused: {out} already exists")
    scenario = json.loads(Path(scenario_path).read_text(encoding="utf-8"))
    oh, ostates, oevents = load_trace(oracle_path)
    hh, hstates, hevents = load_trace(host_path)
    ticks = int(scenario["ticks"])
    screen = (scenario.get("screens") or [{"w": 1280, "h": 720}])[0]
    orects, hrects = window_timeline(scenario, oevents, ticks), window_timeline(scenario, hevents, ticks)
    natural = Sprites(pack, 1)
    vx, vy, vw, vh = viewport(screen, (ostates, hstates), (orects, hrects),
                              lambda i, r, f: natural.get(i, r, f).size, full=full)
    scale = min(4.0, panel_width / vw)
    pw, ph = round(vw * scale), round(vh * scale)
    pw += pw % 2
    ph += ph % 2
    width, height = pw * 2 + GAP, ph + HEADER + FOOTER
    width += width % 2
    sprites = Sprites(pack, scale)
    view = (vx, vy, scale)
    big, small = font(16), font(12)
    differing = [t for t in range(ticks + 1) if picture(ostates, t) != picture(hstates, t) or orects[t] != hrects[t]]
    diff_set = set(differing)
    cmd = [ffmpeg, "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{width}x{height}",
           "-r", str(25 / max(1, slow)), "-i", "-", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", str(out)]
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    frames = []
    try:
        for tick in range(ticks + 1):
            frame = Image.new("RGB", (width, height), (20, 22, 26))
            d = ImageDraw.Draw(frame)
            d.text((8, 4), f"{scenario['id']}  tick {tick}/{ticks}", fill=TEXT, font=big)
            d.text((8, 24), "real Shimeji-EE 1.0.23", fill=TEXT, font=small)
            d.text((pw + GAP + 8, 24), "Desktop AI Companion", fill=TEXT, font=small)
            status = f"picture differs on {len(differing)} of {ticks + 1} ticks" if differing else "same picture on every tick"
            d.text((pw + GAP + 8, 4), status, fill=DIFF if differing else TEXT, font=small)
            frame.paste(render_panel(scenario, ostates, orects, tick, sprites, view, (pw, ph)), (0, HEADER))
            frame.paste(render_panel(scenario, hstates, hrects, tick, sprites, view, (pw, ph)), (pw + GAP, HEADER))
            if tick in diff_set:
                d.rectangle([0, HEADER + ph, width, height], fill=DIFF)
            proc.stdin.write(frame.tobytes())
            if sheet is not None and tick % max(1, ticks // 11) == 0 and len(frames) < 12:
                frames.append(frame.copy())
    finally:
        proc.stdin.close()
        code = proc.wait()
    if code != 0:
        raise SystemExit(f"ffmpeg exit {code}")
    if sheet is not None and frames:
        cols = 3
        rows = (len(frames) + cols - 1) // cols
        tw, th = width // 2, height // 2
        contact = Image.new("RGB", (tw * cols, th * rows), (0, 0, 0))
        for i, f in enumerate(frames):
            contact.paste(f.resize((tw, th)), ((i % cols) * tw, (i // cols) * th))
        contact.save(sheet)
    return {"ticks": ticks + 1, "differingTicks": differing}


def self_test():
    def st(tick, sid, image="img/A/a.png", right=False, origin=(10, 20), visible=True, image_right=None):
        return {"type": "state", "tick": tick, "sid": sid, "visible": visible, "lookRight": right, "origin": list(origin),
                "pose": {"image": image, "imageRight": image_right, "index": 0}}
    base = {0: {"m1": st(0, "m1")}, 1: {"m1": st(1, "m1"), "m2": st(1, "m2", origin=(50, 20))}}
    checks = []
    checks.append(("same states draw the same picture", picture(base, 1) == picture(copy.deepcopy(base), 1) != ()))
    for label, change in (("facing", {"lookRight": True}), ("origin", {"origin": [11, 20]}),
                          ("visibility", {"visible": False}), ("image", {"pose": {"image": "img/A/b.png",
                                                                                   "imageRight": None, "index": 0}})):
        other = {t: {sid: dict(s) for sid, s in v.items()} for t, v in base.items()}
        other[1]["m2"].update(change)
        checks.append((f"a {label} change makes the picture differ", picture(base, 1) != picture(other, 1)))
    hidden = {0: {"m1": st(0, "m1", visible=False)}}
    checks.append(("an invisible companion draws nothing", picture(hidden, 0) == ()))
    no_pose = {0: {"m1": dict(st(0, "m1"), pose=None)}}
    checks.append(("a pose-less companion draws nothing", picture(no_pose, 0) == ()))
    checks.append(("an index-only change is not a picture change",
                   picture(base, 0) == picture({0: {"m1": dict(st(0, "m1"), pose={"image": "img/A/a.png",
                                                                               "imageRight": None, "index": 3})}}, 0)))
    scen = {"activeWindow": [{"tick": 0, "visible": True, "rect": [100, 100, 300, 200]}]}
    rects = window_timeline(scen, {2: [{"kind": "window-move", "from": [100, 100, 300, 200], "to": [120, 100, 320, 200]}],
                                   4: [{"kind": "window-minimize"}]}, 5)
    checks.append(("window moves apply from their tick", rects[1] == [100, 100, 300, 200] and rects[2] == [120, 100, 320, 200]))
    checks.append(("a hidden scenario window is not drawn",
                   window_timeline({"activeWindow": [{"tick": 0, "visible": False}]}, {}, 1) == [None, None]))
    checks.append(("a minimized window is not drawn", rects[4] is None and rects[5] is None))
    screen = {"w": 1280, "h": 720}
    far = {0: {"m1": st(0, "m1", origin=(600, 600))}, 9: {"m1": st(9, "m1", origin=(700, 600))}}
    vx, vy, vw, vh = viewport(screen, (far, {}), ([], []), lambda i, r, f: (64, 64))
    checks.append(("the viewport holds every drawn sprite", vx <= 600 and vy <= 600 and vx + vw >= 764 and vy + vh >= 664))
    checks.append(("the viewport crops a small scene", vw < 1280 and vh < 720 and vx >= 0 and vy >= 0
                   and vx + vw <= 1280 and vy + vh <= 720))
    oracle_only = {0: {"m1": st(0, "m1", origin=(100, 100))}}
    vx2, vy2, vw2, vh2 = viewport(screen, (far, oracle_only), ([], []), lambda i, r, f: (64, 64))
    checks.append(("the viewport covers BOTH runtimes", vx2 <= 100 and vx2 + vw2 >= 764))
    checks.append(("--full shows the whole screen", viewport(screen, (far, {}), ([], []), lambda i, r, f: (64, 64),
                                                              full=True) == (0, 0, 1280, 720)))
    failed = [name for name, ok in checks if not ok]
    for name, ok in checks:
        print(("ok   " if ok else "FAIL ") + name)
    print("render_pair self-test: " + ("PASS" if not failed else f"FAIL ({len(failed)})"))
    return 0 if not failed else 1


def main(argv):
    if argv == ["--self-test"]:
        return self_test()
    opts = {"--panel-width": "640", "--slow": "1", "--ffmpeg": "ffmpeg", "--sheet": None}
    full = "--full" in argv
    argv = [a for a in argv if a != "--full"]
    pos = []
    i = 0
    while i < len(argv):
        if argv[i] in opts:
            opts[argv[i]] = argv[i + 1]
            i += 2
        else:
            pos.append(argv[i])
            i += 1
    if len(pos) != 5:
        print(__doc__)
        return 2
    result = render(*pos, panel_width=int(opts["--panel-width"]), slow=int(opts["--slow"]), ffmpeg=opts["--ffmpeg"],
                    sheet=opts["--sheet"], full=full)
    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
