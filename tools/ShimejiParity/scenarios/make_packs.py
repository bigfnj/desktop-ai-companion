"""Build the synthetic, IP-free parity packs and scenarios (CONTRACT.md section 1).

    make_packs.py <out dir>

Writes <out>/packs/<pack>/ (a Shimeji-EE layout: img/<Set>/conf/actions.xml, img/<Set>/conf/behaviors.xml,
img/<Set>/*.png; the conf-tiers pack keeps its documents at the source's lower tiers, conf/<Set>/ and conf/) and
<out>/<scenario id>.json for every scenario below. The sprites are generated, so nothing here is anyone's art: each is
an asymmetric body (a notch on its LEFT side, so a mirrored frame is visibly mirrored), a red anchor dot at the
authored ImageAnchor, and its frame number as white marks along the top, so a clip shows which pose is on screen.
Deterministic: a manifest records the generated packs tree and S-*.json documents. A rerun replaces those owned
paths, so the same inputs give byte-identical generated files. Populated unmanaged output and extra paths in these
namespaces are refused before anything is removed. Other output-root files are left alone.

Beyond the three vertical-slice scenarios, every Shimeji feature and branch the parity suite covers has a DIRECTED
scenario (rng none or a chosen tape, a fixed tick count, a description a reader can check against the trace). Packs are
shared where one conf serves several scenarios; PACK_MAIN names the main image set the converter's paritypack verb
needs for a cross-member or hidden-member pack.
"""
import json
import os
import shutil
import sys
import tempfile
from functools import partial
from pathlib import Path
from xml.sax.saxutils import escape

from PIL import Image, ImageDraw

NS = "http://www.group-finity.com/Mascot"
CELL = (64, 64)
ANCHOR = (32, 64)

SET_COLOURS = {"Alpha": (60, 120, 220), "Beta": (60, 170, 90), "Egg": (230, 220, 180), ".Ball": (230, 140, 40),
               "Gamma": (170, 80, 200), ".Egg": (240, 225, 190)}


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
    write_documents(root / "img" / set_name / "conf", actions, behaviours)


def write_documents(conf, actions, behaviours):
    """One configuration folder's actions.xml and behaviors.xml, wherever the pack keeps them."""
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
    {"id": "S-slice-breed-01", "description": "Alpha breeds one .Ball 40 px behind it, which rolls; Alpha then stands",
     "pack": "packs/slice-breed", "ticks": 80,
     "mascots": [{"sid": "m1", "imageSet": "Alpha", "behavior": "Throw", "anchor": [640, 680], "lookRight": False}],
     "rng": {"kind": "tape", "values": [0.5] * 64}},
]


# ---------------------------------------------------------------------------------------------------------------------
# Directed feature scenarios: one or more per Shimeji feature and branch, ids S-<feature>-<branch>-NN. Every conf below
# is written fresh for this suite; each description says what a reader should see in the trace. Directed means rng
# kind none (any draw fails) or a tape whose values are chosen per draw, and a fixed tick count.
# ---------------------------------------------------------------------------------------------------------------------

QUOTE = {'"': "&quot;"}


def _attrs(pairs):
    return "".join(f' {k}="{escape(str(v), QUOTE)}"' for k, v in pairs.items())


def blank(dx=0, dy=0, ticks=4):
    """A pose with no image: the companion is hidden while it plays and its anchor still moves (CONTRACT section 3)."""
    return f'<Pose Velocity="{dx},{dy}" Duration="{ticks}"/>'


def anim(poses, hotspots=(), **attrs):
    return f"<Animation{_attrs(attrs)}>" + "".join(poses) + "".join(hotspots) + "</Animation>"


def hotspot(x, y, w, h, behaviour=None, shape="Rectangle"):
    a = {"Shape": shape, "Origin": f"{x},{y}", "Size": f"{w},{h}"}
    if behaviour:
        a["Behavior"] = behaviour
    return f"<Hotspot{_attrs(a)}/>"


def act(name, typ, body, cls=None, border="Floor", **attrs):
    """An Action whose children (Animation, ActionReference) are given as written."""
    a = {"Name": name, "Type": typ}
    if cls:
        a["Class"] = "com.group_finity.mascot.action." + cls
    if border:
        a["BorderType"] = border
    a.update(attrs)
    return f"    <Action{_attrs(a)}>\n" + "".join(f"      {b}\n" for b in body) + "    </Action>"


def posed(name, typ, poses, cls=None, border="Floor", **attrs):
    return act(name, typ, [anim(poses)], cls, border, **attrs)


def aref(name, **attrs):
    return f'<ActionReference Name="{name}"{_attrs(attrs)}/>'


def beh(name, frequency, hidden=False, nxt=None, add=None, **attrs):
    """A root Behavior; nxt is its NextBehaviorList as (name, frequency) pairs, add its Add attribute."""
    a = {"Name": name, "Frequency": frequency}
    if hidden:
        a["Hidden"] = "true"
    a.update(attrs)
    if nxt is None:
        return f"    <Behavior{_attrs(a)}/>"
    head = "<NextBehaviorList" + ("" if add is None else f' Add="{add}"') + ">"
    refs = "".join(f'<BehaviorReference Name="{n}" Frequency="{f}"/>' for n, f in nxt)
    return f"    <Behavior{_attrs(a)}>\n      {head}{refs}</NextBehaviorList>\n    </Behavior>"


def member(root, set_name, actions, behaviours, count=6, lifecycle=True):
    sprites(root, set_name, count)
    if lifecycle:
        actions = actions + lifecycle_actions("shime")
        behaviours = behaviours + lifecycle_behaviours()
    write_conf(root, set_name, actions, behaviours)


def then(target, *names):
    """Transition-only behaviours (Frequency 0, hidden) that hand on to target through an Add="false" next list."""
    return [beh(n, 0, hidden=True, nxt=[(target, 1)], add="false") for n in names]


def img(name, dx=0, dy=0, ticks=4):
    return pose(name, dx, dy, ticks)


WALK = [img("shime1.png", dx=-2, ticks=2), img("shime2.png", dx=-2, ticks=2)]
TURN = anim([img("shime3.png", ticks=4), img("shime6.png", ticks=4)], IsTurn="true")


# 1. Transform: one-frame and multi-frame, to another member and to self, TransformBehavior given and empty.
def pack_transform_forms(root):
    member(root, "Alpha",
           [posed("MorphOne", "Embedded", [img("shime1.png", ticks=1)], cls="Transform",
                  TransformMascot="Beta", TransformBehavior="Stand"),
            posed("MorphMany", "Embedded", [img("shime1.png"), img("shime2.png"), img("shime3.png")], cls="Transform",
                  TransformMascot="Beta", TransformBehavior="Stand"),
            posed("MorphSelf", "Embedded", [img("shime2.png", ticks=3), img("shime3.png", ticks=3)], cls="Transform",
                  TransformMascot="Alpha", TransformBehavior="Rest"),
            posed("MorphBlank", "Embedded", [img("shime1.png", ticks=3), img("shime2.png", ticks=3)], cls="Transform",
                  TransformMascot="Beta", TransformBehavior=""),
            posed("Rest", "Stay", [img("shime6.png", ticks=5)], Duration=40)],
           [beh(n, 0, hidden=True) for n in ("MorphOne", "MorphMany", "MorphSelf", "MorphBlank")] + [beh("Rest", 1)])
    member(root, "Beta", [posed("Stand", "Stay", [img("shime1.png", ticks=5), img("shime2.png", ticks=5)], Duration=30)],
           [beh("Stand", 1)])


# 2. Breed: BornCount, BornX/BornY with both facings, BornBehavior given and empty, BornMascot self and another member,
# BornTransient against the Breeding and Transients settings.
def pack_breed_forms(root):
    lay = [img("shime1.png", ticks=5), img("shime2.png", ticks=5)]
    member(root, "Alpha",
           [posed("LayOne", "Embedded", lay, cls="Breed", BornX=40, BornY=-30, BornBehavior="Hover"),
            posed("LaySelf", "Embedded", lay, cls="Breed", BornX=40, BornY=-30, BornBehavior="Hover", BornMascot="Alpha"),
            posed("LayThree", "Embedded", lay, cls="Breed", BornX=24, BornBehavior="Sit", BornMascot="Beta", BornCount=3),
            posed("LayBlank", "Embedded", lay, cls="Breed", BornX=24, BornBehavior="", BornMascot="Beta"),
            posed("LayTransient", "Embedded", lay, cls="Breed", BornX=24, BornBehavior="Sit", BornMascot="Beta",
                  BornTransient="true"),
            posed("LayKept", "Embedded", lay, cls="Breed", BornX=24, BornBehavior="Sit", BornMascot="Beta",
                  BornTransient="false"),
            posed("Hover", "Stay", [img("shime3.png", ticks=5)], border=None, Duration=60),
            posed("Wait", "Stay", [img("shime6.png", ticks=5)], Duration=60)],
           then("Wait", "LayOne", "LaySelf", "LayThree", "LayBlank", "LayTransient", "LayKept")
           + [beh("Hover", 0, hidden=True), beh("Wait", 1)])
    member(root, "Beta", [posed("Sit", "Stay", [img("shime2.png", ticks=5)], Duration=60)], [beh("Sit", 1)])


# 3. BreedMove, BreedJump, BreedFall: BornInterval 1 and above 1; BreedMove breeds nothing while it turns. One pack per
# class, so a converter that learns one class is not held back by the others.
BORN = {"BornMascot": ".Ball", "BornBehavior": "Rest"}


def breeding_pack(root, actions, names):
    member(root, "Alpha", actions + [posed("Stand", "Stay", [img("shime1.png", ticks=5)], Duration=100)],
           then("Stand", *names) + [beh("Stand", 1)])
    member(root, ".Ball", [posed("Rest", "Stay", [img("shime1.png", ticks=5)], border=None, Duration=200)],
           [beh("Rest", 1)])


def pack_breedmove(root):
    breeding_pack(root,
                  [posed("TrailEvery", "Embedded", WALK, cls="BreedMove", TargetX=620, BornInterval=1, **BORN),
                   posed("TrailSparse", "Embedded", WALK, cls="BreedMove", TargetX=600, BornInterval=4, **BORN),
                   act("TrailTurn", "Embedded", [anim(WALK), TURN], cls="BreedMove", TargetX=680, BornInterval=2, **BORN)],
                  ("TrailEvery", "TrailSparse", "TrailTurn"))


def pack_breedjump(root):
    hop = [img("shime3.png", ticks=4)]
    breeding_pack(root,
                  [posed("HopEvery", "Embedded", hop, cls="BreedJump", border=None, TargetX=700, TargetY=680,
                         VelocityParam=10, BornInterval=1, **BORN),
                   posed("HopSparse", "Embedded", hop, cls="BreedJump", border=None, TargetX=760, TargetY=680,
                         VelocityParam=10, BornInterval=3, **BORN)],
                  ("HopEvery", "HopSparse"))


def pack_breedfall(root):
    drop = [img("shime4.png", ticks=250)]
    breeding_pack(root,
                  [posed("DropEvery", "Embedded", drop, cls="BreedFall", border=None, BornInterval=1, **BORN),
                   posed("DropSparse", "Embedded", drop, cls="BreedFall", border=None, BornInterval=5, **BORN)],
                  ("DropEvery", "DropSparse"))


# 4. Births at the host's cap of 16 companions (policy:birth-cap).
def pack_breed_cap(root):
    lay = [img("shime1.png", ticks=3), img("shime2.png", ticks=3)]
    member(root, "Alpha",
           [posed("LayFifteen", "Embedded", lay, cls="Breed", BornCount=15, BornBehavior="Sit"),
            posed("LaySeventeen", "Embedded", lay, cls="Breed", BornCount=17, BornBehavior="Sit"),
            posed("Sit", "Stay", [img("shime6.png", ticks=5)], Duration=100)],
           then("Sit", "LayFifteen", "LaySeventeen") + [beh("Sit", 1)])


# 5. Egg arrival: the pack's main member (Gamma) starts as its egg (.Egg, hidden in the source's chooser), which
# hatches into it by Transform.
def pack_egg(root):
    member(root, ".Egg",
           [posed("Hatching", "Embedded", [img("shime1.png", ticks=8), img("shime2.png", ticks=8), img("shime3.png", ticks=8)],
                  cls="Transform", TransformMascot="Gamma", TransformBehavior="Arrive")],
           [beh("Hatching", 1)])
    member(root, "Gamma",
           [posed("Arrive", "Stay", [img("shime6.png", ticks=4)], Duration=16),
            posed("Stand", "Stay", [img("shime1.png", ticks=5), img("shime2.png", ticks=5)], Duration=40)],
           then("Stand", "Arrive") + [beh("Stand", 1)])


# 6. Scan and Interact. In the scan packs Alpha seeks and Beta offers; one pack per class.
IDLE = posed("Idle", "Stay", [img("shime1.png", ticks=5)], Duration=200)


def offerers(root):
    member(root, "Beta",
           [posed("Offer", "Stay", [img("shime1.png", ticks=5)], Duration=400, Affordance="Hug"),
            posed("OfferBriefly", "Stay", [img("shime1.png", ticks=4)], Duration=12, Affordance="Hug"),
            posed("WaveOffer", "Stay", [img("shime2.png", ticks=5)], Duration=400, Affordance="Wave"),
            posed("WaveBriefly", "Stay", [img("shime2.png", ticks=4)], Duration=8, Affordance="Wave"),
            posed("QuietThenWave", "Stay", [img("shime3.png", ticks=5)], Duration=10),
            posed("Quiet", "Stay", [img("shime3.png", ticks=5)], Duration=400),
            posed("Hugged", "Stay", [img("shime6.png", ticks=5)], Duration=40),
            posed("WavedAt", "Stay", [img("shime6.png", ticks=5)], Duration=40)],
           then("Quiet", "OfferBriefly", "WaveBriefly") + then("WaveOffer", "QuietThenWave")
           + [beh(n, 0, hidden=True) for n in ("Offer", "WaveOffer", "Hugged", "WavedAt")] + [beh("Quiet", 1)])


def pack_scan_move(root):
    member(root, "Alpha",
           [posed("Look", "Stay", [img("shime1.png", ticks=3)], Duration=3),
            posed("Seek", "Embedded", [img("shime2.png", dx=-4, ticks=2), img("shime3.png", dx=-4, ticks=2)], cls="ScanMove",
                  Affordance="Hug", Behavior="Hugging", TargetBehavior="Hugged", TargetLook="true"),
            posed("Hugging", "Stay", [img("shime6.png", ticks=5)], Duration=40), IDLE],
           then("Seek", "Look") + then("Idle", "Seek") + [beh("Hugging", 0, hidden=True), beh("Idle", 1)])
    offerers(root)


