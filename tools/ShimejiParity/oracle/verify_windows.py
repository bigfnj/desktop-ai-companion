"""Compile original adapter tests in private folders; never create a native window or send input."""

import argparse
import importlib.util
import json
import os
from pathlib import Path
import time
import uuid


HERE = Path(__file__).resolve().parent


def mutation_cases():
    return (
        ("move-null-noop", "        if (!usable()) return;\n        Rectangle before", "        if (false) return;\n        Rectangle before", "null-window-noops"),
        ("cached-move-delayed", "        desktopWindow = new Rectangle(point.x, point.y, window.getWidth(), window.getHeight());",
         "        desktopWindow = new Rectangle(point.x, point.y, window.getWidth(), window.getHeight());\n        window.set(desktopWindow);", "cached-geometry-delayed"),
        ("every-tick-area-set", "        window.set(found ? desktopWindow : new Rectangle());",
         "        if (state.windowChanged) window.set(found ? desktopWindow : new Rectangle());", "area-refresh-clears-deltas"),
        ("move-live-before", "        Rectangle before = new Rectangle(desktopWindow);", "        Rectangle before = window.toRectangle();", "move-reacquires-desktop-state"),
        ("cached-minimize-delayed", "        minimized = true;", "        minimized = true;\n        window.setVisible(false);", "minimize-cache-delayed"),
        ("offscreen-reacquire", "return shown && !minimized && desktopWindow.intersects(desktopScreen);",
         "return shown && !minimized;", "offscreen-reacquire-noops"),
        ("absent-zero-rectangle", "window.set(found ? desktopWindow : new Rectangle());",
         "window.set(found ? desktopWindow : window.toRectangle());", "absent-window-zero-rect"),
        ("restore-minimized", "    public void restoreIE() {", "    public void restoreIE() {\n        minimized = false;", "restore-does-not-unminimize"),
        ("restore-invented-event", "    public void restoreIE() {",
         '    public void restoreIE() {\n        DraftParityHooks.observe("window-restore", null, null);', "restore-has-no-invented-event"),
        ("restore-work-offset", "new Rectangle(workArea.getLeft() + 25, workArea.getTop() + 25,",
         "new Rectangle(workArea.getLeft() + 26, workArea.getTop() + 25,", "restore-offscreen-to-work-area-offset"),
        ("restore-move-cached-size", "new Rectangle(point.x, point.y, window.getWidth(), window.getHeight());",
         "new Rectangle(point.x, point.y, desktopWindow.width, desktopWindow.height);", "restore-then-move-uses-cached-size"),
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cache-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--jdk-root", type=Path, required=True)
    parser.add_argument("--mutate", action="store_true")
    parser.add_argument("--only", choices=[case[0] for case in mutation_cases()])
    args = parser.parse_args()
    if args.only and not args.mutate:
        parser.error("--only requires --mutate")
    spec = importlib.util.spec_from_file_location("oracle_windows_tools", HERE / "oracle.py")
    oracle = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(oracle)
    oracle.validate_storage_roots(args)
    source = oracle.check_source(args)
    draft = json.loads((args.cache_root / "latest-draft.json").read_text(encoding="utf-8"))
    artifact = Path(draft["artifact"])
    oracle.verify(artifact.read_bytes(), draft["artifact_sha256"], "window-adapter-base-runtime")
    root = args.output_root / ("window-adapter-" + uuid.uuid4().hex[:8])
    root.mkdir(parents=True)
    os.environ.update({"DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT": "1", "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "0",
                       "DESKTOP_AI_COMPANION_DATA_ROOT": str(root / "data"), "TEMP": str(root / "temp"), "TMP": str(root / "temp")})
    for directory in (root / "data", root / "temp"):
        directory.mkdir()
    sources = {name: (HERE / relative).read_bytes() for name, relative in (
        ("DraftParityHooks.java", "draft/DraftParityHooks.java"),
        ("ScenarioEnvironment.java", "draft/ScenarioEnvironment.java"),
        ("ScenarioEnvironmentProbe.java", "ScenarioEnvironmentProbe.java"),
    )}
    adapter = sources["ScenarioEnvironment.java"].decode("utf-8").replace("\r\n", "\n")
    classpath = str(artifact) + os.pathsep + str(source / "lib" / "*")
    runs = [("baseline", adapter, None)]
    if args.mutate:
        for name, old, new, witness in mutation_cases():
            if args.only and name != args.only:
                continue
            if adapter.count(old) != 1:
                raise oracle.Refusal("WINDOW_MUTATION_PATTERN_REFUSAL " + name)
            runs.append((name, adapter.replace(old, new), witness))
    results = []
    baseline_hash = None
    for name, java, witness in runs:
        run = root / name
        inputs = run / "source"
        classes = run / "classes"
        inputs.mkdir(parents=True)
        classes.mkdir()
        for filename, payload in sources.items():
            (inputs / filename).write_bytes(java.encode("utf-8") if filename == "ScenarioEnvironment.java" else payload)
        source_written = (inputs / "ScenarioEnvironment.java").stat().st_mtime_ns
        started = time.time_ns()
        compiler = oracle.command([args.jdk_root / "bin" / "javac.exe", "-encoding", "UTF-8", "-source", "1.6", "-target", "1.6", "-g",
                                   "-classpath", classpath, "-d", classes] + [inputs / filename for filename in sources], run, timeout=60)
        oracle.write_json(run / "compile.json", compiler)
        oracle.require_command(compiler, "WINDOW_PROBE_BUILD_FAILED " + str(run / "compile.json"))
        built = classes / "com/group_finity/mascot/parity/ScenarioEnvironment.class"
        if not built.is_file() or built.stat().st_mtime_ns < source_written:
            raise oracle.Refusal("WINDOW_PROBE_BUILD_WITNESS_MISSING " + name)
        digest = oracle.sha(built.read_bytes())
        if witness is None:
            baseline_hash = digest
        elif digest == baseline_hash:
            raise oracle.Refusal("WINDOW_MUTATION_ARTIFACT_UNCHANGED " + name)
        launch = oracle.command([args.jdk_root / "bin" / "java.exe", "-Djava.awt.headless=true", "-Dfile.encoding=UTF-8",
                                 "-cp", str(classes) + os.pathsep + classpath,
                                 "com.group_finity.mascot.parity.ScenarioEnvironmentProbe"], run, timeout=30)
        oracle.write_json(run / "launch.json", launch)
        if launch["stdout"].splitlines().count("SCENARIO_ENVIRONMENT_PROBE_EXECUTED") != 1 or launch["timed_out"]:
            raise oracle.Refusal("WINDOW_PROBE_EXECUTION_WITNESS_MISSING " + name)
        if witness is None:
            oracle.require_command(launch, "WINDOW_PROBE_FAILED " + str(run / "launch.json"))
            result = json.loads(launch["stdout"].splitlines()[-1])
            if result.get("status") != "pass" or result.get("nativeOperations") != 0:
                raise oracle.Refusal("WINDOW_PROBE_RESULT_WITNESS_MISSING")
            status = "pass"
        else:
            expected = "ScenarioEnvironmentProbe:" + witness
            if launch["exit_code"] != 1 or launch["stderr"].count(expected) != 1:
                raise oracle.Refusal("WINDOW_MUTATION_DID_NOT_FIRE " + name + ": " + str(run / "launch.json"))
            status = "FIRED"
        results.append({"case": name, "status": status, "witness": witness, "classSha256": digest,
                        "classWriteTimeNs": built.stat().st_mtime_ns, "sourceWriteTimeNs": source_written,
                        "compileStartedNs": started, "processId": launch["pid"], "processExited": launch["process_exited"]})
    for name, relative in (("DraftParityHooks.java", "draft/DraftParityHooks.java"),
                           ("ScenarioEnvironment.java", "draft/ScenarioEnvironment.java"),
                           ("ScenarioEnvironmentProbe.java", "ScenarioEnvironmentProbe.java")):
        if (HERE / relative).read_bytes() != sources[name]:
            raise oracle.Refusal("WINDOW_PROBE_TRACKED_SOURCE_CHANGED " + name)
    oracle.check_source(args)
    report = {"status": "pass", "nativeOperations": 0, "evidence": str(root), "results": results,
              "baseArtifactSha256": draft["artifact_sha256"], "sourceZipSha256": oracle.SOURCE_SHA256,
              "adapterSha256": oracle.sha(sources["ScenarioEnvironment.java"]), "trackedSourceUnchanged": True,
              "assumption": "Failed native lookup leaves freshly allocated RECTs zeroed; native probing was not run."}
    oracle.write_json(root / "results.json", report)
    oracle.write_json(args.output_root / "window-adapter-latest.json", report)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
