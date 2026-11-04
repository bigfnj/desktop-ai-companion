"""Build the synthetic, IP-free parity packs and scenarios (CONTRACT.md section 1).

    make_packs.py <out dir>

Writes <out>/packs/<pack>/ (a Shimeji-EE layout: conf/actions.xml, conf/behaviors.xml, img/<Set>/*.png) and
<out>/<scenario id>.json for every scenario below. The sprites are generated, so nothing here is anyone's art: each is
an asymmetric body (a notch on its LEFT side, so a mirrored frame is visibly mirrored), a red anchor dot at the
authored ImageAnchor, and its frame number as white marks along the top, so a clip shows which pose is on screen.
Deterministic: the same inputs give byte-identical files, so a scenario's SHA-256 is stable.
"""
import json
import sys
from pathlib import Path

from PIL import Image, ImageDraw

NS = "http://www.group-finity.com/Mascot"
CELL = (64, 64)
ANCHOR = (32, 64)

SET_COLOURS = {"Alpha": (60, 120, 220), "Beta": (60, 170, 90), "Egg": (230, 220, 180), ".Ball": (230, 140, 40)}


def sprite(path, set_name, index):
    img = Image.new("RGBA", CELL, (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    colour = SET_COLOURS.get(set_name, (150, 150, 150))
    d.rectangle([14, 16, 50, 63], fill=colour + (255,))
    d.rectangle([14, 30, 22, 40], fill=(0, 0, 0, 0))          # the notch: left side only
    d.ellipse([ANCHOR[0] - 3, ANCHOR[1] - 6, ANCHOR[0] + 3, ANCHOR[1]], fill=(220, 30, 30, 255))
    for k in range(index):                                     # frame number as marks
        d.rectangle([4 + 6 * k, 2, 7 + 6 * k, 8], fill=(255, 255, 255, 255))
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path, format="PNG", optimize=False)


def pose(image, dx=0, dy=0, ticks=4):
    return f'<Pose Image="/{image}" ImageAnchor="{ANCHOR[0]},{ANCHOR[1]}" Velocity="{dx},{dy}" Duration="{ticks}"/>'


def action(name, typ, poses, cls=None, border="Floor", **attrs):
    a = [f'Name="{name}"', f'Type="{typ}"']
    if cls:
        a.append(f'Class="com.group_finity.mascot.action.{cls}"')
    if border:
        a.append(f'BorderType="{border}"')
    a += [f'{k}="{v}"' for k, v in attrs.items()]
    return f'    <Action {" ".join(a)}>\n      <Animation>' + "".join(poses) + "</Animation>\n    </Action>"


# The behaviours every pack needs because the runtime enters them by name on lifecycle events.
def lifecycle_actions(prefix):
    return [action("Fall", "Embedded", [pose(f"{prefix}4.png", ticks=250)], cls="Fall", border=None),
            action("Dragged", "Embedded", [pose(f"{prefix}5.png", ticks=250)], cls="Dragged", border=None),
            action("Thrown", "Embedded", [pose(f"{prefix}4.png", ticks=250)], cls="Fall", border=None)]


def lifecycle_behaviours():
    return ['    <Behavior Name="Fall" Frequency="0" Hidden="true"/>',
            '    <Behavior Name="Dragged" Frequency="0" Hidden="true"/>',
            '    <Behavior Name="Thrown" Frequency="0" Hidden="true"/>']


def write_conf(root, set_name, actions, behaviours):
    conf = root / "img" / set_name / "conf"
    conf.mkdir(parents=True, exist_ok=True)
    (conf / "actions.xml").write_text(
        f'<?xml version="1.0" encoding="UTF-8"?>\n<Mascot xmlns="{NS}">\n  <ActionList>\n' + "\n".join(actions)
        + "\n  </ActionList>\n</Mascot>\n", encoding="utf-8", newline="\n")
    (conf / "behaviors.xml").write_text(
        f'<?xml version="1.0" encoding="UTF-8"?>\n<Mascot xmlns="{NS}">\n  <BehaviorList>\n' + "\n".join(behaviours)
        + "\n  </BehaviorList>\n</Mascot>\n", encoding="utf-8", newline="\n")


def sprites(root, set_name, count, prefix="shime"):
    for i in range(1, count + 1):
        sprite(root / "img" / set_name / f"{prefix}{i}.png", set_name, i)


def pack_stand_walk(root):
    sprites(root, "Alpha", 5)
    write_conf(root, "Alpha",
               [action("Stand", "Stay", [pose("shime1.png", ticks=5), pose("shime2.png", ticks=5)], Duration=20),
                action("Walk", "Move", [pose("shime3.png", dx=-2, ticks=4), pose("shime1.png", dx=-2, ticks=4)], TargetX=600)]
               + lifecycle_actions("shime"),
               ['    <Behavior Name="Stand" Frequency="1">\n      <NextBehaviorList Add="false">'
                '<BehaviorReference Name="Walk" Frequency="1"/></NextBehaviorList>\n    </Behavior>',
                '    <Behavior Name="Walk" Frequency="0" Hidden="true">\n      <NextBehaviorList Add="false">'
                '<BehaviorReference Name="Stand" Frequency="1"/></NextBehaviorList>\n    </Behavior>']
               + lifecycle_behaviours())


def pack_transform(root):
    sprites(root, "Egg", 5)
    sprites(root, "Beta", 5)
    write_conf(root, "Egg",
               [action("Laid", "Embedded", [pose("shime1.png", ticks=6), pose("shime2.png", ticks=6), pose("shime3.png", ticks=6)],
                       cls="Transform", TransformMascot="Beta", TransformBehavior="Stand")] + lifecycle_actions("shime"),
               ['    <Behavior Name="Laid" Frequency="1"/>'] + lifecycle_behaviours())
    write_conf(root, "Beta",
               [action("Stand", "Stay", [pose("shime1.png", ticks=5), pose("shime2.png", ticks=5)], Duration=30)]
               + lifecycle_actions("shime"),
               ['    <Behavior Name="Stand" Frequency="1"/>'] + lifecycle_behaviours())


def pack_breed(root):
    sprites(root, "Alpha", 5)
    sprites(root, ".Ball", 5)
    write_conf(root, "Alpha",
               [action("Throw", "Embedded", [pose("shime1.png", ticks=5), pose("shime2.png", ticks=5)],
                       cls="Breed", BornX=40, BornY=0, BornMascot=".Ball", BornBehavior="Roll"),
                action("Stand", "Stay", [pose("shime1.png", ticks=5)], Duration=20)] + lifecycle_actions("shime"),
               ['    <Behavior Name="Throw" Frequency="1">\n      <NextBehaviorList Add="false">'
                '<BehaviorReference Name="Stand" Frequency="1"/></NextBehaviorList>\n    </Behavior>',
                '    <Behavior Name="Stand" Frequency="0" Hidden="true"/>'] + lifecycle_behaviours())
    write_conf(root, ".Ball",
               [action("Roll", "Stay", [pose("shime1.png", dx=-1, ticks=4), pose("shime2.png", dx=-1, ticks=4)], Duration=24)]
               + lifecycle_actions("shime"),
               ['    <Behavior Name="Roll" Frequency="1"/>'] + lifecycle_behaviours())


PACKS = {"slice-stand-walk": pack_stand_walk, "slice-transform": pack_transform, "slice-breed": pack_breed}

BASE = {
    "schema": "shimeji-parity-scenario/1",
    "screens": [{"x": 0, "y": 0, "w": 1280, "h": 720}],
    "workArea": {"x": 0, "y": 0, "w": 1280, "h": 680},
    "dpiScale": 1.0,
    "settings": {"breeding": True, "transients": True, "transformation": True, "throwing": False,
                 "sounds": False, "multiscreen": False},
    "activeWindow": [{"tick": 0, "visible": False}],
    "input": [{"tick": 0, "cursor": [100, 100]}],
}

SCENARIOS = [
    {"id": "S-slice-stand-walk-01", "description": "Alpha stands 20 ticks, walks left toward x 600, stands again",
     "pack": "packs/slice-stand-walk", "ticks": 160,
     "mascots": [{"sid": "m1", "imageSet": "Alpha", "behavior": "Stand", "anchor": [800, 680], "lookRight": False}],
     "rng": {"kind": "tape", "values": [0.5] * 64}},
    {"id": "S-slice-transform-01", "description": "an Egg plays its three poses and transforms in place into Beta, which stands",
     "pack": "packs/slice-transform", "ticks": 80,
     "mascots": [{"sid": "m1", "imageSet": "Egg", "behavior": "Laid", "anchor": [640, 680], "lookRight": False}],
     "rng": {"kind": "tape", "values": [0.5] * 64}},
    {"id": "S-slice-breed-01", "description": "Alpha breeds one .Ball 40 px in front of it, which rolls; Alpha then stands",
     "pack": "packs/slice-breed", "ticks": 80,
     "mascots": [{"sid": "m1", "imageSet": "Alpha", "behavior": "Throw", "anchor": [640, 680], "lookRight": False}],
     "rng": {"kind": "tape", "values": [0.5] * 64}},
]


def main(out):
    out = Path(out)
    for name, build in PACKS.items():
        build(out / "packs" / name)
    for s in SCENARIOS:
        doc = dict(BASE)
        doc.update(s)
        (out / f"{s['id']}.json").write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {len(PACKS)} pack(s) and {len(SCENARIOS)} scenario(s) to {out}")


if __name__ == "__main__":
    main(sys.argv[1])
