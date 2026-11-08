"""Reproduce the upstream build without storing upstream sources in the checkout."""

import argparse
import datetime
import hashlib
import io
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
from types import FunctionType, SimpleNamespace
import subprocess
import sys
import tempfile
import struct
import urllib.request
import uuid
import zipfile
import zlib
import xml.etree.ElementTree as ET


SOURCE_URL = "https://kilkakon.com/shimeji/shimejieesrc.zip"
DISTRIBUTION_URL = "https://kilkakon.com/shimeji/shimejiee.zip"
DISTRIBUTION_SHA256 = "fee19b4003d718fab7ddd3ac6955bd4c1228845f94f8a1f9d72da12571a2a3d6"
DISTRIBUTION_JAR_SHA256 = "4e68d8dbe67ed79400056f9e52fb7d619c48d566e5a2104cc1f64c8c1276802c"
EXPECTED_CLASS_COUNT = 298
MAX_EXPANDED_BYTES = 256 * 1024 * 1024
SOURCE_ROOTS = ("src", "src_win", "src_generic", "src_virtual")
HERE = Path(__file__).resolve().parent
# The comparer and producer use one data-only pin, without importing each other's executable tooling.
SOURCE_SHA256 = json.loads((HERE / "source-pin.json").read_text(encoding="utf-8"))["sourceZipSha256"]


class Refusal(RuntimeError):
    pass


def repository_root(here=HERE):
    for parent in (here, *here.parents):
        if (parent / ".git").exists():
            return parent.resolve()
    return here.resolve()


def validate_storage_roots(args, repository=None):
    repository = repository_root() if repository is None else Path(repository).resolve()
    for label, attribute in (
            ("cache-root", "cache_root"), ("output-root", "output_root"),
            ("data-root", "data_root"),
            ("temp-root", "temp_root"),
            ("shared-root", "shared_root")):
        supplied = getattr(args, attribute, None)
        if supplied is None:
            continue
        # Check both spellings: an in-checkout junction must not disguise a private destination.
        for target in (Path(os.path.abspath(supplied)), Path(supplied).resolve()):
            if target == repository or repository in target.parents:
                raise Refusal("REPOSITORY_STORAGE_REFUSAL " + label + " must be outside the tool's repository")
    return repository


def sha(data):
    return hashlib.sha256(data).hexdigest()


def verify(data, expected, label):
    actual = sha(data)
    if actual != expected:
        raise Refusal(f"CHECKSUM_REFUSAL {label}: expected {expected}, actual {actual}")
    return actual


def protected_member(name):
    parts = PurePosixPath(name.replace("\\", "/")).parts
    for index, component in enumerate(parts):
        leaf = component.lower()
        if leaf.endswith(".env"):
            return "W01"
        if leaf.startswith(".env."):
            return "W02"
        if leaf == ".npmrc":
            return "W03"
        if index and parts[index - 1].lower() == ".git" and leaf == "credentials":
            return "W04"
        for suffix, pattern in ((".pem", "W05"), (".key", "W06"), (".p12", "W07"),
                                (".pfx", "W08"), (".keystore", "W09")):
            if leaf.endswith(suffix):
                return pattern
        if leaf.startswith("privatekey"):
            return "W10"
    return None


def member_target(root, info):
    name = info.filename.replace("\\", "/")
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts or any(":" in p for p in path.parts):
        raise Refusal(f"ARCHIVE_PATH_REFUSAL {info.filename}")
    pattern = protected_member(name)
    if pattern:
        raise Refusal(f"PROTECTED_PATH_REFUSAL {pattern}: {info.filename}")
    if (info.external_attr >> 16) & 0o170000 == 0o120000:
        raise Refusal(f"ARCHIVE_LINK_REFUSAL {info.filename}")
    target = root.joinpath(*path.parts).resolve()
    if root.resolve() not in target.parents and target != root.resolve():
        raise Refusal(f"ARCHIVE_PATH_REFUSAL {info.filename}")
    return target


def archive_targets(root, infos):
    targets = [(info, member_target(root, info)) for info in infos]
    if sum(info.file_size for info, _ in targets) > MAX_EXPANDED_BYTES:
        raise Refusal("ARCHIVE_SIZE_REFUSAL expanded archive exceeds 256 MiB")
    return targets


def extract(data, root):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        # Validate all names before reading any payload, including synthetic fixtures.
        targets = archive_targets(root, archive.infolist())
        inventory = {}
        for info, target in targets:
            if info.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                payload = archive.read(info)
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(payload)
                inventory[info.filename] = sha(payload)
    return inventory


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def fetch_bytes(url):
    with urllib.request.urlopen(url, timeout=45) as response:
        data = response.read(64 * 1024 * 1024 + 1)
        if len(data) > 64 * 1024 * 1024:
            raise Refusal("DOWNLOAD_SIZE_REFUSAL exceeds 64 MiB")
        return data, dict(response.headers)


def cache_source(args):
    validate_storage_roots(args)
    root = args.cache_root
    archive_path = root / "shimejieesrc.zip"
    if archive_path.exists():
        data = archive_path.read_bytes()
        headers = {}
    else:
        data, headers = fetch_bytes(SOURCE_URL)
    digest = verify(data, SOURCE_SHA256, "official-source-zip")
    root.mkdir(parents=True, exist_ok=True)
    if not archive_path.exists():
        archive_path.write_bytes(data)
    source = root / ("source-" + digest[:12])
    inventory_path = root / "source-inventory.json"
    if not inventory_path.exists():
        if source.exists():
            raise Refusal("SOURCE_STATE_REFUSAL source exists without its inventory")
        inventory = extract(data, source)
        write_json(inventory_path, {"sha256": digest, "source": str(source), "files": inventory,
                                    "url": SOURCE_URL, "headers": headers})
    check_source(args)
    return source


def check_source(args):
    record = json.loads((args.cache_root / "source-inventory.json").read_text(encoding="utf-8"))
    root = args.cache_root / ("source-" + SOURCE_SHA256[:12])
    if Path(record["source"]).resolve() != root.resolve() or record["sha256"] != SOURCE_SHA256:
        raise Refusal("SOURCE_STATE_REFUSAL unexpected extraction root or archive identity")
    data = (args.cache_root / "shimejieesrc.zip").read_bytes()
    verify(data, SOURCE_SHA256, "cached-source-zip")
    check_extracted(data, root, record["files"])
    return root


def check_extracted(data, root, recorded_files):
    expected_files = {}
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        targets = archive_targets(root, archive.infolist())
        for info, target in targets:
            if info.is_dir():
                continue
            expected = sha(archive.read(info))
            expected_files[info.filename] = expected
            if not target.is_file() or sha(target.read_bytes()) != expected:
                raise Refusal(f"SOURCE_CHANGED {info.filename}")
    if recorded_files != expected_files:
        raise Refusal("SOURCE_INVENTORY_CHANGED")
    actual_files = set()
    for directory, dirs, names in os.walk(root, followlinks=False):
        for leaf in dirs + names:
            item = Path(directory) / leaf
            relative = item.relative_to(root).as_posix()
            if protected_member(relative) or item.is_symlink():
                raise Refusal("EXTRACTED_PATH_REFUSAL " + relative)
            if item.is_file():
                actual_files.add(relative)
    if actual_files != set(expected_files):
        raise Refusal("EXTRACTED_INVENTORY_CHANGED")


def command(argv, cwd, timeout=120):
    process = subprocess.Popen([str(a) for a in argv], cwd=str(cwd), stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               encoding="utf-8", errors="replace",
                               creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    timed_out = False
    try:
        stdout, stderr = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        timed_out = True
        process.kill()  # Only the direct process created above is owned by this invocation.
        stdout, stderr = process.communicate()
    return {"argv": [str(a) for a in argv], "cwd": str(cwd), "exit_code": process.returncode,
            "pid": process.pid, "process_exited": process.poll() is not None, "timed_out": timed_out,
            "stdout": stdout, "stderr": stderr}


def require_command(record, label):
    if record["exit_code"]:
        raise Refusal(f"{label}: exit {record['exit_code']}; see private evidence")


def jdk(args):
    java = args.jdk_root / "bin" / "java.exe"
    record = command([java, "-version"], HERE)
    require_command(record, "JDK_VERSION")
    if not re.search(r'version "1\.8\.', record["stderr"]):
        raise Refusal("JDK_VERSION_REFUSAL oracle baseline requires JDK 8")
    return record


def build(args):
    source = cache_source(args)
    version = jdk(args)
    build_id = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:6]
    root = args.cache_root / "builds" / build_id
    classes = root / "classes"
    classes.mkdir(parents=True)
    inventory = json.loads((args.cache_root / "source-inventory.json").read_text(encoding="utf-8"))["files"]
    files = sorted(source / name for name in inventory
                   if PurePosixPath(name).parts[0] in SOURCE_ROOTS and name.endswith(".java"))
    for path in files:
        path.read_bytes().decode("utf-8-sig")
    response = root / "sources.args"
    response.write_text("\n".join('"' + str(p).replace("\\", "/") + '"' for p in files) + "\n", encoding="utf-8")
    argv = [args.jdk_root / "bin" / "javac.exe", "-encoding", "UTF-8", "-source", "1.6", "-target", "1.6",
            "-g", "-classpath", str(source / "lib" / "*"), "-d", classes, "@" + str(response)]
    compiler = command(argv, source)
    write_json(root / "compile.json", compiler)
    require_command(compiler, "UNCHANGED_BUILD_FAILED " + str(root / "compile.json"))
    artifact = root / "Shimeji-rebuilt.jar"
    jar = command([args.jdk_root / "bin" / "jar.exe", "cfm", artifact, source / "MANIFEST.MF", "-C", classes, "."], source)
    write_json(root / "jar.json", jar)
    require_command(jar, "JAR_FAILED")
    check_source(args)
    state = {"build_id": build_id, "source": str(source), "source_sha256": SOURCE_SHA256,
             "artifact": str(artifact), "artifact_sha256": sha(artifact.read_bytes()), "jdk": version,
             "compiler": compiler, "source_count": len(files), "classes": str(classes),
             "source_unchanged": True}
    write_json(root / "build.json", state)
    write_json(args.cache_root / "latest-build.json", state)
    return state


def verify_distribution(data):
    return verify(data, DISTRIBUTION_SHA256, "official-distribution-zip")


def verify_distribution_jar(data):
    return verify(data, DISTRIBUTION_JAR_SHA256, "official-distribution-jar")


def distribution(args):
    validate_storage_roots(args)
    path = args.cache_root / "shimejiee.zip"
    if path.exists():
        data = path.read_bytes()
        headers = {}
    else:
        data, headers = fetch_bytes(DISTRIBUTION_URL)
    digest = verify_distribution(data)
    if not path.exists():
        args.cache_root.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    root = args.cache_root / ("distribution-" + digest[:12])
    state_path = args.cache_root / "distribution.json"
    if not state_path.exists():
        if root.exists():
            raise Refusal("DISTRIBUTION_STATE_REFUSAL extraction has no inventory")
        inventory = extract(data, root)
        jars = [root / n for n in inventory if n.lower().endswith("shimejiee.jar") or n.lower().endswith("shimeji-ee.jar")]
        if len(jars) != 1:
            raise Refusal("DISTRIBUTION_LAYOUT_REFUSAL expected one runtime JAR")
        state = {"url": DISTRIBUTION_URL, "sha256": digest, "headers": headers, "root": str(root),
                 "artifact": str(jars[0]), "artifact_sha256": verify_distribution_jar(jars[0].read_bytes()), "files": inventory}
        write_json(state_path, state)
    state = json.loads(state_path.read_text(encoding="utf-8"))
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        jars = [root / info.filename for info, _ in archive_targets(root, archive.infolist())
                if info.filename.lower().endswith(("shimejiee.jar", "shimeji-ee.jar"))]
    if len(jars) != 1 or state["sha256"] != DISTRIBUTION_SHA256 or state["artifact_sha256"] != DISTRIBUTION_JAR_SHA256 \
            or Path(state["root"]).resolve() != root.resolve() or Path(state["artifact"]).resolve() != jars[0].resolve():
        raise Refusal("DISTRIBUTION_STATE_REFUSAL pinned identity or path differs")
    check_extracted(data, root, state["files"])
    verify_distribution_jar(jars[0].read_bytes())
    return state


def runtime_inputs(args, runtime):
    source = check_source(args)
    if runtime == "official":
        state = distribution(args)
        root = Path(state["root"])
        inventory = state["files"]
    else:
        state = json.loads((args.cache_root / "latest-build.json").read_text(encoding="utf-8"))
        verify(Path(state["artifact"]).read_bytes(), state["artifact_sha256"], "rebuilt-runtime")
        root = source
        inventory = json.loads((args.cache_root / "source-inventory.json").read_text(encoding="utf-8"))["files"]
    resources = {name: digest for name, digest in inventory.items() if name.startswith(("lib/", "conf/"))}
    libraries = sorted(root / name for name in resources if name.startswith("lib/") and name.lower().endswith(".jar"))
    if not libraries or not any(name.startswith("conf/") for name in resources):
        raise Refusal("RUNTIME_RESOURCES_REFUSAL " + runtime)
    classpath = os.pathsep.join(str(path) for path in [Path(state["artifact"])] + libraries + [root / "conf"])
    witness = {"root": str(root), "files": resources, "classpath": classpath}
    return state, classpath, witness


def validate_comparison(result):
    if result.get("common_classes") != EXPECTED_CLASS_COUNT:
        raise Refusal("COMPARISON_CLASS_COUNT_REFUSAL expected " + str(EXPECTED_CLASS_COUNT))
    if result.get("only_rebuilt") != [] or result.get("only_official") != []:
        raise Refusal("COMPARISON_INVENTORY_REFUSAL class inventories differ")
    if result.get("instruction_or_signature_differences") != {}:
        raise Refusal("COMPARISON_BYTECODE_REFUSAL class instructions or signatures differ")


def require_passing_comparison(args):
    path = args.output_root / "comparison" / "comparison.json"
    if not path.is_file():
        raise Refusal("COMPARISON_WITNESS_MISSING " + str(path))
    result = json.loads(path.read_text(encoding="utf-8"))
    validate_comparison(result)
    rebuilt, _, rebuilt_inputs = runtime_inputs(args, "rebuilt")
    official, _, official_inputs = runtime_inputs(args, "official")
    expected = {"status": "pass", "rebuilt_sha256": rebuilt["artifact_sha256"],
                "official_zip_sha256": DISTRIBUTION_SHA256, "official_jar_sha256": DISTRIBUTION_JAR_SHA256,
                "source_zip_sha256": SOURCE_SHA256, "tool_sha256": sha((HERE / "oracle.py").read_bytes()),
                "runtime_inputs": {"rebuilt": rebuilt_inputs, "official": official_inputs}}
    if any(result.get(key) != value for key, value in expected.items()):
        raise Refusal("COMPARISON_WITNESS_STALE runtime, resources, or tooling changed")
    return result


def normalize_javap(text):
    lines = []
    for line in text.splitlines():
        if line.startswith("Compiled from "):
            continue
        instruction, separator, resolved = line.partition("//")
        instruction = re.sub(r"#\d+", "#CP", instruction)
        lines.append(instruction.rstrip() + (" //" + resolved if separator else ""))
    return "\n".join(lines).strip()


def javap_classes(args, artifact, names, output, label):
    record = command([args.jdk_root / "bin" / "javap.exe", "-classpath", artifact,
                      "-p", "-c", "-s", "-constants"] + names, HERE, timeout=120)
    write_json(output / (label + "-javap-command.json"), record)
    require_command(record, "JAVAP_FAILED " + label)
    parsed = {}
    for section in re.split(r'(?m)(?=^Compiled from ")', record["stdout"]):
        declaration = re.search(r"(?m)^(?:public |protected |private |abstract |final |static )*(?:class|interface) ([\w.$]+)", section)
        if declaration:
            parsed[declaration.group(1)] = normalize_javap(section)
    missing = sorted(set(names) - set(parsed))
    if missing:
        raise Refusal("JAVAP_MISSING_CLASSES " + ",".join(missing))
    return parsed


def compare(args):
    rebuilt, _, rebuilt_inputs = runtime_inputs(args, "rebuilt")
    official, _, official_inputs = runtime_inputs(args, "official")
    reports = {}
    inventories = {}
    for label, state in (("rebuilt", rebuilt), ("official", official)):
        with zipfile.ZipFile(state["artifact"]) as jar:
            inventories[label] = sorted(n[:-6].replace("/", ".") for n in jar.namelist() if n.endswith(".class"))
    common = sorted(set(inventories["rebuilt"]) & set(inventories["official"]))
    output = args.output_root / "comparison"
    output.mkdir(parents=True, exist_ok=True)
    disassembled = {label: javap_classes(args, state["artifact"], common, output, label)
                    for label, state in (("rebuilt", rebuilt), ("official", official))}
    for name in common:
        pair = {label: disassembled[label][name] for label in ("rebuilt", "official")}
        if pair["rebuilt"] != pair["official"]:
            path = output / (name + ".json")
            write_json(path, pair)
            reports[name] = str(path)
    result = {"rebuilt_sha256": rebuilt["artifact_sha256"], "official_zip_sha256": official["sha256"],
              "official_jar_sha256": official["artifact_sha256"], "common_classes": len(common),
              "only_rebuilt": sorted(set(inventories["rebuilt"]) - set(common)),
              "only_official": sorted(set(inventories["official"]) - set(common)),
              "instruction_or_signature_differences": reports,
              "source_zip_sha256": SOURCE_SHA256, "tool_sha256": sha((HERE / "oracle.py").read_bytes()),
              "runtime_inputs": {"rebuilt": rebuilt_inputs, "official": official_inputs},
              "status": "fail",
              "note": "Constant-pool references normalized; bytecode comparison is not a complete behavioral proof."}
    write_json(output / "comparison.json", result)
    validate_comparison(result)
    result["status"] = "pass"
    write_json(output / "comparison.json", result)
    return result


def expect_refusal(call, expected, witness):
    try:
        call()
    except Refusal as error:
        if not str(error).startswith(expected):
            raise AssertionError(witness + " wrong refusal: " + str(error))
    except Exception as error:
        # A disabled guard can let the input crash further on; the witness still names what failed.
        raise AssertionError(witness + " crashed instead of refusing: " + type(error).__name__)
    else:
        raise AssertionError(witness + " missing refusal")