def pack_scan_interact(root):
    member(root, "Alpha",
           [posed("LookAround", "Stay", [img("shime1.png", ticks=3)], Duration=3),
            posed("Watch", "Embedded", [img("shime2.png", ticks=10), img("shime3.png", ticks=10)], cls="ScanInteract",
                  Affordance="Wave", Behavior="Waving", TargetBehavior="WavedAt"),
            posed("Waving", "Stay", [img("shime6.png", ticks=5)], Duration=40), IDLE],
           then("Watch", "LookAround") + then("Idle", "Watch") + [beh("Waving", 0, hidden=True), beh("Idle", 1)])
    offerers(root)


def pack_interact(root):
    # A start companion initialises before it joins the manager (CONTRACT section 4), and Interact's first check reads
    # the manager, so the Interact scenarios start in Gather, a one-tick Stay whose next list hands to Huddle once the
    # companions are managed.
    member(root, "Alpha",
           [posed("Huddle", "Embedded", [img("shime1.png", ticks=15), img("shime2.png", ticks=15)], cls="Interact",
                  Behavior="Huddled"),
            posed("Huddled", "Stay", [img("shime6.png", ticks=5)], Duration=40),
            posed("Approach", "Move", [img("shime2.png", dx=-2, ticks=2), img("shime3.png", dx=-2, ticks=2)], TargetX=640),
            IDLE, posed("Gather", "Stay", [img("shime1.png", ticks=1)], Duration=1)],
           then("Idle", "Huddle", "Approach") + then("Huddle", "Gather") + [beh("Huddled", 0, hidden=True), beh("Idle", 1)])


# 7. Move: either axis ends it; a turn variant (Move with IsTurn, and the MoveWithTurn class); both facings.
def pack_move(root):
    climb = [img("shime1.png", dx=-2, dy=-1, ticks=2), img("shime2.png", dx=-2, dy=-1, ticks=2)]
    member(root, "Alpha",
           [posed("Slide", "Move", climb, border=None, TargetX=600, TargetY=500),
            posed("Climb", "Move", climb, border=None, TargetX=560, TargetY=590),
            act("Turnabout", "Move", [anim([img("shime1.png", dx=-2, ticks=3), img("shime2.png", dx=-2, ticks=3)]), TURN],
                TargetX=700),
            posed("Hang", "Stay", [img("shime3.png", ticks=5)], border=None, Duration=200),
            posed("Stand", "Stay", [img("shime6.png", ticks=5)], Duration=200)],
           then("Hang", "Slide", "Climb") + then("Stand", "Turnabout") + [beh("Hang", 0, hidden=True), beh("Stand", 1)])


def pack_move_withturn(root):
    member(root, "Alpha",
           [act("Pivot", "Embedded", [anim([img("shime1.png", dx=-2, ticks=3), img("shime2.png", dx=-2, ticks=3)]),
                                      anim([img("shime3.png", ticks=4), img("shime6.png", ticks=4)])],
                cls="MoveWithTurn", TargetX=700),
            posed("Stand", "Stay", [img("shime6.png", ticks=5)], Duration=200)],
           then("Stand", "Pivot") + [beh("Stand", 1)])


# 8. Jump: Arc true and false, and a target re-aimed every tick.
def pack_jump(root):
    hop = [img("shime3.png", ticks=4)]
    member(root, "Alpha",
           [posed("HopArc", "Embedded", hop, cls="Jump", border=None, TargetX=840, TargetY=680, VelocityParam=12, Arc="true"),
            posed("HopFlat", "Embedded", hop, cls="Jump", border=None, TargetX=840, TargetY=680, VelocityParam=12, Arc="false"),
            posed("HopChase", "Embedded", hop, cls="Jump", border=None, TargetX="#{mascot.environment.cursor.x}",
                  TargetY=680, VelocityParam=10),
            posed("Land", "Stay", [img("shime6.png", ticks=5)], Duration=200)],
           then("Land", "HopArc", "HopFlat", "HopChase") + [beh("Land", 1)])


# 9. Fall: InitialVX/VY, resistance and gravity; a floor met inside one step's sweep; a wall.
def pack_fall(root):
    drop = [img("shime4.png", ticks=250)]
    member(root, "Alpha",
           [posed("Drop", "Embedded", drop, cls="Fall", border=None, InitialVX=12, InitialVY=-16, RegistanceX="0.1",
                  RegistanceY="0.2", Gravity=3),
            posed("Plunge", "Embedded", drop, cls="Fall", border=None, InitialVY=40),
            posed("Fling", "Embedded", drop, cls="Fall", border=None, InitialVX=-30, InitialVY=-4),
            posed("Stand", "Stay", [img("shime1.png", ticks=5)], Duration=200),
            posed("Cling", "Stay", [img("shime6.png", ticks=5)], border="Wall", Duration=200)],
           then("Stand", "Drop", "Plunge") + then("Cling", "Fling") + [beh("Stand", 1), beh("Cling", 0, hidden=True)])


# 10. An image-less pose hides the companion while it keeps moving.
def pack_imageless(root):
    member(root, "Alpha",
           [posed("Vanish", "Animate", [img("shime1.png", dx=-2, ticks=4), blank(dx=-2, ticks=6), img("shime2.png", dx=-2, ticks=4)]),
            posed("Stand", "Stay", [img("shime6.png", ticks=5)], Duration=200)],
           then("Stand", "Vanish") + [beh("Stand", 1)])


# 11. Behaviour selection.
def stays(*names):
    return [posed(n, "Stay", [img(f"shime{i + 1}.png", ticks=2)], Duration=4) for i, n in enumerate(names)]


def pack_select_weighted(root):
    member(root, "Alpha", stays("A", "B"), [beh("A", 1), beh("B", 3)])


def pack_select_next(root):
    member(root, "Alpha", stays("Lead", "Other", "Solo", "Tail"),
           [beh("Lead", 1, nxt=[("Tail", 2)], add="true"), beh("Other", 1),
            beh("Solo", 0, nxt=[("Tail", 1)], add="false"), beh("Tail", 0)])


def pack_select_hidden(root):
    member(root, "Alpha", stays("Lead", "Secret"), [beh("Lead", 1), beh("Secret", 5, hidden=True)])


def pack_select_missing(root):
    member(root, "Alpha", stays("Lead", "Ghost", "Stand"),
           [beh("Lead", 0, nxt=[("Ghost", 1)], add="false"), beh("Stand", 1)])


def pack_select_fallback(root):
    member(root, "Alpha", stays("Dead", "Tail") + [posed("Stand", "Stay", [img("shime6.png", ticks=5)], Duration=200)],
           [beh("Dead", 0, nxt=[("Tail", 0)], add="false"), beh("Tail", 0), beh("Stand", 1)])


# 12. Variables and caching.
def pack_vars_cache(root):
    member(root, "Alpha",
           [posed("FollowOnce", "Move", WALK, TargetX="${mascot.environment.cursor.x}"),
            posed("FollowEach", "Move", WALK, TargetX="#{mascot.environment.cursor.x}"),
            posed("Stand", "Stay", [img("shime6.png", ticks=5)], Duration=200)],
           then("Stand", "FollowOnce", "FollowEach") + [beh("Stand", 1)])


def pack_vars_transform(root):
    member(root, "Alpha",
           [posed("Mark", "Embedded", [img("shime1.png"), img("shime2.png")], cls="Transform",
                  Condition="${mascot.variables.put('kept', 7) == null}", TransformMascot="Beta", TransformBehavior="Check")],
           [beh("Mark", 1)])
    member(root, "Beta",
           [posed("Check", "Stay", [img("shime3.png", ticks=5)], Condition="#{mascot.variables.get('kept') == 7}", Duration=30),
            posed("Stand", "Stay", [img("shime1.png", ticks=5)], Duration=200)],
           then("Stand", "Check") + [beh("Stand", 1)])


# 13. One action referenced twice with different parameters.
def pack_refs(root):
    member(root, "Alpha",
           [posed("Pause", "Stay", [img("shime6.png", ticks=5)], Duration=50),
            posed("Walk", "Move", WALK, TargetX=10),
            act("Patrol", "Sequence", [aref("Pause", Duration=5), aref("Walk", TargetX=600), aref("Pause", Duration=15),
                                       aref("Walk", TargetX=700)], border=None),
            posed("Stand", "Stay", [img("shime1.png", ticks=5)], Duration=200)],
           then("Stand", "Patrol") + [beh("Stand", 1)])


# 14. Window actions, one pack per class.
def window_pack(root, action):
    name = action.split('"', 2)[1]
    member(root, "Alpha", [action, posed("Stand", "Stay", [img("shime1.png", ticks=5)], Duration=200)],
           then("Stand", name) + [beh("Stand", 1)])


def pack_window_carry(root):
    window_pack(root, posed("Carry", "Embedded", WALK, cls="WalkWithIE", TargetX=500, IeOffsetX=0, IeOffsetY=0))


def pack_window_throw(root):
    window_pack(root, posed("Toss", "Embedded", [img("shime3.png", ticks=10)], cls="ThrowIE"))


def pack_window_minimize(root):
    window_pack(root, posed("Shrink", "Embedded", [img("shime6.png", ticks=6)], cls="MinimizeIE"))


# 15. Input: a drag, Dragged's regist, a hotspot. Dragged hangs the anchor 56 px below the cursor (64 px sprites).
def input_lifecycle(next_after_drag):
    return ([posed("Fall", "Embedded", [img("shime4.png", ticks=250)], cls="Fall", border=None),
             posed("Dragged", "Embedded", [img("shime5.png", ticks=250)], cls="Dragged", border=None, OffsetY=56),
             posed("Thrown", "Embedded", [img("shime4.png", ticks=250)], cls="Fall", border=None)],
            [beh("Fall", 0, hidden=True), beh("Dragged", 0, hidden=True, nxt=[(next_after_drag, 1)], add="false"),
             beh("Thrown", 0, hidden=True)])


def pack_input(root):
    life_actions, life_behaviours = input_lifecycle("Resist")
    sprites(root, "Alpha", 6)
    write_conf(root, "Alpha",
               [posed("Stand", "Stay", [img("shime1.png", ticks=5)], Duration=400),
                posed("Resist", "Stay", [img("shime6.png", ticks=5)], border=None, Duration=30)] + life_actions,
               [beh("Stand", 1), beh("Resist", 0, hidden=True)] + life_behaviours)


def pack_input_regist(root):
    life_actions, life_behaviours = input_lifecycle("Resist")
    sprites(root, "Alpha", 6)
    write_conf(root, "Alpha",
               [posed("Stand", "Stay", [img("shime1.png", ticks=5)], Duration=400),
                posed("Resist", "Embedded", [img("shime6.png", ticks=5), img("shime3.png", ticks=5)], cls="Regist", border=None)]
               + life_actions,
               [beh("Stand", 1), beh("Resist", 0, hidden=True)] + life_behaviours)


def pack_input_hotspot(root):
    member(root, "Alpha",
           [act("Stand", "Stay", [anim([img("shime1.png", ticks=5)], hotspots=[hotspot(0, 0, 64, 32, "Petted")])],
                Duration=400),
            posed("Petted", "Stay", [img("shime6.png", ticks=5)], Duration=20)],
           [beh("Stand", 1)] + then("Stand", "Petted"))


# 16. The source's lower configuration tiers. Main.loadConfiguration takes conf/, then conf/<set>/, then
# img/<set>/conf/, a later tier's file replacing an earlier one's; every other pack keeps its documents in
# img/<set>/conf/. Here Alpha's are in conf/Alpha/ (the set tier, over conf/), and Beta, with none of its own, takes the
# pack's shared conf/.
def pack_conf_tiers(root):
    sprites(root, "Alpha", 5)
    sprites(root, "Beta", 5)
    write_documents(root / "conf" / "Alpha",
                    [posed("Stand", "Stay", [img("shime1.png", ticks=5), img("shime2.png", ticks=5)], Duration=20),
                     posed("Walk", "Move", [img("shime3.png", dx=-2, ticks=4), img("shime1.png", dx=-2, ticks=4)], TargetX=600)]
                    + lifecycle_actions("shime"),
                    [beh("Stand", 1, nxt=[("Walk", 1)], add="false"), beh("Walk", 0, hidden=True, nxt=[("Stand", 1)], add="false")]
                    + lifecycle_behaviours())
    write_documents(root / "conf",
                    [posed("Rest", "Stay", [img("shime1.png", ticks=5), img("shime2.png", ticks=5)], Duration=30)]
                    + lifecycle_actions("shime"),
                    [beh("Rest", 1)] + lifecycle_behaviours())


PACKS.update({
    "transform-forms": pack_transform_forms, "breed-forms": pack_breed_forms, "breedmove": pack_breedmove,
    "breedjump": pack_breedjump, "breedfall": pack_breedfall, "breed-cap": pack_breed_cap, "egg-hatch": pack_egg,
    "scan-move": pack_scan_move, "scan-interact": pack_scan_interact, "interact": pack_interact, "move": pack_move,
    "move-withturn": pack_move_withturn, "jump": pack_jump, "fall": pack_fall, "imageless": pack_imageless,
    "select-weighted": pack_select_weighted, "select-next": pack_select_next, "select-hidden": pack_select_hidden,
    "select-missing": pack_select_missing, "select-fallback": pack_select_fallback, "vars-cache": pack_vars_cache,
    "vars-transform": pack_vars_transform, "refs": pack_refs, "window-carry": pack_window_carry,
    "window-throw": pack_window_throw, "window-minimize": pack_window_minimize, "input": pack_input,
    "input-regist": pack_input_regist, "input-hotspot": pack_input_hotspot, "conf-tiers": pack_conf_tiers,
})

# The image set the converter's paritypack verb is told is the pack's main member. The converter needs one when a
# member names another (TransformMascot, BornMascot) or is hidden in the source's chooser (a leading dot); the source
# itself has no such notion, so the oracle never reads this.
PACK_MAIN = {
    "slice-transform": "Beta", "slice-breed": "Alpha", "transform-forms": "Alpha", "breed-forms": "Alpha",
    "breedmove": "Alpha", "breedjump": "Alpha", "breedfall": "Alpha", "egg-hatch": "Gamma", "vars-transform": "Alpha",
}


def cfg(**changes):
    settings = dict(BASE["settings"])
    settings.update(changes)
    return settings


def tape(*head, length=32):
    """A tape whose first values are chosen per draw; the rest (0.5) is headroom, never a planned draw."""
    return {"kind": "tape", "values": list(head) + [0.5] * (length - len(head))}


NONE = {"kind": "none"}


