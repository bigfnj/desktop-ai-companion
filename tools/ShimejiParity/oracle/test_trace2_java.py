"""Compile original oracle Java against synthetic API stubs and exercise its real protocol paths.

This is not a pinned-runtime parity run or evidence for typed failure-site attribution. It neither reads nor copies
upstream source. Each mutation compiles fresh classes from private copies and requires exactly its named witness.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time


HERE = Path(__file__).resolve().parent
PACKAGE = "com/group_finity/mascot"
JAVA_FILES = ("V1ScenarioRunner.java", "draft/DraftParityHooks.java", "draft/DraftTraceWriter.java", "Trace2ProtocolProbe.java")
PROCESS_RECORDS = []


STUBS = {
    "Main": """public final class Main {
        private static final Main instance = new Main();
        public static Main getInstance() { return instance; }
        private Manager manager = new Manager(); private Platform platform; private java.util.ResourceBundle languageBundle;
        private java.util.Properties properties = new java.util.Properties();
        private java.util.Map<String,config.Configuration> configurations = new java.util.LinkedHashMap<String,config.Configuration>();
        public java.util.Properties getProperties() { return properties; }
        public config.Configuration getConfiguration(String name) { return configurations.get(name); }
        private boolean loadConfiguration(String name) {
            if (!"StartupSynthetic".equals(name)) return false;
            configurations.put(name, new config.Configuration());
            return true;
        }
        public boolean getMascotBooleanSetting(String name, Mascot mascot) { return false; }
    }""",
    "Manager": """public final class Manager {
        private java.util.List<Mascot> mascots = new java.util.ArrayList<Mascot>();
        private java.util.Set<Mascot> added = new java.util.LinkedHashSet<Mascot>();
        public void add(Mascot mascot) { added.add(mascot); }
        public void setExitOnLastRemoved(boolean value) { }
        public void parityStep() {
            if (Boolean.getBoolean("probe.draw")) parity.DraftParityHooks.random("builtin:behavior-select");
            if (System.getProperty("probe.error") != null) {
                parity.DraftParityHooks.Fault fault = null;
                if (Boolean.getBoolean("probe.typed")) {
                    java.util.Map<String,Object> lookup = new java.util.LinkedHashMap<String,Object>();
                    lookup.put("imageSet", "SearchedMember");
                    lookup.put("behavior", System.getProperty("probe.name"));
                    java.util.List<String> via = new java.util.ArrayList<String>();
                    via.add(System.getProperty("probe.via", "birth"));
                    fault = new parity.DraftParityHooks.Fault("behavior-missing", lookup, via);
                }
                parity.DraftParityHooks.fail(System.getProperty("probe.error"), fault);
            }
        }
    }""",
    "Mascot": """public class Mascot {
        private script.VariableMap variables; private java.awt.Point anchor = new java.awt.Point();
        private behavior.Behavior behavior; private String imageSet; private boolean right;
        public Mascot(String set) { imageSet = set; }
        public java.awt.Point getAnchor() { return anchor; } public void setAnchor(java.awt.Point value) { anchor = value; }
        public java.awt.Rectangle getBounds() { return new java.awt.Rectangle(); }
        public String getImageSet() { return imageSet; }
        public boolean isLookRight() { return right; } public void setLookRight(boolean value) { right = value; }
        public behavior.Behavior getBehavior() { return behavior; } public void setBehavior(behavior.Behavior value) { behavior = value; }
        public image.MascotImage getImage() { return null; } public int getTime() { return 0; }
        public java.util.Collection<String> getAffordances() { return java.util.Collections.emptyList(); }
        public boolean isDragging() { return false; } public boolean isHotspotClicked() { return false; }
    }""",
    "NativeFactory": """public abstract class NativeFactory {
        private static NativeFactory instance;
        public static NativeFactory getInstance() { return instance; }
        public abstract environment.Environment getEnvironment();
        public abstract image.NativeImage newNativeImage(java.awt.image.BufferedImage image, boolean mirror) throws exception.OutOfMemoryException;
        public abstract image.TranslucentWindow newTransparentWindow();
    }""",
    "Platform": "public enum Platform { x86_64 }",
    "action/ActionBase": "public class ActionBase { }",
    "action/ComplexAction": "public class ComplexAction extends ActionBase { }",
    "animation/Pose": "public class Pose { }",
    "behavior/Behavior": "public class Behavior { }",
    "config/Configuration": """public class Configuration {
        private java.util.Map<String,Object> actionBuilders = new java.util.LinkedHashMap<String,Object>();
        private java.util.Map<String,Object> behaviorBuilders = new java.util.LinkedHashMap<String,Object>();
        public behavior.Behavior buildBehavior(String name, Mascot mascot) {
            throw new IllegalStateException("PROBE_STARTER_BUILD_BEHAVIOR");
        }
    }""",
    "config/Entry": "public class Entry { }",
    "environment/Area": """public class Area {
        public void set(java.awt.Rectangle value) { } public void set(java.util.Map<String,java.awt.Rectangle> value) { }
        public void set(java.awt.Point value) { } public void setVisible(boolean value) { }
        public int getWidth() { return 0; } public int getHeight() { return 0; }
        public int getLeft() { return 0; } public int getTop() { return 0; }
    }""",
    "environment/Environment": "public class Environment { public Area screen = new Area(), complexScreen = new Area(), cursor = new Area(); }",
    "exception/OutOfMemoryException": "public class OutOfMemoryException extends Exception { private static final long serialVersionUID = 1L; }",
    "image/MascotImage": "public class MascotImage { public java.awt.Point getCenter() { return new java.awt.Point(); } }",
    "image/NativeImage": "public interface NativeImage { void dispose(); }",
    "image/TranslucentWindow": "public interface TranslucentWindow { java.awt.Component asComponent(); }",
    "script/Variable": "public class Variable { }",
    "script/Constant": "public class Constant extends Variable { }",
    "script/VariableMap": "public class VariableMap { public java.util.Map<String,Variable> getRawMap() { return new java.util.LinkedHashMap<String,Variable>(); } }",
}


def require(condition, witness):
    if not condition:
        raise AssertionError(witness)


def command(argv, folder):
    values = [str(value) for value in argv]
    started = time.time_ns()
    with subprocess.Popen(values, cwd=folder, text=True, encoding="utf-8", errors="replace",
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=os.environ.copy()) as process:
        try:
            stdout, stderr = process.communicate(timeout=60)
        except subprocess.TimeoutExpired:
            process.kill()
            process.communicate()
            raise
        result = subprocess.CompletedProcess(values, process.returncode, stdout, stderr)
        result.execution = {"argv": values, "pid": process.pid, "exit_code": process.returncode,
                            "exited": process.poll() is not None, "started_ns": started,
                            "finished_ns": time.time_ns(),
                            "stdout_sha256": hashlib.sha256(stdout.encode("utf-8")).hexdigest(),
                            "stderr_sha256": hashlib.sha256(stderr.encode("utf-8")).hexdigest()}
    PROCESS_RECORDS.append(result.execution)
    return result


def compile_case(root, args, mutation=None):
    source = root / "source"
    classes = root / "classes"
    source.mkdir()
    classes.mkdir()
    for relative in JAVA_FILES:
        text = (HERE / relative).read_text(encoding="utf-8")
        if mutation is not None and relative == mutation[0]:
            require(text.count(mutation[1]) == 1, "JAVA_MUTATION_PATTERN " + mutation[4])
            text = text.replace(mutation[1], mutation[2])
        (source / Path(relative).name).write_text(text, encoding="utf-8")
    for name, body in STUBS.items():
        suffix = name.rsplit("/", 1)[0] if "/" in name else ""
        target = source / (name + ".java")
        target.parent.mkdir(parents=True, exist_ok=True)
        # Short package aliases in these original synthetic stubs are resolved by imports, not by copied runtime code.
        imports = "".join("import com.group_finity.mascot." + item + ".*;\n" for item in
                          ("action", "animation", "behavior", "config", "environment", "exception", "image", "script", "parity"))
        aliases = ("config.", "behavior.", "environment.", "exception.", "image.", "script.", "parity.")
        for alias in aliases:
            body = re.sub(r"(?<![\w.])" + re.escape(alias), "com.group_finity.mascot." + alias, body)
        target.write_text("package com.group_finity.mascot" + ("." + suffix.replace("/", ".") if suffix else "")
                          + ";\nimport com.group_finity.mascot.*;\n" + imports + body, encoding="utf-8")
    control = source / "Utf8ResourceBundleControl.java"
    control.write_text("package com.joconner.i18n; public class Utf8ResourceBundleControl extends java.util.ResourceBundle.Control {"
                       " public Utf8ResourceBundleControl(boolean value) { } }", encoding="utf-8")
    started = time.time_ns()
    result = command([args.jdk_root / "bin/javac.exe", "-encoding", "UTF-8", "-source", "1.8", "-target", "1.8",
                      "-d", classes] + sorted(source.rglob("*.java")), root)
    require(result.returncode == 0, "JAVA_COMPILE_FAILED\n" + result.stdout + result.stderr)
    loaded = sorted(classes.rglob("*.class"))
    expected = {classes / PACKAGE / ("parity/" + name + ".class") for name in
                ("V1ScenarioRunner", "DraftParityHooks", "DraftParityHooks$Fault", "DraftTraceWriter", "Trace2ProtocolProbe")}
    require(expected.issubset(set(loaded)), "JAVA_CLASS_SET_WITNESS")
    require(all(path.is_file() and path.stat().st_mtime_ns >= started - 2_000_000_000 for path in loaded), "JAVA_REBUILD_WITNESS")
    (classes / "language.properties").write_text("probe=value\n", encoding="utf-8")
    return classes, {"fresh_classes": {str(path.relative_to(root)): {"sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                      "mtime_ns": path.stat().st_mtime_ns} for path in loaded}, "compile_started_ns": started,
                     "compile_process": result.execution,
                     "source_sha256": {name: hashlib.sha256((source / Path(name).name).read_bytes()).hexdigest() for name in JAVA_FILES},
                     "compiled_source_sha256": {str(path.relative_to(source)): hashlib.sha256(path.read_bytes()).hexdigest()
                                                for path in sorted(source.rglob("*.java"))}}


def run_trace(root, classes, args, name, schema=2, rng=None, ticks=2, properties=(), off=False, startup=False):
    case = root / name
    case.mkdir()
    scenario = {"schema": "shimeji-parity-scenario/1", "ticks": ticks, "rng": rng or {"kind": "none"},
                "screens": [{"x": 0, "y": 0, "w": 100, "h": 100}], "workArea": {"x": 0, "y": 0, "w": 100, "h": 100},
                "dpiScale": 1, "settings": {}, "mascots": []}
    if startup:
        scenario["mascots"] = [{"sid": "m1", "imageSet": "StartupSynthetic", "behavior": "Stand", "anchor": [0, 0], "lookRight": False}]
    (case / "header.json").write_text(json.dumps({"type": "header", "schema": "shimeji-parity-trace/" + str(schema)}), encoding="utf-8")
    (case / "scenario.json").write_text(json.dumps(scenario), encoding="utf-8")
    result = command([args.jdk_root / "bin/java.exe", "-Djava.awt.headless=true", "-Dshimeji.parity.enabled=true",
                      "-Dshimeji.parity.continueAfterError=true"] + list(properties)
                     + ["-cp", classes, "com.group_finity.mascot.parity.V1ScenarioRunner", case / "header.json",
                        case / "scenario.json", case / "trace.jsonl"] + (["off"] if off else []), case)
    lines = [json.loads(line) for line in (case / "trace.jsonl").read_text(encoding="utf-8").splitlines()]
    return result, lines


def check_case(root, classes, args, only=None):
    done = []

    def run(label, check):
        if only is None or only == label:
            check()
            done.append(label)

    def payload():
        result = command([args.jdk_root / "bin/java.exe", "-cp", classes, "com.group_finity.mascot.parity.Trace2ProtocolProbe"], root)
        if result.returncode != 0:
            for label in ("JAVA_T2_IMMUTABLE_CAPTURE", "JAVA_T2_IMMUTABLE_VIA", "JAVA_T2_IMMUTABLE_LOOKUP",
                          "JAVA_T2_IMMUTABLE_WRITER_VIA", "JAVA_T2_UNCLASSIFIED_FALLBACK"):
                if label in result.stderr:
                    raise AssertionError(label)
            raise AssertionError("JAVA_PAYLOAD_PROBE_FAILED\n" + result.stderr)
        require("TRACE2_PROTOCOL_PROBE_EXECUTED:" in result.stdout, "JAVA_PAYLOAD_PROBE_EXECUTION")
    run("payload", payload)

    def quiet():
        result, lines = run_trace(root, classes, args, "quiet")
        require(result.returncode == 0 and lines[0]["schema"] == "shimeji-parity-trace/2", "JAVA_T2_SCHEMA")
        require(lines[-1].get("tapeExhausted") is False, "JAVA_T2_FALSE_FLAG")
        require(lines[-1]["status"] == "complete", "JAVA_T2_QUIET_STATUS")
    run("quiet", quiet)

    def tape():
        result, lines = run_trace(root, classes, args, "tape", rng={"kind": "tape", "values": [0.25]}, properties=("-Dprobe.draw=true",))
        require(result.returncode == 0 and lines[-1]["status"] == "complete", "JAVA_T2_CONTINUES")
        require([row["value"] for row in lines if row.get("kind") == "rng-draw"] == [0.25, 0.5], "JAVA_T2_PAST_TAPE_VALUE")
        require(lines[-1].get("tapeExhausted") is True and lines[-1]["draws"] == 2, "JAVA_T2_TRUE_FLAG")
    run("tape", tape)

    def off():
        result, lines = run_trace(root, classes, args, "off", rng={"kind": "tape", "values": []}, properties=("-Dprobe.draw=true",), off=True)
        require(result.returncode == 0 and lines[-1].get("tapeExhausted") is True, "JAVA_T2_OFF_FLAG")
        require(lines[-1].get("unobservedDraws") == 2 and lines[-1].get("unobservedEvents") == 2
                and lines[-1]["draws"] == 0 and lines[-1]["events"] == 0, "JAVA_T2_OFF_COUNTERS")
    run("off", off)

    def unknown():
        result, lines = run_trace(root, classes, args, "unknown", properties=("-Dprobe.error=an original diagnostic",))
        errors = [row for row in lines if row.get("kind") == "error"]
        require(result.returncode == 0 and len(errors) == 2, "JAVA_T2_ERROR_PRESENCE")
        require(all(row.get("error") == {"code": "unclassified", "lookup": None, "via": []} for row in errors), "JAVA_T2_UNCLASSIFIED")
    run("unknown", unknown)

    def typed():
        for index, name in enumerate((None, "", " ")):
            props = ["-Dprobe.error=another original diagnostic", "-Dprobe.typed=true", "-Dprobe.via=transform"]
            if name is not None:
                props.append("-Dprobe.name=" + name)
            result, lines = run_trace(root, classes, args, "typed-" + str(index), properties=props)
            errors = [row for row in lines if row.get("kind") == "error"]
            expected = {"code": "behavior-missing", "lookup": {"imageSet": "SearchedMember", "behavior": name}, "via": ["transform"]}
            require(result.returncode == 0 and len(errors) == 2 and all(row.get("error") == expected for row in errors), "JAVA_T2_CARRIED_FAULT")
    run("typed", typed)

    def v1():
        result, lines = run_trace(root, classes, args, "v1", schema=1, properties=("-Dprobe.error=old diagnostic", "-Dprobe.typed=true"))
        errors = [row for row in lines if row.get("kind") == "error"]
        require(result.returncode == 0 and len(errors) == 2
                and all(row.get("message") == "old diagnostic" and "error" not in row for row in errors), "JAVA_T1_EVENT_FREEZE")
        require("tapeExhausted" not in lines[-1], "JAVA_T1_FALSE_FLAG_FREEZE")
    run("v1", v1)

    def startup():
        result, lines = run_trace(root, classes, args, "startup", startup=True)
        require("PROBE_STARTER_BUILD_BEHAVIOR" in result.stderr and "V1_CONFIGURATION_REFUSED" not in result.stderr,
                "JAVA_T2_STARTUP_PATH")
        require(result.returncode == 1 and lines[-1]["status"] == "error" and lines[-1]["states"] == 0
                and not any(row.get("type") == "state" for row in lines), "JAVA_T2_STARTUP_NO_SNAPSHOT")
        require(lines[-1].get("tapeExhausted") is False, "JAVA_T2_FAILED_FLAG")
    run("startup", startup)
    return done


MUTATIONS = (
    ("V1ScenarioRunner.java", 'if (trace2) record.put("error",', 'if (false) record.put("error",', "unknown", "JAVA_T2_UNCLASSIFIED"),
    ("V1ScenarioRunner.java", 'if (trace2) record.put("error",', 'if (true) record.put("error",', "v1", "JAVA_T1_EVENT_FREEZE"),
    ("V1ScenarioRunner.java", 'runner.trace2 || runner.tapeExhausted', 'runner.tapeExhausted', "quiet", "JAVA_T2_FALSE_FLAG"),
    ("V1ScenarioRunner.java", 'runner.trace2 || runner.tapeExhausted', '(runner.trace2 || runner.tapeExhausted) && runner.observing', "off", "JAVA_T2_OFF_FLAG"),
    ("V1ScenarioRunner.java", 'else { tapeExhausted = true; value = 0.5; }', 'else { throw new IllegalStateException("probe exhausted"); }', "tape", "JAVA_T2_CONTINUES"),
    ("V1ScenarioRunner.java", 'else { tapeExhausted = true; value = 0.5; }', 'else { tapeExhausted = true; value = 0.7; }', "tape", "JAVA_T2_PAST_TAPE_VALUE"),
    ("V1ScenarioRunner.java", 'footer.put("tapeExhausted", runner.tapeExhausted)', 'footer.put("tapeExhausted", false)', "tape", "JAVA_T2_TRUE_FLAG"),
    ("draft/DraftParityHooks.java", 'observe("error", fault, message)', 'observe("error", null, message)', "typed", "JAVA_T2_CARRIED_FAULT"),
    ("draft/DraftParityHooks.java", 'this.lookup = lookup == null ? null : new LinkedHashMap<String, Object>(lookup)', 'this.lookup = lookup', "payload", "JAVA_T2_IMMUTABLE_CAPTURE"),
    ("draft/DraftParityHooks.java", 'this.via = new ArrayList<String>(via)', 'this.via = via', "payload", "JAVA_T2_IMMUTABLE_VIA"),
    ("draft/DraftParityHooks.java", 'result.put("lookup", lookup == null ? null : new LinkedHashMap<String, Object>(lookup))', 'result.put("lookup", lookup)', "payload", "JAVA_T2_IMMUTABLE_LOOKUP"),
    ("draft/DraftParityHooks.java", 'result.put("via", new ArrayList<String>(via))', 'result.put("via", via)', "payload", "JAVA_T2_IMMUTABLE_WRITER_VIA"),
    ("V1ScenarioRunner.java", 'runner.flush();\n                Map<String, Object> footer',
     'runner.flush();\n                if ("error".equals(status)) { Map<String,Object> fabricated = new LinkedHashMap<String,Object>(); '
     'fabricated.put("type", "state"); fabricated.put("tick", 0); fabricated.put("sid", "m1"); '
     'writer.writeRecord(fabricated); runner.states++; }\n                Map<String, Object> footer',
     "startup", "JAVA_T2_STARTUP_NO_SNAPSHOT"),
    ("V1ScenarioRunner.java", 'if (trace2) record.put("error", DraftParityHooks.faultRecord(subject));\n            emit(record);',
     'if (trace2) record.put("error", DraftParityHooks.faultRecord(subject));\n            if (trace2) emit(record);',
     "v1", "JAVA_T1_EVENT_FREEZE"),
    ("V1ScenarioRunner.java", 'record.put("message", detail == null ? null : detail.toString());',
     'record.put("message", "changed diagnostic");', "v1", "JAVA_T1_EVENT_FREEZE"),
)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jdk-root", type=Path, required=True)
    parser.add_argument("--mutate", action="store_true")
    parser.add_argument("--result", type=Path)
    args = parser.parse_args()
    hashes = {name: hashlib.sha256((HERE / name).read_bytes()).hexdigest() for name in JAVA_FILES}
    harness_sha256 = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    rows = []
    with tempfile.TemporaryDirectory(prefix="oracle-trace2-java-", dir=os.environ.get("TEMP")) as folder:
        root = Path(folder)
        version = command([args.jdk_root / "bin/java.exe", "-version"], root)
        require(version.returncode == 0 and 'version "1.8.' in version.stderr, "JAVA8_RUNTIME_WITNESS")
        baseline = root / "baseline"
        baseline.mkdir()
        start = len(PROCESS_RECORDS)
        classes, witness = compile_case(baseline, args)
        checks = check_case(baseline, classes, args)
        baseline_classes = {name: data["sha256"] for name, data in witness["fresh_classes"].items()}
        rows.append({"case": "control", "status": "PASS", "checks": checks, "rebuild": witness,
                     "processes": PROCESS_RECORDS[start:]})
        print("TRACE2_JAVA_CONTROL_PASS: " + ", ".join(checks), flush=True)
        if args.mutate:
            for index, mutation in enumerate(MUTATIONS):
                case = root / ("mutant-" + str(index))
                case.mkdir()
                start = len(PROCESS_RECORDS)
                classes, witness = compile_case(case, args, mutation)
                mutant_classes = {name: data["sha256"] for name, data in witness["fresh_classes"].items()}
                changed_classes = sorted(name for name in baseline_classes.keys() | mutant_classes.keys()
                                         if baseline_classes.get(name) != mutant_classes.get(name))
                require(changed_classes, "JAVA_MUTATION_BINARY_UNCHANGED " + mutation[4])
                try:
                    check_case(case, classes, args, mutation[3])
                except AssertionError as failure:
                    require(str(failure) == mutation[4], "JAVA_MUTATION_WRONG_WITNESS " + str(failure))
                else:
                    raise AssertionError("JAVA_MUTATION_SURVIVED " + mutation[4])
                rows.append({"case": mutation[4], "status": "FIRED", "mutation_index": index,
                             "changed_classes": changed_classes, "rebuild": witness,
                             "processes": PROCESS_RECORDS[start:]})
                print("FIRED: " + mutation[4] + " (fresh compiled class witnessed)", flush=True)
    require(all(hashlib.sha256((HERE / name).read_bytes()).hexdigest() == digest for name, digest in hashes.items()), "JAVA_SOURCE_RESTORE_WITNESS")
    require(hashlib.sha256(Path(__file__).read_bytes()).hexdigest() == harness_sha256, "JAVA_HARNESS_RESTORE_WITNESS")
    result = {"status": "pass", "scope": "synthetic protocol API stubs; not a pinned-runtime parity run or typed-site attribution proof",
              "rows": rows, "harness_sha256": harness_sha256, "source_sha256": hashes,
              "java_version": {"stdout": version.stdout, "stderr": version.stderr, "process": version.execution},
              "tracked_source_unchanged": True, "scratch_removed": not root.exists(), "scratch_path": str(root)}
    if args.result:
        args.result.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": "pass", "controls": len(checks), "mutations": len(rows) - 1,
                      "scratch_removed": not root.exists(), "tracked_source_unchanged": True}), flush=True)


if __name__ == "__main__":
    main()