def validate_isolation(args, environment=None):
    environment = os.environ if environment is None else environment
    flags = {"DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT": "1", "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "0"}
    if any(environment.get(key) != value for key, value in flags.items()):
        raise Refusal("ISOLATION_VALUE_REFUSAL required flag differs")
    paths = {"DESKTOP_AI_COMPANION_DATA_ROOT": args.data_root, "TEMP": args.temp_root, "TMP": args.temp_root}
    if any(not environment.get(key) or Path(environment[key]).resolve() != Path(value).resolve() for key, value in paths.items()):
        raise Refusal("ISOLATION_PATH_REFUSAL data or temporary root differs")


def validate_shared_rng(scenario):
    if scenario.get("rng", {}).get("kind") != "none":
        raise Refusal("SHARED_RNG_UNCONTROLLED_REFUSAL only declared rng.kind=none is accepted by this provisional driver")


def distribution_cache_witness(args):
    runtime = b"synthetic-runtime-for-cache-witness"
    payload = io.BytesIO()
    with zipfile.ZipFile(payload, "w") as archive:
        archive.writestr("Shimeji-ee.jar", runtime)
        archive.writestr("conf/synthetic.properties", "fixture=true")
    data = payload.getvalue()
    fetches = []

    def synthetic_fetch(url):
        fetches.append(url)
        return data, {"witness": "synthetic-no-network"}

    # Separate function globals use synthetic fixture identities, never changing live pins.
    namespace = dict(globals(), DISTRIBUTION_SHA256=sha(data), DISTRIBUTION_JAR_SHA256=sha(runtime),
                     fetch_bytes=synthetic_fetch)
    for function in (verify_distribution, verify_distribution_jar, distribution):
        namespace[function.__name__] = FunctionType(function.__code__, namespace, function.__name__)
    with tempfile.TemporaryDirectory(prefix="oracle-distribution-cache-", dir=args.temp_root) as directory:
        root = Path(directory)
        fixture_args = SimpleNamespace(cache_root=root / "cache", output_root=root / "evidence")
        namespace["distribution"](fixture_args)
        cached = fixture_args.cache_root / "shimejiee.zip"
        assert cached.is_file(), "DISTRIBUTION_CACHE_WITNESS initial archive was not retained"
        cached.unlink()
        namespace["distribution"](fixture_args)
        assert cached.is_file() and cached.read_bytes() == data, "DISTRIBUTION_CACHE_WITNESS missing archive was not restored"
        namespace["distribution"](fixture_args)
        assert len(fetches) == 2, "DISTRIBUTION_CACHE_WITNESS restored archive was downloaded again"


def self_test(args):
    labels = []
    current = {"probe_source_sha256": "probe", "tool_sha256": "tool", "pose_geometry_source_sha256": "geometry"}
    validate_probe_tooling(current, "probe", "tool", "geometry")
    stale_geometry = dict(current, pose_geometry_source_sha256="old-geometry")
    expect_refusal(lambda: validate_probe_tooling(stale_geometry, "probe", "tool", "geometry"),
                   "PROBE_VERSION_MISMATCH", "POSE_GEOMETRY_VERSION_WITNESS")
    labels.append("pose-geometry-tooling-version")
    repository = repository_root()
    forbidden = SimpleNamespace(cache_root=repository / "unwritten-cache", output_root=args.output_root)
    expect_refusal(lambda: validate_storage_roots(forbidden), "REPOSITORY_STORAGE_REFUSAL cache-root", "REPOSITORY_CACHE_WITNESS")
    forbidden = SimpleNamespace(cache_root=args.cache_root, output_root=repository / "unwritten-evidence")
    expect_refusal(lambda: validate_storage_roots(forbidden), "REPOSITORY_STORAGE_REFUSAL output-root", "REPOSITORY_OUTPUT_WITNESS")
    for attribute, label, witness in (
            ("data_root", "data-root", "REPOSITORY_DATA_WITNESS"),
            ("temp_root", "temp-root", "REPOSITORY_TEMP_WITNESS"),
            ("shared_root", "shared-root", "REPOSITORY_SHARED_WITNESS")):
        forbidden = SimpleNamespace(cache_root=args.cache_root, output_root=args.output_root)
        setattr(forbidden, attribute, repository / ("unwritten-" + label))
        expect_refusal(lambda: validate_storage_roots(forbidden), "REPOSITORY_STORAGE_REFUSAL " + label, witness)
    sibling = repository.parent / (repository.name + "-separate")
    validate_storage_roots(SimpleNamespace(cache_root=sibling / "cache", output_root=sibling / "evidence",
                                         data_root=sibling / "data", temp_root=sibling / "temp", shared_root=sibling / "shared"))
    labels.append("repository-storage-and-prefix-boundary")
    distribution_cache_witness(args)
    labels.append("distribution-cache-writeback")
    verify(b"known", sha(b"known"), "positive-control")
    try:
        verify(b"corrupt", sha(b"known"), "synthetic-corruption")
    except Refusal as error:
        assert str(error).startswith("CHECKSUM_REFUSAL synthetic-corruption")
        labels.append("checksum-corruption-refused")
    else:
        raise AssertionError("CHECKSUM_WITNESS missing checksum refusal")
    for name, expected in (("../escape.txt", "ARCHIVE_PATH_REFUSAL"), ("fixture.env", "PROTECTED_PATH_REFUSAL W01")):
        try:
            member_target(args.output_root, zipfile.ZipInfo(name))
        except Refusal as error:
            assert str(error).startswith(expected)
            labels.append(expected)
        else:
            raise AssertionError("ARCHIVE_WITNESS " + name)
    assert member_target(args.output_root, zipfile.ZipInfo("safe/source.java")).name == "source.java"
    assert protected_member("settings.env/child.txt") == "W01", "PROTECTED_ANCESTOR_WITNESS oracle.py"
    assert normalize_javap("  0: ldc #12 // String witness") == normalize_javap("  0: ldc #98 // String witness")
    assert normalize_javap("  0: iconst_1") != normalize_javap("  0: iconst_2"), "JAVAP_DIFFERENCE_WITNESS oracle.py"
    link = zipfile.ZipInfo("SyntheticLink.java")
    link.external_attr = 0o120777 << 16
    expect_refusal(lambda: member_target(args.output_root, link), "ARCHIVE_LINK_REFUSAL", "ARCHIVE_LINK_WITNESS")
    large = zipfile.ZipInfo("SyntheticLarge.java")
    large.file_size = MAX_EXPANDED_BYTES + 1
    expect_refusal(lambda: archive_targets(args.output_root, [large]), "ARCHIVE_SIZE_REFUSAL", "ARCHIVE_SIZE_WITNESS")
    expect_refusal(lambda: verify_distribution(b"changed distribution"), "CHECKSUM_REFUSAL official-distribution-zip", "DISTRIBUTION_PIN_WITNESS")
    expect_refusal(lambda: verify_distribution_jar(b"changed runtime"), "CHECKSUM_REFUSAL official-distribution-jar", "DISTRIBUTION_JAR_PIN_WITNESS")
    isolated = {"DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT": "1", "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "0",
                "DESKTOP_AI_COMPANION_DATA_ROOT": str(args.data_root), "TEMP": str(args.temp_root), "TMP": str(args.temp_root)}
    validate_isolation(args, isolated)
    altered_environment = dict(isolated, DESKTOP_AI_COMPANION_SELFTEST_NO_REAL_INPUT="0")
    expect_refusal(lambda: validate_isolation(args, altered_environment), "ISOLATION_VALUE_REFUSAL", "ISOLATION_VALUE_WITNESS")
    altered_environment = dict(isolated, TMP=str(args.temp_root / "different"))
    expect_refusal(lambda: validate_isolation(args, altered_environment), "ISOLATION_PATH_REFUSAL", "ISOLATION_PATH_WITNESS")
    validate_shared_rng({"rng": {"kind": "none"}})
    expect_refusal(lambda: validate_shared_rng({"rng": {"kind": "tape"}}), "SHARED_RNG_UNCONTROLLED_REFUSAL", "SHARED_RNG_WITNESS")
    comparison = {"common_classes": EXPECTED_CLASS_COUNT, "only_rebuilt": [], "only_official": [],
                  "instruction_or_signature_differences": {}}
    validate_comparison(comparison)
    for key, value, expected, witness in (
        ("common_classes", EXPECTED_CLASS_COUNT - 1, "COMPARISON_CLASS_COUNT_REFUSAL", "CLASS_COUNT_WITNESS"),
        ("only_rebuilt", ["SyntheticExtra"], "COMPARISON_INVENTORY_REFUSAL", "CLASS_INVENTORY_WITNESS"),
        ("instruction_or_signature_differences", {"Synthetic": "changed instruction"}, "COMPARISON_BYTECODE_REFUSAL", "CLASS_BYTECODE_WITNESS")):
        altered = dict(comparison)
        altered[key] = value
        expect_refusal(lambda: validate_comparison(altered), expected, witness)
    with tempfile.TemporaryDirectory(prefix="oracle-integrity-", dir=os.environ["TEMP"]) as folder:
        archive_bytes = io.BytesIO()
        with zipfile.ZipFile(archive_bytes, "w") as archive:
            archive.writestr("SyntheticSource.java", b"original")
        root = Path(folder)
        recorded = extract(archive_bytes.getvalue(), root)
        (root / "SyntheticSource.java").write_bytes(b"changed")
        try:
            check_extracted(archive_bytes.getvalue(), root, recorded)
        except Refusal as error:
            assert str(error).startswith("SOURCE_CHANGED"), "SOURCE_INTEGRITY_WITNESS wrong refusal"
            labels.append("source-file-tamper-refused")
        else:
            raise AssertionError("SOURCE_INTEGRITY_WITNESS oracle.py")
        (root / "SyntheticSource.java").write_bytes(b"original")
        expect_refusal(lambda: check_extracted(archive_bytes.getvalue(), root, {}),
                       "SOURCE_INVENTORY_CHANGED", "INVENTORY_ONLY_WITNESS")
        (root / "SyntheticExtra.java").write_bytes(b"extra")
        expect_refusal(lambda: check_extracted(archive_bytes.getvalue(), root, recorded),
                       "EXTRACTED_INVENTORY_CHANGED", "EXTRACTED_INVENTORY_WITNESS")
    labels.extend(["link-mode-refused", "expanded-size-refused", "distribution-pins-refused",
                   "class-comparison-refuses-difference-count-inventory", "inventory-only-refused", "extra-extracted-file-refused",
                   "isolation-values-and-paths-refused", "uncontrolled-shared-tape-refused"])
    v1_self_test(args, labels)
    burndown_self_test(args, labels)
    review_self_test(args, labels)
    synthetic_input_self_test(args, labels)
    timeout_footer_self_test(args, labels)
    trace2_self_test(args, labels)
    return {"status": "pass", "witnesses": labels, "network_requests": 0}


def v1_self_test(args, labels):
    """The v1 producer's own controls, on synthetic inputs: each refusal and each distinct run status fires."""
    with tempfile.TemporaryDirectory(prefix="oracle-v1-witness-", dir=args.temp_root) as folder:
        root = Path(folder)
        (root / "draft.jar").write_bytes(b"synthetic draft runtime")
        (root / "seams.patch").write_bytes(b"synthetic patch")
        helpers = root / "helpers"
        helpers.mkdir()
        inventory = [{"file": "src/com/group_finity/mascot/config/Changed.java", "beforeSha256": "0" * 64,
                      "expectedAfterSha256": "1" * 64}]
        for name in V1_HELPERS:
            (helpers / name).write_bytes(b"synthetic helper " + name.encode("ascii") + b"\n")
            inventory.append({"file": V1_HELPER_PATH + name, "beforeSha256": None,
                              "expectedAfterSha256": sha(b"synthetic helper " + name.encode("ascii") + b"\n")})
        write_json(root / "file-witnesses.json", inventory)
        spec = b"synthetic edit specification"
        state = {"artifact": str(root / "draft.jar"), "artifact_sha256": sha(b"synthetic draft runtime"),
                 "patch": str(root / "seams.patch"), "patch_sha256": sha(b"synthetic patch"), "source": str(root),
                 "edit_spec_sha256": sha(spec), "file_witnesses": str(root / "file-witnesses.json")}
        validate_v1_build(state, spec, helpers)
        incomplete = dict(state)
        incomplete.pop("artifact_sha256")
        expect_refusal(lambda: validate_v1_build(incomplete, spec, helpers), "V1_BUILD_WITNESS_MISSING", "V1_BUILD_WITNESS_WITNESS")
        (root / "seams.patch").write_bytes(b"synthetic patch, tampered")
        expect_refusal(lambda: validate_v1_build(state, spec, helpers), "V1_PATCH_TAMPERED", "V1_PATCH_WITNESS")
        (root / "seams.patch").write_bytes(b"synthetic patch")
        (root / "draft.jar").write_bytes(b"synthetic draft runtime, rebuilt")
        expect_refusal(lambda: validate_v1_build(state, spec, helpers), "V1_ARTIFACT_CHANGED", "V1_ARTIFACT_WITNESS")
        (root / "draft.jar").write_bytes(b"synthetic draft runtime")
        expect_refusal(lambda: validate_v1_build(state, spec + b" edited", helpers), "V1_DRAFT_STALE edit-spec", "V1_SPEC_STALE_WITNESS")
        (helpers / "DraftParityHooks.java").write_bytes(b"synthetic helper, edited\n")
        expect_refusal(lambda: validate_v1_build(state, spec, helpers), "V1_DRAFT_STALE helper", "V1_HELPER_STALE_WITNESS")
        (helpers / "DraftParityHooks.java").write_bytes(b"synthetic helper DraftParityHooks.java\n")
        validate_v1_build(state, spec, helpers)
        labels.append("v1-build-witness-patch-artifact-spec-helper-refused")

        # F2: an empty or partial helper inventory, a repeated row or a helper at another path is refused.
        def with_inventory(rows):
            write_json(root / "file-witnesses.json", rows)
            return lambda: validate_v1_build(state, spec, helpers)
        expect_refusal(with_inventory([]), "V1_HELPER_INVENTORY_REFUSAL missing", "V1_HELPER_MISSING_WITNESS")
        for index in range(1, len(inventory)):
            expect_refusal(with_inventory(inventory[:index] + inventory[index + 1:]), "V1_HELPER_INVENTORY_REFUSAL missing",
                           "V1_HELPER_MISSING_WITNESS")
        expect_refusal(with_inventory(inventory + [inventory[1]]), "V1_HELPER_INVENTORY_REFUSAL unexpected or repeated",
                       "V1_HELPER_SHAPE_WITNESS")
        moved = [dict(row, file=row["file"].replace("/parity/", "/elsewhere/")) if row is inventory[2] else row for row in inventory]
        expect_refusal(with_inventory(moved), "V1_HELPER_INVENTORY_REFUSAL unexpected or repeated", "V1_HELPER_SHAPE_WITNESS")
        extra = inventory + [{"file": V1_HELPER_PATH + "Unexpected.java", "beforeSha256": None, "expectedAfterSha256": "2" * 64}]
        expect_refusal(with_inventory(extra), "V1_HELPER_INVENTORY_REFUSAL unexpected or repeated", "V1_HELPER_SHAPE_WITNESS")
        write_json(root / "file-witnesses.json", inventory)
        validate_v1_build(state, spec, helpers)
        labels.append("v1-helper-inventory-empty-partial-repeated-moved-extra-refused")

        # Distinct statuses. The stub stands in for compare.load_trace, whose own classes compare.py --self-test proves.
        class Unjudgeable(Exception):
            pass
        stub = SimpleNamespace(Unjudgeable=Unjudgeable, load_trace=lambda path, runtime: None)
        trace = root / "trace.jsonl"
        header = json.dumps({"type": "header"})
        footer = lambda status: json.dumps({"type": "footer", "status": status})
        for lines, expected, witness in (
                ([header, footer("complete")], "complete", "V1_COMPLETE_WITNESS"),
                ([header, json.dumps({"type": "state"})], "truncated", "V1_TRUNCATED_WITNESS"),
                ([header, footer("timeout")], "timeout", "V1_TIMEOUT_WITNESS"),
                ([header, footer("error")], "error", "V1_ERROR_WITNESS")):
            trace.write_text("\n".join(lines) + "\n", encoding="utf-8")
            status = v1_trace_status(stub, trace)[0]
            if status != expected:
                raise AssertionError(witness + " status " + status)
        labels.append("v1-run-status-complete-truncated-timeout-error")
        require_v1_results(["S-a", "S-b"], [{"scenario": "S-a"}, {"scenario": "S-b"}])
        expect_refusal(lambda: require_v1_results(["S-a", "S-b"], [{"scenario": "S-a"}]), "V1_SCENARIO_SKIPPED S-b", "V1_SKIPPED_WITNESS")
        labels.append("v1-skipped-scenario-refused")
        state_line = {"type": "state", "tick": 2, "sid": "m1", "anchor": [1, 2], "velocity": [0, 0]}
        on = [{"type": "header", "scenario": {"id": "S-a"}}, state_line,
              {"type": "footer", "ticks": 2, "status": "complete", "draws": 1, "events": 3,
               "eventSequenceSha256": sha(b"")}]
        off = [on[0], dict(state_line, velocity=[5, 5]),
               {"type": "footer", "ticks": 2, "status": "complete", "unobservedDraws": 1, "unobservedEvents": 3,
                "eventSequenceSha256": on[-1]["eventSequenceSha256"]}]

        def proof(on_rows, off_rows):
            for name, rows in (("on.jsonl", on_rows), ("off.jsonl", off_rows)):
                (root / name).write_text("\n".join(json.dumps(row) for row in rows) + "\n", encoding="utf-8")
            return lambda: v1_observation_proof(root / "on.jsonl", root / "off.jsonl")
        proof(on, off)()
        for digest in (None, "invalid", sha(b"second\nfirst\nthird\n")):
            expect_refusal(proof(on, [off[0], off[1], dict(off[2], eventSequenceSha256=digest)]),
                           "V1_OBSERVATION_CHANGED_BEHAVIOUR event sequence", "V1_EVENT_SEQUENCE_WITNESS")
        labels.append("v1-observation-event-order-digest-refuses-equal-count-reordering-and-missing-digests")
        forged = [on[0], {"type": "event", "tick": 1, "sid": "m1", "kind": "drag", "op": "start"}, on[1], on[2]]
        expect_refusal(proof(forged, off), "V1_OBSERVATION_CHANGED_BEHAVIOUR event sequence digest does not match",
                       "V1_EVENT_TRACE_DIGEST_WITNESS")
        labels.append("v1-event-digest-is-recomputed-from-observed-event-records")
        expect_refusal(proof(on, [off[0], dict(state_line, anchor=[1, 3]), off[2]]),
                       "V1_OBSERVATION_CHANGED_BEHAVIOUR final tick differs", "V1_OBSERVATION_WITNESS")
        expect_refusal(proof(on, [off[0], dict(off[2], status="error")]),
                       "V1_OBSERVATION_CHANGED_BEHAVIOUR a run did not complete", "V1_OBSERVATION_STATUS_WITNESS")
        # F4: every companion disposed before the last tick, equal on both sides: an empty final population passes.
        gone = [on[0], dict(state_line, tick=0), {"type": "event", "tick": 1, "sid": "m1", "kind": "dispose"}, on[2]]
        gone[-1] = dict(gone[-1], eventSequenceSha256=sha((json.dumps(gone[2]) + "\n").encode("utf-8")))
        try:
            proof(gone, [off[0], dict(off[2], eventSequenceSha256=gone[-1]["eventSequenceSha256"])])()
        except Refusal as failure:
            raise AssertionError("V1_EMPTY_FINAL_WITNESS equal empty final populations refused: " + str(failure))
        labels.append("v1-observation-proof-refuses-a-changed-final-tick-or-an-incomplete-run-accepts-all-disposed")

        # F1: a scenario id is one safe leaf name; nothing is written for a traversal, rooted or separator id.
        runs = root / "runs"
        runs.mkdir()
        v1_run_directory(runs, "S-ok_01.a")
        for bad in ("../escaped", "S-a/../../escaped", "S-a\\b", str(root / "rooted"), "/rooted", "C:escaped", "S-.", "", None):
            expect_refusal(lambda: v1_run_directory(runs, bad), "V1_SCENARIO_ID_REFUSAL", "V1_SCENARIO_ID_WITNESS")
        expect_refusal(lambda: v1_contained(runs, runs.parent / "outside"), "V1_RUN_CONTAINMENT_REFUSAL", "V1_CONTAINMENT_WITNESS")
        escaping = root / "scenario.json"
        escaping.write_text(json.dumps({"id": "../escaped", "pack": "packs/none"}), encoding="utf-8")
        refusing = SimpleNamespace(validate_scenario=lambda doc: None,
                                   pack_preflight=lambda pack: (_ for _ in ()).throw(AssertionError("V1_SCENARIO_ID_WITNESS reached the pack")))
        expect_refusal(lambda: v1_run(args, refusing, escaping, runs, "", root, {}), "V1_SCENARIO_ID_REFUSAL", "V1_SCENARIO_ID_WITNESS")
        if (root / "escaped").exists() or any(runs.iterdir()):
            raise AssertionError("V1_SCENARIO_ID_WITNESS a refused id still wrote " + str(sorted(p.name for p in root.iterdir())))
        labels.append("v1-scenario-id-traversal-rooted-separator-refused-before-any-write")

        # V1Failures binding: stale producer, build or trace bytes are refused.
        recorded = root / "recorded-trace.jsonl"
        recorded.write_bytes(b"synthetic trace\n")
        row = {"scenario": "S-a", "trace": str(recorded), "trace_sha256": sha(b"synthetic trace\n")}
        latest = {"tool_sha256": "a" * 64, "runner_sha256": "b" * 64,
                  "build": {"artifactSha256": state["artifact_sha256"], "patchSha256": state["patch_sha256"]}}
        require_current_v1_evidence(latest, "a" * 64, "b" * 64, state, row)
        for tool, runner_sha, witness in (("c" * 64, "b" * 64, "V1_EVIDENCE_TOOL_WITNESS"), ("a" * 64, "c" * 64, "V1_EVIDENCE_TOOL_WITNESS")):
            expect_refusal(lambda: require_current_v1_evidence(latest, tool, runner_sha, state, row), "V1_EVIDENCE_STALE oracle.py", witness)
        expect_refusal(lambda: require_current_v1_evidence(dict(latest, build={"artifactSha256": "d" * 64, "patchSha256": state["patch_sha256"]}),
                                                           "a" * 64, "b" * 64, state, row), "V1_EVIDENCE_STALE the draft", "V1_EVIDENCE_BUILD_WITNESS")
        recorded.write_bytes(b"synthetic trace, edited\n")
        expect_refusal(lambda: require_current_v1_evidence(latest, "a" * 64, "b" * 64, state, row), "V1_EVIDENCE_STALE trace", "V1_EVIDENCE_TRACE_WITNESS")
        labels.append("v1-failures-stale-tool-runner-build-trace-refused")

        # The proof case is named and directed: a splitmix64 proof scenario is unavailable, a tape one is used.
        proof_root = root / "proof"
        proof_root.mkdir()
        (proof_root / (V1_PROOF_SCENARIO + ".json")).write_text(json.dumps({"rng": {"kind": "tape", "values": [0.5]}}), encoding="utf-8")
        if v1_proof_case(proof_root)[0] is None:
            raise AssertionError("V1_PROOF_KIND_WITNESS a tape proof scenario was refused")
        (proof_root / (V1_PROOF_SCENARIO + ".json")).write_text(json.dumps({"rng": {"kind": "splitmix64", "seed": "0x1"}}), encoding="utf-8")
        if v1_proof_case(proof_root)[0] is not None:
            raise AssertionError("V1_PROOF_KIND_WITNESS a free-running proof scenario was accepted")
        labels.append("v1-proof-case-named-and-directed")

    # Reviews W1-W3: the document witness asserts the exact file per image set and stack position, spelled as the
    # pack manifest lists it, and the unmodified pack's stacks. Two sets, one of them at a lower tier.
    expected = {"Alpha": ("img/Alpha/conf/behaviors.xml", "img/Alpha/conf/actions.xml"),
                "Beta": ("conf/Beta/behaviors.xml", "conf/Beta/actions.xml")}
    manifest = {document for documents in expected.values() for document in documents}
    steps = ("/Mascot[1]/BehaviorList[1]/Behavior[1]", "/Mascot[1]/ActionList[1]/Action[1]")

    def stacked(sid, name, files, paths=steps):
        stack = [name + "|" + files[0] + "|" + paths[0]] + [name + "|" + files[1] + "|" + step for step in paths[1:]]
        return {"type": "state", "tick": 3, "sid": sid, "actionStack": stack, "refKey": stack[-1]}
    good = [{"type": "header"}, stacked("m1", "Alpha", expected["Alpha"]), stacked("m2", "Beta", expected["Beta"])]

    def document_refused(lines, prefix, witness, wanted=expected, baseline=None):
        try:
            problems = v1_document_check(lines, wanted, manifest, baseline)
        except Exception as error:
            raise AssertionError(witness + " crashed instead of refusing: " + type(error).__name__)
        if not problems or not problems[0].startswith(prefix):
            raise AssertionError(witness + " " + (problems[0] if problems else "accepted"))
    if v1_document_check(good, expected, manifest, good):
        raise AssertionError("V1_DOCUMENT_CHECK_WITNESS the exact documents were refused")
    # Review W1's mutant D: every document bound to the behaviours file's name.
    document_refused([good[0], stacked("m1", "Alpha", (expected["Alpha"][0],) * 2), stacked("m2", "Beta", (expected["Beta"][0],) * 2)],
                     "V1_DOCUMENT_KEY_FILE", "V1_DOCUMENT_FILE_WITNESS")
    document_refused([good[0], good[1], stacked("m2", "Beta", expected["Alpha"])], "V1_DOCUMENT_KEY_FILE", "V1_DOCUMENT_CROSS_SET_WITNESS")
    document_refused([good[0], good[1], stacked("m2", "Gamma", expected["Beta"])], "V1_DOCUMENT_KEY_SET", "V1_DOCUMENT_SET_WITNESS")
    document_refused([good[0], dict(good[1], actionStack=[], refKey=None)], "V1_DOCUMENT_KEY_NONE", "V1_DOCUMENT_NONE_WITNESS")
    respelled = dict(expected, Alpha=("img/Alpha/conf/Behaviors.xml", expected["Alpha"][1]))
    document_refused([good[0], stacked("m1", "Alpha", respelled["Alpha"]), good[2]], "V1_DOCUMENT_EXPECTATION_UNLISTED",
                     "V1_DOCUMENT_UNLISTED_WITNESS", respelled)
    document_refused([good[0], stacked("m1", "Alpha", expected["Alpha"], (steps[0], "/Mascot[1]/ActionList[1]/Action[2]")), good[2]],
                     "V1_DOCUMENT_KEY_BASELINE", "V1_DOCUMENT_BASELINE_WITNESS", baseline=good)
    labels.append("v1-document-check-exact-file-per-set-and-position-listed-and-as-the-baseline")

    # Review W123 F2: a scenario spelling its set unlike the folder is judged against the baseline renamed to it.
    lowered = {"alpha": expected["Alpha"]}
    if v1_document_check([good[0], stacked("m1", "alpha", expected["Alpha"])], lowered, manifest,
                         v1_renamed_sets([good[0], good[1]], {"Alpha": "alpha"})):
        raise AssertionError("V1_DOCUMENT_RENAME_WITNESS a renamed set failed its baseline")
    # Review W123 F1: a case the runner must refuse passes only aborted with the named refusal at the expected path.
    named = {"status": "aborted", "detail": "V1_REFUSAL V1_REFKEY_DOCUMENT_SPELLING ../pack/img/A/conf/behaviors.xml: 0 pack entries spell .."}
    if v1_document_refusal(named, "../pack/img/A/conf/"):
        raise AssertionError("V1_DOCUMENT_REFUSAL_WITNESS the named refusal was not accepted")
    for row in ({"status": "complete", "detail": ""}, dict(named, detail="V1_REFUSAL V1_CONFIGURATION_REFUSED ../A"),
                dict(named, status="error"), dict(named, detail=named["detail"].replace("../pack", "img"))):
        problems = v1_document_refusal(row, "../pack/img/A/conf/")
        if not problems or not problems[0].startswith("V1_DOCUMENT_REFUSAL_MISSING"):
            raise AssertionError("V1_DOCUMENT_REFUSAL_WITNESS accepted " + row["status"] + " " + row["detail"][:60])
    labels.append("v1-document-renamed-set-baseline-and-named-spelling-refusal")

    # Review W123 F4: two entries a case-insensitive disk holds as one file are refused after the copy. Windows only,
    # as the oracle is: the second write replaces the first file's bytes under its name.
    with tempfile.TemporaryDirectory(prefix="oracle-v1-copy-", dir=args.temp_root) as folder:
        source = Path(folder) / "source"
        for part, payload in (("one", b"first entry"), ("two", b"second entry")):
            (source / part).mkdir(parents=True)
            (source / part / "actions.xml").write_bytes(payload)
        v1_copy_pack([("conf/actions.xml", source / "one" / "actions.xml")], Path(folder) / "single")
        expect_refusal(lambda: v1_copy_pack([("conf/actions.xml", source / "one" / "actions.xml"),
                                             ("conf/Actions.xml", source / "two" / "actions.xml")], Path(folder) / "merged"),
                       "CHECKSUM_REFUSAL v1-pack-copy-merged conf/actions.xml", "V1_PACK_COPY_MERGE_WITNESS")
    labels.append("v1-pack-copy-merged-entries-refused")


def burndown_self_test(args, labels):
    """Focused synthetic witnesses; no JDK, source archive, or network is used."""
    with tempfile.TemporaryDirectory(prefix="oracle-burndown-", dir=args.temp_root) as folder:
        root = Path(folder)
        source = root / "private-source"
        resources = {"lib/synthetic.jar": b"original library", "conf/synthetic.properties": b"setting=true"}
        inventory = {name: sha(payload) for name, payload in resources.items()}
        for name, payload in resources.items():
            target = source / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(payload)
        validate_private_resources(source, inventory)
        for name in resources:
            (source / name).write_bytes(b"changed resource")
            expect_refusal(lambda: validate_private_resources(source, inventory), "V1_RESOURCE_CHANGED " + name,
                           "V1_PRIVATE_RESOURCE_HASH_WITNESS")
            (source / name).write_bytes(resources[name])
        extra = source / "lib" / "extra.jar"
        extra.write_bytes(b"extra library")
        expect_refusal(lambda: validate_private_resources(source, inventory), "V1_RESOURCE_INVENTORY_REFUSAL",
                       "V1_PRIVATE_RESOURCE_INVENTORY_WITNESS")
        extra.unlink()
        missing = source / "lib" / "synthetic.jar"
        missing.unlink()
        expect_refusal(lambda: validate_private_resources(source, inventory), "V1_RESOURCE_INVENTORY_REFUSAL",
                       "V1_PRIVATE_RESOURCE_INVENTORY_WITNESS")
        missing.write_bytes(resources["lib/synthetic.jar"])
        validate_private_resources(source, inventory)
        labels.append("v1-private-resource-library-conf-changes-extra-and-missing-refused")

        def unreadable_walk(path, followlinks=False, onerror=None):
            for row in os.walk(path, followlinks=followlinks):
                yield row
            if onerror is not None:
                onerror(OSError("synthetic unreadable resource directory"))

        scanned = FunctionType(validate_private_resources.__code__, dict(globals(), os=SimpleNamespace(walk=unreadable_walk)),
                               "validate_private_resources")
        expect_refusal(lambda: scanned(source, inventory), "V1_RESOURCE_SCAN_REFUSAL", "V1_RESOURCE_SCAN_WITNESS")
        labels.append("v1-private-resource-incomplete-directory-scan-refused")

        # Drive both consumers before a compiler can run. Helper-only tests miss a deleted entry-point call.
        here = root / "tool"
        here.mkdir()
        for name in ("edit-spec.json", "oracle.py", V1_RUNNER):
            (here / name).write_bytes(b"synthetic tooling witness")
        fixture_args = SimpleNamespace(cache_root=root / "cache", output_root=root / "evidence",
                                       shared_root=root / "shared", temp_root=args.temp_root)
        write_json(fixture_args.cache_root / "latest-draft.json", {})
        write_json(fixture_args.cache_root / "source-inventory.json", {"files": inventory})
        resource_bridge = FunctionType(validate_draft_resources.__code__,
                                       dict(globals(), check_source=lambda *unused: source), "validate_draft_resources")
        assert resource_bridge(fixture_args, {"source": str(source)}) == inventory, "V1_RESOURCE_BRIDGE_POSITIVE_WITNESS"
        missing.write_bytes(b"changed private library")
        expect_refusal(lambda: resource_bridge(fixture_args, {"source": str(source)}), "V1_RESOURCE_CHANGED",
                       "V1_RESOURCE_BRIDGE_WITNESS")
        missing.write_bytes(resources["lib/synthetic.jar"])
        labels.append("v1-archive-inventory-to-private-source-resource-wiring")

        def reject_resources(*unused):
            raise Refusal("V1_RESOURCE_CHANGED synthetic.jar")

        def unexpected_compile(*unused, **unused_keywords):
            raise AssertionError("V1_RESOURCE_WIRING_WITNESS compilation reached before resource validation")

        wiring = dict(globals(), HERE=here, validate_v1_build=lambda *unused: {},
                      validate_draft_resources=reject_resources, command=unexpected_compile)
        for entry in (v1_prepare, bridge):
            copied = FunctionType(entry.__code__, wiring, entry.__name__)
            call = (lambda: copied(fixture_args, root)) if entry is v1_prepare else (lambda: copied(fixture_args))
            expect_refusal(call, "V1_RESOURCE_CHANGED", "V1_RESOURCE_WIRING_WITNESS")
        labels.append("v1-and-bridge-validate-private-resources-before-compilation")

        runs = root / "shared-runs"
        assert shared_run_directory(runs, "S-ok", "S-ok") == runs / "S-ok", "SHARED_ID_POSITIVE_WITNESS"
        expect_refusal(lambda: shared_run_directory(runs, "S-other", "S-ok"), "SHARED_SCENARIO_ID_REFUSAL",
                       "SHARED_ID_BINDING_WITNESS")
        expect_refusal(lambda: shared_run_directory(runs, "invalid name", "invalid name"), "SHARED_SCENARIO_ID_REFUSAL",
                       "SHARED_ID_FORMAT_WITNESS")
        assert not runs.exists(), "SHARED_ID_NO_WRITE_WITNESS"
        labels.append("shared-id-bound-to-filename-and-contained-before-output")

        class SyntheticUnjudgeable(Exception):
            pass

        def refused_stage(*unused, **unused_keywords):
            raise Refusal("SYNTHETIC_STAGE_FAILURE")

        base = dict(globals(), HERE=here, check_source=lambda *unused: None,
                    comparer=lambda: SimpleNamespace(Unjudgeable=SyntheticUnjudgeable),
                    v1_prepare=lambda *unused: ({}, "", root, {}),
                    v1_scenarios=lambda *unused: [root / (V1_PROOF_SCENARIO + ".json")],
                    v1_run=lambda *unused, **kw: {"scenario": V1_PROOF_SCENARIO, "status": "complete", "trace": "fixture"},
                    v1_proof_case=lambda *unused: (None, "synthetic unavailable"),
                    v1_document_witnesses=lambda *unused: {"status": "pass"},
                    validate_v1_build=lambda *unused: {}, validate_draft_resources=lambda *unused: {})
        for name, expected_stage, overrides in (
                ("positive", None, {}),
                ("stale-build", "build-witness", {"v1_prepare": refused_stage}),
                ("missing-scenario", "requested-scenario-results", {
                    "v1_run": lambda *unused: {"scenario": "S-other", "status": "complete"}}),
                ("proof-failure", "observation-proof", {"v1_proof_case": lambda *unused: (root / "proof.json", ""),
                                                        "v1_observation_proof": refused_stage}),
                ("stale-recheck", "build-witness-recheck", {"validate_v1_build": refused_stage}),
                ("resource-recheck", "build-witness-recheck", {"validate_draft_resources": refused_stage})):
            isolated = SimpleNamespace(**vars(fixture_args))
            isolated.output_root = root / name
            copied = FunctionType(v1.__code__, dict(base, **overrides), "v1")
            witness = "V1_RESOURCE_RECHECK_WITNESS" if name == "resource-recheck" else "V1_FAILURE_RECORD_WITNESS"
            try:
                copied(isolated)
            except Refusal:
                assert expected_stage is not None, witness + " positive control refused"
            else:
                assert expected_stage is None, witness + " failure did not propagate"
            latest = json.loads((isolated.output_root / "v1-latest.json").read_text(encoding="utf-8"))
            recorded = json.loads((Path(latest["evidence"]) / "results.json").read_text(encoding="utf-8"))
            assert latest == recorded, witness + " latest differs from run record"
            if expected_stage is not None:
                assert recorded.get("status") == "fail" and recorded.get("failed_check") == expected_stage, (
                    witness + " wrong or missing failed check " + str(expected_stage))
        labels.append("v1-failed-build-missing-scenario-proof-and-recheck-recorded-before-raising")
        # A timed-out JVM can already have written a complete footer before hanging during shutdown. That file
        # alone must not override the run result and satisfy the observation proof.
        for off_status in ("complete", "timeout", "aborted", "error", "malformed"):
            def run_for_proof(*unused, **keywords):
                return {"scenario": V1_PROOF_SCENARIO, "status": "complete" if keywords.get("observe", True) else off_status,
                        "trace": "synthetic-complete-footer", "timed_out": off_status == "timeout"}

            def prove_only_completed(*unused):
                assert off_status == "complete", "V2_PROOF_OFF_RUN_WITNESS"
                return {"scenario": V1_PROOF_SCENARIO}

            proof_globals = dict(base, v1_run=run_for_proof, v1_proof_case=lambda *unused: (root / "proof.json", ""),
                                 v1_observation_proof=prove_only_completed)
            isolated = SimpleNamespace(**vars(fixture_args))
            isolated.output_root = root / ("off-proof-" + off_status)
            result = FunctionType(v1.__code__, proof_globals, "v1")(isolated)
            assert result["status"] == ("pass" if off_status == "complete" else "incomplete"), "V2_PROOF_OFF_RUN_WITNESS"
        labels.append("trace2-observation-proof-requires-the-off-child-run-to-complete")


def review_self_test(args, labels):
    """Entry-point witnesses added after review; all inputs and child results are synthetic."""
    with tempfile.TemporaryDirectory(prefix="oracle-review-", dir=args.temp_root) as folder:
        root = Path(folder)
        here = root / "tool"
        here.mkdir()
        (here / "edit-spec.json").write_text("{}", encoding="utf-8")
        artifact = root / "synthetic.jar"
        artifact.write_bytes(b"synthetic runtime")
        state = {"artifact": str(artifact), "artifact_sha256": sha(artifact.read_bytes()), "source": str(root)}
        fixture_args = SimpleNamespace(cache_root=root / "cache", output_root=root / "bridge",
                                       shared_root=root / "shared-input", jdk_root=root / "jdk")
        write_json(fixture_args.cache_root / "latest-draft.json", state)

        good = {"status": "pass", "scriptDraws": 4, "unusedReadsOff": 0, "unusedReadsOn": 0,
                "globalTypes": {name: "object" for name in ("engine", "context", "arguments")},
                "boundGlobalTypes": {"engine": "number", "context": "number", "arguments": "object"},
                "boundCustomValueObserved": {"engine": True, "context": True, "arguments": False}}
        result = dict(good)

        def synthetic_command(argv, *unused, **unused_keywords):
            return {"exit_code": 0, "stdout": json.dumps(result)}

        bridged = FunctionType(bridge.__code__, dict(globals(), HERE=here, command=synthetic_command,
                               validate_v1_build=lambda *unused: state, validate_draft_resources=lambda *unused: None),
                               "bridge")
        assert bridged(fixture_args)["status"] == "pass", "BRIDGE_GLOBALS_POSITIVE_WITNESS"
        for types in ({}, {"engine": "object", "context": "object", "arguments": "undefined"}):
            result = dict(good, globalTypes=types)
            expect_refusal(lambda: bridged(fixture_args), "BRIDGE_WITNESS_MISSING", "BRIDGE_GLOBALS_WITNESS")
        labels.append("bridge-entry-point-refuses-missing-or-changed-pinned-global-types")
        for changed in (dict(good, boundGlobalTypes=good["globalTypes"]),
                        dict(good, boundCustomValueObserved={name: True for name in good["globalTypes"]})):
            result = changed
            expect_refusal(lambda: bridged(fixture_args), "BRIDGE_WITNESS_MISSING", "BRIDGE_BINDINGS_WITNESS")
        labels.append("bridge-entry-point-refuses-changed-explicit-variable-binding-behaviour")

        # Exercise shared(), not just its path helper. A bad id must fail before output creation or compilation.
        names = ("S-slice-stand-walk-01", "S-slice-transform-01", "S-slice-breed-01")
        (fixture_args.shared_root / "pack").mkdir(parents=True)
        for name in names:
            write_json(fixture_args.shared_root / (name + ".json"),
                       {"id": name, "pack": "pack", "rng": {"kind": "none"}, "ticks": 1})
        write_json(fixture_args.shared_root / (names[0] + ".json"),
                   {"id": "S-other", "pack": "pack", "rng": {"kind": "none"}, "ticks": 1})
        fixture_args.output_root = root / "shared-preflight"

        def unexpected_command(*unused, **unused_keywords):
            raise Refusal("SHARED_COMMAND_UNEXPECTED")

        shared_globals = dict(globals(), HERE=here, check_source=lambda *unused: root,
                              require_passing_comparison=lambda *unused: {}, command=unexpected_command)
        copied_shared = FunctionType(shared.__code__, shared_globals, "shared")
        expect_refusal(lambda: copied_shared(fixture_args), "SHARED_SCENARIO_ID_REFUSAL", "SHARED_PREFLIGHT_WITNESS")
        assert not fixture_args.output_root.exists(), "SHARED_PREFLIGHT_WITNESS output created before id validation"
        write_json(fixture_args.shared_root / (names[0] + ".json"),
                   {"id": names[0], "pack": "pack", "rng": {"kind": "none"}, "ticks": 1})

        # The first three calls preflight inputs; the fourth must guard the first run before mkdir/copy/launch.
        calls = []

        def guarded_run(root_path, scenario_id, stem):
            calls.append((root_path, scenario_id, stem))
            if len(calls) == 4:
                raise Refusal("SHARED_RUN_DIRECTORY_CHECKED")
            return shared_run_directory(root_path, scenario_id, stem)

        shared_globals.update(shared_run_directory=guarded_run,
                              runtime_inputs=lambda *unused: (state, "synthetic-classpath", {}),
                              command=lambda *unused, **kw: {"exit_code": 0, "stdout": ""})
        fixture_args.output_root = root / "shared-run-site"
        copied_shared = FunctionType(shared.__code__, shared_globals, "shared")
        expect_refusal(lambda: copied_shared(fixture_args), "SHARED_RUN_DIRECTORY_CHECKED", "SHARED_RUN_SITE_WITNESS")
        assert len(calls) == 4, "SHARED_RUN_SITE_WITNESS wrong call count"
        labels.append("shared-entry-point-preflight-and-run-directory-calls-are-required")

        # A failed/running V1 record is not evidence. A healthy record must reach the build check instead.
        fixture_args.output_root = root / "failure-records"

        def reached_build(*unused):
            raise Refusal("SYNTHETIC_CURRENT_BUILD_REACHED")

        copied_failures = FunctionType(v1_failures.__code__, dict(globals(), HERE=here,
                                      comparer=lambda: SimpleNamespace(), validate_v1_build=reached_build), "v1_failures")
        complete = [{"scenario": "S-synthetic", "status": "complete", "trace": "not opened"}]
        for status in ("fail", "running", "incomplete"):
            write_json(fixture_args.output_root / "v1-latest.json",
                       {"status": status, "failed_check": "build-witness-recheck", "results": complete})
            expect_refusal(lambda: copied_failures(fixture_args), "V1_EVIDENCE_NOT_PASSED", "V1_FAILURE_STATUS_WITNESS")
        write_json(fixture_args.output_root / "v1-latest.json", {"status": "pass", "results": []})
        expect_refusal(lambda: copied_failures(fixture_args), "V1_EVIDENCE_NO_COMPLETE_SCENARIOS", "V1_FAILURE_EMPTY_WITNESS")
        write_json(fixture_args.output_root / "v1-latest.json", {"status": "pass", "results": complete})
        expect_refusal(lambda: copied_failures(fixture_args), "SYNTHETIC_CURRENT_BUILD_REACHED", "V1_FAILURE_POSITIVE_WITNESS")
        labels.append("v1-failures-refuses-nonpassing-and-empty-evidence-before-judging")

        # Exercise v1_run itself, including its recorded child stderr and physical footer. The driver refuses an
        # inactive input target at dispatch; that is invalid scenario input, not a runtime disagreement. Other
        # refusals and source errors must retain their existing statuses, and trace/1 still uses an aborted footer.
        input_root = root / "inactive-input"
        pack = input_root / "pack"
        pack.mkdir(parents=True)
        (pack / "synthetic.txt").write_bytes(b"synthetic input-classification witness")
        scenario = input_root / "S-input-classification.json"
        write_json(scenario, {"id": scenario.stem, "pack": "pack", "ticks": 1, "rng": {"kind": "none"}, "dpiScale": 1})
        input_compare = SimpleNamespace(validate_scenario=lambda *unused: None,
                                        pack_preflight=lambda path: [("synthetic.txt", pack / "synthetic.txt")],
                                        pack_sha256=lambda *unused: sha((pack / "synthetic.txt").read_bytes()))
        for index, (footer_status, stderr, expected) in enumerate((
                ("aborted", "V1_REFUSAL V1_INPUT_TARGET_ABSENT m2", "scenario-invalid"),
                ("aborted", "V1_REFUSAL V1_OTHER_DRIVER_REFUSAL m2", "aborted"),
                ("aborted", "java.lang.IllegalStateException: V1_INPUT_TARGET_ABSENT m2", "aborted"),
                ("aborted", "V1_REFUSAL V1_INPUT_TARGET_ABSENT_OTHER m2", "aborted"),
                ("error", "V1_REFUSAL V1_INPUT_TARGET_ABSENT m2", "error"),
                ("error", "java.lang.IllegalStateException: synthetic source error", "error"))):
            def input_command(argv, *unused, **unused_keywords):
                trace = Path(argv[-1])
                header = json.loads(Path(argv[-3]).read_text(encoding="utf-8"))
                trace.write_text(json.dumps(header) + "\n" + json.dumps({"type": "footer", "ticks": 1,
                                 "status": footer_status, "states": 0, "events": 0, "draws": 0}) + "\n", encoding="utf-8")
                return {"exit_code": 1, "timed_out": False, "stdout": "", "stderr": stderr}

            copied_run = FunctionType(v1_run.__code__, dict(globals(), command=input_command), "v1_run")
            row = copied_run(fixture_args, input_compare, scenario, input_root / str(index), "synthetic-classpath", root, {}, True)
            assert row["status"] == expected, "V1_" + "INACTIVE_INPUT_WITNESS " + expected + " became " + row["status"]
            physical = Path(row["trace"])
            assert json.loads(physical.read_text(encoding="utf-8").splitlines()[-1])["status"] == footer_status, (
                "V1_" + "INACTIVE_INPUT_WITNESS physical footer changed")
            assert row["trace_sha256"] == sha(physical.read_bytes()), "V1_" + "INACTIVE_INPUT_WITNESS trace hash changed"
        labels.append("v1-entry-point-classifies-only-inactive-input-targets-as-scenario-invalid")


def synthetic_input_self_test(args, labels):
    """Interrupted generation is refused by the real consumers, before source or compiler access."""
    with tempfile.TemporaryDirectory(prefix="oracle-input-set-", dir=args.temp_root) as folder:
        root = Path(folder)
        inputs = root / "inputs"
        pack = inputs / "packs" / "synthetic"
        pack.mkdir(parents=True)
        payload = pack / "payload.txt"
        payload.write_bytes(b"synthetic managed input")
        scenario = inputs / "S-synthetic-input.json"
        write_json(scenario, {})
        owned = ["packs", "packs/synthetic", "packs/synthetic/payload.txt", scenario.name]
        manifest = inputs / SHARED_OUTPUT_MANIFEST

        def journal():
            write_json(manifest, dict(schema=SHARED_OUTPUT_SCHEMA, state="ready", paths=owned))

        def reached_source(*unused):
            raise Refusal("SYNTHETIC_INPUT_SOURCE_REACHED")

        fixture_args = SimpleNamespace(shared_root=inputs, output_root=root / "evidence", only=None)
        copied_shared = FunctionType(shared.__code__, dict(globals(), check_source=reached_source), "shared")
        here = root / "tool"
        here.mkdir()
        for name in ("oracle.py", V1_RUNNER):
            (here / name).write_bytes(b"synthetic input consumer witness")
        copied_v1 = FunctionType(v1.__code__, dict(globals(), HERE=here, check_source=reached_source), "v1")
        stub_compare = SimpleNamespace(validate_scenario=reached_source)
        consumers = (
            (lambda: copied_shared(fixture_args), "SYNTHETIC_SHARED_WIRING_WITNESS"),
            (lambda: copied_v1(fixture_args), "SYNTHETIC_V1_WIRING_WITNESS"),
            (lambda: v1_scenarios(fixture_args), "SYNTHETIC_SELECTION_WIRING_WITNESS"),
            (lambda: v1_run(fixture_args, stub_compare, scenario, root, "", root, {}), "SYNTHETIC_RUN_WIRING_WITNESS"))

        # Positive controls cover both completed journal versions and custom, unmanifested fixtures.
        for state in ("ready", None, "unmanifested"):
            if state == "unmanifested":
                manifest.unlink()
            else:
                document = {"schema": SHARED_OUTPUT_SCHEMA, "paths": owned}
                if state is not None:
                    document["state"] = state
                write_json(manifest, document)
            validate_shared_output(inputs)
            assert v1_scenarios(fixture_args) == [scenario], "SYNTHETIC_INPUT_POSITIVE_WITNESS scenario selection changed"
            for consumer, witness in (consumers[0], consumers[1], consumers[3]):
                expect_refusal(consumer, "SYNTHETIC_INPUT_SOURCE_REACHED", witness + " positive control")

        journal()
        document = json.loads(manifest.read_text(encoding="utf-8"))
        document["state"] = "building"
        write_json(manifest, document)
        expect_refusal(lambda: validate_shared_output(inputs), "SYNTHETIC_INPUT_BUILDING", "SYNTHETIC_BUILDING_WITNESS")
        for consumer, witness in consumers:
            expect_refusal(consumer, "SYNTHETIC_INPUT_BUILDING", witness)
        journal()
        payload.unlink()
        expect_refusal(lambda: validate_shared_output(inputs), "SYNTHETIC_INPUT_INCOMPLETE missing packs/synthetic/payload.txt",
                       "SYNTHETIC_MISSING_PATH_WITNESS")
        payload.write_bytes(b"synthetic managed input")
        extra = inputs / "S-unlisted.json"
        write_json(extra, {})
        before = extra.read_bytes()
        expect_refusal(lambda: validate_shared_output(inputs), "SYNTHETIC_INPUT_INCOMPLETE unlisted S-unlisted.json",
                       "SYNTHETIC_EXTRA_PATH_WITNESS")
        assert extra.read_bytes() == before, "SYNTHETIC_EXTRA_PRESERVED_WITNESS"
        extra.unlink()
        for broken, witness in (
                ({"schema": "other", "paths": owned}, "SYNTHETIC_MANIFEST_SCHEMA_WITNESS"),
                ({"schema": SHARED_OUTPUT_SCHEMA, "state": "unknown", "paths": owned}, "SYNTHETIC_MANIFEST_STATE_WITNESS"),
                ({"schema": SHARED_OUTPUT_SCHEMA, "paths": ["../escape"]}, "SYNTHETIC_MANIFEST_PATH_WITNESS"),
                ({"schema": SHARED_OUTPUT_SCHEMA, "paths": [1]}, "SYNTHETIC_MANIFEST_PATHS_WITNESS"),
                ({"schema": SHARED_OUTPUT_SCHEMA, "paths": owned + [owned[0]]}, "SYNTHETIC_MANIFEST_REPEATED_WITNESS")):
            write_json(manifest, broken)
            expect_refusal(lambda: validate_shared_output(inputs), "SYNTHETIC_INPUT_MANIFEST_INVALID", witness)
        manifest.write_text("{", encoding="utf-8")
        expect_refusal(lambda: validate_shared_output(inputs), "SYNTHETIC_INPUT_MANIFEST_INVALID", "SYNTHETIC_MANIFEST_JSON_WITNESS")
        journal()
        validate_shared_output(inputs)
        labels.append("managed-synthetic-inputs-complete-building-partial-and-custom-consumer-controls")


def timeout_footer_self_test(args, labels):
    """Synthetic killed-child files exercise the real append path without launching a JVM."""
    with tempfile.TemporaryDirectory(prefix="oracle-timeout-counts-", dir=args.temp_root) as folder:
        root = Path(folder)
        header = {"type": "header"}
        body = [{"type": "state", "tick": 0, "sid": "m1"},
                {"type": "event", "kind": "behavior", "tick": 1, "sid": "m1"},
                {"type": "event", "kind": "rng-draw", "tick": 1, "sid": "m1", "occurrence": 1, "value": 0.5},
                {"type": "event", "kind": "error", "tick": 1, "sid": "m1"},
                {"type": "state", "tick": 1, "sid": "m1"}]
        trace = root / "partial.jsonl"
        prefix = ("\n".join(json.dumps(row) for row in [header] + body) + "\n").encode("utf-8")
        trace.write_bytes(prefix)
        v1_append_timeout_footer(trace, 3)
        footer = json.loads(trace.read_text(encoding="utf-8").splitlines()[-1])
        assert footer["states"] == 2, "V1_TIMEOUT_STATES_WITNESS"
        assert footer["events"] == 3, "V1_TIMEOUT_EVENTS_WITNESS"
        assert footer["draws"] == 1, "V1_TIMEOUT_DRAWS_WITNESS"
        assert trace.read_bytes().startswith(prefix), "V1_TIMEOUT_PREFIX_WITNESS"
        for payload in (b"", (json.dumps(header) + "\n").encode("utf-8")):
            trace.write_bytes(payload)
            assert v1_append_timeout_footer(trace, 3) == {"draws": 0, "states": 0, "events": 0}, "V1_TIMEOUT_ZERO_WITNESS"
        trace.unlink()
        assert v1_append_timeout_footer(trace, 3) == {"draws": 0, "states": 0, "events": 0}, "V1_TIMEOUT_ABSENT_WITNESS"
        trace.write_bytes(prefix.rstrip(b"\n"))
        v1_append_timeout_footer(trace, 3)
        try:
            separated = [json.loads(line) for line in trace.read_text(encoding="utf-8").splitlines()]
        except json.JSONDecodeError as failure:
            raise AssertionError("V1_TIMEOUT_SEPARATOR_WITNESS valid final record merged with footer") from failure
        assert separated[-1]["type"] == "footer" and separated[-2] == body[-1], "V1_TIMEOUT_SEPARATOR_WITNESS"
        for invalid in (b"{", b"\xff", b"[]\n", b'{"type":"unknown"}\n', b'{"type":"footer"}\n',
                        (json.dumps(body[0]) + "\n").encode("utf-8"),
                        (json.dumps(header) + "\n" + json.dumps(header) + "\n").encode("utf-8")):
            trace.write_bytes(invalid)
            expect_refusal(lambda: v1_append_timeout_footer(trace, 3), "V1_TIMEOUT_TRACE_INVALID", "V1_TIMEOUT_INVALID_WITNESS")
            assert trace.read_bytes() == invalid, "V1_TIMEOUT_INVALID_PREFIX_WITNESS"

        pack = root / "pack"
        pack.mkdir()
        (pack / "synthetic.txt").write_bytes(b"synthetic timeout input")
        scenario = root / "S-timeout.json"
        write_json(scenario, {"id": scenario.stem, "pack": "pack", "ticks": 3, "rng": {"kind": "tape", "values": [0.5]}, "dpiScale": 1})
        stub = SimpleNamespace(validate_scenario=lambda *unused: None,
                               pack_preflight=lambda *unused: [("synthetic.txt", pack / "synthetic.txt")],
                               pack_sha256=lambda *unused: sha((pack / "synthetic.txt").read_bytes()),
                               load_trace=lambda *unused: None, Unjudgeable=type("SyntheticUnjudgeable", (Exception,), {}))
        captured = []
        for timed_out in (True, False):
            def synthetic_child(argv, *unused, **unused_keywords):
                actual_header = json.loads(Path(argv[-3]).read_text(encoding="utf-8"))
                rows = [actual_header] + body
                if not timed_out:
                    rows.append({"type": "footer", "ticks": 3, "status": "complete", "states": 2, "events": 3, "draws": 1})
                payload = ("\n".join(json.dumps(row) for row in rows) + "\n").encode("utf-8")
                Path(argv[-1]).write_bytes(payload)
                captured.append(payload)
                return {"timed_out": timed_out, "exit_code": None if timed_out else 0, "stdout": "", "stderr": ""}

            copied = FunctionType(v1_run.__code__, dict(globals(), command=synthetic_child), "v1_run")
            try:
                row = copied(SimpleNamespace(jdk_root=root / "not-used-jdk"), stub, scenario, root / str(timed_out), "", root, {}, True)
            except Exception as failure:
                raise AssertionError("V1_TIMEOUT_CONTROL_WITNESS child result handling failed: " + type(failure).__name__ + ": " + str(failure)) from failure
            physical = Path(row["trace"])
            last = json.loads(physical.read_text(encoding="utf-8").splitlines()[-1])
            assert (last["states"], last["events"], last["draws"]) == (2, 3, 1), "V1_TIMEOUT_ENTRY_WITNESS"
            assert row["status"] == ("timeout" if timed_out else "complete"), "V1_TIMEOUT_CONTROL_WITNESS"
            assert physical.read_bytes().startswith(captured[-1]), "V1_TIMEOUT_ENTRY_PREFIX_WITNESS"
        labels.append("timeout-footer-counts-physical-prefix-states-events-draws-zero-malformed-and-entrypoint")


def trace2_self_test(args, labels):
    """Original synthetic records and killed-child results; never opens the private runtime/source cache."""
    import copy
    with tempfile.TemporaryDirectory(prefix="oracle-trace2-", dir=args.temp_root) as folder:
        root = Path(folder)
        trace = root / "prefix.jsonl"
        header = {"type": "header", "schema": "shimeji-parity-trace/2", "scenario": {"id": "S-proof"},
                  "runtime": "oracle", "build": {"artifactSha256": "a" * 64},
                  "rng": {"kind": "tape", "values": [0.5]}}
        draws = [{"type": "event", "kind": "rng-draw", "tick": 1, "sid": "m1", "value": 0.5},
                 {"type": "event", "kind": "rng-draw", "tick": 2, "sid": "m1", "value": 0.5}]

        def save(path, rows):
            path.write_text("\n".join(json.dumps(row) for row in rows) + "\n", encoding="utf-8")

        for rng, expected in ((header["rng"], True), ({"kind": "tape", "values": [0.5, 0.5]}, False),
                              ({"kind": "none"}, False), ({"kind": "splitmix64", "seed": "1"}, False)):
            save(trace, [dict(header, rng=rng)] + draws)
            v1_append_timeout_footer(trace, 4)
            footer = json.loads(trace.read_text(encoding="utf-8").splitlines()[-1])
            assert footer.get("tapeExhausted") is expected, "V2_TIMEOUT_FLAG_WITNESS"
        save(trace, [header])
        before = trace.read_bytes()
        expect_refusal(lambda: v1_append_timeout_footer(trace, 4, observe=False),
                       "V2_TIMEOUT_TRACE_OFF_UNVERIFIED", "V2_TIMEOUT_OFF_TALLY_WITNESS")
        assert trace.read_bytes() == before, "V2_TIMEOUT_OFF_PREFIX_WITNESS"
        for missing in (False, True):
            trace.write_bytes(b"")
            if missing:
                trace.unlink()
            expect_refusal(lambda: v1_append_timeout_footer(trace, 4, observe=False),
                           "V2_TIMEOUT_TRACE_OFF_UNVERIFIED", "V2_TIMEOUT_OFF_EMPTY_WITNESS")
            assert (not trace.exists()) if missing else trace.read_bytes() == b"", "V2_TIMEOUT_OFF_EMPTY_PREFIX_WITNESS"
        save(trace, [dict(header, rng={"kind": "tape"})])
        expect_refusal(lambda: v1_append_timeout_footer(trace, 4),
                       "V1_TIMEOUT_TRACE_INVALID", "V2_TIMEOUT_RNG_WITNESS")
        labels.append("trace2-timeout-flag-from-physical-prefix-and-off-tally-unverified")

        pack = root / "pack"
        pack.mkdir()
        (pack / "synthetic.txt").write_bytes(b"original synthetic fixture")
        scenario = root / "S-timeout-local.json"
        write_json(scenario, {"id": scenario.stem, "pack": "pack", "ticks": 4, "rng": header["rng"], "dpiScale": 1})
        stub = SimpleNamespace(validate_scenario=lambda *unused: None,
                               pack_preflight=lambda *unused: [("synthetic.txt", pack / "synthetic.txt")],
                               pack_sha256=lambda *unused: sha((pack / "synthetic.txt").read_bytes()))
        payload = b""
        child_timeout = True

        def child(argv, *unused, **unused_keywords):
            off = argv[-1] == "off"
            output = Path(argv[-2] if off else argv[-1])
            output.write_bytes(payload)
            return {"timed_out": child_timeout, "exit_code": None if child_timeout else 0, "stderr": "", "stdout": ""}

        copied = FunctionType(v1_run.__code__, dict(globals(), command=child,
                              validate_shared_output=lambda *unused: None), "v1_run")
        for i, records in enumerate(([header], [header, {"type": "footer", "status": "complete"}])):
            payload = ("\n".join(json.dumps(row) for row in records) + "\n").encode("utf-8")
            if i == 0:
                payload += b"{"
            try:
                row = copied(SimpleNamespace(jdk_root=root / "unused"), stub, scenario, root / ("run" + str(i)), "", root, {}, True)
            except Refusal as failure:
                raise AssertionError("V2_TIMEOUT_SCENARIO_LOCAL_WITNESS " + str(failure)) from failure
            assert row["status"] == "timeout" and row["timeout_footer_verified"] is False, "V2_TIMEOUT_SCENARIO_LOCAL_WITNESS"
            assert row["detail"].startswith("V1_TIMEOUT_TRACE_INVALID "), "V2_TIMEOUT_SCENARIO_DETAIL_WITNESS"
            assert Path(row["trace"]).read_bytes() == payload, "V2_TIMEOUT_SCENARIO_PREFIX_WITNESS"
            emitted_header = json.loads((Path(row["trace"]).parent / "header.json").read_text(encoding="utf-8"))
            assert emitted_header["schema"] == "shimeji-parity-trace/2", "V2_PRODUCER_SCHEMA_WITNESS"
        payload = (json.dumps(header) + "\n").encode("utf-8")
        row = copied(SimpleNamespace(jdk_root=root / "unused"), stub, scenario, root / "off-run", "", root, {}, False)
        assert row["status"] == "timeout" and row["detail"].startswith("V2_TIMEOUT_TRACE_OFF_UNVERIFIED "), "V2_TIMEOUT_OFF_ENTRY_WITNESS"

        def unrelated(*unused, **unused_keywords):
            raise Refusal("SYNTHETIC_UNRELATED_REFUSAL")

        copied_globals = dict(copied.__globals__, v1_append_timeout_footer=unrelated)
        not_swallowed = FunctionType(v1_run.__code__, copied_globals, "v1_run")
        expect_refusal(lambda: not_swallowed(SimpleNamespace(jdk_root=root / "unused"), stub, scenario,
                                             root / "unrelated", "", root, {}, True),
                       "SYNTHETIC_UNRELATED_REFUSAL", "V2_TIMEOUT_REFUSAL_SCOPE_WITNESS")
        labels.append("trace2-timeout-prefix-refusal-is-scenario-local-and-unrelated-refusals-escape")

        on = [header] + draws + [{"type": "footer", "ticks": 4, "status": "complete", "draws": 2,
                                 "events": 2, "states": 0, "tapeExhausted": True}]
        on_path, off_path = root / "on.jsonl", root / "off.jsonl"
        save(on_path, on)
        digest = trace_event_digest(on_path)
        on[-1]["eventSequenceSha256"] = digest
        off = [copy.deepcopy(header), {"type": "footer", "ticks": 4, "status": "complete", "draws": 0,
                                      "events": 0, "states": 0, "tapeExhausted": True, "unobservedDraws": 2,
                                      "unobservedEvents": 2, "eventSequenceSha256": digest}]

        def proof(mine=on, theirs=off):
            save(on_path, mine)
            save(off_path, theirs)
            return v1_observation_proof(on_path, off_path)

        assert proof()["draws"] == 2, "V2_PROOF_POSITIVE_WITNESS"
        for which in ("on", "off", "both", "missing", "not-bool"):
            mine, theirs = copy.deepcopy(on), copy.deepcopy(off)
            if which in ("on", "both"):
                mine[-1]["tapeExhausted"] = False
            if which in ("off", "both"):
                theirs[-1]["tapeExhausted"] = False
            if which == "missing":
                theirs[-1].pop("tapeExhausted")
            if which == "not-bool":
                theirs[-1]["tapeExhausted"] = 1
            expect_refusal(lambda: proof(mine, theirs), "V2_OBSERVATION_TALLY_REFUSAL", "V2_PROOF_FLAGS_WITNESS")
        for key in ("unobservedDraws", "unobservedEvents"):
            changed = copy.deepcopy(off)
            changed[-1].pop(key)
            expect_refusal(lambda: proof(on, changed), "V2_OBSERVATION_TALLY_REFUSAL", "V2_PROOF_DURABLE_TALLY_WITNESS")
        changed = copy.deepcopy(off)
        changed[-1]["status"] = "timeout"
        changed[-1].pop("unobservedDraws")
        expect_refusal(lambda: proof(on, changed), "V1_OBSERVATION_CHANGED_BEHAVIOUR", "V2_PROOF_TIMEOUT_WITNESS")
        changed = copy.deepcopy(on)
        changed[-1]["draws"] = 3
        expect_refusal(lambda: proof(changed, off), "V2_OBSERVATION_TALLY_REFUSAL", "V2_PROOF_PHYSICAL_WITNESS")
        changed = copy.deepcopy(off)
        changed[0]["schema"] = "shimeji-parity-trace/1"
        expect_refusal(lambda: proof(on, changed), "V2_OBSERVATION_TALLY_REFUSAL", "V2_PROOF_BINDING_WITNESS")
        for kind in ("none", "splitmix64"):
            mine, theirs = copy.deepcopy(on), copy.deepcopy(off)
            mine[0]["rng"] = theirs[0]["rng"] = {"kind": kind, "seed": "1"}
            expect_refusal(lambda: proof(mine, theirs), "V2_OBSERVATION_TALLY_REFUSAL", "V2_PROOF_NON_TAPE_FLAGS_WITNESS")
            mine[-1]["tapeExhausted"] = theirs[-1]["tapeExhausted"] = False
            assert proof(mine, theirs)["draws"] == 2, "V2_PROOF_NON_TAPE_POSITIVE_WITNESS"
        save(off_path, off)

        def no_public_grade(*unused):
            raise AssertionError("V2_OFF_STATUS_WITNESS off artifact entered the public trace validator")

        off_validator = SimpleNamespace(load_trace=no_public_grade, Unjudgeable=type("SyntheticUnjudgeable", (Exception,), {}))
        off_status = v1_trace_status(off_validator, off_path, observe=False)
        assert off_status[0] == "complete" and "proof-only" in off_status[1], "V2_OFF_STATUS_WITNESS"
        changed = copy.deepcopy(off)
        changed[-1]["status"] = "unknown"
        save(off_path, changed)
        assert v1_trace_status(off_validator, off_path, observe=False)[0] == "malformed", "V2_OFF_STATUS_CLASS_WITNESS"
        child_timeout = False
        payload = ("\n".join(json.dumps(row) for row in off) + "\n").encode("utf-8")
        complete_stub = SimpleNamespace(**vars(stub), **vars(off_validator))
        row = copied(SimpleNamespace(jdk_root=root / "unused"), complete_stub, scenario,
                     root / "off-complete", "", root, {}, False)
        assert row["status"] == "complete" and row["proof_only"] is True, "V2_OFF_ENTRY_STATUS_WITNESS"
        for key in ("runtime", "build"):
            changed = copy.deepcopy(off)
            changed[0][key] = "other" if key == "runtime" else {"artifactSha256": "b" * 64}
            expect_refusal(lambda: proof(on, changed), "V2_OBSERVATION_TALLY_REFUSAL", "V2_PROOF_BUILD_WITNESS")
        labels.append("trace2-observation-proof-validates-flags-physical-draws-durable-tally-and-completion")


def png(red, green, blue):
    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xffffffff)
    rows = bytearray()
    for y in range(16):
        rows.append(0)
        for x in range(16):
            rows.extend((255, 255, 255, 255) if x == 8 or y == 15 else
                        (0, 0, 0, 255) if x < 3 and y < 4 else (red, green, blue, 255))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 16, 16, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(bytes(rows))) + chunk(b"IEND", b"")