def mascot(sid, image_set, behaviour, x, y=680, right=False):
    return {"sid": sid, "imageSet": image_set, "behavior": behaviour, "anchor": [x, y], "lookRight": right}


def scenario(sid, description, pack, ticks, mascots, rng, **extra):
    s = {"id": sid, "description": description, "pack": "packs/" + pack, "ticks": ticks, "mascots": mascots, "rng": rng}
    s.update(extra)
    return s


WINDOW = [{"tick": 0, "visible": True, "rect": [400, 380, 640, 680], "title": "Scratch"}]
THROWN_WINDOW = [{"tick": 0, "visible": True, "rect": [300, 200, 600, 500], "title": "Scratch"}]

SCENARIOS += [
    # 1. Transform
    scenario("S-transform-oneframe-01",
             "Alpha's one-pose Transform (duration 1) becomes Beta at action age 0 on tick 1, entering Stand (cause forced); "
             "tick 1 still shows Alpha's shime1, Beta's first pose follows on tick 2",
             "transform-forms", 20, [mascot("m1", "Alpha", "MorphOne", 640)], NONE),
    scenario("S-transform-multiframe-01",
             "Alpha's three-pose Transform (duration 12) becomes Beta at action age 11, on tick 12, entering Stand",
             "transform-forms", 30, [mascot("m1", "Alpha", "MorphMany", 640)], NONE),
    scenario("S-transform-self-01",
             "Alpha's Transform names its own member: at action age 5 (tick 6) a transform event Alpha to Alpha, then "
             "Alpha's Rest; the sid, anchor and facing are unchanged",
             "transform-forms", 30, [mascot("m1", "Alpha", "MorphSelf", 640, right=True)], NONE),
    scenario("S-transform-blankbehavior-01",
             "Alpha's Transform to Beta has an empty TransformBehavior: at action age 5 (tick 6) the image set becomes "
             "Beta and no behaviour named '' can be built, which the source reports as an error and survives in its old "
             "behaviour",
             "transform-forms", 30, [mascot("m1", "Alpha", "MorphBlank", 640)], tape(0.5)),
    # 2. Breed
    scenario("S-breed-facing-01",
             "two Alphas breed one Alpha each at action age 9 (tick 10), 40 px BEHIND and 30 px above: m1 facing left "
             "at x 400 makes m1.b1 at [440, 650] facing left, m2 facing right at x 880 makes m2.b1 at [840, 650] facing "
             "right; the children enter Hover (no border, so they stay there) and activate on tick 11",
             "breed-forms", 30, [mascot("m1", "Alpha", "LayOne", 400), mascot("m2", "Alpha", "LayOne", 880, right=True)],
             tape(0.5, 0.5)),
    scenario("S-breed-count-01",
             "Alpha breeds BornCount 3 Betas at [664, 680] on tick 10 (m1.b1 to m1.b3, attempts 1 to 3), each entering "
             "Sit, all three activated on tick 11; Alpha then waits",
             "breed-forms", 30, [mascot("m1", "Alpha", "LayThree", 640)], tape(0.5)),
    scenario("S-breed-blankbehavior-01",
             "Alpha's Breed names Beta with an empty BornBehavior: on tick 10 the child cannot enter a behaviour named '', "
             "so the birth fails (the source reports it and disposes the child, which never joins the manager)",
             "breed-forms", 30, [mascot("m1", "Alpha", "LayBlank", 640)], tape(0.5)),
    scenario("S-breed-self-01",
             "Alpha's Breed names its own member: one Alpha child m1.b1 at [680, 650] (facing left, 40 px behind) "
             "enters Hover on tick 10",
             "breed-forms", 30, [mascot("m1", "Alpha", "LaySelf", 640)], tape(0.5)),
    scenario("S-breed-transient-01",
             "with Transients off and Breeding on: m1's BornTransient=true Breed makes nothing and writes no birth event; "
             "m2's BornTransient=false Breed makes one Beta on tick 10",
             "breed-forms", 30, [mascot("m1", "Alpha", "LayTransient", 400), mascot("m2", "Alpha", "LayKept", 880)],
             tape(0.5, 0.5), settings=cfg(transients=False)),
    scenario("S-breed-transient-02",
             "with Breeding off and Transients on: m1's BornTransient=true Breed makes one Beta on tick 10; m2's "
             "BornTransient=false Breed makes nothing and writes no birth event",
             "breed-forms", 30, [mascot("m1", "Alpha", "LayTransient", 400), mascot("m2", "Alpha", "LayKept", 880)],
             tape(0.5, 0.5), settings=cfg(breeding=False)),
    # 3. BreedMove / BreedJump / BreedFall
    scenario("S-breedmove-interval-01",
             "BreedMove with BornInterval 1: Alpha walks left from x 640 to 620 at 2 px a tick and a .Ball is born at "
             "its anchor after the move on every tick of the walk",
             "breedmove", 20, [mascot("m1", "Alpha", "TrailEvery", 640)], tape(0.5)),
    scenario("S-breedmove-interval-02",
             "BreedMove with BornInterval 4: Alpha walks left from x 640 to 600 and a .Ball is born on the ticks whose "
             "action age is a multiple of 4 (five births)",
             "breedmove", 30, [mascot("m1", "Alpha", "TrailSparse", 640)], tape(0.5)),
    scenario("S-breedmove-turn-01",
             "BreedMove with BornInterval 2 toward a target behind it: Alpha (facing left) turns right for its 8-tick "
             "turn animation with no births, then walks right to x 680 breeding on even action ages",
             "breedmove", 40, [mascot("m1", "Alpha", "TrailTurn", 640)], tape(0.5)),
    scenario("S-breedjump-interval-01",
             "BreedJump with BornInterval 1: Alpha jumps from x 640 to 700 and a .Ball is born at its anchor on every "
             "tick of the jump",
             "breedjump", 20, [mascot("m1", "Alpha", "HopEvery", 640)], tape(0.5)),
    scenario("S-breedjump-interval-02",
             "BreedJump with BornInterval 3: Alpha jumps from x 640 to 760 and a .Ball is born every third tick",
             "breedjump", 30, [mascot("m1", "Alpha", "HopSparse", 640)], tape(0.5)),
    scenario("S-breedfall-interval-01",
             "BreedFall with BornInterval 1: Alpha falls from y 600 to the floor at 680 and a .Ball is born at its "
             "anchor on every tick of the fall",
             "breedfall", 20, [mascot("m1", "Alpha", "DropEvery", 640, y=600)], tape(0.5)),
    scenario("S-breedfall-interval-02",
             "BreedFall with BornInterval 5: Alpha falls from y 480 to the floor and a .Ball is born every fifth tick",
             "breedfall", 40, [mascot("m1", "Alpha", "DropSparse", 640, y=480)], tape(0.5)),
    # 4. Births at the cap
    scenario("S-cap-below-01",
             "Alpha breeds BornCount 15 on tick 6: all 15 births succeed (m1.b1 to m1.b15), bringing the total to "
             "exactly 16, and none is refused",
             "breed-cap", 20, [mascot("m1", "Alpha", "LayFifteen", 640)], tape(0.5)),
    scenario("S-cap-at-01",
             "Alpha breeds BornCount 17 on tick 6: births 1 to 15 succeed and attempts 16 and 17 are refused with a "
             "birth-refused event, reason cap (policy:birth-cap; the source has no cap and makes all 17)",
             "breed-cap", 20, [mascot("m1", "Alpha", "LaySeventeen", 640)], tape(0.5)),
    # 5. Egg arrival
    scenario("S-egg-hatch-01",
             "the pack's main member Gamma arrives as its egg: .Egg plays three 8-tick poses and at action age 23 "
             "(tick 24) transforms in place into Gamma, entering Arrive; tick 24 still shows the egg's shime3",
             "egg-hatch", 50, [mascot("m1", ".Egg", "Hatching", 640)], tape(0.5)),
    # 6. Scan and Interact
    scenario("S-scan-first-01",
             "ScanMove takes the FIRST offering companion in manager order, not the nearest: after a 3-tick look, m1 "
             "targets m2 (x 840, farther) over m3 (x 560), walks right 4 px a tick, and on exact arrival enters Hugging "
             "while m2 enters Hugged and turns to face m1",
             "scan-move", 80,
             [mascot("m1", "Alpha", "Look", 640), mascot("m2", "Beta", "Offer", 840), mascot("m3", "Beta", "Offer", 560)],
             tape(0.5, 0.5)),
    scenario("S-scan-loss-01",
             "ScanMove ends when its target stops offering: m1 targets m2, which offers Hug for 12 ticks then goes "
             "Quiet; m1's walk ends there, short of m2, and it selects Idle",
             "scan-move", 50, [mascot("m1", "Alpha", "Look", 640), mascot("m2", "Beta", "OfferBriefly", 840)],
             tape(0.5, 0.5, 0.5)),
    scenario("S-scan-reacquire-01",
             "ScanInteract reacquires: m1 targets m2, which offers Wave for 8 ticks; with no offer the scan writes a null "
             "target each tick until m3 starts offering, then targets m3; at its 20-tick animation's end m1 enters "
             "Waving and m3 WavedAt",
             "scan-interact", 50,
             [mascot("m1", "Alpha", "LookAround", 640), mascot("m2", "Beta", "WaveBriefly", 900),
              mascot("m3", "Beta", "QuietThenWave", 300)],
             tape(0.5, 0.5, 0.5, 0.5)),
    scenario("S-interact-arrive-01",
             "Interact while a third companion arrives: m1 and m2 start in Gather on x 640 (a companion cannot start inside "
             "Interact, which reads the manager it joins only after initialising) and enter Huddle by its next list once "
             "managed, each interact event naming the other; m3 walks in from x 700 and, from its arrival, is named too; "
             "on the last tick of Huddle's 30-tick animation m1 and m2 enter Huddled",
             "interact", 50,
             [mascot("m1", "Alpha", "Gather", 640), mascot("m2", "Alpha", "Gather", 640), mascot("m3", "Alpha", "Approach", 700)],
             tape(0.5, 0.5, 0.5)),
    scenario("S-interact-shared-01",
             "Interact while three share an anchor: m1 and m2 start in Gather on x 640 beside m3 (Idle, also on 640) and "
             "enter Huddle by its next list once managed; each interact event names the other two in manager order, never "
             "m4 at x 900; on the last tick of Huddle's 30-tick animation m1 and m2 enter Huddled",
             "interact", 40,
             [mascot("m1", "Alpha", "Gather", 640), mascot("m2", "Alpha", "Gather", 640), mascot("m3", "Alpha", "Idle", 640),
              mascot("m4", "Alpha", "Idle", 900)],
             tape(0.5, 0.5)),
    # 7. Move
    scenario("S-move-axis-01",
             "Move ends when the X axis reaches its target first: from [640, 600] moving 2 left and 1 up a tick, x reaches "
             "600 on tick 20 (y 580, short of 500) and the move ends there",
             "move", 30, [mascot("m1", "Alpha", "Slide", 640, y=600)], tape(0.5)),
    scenario("S-move-axis-02",
             "Move ends when the Y axis reaches its target first: from [640, 600] moving 2 left and 1 up a tick, y reaches "
             "590 on tick 10 (x 620, short of 560) and the move ends there",
             "move", 30, [mascot("m1", "Alpha", "Climb", 640, y=600)], tape(0.5)),
    scenario("S-move-turn-01",
             "Move with an IsTurn variant, both facings: m1 (facing left, x 640) and m2 (facing right, x 760) both target "
             "x 700, so each first plays the 8-tick turn variant in place, then walks 2 px a tick to x 700",
             "move", 50, [mascot("m1", "Alpha", "Turnabout", 640), mascot("m2", "Alpha", "Turnabout", 760, right=True)],
             tape(0.5, 0.5)),
    scenario("S-move-withturn-01",
             "MoveWithTurn, both facings: as S-move-turn-01, but the turn is the class's LAST animation (no IsTurn)",
             "move-withturn", 50, [mascot("m1", "Alpha", "Pivot", 640), mascot("m2", "Alpha", "Pivot", 760, right=True)],
             tape(0.5, 0.5)),
    # 8. Jump
    scenario("S-jump-arc-01",
             "Jump with Arc true from [640, 680] to [840, 680] at VelocityParam 12: aimed abs(dx)/2 above the target, it "
             "rises then descends and snaps onto the target, then lands",
             "jump", 40, [mascot("m1", "Alpha", "HopArc", 640)], tape(0.5)),
    scenario("S-jump-flat-01",
             "Jump with Arc false from [640, 680] to [840, 680]: a straight line along the floor, then it lands",
             "jump", 40, [mascot("m1", "Alpha", "HopFlat", 640)], tape(0.5)),
    scenario("S-jump-reaim-01",
             "Jump re-aims every tick: TargetX is the cursor's x per frame; the cursor jumps from x 900 to 450 on tick 6 "
             "and the jump turns round toward it",
             "jump", 60, [mascot("m1", "Alpha", "HopChase", 640)], tape(0.5),
             input=[{"tick": 0, "cursor": [900, 100]}, {"tick": 6, "cursor": [450, 100]}]),
    # 9. Fall
    scenario("S-fall-params-01",
             "Fall with InitialVX 12, InitialVY -16, RegistanceX 0.1, RegistanceY 0.2, Gravity 3 from [200, 300]: it faces "
             "right, rises, drifts right and lands on the floor at y 680, then stands",
             "fall", 70, [mascot("m1", "Alpha", "Drop", 200, y=300)], tape(0.5)),
    scenario("S-fall-sweep-01",
             "Fall with InitialVY 40 from [640, 500]: each tick moves about 38 px, and the floor at 680 is met inside one "
             "step's sweep, so it lands exactly on 680 rather than past it",
             "fall", 20, [mascot("m1", "Alpha", "Plunge", 640, y=500)], tape(0.5)),
    scenario("S-fall-wall-01",
             "Fall with InitialVX -30 from [200, 300]: it flies left into the work area's left edge (x 0) before reaching "
             "the floor, stops on the wall, and clings",
             "fall", 30, [mascot("m1", "Alpha", "Fling", 200, y=300)], tape(0.5)),
    # 10. Image-less pose
    scenario("S-imageless-01",
             "an Animate whose middle pose has no image: ticks 5 to 10 show visible false and pose image null, the anchor "
             "still moves 2 px left each tick and origin stays where the window last was",
             "imageless", 20, [mascot("m1", "Alpha", "Vanish", 640)], tape(0.5)),
    # 11. Behaviour selection
    scenario("S-select-weighted-01",
             "a weighted root list (A 1, B 3): the tape's 0.1, 0.6, 0.9, 0.2 pick A, B, B, A in turn (cause select)",
             "select-weighted", 20, [mascot("m1", "Alpha", "A", 640)], tape(0.1, 0.6, 0.9, 0.2, length=8)),
    scenario("S-rng-tape-short-01",
             "trace/2 tape-exhaustion closure: named startup A draws nothing; the two Duration=4 Stay behaviours "
             "make four builtin:behavior-select draws at ticks 5, 9, 13 and 17. The three tape values 0.1, 0.6, 0.9 "
             "pick A, B, B; draw 4 uses 0.5 and picks B. Both runs complete tick 20 with tapeExhausted true",
             "select-weighted", 20, [mascot("m1", "Alpha", "A", 640)], tape(0.1, 0.6, 0.9, length=3)),
    scenario("S-select-next-add-01",
             "Lead's NextBehaviorList Add=true appends Tail (2) to the root list (Lead 1, Other 1, Solo 0, Tail 0): 0.9 picks "
             "Tail from the next list (cause next); Tail, Frequency 0 at the root and reachable only so, has no list and "
             "the root's 0.3 picks Lead (cause select)",
             "select-next", 16, [mascot("m1", "Alpha", "Lead", 640)], tape(0.9, 0.3, 0.1, length=8)),
    scenario("S-select-next-replace-01",
             "Solo's NextBehaviorList Add=false replaces the root list with its own (Tail 1): even 0.99 picks Tail "
             "(cause next); Tail then returns to the root list, where 0.3 picks Lead",
             "select-next", 16, [mascot("m1", "Alpha", "Solo", 640)], tape(0.99, 0.3, 0.1, length=8)),
    scenario("S-select-hidden-01",
             "a Hidden behaviour with positive frequency is still selected: root Lead 1, Secret 5 (hidden); 0.5 picks "
             "Secret, then 0.1 picks Lead",
             "select-hidden", 16, [mascot("m1", "Alpha", "Lead", 640)], tape(0.5, 0.1, 0.5, length=8)),
    scenario("S-select-missing-01",
             "Lead's next list names Ghost, which has an action but no root behaviour: Ghost runs (cause next), and when "
             "it ends there is no root definition named Ghost to continue from",
             "select-missing", 16, [mascot("m1", "Alpha", "Lead", 640)], tape(0.5, 0.5, length=8)),
    scenario("S-select-fallback-01",
             "Dead's next list (Add=false) holds only Tail at Frequency 0: the candidates weigh 0, so the companion drops "
             "in at x 320 (draw 0.25, builtin:no-candidate-drop-x), 256 px above the work area, enters Fall (cause "
             "fallback) and falls to the floor, where the root list picks Stand",
             "select-fallback", 70, [mascot("m1", "Alpha", "Dead", 640)], tape(0.25, 0.5, length=8)),
    # 12. Variables and caching
    scenario("S-vars-cache-01",
             "${} is evaluated once per action entry, #{} every frame: both walk toward the cursor's x (400); on tick 20 "
             "the cursor moves to x 1000, m1 (${}) keeps walking left, m2 (#{}) turns and walks right",
             "vars-cache", 60, [mascot("m1", "Alpha", "FollowOnce", 640), mascot("m2", "Alpha", "FollowEach", 700)],
             tape(0.5, 0.5), input=[{"tick": 0, "cursor": [400, 100]}, {"tick": 20, "cursor": [1000, 100]}]),
    scenario("S-vars-transform-01",
             "a persistent variable survives Transform: Alpha's Mark stores kept=7 when its ${} Condition is first read "
             "(tick 0, vars shows it), transforms to Beta at age 7, and Beta's Check, whose #{} Condition needs kept==7, "
             "stays its full 30 ticks with vars still {kept: 7}",
             "vars-transform", 50, [mascot("m1", "Alpha", "Mark", 640)], tape(0.5, 0.5)),
    # 13. Reference overrides
    scenario("S-refs-override-01",
             "one Sequence references Pause twice (Duration 5, then 15) and Walk twice (TargetX 600, then 700) over "
             "declarations of Duration 50 and TargetX 10: pause 5, walk left to 600, pause 15, walk right to 700",
             "refs", 110, [mascot("m1", "Alpha", "Patrol", 640)], tape(0.5)),
    # 14. Window actions
    scenario("S-window-carry-on-01",
             "window actions ON: Alpha holds the window [400, 380, 640, 680] by its lower right corner and WalkWithIE "
             "walks left to x 500, the window moving with it each tick (window-move events)",
             "window-carry", 90, [mascot("m1", "Alpha", "Carry", 640)], tape(0.5), activeWindow=WINDOW,
             settings=cfg(throwing=True, windowActions=True)),
    scenario("S-window-carry-off-01",
             "window actions OFF (Throwing on): WalkWithIE plays as a Move to x 500 and the window never moves "
             "(policy:window-off-plays)",
             "window-carry", 90, [mascot("m1", "Alpha", "Carry", 640)], tape(0.5), activeWindow=WINDOW,
             settings=cfg(throwing=True, windowActions=False)),
    scenario("S-window-throwing-off-01",
             "the source's own Throwing setting off (window actions on): WalkWithIE ends at once and the window never moves",
             "window-carry", 20, [mascot("m1", "Alpha", "Carry", 640)], tape(0.5, 0.5), activeWindow=WINDOW,
             settings=cfg(throwing=False, windowActions=True)),
    scenario("S-window-throw-on-01",
             "window actions ON: ThrowIE (InitialVX 32, InitialVY -10, Gravity 0.5) moves the window [300, 200, 600, 500] "
             "each of its 10 ticks, by 32 px against Alpha's facing and -10 + int(age * 0.5) vertically",
             "window-throw", 20, [mascot("m1", "Alpha", "Toss", 640)], tape(0.5), activeWindow=THROWN_WINDOW,
             settings=cfg(throwing=True, windowActions=True)),
    scenario("S-window-throw-off-01",
             "window actions OFF: ThrowIE plays its 10-tick animation and the window never moves (policy:window-off-plays)",
             "window-throw", 20, [mascot("m1", "Alpha", "Toss", 640)], tape(0.5), activeWindow=THROWN_WINDOW,
             settings=cfg(throwing=True, windowActions=False)),
    scenario("S-window-minimize-on-01",
             "window actions ON: MinimizeIE plays 6 ticks and minimises the window at its end (window-minimize), with "
             "Alpha nowhere near it",
             "window-minimize", 20, [mascot("m1", "Alpha", "Shrink", 900)], tape(0.5), activeWindow=THROWN_WINDOW,
             settings=cfg(throwing=True, windowActions=True)),
    scenario("S-window-minimize-off-01",
             "window actions OFF: MinimizeIE plays its 6 ticks and the window stays (policy:window-off-plays)",
             "window-minimize", 20, [mascot("m1", "Alpha", "Shrink", 900)], tape(0.5), activeWindow=THROWN_WINDOW,
             settings=cfg(throwing=True, windowActions=False)),
    # 15. Input
    scenario("S-input-drag-01",
             "a drag: a press on m1 at tick 5 enters Dragged (forced); the cursor moves right and up to [700, 500], the "
             "anchor following 56 px below it; the release at tick 20 enters Thrown, which falls to the floor",
             "input", 60, [mascot("m1", "Alpha", "Stand", 640)], tape(0.5),
             input=[{"tick": 0, "cursor": [640, 624]}, {"tick": 5, "sid": "m1", "press": "left"},
                    {"tick": 8, "cursor": [660, 600]}, {"tick": 11, "cursor": [680, 560]},
                    {"tick": 14, "cursor": [700, 500]}, {"tick": 20, "sid": "m1", "release": "left"}]),
    scenario("S-input-regist-01",
             "Dragged's regist: m1 is pressed at tick 2 and held still; at action age 249 the regist draw 0.5 (0.1 or "
             "more) adds a tick, at age 250 the draw 0.05 does not, so Dragged ends at age 251 and its next list "
             "selects Resist",
             "input", 270, [mascot("m1", "Alpha", "Stand", 640)], tape(0.5, 0.05, 0.5, length=8),
             input=[{"tick": 0, "cursor": [640, 624]}, {"tick": 2, "sid": "m1", "press": "left"}]),
    scenario("S-input-regist-02",
             "Dragged's regist into the Regist class: as S-input-regist-01, then Resist (Regist) holds while the cursor "
             "stays within 5 px and, on its animation's last tick, faces at random (draw 0.7) and falls",
             "input-regist", 290, [mascot("m1", "Alpha", "Stand", 640)], tape(0.5, 0.05, 0.5, 0.7, length=8),
             input=[{"tick": 0, "cursor": [640, 624]}, {"tick": 2, "sid": "m1", "press": "left"}]),
    scenario("S-input-hotspot-01",
             "a hotspot click: a click on m1's upper half (window-local [32, 14], inside the hotspot) at tick 5 enters "
             "Petted (forced) instead of a drag; Petted stands 20 ticks and hands back to Stand",
             "input-hotspot", 40, [mascot("m1", "Alpha", "Stand", 640)], tape(0.5),
             input=[{"tick": 0, "cursor": [640, 630]}, {"tick": 5, "sid": "m1", "click": "left"}]),
    # 16. Configuration tiers
    scenario("S-conf-tiers-01",
             "documents at the source's lower tiers: Alpha, configured from conf/Alpha/, stands 20 ticks, walks left from "
             "x 660 to 600 and stands again; Beta, configured from the pack's conf/, rests in 30-tick turns; every refKey "
             "names its own set's chosen file (conf/Alpha/... for m1, conf/... for m2)",
             "conf-tiers", 80, [mascot("m1", "Alpha", "Stand", 660), mascot("m2", "Beta", "Rest", 300)], tape(0.5)),
]


