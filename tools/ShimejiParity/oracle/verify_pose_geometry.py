"""Private headless proof against unchanged Mascot getters/apply, with named mutants."""

import argparse
import importlib.util
import json
import os
from pathlib import Path
import uuid

HERE = Path(__file__).resolve().parent


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cache-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--jdk-root", type=Path, required=True)
    parser.add_argument("--mutate", action="store_true")
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location("oracle_pose_tools", HERE / "oracle.py")
    oracle = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(oracle)
    oracle.validate_storage_roots(args)
    source = oracle.check_source(args)
    build = json.loads((args.cache_root / "latest-build.json").read_text(encoding="utf-8"))
    artifact = Path(build["artifact"])
    oracle.verify(artifact.read_bytes(), build["artifact_sha256"], "unchanged-pose-runtime")
    root = args.output_root / ("pose-geometry-" + uuid.uuid4().hex[:8])
    root.mkdir(parents=True)
    os.environ.update({"DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT": "1", "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "0",
                       "DESKTOP_AI_COMPANION_DATA_ROOT": str(root / "data"), "TEMP": str(root / "temp"), "TMP": str(root / "temp")})
    for directory in (root / "data", root / "temp"):
        directory.mkdir()
    tracked = {"geometry": HERE / "draft" / "DraftPoseGeometry.java", "formatter": HERE / "FeasibilityProbe.java",
               "transport": HERE / "draft" / "DraftTraceWriter.java", "probe": HERE / "PoseGeometryProbe.java"}
    frozen = {name: path.read_bytes() for name, path in tracked.items()}
    geometry = frozen["geometry"].decode("utf-8").replace("\r\n", "\n")
    driver = frozen["formatter"].decode("utf-8").replace("\r\n", "\n")
    # Private dependency seam: avoids Main's static JFrame; the unchanged apply()
    # only asks whether sounds are enabled, which is false in this directed proof.
    main_seam = """package com.group_finity.mascot;
public final class Main {
    private static final Main TEST = new Main();
    public static Main getInstance() { return TEST; }
    public boolean getMascotBooleanSetting(String setting, Mascot mascot) { return false; }
}
"""
    mutations = [
        ("hidden-anchor-recomputed", "Rectangle bounds = appliedPose ? mascot.getBounds() : null;",
         "Rectangle bounds = appliedPose ? (image == null ? new Rectangle(mascot.getAnchor().x, mascot.getAnchor().y, 0, 0) : mascot.getBounds()) : null;",
         "hidden-origin-frozen"),
        ("hidden-anchor-invented", "Point center = !appliedPose || image == null ? null : image.getCenter();",
         "Point center = image == null ? (appliedPose ? new Point(0, 0) : null) : image.getCenter();", "hidden-anchor-null"),
    ]
    runs = [("baseline", geometry, driver, None)]
    if args.mutate:
        for name, old, new, witness in mutations:
            if geometry.count(old) != 1:
                raise oracle.Refusal("POSE_MUTATION_PATTERN_REFUSAL " + name)
            runs.append((name, geometry.replace(old, new), driver, witness))
        old = 'geometry.get("origin")'
        if driver.count(old) != 1:
            raise oracle.Refusal("POSE_WRITER_MUTATION_PATTERN_REFUSAL")
        runs.append(("writer-origin-disconnected", geometry, driver.replace(old, "null"), "feasibility-writer-hidden-geometry"))
        old = 'geometry.get("poseAnchor")'
        if driver.count(old) != 1:
            raise oracle.Refusal("POSE_ANCHOR_WRITER_MUTATION_PATTERN_REFUSAL")
        runs.append(("writer-pose-anchor-disconnected", geometry, driver.replace(old, "null"), "feasibility-writer-reappeared-geometry"))
    results = []
    baseline_sha = None
    baseline_driver_sha = None
    for name, edited, edited_driver, witness in runs:
        run = root / name
        inputs = run / "source"
        classes = run / "classes"
        inputs.mkdir(parents=True)
        classes.mkdir()
        payloads = {"Main.java": main_seam, "DraftPoseGeometry.java": edited,
                    "DraftTraceWriter.java": frozen["transport"].decode("utf-8"),
                    "PoseGeometryProbe.java": frozen["probe"].decode("utf-8")}
        for filename, text in payloads.items():
            (inputs / filename).write_text(text, encoding="utf-8")
        (inputs / "FeasibilityProbe.java").write_text(edited_driver, encoding="utf-8")
        written = (inputs / "DraftPoseGeometry.java").stat().st_mtime_ns
        driver_written = (inputs / "FeasibilityProbe.java").stat().st_mtime_ns
        classpath = str(artifact) + os.pathsep + str(source / "lib" / "*")
        driver_compile = oracle.command([args.jdk_root / "bin" / "javac.exe", "-J-XX:-UsePerfData", "-encoding", "UTF-8",
                                        "-cp", classpath, "-d", classes, inputs / "FeasibilityProbe.java", inputs / "DraftPoseGeometry.java"], run, timeout=60)
        oracle.write_json(run / "driver-compile.json", driver_compile)
        oracle.require_command(driver_compile, "POSE_DRIVER_COMPILE_FAILED")
        compile_result = oracle.command([args.jdk_root / "bin" / "javac.exe", "-J-XX:-UsePerfData", "-encoding", "UTF-8",
                                        "-cp", artifact, "-d", classes] + [inputs / name for name in payloads], run, timeout=60)
        oracle.write_json(run / "compile.json", compile_result)
        oracle.require_command(compile_result, "POSE_PROBE_COMPILE_FAILED")
        built = classes / "com/group_finity/mascot/parity/DraftPoseGeometry.class"
        digest = oracle.sha(built.read_bytes())
        built_driver = classes / "com/group_finity/mascot/oracle/FeasibilityProbe.class"
        driver_digest = oracle.sha(built_driver.read_bytes())
        if (built.stat().st_mtime_ns < written or built_driver.stat().st_mtime_ns < driver_written
                or (witness and digest == baseline_sha and driver_digest == baseline_driver_sha)):
            raise oracle.Refusal("POSE_BUILD_WITNESS_MISSING " + name)
        if witness is None:
            baseline_sha = digest
            baseline_driver_sha = driver_digest
        launched = oracle.command([args.jdk_root / "bin" / "java.exe", "-XX:-UsePerfData", "-Djava.awt.headless=true",
                                  "-cp", str(classes) + os.pathsep + classpath,
                                  "com.group_finity.mascot.parity.PoseGeometryProbe"], run, timeout=30)
        oracle.write_json(run / "launch.json", launched)
        if launched["stdout"].splitlines().count("POSE_GEOMETRY_PROBE_EXECUTED") != 1 or launched["timed_out"]:
            raise oracle.Refusal("POSE_EXECUTION_WITNESS_MISSING " + name)
        if witness is None:
            oracle.require_command(launched, "POSE_PROBE_FAILED")
        elif launched["exit_code"] != 1 or launched["stderr"].count("PoseGeometryProbe:" + witness) != 1:
            raise oracle.Refusal("POSE_MUTATION_DID_NOT_FIRE " + name)
        results.append({"case": name, "status": "FIRED" if witness else "pass", "witness": witness,
                        "classSha256": digest, "classWriteTimeNs": built.stat().st_mtime_ns,
                        "driverClassSha256": driver_digest, "driverClassWriteTimeNs": built_driver.stat().st_mtime_ns,
                        "driverSourceWriteTimeNs": driver_written,
                        "sourceWriteTimeNs": written, "processId": launched["pid"], "processExited": launched["process_exited"]})
    for name, path in tracked.items():
        if path.read_bytes() != frozen[name]:
            raise oracle.Refusal("POSE_TRACKED_SOURCE_CHANGED " + name)
    report = {"schema": "oracle-pose-geometry-proof/1", "status": "pass", "nativeOperations": 0,
              "artifactSha256": build["artifact_sha256"], "helperSha256": oracle.sha(geometry.encode("utf-8")),
              "sourceHashes": {name: oracle.sha(data) for name, data in frozen.items()}, "trackedSourcesUnchanged": True,
              "dependencySeam": "Main settings only; Mascot allocated without constructor and given an in-memory JPanel window; Mascot getters/apply unchanged",
              "results": results, "evidence": str(root)}
    oracle.write_json(root / "results.json", report)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