def fixture(root):
    for name, color in (("A", (170, 40, 30)), ("B", (30, 120, 180))):
        directory = root / "img" / name
        (directory / "conf").mkdir(parents=True)
        (directory / "tile.png").write_bytes(png(*color))
        (directory / "alternate.png").write_bytes(png(color[2], color[0], color[1]))
        # Independently constructed scenarios: no upstream XML declarations are stored here.
        actions = ET.Element("Mascot")
        action_list = ET.SubElement(actions, "ActionList")

        def action(label, kind, embedded=None, **parameters):
            node = ET.SubElement(action_list, "Action", Name=label, Type=kind, **parameters)
            if embedded:
                node.set("Class", ".".join(("com", "group_finity", "mascot", "action", embedded)))
            return node

        def poses(node, entries):
            animation = ET.SubElement(node, "Animation")
            for image, duration in entries:
                ET.SubElement(animation, "Pose", Image=image + ".png", ImageAnchor="8,16", Velocity="0,0", Duration=str(duration))

        def reference(parent, label, **parameters):
            ET.SubElement(parent, "ActionReference", Name=label, **parameters)

        poses(action("Idle", "Stay"), [("tile", 10000)])
        poses(action("Intro", "Animate"), [("tile", 1), ("alternate", 1)])
        action("Look", "Embedded", "Look")
        action("Offset", "Embedded", "Offset")
        sequence = action("Sequence", "Sequence")
        reference(sequence, "Intro")
        reference(sequence, "Look", LookRight="#{mascot.anchor.x == 120}")
        reference(sequence, "Offset", X="${1+2}", Y="4")
        reference(sequence, "Idle")
        poses(action("Birth", "Embedded", "Breed", BornCount="2", BornX="7", BornY="5", BornBehavior="Child"), [("tile", 4)])
        spawn = action("Spawn", "Sequence")
        reference(spawn, "Birth")
        reference(spawn, "Idle")
        poses(action("Change", "Embedded", "Transform", TransformMascot="B", TransformBehavior="Changed"), [("tile", 3)])
        behaviors = ET.Element("Mascot")
        behavior_list = ET.SubElement(behaviors, "BehaviorList")
        for label, runs in (("SequenceFixture", "Sequence"), ("BreedFixture", "Spawn"), ("TransformFixture", "Change"),
                            ("Child", "Idle"), ("Changed", "Idle"), ("Fall", "Idle")):
            ET.SubElement(behavior_list, "Behavior", Name=label, Action=runs, Frequency="0")
        ET.ElementTree(actions).write(directory / "conf" / "actions.xml", encoding="utf-8")
        ET.ElementTree(behaviors).write(directory / "conf" / "behaviors.xml", encoding="utf-8")


