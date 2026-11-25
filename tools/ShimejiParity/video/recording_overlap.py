"""Repeatable GUI staging of the synthetic two-mascot overlap fixture."""

import shutil
from xml.etree import ElementTree as ET


def stage_overlap(pack, target, region):
    """Seat the two starters, show the cover, then call the original AfterRaise."""
    shutil.copytree(pack, target)
    namespace = "http://www.group-finity.com/Mascot"
    ET.register_namespace("", namespace)
    tag = lambda name: "{" + namespace + "}" + name
    center = region[0] + region[2] // 2
    floor = region[1] + region[3]
    for member, offset, original in (("Alpha", -8, "AfterRaise"), ("Beta", 8, "Cover")):
        conf = target / "img" / member / "conf"
        actions = ET.parse(conf / "actions.xml")
        behaviors = ET.parse(conf / "behaviors.xml")
        action_list = actions.getroot().find(tag("ActionList"))
        behavior_list = behaviors.getroot().find(tag("BehaviorList"))
        if action_list is None or behavior_list is None:
            raise ValueError("overlap pack lacks a member action/behavior list")
        stage = ET.SubElement(action_list, tag("Action"), Name="RecordingSetup", Type="Sequence")
        ET.SubElement(
            stage,
            tag("Action"),
            Type="Embedded",
            Class="com.group_finity.mascot.action.Offset",
            X="#{" + str(center + offset) + " - mascot.anchor.x}",
            Y="#{" + str(floor) + " - mascot.anchor.y}",
        )
        delay = ET.SubElement(stage, tag("Action"), Type="Stay", Duration="100" if member == "Alpha" else "20")
        anim = ET.SubElement(delay, tag("Animation"))
        ET.SubElement(anim, tag("Pose"), Image="/shime2.png", ImageAnchor="32,64", Velocity="0,0", Duration="20")
        if member == "Beta":
            ET.SubElement(
                stage, tag("Action"), Type="Embedded", Class="com.group_finity.mascot.action.BringToFront"
            )
        reference = ET.SubElement(stage, tag("ActionReference"), Name=original)
        if member == "Beta":
            # Keep the original Cover pose for the rest of a take. Re-entering
            # staging would raise Beta again and race Alpha's observed raise.
            reference.set("Duration", "1200")
        for behavior in behavior_list.findall(tag("Behavior")):
            behavior.set("Frequency", "0")
        ET.SubElement(behavior_list, tag("Behavior"), Name="RecordingSetup", Frequency="1")
        actions.write(conf / "actions.xml", encoding="utf-8", xml_declaration=True)
        behaviors.write(conf / "behaviors.xml", encoding="utf-8", xml_declaration=True)
    return {
        "kind": "synthetic overlap setup",
        "centerX": center,
        "floorY": floor,
        "description": "Both runs use the same temporary staging sequence. Beta raises once and enters its original "
        "Cover with a 1200-tick duration. Alpha waits 100 ticks before entering the original four-tick AfterRaise sequence.",
    }