# ---------------------------------------------------------------------------------------------------------------------
# 17. Converter lowerings (lane feature/shimeji-parity-lowerings): one directed scenario per construct the converter
# newly lowers, each run on the oracle and on the host. Every pack is written fresh here; the Japanese names and values
# are the source schema's element, attribute and value names (the converter's verified fact tables), not anyone's conf.
# ---------------------------------------------------------------------------------------------------------------------

def write_text(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def mascot_document(body):
    return f'<?xml version="1.0" encoding="UTF-8"?>\n<Mascot xmlns="{NS}">\n' + body + "\n</Mascot>\n"


def stand_walk_documents(conf, actions_name="actions.xml", behaviours_name="behaviors.xml", poses=None):
    """The stand-walk slice's two documents, under the given file names, optionally with other Stand poses."""
    stand = poses or [pose("shime1.png", ticks=5), pose("shime2.png", ticks=5)]
    actions = ([action("Stand", "Stay", stand, Duration=20),
                action("Walk", "Move", [pose("shime3.png", dx=-2, ticks=4), pose("shime1.png", dx=-2, ticks=4)], TargetX=600)]
               + lifecycle_actions("shime"))
    behaviours = ([beh("Stand", 1, nxt=[("Walk", 1)], add="false"), beh("Walk", 0, hidden=True, nxt=[("Stand", 1)], add="false")]
                  + lifecycle_behaviours())
    write_text(conf / actions_name, mascot_document("  <ActionList>\n" + "\n".join(actions) + "\n  </ActionList>"))
    write_text(conf / behaviours_name, mascot_document("  <BehaviorList>\n" + "\n".join(behaviours) + "\n  </BehaviorList>"))


# 17.1 Japanese vocabulary: element, attribute and value names in the source's Japanese schema, and the engine's own
# behaviours under their Japanese names, which the source enters through the configuration's schema.
def ja_pose(image, dx=0, ticks=4):
    return f'<ポーズ 画像="/{image}" 基準座標="{ANCHOR[0]},{ANCHOR[1]}" 移動速度="{dx},0" 長さ="{ticks}"/>'


def ja_action(name, typ, poses, cls=None, border="地面", **attrs):
    a = {"名前": name, "種類": typ}
    if cls:
        a["クラス"] = "com.group_finity.mascot.action." + cls
    if border:
        a["枠"] = border
    a.update(attrs)
    return f"    <動作{_attrs(a)}>\n      <アニメーション>" + "".join(poses) + "</アニメーション>\n    </動作>"


def ja_behaviour(name, frequency, action_name=None, nxt=None):
    a = {"名前": name, "頻度": frequency}
    if action_name:
        a["動作"] = action_name
    if nxt is None:
        return f"    <行動{_attrs(a)}/>"
    refs = "".join(f'<行動参照 名前="{n}" 頻度="{f}"/>' for n, f in nxt)
    return f'    <行動{_attrs(a)}>\n      <次の行動リスト 追加="false">{refs}</次の行動リスト>\n    </行動>'


def pack_lower_japanese(root):
    sprites(root, "Alpha", 5)
    conf = root / "img" / "Alpha" / "conf"
    actions = [ja_action("Stand", "静止", [ja_pose("shime1.png", ticks=5), ja_pose("shime2.png", ticks=5)], 長さ=20),
               ja_action("Walk", "移動", [ja_pose("shime3.png", dx=-2), ja_pose("shime1.png", dx=-2)], 目的地X=600),
               ja_action("Drop", "組み込み", [ja_pose("shime4.png", ticks=250)], cls="Fall", border=None),
               ja_action("Hold", "組み込み", [ja_pose("shime5.png", ticks=250)], cls="Dragged", border=None),
               ja_action("Toss", "組み込み", [ja_pose("shime4.png", ticks=250)], cls="Fall", border=None)]
    behaviours = [ja_behaviour("Stand", 1, nxt=[("Walk", 1)]), ja_behaviour("Walk", 0, nxt=[("Stand", 1)]),
                  ja_behaviour("落下する", 0, "Drop"), ja_behaviour("ドラッグされる", 0, "Hold"), ja_behaviour("投げられる", 0, "Toss")]
    write_text(conf / "actions.xml", mascot_document("  <動作リスト>\n" + "\n".join(actions) + "\n  </動作リスト>"))
    write_text(conf / "behaviors.xml", mascot_document("  <行動リスト>\n" + "\n".join(behaviours) + "\n  </行動リスト>"))


# 17.2 An information document holding only <Information>: the source loads it as one more configuration document and
# nothing at run time reads it.
def pack_lower_info(root):
    sprites(root, "Alpha", 5)
    conf = root / "img" / "Alpha" / "conf"
    stand_walk_documents(conf)
    write_text(conf / "info.xml", mascot_document(
        '  <Information>\n    <Name>Synthetic Alpha</Name>\n    <Artist Name="nobody" URL="https://example.invalid/"/>\n'
        "  </Information>"))


# 17.3 Anonymous nested actions: a Sequence holding a nameless Stay, a reference, an empty-named Move and a nameless
# Select around a nameless Stay. Each is built where it stands (never looked up by name).
def nested(typ, body, **attrs):
    a = {"Type": typ}
    a.update(attrs)
    return f"<Action{_attrs(a)}>" + "".join(body) + "</Action>"


def pack_lower_nested(root):
    member(root, "Alpha",
           [posed("Walk", "Move", WALK, TargetX=10),
            act("Patrol", "Sequence",
                [nested("Stay", [anim([img("shime6.png", ticks=3)])], BorderType="Floor", Duration=6),
                 aref("Walk", TargetX=600),
                 nested("Move", [anim([img("shime1.png", dx=-2, ticks=2), img("shime2.png", dx=-2, ticks=2)])], Name="",
                        BorderType="Floor", TargetX=640),
                 nested("Select", [nested("Stay", [anim([img("shime3.png", ticks=2)])], BorderType="Floor", Duration=4)])],
                border=None),
            posed("Stand", "Stay", [img("shime1.png", ticks=5)], Duration=200)],
           then("Stand", "Patrol") + [beh("Stand", 1)])


# 17.4 Custom variables: an attribute the schema does not name is a variable of the source's action map. A root
# behaviour's Step read by its action, an action's Speed read by its own Duration, a reference's Gap read by its own
# TargetX. The companion starts in the root Stride (a next-list entry is built from its own element, which would not
# carry Step), and every behaviour is named as its action, since an entry's action defaults to its own name.
def pack_lower_vars(root):
    member(root, "Alpha",
           [posed("Walk", "Move", WALK, TargetX=10),
            posed("Pace", "Stay", [img("shime6.png", ticks=3)], Duration="${Speed*4}", Speed=3),
            act("Chase", "Sequence", [aref("Walk", TargetX="${mascot.anchor.x - Gap}", Gap=40)], border=None),
            posed("Stride", "Move", [img("shime2.png", dx=-2, ticks=2), img("shime3.png", dx=-2, ticks=2)],
                  TargetX="${mascot.anchor.x + Step}"),
            posed("Stand", "Stay", [img("shime1.png", ticks=5)], Duration=200)],
           [beh("Stride", 1, nxt=[("Pace", 1)], add="false", Step=30),
            beh("Pace", 0, hidden=True, nxt=[("Chase", 1)], add="false"),
            beh("Chase", 0, hidden=True, nxt=[("Stand", 1)], add="false"),
            beh("Stand", 0, hidden=True)])


# 17.5 Instant embedded actions written with no Animation, as the stock configuration writes them: Look, Offset and
# BringToFront inside a Sequence (Mute is left out: the oracle refuses it until mute events have a seam).
def bare(name, cls, **attrs):
    a = {"Name": name, "Type": "Embedded", "Class": "com.group_finity.mascot.action." + cls}
    a.update(attrs)
    return f"    <Action{_attrs(a)}/>"


def pack_lower_instant(root):
    member(root, "Alpha",
           [posed("Wait", "Stay", [img("shime6.png", ticks=3)], Duration=6),
            bare("Face", "Look", LookRight="true"), bare("Nudge", "Offset", X=12, Y=0), bare("Raise", "BringToFront"),
            posed("Walk", "Move", WALK, TargetX=720),
            act("Hop", "Sequence", [aref("Wait"), aref("Face"), aref("Nudge"), aref("Raise"), aref("Walk")], border=None),
            posed("Stand", "Stay", [img("shime1.png", ticks=5)], Duration=200)],
           then("Stand", "Hop") + [beh("Stand", 1)])


# 17.6 Math.random named without a call (NaN in the source), in the stock template's shape: a reference's TargetX whose
# facing-left branch is NaN, which Move reads as intValue() 0.
NAN_TARGET = ("${mascot.lookRight ? mascot.environment.workArea.left+Math.random()*100 : "
              "mascot.environment.workArea.right-Math.random*100}")


def pack_lower_nan(root):
    member(root, "Alpha",
           [posed("Walk", "Move", WALK, TargetX=10),
            act("Roam", "Sequence", [aref("Walk", TargetX=NAN_TARGET)], border=None),
            posed("Stand", "Stay", [img("shime1.png", ticks=5)], Duration=200)],
           then("Stand", "Roam") + [beh("Stand", 1)])


# 17.7 Pose Sound and Volume, lowered to WAV clips: one clip at the pack's sound/, one under img/<set>/sound/.
def wave_bytes(period, frames, amplitude=6000):
    """A deterministic 8 kHz mono 16-bit PCM square wave (generated, nobody's recording)."""
    import io
    import struct
    import wave
    data = b"".join(struct.pack("<h", amplitude if (i // period) % 2 == 0 else -amplitude) for i in range(frames))
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(8000)
        w.writeframes(data)
    return buffer.getvalue()


def pack_lower_sound(root):
    sprites(root, "Alpha", 5)
    (root / "sound").mkdir(parents=True, exist_ok=True)
    (root / "sound" / "tone.wav").write_bytes(wave_bytes(10, 800))
    (root / "img" / "Alpha" / "sound").mkdir(parents=True, exist_ok=True)
    (root / "img" / "Alpha" / "sound" / "beep.wav").write_bytes(wave_bytes(4, 400))
    sounding = [pose("shime1.png", ticks=5).replace("/>", ' Sound="/tone.wav" Volume="-6.5"/>'),
                pose("shime2.png", ticks=5).replace("/>", ' Sound="beep.wav"/>')]
    stand_walk_documents(root / "img" / "Alpha" / "conf", poses=sounding)


# 17.8 Configuration files spelt as the pack spells them (Actions.xml, Behaviors.xml), which the source's lower-case
# probe also opens on Windows.
def pack_lower_spelling(root):
    sprites(root, "Alpha", 5)
    stand_walk_documents(root / "img" / "Alpha" / "conf", "Actions.xml", "Behaviors.xml")


def japanese_alpha(root, extra_actions=(), extra_behaviours=(), start=None):
    """17.1's Japanese member Alpha, with optional extra actions and behaviours (start: a behaviour listed first)."""
    sprites(root, "Alpha", 5)
    conf = root / "img" / "Alpha" / "conf"
    actions = list(extra_actions) + [
        ja_action("Stand", "静止", [ja_pose("shime1.png", ticks=5), ja_pose("shime2.png", ticks=5)], 長さ=20),
        ja_action("Walk", "移動", [ja_pose("shime3.png", dx=-2), ja_pose("shime1.png", dx=-2)], 目的地X=600),
        ja_action("Drop", "組み込み", [ja_pose("shime4.png", ticks=250)], cls="Fall", border=None),
        ja_action("Hold", "組み込み", [ja_pose("shime5.png", ticks=250)], cls="Dragged", border=None),
        ja_action("Toss", "組み込み", [ja_pose("shime4.png", ticks=250)], cls="Fall", border=None)]
    behaviours = list(extra_behaviours) + [
        ja_behaviour("Stand", 1, nxt=[("Walk", 1)]), ja_behaviour("Walk", 0, nxt=[("Stand", 1)]),
        ja_behaviour("落下する", 0, "Drop"), ja_behaviour("ドラッグされる", 0, "Hold"), ja_behaviour("投げられる", 0, "Toss")]
    write_text(conf / "actions.xml", mascot_document("  <動作リスト>\n" + "\n".join(actions) + "\n  </動作リスト>"))
    write_text(conf / "behaviors.xml", mascot_document("  <行動リスト>\n" + "\n".join(behaviours) + "\n  </行動リスト>"))


# 17.9 CONTRACT v1.1 source names (decision 67): an English member whose own Fall keeps its name, beside the Japanese
# member whose engine Fall the converter renames. Both fall from the air; only the Japanese one's name is mapped back.
def pack_lower_japanese_mixed(root):
    japanese_alpha(root)
    member(root, "Beta", [posed("Stand", "Stay", [img("shime1.png", ticks=5), img("shime2.png", ticks=5)], Duration=30)],
           [beh("Stand", 1)])


# 17.10 A requested behaviour name the converter renamed: Alpha's Transform asks for its own engine Fall by the
# Japanese name, so the transform event and the behaviour it enters both carry the source's name.
def pack_lower_japanese_transform(root):
    morph = ('    <動作 名前="Morph" 種類="組み込み" クラス="com.group_finity.mascot.action.Transform" '
             'TransformMascot="Alpha" TransformBehavior="落下する">\n      <アニメーション>'
             + ja_pose("shime2.png", ticks=3) + ja_pose("shime3.png", ticks=3) + "</アニメーション>\n    </動作>")
    japanese_alpha(root, [morph], [ja_behaviour("Morph", 0)])


PACKS.update({
    "lower-japanese-mixed": pack_lower_japanese_mixed, "lower-japanese-transform": pack_lower_japanese_transform,
})

PACKS.update({
    "lower-japanese": pack_lower_japanese, "lower-info": pack_lower_info, "lower-nested": pack_lower_nested,
    "lower-vars": pack_lower_vars, "lower-instant": pack_lower_instant, "lower-nan": pack_lower_nan,
    "lower-sound": pack_lower_sound, "lower-spelling": pack_lower_spelling,
})

SCENARIOS += [
    scenario("S-lower-japanese-01",
             "a Japanese-schema member: m1 stands (静止, 地面) 20 ticks, walks (移動) left from x 800 to 600 and stands "
             "again; m2 starts in its Stand 80 px above the floor, loses the ground and enters the engine's 落下する "
             "(an embedded Fall), lands on the floor and returns to Stand",
             "lower-japanese", 120, [mascot("m1", "Alpha", "Stand", 800), mascot("m2", "Alpha", "Stand", 300, y=600)],
             tape(0.5, 0.5, 0.5, 0.5, 0.5, 0.5)),
    scenario("S-lower-info-01",
             "the stand-walk slice with an information document beside its configuration: nothing at run time reads it, "
             "so Alpha stands 20 ticks, walks left from x 800 to 600 and stands again, as in S-slice-stand-walk-01",
             "lower-info", 160, [mascot("m1", "Alpha", "Stand", 800)], tape(0.5, 0.5, 0.5, 0.5)),
    scenario("S-lower-nested-01",
             "anonymous nested actions: Patrol plays a nameless 6-tick Stay, walks left to x 600 by reference, walks right "
             "to x 640 in an empty-named Move, then a nameless Select's nameless 4-tick Stay, and hands back to Stand",
             "lower-nested", 80, [mascot("m1", "Alpha", "Patrol", 640)], tape(0.5, 0.5)),
    scenario("S-lower-vars-01",
             "custom variables: the root Stride behaviour's Step (30) sets its action's target 30 px right, so Alpha walks "
             "from x 640 to 670; Pace's Duration is its own Speed (3) times 4, 12 ticks; Chase's reference walks to its "
             "anchor x minus the reference's Gap (40), from 670 to 630; then Stand",
             "lower-vars", 80, [mascot("m1", "Alpha", "Stride", 640)], tape(0.5, 0.5, 0.5, 0.5)),
    scenario("S-lower-instant-01",
             "instant embedded actions without an Animation: Hop waits 6 ticks, Look turns Alpha right, Offset moves it 12 "
             "px right (x 640 to 652), BringToFront changes nothing traced, and the walk goes right to x 720",
             "lower-instant", 60, [mascot("m1", "Alpha", "Hop", 640)], tape(0.5, 0.5)),
    scenario("S-lower-nan-left-01",
             "Math.random named without a call, facing left: the reference's TargetX takes the NaN branch, which Move reads "
             "as 0 and which draws nothing, so Alpha walks left from x 60 to 0, then stands (the facing-right branch draws "
             "from a script, which v1 cannot attribute)",
             "lower-nan", 50, [mascot("m1", "Alpha", "Roam", 60)], tape(0.5, 0.5)),
    scenario("S-lower-sound-01",
             "pose Sound and Volume as WAV clips, sounds off: the stand-walk slice whose Stand poses name a clip (one with a "
             "Volume), so the timing and motion are S-slice-stand-walk-01's; v1 traces carry no audio",
             "lower-sound", 160, [mascot("m1", "Alpha", "Stand", 800)], tape(0.5, 0.5, 0.5, 0.5)),
    scenario("S-lower-spelling-01",
             "the stand-walk slice with its documents spelt Actions.xml and Behaviors.xml: every refKey names the file as "
             "the pack spells it",
             "lower-spelling", 160, [mascot("m1", "Alpha", "Stand", 800)], tape(0.5, 0.5, 0.5, 0.5)),
    scenario("S-lower-japanese-02",
             "source names beside an unchanged one: m1 (the Japanese Alpha) and m2 (the English Beta) both start 80 px above "
             "the floor and lose the ground; m1 enters 落下する and m2 enters its own Fall, whose name conversion never "
             "changed; both land and stand; m3 (Alpha) stands and walks on the floor",
             "lower-japanese-mixed", 80,
             [mascot("m1", "Alpha", "Stand", 300, y=600), mascot("m2", "Beta", "Stand", 900, y=600),
              mascot("m3", "Alpha", "Stand", 700)],
             tape(*([0.5] * 8))),
    scenario("S-lower-japanese-03",
             "a requested name the converter renamed: Alpha's Transform (6 ticks, in the air) names its own 落下する, so the "
             "transform event requests 落下する and Alpha enters it, falls 80 px to the floor and stands",
             "lower-japanese-transform", 60, [mascot("m1", "Alpha", "Morph", 640, y=600)], tape(0.5, 0.5, 0.5, 0.5)),
]


# Mute intent fixtures are separate from every frozen policy/calibration recipe above.
# No fixture loads a pose sound or claims a Clip stop. Non-String and faulted reads
# deliberately expose runtime differences; they are not policy exemptions.
MUTE_LITERALS = (
    ("raw", "/TO"), ("same-canonical", "/to"), ("number-crossing", "/01"),
    ("boolean-crossing", "/true"), ("upper-true", "TRUE"), ("mixed-false", "False"),
    ("hex-crossing", "/0X1P1"), ("nbsp", chr(0xA0) + "1"),
    ("space-number", " 1"), ("empty", ""), ("slash", "/"),
    ("backslash", chr(92)), ("space", " "), ("null-text", "null"),
    ("number", "01"), ("true", "true"), ("infinity", "Infinity"),
    ("negative-infinity", "-Infinity"), ("nan", "NaN"), ("hex", "0x1p1"),
)


def mute_action(name, **parameters):
    return act(name, "Embedded", [], cls="Mute", border=None, **parameters)


def pack_mute_literals(root):
    actions = [posed("Stand", "Stay", [img("shime1.png", ticks=100)], Duration=100)]
    behaviours = [beh("Stand", 1)]
    for index, (_, value) in enumerate(MUTE_LITERALS):
        name = "Mute" + str(index)
        actions.append(mute_action(name, Sound=value))
        behaviours.append(beh(name, 0, hidden=True))
    member(root, "Alpha", actions, behaviours)


def pack_mute_bindings(root):
    member(root, "Alpha", [
        # Sound on a non-Mute action is unused, but still needs map provenance.
        posed("Stand", "Stay", [img("shime1.png", ticks=100)], Duration=100, Sound="/unused"),
        mute_action("Own", Sound="/TO"),
        act("Incoming", "Sequence", [aref("Own", Sound="/to"), aref("Stand")], Loop="false"),
        act("Inner", "Sequence", [aref("Own"), aref("Own", Sound="/To")], Loop="false"),
        act("Nested", "Sequence", [aref("Inner", Sound="/outer-unused"), aref("Stand")], Loop="false"),
        mute_action("ComputedString", Sound="${'1'}"),
        mute_action("ComputedNumber", Sound="${1}"),
        mute_action("Null", Sound="${null}"),
        mute_action("Absent"),
        mute_action("Fault", Sound="${Foo}"),
        act("FaultRefs", "Sequence", [aref("Fault", Foo="/a"), aref("Fault"), aref("Stand")], Loop="false"),
    ], [beh("Stand", 1),
        beh("Own", 0, hidden=True), beh("Incoming", 0, hidden=True),
        beh("BehaviourBind", 0, hidden=True, Action="Own", Sound="/tO"),
        beh("Nested", 0, hidden=True, Sound="/behaviour-unused"),
        *[beh(name, 0, hidden=True) for name in
          ("ComputedString", "ComputedNumber", "Null", "Absent", "Fault", "FaultRefs")]])


def pack_mute_reads(root):
    # Reading Sound makes normalized literals observable. Keep these probes in a
    # separate pack with no normalized literal, preserving the converter's guard.
    member(root, "Alpha", [
        posed("Stand", "Stay", [img("shime1.png", ticks=100)], Duration=100),
        mute_action("SelfReference", Sound="${Sound == null ? 'x' : 'y'}"),
        mute_action("Draw", Sound="${Math.random() < 2 ? null : 'x'}", Condition="${Sound == null}"),
    ], [beh("Stand", 1), beh("SelfReference", 0, hidden=True), beh("Draw", 0, hidden=True)])


def pack_mute_retained(root):
    member(root, "Alpha", [
        posed("Stand", "Stay", [img("shime1.png", ticks=100)], Duration=100),
        posed("Morph", "Embedded", [img("shime2.png", ticks=1)], cls="Transform",
              TransformMascot="Beta", TransformBehavior=""),
        mute_action("Own", Sound="/TO"),
        mute_action("OwnNull", Sound="${null}"),
        act("Retained", "Sequence", [aref("Morph"), aref("OwnNull"), aref("Own"), aref("Stand")], Loop="false"),
    ], [beh("Stand", 1), beh("Retained", 0, hidden=True)])
    member(root, "Beta", [
        posed("Stand", "Stay", [img("shime1.png", ticks=100)], Duration=100),
        mute_action("Own", Sound="/beta"),
    ], [beh("Stand", 1), beh("Own", 0, hidden=True)])


PACKS.update({"mute-literals": pack_mute_literals, "mute-bindings": pack_mute_bindings, "mute-reads": pack_mute_reads,
              "mute-retained": pack_mute_retained})
PACK_MAIN["mute-retained"] = "Alpha"
for index, (label, _) in enumerate(MUTE_LITERALS):
    SCENARIOS.append(scenario(
        "S-mute-literal-" + label + "-01",
        "Mute source-literal intent/type boundary: " + label + "; no pose audio or Clip-stop claim",
        "mute-literals", 4, [mascot("m1", "Alpha", "Mute" + str(index), 640)], tape(0.5)))
for label, behaviour in (("own", "Own"), ("incoming", "Incoming"),
                         ("behaviour", "BehaviourBind"), ("nested", "Nested"),
                         ("computed-string", "ComputedString"), ("computed-number", "ComputedNumber"),
                         ("fault-refs", "FaultRefs")):
    SCENARIOS.append(scenario(
        "S-mute-" + label + "-01",
        "Mute winning-definition/read control: " + label + "; raw intent only, runtime differences remain visible",
        "mute-bindings", 8, [mascot("m1", "Alpha", behaviour, 640)], tape(0.5)))
for label, behaviour in (("absent", "Absent"), ("null", "Null"), ("fault", "Fault")):
    for enabled in (False, True):
        SCENARIOS.append(scenario(
            "S-mute-" + label + ("-on-01" if enabled else "-off-01"),
            "Mute " + label + " with Sounds " + ("on" if enabled else "off")
            + "; empty audio cache, no Clip-stop assertion",
            "mute-bindings", 4, [mascot("m1", "Alpha", behaviour, 640)], tape(0.5),
            settings=cfg(sounds=enabled)))
SCENARIOS.append(scenario(
    "S-mute-self-reference-01", "Self-reference can report an inner fault and still return String x; preserve the actual outer result",
    "mute-reads", 4, [mascot("m1", "Alpha", "SelfReference", 640)], tape(0.5)))
SCENARIOS.append(scenario(
    "S-mute-draw-proof-01", "Sound/Condition draw observation-on/off proof; zero added evaluations, not a paired exactness claim",
    "mute-reads", 4, [mascot("m1", "Alpha", "Draw", 640)], tape(0.25, 0.5, 0.75)))
SCENARIOS.append(scenario(
    "S-mute-retained-owner-01", "Retained Alpha definitions run on Beta: null obeys Beta's Sounds-off switch, then named intent uses Alpha's spelling",
    "mute-retained", 8, [mascot("m1", "Alpha", "Retained", 640)], tape(0.5),
    typeSettings={"Alpha": {"sounds": True}, "Beta": {"sounds": False}}))


def pack_social_motion(root, family):
    """Start in a short wait so scans use a populated manager, as they would after a normal transition."""
    move = family == "ComplexMove"
    complex_action = family != "ScanJump"
    actions = [IDLE, posed("Met", "Stay", [img("shime6.png")], border=None, Duration=100)]
    behaviours = [beh("Idle", 1), beh("Met", 0, hidden=True)]
    phases = (("Plain", None), ("Trail", "Breed"), ("Seek", "Scan"), ("Together", "Breed,Scan")) if complex_action else (
        ("Seek", "Scan"), ("Flat", "Scan"))
    for name, characteristics in phases:
        scans = characteristics is not None and "Scan" in characteristics
        attrs = {}
        if complex_action and characteristics:
            attrs["Characteristics"] = characteristics
        if scans:
            attrs.update(Affordance="Hug", Behavior="Met", TargetBehavior="Hugged", TargetLook="true" if name != "Flat" else "false")
        else:
            attrs["TargetX"] = 600 if family == "ComplexJump" and name == "Trail" else 624
        if characteristics and "Breed" in characteristics:
            attrs.update(BornMascot="Alpha", BornBehavior="Idle", BornInterval=3)
        if move:
            body = [anim(WALK), TURN]
        else:
            body = [anim([img("shime3.png", ticks=4)])]
            attrs.update(VelocityParam=8, Arc="false" if name in ("Flat", "Trail") else "true")
            if not scans:
                attrs["TargetY"] = 680
        actions.append(act(name, "Embedded", body, cls=family, border="Floor" if move else None, **attrs))
        start = "Start" + name
        actions.append(posed(start, "Stay", [img("shime1.png", ticks=2)], Duration=2))
        behaviours.extend(then(name, start) + then("Idle", name))
    if move:
        actions.append(posed("Diagonal", "Embedded", [img("shime2.png", dx=-2, dy=2)], cls=family, border=None,
                             TargetX=624, TargetY=676, Behavior="Met", TargetBehavior="Hugged"))
        behaviours.extend(then("Idle", "Diagonal"))
    member(root, "Alpha", actions, behaviours)
    offerers(root)


for family in ("ScanJump", "ComplexMove", "ComplexJump"):
    pack = "p2-" + family.lower()
    PACKS[pack] = partial(pack_social_motion, family=family)
    PACK_MAIN[pack] = "Alpha"
    branches = [
        ("arrive", "Seek", "Offer", 664, "the first offering Beta is reached and both partners switch behaviours"),
        ("missing", "Seek", "Quiet", 664, "Beta never offers Hug and Alpha returns to Idle without a target"),
        ("loss", "Seek", "OfferBriefly", 850, "Beta stops offering before arrival and Alpha abandons the chase"),
    ]
    if family == "ScanJump":
        branches.append(("flat", "Flat", "Offer", 664, "the flat jump lands at Beta and leaves Beta's facing unchanged"))
    else:
        branches.extend([
            ("plain", "Plain", "Quiet", 664, "Alpha moves to its fixed x=624 and enters Idle without a scan or a birth"),
            ("breed", "Trail", "Quiet", 664, "Alpha travels to its fixed target and leaves children at interval positions"),
            ("both", "Together", "Offer", 664, "Alpha pursues Beta, produces interval births and enters Met on arrival"),
            ("breed-disabled", "Trail", "Quiet", 664, "Alpha reaches its fixed target with breeding disabled and produces no child"),
        ])
    for suffix, name, partner, partner_x, observation in branches:
        SCENARIOS.append(scenario(
            "S-" + pack + "-" + suffix + "-01", family + " starts after the wait and " + observation,
            pack, 34, [mascot("m1", "Alpha", "Start" + name, 640), mascot("m2", "Beta", partner, partner_x, right=True)],
            tape(0.5), settings=cfg(breeding=suffix != "breed-disabled")))
    SCENARIOS.append(scenario(
        "S-" + pack + "-first-01", family + " chooses the first offering Beta in manager order, past a nearer Beta",
        pack, 34, [mascot("m1", "Alpha", "StartSeek", 640), mascot("m2", "Beta", "Offer", 672, right=True),
                   mascot("m3", "Beta", "Offer", 648)], tape(0.5)))
SCENARIOS.append(scenario(
    "S-p2-complexmove-no-partner-01", "Without scanning, Alpha reaches both coordinates, enters Met and reports the absent partner",
    "p2-complexmove", 13, [mascot("m1", "Alpha", "Diagonal", 640, y=660)], tape(0.5)))


def pack_broadcast_family(root, family):
    poses = [img("shime2.png", dx=-2, ticks=2), img("shime3.png", dx=-2, ticks=2)]
    attrs = {"Broadcast": {}, "BroadcastStay": {"Duration": 8}, "BroadcastMove": {"TargetX": 624},
             "BroadcastJump": {"TargetX": 624, "TargetY": 680, "VelocityParam": 5, "Arc": "true"}}[family]
    actions = [posed("Offer", "Embedded", poses, cls=family, border=None, Affordance="Signal", **attrs),
               posed("Quiet", "Embedded", poses, cls=family, border=None, **attrs), IDLE]
    member(root, "Alpha", actions, then("Idle", "Offer", "Quiet") + [beh("Idle", 1)])


for family, motion in (("Broadcast", "plays its two frames once"), ("BroadcastStay", "plays until its duration ends"),
                       ("BroadcastMove", "walks to x=624"), ("BroadcastJump", "jumps to x=624")):
    pack = "p2-" + family.lower()
    PACKS[pack] = partial(pack_broadcast_family, family=family)
    for name, offered in (("Offer", "offers Signal while active"), ("Quiet", "has no offer")):
        SCENARIOS.append(scenario(
            "S-" + pack + "-" + name.lower() + "-01", family + " " + motion + " and " + offered + " before Idle",
            pack, 18, [mascot("m1", "Alpha", name, 640)], tape(0.5)))


def pack_air_carry(root, family):
    attrs = {"InitialVX": 2, "InitialVY": 1, "Gravity": 2} if family == "FallWithIE" else {
        "TargetX": 604, "TargetY": 624, "Arc": "false", "VelocityParam": 6}
    window_pack(root, posed("Carry", "Embedded", [img("shime4.png", ticks=30)], cls=family, border=None,
                            IeOffsetX=4, IeOffsetY=6, **attrs))


for family in ("FallWithIE", "JumpWithIE"):
    pack = "p2-" + family.lower()
    PACKS[pack] = partial(pack_air_carry, family=family)
    for suffix, right, throwing, enabled, visible, x, observation in (
            ("on-left", False, True, True, True, 636, "holds the right window edge while facing left"),
            ("on-right", True, True, True, True, 404, "holds the left window edge while facing right"),
            ("off", False, True, False, True, 636, "runs with the host window-action switch off and source Throwing on"),
            ("throwing-off", False, False, True, True, 636, "ends without carrying because Throwing is disabled"),
            ("missing", False, True, True, False, 636, "loses the grip because the active window is absent"),
            ("wrong-grip", False, True, True, True, 620, "starts away from the required corner and loses the grip")):
        SCENARIOS.append(scenario(
            "S-" + pack + "-" + suffix + "-01", family + " " + observation, pack, 14,
            [mascot("m1", "Alpha", "Carry", x, y=594, right=right)], tape(0.5),
            activeWindow=[{"tick": 0, "visible": visible, "rect": [400, 360, 640, 600], "title": "Scratch"}],
            settings=cfg(throwing=throwing, windowActions=enabled)))


def pack_turn_and_dispose(root):
    poses = [img("shime3.png", ticks=2), img("shime6.png", ticks=3)]
    actions = [posed("TurnRight", "Embedded", poses, cls="Turn", LookRight="true"),
               posed("Flip", "Embedded", poses, cls="Turn"),
               posed("Fade", "Embedded", poses, cls="SelfDestruct"),
               posed("Gone", "Embedded", [img("shime3.png", ticks=1)], cls="SelfDestruct"), IDLE]
    member(root, "Alpha", actions, then("Idle", "TurnRight", "Flip", "Fade", "Gone") + [beh("Idle", 1)])


def pack_instant_cases(root):
    wait = posed("Wait", "Stay", [img("shime2.png", ticks=4)], Duration=4)
    air = posed("Hold", "Stay", [img("shime3.png", ticks=20)], border=None, Duration=100)
    steps = {"Flip": bare("Flip", "Look"), "Left": bare("Left", "Look", LookRight="false"),
             "Shift": bare("Shift", "Offset", X=-12, Y=-8), "Zero": bare("Zero", "Offset"),
             "Raise": bare("Raise", "BringToFront")}
    actions = [wait, air, *steps.values()]
    behaviours = [beh("Hold", 1)]
    for name in steps:
        label = "After" + name
        actions.append(act(label, "Sequence", [aref("Wait"), aref(name), aref("Hold")], border=None))
        behaviours.append(beh(label, 0, hidden=True))
    member(root, "Alpha", actions, behaviours)
    member(root, "Beta", [posed("Cover", "Stay", [img("shime6.png", ticks=20)], Duration=100)], [beh("Cover", 1)])


PACKS.update({"p2-turn-dispose": pack_turn_and_dispose, "p2-instant": pack_instant_cases})
PACK_MAIN["p2-instant"] = "Alpha"
for suffix, name, facing, observation in (
        ("turn-change", "TurnRight", False, "Alpha faces right and plays the turn animation before Idle"),
        ("turn-same", "TurnRight", True, "already facing right, Alpha skips the turn animation and enters Idle"),
        ("turn-default", "Flip", False, "Turn reads the opposite of Alpha's current facing on each step"),
        ("selfdestruct-one", "Gone", False, "Alpha requests disposal on its single pose and leaves the manager"),
        ("selfdestruct-many", "Fade", False, "Alpha survives the first pose and requests disposal on the last frame")):
    SCENARIOS.append(scenario("S-p2-" + suffix + "-01", observation, "p2-turn-dispose", 10,
                              [mascot("m1", "Alpha", name, 640, right=facing), mascot("m2", "Alpha", "Idle", 740)],
                              tape(0.5)))
for suffix, name, observation in (
        ("look-default", "Flip", "Look without a parameter flips both mascots from their opposite starting facings"),
        ("look-left", "Left", "an explicit left Look aligns the two mascots from opposite starting facings"),
        ("offset-signed", "Shift", "both mascots move 12 px left and 8 px up regardless of their facing"),
        ("offset-default", "Zero", "both mascots keep their original positions after an Offset with no parameters")):
    SCENARIOS.append(scenario("S-p2-" + suffix + "-01", observation, "p2-instant", 10,
                              [mascot("m1", "Alpha", "After" + name, 620),
                               mascot("m2", "Alpha", "After" + name, 660, right=True)], tape(0.5)))
SCENARIOS.append(scenario(
    "S-p2-bringtofront-overlap-01", "the overlapping blue mascot raises its window after four ticks while green keeps its pose",
    "p2-instant", 12, [mascot("m1", "Alpha", "AfterRaise", 640), mascot("m2", "Beta", "Cover", 648)], tape(0.5)))


def pack_toggle_selection(root):
    lead = posed("Lead", "Stay", [img("shime2.png")], Duration=2)
    direct = posed("Direct", "Stay", [img("shime2.png")], Duration=2)
    gated = posed("Gated", "Stay", [img("shime3.png")], Duration=100)
    tail = posed("Fallback", "Stay", [img("shime6.png")], Duration=100)
    next_list = ('<Behavior Name="Direct" Frequency="0"><NextBehaviorList Add="false">'
                 '<BehaviorReference Name="Gated" Frequency="100" Toggleable="true"/>'
                 '<BehaviorReference Name="Fallback" Frequency="1"/></NextBehaviorList></Behavior>')
    for name in ("Alpha", "Beta"):
        member(root, name, [lead, direct, gated, tail],
               [beh("Lead", 0), next_list, beh("Gated", 100, Toggleable="true"), beh("Fallback", 1)])


def pack_condition_selection(root):
    actions = [posed("Lead", "Stay", [img("shime2.png")], Duration=2),
               posed("Pick", "Stay", [img("shime3.png")], Duration=100),
               posed("Other", "Stay", [img("shime6.png")], Duration=100)]
    own = beh("Pick", 100, Condition="#{mascot.environment.cursor.x > 500}")
    group = '<Condition Condition="#{mascot.environment.cursor.y > 300}">' + own + '</Condition>'
    member(root, "Alpha", actions, [beh("Lead", 0), group, beh("Other", 1)])


def pack_animation_choice(root):
    actions = []
    for name, condition in (("Frame", "#{mascot.environment.cursor.x > 500}"),
                            ("Once", "${mascot.environment.cursor.x > 500}")):
        actions.append(act(name, "Stay", [anim([img("shime3.png")], Condition=condition),
                                           anim([img("shime6.png")])], Duration=100))
    member(root, "Alpha", actions, [beh("Frame", 1), beh("Once", 0, hidden=True)])


PACKS.update({"p2-toggle": pack_toggle_selection, "p2-condition": pack_condition_selection,
              "p2-animation": pack_animation_choice})
PACK_MAIN["p2-toggle"] = "Alpha"
for source, name in (("root", "Lead"), ("next", "Direct")):
    for enabled in (True, False):
        switch = "on" if enabled else "off"
        SCENARIOS.append(scenario(
            "S-p2-toggle-" + source + "-" + switch + "-01",
            "the " + source + " choice takes " + ("Gated" if enabled else "Fallback") + " with the behaviour toggle " + switch,
            "p2-toggle", 8, [mascot("m1", "Alpha", name, 640)], tape(0.5),
            settings=cfg(**{"behaviour:Gated": enabled})))
SCENARIOS.append(scenario(
    "S-p2-toggle-member-01", "only Beta enables Gated over the disabled global setting, while Alpha takes Fallback",
    "p2-toggle", 8, [mascot("m1", "Alpha", "Lead", 620), mascot("m2", "Beta", "Lead", 660)], tape(0.5, 0.5),
    settings=cfg(**{"behaviour:Gated": False}), typeSettings={"Beta": {"behaviour:Gated": True}}))
for suffix, x, y, picked in (("both", 750, 450, "Pick"), ("own-false", 250, 450, "Other"),
                            ("group-false", 750, 150, "Other"), ("both-false", 250, 150, "Other")):
    SCENARIOS.append(scenario(
        "S-p2-condition-" + suffix + "-01", "the enclosing cursor-Y guard and behaviour cursor-X guard select " + picked,
        "p2-condition", 8, [mascot("m1", "Alpha", "Lead", 640)], tape(0.5), input=[{"tick": 0, "cursor": [x, y]}]))
for name, cache in (("Frame", "frame"), ("Once", "action")):
    for initial, x in (("low", 250), ("high", 750)):
        SCENARIOS.append(scenario(
            "S-p2-animation-" + cache + "-" + initial + "-01",
            "the cursor crosses the " + cache + "-cached first variant guard on tick 4 and crosses back on tick 8",
            "p2-animation", 12, [mascot("m1", "Alpha", name, 640)], NONE,
            input=[{"tick": 0, "cursor": [x, 100]}, {"tick": 4, "cursor": [1000 - x, 100]},
                   {"tick": 8, "cursor": [x, 100]}]))


def pack_mute_prefix(root):
    # Two declared clips make a case-sensitive prefix and a non-match observable during conversion.
    # The directed behaviours leave the sounding poses unselected. The trace witnesses Mute intent.
    prefix = mute_action("Prefix", Sound="/To")
    all_clips = mute_action("All")
    declarations = [img("shime2.png").replace("/>", ' Sound="/Tone.wav"/>'),
                    img("shime3.png").replace("/>", ' Sound="/Other.wav"/>')]
    wait = posed("Wait", "Stay", [img("shime1.png")], Duration=2)
    actions = [prefix, all_clips, wait, IDLE, posed("Library", "Animate", declarations)]
    for name in ("Prefix", "All"):
        actions.append(act("After" + name, "Sequence", [aref("Wait"), aref(name), aref("Idle")], border=None))
    member(root, "Alpha", actions, [beh("Idle", 1), beh("AfterPrefix", 0), beh("AfterAll", 0), beh("Library", 0)])
    directory = root / "sound"
    directory.mkdir()
    for name in ("Tone.wav", "Other.wav"):
        (directory / name).write_bytes(wave_bytes(10, 80, amplitude=0))


PACKS["p2-mute-prefix"] = pack_mute_prefix
for name in ("Prefix", "All"):
    for enabled in (True, False):
        switch = "on" if enabled else "off"
        SCENARIOS.append(scenario(
            "S-p2-mute-" + name.lower() + "-sounds-" + switch + "-01",
            "after two ticks Alpha reads " + ("the source prefix /To" if name == "Prefix" else "an absent Sound") +
            " with two declared clips and Sounds " + switch, "p2-mute-prefix", 8,
            [mascot("m1", "Alpha", "After" + name, 640)], tape(0.5), settings=cfg(sounds=enabled)))


OUTPUT_MANIFEST = ".make-packs-manifest.json"
OUTPUT_SCHEMA = "shimeji-parity-pack-output/1"


def write_output_manifest(out, paths, in_progress=False):
    """Replace the journal atomically: an interrupted write must leave the previous ownership readable."""
    document = {"schema": OUTPUT_SCHEMA, "paths": sorted(paths), "state": "building" if in_progress else "ready"}
    descriptor, temporary = tempfile.mkstemp(prefix=OUTPUT_MANIFEST + ".", suffix=".tmp", dir=out)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(json.dumps(document, indent=1, sort_keys=True) + "\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, out / OUTPUT_MANIFEST)
    finally:
        Path(temporary).unlink(missing_ok=True)


def generated_paths(out):
    """Enumerate the managed namespaces completely before any deletion, without traversing links."""
    paths = set()

    def remember(path):
        if path.is_symlink() or getattr(path.lstat(), "st_file_attributes", 0) & 0x400:
            raise ValueError("PACK_GENERATOR_LINKED_OUTPUT: " + path.relative_to(out).as_posix())
        paths.add(path.relative_to(out).as_posix())

    def unreadable(error):
        raise ValueError("PACK_GENERATOR_UNREADABLE_OUTPUT: " + str(error)) from error

    packs = out / "packs"
    if packs.exists() or packs.is_symlink():
        remember(packs)
        if not packs.is_dir():
            raise ValueError("PACK_GENERATOR_INVALID_OUTPUT: packs is not a directory")
        for folder, directories, files in os.walk(packs, followlinks=False, onerror=unreadable):
            for name in directories + files:
                remember(Path(folder) / name)
    for scenario_file in out.glob("S-*.json"):
        remember(scenario_file)
        if not scenario_file.is_file():
            raise ValueError("PACK_GENERATOR_INVALID_OUTPUT: scenario output is not a file")
    return paths


def require_owned_output(out):
    actual = generated_paths(out)
    manifest = out / OUTPUT_MANIFEST
    if manifest.is_symlink() or (manifest.exists() and getattr(manifest.lstat(), "st_file_attributes", 0) & 0x400):
        raise ValueError("PACK_GENERATOR_LINKED_OUTPUT: " + OUTPUT_MANIFEST)
    if actual and not manifest.exists():
        raise ValueError("PACK_GENERATOR_UNMANAGED_OUTPUT: populated packs or S-*.json output has no ownership manifest; "
                         "use an empty output folder, or move those existing files elsewhere before retrying")
    if manifest.exists():
        ownership = json.loads(manifest.read_text(encoding="utf-8"))
        if (not isinstance(ownership, dict) or ownership.get("schema") != OUTPUT_SCHEMA
                or not isinstance(ownership.get("paths"), list)
                or not all(isinstance(path, str) for path in ownership["paths"])):
            raise ValueError("PACK_GENERATOR_INVALID_MANIFEST: unrecognized output ownership manifest")
        unknown = actual - set(ownership["paths"])
        if unknown:
            raise ValueError("PACK_GENERATOR_UNOWNED_OUTPUT: " + sorted(unknown)[0])
        return set(ownership["paths"])
    return set()


def main(out):
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    # Overwriting known leaves alone retains retired members or sprites, making hashes depend on destination
    # history (N-audit-1106-275). Ownership must be checked FIRST: a caller's custom pack or scenario has the same
    # shape as ours and must never be inferred to be disposable from its name alone. Retired paths from a previous
    # generator version remain removable because that version recorded them in its manifest.
    owned = require_owned_output(out)
    # Journal before ANY builder or cleanup. Builders run in private staging, so even a new builder which makes
    # directories and then raises cannot leave unowned files in the destination. Only after staging succeeds can
    # we know every exact future path; journal those together with retired old paths BEFORE deleting or copying.
    # A pack-name wildcard would wrongly claim a caller's additions after an interruption.
    # Keep ONLY old ownership here. A caller can create even a planned scenario filename while staging runs;
    # claiming that name now would make a refused run silently adopt it for deletion on the following run.
    write_output_manifest(out, owned, in_progress=True)
    with tempfile.TemporaryDirectory(prefix="parity-pack-stage-") as temporary:
        staged = Path(temporary)
        for name, build in PACKS.items():
            build(staged / "packs" / name)
        for s in SCENARIOS:
            doc = dict(BASE)
            doc.update(s)
            (staged / f"{s['id']}.json").write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n",
                                                  encoding="utf-8", newline="\n")
        staged_paths = generated_paths(staged)
        # A caller could have added output while the builders ran. Planned names alone do not license deleting
        # those files: only paths from the ownership we read before starting belong to the previous generation.
        unknown = generated_paths(out) - owned
        if unknown:
            raise ValueError("PACK_GENERATOR_UNOWNED_OUTPUT: " + sorted(unknown)[0])
        write_output_manifest(out, owned | staged_paths, in_progress=True)
        packs = out / "packs"
        if packs.exists():
            shutil.rmtree(packs)
        for previous in out.glob("S-*.json"):
            previous.unlink()
        shutil.copytree(staged / "packs", packs)
        for scenario in staged.glob("S-*.json"):
            shutil.copyfile(scenario, out / scenario.name)
        actual = generated_paths(out)
        unknown = actual - staged_paths
        if unknown:
            raise ValueError("PACK_GENERATOR_UNOWNED_OUTPUT: " + sorted(unknown)[0])
        write_output_manifest(out, actual)
    print(f"wrote {len(PACKS)} pack(s) and {len(SCENARIOS)} scenario(s) to {out}")


def _check_tape_exhaustion_fixture(out):
    """Pin the generated recipe; fresh paired runs prove its actual draw cadence."""
    import xml.etree.ElementTree as ET

    def require(condition, name):
        if not condition:
            raise AssertionError(name)

    path = out / "S-rng-tape-short-01.json"
    require(path.is_file(), "TAPE_EXHAUSTION_SCENARIO_WITNESS")
    document = json.loads(path.read_text(encoding="utf-8"))
    require(document["pack"] == "packs/select-weighted" and document["ticks"] == 20
            and document["mascots"] == [{"sid": "m1", "imageSet": "Alpha", "behavior": "A",
                                         "anchor": [640, 680], "lookRight": False}], "TAPE_EXHAUSTION_START_WITNESS")
    conf = out / document["pack"] / "img/Alpha/conf"
    actions = {node.get("Name"): node for node in ET.parse(conf / "actions.xml").findall(".//{" + NS + "}Action")}
    for index, name in enumerate(("A", "B"), 1):
        node = actions[name]
        require(node.attrib == {"Name": name, "Type": "Stay", "BorderType": "Floor", "Duration": "4"},
                "TAPE_EXHAUSTION_DURATION_WITNESS")
        animations = list(node)
        require(len(animations) == 1 and animations[0].tag == "{" + NS + "}Animation" and not animations[0].attrib,
                "TAPE_EXHAUSTION_LITERAL_ACTION_WITNESS")
        poses = list(animations[0])
        require(len(poses) == 1 and poses[0].tag == "{" + NS + "}Pose"
                and poses[0].attrib == {"Image": "/shime" + str(index) + ".png", "ImageAnchor": "32,64",
                                        "Velocity": "0,0", "Duration": "2"}, "TAPE_EXHAUSTION_LITERAL_ACTION_WITNESS")
    roots = ET.parse(conf / "behaviors.xml").findall(".//{" + NS + "}Behavior")
    weighted = {node.get("Name"): node for node in roots if node.get("Frequency") != "0"}
    require({name: node.get("Frequency") for name, node in weighted.items()} == {"A": "1", "B": "3"},
            "TAPE_EXHAUSTION_WEIGHTS_WITNESS")
    require(all(node.get("Condition") is None and not list(node) for node in weighted.values()),
            "TAPE_EXHAUSTION_LITERAL_SELECTION_WITNESS")
    draws = list(range(5, document["ticks"] + 1, 4))
    require(draws == [5, 9, 13, 17] and document["rng"]["kind"] == "tape"
            and len(document["rng"]["values"]) == len(draws) - 1, "TAPE_EXHAUSTION_ONE_DRAW_SHORT_WITNESS")
    require(document["rng"]["values"] == [0.1, 0.6, 0.9], "TAPE_EXHAUSTION_VALUES_WITNESS")


def self_test():
    # Small synthetic packs cover ownership/recovery writers and the literal one-draw-short closure recipe.
    saved = dict(PACKS)
    try:
        PACKS.clear()
        PACKS.update({"slice-stand-walk": pack_stand_walk, "lower-sound": pack_lower_sound,
                      "select-weighted": pack_select_weighted})
        return _self_test_output()
    finally:
        PACKS.clear()
        PACKS.update(saved)


def _self_test_output():
    """A reused destination must supply exactly the same parity inputs as a fresh one."""
    import contextlib
    import io

    def inventory(root):
        files = list((root / "packs").rglob("*")) + list(root.glob("S-*.json"))
        return {p.relative_to(root).as_posix(): p.read_bytes() for p in files if p.is_file()}

    def require(condition, label):
        if not condition:
            raise AssertionError(label)

    def refuse_unchanged(root, prefix, label):
        before = {p.relative_to(root).as_posix(): p.read_bytes() for p in root.rglob("*") if p.is_file()}
        first = next(iter(PACKS))
        saved_builder = PACKS[first]

        def unexpected_builder(path):
            raise AssertionError(label + ": unowned output reached a builder instead of refusing at inspection")

        try:
            PACKS[first] = unexpected_builder
            main(root)
        except ValueError as error:
            require(str(error).startswith(prefix), label + ": wrong refusal: " + str(error))
        else:
            raise AssertionError(label + ": custom output was not refused")
        finally:
            PACKS[first] = saved_builder
        after = {p.relative_to(root).as_posix(): p.read_bytes() for p in root.rglob("*") if p.is_file()}
        require(before == after, label + ": refusal changed caller files")

    with tempfile.TemporaryDirectory(prefix="parity-pack-generator-") as temporary:
        clean, reused = Path(temporary) / "clean", Path(temporary) / "reused"
        with contextlib.redirect_stdout(io.StringIO()):
            main(clean)
        _check_tape_exhaustion_fixture(clean)
        require(json.loads((clean / OUTPUT_MANIFEST).read_text(encoding="utf-8")).get("state") == "ready",
                "PACK_GENERATOR_READY_MANIFEST: completed output is not marked ready")
        shutil.copytree(clean, reused)
        first_pack = reused / "packs" / next(iter(PACKS))
        stale_frame = first_pack / "img" / "retired-member" / "obsolete.png"
        stale_frame.parent.mkdir(parents=True)
        stale_frame.write_bytes(b"old sprite")
        old_pack = reused / "packs" / "retired-pack"
        old_pack.mkdir()
        (old_pack / "obsolete.xml").write_bytes(b"old pack")
        old_scenario = reused / "S-retired-scenario.json"
        old_scenario.write_bytes(b"old scenario")
        # Simulate a previous generator version which owned these now-retired paths. A bare stale-looking file
        # without this ownership is caller data, tested separately below, not permission to delete it.
        (reused / OUTPUT_MANIFEST).write_text(json.dumps({"schema": OUTPUT_SCHEMA,
                                                        "paths": sorted(generated_paths(reused))}), encoding="utf-8")
        keep = reused / "notes.txt"
        keep.write_bytes(b"caller notes")
        with contextlib.redirect_stdout(io.StringIO()):
            try:
                main(reused)
            except Exception:
                # The final inventory/copy can notice a missed cleanup before the assertions below. Regeneration
                # still has to succeed, and these exact retired paths identify which cleanup was omitted.
                require(not stale_frame.exists(), "PACK_GENERATOR_STALE_MEMBER: regeneration left a retired member")
                require(not old_scenario.exists(), "PACK_GENERATOR_STALE_SCENARIO: regeneration left a retired scenario")
                raise
        require(not stale_frame.exists(), "PACK_GENERATOR_STALE_MEMBER: retired member survived regeneration")
        require(not old_pack.exists(), "PACK_GENERATOR_STALE_PACK: retired pack survived regeneration")
        require(not old_scenario.exists(), "PACK_GENERATOR_STALE_SCENARIO: retired scenario survived regeneration")
        require(inventory(reused) == inventory(clean), "PACK_GENERATOR_HISTORY: reused output differs from fresh output")
        require(keep.read_bytes() == b"caller notes", "PACK_GENERATOR_NOTES: unrelated output-root file changed")
        require((reused / OUTPUT_MANIFEST).read_bytes() == (clean / OUTPUT_MANIFEST).read_bytes(),
                "PACK_GENERATOR_MANIFEST: fresh and reused ownership differ")

        # An interruption during cleanup must retain the old version's exact retired paths. Recording only this
        # version's planned roots would make the surviving retired files look like caller data on the next run.
        interrupted_cleanup = Path(temporary) / "interrupted-cleanup"
        shutil.copytree(clean, interrupted_cleanup)
        retired = interrupted_cleanup / "packs" / "retired-pack" / "old.txt"
        retired.parent.mkdir()
        retired.write_bytes(b"old owned data")
        write_output_manifest(interrupted_cleanup, generated_paths(interrupted_cleanup))
        remove_tree = shutil.rmtree

        def stop_cleanup(path, *args, **kwargs):
            if Path(path) == interrupted_cleanup / "packs":
                journal = json.loads((interrupted_cleanup / OUTPUT_MANIFEST).read_text(encoding="utf-8"))
                require(journal.get("state") == "building", "PACK_GENERATOR_PROVISIONAL_BEFORE_CLEANUP")
                require(retired.relative_to(interrupted_cleanup).as_posix() in journal["paths"],
                        "PACK_GENERATOR_RETIRED_OWNERSHIP: cleanup forgot an old owned path")
                remove_tree(Path(path) / next(iter(PACKS)))
                raise KeyboardInterrupt("synthetic cleanup interruption")
            return remove_tree(path, *args, **kwargs)

        try:
            shutil.rmtree = stop_cleanup
            try:
                main(interrupted_cleanup)
            except KeyboardInterrupt:
                pass
            else:
                raise AssertionError("PACK_GENERATOR_CLEANUP_CONTROL: interruption was not reached")
        finally:
            shutil.rmtree = remove_tree
        with contextlib.redirect_stdout(io.StringIO()):
            main(interrupted_cleanup)
        require(inventory(interrupted_cleanup) == inventory(clean), "PACK_GENERATOR_CLEANUP_RECOVERY")

        # An interruption while staged files are copied needs the complete future inventory, not only pack roots.
        interrupted_copy = Path(temporary) / "interrupted-copy"
        copy_tree = shutil.copytree

        def stop_copy(source, destination, *args, **kwargs):
            if Path(destination) == interrupted_copy / "packs":
                name = next(iter(PACKS))
                copy_tree(Path(source) / name, Path(destination) / name)
                raise KeyboardInterrupt("synthetic copy interruption")
            return copy_tree(source, destination, *args, **kwargs)

        try:
            shutil.copytree = stop_copy
            try:
                main(interrupted_copy)
            except KeyboardInterrupt:
                pass
            else:
                raise AssertionError("PACK_GENERATOR_COPY_CONTROL: interruption was not reached")
        finally:
            shutil.copytree = copy_tree
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                main(interrupted_copy)
        except Exception as error:
            raise AssertionError("PACK_GENERATOR_COPY_RECOVERY: " + str(error)) from error
        require(inventory(interrupted_copy) == inventory(clean), "PACK_GENERATOR_COPY_HISTORY")

        # Builders do not have to use a special writer API: raw mkdir/write failures in a newly added pack stay
        # private to staging. RuntimeError and KeyboardInterrupt both leave a destination the next main can use.
        added_pack = "selftest-new-pack"
        expected_new = Path(temporary) / "expected-new"

        def build_new(root):
            write_documents(root / "img" / "New" / "conf", [], [])

        try:
            PACKS[added_pack] = build_new
            with contextlib.redirect_stdout(io.StringIO()):
                main(expected_new)
            for failure in (RuntimeError, KeyboardInterrupt):
                interrupted = Path(temporary) / failure.__name__
                if failure is KeyboardInterrupt:
                    shutil.copytree(clean, interrupted)

                def fail_new(root):
                    require((interrupted / OUTPUT_MANIFEST).is_file(), "PACK_GENERATOR_PROVISIONAL_BEFORE_BUILD")
                    journal = json.loads((interrupted / OUTPUT_MANIFEST).read_text(encoding="utf-8"))
                    require(journal.get("state") == "building", "PACK_GENERATOR_PROVISIONAL_BEFORE_BUILD")
                    (root / "img").mkdir(parents=True)
                    (root / "img" / "partial.txt").write_bytes(b"partial new pack")
                    raise failure("synthetic builder interruption")

                PACKS[added_pack] = fail_new
                try:
                    main(interrupted)
                except failure:
                    pass
                else:
                    raise AssertionError("PACK_GENERATOR_BUILD_CONTROL: interruption was not reached")
                PACKS[added_pack] = build_new
                # A caller's addition inside the newly created pack is not part of its provisional ownership.
                custom_interrupted = Path(temporary) / (failure.__name__ + "-custom")
                shutil.copytree(interrupted, custom_interrupted)
                caller = custom_interrupted / "packs" / added_pack / "img" / "New" / "caller.txt"
                caller.parent.mkdir(parents=True, exist_ok=True)
                caller.write_bytes(b"caller data after interruption")
                refuse_unchanged(custom_interrupted, "PACK_GENERATOR_UNOWNED_OUTPUT:",
                                 "PACK_GENERATOR_INTERRUPTED_CUSTOM_PRESERVED")
                try:
                    with contextlib.redirect_stdout(io.StringIO()):
                        main(interrupted)
                except Exception as error:
                    raise AssertionError("PACK_GENERATOR_INTERRUPTED_RECOVERY: " + failure.__name__ + ": " + str(error)) from error
                require(inventory(interrupted) == inventory(expected_new), "PACK_GENERATOR_INTERRUPTED_HISTORY")
                require((interrupted / OUTPUT_MANIFEST).read_bytes() == (expected_new / OUTPUT_MANIFEST).read_bytes(),
                        "PACK_GENERATOR_INTERRUPTED_MANIFEST")

            # Caller files arriving during staging or after the copy may not become generator-owned.
            for stage, label in (("before-write", "PACK_GENERATOR_LATE_CLAIM_PRESERVED"),
                                 ("planned-scenario", "PACK_GENERATOR_PLANNED_CALLER_PRESERVED"),
                                 ("at-completion", "PACK_GENERATOR_LATE_OUTPUT_PRESERVED")):
                destination = Path(temporary) / stage
                caller = (destination / (SCENARIOS[0]["id"] + ".json") if stage == "planned-scenario"
                          else destination / "packs" / added_pack / "img" / "New" / "conf" / (
                              "actions.xml" if stage == "before-write" else "caller.txt"))

                def late_caller(root):
                    build_new(root)
                    if stage in ("before-write", "planned-scenario"):
                        caller.parent.mkdir(parents=True, exist_ok=True)
                        caller.write_bytes(b"caller data during generation")

                def late_copy(source, target, *args, **kwargs):
                    result = copy_tree(source, target, *args, **kwargs)
                    if stage == "at-completion" and Path(target) == destination / "packs":
                        caller.parent.mkdir(parents=True, exist_ok=True)
                        caller.write_bytes(b"caller data during generation")
                    return result

                PACKS[added_pack] = late_caller
                try:
                    shutil.copytree = late_copy
                    main(destination)
                except ValueError as error:
                    require(str(error).startswith("PACK_GENERATOR_UNOWNED_OUTPUT:"), label + ": wrong refusal")
                else:
                    raise AssertionError(label + ": caller data was claimed as generated output")
                finally:
                    shutil.copytree = copy_tree
                require(caller.read_bytes() == b"caller data during generation", label + ": caller data changed")
                journal = json.loads((destination / OUTPUT_MANIFEST).read_text(encoding="utf-8"))
                require(caller.relative_to(destination).as_posix() not in journal["paths"], label + ": caller data was journalled")
                PACKS[added_pack] = build_new
                refuse_unchanged(destination, "PACK_GENERATOR_UNOWNED_OUTPUT:", label)
        finally:
            del PACKS[added_pack]

        unmanaged = Path(temporary) / "unmanaged"
        custom = unmanaged / "packs" / "caller-pack" / "custom.xml"
        custom.parent.mkdir(parents=True)
        custom.write_bytes(b"caller pack")
        refuse_unchanged(unmanaged, "PACK_GENERATOR_UNMANAGED_OUTPUT:", "PACK_GENERATOR_UNMANAGED_PRESERVED")
        for name, relative, label in (
                ("custom-pack", "packs/caller-pack/custom.xml", "PACK_GENERATOR_CUSTOM_PACK_PRESERVED"),
                ("custom-scenario", "S-custom.json", "PACK_GENERATOR_CUSTOM_SCENARIO_PRESERVED")):
            destination = Path(temporary) / name
            shutil.copytree(clean, destination)
            caller_file = destination / relative
            caller_file.parent.mkdir(parents=True, exist_ok=True)
            caller_file.write_bytes(b"caller content")
            refuse_unchanged(destination, "PACK_GENERATOR_UNOWNED_OUTPUT:", label)
    print("MAKE-PACKS SELF-TEST PASS")
    return 0


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        sys.exit(self_test())
    main(sys.argv[1])