def probe(args):
    source = check_source(args)
    if args.runtime == "official":
        require_passing_comparison(args)
    state, classpath, resources = runtime_inputs(args, args.runtime)
    artifact = Path(state["artifact"])
    verify(artifact.read_bytes(), state["artifact_sha256"], "probe-runtime-jar")
    root = args.output_root / (args.runtime + "-" + uuid.uuid4().hex[:8])
    root.mkdir(parents=True)
    helper = root / "probe-classes"
    helper.mkdir()
    compiler = command([args.jdk_root / "bin" / "javac.exe", "-encoding", "UTF-8", "-source", "1.8", "-target", "1.8",
                        "-classpath", classpath, "-d", helper, HERE / "FeasibilityProbe.java",
                        HERE / "draft" / "DraftPoseGeometry.java"], root)
    write_json(root / "probe-compile.json", compiler)
    require_command(compiler, "PROBE_COMPILE_FAILED " + str(root / "probe-compile.json"))
    results = []
    for scenario in ("sequence", "breed", "transform"):
        run = root / scenario
        run.mkdir()
        fixture(run)
        evidence = run / "feasibility.jsonl"
        record = command([args.jdk_root / "bin" / "java.exe", "-Dfile.encoding=UTF-8", "-Djava.io.tmpdir=" + os.environ["TEMP"],
                          "-Duser.home=" + str(run), "-cp", str(helper) + os.pathsep + classpath,
                          "com.group_finity.mascot.oracle.FeasibilityProbe", evidence, state["artifact_sha256"], scenario], run, timeout=30)
        write_json(run / "launch.json", record)
        require_command(record, "PROBE_FAILED " + scenario + ": " + str(run / "launch.json"))
        lines = [json.loads(line) for line in evidence.read_text(encoding="utf-8").splitlines()]
        if not lines or lines[-1].get("status") != "pass" or sum(line.get("record") == "step" for line in lines) != 9:
            raise Refusal("PROBE_EVIDENCE_REFUSAL " + scenario)
        results.append({"scenario": scenario, "trace": str(evidence), "sha256": sha(evidence.read_bytes()),
                        "step_signature": sha(json.dumps([line for line in lines if line.get("record") == "step"], sort_keys=True).encode("utf-8")),
                        "exit_code": record["exit_code"]})
    check_source(args)
    result = {"status": "pass", "runtime": args.runtime, "artifact_sha256": state["artifact_sha256"],
              "results": results, "probe_source_sha256": sha((HERE / "FeasibilityProbe.java").read_bytes()),
              "pose_geometry_source_sha256": sha((HERE / "draft" / "DraftPoseGeometry.java").read_bytes()),
              "tool_sha256": sha((HERE / "oracle.py").read_bytes()),
              "runtime_inputs": resources,
              "visual_capture": "not_performed", "source_unchanged": True}
    write_json(root / "results.json", result)
    write_json(args.output_root / (args.runtime + "-latest-probe.json"), result)
    return result


