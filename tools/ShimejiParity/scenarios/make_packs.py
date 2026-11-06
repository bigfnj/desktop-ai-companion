"""Build the synthetic, IP-free parity packs and scenarios (CONTRACT.md section 1).

    make_packs.py <out dir>

Writes <out>/packs/<pack>/ (a Shimeji-EE layout: img/<Set>/conf/actions.xml, img/<Set>/conf/behaviors.xml,
img/<Set>/*.png; the conf-tiers pack keeps its documents at the source's lower tiers, conf/<Set>/ and conf/) and
<out>/<scenario id>.json for every scenario below. The sprites are generated, so nothing here is anyone's art: each is
an asymmetric body (a notch on its LEFT side, so a mirrored frame is visibly mirrored), a red anchor dot at the
authored ImageAnchor, and its frame number as white marks along the top, so a clip shows which pose is on screen.
Deterministic: the same inputs give byte-identical files, so a scenario's SHA-256 is stable.

Beyond the three vertical-slice scenarios, every Shimeji feature and branch the parity suite covers has a DIRECTED
scenario (rng none or a chosen tape, a fixed tick count, a description a reader can check against the trace). Packs are
shared where one conf serves several scenarios; PACK_MAIN names the main image set the converter's paritypack verb
needs for a cross-member or hidden-member pack.
"""
import json
import sys
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
    {"id": "S-slice-breed-01", "description": "Alpha breeds one .Ball 40 px in front of it, which rolls; Alpha then stands",
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
def wave_bytes(period, frames):
    """A deterministic 8 kHz mono 16-bit PCM square wave (generated, nobody's recording)."""
    import io
    import struct
    import wave
    data = b"".join(struct.pack("<h", 6000 if (i // period) % 2 == 0 else -6000) for i in range(frames))
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