def validate_probe_tooling(pair, current_probe, current_tool, current_geometry):
    if (pair.get("probe_source_sha256") != current_probe or pair.get("tool_sha256") != current_tool
            or pair.get("pose_geometry_source_sha256") != current_geometry):
        raise Refusal("PROBE_VERSION_MISMATCH current tooling differs")


def check(args):
    comparison = require_passing_comparison(args)
    pairs = [json.loads((args.output_root / (label + "-latest-probe.json")).read_text(encoding="utf-8"))
             for label in ("rebuilt", "official")]
    current_probe = sha((HERE / "FeasibilityProbe.java").read_bytes())
    current_tool = sha((HERE / "oracle.py").read_bytes())
    current_geometry = sha((HERE / "draft" / "DraftPoseGeometry.java").read_bytes())
    states = [json.loads((args.cache_root / name).read_text(encoding="utf-8"))
              for name in ("latest-build.json", "distribution.json")]
    for pair, state in zip(pairs, states):
        validate_probe_tooling(pair, current_probe, current_tool, current_geometry)
        actual_runtime = sha(Path(state["artifact"]).read_bytes())
        if pair["artifact_sha256"] != actual_runtime or state["artifact_sha256"] != actual_runtime:
            raise Refusal("PROBE_RUNTIME_VERSION_MISMATCH")
        if pair.get("runtime_inputs") != comparison["runtime_inputs"][pair["runtime"]]:
            raise Refusal("PROBE_RESOURCE_VERSION_MISMATCH")
        for row in pair["results"]:
            trace = Path(row["trace"])
            verify(trace.read_bytes(), row["sha256"], "probe-evidence " + row["scenario"])
            lines = [json.loads(line) for line in trace.read_text(encoding="utf-8").splitlines()]
            if not lines or lines[0].get("runtimeSha256") != actual_runtime or lines[-1].get("status") != "pass":
                raise Refusal("PROBE_TRACE_WITNESS_MISSING " + row["scenario"])
            row["step_signature"] = sha(json.dumps([line for line in lines if line.get("record") == "step"], sort_keys=True).encode("utf-8"))
    maps = [{row["scenario"]: row["step_signature"] for row in pair["results"]} for pair in pairs]
    for scenario in ("sequence", "breed", "transform"):
        if maps[0].get(scenario) != maps[1].get(scenario) or scenario not in maps[0]:
            raise Refusal("RUNTIME_WITNESS_MISMATCH " + scenario)
    result = {"status": "pass", "directed_scenarios": sorted(maps[0]),
              "runtime_sha256": [pair["artifact_sha256"] for pair in pairs], "step_signatures": maps[0]}
    write_json(args.output_root / "behavior-comparison.json", result)
    return result


def mutate(args):
    original_bytes = (HERE / "oracle.py").read_bytes()
    original = original_bytes.decode("utf-8")
    legs = [
        ("checksum", [("if actual " + "!= expected:", "if False:")], "CHECKSUM_WITNESS"),
        ("protected-path", [("if " + "pattern:", "if False:")], "ARCHIVE_WITNESS fixture.env"),
        ("javap-difference", [('return "\\n".join(lines).strip()', 'return "ignored"')], "JAVAP_DIFFERENCE_WITNESS"),
        ("archive-containment", [
            ('if ' + 'path.is_absolute() or ".." in path.parts or any(":" in p for p in path.parts):', "if False:"),
            ('if ' + 'root.resolve() not in target.parents and target != root.resolve():', "if False:")], "ARCHIVE_WITNESS ../escape.txt"),
        ("source-integrity", [
            ('if ' + 'not target.is_file() or sha(target.read_bytes()) != expected:', 'if False:')], "SOURCE_INTEGRITY_WITNESS"),
        ("inventory-only", [('if ' + 'recorded_files != expected_files:', 'if False:')], "INVENTORY_ONLY_WITNESS"),
        ("extracted-inventory", [('if ' + 'actual_files != set(expected_files):', 'if False:')], "EXTRACTED_INVENTORY_WITNESS"),
        ("link-mode", [('if ' + '(info.external_attr >> 16) & 0o170000 == 0o120000:', 'if False:')], "ARCHIVE_LINK_WITNESS"),
        ("expanded-size", [('if ' + 'sum(info.file_size for info, _ in targets) > MAX_EXPANDED_BYTES:', 'if False:')], "ARCHIVE_SIZE_WITNESS"),
        ("distribution-pin", [('return verify(data, ' + 'DISTRIBUTION_SHA256, "official-distribution-zip")', 'return sha(data)')], "DISTRIBUTION_PIN_WITNESS"),
        ("distribution-jar-pin", [('return verify(data, ' + 'DISTRIBUTION_JAR_SHA256, "official-distribution-jar")', 'return sha(data)')], "DISTRIBUTION_JAR_PIN_WITNESS"),
        ("class-count", [('if ' + 'result.get("common_classes") != EXPECTED_CLASS_COUNT:', 'if False:')], "CLASS_COUNT_WITNESS"),
        ("class-inventory", [('if ' + 'result.get("only_rebuilt") != [] or result.get("only_official") != []:', 'if False:')], "CLASS_INVENTORY_WITNESS"),
        ("class-bytecode", [('if ' + 'result.get("instruction_or_signature_differences") != {}:', 'if False:')], "CLASS_BYTECODE_WITNESS")]
    legs.extend([
        ("pose-geometry-version", [('or pair.get("pose_geometry_source_sha256") ' + '!= current_geometry', 'or False')], "POSE_GEOMETRY_VERSION_WITNESS"),
        ("repository-storage", [('if target == repository ' + 'or repository in target.parents:', 'if False:')], "REPOSITORY_CACHE_WITNESS"),
        ("repository-data-storage", [('("data-root", ' + '"data_root"),', '("data-root", "ignored_data_root"),')], "REPOSITORY_DATA_WITNESS"),
        ("repository-temp-storage", [('("temp-root", ' + '"temp_root"),', '("temp-root", "ignored_temp_root"),')], "REPOSITORY_TEMP_WITNESS"),
        ("repository-shared-storage", [('("shared-root", ' + '"shared_root")):', '("shared-root", "ignored_shared_root")):')], "REPOSITORY_SHARED_WITNESS"),
        ("distribution-cache-writeback", [('if not ' + 'path.exists():', 'if not path.exists() and not (args.cache_root / "distribution.json").exists():')], "DISTRIBUTION_CACHE_WITNESS"),
        ("isolation-value", [('if ' + 'any(environment.get(key) != value for key, value in flags.items()):', 'if False:')], "ISOLATION_VALUE_WITNESS"),
        ("isolation-path", [('if ' + 'any(not environment.get(key) or Path(environment[key]).resolve() != Path(value).resolve() for key, value in paths.items()):', 'if False:')], "ISOLATION_PATH_WITNESS"),
        ("shared-rng", [('if ' + 'scenario.get("rng", {}).get("kind") != "none":', 'if False:')], "SHARED_RNG_WITNESS")])
    # The v1 producer's controls (lane feature/shimeji-oracle-v1).
    legs.extend([
        ("v1-build-witness", [('if not ' + 'state.get(key):', 'if False:')], "V1_BUILD_WITNESS_WITNESS"),
        ("v1-patch-tamper", [('if not patch.is_file() or sha(patch.read_bytes()) ' + '!= state["patch_sha256"]:', 'if False:')], "V1_PATCH_WITNESS"),
        ("v1-artifact", [('if sha(artifact.read_bytes()) ' + '!= state["artifact_sha256"]:', 'if False:')], "V1_ARTIFACT_WITNESS"),
        ("v1-spec-stale", [('if sha(edit_spec_bytes) ' + '!= state["edit_spec_sha256"]:', 'if False:')], "V1_SPEC_STALE_WITNESS"),
        ("v1-helper-stale", [('if sha(current.encode("utf-8")) ' + '!= row["expectedAfterSha256"]:', 'if False:')], "V1_HELPER_STALE_WITNESS"),
        ("v1-truncated", [('if footer.get("type") ' + '!= "footer":', 'if False:')], "V1_TRUNCATED_WITNESS"),
        ("v1-footer-status", [('footer.get("status") in ' + '("timeout", "aborted", "error"):', 'False:')], "V1_TIMEOUT_WITNESS"),
        ("v1-skipped", [('if missing:\n        raise Refusal("V1_SCENARIO_' + 'SKIPPED "', 'if False:\n        raise Refusal("V1_SCENARIO_SKIPPED "')], "V1_SKIPPED_WITNESS"),
        ("v1-observation", [('if final_on ' + '!= final_off:', 'if False:')], "V1_OBSERVATION_WITNESS")])
    # Review fix F4 (an equal empty final population, and both runs complete).
    legs.extend([
        ("v1-empty-final", [('if final_on ' + '!= final_off:', 'if not final_on or final_on != final_off:')], "V1_EMPTY_FINAL_WITNESS"),
        ("v1-observation-status", [('if on[-1].get("status") != "complete" or off[-1].get("status") ' + '!= "complete"', 'if False')], "V1_OBSERVATION_STATUS_WITNESS")])
    # V1Failures bound to current evidence; the proof case named and directed.
    legs.extend([
        ("v1-evidence-tool", [('if latest.get("tool_sha256") != tool_sha or latest.get("runner_sha256") ' + '!= runner_sha:', 'if False:')], "V1_EVIDENCE_TOOL_WITNESS"),
        ("v1-evidence-build", [('if build.get("artifactSha256") != state["artifact_sha256"] or build.get("patchSha256") ' + '!= state["patch_sha256"]:', 'if False:')], "V1_EVIDENCE_BUILD_WITNESS"),
        ("v1-evidence-trace", [('or sha(Path(row["trace"]).read_bytes()) ' + '!= row.get("trace_sha256"):', 'or False:')], "V1_EVIDENCE_TRACE_WITNESS"),
        ("v1-proof-kind", [('if kind not in ' + '("none", "tape"):', 'if False:')], "V1_PROOF_KIND_WITNESS")])
    # Review fixes F1 (scenario id paths) and F2 (helper inventory).
    legs.extend([
        ("v1-scenario-id", [('if not isinstance(scenario_id, str) or not V1_SCENARIO_ID.fullmatch(scenario_id) ' + 'or scenario_id.endswith("."):', 'if False:')], "V1_SCENARIO_ID_WITNESS"),
        ("v1-run-containment", [('if base != resolved and base ' + 'not in resolved.parents:', 'if False:')], "V1_CONTAINMENT_WITNESS"),
        ("v1-helper-missing", [('if missing:\n        raise Refusal("V1_HELPER_INVENTORY_' + 'REFUSAL missing "', 'if False:\n        raise Refusal("V1_HELPER_INVENTORY_REFUSAL missing "')], "V1_HELPER_MISSING_WITNESS"),
        ("v1-helper-shape", [('if name not in V1_HELPERS or row["file"] != V1_HELPER_PATH + name ' + 'or name in helpers:', 'if False:')], "V1_HELPER_SHAPE_WITNESS")])
    # Review W1-W3: the document witness's conditions.
    legs.extend([
        ("v1-document-file", [('elif parts[1] ' + '!= expected[parts[0]][0 if position == 0 else 1]:', 'elif False:')], "V1_DOCUMENT_FILE_WITNESS"),
        ("v1-document-set", [('if len(parts) != 3 ' + 'or parts[0] not in expected:', 'if False:')], "V1_DOCUMENT_SET_WITNESS"),
        ("v1-document-unlisted", [('if document ' + 'not in manifest:', 'if False:')], "V1_DOCUMENT_UNLISTED_WITNESS"),
        ("v1-document-none", [('if not any(line["actionStack"] ' + 'for line in states):', 'if False:')], "V1_DOCUMENT_NONE_WITNESS"),
        ("v1-document-baseline", [('if mine ' + '!= theirs:', 'if False:')], "V1_DOCUMENT_BASELINE_WITNESS")])
    # Review W123 F1, F2, F4: the refusal cases' expectation, the renamed baseline and the copy's final check.
    legs.extend([
        ("v1-document-refusal", [('if row["status"] != "aborted" or not detail.startswith("V1_REFUSAL ' + 'V1_REFKEY_DOCUMENT_SPELLING " + path):', 'if False:')], "V1_DOCUMENT_REFUSAL_WITNESS"),
        ("v1-document-rename", [('return key if parts is None or parts[0] ' + 'not in names else', 'return key if True else')], "V1_DOCUMENT_RENAME_WITNESS"),
        ("v1-pack-copy-merged", [('for target, digest, relative ' + 'in written:', 'for target, digest, relative in []:')], "V1_PACK_COPY_MERGE_WITNESS")])
    # ---- burndown/codex-parity ----
    legs.extend([
        ("bd-codex-parity: private-resource-hash", [('if sha(target.read_bytes()) ' + '!= digest:', 'if False:')], "V1_PRIVATE_RESOURCE_HASH_WITNESS"),
        ("bd-codex-parity: private-resource-inventory", [('if actual ' + '!= set(expected):', 'if False:')], "V1_PRIVATE_RESOURCE_INVENTORY_WITNESS"),
        ("bd-codex-parity: v1-resource-wiring", [('    validate_draft_resources(args, state)\n    source = ' + 'Path(state["source"])', '    source = Path(state["source"])')], "V1_RESOURCE_WIRING_WITNESS"),
        ("bd-codex-parity: bridge-resource-wiring", [('    validate_draft_resources(args, state)\n    artifact = ' + 'Path(state["artifact"])', '    artifact = Path(state["artifact"])')], "V1_RESOURCE_WIRING_WITNESS"),
        ("bd-codex-parity: shared-id-filename", [('if scenario_id ' + '!= expected_stem:', 'if False:')], "SHARED_ID_BINDING_WITNESS"),
        ("bd-codex-parity: shared-id-leaf", [('return v1_run_directory(root, ' + 'scenario_id)[0]', 'return root / scenario_id')], "SHARED_ID_FORMAT_WITNESS"),
        ("bd-codex-parity: failed-run-record", [('result.update(status="fail", failed_check=phase, error_type=type(failure).__name__, error=str(failure))\n        ' + 'record()', 'result.update(status="fail", failed_check=phase, error_type=type(failure).__name__, error=str(failure))\n        pass')], "V1_FAILURE_RECORD_WITNESS"),
        ("bd-codex-parity: event-sequence-digest", [('if not isinstance(on_digest, str) or re.fullmatch(r"[0-9a-f]{64}", on_digest) is None ' + 'or on_digest != off_digest:', 'if False:')], "V1_EVENT_SEQUENCE_WITNESS")])
    legs.append(("bd-codex-parity: resource-scan-errors", [(', onerror=' + 'resource_scan_failed', '')], "V1_RESOURCE_SCAN_WITNESS"))
    legs.append(("bd-codex-parity: event-digest-trace-binding", [('if on_digest ' + '!= trace_event_digest(on_trace):', 'if False:')], "V1_EVENT_TRACE_DIGEST_WITNESS"))
    legs.extend([
        ("bd-codex-parity: resource-wrapper-wiring", [('return validate_private_resources(state["source"], ' + 'inventory)', 'return inventory')], "V1_RESOURCE_BRIDGE_WITNESS"),
        ("bd-codex-parity: resource-post-run-wiring", [('        validate_draft_resources(args, state)\n        ' + 'passed = ', '        passed = ')], "V1_RESOURCE_RECHECK_WITNESS"),
        ("bd-codex-parity: shared-preflight-wiring", [('        shared_run_directory(root / "rebuilt", doc.get("id"), ' + 'path.stem)\n', '')], "SHARED_PREFLIGHT_WITNESS"),
        ("bd-codex-parity: shared-run-wiring", [('            run = shared_run_directory(root / runtime, doc["id"], ' + 'path.stem)', '            run = root / runtime / doc["id"]')], "SHARED_RUN_SITE_WITNESS"),
        ("bd-codex-parity: v1-failure-evidence-status", [('if latest.get("status") ' + '!= "pass":', 'if False:')], "V1_FAILURE_STATUS_WITNESS"),
        ("bd-codex-parity: v1-failure-evidence-empty", [('if not ' + 'complete:', 'if False:')], "V1_FAILURE_EMPTY_WITNESS"),
        ("bd-codex-parity: bridge-global-values", [('            or result.get("globalTypes") ' + '!= expected_types\n', '')], "BRIDGE_GLOBALS_WITNESS"),
        ("bd-codex-parity: bridge-result-wiring", [('    validate_bridge_witness(' + 'result)\n', '    pass\n')], "BRIDGE_GLOBALS_WITNESS")])
    legs.append(("bd-codex-parity: bridge-bound-variable-values", [(
        '            or result.get("boundGlobalTypes") != expected_bound_types\n            ' + 'or result.get("boundCustomValueObserved") != expected_bound_values\n', '')],
        "BRIDGE_BINDINGS_WITNESS"))
    legs.append(("bd-codex-parity: inactive-input-classification", [(
        'if status == "aborted" and detail.startswith("V1_REFUSAL ' + 'V1_INPUT_TARGET_ABSENT "):', 'if False:')],
        "V1_INACTIVE_INPUT_WITNESS"))
    # ---- burndown/codex-parity-2 ----
    legs.extend([
        ("bd-codex-parity-2: input-building", [('if document.get("state") ' + '== "building":', 'if False:')],
         "SYNTHETIC_BUILDING_WITNESS"),
        ("bd-codex-parity-2: input-inventory", [('if missing ' + 'or extra:', 'if False:')],
         "SYNTHETIC_MISSING_PATH_WITNESS"),
        ("bd-codex-parity-2: input-schema", [('if not isinstance(document, dict) or document.get("schema") ' + '!= SHARED_OUTPUT_SCHEMA:', 'if False:')],
         "SYNTHETIC_MANIFEST_SCHEMA_WITNESS"),
        ("bd-codex-parity-2: input-state", [('if "state" in document and document["state"] ' + '!= "ready":', 'if False:')],
         "SYNTHETIC_MANIFEST_STATE_WITNESS"),
        ("bd-codex-parity-2: input-paths", [('if not isinstance(paths, list) or not all(isinstance(path, str) ' + 'for path in paths):', 'if False:')],
         "SYNTHETIC_MANIFEST_PATHS_WITNESS"),
        ("bd-codex-parity-2: input-path-spelling", [('if (not managed or parsed.is_absolute() or ".." in parsed.parts or ":" in relative\n                    '
                                                  + 'or "\\\\" in relative or parsed.as_posix() != relative):', 'if False:')],
         "SYNTHETIC_MANIFEST_PATH_WITNESS"),
        ("bd-codex-parity-2: input-path-duplicates", [('if len(expected) ' + '!= len(paths):', 'if False:')],
         "SYNTHETIC_MANIFEST_REPEATED_WITNESS"),
        ("bd-codex-parity-2: shared-input-wiring", [('    validate_shared_output(args.shared_root)\n    source = ' + 'check_source(args)',
                                                   '    source = check_source(args)')], "SYNTHETIC_SHARED_WIRING_WITNESS"),
        ("bd-codex-parity-2: v1-input-wiring", [('        phase = "synthetic-input-set"\n        ' + 'validate_shared_output(args.shared_root)\n', '')],
         "SYNTHETIC_V1_WIRING_WITNESS"),
        ("bd-codex-parity-2: selection-input-wiring", [('def v1_scenarios(args):\n    ' + 'validate_shared_output(args.shared_root)\n',
                                                      'def v1_scenarios(args):\n')], "SYNTHETIC_SELECTION_WIRING_WITNESS"),
        ("bd-codex-parity-2: run-input-wiring", [('    validate_shared_output(scenario_path.parent)\n    raw = ' + 'scenario_path.read_bytes()',
                                                '    raw = scenario_path.read_bytes()')], "SYNTHETIC_RUN_WIRING_WITNESS")])
    # ---- burndown/codex-parity2-final ----
    legs.extend([
        ("bd-codex-parity2-final: timeout-state-count", [('counts["states"] ' + '+= 1', 'counts["states"] += 0')], "V1_TIMEOUT_STATES_WITNESS"),
        ("bd-codex-parity2-final: timeout-event-count", [('counts["events"] ' + '+= 1', 'counts["events"] += 0')], "V1_TIMEOUT_EVENTS_WITNESS"),
        ("bd-codex-parity2-final: timeout-draw-count", [('counts["draws"] ' + '+= 1', 'counts["draws"] += 0')], "V1_TIMEOUT_DRAWS_WITNESS"),
        ("bd-codex-parity2-final: timeout-entrypoint-zero-footer", [('            v1_append_timeout_footer(trace, ' + 'doc["ticks"], observe=observe)',
            '            with trace.open("a", encoding="utf-8") as stream:\n                stream.write(json.dumps({"type": "footer", "ticks": doc["ticks"], "status": "timeout", "draws": 0, "states": 0, "events": 0}) + chr(10))')], "V1_TIMEOUT_ENTRY_WITNESS"),
        ("bd-codex-parity2-final: timeout-record-separator", [('if prefix and not prefix.' + 'endswith(', 'if False and not prefix.endswith(')], "V1_TIMEOUT_SEPARATOR_WITNESS"),
        ("bd-codex-parity2-final: timeout-invalid-json", [('raise Refusal("V1_TIMEOUT_TRACE_INVALID cannot count an incomplete ' + 'or malformed JSONL prefix") from failure', 'records = []')], "V1_TIMEOUT_INVALID_WITNESS"),
        ("bd-codex-parity2-final: timeout-record-shape", [('if not isinstance(row, dict) or row.get("type") ' + 'not in ("header", "state", "event"):', 'if False:')], "V1_TIMEOUT_INVALID_WITNESS"),
        ("bd-codex-parity2-final: timeout-header-order", [('if (index == 0 and kind != "header") or ' + '(index > 0 and kind == "header"):', 'if False:')], "V1_TIMEOUT_INVALID_WITNESS")])
    # ---- burndown/codex-parity-3 ----
    legs.extend([
        ("bd-codex-parity-3: timeout-prefix-exhaustion", [('footer["tapeExhausted"] = rng["kind"] == "tape" ' + 'and counts["draws"] > len(rng["values"])',
                                                        'footer["tapeExhausted"] = False')], "V2_TIMEOUT_FLAG_WITNESS"),
        ("bd-codex-parity-3: timeout-off-no-tally", [('    if not observe:\n        raise Refusal("V2_TIMEOUT_' + 'TRACE_OFF_UNVERIFIED',
                                                   '    if False:\n        raise Refusal("V2_TIMEOUT_TRACE_OFF_UNVERIFIED')], "V2_TIMEOUT_OFF_TALLY_WITNESS"),
        ("bd-codex-parity-3: timeout-off-without-header", [('    if not observe:\n        raise Refusal("V2_TIMEOUT_' + 'TRACE_OFF_UNVERIFIED',
                                                        '    if not observe and trace.is_file() and trace.stat().st_size:\n        raise Refusal("V2_TIMEOUT_TRACE_OFF_UNVERIFIED')], "V2_TIMEOUT_OFF_EMPTY_WITNESS"),
        ("bd-codex-parity-3: timeout-rng-shape", [('raise Refusal("V1_TIMEOUT_TRACE_INVALID missing declared RNG ' + 'for prefix exhaustion")', 'rng = {"kind": "none"}')], "V2_TIMEOUT_RNG_WITNESS"),
        ("bd-codex-parity-3: timeout-scenario-local", [('            timeout_unverified = ' + 'str(failure)', '            raise')], "V2_TIMEOUT_SCENARIO_LOCAL_WITNESS"),
        ("bd-codex-parity-3: timeout-refusal-scope", [('            if not str(failure).startswith((' + '"V1_TIMEOUT_TRACE_INVALID ", "V2_TIMEOUT_TRACE_OFF_UNVERIFIED ")):',
                                                    '            if False:')], "V2_TIMEOUT_REFUSAL_SCOPE_WITNESS"),
        ("bd-codex-parity-3: proof-flags", [('type(footer.get("tapeExhausted")) is not bool ' + 'or footer["tapeExhausted"] != exhausted', 'False')], "V2_PROOF_FLAGS_WITNESS"),
        ("bd-codex-parity-3: proof-durable-tally", [('type(off[-1].get(key)) is not int ' + 'or off[-1][key] < 0', 'False')], "V2_PROOF_DURABLE_TALLY_WITNESS"),
        ("bd-codex-parity-3: proof-physical-counts", [('on[-1].get("draws") != actual_draws ' + 'or on[-1].get("events") != actual_events', 'False')], "V2_PROOF_PHYSICAL_WITNESS"),
        ("bd-codex-parity-3: proof-binding", [('on[0].get("schema") != "shimeji-parity-trace/2" ' + 'or off[0].get("schema") != "shimeji-parity-trace/2"', 'False')], "V2_PROOF_BINDING_WITNESS")])
    legs.extend([
        ("bd-codex-parity-3: producer-schema", [('V1_TRACE_SCHEMA = ' + '"shimeji-parity-trace/2"', 'V1_TRACE_SCHEMA = "shimeji-parity-trace/1"')], "V2_PRODUCER_SCHEMA_WITNESS"),
        ("bd-codex-parity-3: proof-entrypoint", [('        v2_observation_tally(' + 'on, off)', '        pass')], "V2_PROOF_FLAGS_WITNESS"),
        ("bd-codex-parity-3: proof-off-status", [('    if not observe:\n        # This is only ' + "the child's completion status", '    if False:\n        # This is only the child\'s completion status')], "V2_OFF_STATUS_WITNESS"),
        ("bd-codex-parity-3: proof-build-binding", [('or on[0].get("build") ' + '!= off[0].get("build")', 'or False')], "V2_PROOF_BUILD_WITNESS"),
        ("bd-codex-parity-3: proof-runtime-binding", [('or on[0].get("runtime") ' + '!= off[0].get("runtime")', 'or False')], "V2_PROOF_BUILD_WITNESS")])
    legs.append(("bd-codex-parity-3: proof-off-status-class", [('        if footer.get("status") ' + '!= "complete":', '        if False:')], "V2_OFF_STATUS_CLASS_WITNESS"))
    legs.append(("bd-codex-parity-3: proof-off-run-status", [('                if off["status"] ' + '!= "complete":', '                if False:')], "V2_PROOF_OFF_RUN_WITNESS"))
    if args.only:
        legs = [leg for leg in legs if args.only in leg[0]]
        if not legs:
            raise Refusal("MUTATION_SELECTION_EMPTY " + args.only)
    results = []
    with tempfile.TemporaryDirectory(prefix="oracle-mutations-", dir=os.environ["TEMP"]) as folder:
        root = Path(folder)
        shutil.copyfile(HERE / "source-pin.json", root / "source-pin.json")
        for label, replacements, witness in legs:
            mutated = original
            for old, new in replacements:
                if mutated.count(old) != 1:
                    raise Refusal("MUTATION_PATTERN_REFUSAL " + label)
                mutated = mutated.replace(old, new)
            script = root / (re.sub(r"[^A-Za-z0-9._-]", "_", label) + ".py")
            script.write_bytes(mutated.encode("utf-8"))
            executed_sha256 = sha(script.read_bytes())
            executed_mtime_ns = script.stat().st_mtime_ns
            record = command([sys.executable, "-I", "-B", "-X", "utf8", script, "selftest", "--cache-root", args.cache_root,
                              "--output-root", args.output_root, "--jdk-root", args.jdk_root,
                              "--data-root", args.data_root, "--temp-root", args.temp_root], root)
            if record["exit_code"] != 1 or record["stderr"].count(witness) != 1:
                raise Refusal("MUTATION_DID_NOT_FIRE " + label)
            results.append({"mutation": label, "status": "fired", "witness": witness, "exit_code": 1,
                            "executed_script": str(script), "source_sha256": executed_sha256,
                            "source_mtime_ns": executed_mtime_ns, "pid": record["pid"], "process_exited": record["process_exited"]})
    if root.exists():
        raise Refusal("MUTATION_SCRATCH_REMAINS")
    if (HERE / "oracle.py").read_bytes() != original_bytes:
        raise Refusal("MUTATION_TRACKED_SOURCE_CHANGED")
    result = {"status": "pass", "mutations": results, "tracked_source_unchanged": True,
              "baseline_source_sha256": sha(original_bytes), "source_pin_sha256": sha((HERE / "source-pin.json").read_bytes()),
              "removed_scratch": str(root), "scratch_exists": root.exists()}
    write_json(args.output_root / "mutation-results.json", result)
    return result


def validate_bridge_witness(result):
    # typeof always returns a string, including "undefined". Require the pinned values, not just that JS ran.
    expected_types = {name: "object" for name in ("engine", "context", "arguments")}
    expected_bound_types = {"engine": "number", "context": "number", "arguments": "object"}
    expected_bound_values = {"engine": True, "context": True, "arguments": False}
    if (result.get("status") != "pass" or result.get("scriptDraws") != 4
            or result.get("globalTypes") != expected_types
            or result.get("boundGlobalTypes") != expected_bound_types
            or result.get("boundCustomValueObserved") != expected_bound_values
            or result.get("unusedReadsOff") != 0 or result.get("unusedReadsOn") != 0):
        raise Refusal("BRIDGE_WITNESS_MISSING")


def bridge(args):
    state = validate_v1_build(json.loads((args.cache_root / "latest-draft.json").read_text(encoding="utf-8")),
                              (HERE / "edit-spec.json").read_bytes(), HERE / "draft")
    validate_draft_resources(args, state)
    artifact = Path(state["artifact"])
    verify(artifact.read_bytes(), state["artifact_sha256"], "draft-runtime")
    root = args.output_root / ("bridge-" + uuid.uuid4().hex[:8])
    root.mkdir(parents=True)
    classes = root / "classes"
    classes.mkdir()
    source = Path(state["source"])
    classpath = str(artifact) + os.pathsep + str(source / "conf") + os.pathsep + str(source / "lib" / "*")
    compiler = command([args.jdk_root / "bin" / "javac.exe", "-encoding", "UTF-8", "-source", "1.8", "-target", "1.8",
                        "-classpath", classpath, "-d", classes, HERE / "DraftBridgeProbe.java"], root)
    write_json(root / "compile.json", compiler)
    require_command(compiler, "BRIDGE_PROBE_COMPILE_FAILED " + str(root / "compile.json"))
    record = command([args.jdk_root / "bin" / "java.exe", "-Djava.awt.headless=true", "-Dfile.encoding=UTF-8", "-cp",
                      str(classes) + os.pathsep + classpath, "com.group_finity.mascot.parity.DraftBridgeProbe"], root, timeout=30)
    write_json(root / "launch.json", record)
    require_command(record, "BRIDGE_PROBE_FAILED " + str(root / "launch.json"))
    result = json.loads(record["stdout"].strip())
    validate_bridge_witness(result)
    result.update({"draft_artifact_sha256": state["artifact_sha256"], "evidence": str(root / "launch.json")})
    write_json(args.output_root / "bridge-latest.json", result)
    return result


def failures(args):
    # These witnesses exercise the real unchanged classes, not a replayed success log.
    check(args)
    state = json.loads((args.cache_root / "latest-build.json").read_text(encoding="utf-8"))
    source = check_source(args)
    root = args.output_root / ("failure-witnesses-" + uuid.uuid4().hex[:8])
    root.mkdir(parents=True)
    helper = root / "classes"
    helper.mkdir()
    classpath = state["artifact"] + os.pathsep + str(source / "lib" / "*") + os.pathsep + str(source / "conf")
    compiler = command([args.jdk_root / "bin" / "javac.exe", "-encoding", "UTF-8", "-classpath", classpath,
                        "-d", helper, HERE / "FeasibilityProbe.java", HERE / "draft" / "DraftPoseGeometry.java"], root)
    write_json(root / "compile.json", compiler)
    require_command(compiler, "FAILURE_WITNESS_COMPILE_FAILED " + str(root / "compile.json"))
    results = []
    for label, digest, offset, witness in (("wrong-offset", state["artifact_sha256"], "4", "sequence-anchor"),
                                           ("wrong-runtime", "0" * 64, "3", "expected-runtime-artifact")):
        run = root / label
        run.mkdir()
        fixture(run)
        record = command([args.jdk_root / "bin" / "java.exe", "-Dfile.encoding=UTF-8", "-Djava.io.tmpdir=" + os.environ["TEMP"],
                          "-Duser.home=" + str(run), "-cp", str(helper) + os.pathsep + classpath,
                          "com.group_finity.mascot.oracle.FeasibilityProbe", run / "trace.jsonl", digest, "sequence", offset], run, timeout=30)
        write_json(run / "launch.json", record)
        if record["exit_code"] != 1 or record["stderr"].count("FeasibilityProbe:" + witness) != 1 or record["timed_out"]:
            raise Refusal("FAILURE_WITNESS_DID_NOT_FIRE " + label)
        results.append({"mutation": label, "witness": "FeasibilityProbe:" + witness, "exit_code": 1,
                        "pid": record["pid"], "process_exited": record["process_exited"]})
    indexes = [json.loads((args.output_root / (label + "-latest-probe.json")).read_text(encoding="utf-8"))
               for label in ("rebuilt", "official")]
    for label in ("stale-tool", "changed-step"):
        run = root / label
        run.mkdir()
        copies = json.loads(json.dumps(indexes))
        if label == "stale-tool":
            copies[0]["tool_sha256"] = "0" * 64
            witness = "PROBE_VERSION_MISMATCH"
        else:
            row = next(item for item in copies[1]["results"] if item["scenario"] == "sequence")
            records = [json.loads(line) for line in Path(row["trace"]).read_text(encoding="utf-8").splitlines()]
            next(line for line in records if line.get("record") == "step" and line["step"] == 0)["states"][0]["x"] += 1
            trace = run / "changed-trace.jsonl"
            trace.write_text("\n".join(json.dumps(line) for line in records) + "\n", encoding="utf-8")
            row["trace"] = str(trace)
            row["sha256"] = sha(trace.read_bytes())
            # Leave its stored signature unchanged: check() must recompute it.
            witness = "RUNTIME_WITNESS_MISMATCH sequence"
        for name, value in zip(("rebuilt", "official"), copies):
            write_json(run / (name + "-latest-probe.json"), value)
        write_json(run / "comparison" / "comparison.json", require_passing_comparison(args))
        altered_args = SimpleNamespace(**vars(args))
        altered_args.output_root = run
        try:
            check(altered_args)
        except Refusal as error:
            if not str(error).startswith(witness):
                raise Refusal("WRONG_FAILURE_WITNESS " + label + ": " + str(error))
            results.append({"mutation": label, "witness": witness, "status": "fired"})
        else:
            raise Refusal("FAILURE_WITNESS_DID_NOT_FIRE " + label)
    result = {"status": "pass", "mutations": results, "evidence": str(root)}
    write_json(args.output_root / "failure-witnesses-latest.json", result)
    return result


SHARED_OUTPUT_MANIFEST = ".make-packs-manifest.json"
SHARED_OUTPUT_SCHEMA = "shimeji-parity-pack-output/1"


def validate_shared_output(root):
    """A generator journal owns the whole synthetic input set, not just the selected scenario's files."""
    root = Path(root)
    manifest = root / SHARED_OUTPUT_MANIFEST
    if not manifest.exists() and not manifest.is_symlink():
        # Independently authored synthetic fixtures have no generator journal and remain valid inputs.
        return

    def linked(path):
        return path.is_symlink() or getattr(path.lstat(), "st_file_attributes", 0) & 0x400

    try:
        if linked(manifest):
            raise Refusal("SYNTHETIC_INPUT_MANIFEST_INVALID linked manifest")
        document = json.loads(manifest.read_text(encoding="utf-8"))
        if not isinstance(document, dict) or document.get("schema") != SHARED_OUTPUT_SCHEMA:
            raise Refusal("SYNTHETIC_INPUT_MANIFEST_INVALID schema")
        if document.get("state") == "building":
            raise Refusal("SYNTHETIC_INPUT_BUILDING regenerate the interrupted synthetic input set")
        # Earlier completed journals omitted state. They remain usable only after the same complete-inventory check.
        if "state" in document and document["state"] != "ready":
            raise Refusal("SYNTHETIC_INPUT_MANIFEST_INVALID state")
        paths = document.get("paths")
        if not isinstance(paths, list) or not all(isinstance(path, str) for path in paths):
            raise Refusal("SYNTHETIC_INPUT_MANIFEST_INVALID paths")
        expected = set(paths)
        for relative in paths:
            parsed = PurePosixPath(relative)
            managed = (relative == "packs" or relative.startswith("packs/")
                       or ("/" not in relative and relative.startswith("S-") and relative.endswith(".json")))
            if (not managed or parsed.is_absolute() or ".." in parsed.parts or ":" in relative
                    or "\\" in relative or parsed.as_posix() != relative):
                raise Refusal("SYNTHETIC_INPUT_MANIFEST_INVALID path " + relative)
        if len(expected) != len(paths):
            raise Refusal("SYNTHETIC_INPUT_MANIFEST_INVALID repeated path")
        actual = set()

        def remember(path):
            relative = path.relative_to(root).as_posix()
            if linked(path):
                raise Refusal("SYNTHETIC_INPUT_MANIFEST_INVALID linked input " + relative)
            actual.add(relative)

        def unreadable(error):
            raise Refusal("SYNTHETIC_INPUT_MANIFEST_INVALID unreadable input " + str(error)) from error

        packs = root / "packs"
        if packs.exists() or packs.is_symlink():
            remember(packs)
            if not packs.is_dir():
                raise Refusal("SYNTHETIC_INPUT_MANIFEST_INVALID packs is not a directory")
            for folder, directories, files in os.walk(packs, followlinks=False, onerror=unreadable):
                for name in directories + files:
                    remember(Path(folder) / name)
        for scenario in root.glob("S-*.json"):
            remember(scenario)
            if not scenario.is_file():
                raise Refusal("SYNTHETIC_INPUT_MANIFEST_INVALID scenario is not a file")
        missing, extra = expected - actual, actual - expected
        if missing or extra:
            detail = "missing " + sorted(missing)[0] if missing else "unlisted " + sorted(extra)[0]
            raise Refusal("SYNTHETIC_INPUT_INCOMPLETE " + detail)
    except (OSError, UnicodeError, json.JSONDecodeError) as failure:
        raise Refusal("SYNTHETIC_INPUT_MANIFEST_INVALID " + str(failure)) from failure


def shared(args):
    """Additional provisional witnesses using the peer's exact synthetic inputs."""
    validate_shared_output(args.shared_root)
    source = check_source(args)
    root = args.output_root / ("shared-" + uuid.uuid4().hex[:8])
    scenarios = []
    for name in ("S-slice-stand-walk-01", "S-slice-transform-01", "S-slice-breed-01"):
        path = args.shared_root / (name + ".json")
        doc = json.loads(path.read_text(encoding="utf-8"))
        validate_shared_rng(doc)
        shared_run_directory(root / "rebuilt", doc.get("id"), path.stem)
        pack = member_target(args.shared_root, zipfile.ZipInfo(doc["pack"]))
        files = []
        for directory, dirs, names in os.walk(pack, followlinks=False):
            for leaf in dirs + names:
                item = Path(directory) / leaf
                relative = item.relative_to(pack).as_posix()
                if protected_member(relative) or item.is_symlink():
                    raise Refusal("SHARED_INPUT_PATH_REFUSAL " + relative)
                if item.is_file():
                    files.append((item, relative, sha(item.read_bytes())))
        scenarios.append((path, doc, files))
    results = []
    require_passing_comparison(args)
    root.mkdir(parents=True)
    for runtime, state_name in (("rebuilt", "latest-build.json"), ("official", "distribution.json")):
        state, classpath, resources = runtime_inputs(args, runtime)
        verify(Path(state["artifact"]).read_bytes(), state["artifact_sha256"], "shared-runtime")
        classes = root / runtime / "classes"
        classes.mkdir(parents=True)
        compiler = command([args.jdk_root / "bin" / "javac.exe", "-encoding", "UTF-8", "-classpath", classpath,
                            "-d", classes, HERE / "SharedPackProbe.java"], root)
        write_json(root / runtime / "compile.json", compiler)
        require_command(compiler, "SHARED_COMPILE_FAILED " + str(root / runtime / "compile.json"))
        for path, doc, files in scenarios:
            run = shared_run_directory(root / runtime, doc["id"], path.stem)
            run.mkdir()
            for item, relative, digest in files:
                target = run / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                payload = item.read_bytes()
                verify(payload, digest, "shared-input " + relative)
                target.write_bytes(payload)
            trace = run / "feasibility.jsonl"
            launch = command([args.jdk_root / "bin" / "java.exe", "-Dfile.encoding=UTF-8", "-Djava.io.tmpdir=" + os.environ["TEMP"],
                              "-Duser.home=" + str(run), "-cp", str(classes) + os.pathsep + classpath,
                              "com.group_finity.mascot.oracle.SharedPackProbe", trace, state["artifact_sha256"], path], run, timeout=30)
            write_json(run / "launch.json", launch)
            require_command(launch, "SHARED_PROBE_FAILED " + doc["id"] + ": " + str(run / "launch.json"))
            records = [json.loads(line) for line in trace.read_text(encoding="utf-8").splitlines()]
            steps = [row for row in records if row.get("record") == "step"]
            if len(steps) != doc["ticks"] + 1 or records[-1].get("status") != "pass":
                raise Refusal("SHARED_TRACE_REFUSAL " + doc["id"])
            results.append({"runtime": runtime, "scenario": doc["id"], "scenario_sha256": sha(path.read_bytes()),
                            "pack_manifest_sha256": sha(json.dumps(sorted((relative, digest) for _, relative, digest in files)).encode("utf-8")),
                            "artifact_sha256": state["artifact_sha256"], "trace": str(trace), "trace_sha256": sha(trace.read_bytes()),
                            "runtime_inputs": resources,
                            "step_signature": sha(json.dumps(steps, sort_keys=True).encode("utf-8")), "steps": len(steps)})
    for name in (doc["id"] for _, doc, _ in scenarios):
        pair = [row for row in results if row["scenario"] == name]
        if pair[0]["step_signature"] != pair[1]["step_signature"]:
            raise Refusal("SHARED_RUNTIME_MISMATCH " + name)
    result = {"status": "pass", "schema": "oracle-feasibility/0", "results": results,
              "driver_sha256": sha((HERE / "SharedPackProbe.java").read_bytes()),
              "tool_sha256": sha((HERE / "oracle.py").read_bytes()),
              "limitations": "Provisional state witnesses; RNG calls are not intercepted or counted. Not a shared-protocol parity verdict."}
    write_json(root / "results.json", result)
    write_json(args.output_root / "shared-latest.json", result)
    return result


# ------------------------------------------------------------------ CONTRACT v1 producer

V1_RUNNER = "V1ScenarioRunner.java"
V1_TIMEOUT = 120
V1_TRACE_SCHEMA = "shimeji-parity-trace/2"
V1_MAIN_CLASS = "com.group_finity.mascot.parity.V1ScenarioRunner"
V1_HELPERS = ("DraftParityHooks.java", "DraftTraceWriter.java", "NativeFactoryImpl.java", "ScenarioEnvironment.java")
V1_HELPER_PATH = "src/com/group_finity/mascot/parity/"


def comparer():
    """The repository's comparer: its trace checks classify an oracle trace, its pack manifest binds the header."""
    spec = importlib.util.spec_from_file_location("parity_comparer", HERE.parent / "comparer" / "compare.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def validate_v1_build(state, edit_spec_bytes, helper_root):
    """The draft build a v1 trace binds, re-hashed now: its record, JAR, private patch and the helpers it compiled."""
    for key in ("artifact", "artifact_sha256", "patch", "patch_sha256", "source", "edit_spec_sha256", "file_witnesses"):
        if not state.get(key):
            raise Refusal("V1_BUILD_WITNESS_MISSING " + key)
    artifact = Path(state["artifact"])
    if not artifact.is_file():
        raise Refusal("V1_BUILD_WITNESS_MISSING artifact file")
    if sha(artifact.read_bytes()) != state["artifact_sha256"]:
        raise Refusal("V1_ARTIFACT_CHANGED " + str(artifact))
    patch = Path(state["patch"])
    if not patch.is_file() or sha(patch.read_bytes()) != state["patch_sha256"]:
        raise Refusal("V1_PATCH_TAMPERED " + str(patch))
    if sha(edit_spec_bytes) != state["edit_spec_sha256"]:
        raise Refusal("V1_DRAFT_STALE edit-spec.json changed since the draft build")
    witnesses = json.loads(Path(state["file_witnesses"]).read_text(encoding="utf-8"))
    # Every new file of the private patch is one of the helpers, at its own path, exactly once; an empty or partial
    # inventory would otherwise exempt the helpers it leaves out (review F2).
    helpers = {}
    for row in witnesses:
        name = PurePosixPath(row["file"]).name
        if row["beforeSha256"] is None:
            if name not in V1_HELPERS or row["file"] != V1_HELPER_PATH + name or name in helpers:
                raise Refusal("V1_HELPER_INVENTORY_REFUSAL unexpected or repeated " + row["file"])
            helpers[name] = row
    missing = [name for name in V1_HELPERS if name not in helpers]
    if missing:
        raise Refusal("V1_HELPER_INVENTORY_REFUSAL missing " + ",".join(missing))
    for name, row in helpers.items():
        current = (helper_root / name).read_bytes().decode("utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")
        if sha(current.encode("utf-8")) != row["expectedAfterSha256"]:
            raise Refusal("V1_DRAFT_STALE helper " + name)
    return state


def validate_private_resources(source, inventory):
    """Bind the draft's runtime resources to the already archive-verified source inventory.

    The draft JAR's hash does not cover its external libraries or configuration. Record current hashes only after
    checking the exact inventory, so a replaced dependency cannot become its own new provenance witness.
    """
    source = Path(source)
    expected = {name: digest for name, digest in inventory.items() if name.startswith(("lib/", "conf/"))}
    if not any(name.startswith("lib/") and name.endswith(".jar") for name in expected) or not any(
            name.startswith("conf/") for name in expected):
        raise Refusal("V1_RESOURCE_INVENTORY_REFUSAL missing library or configuration inventory")
    actual = set()
    def resource_scan_failed(failure):
        raise Refusal("V1_RESOURCE_SCAN_REFUSAL " + str(failure)) from failure

    for leaf in ("lib", "conf"):
        directory = source / leaf
        if not directory.is_dir() or directory.is_symlink() or getattr(directory.lstat(), "st_file_attributes", 0) & 0x400:
            raise Refusal("V1_RESOURCE_INVENTORY_REFUSAL missing or linked " + leaf)
        for folder, dirs, names in os.walk(directory, followlinks=False, onerror=resource_scan_failed):
            for name in dirs + names:
                item = Path(folder) / name
                relative = item.relative_to(source).as_posix()
                if item.is_symlink() or getattr(item.lstat(), "st_file_attributes", 0) & 0x400:
                    raise Refusal("V1_RESOURCE_INVENTORY_REFUSAL linked " + relative)
                if item.is_file():
                    actual.add(relative)
    if actual != set(expected):
        raise Refusal("V1_RESOURCE_INVENTORY_REFUSAL private resource files differ from the pinned inventory")
    for relative, digest in sorted(expected.items()):
        target = source.joinpath(*PurePosixPath(relative).parts)
        if sha(target.read_bytes()) != digest:
            raise Refusal("V1_RESOURCE_CHANGED " + relative)
    return expected


def validate_draft_resources(args, state):
    # check_source derives the inventory from the pinned archive and verifies the original extraction. The private
    # patched tree is a different directory, and its lib/conf bytes must independently match that inventory.
    check_source(args)
    inventory = json.loads((args.cache_root / "source-inventory.json").read_text(encoding="utf-8"))["files"]
    return validate_private_resources(state["source"], inventory)


def v1_append_timeout_footer(trace, ticks, observe=True):
    """The killed child may have flushed a valid prefix; footer counts must describe those actual records."""
    # An off JVM may be killed before even its header is written. Its mode is an invocation fact, not something
    # that can safely be inferred from the available prefix; no durable off tally means no manufactured footer.
    if not observe:
        raise Refusal("V2_TIMEOUT_TRACE_OFF_UNVERIFIED no durable unobserved draw tally")
    prefix = trace.read_bytes() if trace.is_file() else b""
    try:
        records = [json.loads(line) for line in prefix.decode("utf-8").splitlines() if line.strip()]
    except (UnicodeError, json.JSONDecodeError) as failure:
        raise Refusal("V1_TIMEOUT_TRACE_INVALID cannot count an incomplete or malformed JSONL prefix") from failure
    counts = {"draws": 0, "states": 0, "events": 0}
    for index, row in enumerate(records):
        if not isinstance(row, dict) or row.get("type") not in ("header", "state", "event"):
            raise Refusal("V1_TIMEOUT_TRACE_INVALID unexpected record or existing footer")
        kind = row["type"]
        if (index == 0 and kind != "header") or (index > 0 and kind == "header"):
            raise Refusal("V1_TIMEOUT_TRACE_INVALID header is missing or repeated")
        if kind == "state":
            counts["states"] += 1
        elif kind == "event":
            counts["events"] += 1
            if row.get("kind") == "rng-draw":
                counts["draws"] += 1
    footer = {"type": "footer", "ticks": ticks, "status": "timeout", **counts}
    if records and records[0].get("schema") == "shimeji-parity-trace/2":
        # An off run has withheld the observations we would count. A killed JVM has no final durable tally;
        # absent draw lines cannot prove that it did not exhaust its tape (CONTRACT 10.5.3).
        rng = records[0].get("rng", {})
        if rng.get("kind") not in ("none", "tape", "splitmix64") or (
                rng.get("kind") == "tape" and not isinstance(rng.get("values"), list)):
            raise Refusal("V1_TIMEOUT_TRACE_INVALID missing declared RNG for prefix exhaustion")
        footer["tapeExhausted"] = rng["kind"] == "tape" and counts["draws"] > len(rng["values"])
    # Do not rewrite, discard, or repair prefix records. An unterminated valid final record only needs its LF.
    with trace.open("ab") as stream:
        if prefix and not prefix.endswith(b"\n"):
            stream.write(b"\n")
        stream.write((json.dumps(footer) + "\n").encode("utf-8"))
    return counts


def v1_trace_status(compare, trace, observe=True):
    """One distinct status per way a run can fail: the footer's own status, else the comparer's trace check."""
    lines = [line for line in trace.read_text(encoding="utf-8").splitlines() if line.strip()] if trace.is_file() else []
    if not lines:
        return "truncated", "no trace lines"
    try:
        footer = json.loads(lines[-1])
    except json.JSONDecodeError:
        footer = {}
    if footer.get("type") != "footer":
        return "truncated", "the last line is not a footer"
    if footer.get("type") == "footer" and footer.get("status") in ("timeout", "aborted", "error"):
        return footer["status"], "footer status " + footer["status"]
    if not observe:
        # This is only the child's completion status, never a cross-runtime verdict. Its final-only states and
        # withheld events are judged with the traced partner by v1_observation_proof, not by load_trace.
        if footer.get("status") != "complete":
            return "malformed", "unrecognized trace-off footer status"
        return "complete", "proof-only trace-off artifact; requires the paired observation proof"
    try:
        compare.load_trace(str(trace), "oracle")
    except compare.Unjudgeable as failure:
        return failure.cls, str(failure)
    return "complete", ""


def require_v1_results(requested, rows):
    produced = {row["scenario"] for row in rows}
    missing = [name for name in requested if name not in produced]
    if missing:
        raise Refusal("V1_SCENARIO_SKIPPED " + ",".join(missing))


def v1_scenarios(args):
    validate_shared_output(args.shared_root)
    paths = sorted(args.shared_root.glob("S-*.json"))
    if args.only:
        paths = [path for path in paths if any(item in path.stem for item in args.only.split(","))]
    if not paths:
        raise Refusal("V1_NO_SCENARIOS " + str(args.shared_root))
    return paths


def v1_prepare(args, root):
    state = validate_v1_build(json.loads((args.cache_root / "latest-draft.json").read_text(encoding="utf-8"))
                              if (args.cache_root / "latest-draft.json").is_file() else {},
                              (HERE / "edit-spec.json").read_bytes(), HERE / "draft")
    validate_draft_resources(args, state)
    source = Path(state["source"])
    libraries = sorted((source / "lib").glob("*.jar"))
    classpath = os.pathsep.join([state["artifact"]] + [str(path) for path in libraries] + [str(source / "conf")])
    classes = root / "runner-classes"
    classes.mkdir(parents=True)
    compiler = command([args.jdk_root / "bin" / "javac.exe", "-encoding", "UTF-8", "-source", "1.8", "-target", "1.8",
                        "-Xlint:-options", "-classpath", classpath, "-d", classes, HERE / V1_RUNNER], root)
    write_json(root / "runner-compile.json", compiler)
    require_command(compiler, "V1_RUNNER_COMPILE_FAILED " + str(root / "runner-compile.json"))
    runner = root / "v1-runner.jar"
    packed = command([args.jdk_root / "bin" / "jar.exe", "cf", runner, "-C", classes, "."], root)
    require_command(packed, "V1_RUNNER_JAR_FAILED")
    version = jdk(args)
    found = re.search(r'version "([^"]+)"', version["stderr"])
    deps = {path.name: sha(path.read_bytes()) for path in libraries}
    deps["v1-runner.jar"] = sha(runner.read_bytes())
    deps[V1_RUNNER] = sha((HERE / V1_RUNNER).read_bytes())
    build = {"sourceZipSha256": SOURCE_SHA256, "patchSha256": state["patch_sha256"], "jdk": found.group(1),
             "deps": deps, "artifactSha256": state["artifact_sha256"], "editSpecSha256": state["edit_spec_sha256"]}
    return state, classpath, runner, build


V1_SCENARIO_ID = re.compile(r"S-[A-Za-z0-9][A-Za-z0-9._-]*")


def v1_contained(root, path):
    """A derived run path must resolve to a child of the run root (a link at that name could lead elsewhere)."""
    resolved, base = Path(os.path.abspath(path)).resolve(), Path(root).resolve()
    if base != resolved and base not in resolved.parents:
        raise Refusal("V1_RUN_CONTAINMENT_REFUSAL " + str(path))
    return path


def v1_run_directory(root, scenario_id, observe=True):
    """The scenario id names exactly one leaf folder of the run root: an S- id of letters, digits, dot, underscore
    and hyphen, never a separator, a drive or rooted form, a dot name or a trailing dot (CONTRACT section 8)."""
    if not isinstance(scenario_id, str) or not V1_SCENARIO_ID.fullmatch(scenario_id) or scenario_id.endswith("."):
        raise Refusal("V1_SCENARIO_ID_REFUSAL " + repr(scenario_id))
    run = v1_contained(root, root / (scenario_id + ("" if observe else "-off")))
    return run, v1_contained(root, run / "pack")


def shared_run_directory(root, scenario_id, expected_stem):
    # Shared accepts three specifically named scenario files. Bind the document's id before any derived output
    # directory or pack copy, then apply the same leaf and physical-containment rules as the V1 producer.
    if scenario_id != expected_stem:
        raise Refusal("SHARED_SCENARIO_ID_REFUSAL id does not match the selected scenario filename")
    try:
        return v1_run_directory(root, scenario_id)[0]
    except Refusal as failure:
        raise Refusal("SHARED_SCENARIO_ID_REFUSAL " + str(failure)) from failure


def v1_copy_pack(files, workdir):
    """Copies the preflighted pack into the run folder. Each target is checked as it is written and every one again
    once all are: two entries a case-insensitive disk holds as one file each pass their own check, and leave the first
    one's name with the last one's bytes (review W123 F4). The preflight already refuses entries that differ only in
    case; this catches a fold it does not know."""
    written = []
    for relative, full in files:
        payload = Path(full).read_bytes()
        target = workdir.joinpath(*PurePosixPath(relative).parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(payload)
        verify(target.read_bytes(), sha(payload), "v1-pack-copy " + relative)
        written.append((target, sha(payload), relative))
    for target, digest, relative in written:
        verify(target.read_bytes(), digest, "v1-pack-copy-merged " + relative)


def v1_run(args, compare, scenario_path, root, classpath, runner, build, observe=True):
    validate_shared_output(scenario_path.parent)
    raw = scenario_path.read_bytes()
    doc = json.loads(raw.decode("utf-8"))
    compare.validate_scenario(doc)
    # The id is checked before any path is derived from it or anything is written (review F1).
    run, workdir = v1_run_directory(root, doc.get("id"), observe)
    pack = scenario_path.parent / doc["pack"]
    v1_copy_pack(compare.pack_preflight(str(pack)), workdir)
    header = {"type": "header", "schema": V1_TRACE_SCHEMA, "runtime": "oracle",
              "scenario": {"id": doc["id"], "sha256": sha(raw), "packSha256": compare.pack_sha256(str(pack))},
              "build": build, "rng": doc["rng"], "tickMs": 40, "dpiScale": doc["dpiScale"],
              "runId": str(uuid.uuid4()), "startedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat()}
    (run / "header.json").write_text(json.dumps(header, separators=(",", ":")) + "\n", encoding="utf-8")
    trace = run / "trace.jsonl"
    argv = [args.jdk_root / "bin" / "java.exe", "-Dfile.encoding=UTF-8", "-Djava.io.tmpdir=" + os.environ["TEMP"],
            "-Duser.home=" + str(run), "-Dshimeji.parity.enabled=true", "-Dshimeji.parity.continueAfterError=true",
            "-cp", str(runner) + os.pathsep + classpath, V1_MAIN_CLASS, run / "header.json", scenario_path, trace]
    if not observe:
        argv.append("off")
    record = command(argv, workdir, timeout=V1_TIMEOUT)
    write_json(run / "launch.json", record)
    timeout_unverified = None
    if record["timed_out"]:
        try:
            v1_append_timeout_footer(trace, doc["ticks"], observe=observe)
        except Refusal as failure:
            # Only an inability to tally this killed child's prefix is local to the scenario. Input, build,
            # containment and resource refusals still escape and fail their phase; do not catch Refusal in v1().
            if not str(failure).startswith(("V1_TIMEOUT_TRACE_INVALID ", "V2_TIMEOUT_TRACE_OFF_UNVERIFIED ")):
                raise
            timeout_unverified = str(failure)
    if timeout_unverified is not None:
        status, detail = "timeout", timeout_unverified
    else:
        status, detail = v1_trace_status(compare, trace, observe=observe)
    if status in ("aborted", "error"):
        # The runner's own refusal names itself; otherwise the first exception line is the source's.
        stderr = [line.strip() for line in record["stderr"].splitlines() if line.strip()]
        refusal = [line for line in stderr if line.startswith("V1_REFUSAL ")]
        detail = refusal[-1] if refusal else (stderr[0] if stderr else detail)
        # This one recognized driver refusal is invalid scenario input. Preserve the physical trace/1 aborted
        # footer, and keep unrelated driver refusals and source errors distinct from input validation failures.
        if status == "aborted" and detail.startswith("V1_REFUSAL V1_INPUT_TARGET_ABSENT "):
            status = "scenario-invalid"
    return {"scenario": doc["id"], "status": status, "detail": detail[:400], "trace": str(trace),
            "trace_sha256": sha(trace.read_bytes()) if trace.is_file() else None, "exit_code": record["exit_code"],
            "timed_out": record["timed_out"], "observe": observe, "proof_only": not observe,
            "timeout_footer_verified": record["timed_out"] and timeout_unverified is None}


def trace_event_digest(path):
    """Hash the actual written event records, so two identically broken digest counters cannot prove each other."""
    digest = hashlib.sha256()
    for line in Path(path).read_bytes().splitlines():
        if line.strip() and json.loads(line).get("type") == "event":
            digest.update(line + b"\n")
    return digest.hexdigest()


def v1_observation_proof(on_trace, off_trace):
    """CONTRACT section 5: final states, draw/event totals and the ordered event digest survive observation."""
    def load(path):
        return [json.loads(line) for line in Path(path).read_text(encoding="utf-8").splitlines() if line.strip()]
    on, off = load(on_trace), load(off_trace)
    ticks = on[-1]["ticks"]
    # Both runs must have reached the last tick: an off run that crashed early would otherwise show the same empty
    # final population as a run whose companions all disposed.
    if on[-1].get("status") != "complete" or off[-1].get("status") != "complete" or off[-1].get("ticks") != ticks:
        raise Refusal("V1_OBSERVATION_CHANGED_BEHAVIOUR a run did not complete all its ticks")
    if on[0].get("schema") == "shimeji-parity-trace/2" or off[0].get("schema") == "shimeji-parity-trace/2":
        v2_observation_tally(on, off)
    strip = lambda row: {k: v for k, v in row.items() if k != "velocity"}
    final_on = [strip(row) for row in on if row.get("type") == "state" and row["tick"] == ticks]
    final_off = [strip(row) for row in off if row.get("type") == "state" and row["tick"] == ticks]
    # Equal populations pass, an empty one included (every companion disposed: review F4); the traced run's own
    # checks already require each companion that stops early to have its dispose event.
    if final_on != final_off:
        raise Refusal("V1_OBSERVATION_CHANGED_BEHAVIOUR final tick differs")
    if (on[-1]["draws"], on[-1]["events"]) != (off[-1].get("unobservedDraws"), off[-1].get("unobservedEvents")):
        raise Refusal("V1_OBSERVATION_CHANGED_BEHAVIOUR draw or event totals differ")
    on_digest, off_digest = on[-1].get("eventSequenceSha256"), off[-1].get("eventSequenceSha256")
    if not isinstance(on_digest, str) or re.fullmatch(r"[0-9a-f]{64}", on_digest) is None or on_digest != off_digest:
        raise Refusal("V1_OBSERVATION_CHANGED_BEHAVIOUR event sequence differs or has no valid digest")
    if on_digest != trace_event_digest(on_trace):
        raise Refusal("V1_OBSERVATION_CHANGED_BEHAVIOUR event sequence digest does not match the traced records")
    return {"scenario": on[0]["scenario"]["id"], "final_states": len(final_on), "draws": on[-1]["draws"]}


def v2_observation_tally(on, off):
    """Proof-only trace/2 tallies never substitute for the public comparer's physical draw records."""
    if (on[0].get("schema") != "shimeji-parity-trace/2" or off[0].get("schema") != "shimeji-parity-trace/2"
            or on[0].get("rng") != off[0].get("rng") or on[0].get("scenario") != off[0].get("scenario")
            or on[0].get("runtime") != off[0].get("runtime") or on[0].get("build") != off[0].get("build")):
        raise Refusal("V2_OBSERVATION_TALLY_REFUSAL trace versions or runtime/build/scenario/RNG binding differ")
    actual_draws = sum(row.get("kind") == "rng-draw" for row in on if row.get("type") == "event")
    actual_events = sum(row.get("type") == "event" for row in on)
    if (on[-1].get("draws") != actual_draws or on[-1].get("events") != actual_events
            or off[-1].get("draws") != 0 or off[-1].get("events") != 0
            or any(row.get("type") == "event" for row in off)):
        raise Refusal("V2_OBSERVATION_TALLY_REFUSAL physical counts differ")
    for key in ("unobservedDraws", "unobservedEvents"):
        if type(off[-1].get(key)) is not int or off[-1][key] < 0:
            raise Refusal("V2_OBSERVATION_TALLY_REFUSAL missing durable " + key)
    rng = on[0].get("rng", {})
    if rng.get("kind") not in ("none", "tape", "splitmix64") or (
            rng.get("kind") == "tape" and not isinstance(rng.get("values"), list)):
        raise Refusal("V2_OBSERVATION_TALLY_REFUSAL missing declared RNG")
    exhausted = rng["kind"] == "tape" and actual_draws > len(rng["values"])
    for footer in (on[-1], off[-1]):
        if type(footer.get("tapeExhausted")) is not bool or footer["tapeExhausted"] != exhausted:
            raise Refusal("V2_OBSERVATION_TALLY_REFUSAL exhaustion flag differs from actual draws")


V1_DOCUMENT_SCENARIO = "S-slice-stand-walk-01"
# The trace-on/off proof's case, named rather than taken from whichever scenario completes first: a directed
# scenario with input, so the dispatch branch is inside the proof (review F3).
V1_PROOF_SCENARIO = "S-input-drag-01"


def v1_proof_case(shared_root):
    """The proof scenario's file, or None and the reason: CONTRACT section 5 asks for a directed (none or tape) one."""
    path = shared_root / (V1_PROOF_SCENARIO + ".json")
    if not path.is_file():
        return None, "no " + path.name
    kind = json.loads(path.read_text(encoding="utf-8")).get("rng", {}).get("kind")
    if kind not in ("none", "tape"):
        return None, "rng kind " + str(kind) + " is not directed"
    return path, None


def require_current_v1_evidence(latest, tool_sha, runner_sha, state, row):
    """V1Failures judges the evidence the CURRENT producer and build wrote, byte for byte, never a stale copy."""
    build = latest.get("build", {})
    if latest.get("tool_sha256") != tool_sha or latest.get("runner_sha256") != runner_sha:
        raise Refusal("V1_EVIDENCE_STALE oracle.py or the runner changed since the V1 run")
    if build.get("artifactSha256") != state["artifact_sha256"] or build.get("patchSha256") != state["patch_sha256"]:
        raise Refusal("V1_EVIDENCE_STALE the draft build changed since the V1 run")
    if not row.get("trace") or not Path(row["trace"]).is_file() or sha(Path(row["trace"]).read_bytes()) != row.get("trace_sha256"):
        raise Refusal("V1_EVIDENCE_STALE trace bytes differ from the run's record " + str(row.get("scenario")))


def v1_document_check(lines, expected, manifest, baseline=None):
    """Review W1: a trace's refKeys name the EXACT documents the source must choose. `expected` maps each image set
    of the pack to its (behaviours, actions) pack paths. Stack position 0 is the Behavior or BehaviorReference the
    behaviour came from, so it names the behaviours file; every later position is an action declaration or
    reference, so it names the actions file (CONTRACT section 3). A key of a set the pack lacks is refused, and a key
    naming another set's file fails its own set's expectation (the old folder prefix let any set's folder satisfy any
    key). Each expected file must be an entry of the pack's manifest, spelled as it lists it (review W3). Against the
    unmodified pack's run (`baseline`) every state line carries the same stack and refKey apart from the file.
    Returns the first problem of each kind, so one run shows every failing condition."""
    problems = []
    for name, documents in sorted(expected.items()):
        for document in documents:
            if document not in manifest:
                problems.append("V1_DOCUMENT_EXPECTATION_UNLISTED " + name + " " + document)
    states = [line for line in lines if line.get("type") == "state"]
    if not any(line["actionStack"] for line in states):
        problems.append("V1_DOCUMENT_KEY_NONE no actionStack entry")
    for line in states:
        for position, key in enumerate(line["actionStack"]):
            parts = key.split("|")
            if len(parts) != 3 or parts[0] not in expected:
                problems.append("V1_DOCUMENT_KEY_SET tick " + str(line["tick"]) + " " + line["sid"] + " " + key)
            elif parts[1] != expected[parts[0]][0 if position == 0 else 1]:
                problems.append("V1_DOCUMENT_KEY_FILE tick " + str(line["tick"]) + " " + line["sid"] + " position "
                                + str(position) + " names " + parts[1])
    if baseline is not None:
        def shape(rows):
            strip = lambda key: None if key is None else "|".join(key.split("|")[0::2])
            return [(row["tick"], row["sid"], [strip(key) for key in row["actionStack"]], strip(row["refKey"]))
                    for row in rows if row.get("type") == "state"]
        mine, theirs = shape(states), shape(baseline)
        if mine != theirs:
            at = next((i for i, (a, b) in enumerate(zip(mine, theirs)) if a != b), min(len(mine), len(theirs)))
            problems.append("V1_DOCUMENT_KEY_BASELINE state line " + str(at) + " of " + str(len(mine)) + " differs from the "
                            + "unmodified pack's (" + str(len(theirs)) + " lines)")
    first = {}
    for problem in problems:
        first.setdefault(problem.split(" ")[0], problem)
    return list(first.values())


# The document witness's fixtures, each a private copy of V1_DOCUMENT_SCENARIO's pack. The source's search
# (Main.loadConfiguration) takes conf/, then conf/<set>/, then img/<set>/conf/, a later tier's file replacing an
# earlier one's (review W2).
V1_DOCUMENT_CASES = ("baseline", "equal-content-override", "ignored-malformed-xml", "set-tier", "pack-tier", "mixed-case",
                     "directory-case", "outside-pack", "unlisted-component")


def v1_document_refusal(row, path):
    """Review W123 F1: a case the runner must refuse ends aborted, its refusal V1_REFKEY_DOCUMENT_SPELLING naming the
    path the source opened, never completes or fails another way."""
    detail = row.get("detail") or ""
    if row["status"] != "aborted" or not detail.startswith("V1_REFUSAL V1_REFKEY_DOCUMENT_SPELLING " + path):
        return ["V1_DOCUMENT_REFUSAL_MISSING run " + row["status"] + " " + detail[:160]]
    return []


def v1_renamed_sets(lines, names):
    """State lines with each key's image set renamed (`names` maps old to new); everything else unchanged."""
    def rename(key):
        parts = None if key is None else key.split("|")
        return key if parts is None or parts[0] not in names else "|".join([names[parts[0]]] + parts[1:])
    return [dict(line, actionStack=[rename(key) for key in line["actionStack"]], refKey=rename(line["refKey"]))
            if line.get("type") == "state" else line for line in lines]


def v1_document_fixture(case, pack, name, doc):
    """Lays out one case in a copy of the pack, whose one image set `name` keeps behaviors.xml and actions.xml in
    img/<set>/conf/, and in `doc`, the case's copy of the scenario. Returns the image set the keys carry, the
    (behaviours, actions) pack paths the source must choose and, for a case the runner must refuse, the path its
    refusal names."""
    chosen = pack / "img" / name / "conf"
    top = ("img/" + name + "/conf/behaviors.xml", "img/" + name + "/conf/actions.xml")
    spec = {"set": name, "files": top, "refusal": None}
    if case == "directory-case":
        # Review W123 F2: directories as well as the file. The scenario names the set in another case than its folder
        # and the set folder's conf is spelled Conf; the source's probes find both, and the keys carry the scenario's
        # set name with the pack's spelling of every path component.
        spelled = name.lower() if name.lower() != name else name.upper()
        for mascot in doc["mascots"]:
            mascot["imageSet"] = spelled
        chosen.rename(chosen.parent / "Conf")
        return dict(spec, set=spelled, files=("img/" + name + "/Conf/behaviors.xml", "img/" + name + "/Conf/actions.xml"))
    if case == "outside-pack":
        # Review W123 F1: a path that leaves the pack lexically. v1_run keeps the pack copy at <run>/pack, so this set
        # name leads the source's img/<set>/conf/ probe out of the pack and back in: it reads only the pack's own file,
        # nothing outside the run folder, and the runner refuses the key, since no pack entry spells "..". One "../"
        # is not enough: the source's img/ prefix brings the path back inside the pack, where nothing is found.
        for mascot in doc["mascots"]:
            mascot["imageSet"] = "../../pack/img/" + name
        return dict(spec, set=None, files=(), refusal="../pack/img/" + name + "/conf/")
    if case == "unlisted-component":
        # Review W123 F1: a component the source opens that no pack entry spells. Win32 drops a trailing dot, so
        # img/<set>./conf/ opens img/<set>/conf/, while the img listing holds no "<set>." entry.
        for mascot in doc["mascots"]:
            mascot["imageSet"] = name + "."
        return dict(spec, set=None, files=(), refusal="img/" + name + "./conf/")
    if case == "equal-content-override":
        for tier in (pack / "conf" / name, pack / "conf"):
            tier.mkdir(parents=True, exist_ok=True)
            for document in ("behaviors.xml", "actions.xml"):
                (tier / document).write_bytes((chosen / document).read_bytes())
    elif case == "ignored-malformed-xml":
        (chosen / "unused.xml").write_text("<Mascot><ActionList>", encoding="utf-8")
        (pack / "notes.xml").write_text("not a document <", encoding="utf-8")
    elif case == "mixed-case":
        # The source probes lower-case names; Windows finds these files anyway, and the key must spell them as the
        # pack does (review W3). The oracle runs on Windows only (java.exe), so the probe always finds them.
        (chosen / "behaviors.xml").rename(chosen / "Behaviors.xml")
        (chosen / "actions.xml").rename(chosen / "Actions.xml")
        return dict(spec, files=("img/" + name + "/conf/Behaviors.xml", "img/" + name + "/conf/Actions.xml"))
    elif case in ("set-tier", "pack-tier"):
        # The documents leave img/<set>/conf/. At conf/<set>/ an equal copy also sits at conf/, the tier below, so a
        # seam that reported the lowest tier fails as well as one that reported the highest.
        tier = pack / "conf" / name if case == "set-tier" else pack / "conf"
        tier.mkdir(parents=True, exist_ok=True)
        for document in ("behaviors.xml", "actions.xml"):
            payload = (chosen / document).read_bytes()
            (chosen / document).unlink()
            (tier / document).write_bytes(payload)
            if case == "set-tier":
                (pack / "conf" / document).write_bytes(payload)
        chosen.rmdir()
        prefix = "conf/" + name + "/" if case == "set-tier" else "conf/"
        return dict(spec, files=(prefix + "behaviors.xml", prefix + "actions.xml"))
    return spec


def v1_document_witnesses(args, compare, root, classpath, runner, build, rows=()):
    """Reviews F5 and W1-W3: refKeys name the file the source's own search chose, exactly. Private fixtures built
    from a real scenario's pack (V1_DOCUMENT_CASES) must each run complete with every key naming the files that
    case lays out (v1_document_check); the unmodified pack's run, the scenario's own row when V1 ran it, is the
    baseline every other case must match apart from the file. Every case runs before any failure is raised, and
    the cases' results are written first, so one run shows every failing case (review W4)."""
    path = args.shared_root / (V1_DOCUMENT_SCENARIO + ".json")
    if not path.is_file():
        return {"status": "unavailable", "reason": "no " + path.name}
    doc = json.loads(path.read_text(encoding="utf-8"))
    source_pack = args.shared_root / doc["pack"]
    sets = sorted(item.name for item in (source_pack / "img").iterdir() if (item / "conf").is_dir())
    if len(sets) != 1 or sorted(item.name for item in (source_pack / "img" / sets[0] / "conf").iterdir()) != ["actions.xml", "behaviors.xml"]:
        raise Refusal("V1_DOCUMENT_WITNESS " + V1_DOCUMENT_SCENARIO + "'s pack no longer keeps one image set's "
                      "behaviors.xml and actions.xml in img/<set>/conf/")
    name = sets[0]
    results, failures, baseline = [], [], None
    for case in V1_DOCUMENT_CASES:
        fixture = root / "document-fixtures" / case
        fixture.mkdir(parents=True)
        pack = fixture / doc["pack"]
        shutil.copytree(str(source_pack), str(pack))
        scenario = json.loads(json.dumps(doc))
        spec = v1_document_fixture(case, pack, name, scenario)
        # The scenario's own bytes unless the case changed it.
        (fixture / path.name).write_bytes(path.read_bytes() if scenario == doc else
                                          (json.dumps(scenario, indent=1, sort_keys=True) + "\n").encode("utf-8"))
        # Keyed by the set name the keys carry, the scenario's spelling, while the files are the pack's; the baseline's
        # keys carry the folder's spelling, so they are renamed to match (review W123 F2: keyed by the folder name, a
        # scenario spelling its set another way failed as an unknown set).
        expected = {spec["set"]: spec["files"]}
        manifest = {relative for relative, _ in compare.pack_preflight(str(pack))}
        row = next((item for item in rows if item["scenario"] == V1_DOCUMENT_SCENARIO), None) if case == "baseline" else None
        origin = "scenario row" if row is not None else "own run"
        if row is None:
            row = v1_run(args, compare, fixture / path.name, root / "document-runs" / case, classpath, runner, build)
        trace = Path(row["trace"]) if row.get("trace") else None
        lines = ([json.loads(line) for line in trace.read_text(encoding="utf-8").splitlines() if line.strip()]
                 if trace is not None and trace.is_file() else [])
        if spec["refusal"] is not None:
            problems = v1_document_refusal(row, spec["refusal"])
        elif row["status"] != "complete":
            problems = ["run " + row["status"] + " " + row.get("detail", "")[:160]]
        else:
            problems = v1_document_check(lines, expected, manifest, None if case == "baseline" or baseline is None
                                         else v1_renamed_sets(baseline, {name: spec["set"]}))
        if case == "baseline" and row["status"] == "complete":
            baseline = lines
        documents = sorted({key.split("|")[1] for line in lines if line.get("type") == "state" for key in line["actionStack"]})
        results.append({"case": case, "status": "failed" if problems else ("refused" if spec["refusal"] else "complete"),
                        "expected": list(spec["files"]) if spec["refusal"] is None else "refused at " + spec["refusal"],
                        "documents": documents, "origin": origin, "problems": problems})
        if problems:
            failures.append(case + ": " + "; ".join(problems)[:320] + " | keys name " + ",".join(documents))
    result = {"status": "fail" if failures else "pass", "scenario": V1_DOCUMENT_SCENARIO, "cases": results}
    write_json(root / "document-witnesses.json", result)
    if failures:
        raise Refusal("V1_DOCUMENT_WITNESS " + " || ".join(failures))
    return result


def v1(args):
    root = args.output_root / ("v1-" + uuid.uuid4().hex[:8])
    root.mkdir(parents=True)
    rows = []
    result = {"status": "running", "schema": V1_TRACE_SCHEMA, "results": rows,
              "tool_sha256": sha((HERE / "oracle.py").read_bytes()), "runner_sha256": sha((HERE / V1_RUNNER).read_bytes()),
              "evidence": str(root)}

    def record():
        result["counts"] = {status: sum(1 for row in rows if row["status"] == status)
                            for status in sorted({row["status"] for row in rows})}
        write_json(root / "results.json", result)
        write_json(args.output_root / "v1-latest.json", result)

    # Every phase, including preparation, has an evidence record. A failure must not leave a previous successful
    # v1-latest.json looking like the current run, or require parsing stderr to discover which check failed.
    phase = "scenario-input"
    record()
    try:
        if args.shared_root is None:
            raise Refusal("V1_NO_SCENARIOS pass --shared-root naming generated synthetic scenarios")
        phase = "synthetic-input-set"
        validate_shared_output(args.shared_root)
        phase = "source-integrity"
        check_source(args)
        compare = comparer()
        phase = "build-witness"
        state, classpath, runner, build = v1_prepare(args, root)
        result["build"] = build
        phase = "scenario-selection"
        paths = v1_scenarios(args)
        requested = [path.stem for path in paths]
        result["requested"] = requested
        for path in paths:
            phase = "scenario-run:" + path.stem
            try:
                rows.append(v1_run(args, compare, path, root, classpath, runner, build))
            except compare.Unjudgeable as failure:
                rows.append({"scenario": path.stem, "status": failure.cls, "detail": str(failure)[:400], "trace": None})
            record()
        phase = "requested-scenario-results"
        require_v1_results(requested, rows)
        phase = "observation-proof"
        proof_path, reason = v1_proof_case(args.shared_root)
        proof = {"status": "unavailable", "scenario": V1_PROOF_SCENARIO, "reason": reason}
        result["observation_proof"] = proof
        if proof_path is not None:
            traced = next((row for row in rows if row["scenario"] == V1_PROOF_SCENARIO), None)
            if traced is None:
                traced = v1_run(args, compare, proof_path, root, classpath, runner, build)
            if traced["status"] != "complete":
                proof["reason"] = "the traced run is " + traced["status"]
            else:
                off = v1_run(args, compare, proof_path, root, classpath, runner, build, observe=False)
                if off["status"] != "complete":
                    proof["reason"] = "the trace-off run is " + off["status"]
                    proof["run"] = off
                else:
                    proof = dict(v1_observation_proof(traced["trace"], off["trace"]), status="pass")
                    result["observation_proof"] = proof
        phase = "document-witnesses"
        documents = v1_document_witnesses(args, compare, root, classpath, runner, build, rows)
        result["document_witnesses"] = documents
        phase = "build-witness-recheck"
        validate_v1_build(state, (HERE / "edit-spec.json").read_bytes(), HERE / "draft")
        validate_draft_resources(args, state)
        passed = all(row["status"] == "complete" for row in rows) and documents["status"] == "pass" and proof["status"] == "pass"
        result["status"] = "pass" if passed else "incomplete"
    except Exception as failure:
        result.update(status="fail", failed_check=phase, error_type=type(failure).__name__, error=str(failure))
        record()
        raise
    record()
    return result


def v1_failures(args):
    """Failure witnesses on REAL v1 evidence, each on a private copy: the producer's run checks and the comparer's
    behaviour rules must each fail with exactly their named class. The latest V1 run supplies the traces."""
    import copy
    latest = json.loads((args.output_root / "v1-latest.json").read_text(encoding="utf-8"))
    # V1 writes running/failed records as well as successes. Never judge a refused or unfinished producer run.
    if latest.get("status") != "pass":
        raise Refusal("V1_EVIDENCE_NOT_PASSED " + str(latest.get("failed_check") or latest.get("status") or "missing status"))
    complete = {row["scenario"]: row for row in latest["results"] if row["status"] == "complete"}
    if not complete:
        raise Refusal("V1_EVIDENCE_NO_COMPLETE_SCENARIOS")
    compare = comparer()
    base_id = "S-slice-breed-01" if "S-slice-breed-01" in complete else sorted(complete)[0]
    # Bound to the current producer, draft build and helpers, and to the trace bytes the run recorded.
    current = validate_v1_build(json.loads((args.cache_root / "latest-draft.json").read_text(encoding="utf-8")),
                                (HERE / "edit-spec.json").read_bytes(), HERE / "draft")
    require_current_v1_evidence(latest, sha((HERE / "oracle.py").read_bytes()), sha((HERE / V1_RUNNER).read_bytes()),
                                current, complete[base_id])
    base = [json.loads(line) for line in Path(complete[base_id]["trace"]).read_text(encoding="utf-8").splitlines() if line.strip()]
    root = args.output_root / ("v1-failures-" + uuid.uuid4().hex[:8])
    root.mkdir(parents=True)
    results = []

    def save(lines, name):
        path = root / name
        path.write_text("\n".join(json.dumps(line) for line in lines) + "\n", encoding="utf-8")
        return path

    def fired(case, expected, actual, field=None, wanted_field=None):
        if actual != expected or field != wanted_field:
            raise Refusal("V1_FAILURE_WITNESS_DID_NOT_FIRE " + case + ": " + str(actual) + " " + str(field))
        results.append({"case": case, "expected": expected, "field": field, "status": "fired"})

    def refused(case, expected, call):
        try:
            call()
        except Refusal as failure:
            fired(case, expected, str(failure).split(" ")[0])
        else:
            fired(case, expected, "accepted")

    # The producer's checks.
    lines = copy.deepcopy(base)
    lines[0].pop("build")
    fired("missing-build-witness", "build-witness-missing", v1_trace_status(compare, save(lines, "no-build.jsonl"))[0])
    fired("truncated-trace", "truncated", v1_trace_status(compare, save(copy.deepcopy(base)[:-1], "truncated.jsonl"))[0])
    state = json.loads((args.cache_root / "latest-draft.json").read_text(encoding="utf-8"))
    tampered = root / "tampered.patch"
    tampered.write_bytes(Path(state["patch"]).read_bytes() + b"\n")
    refused("tampered-patch", "V1_PATCH_TAMPERED",
            lambda: validate_v1_build(dict(state, patch=str(tampered)), (HERE / "edit-spec.json").read_bytes(), HERE / "draft"))
    refused("missing-draft-record-field", "V1_BUILD_WITNESS_MISSING",
            lambda: validate_v1_build({k: v for k, v in state.items() if k != "patch_sha256"}, (HERE / "edit-spec.json").read_bytes(), HERE / "draft"))
    refused("skipped-scenario", "V1_SCENARIO_SKIPPED",
            lambda: require_v1_results([row["scenario"] for row in latest["results"]], latest["results"][1:]))

    # The comparer's behaviour rules, on the real trace against itself relabelled as a host trace.
    host = copy.deepcopy(base)
    host[0] = dict(host[0], runtime="host", build={"commit": "v1-failure-witness", "exeSha256": "0" * 64,
                                                   "engineSha256": "1" * 64, "converterSha256": "2" * 64})

    def judge(case, expected, mutate, wanted_field=None):
        mutated = copy.deepcopy(host)
        mutate(mutated)
        compare._refoot(mutated)
        report = compare.compare(copy.deepcopy(base), mutated)
        first = (report.get("findings") or [{}])[0]
        actual = report["verdict"] if expected == "exact" else report.get("class")
        fired(case, expected, actual, first.get("field") if wanted_field else None, wanted_field)

    states = [row for row in host if row.get("type") == "state"]
    two = next(tick for tick in sorted({row["tick"] for row in states}) if sum(row["tick"] == tick for row in states) >= 2)

    def swap(lines):
        i, j = [k for k, row in enumerate(lines) if row.get("type") == "state" and row["tick"] == two][:2]
        lines[i], lines[j] = lines[j], lines[i]

    def first_state(lines, test):
        return next(row for row in lines if row.get("type") == "state" and test(row))

    def insert_event(lines, record):
        at = next(k for k, row in enumerate(lines) if row.get("type") == "state" and row["tick"] == record["tick"])
        lines.insert(at, record)

    def drop_first(lines, kind):
        lines.remove(next(row for row in lines if row.get("kind") == kind))

    def draw_site(lines):
        next(row for row in lines if row.get("kind") == "rng-draw")["site"] = "builtin:regist-facing"

    def extra_draw(lines):
        count = sum(1 for row in lines if row.get("kind") == "rng-draw")
        last = max(row["tick"] for row in states)
        insert_event(lines, {"type": "event", "tick": last, "sid": states[0]["sid"], "kind": "rng-draw",
                             "site": "builtin:behavior-select", "occurrence": count + 1, "value": 0.5, "purpose": "witness"})

    judge("WITNESS real trace against itself", "exact", lambda lines: None)
    judge("wrong entity: two companions out of manager order", "entity-mismatch", swap)
    judge("wrong image, same position", "state-mismatch",
          lambda lines: first_state(lines, lambda row: row["pose"] is not None)["pose"].update(image="img/Witness/other.png"), "pose")
    judge("wrong action age", "state-mismatch",
          lambda lines: first_state(lines, lambda row: row["actionAge"] is not None).update(actionAge=first_state(lines, lambda row: row["actionAge"] is not None)["actionAge"] + 1), "actionAge")
    extra_error = {"type": "event", "tick": two, "sid": states[0]["sid"], "kind": "error", "message": "witness"}
    if base[0]["schema"] == "shimeji-parity-trace/2":
        extra_error["error"] = {"code": "unclassified", "lookup": None, "via": []}
    judge("extra event", "extra-event", lambda lines: insert_event(lines, extra_error))
    judge("missing event", "missing-event", lambda lines: drop_first(lines, "birth-request"))
    judge("wrong RNG use: another site", "event-mismatch", draw_site)
    judge("wrong RNG use: an extra draw", "rng-draw-count", extra_draw)
    result = {"status": "pass", "base": base_id, "base_trace_sha256": complete[base_id]["trace_sha256"],
              "tool_sha256": latest["tool_sha256"], "runner_sha256": latest["runner_sha256"],
              "artifact_sha256": current["artifact_sha256"], "witnesses": results, "evidence": str(root)}
    write_json(root / "results.json", result)
    write_json(args.output_root / "v1-failures-latest.json", result)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=("preflight", "fetch", "build", "compare", "probe", "check", "draft", "bridge", "failures", "shared", "selftest", "mutate", "v1", "v1failures"))
    parser.add_argument("--only")
    parser.add_argument("--cache-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--jdk-root", type=Path, required=True)
    parser.add_argument("--runtime", choices=("rebuilt", "official"), default="rebuilt")
    parser.add_argument("--shared-root", type=Path)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--temp-root", type=Path, required=True)
    args = parser.parse_args()
    repository = validate_storage_roots(args)
    validate_isolation(args)
    if args.action == "preflight":
        print(json.dumps({"status": "preflight-pass", "repository": str(repository), "paths": {
            name: str(getattr(args, name)) if getattr(args, name) is not None else None
            for name in ("cache_root", "output_root", "jdk_root", "shared_root", "data_root", "temp_root")}}, indent=2))
        return
    for directory in (args.output_root, args.data_root, args.temp_root):
        directory.mkdir(parents=True, exist_ok=True)
    if args.action == "fetch":
        result = {"status": "verified", "source": str(cache_source(args)), "sha256": SOURCE_SHA256}
    elif args.action == "build":
        result = build(args)
    elif args.action == "compare":
        result = compare(args)
    elif args.action == "selftest":
        result = self_test(args)
    elif args.action == "check":
        result = check(args)
    elif args.action == "mutate":
        result = mutate(args)
    elif args.action == "draft":
        spec = importlib.util.spec_from_file_location("oracle_draft_patch", HERE / "draft_patch.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        result = module.run(args, sys.modules[__name__])
    elif args.action == "bridge":
        result = bridge(args)
    elif args.action == "failures":
        result = failures(args)
    elif args.action == "shared":
        result = shared(args)
    elif args.action == "v1":
        result = v1(args)
    elif args.action == "v1failures":
        result = v1_failures(args)
    else:
        result = probe(args)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (Refusal, AssertionError, subprocess.TimeoutExpired) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
