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
SOURCE_SHA256 = "cf9e72f74d93f45e9f70a4f69cb1e56ffaedd38af2c5e5413482944f78faccff"
DISTRIBUTION_URL = "https://kilkakon.com/shimeji/shimejiee.zip"
DISTRIBUTION_SHA256 = "fee19b4003d718fab7ddd3ac6955bd4c1228845f94f8a1f9d72da12571a2a3d6"
DISTRIBUTION_JAR_SHA256 = "4e68d8dbe67ed79400056f9e52fb7d619c48d566e5a2104cc1f64c8c1276802c"
EXPECTED_CLASS_COUNT = 298
MAX_EXPANDED_BYTES = 256 * 1024 * 1024
SOURCE_ROOTS = ("src", "src_win", "src_generic", "src_virtual")
HERE = Path(__file__).resolve().parent


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
              {"type": "footer", "ticks": 2, "status": "complete", "draws": 1, "events": 3}]
        off = [on[0], dict(state_line, velocity=[5, 5]),
               {"type": "footer", "ticks": 2, "status": "complete", "unobservedDraws": 1, "unobservedEvents": 3}]

        def proof(on_rows, off_rows):
            for name, rows in (("on.jsonl", on_rows), ("off.jsonl", off_rows)):
                (root / name).write_text("\n".join(json.dumps(row) for row in rows) + "\n", encoding="utf-8")
            return lambda: v1_observation_proof(root / "on.jsonl", root / "off.jsonl")
        proof(on, off)()
        expect_refusal(proof(on, [off[0], dict(state_line, anchor=[1, 3]), off[2]]),
                       "V1_OBSERVATION_CHANGED_BEHAVIOUR final tick differs", "V1_OBSERVATION_WITNESS")
        expect_refusal(proof(on, [off[0], dict(off[2], status="error")]),
                       "V1_OBSERVATION_CHANGED_BEHAVIOUR a run did not complete", "V1_OBSERVATION_STATUS_WITNESS")
        # F4: every companion disposed before the last tick, equal on both sides: an empty final population passes.
        gone = [on[0], dict(state_line, tick=0), {"type": "event", "tick": 1, "sid": "m1", "kind": "dispose"}, on[2]]
        try:
            proof(gone, [off[0], off[2]])()
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
    # Review fixes F1 (scenario id paths) and F2 (helper inventory).
    legs.extend([
        ("v1-scenario-id", [('if not isinstance(scenario_id, str) or not V1_SCENARIO_ID.fullmatch(scenario_id) ' + 'or scenario_id.endswith("."):', 'if False:')], "V1_SCENARIO_ID_WITNESS"),
        ("v1-run-containment", [('if base != resolved and base ' + 'not in resolved.parents:', 'if False:')], "V1_CONTAINMENT_WITNESS"),
        ("v1-helper-missing", [('if missing:\n        raise Refusal("V1_HELPER_INVENTORY_' + 'REFUSAL missing "', 'if False:\n        raise Refusal("V1_HELPER_INVENTORY_REFUSAL missing "')], "V1_HELPER_MISSING_WITNESS"),
        ("v1-helper-shape", [('if name not in V1_HELPERS or row["file"] != V1_HELPER_PATH + name ' + 'or name in helpers:', 'if False:')], "V1_HELPER_SHAPE_WITNESS")])
    results = []
    with tempfile.TemporaryDirectory(prefix="oracle-mutations-", dir=os.environ["TEMP"]) as folder:
        root = Path(folder)
        for label, replacements, witness in legs:
            mutated = original
            for old, new in replacements:
                if mutated.count(old) != 1:
                    raise Refusal("MUTATION_PATTERN_REFUSAL " + label)
                mutated = mutated.replace(old, new)
            script = root / (label + ".py")
            script.write_bytes(mutated.encode("utf-8"))
            record = command([sys.executable, "-I", "-B", "-X", "utf8", script, "selftest", "--cache-root", args.cache_root,
                              "--output-root", args.output_root, "--jdk-root", args.jdk_root,
                              "--data-root", args.data_root, "--temp-root", args.temp_root], root)
            if record["exit_code"] != 1 or record["stderr"].count(witness) != 1:
                raise Refusal("MUTATION_DID_NOT_FIRE " + label)
            results.append({"mutation": label, "status": "fired", "witness": witness, "exit_code": 1})
    if root.exists():
        raise Refusal("MUTATION_SCRATCH_REMAINS")
    if (HERE / "oracle.py").read_bytes() != original_bytes:
        raise Refusal("MUTATION_TRACKED_SOURCE_CHANGED")
    result = {"status": "pass", "mutations": results, "tracked_source_unchanged": True,
              "removed_scratch": str(root), "scratch_exists": root.exists()}
    write_json(args.output_root / "mutation-results.json", result)
    return result


def bridge(args):
    state = json.loads((args.cache_root / "latest-draft.json").read_text(encoding="utf-8"))
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
    if result.get("status") != "pass" or result.get("scriptDraws") != 4:
        raise Refusal("BRIDGE_WITNESS_MISSING")
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


def shared(args):
    """Additional provisional witnesses using the peer's exact synthetic inputs."""
    source = check_source(args)
    root = args.output_root / ("shared-" + uuid.uuid4().hex[:8])
    scenarios = []
    for name in ("S-slice-stand-walk-01", "S-slice-transform-01", "S-slice-breed-01"):
        path = args.shared_root / (name + ".json")
        doc = json.loads(path.read_text(encoding="utf-8"))
        validate_shared_rng(doc)
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
            run = root / runtime / doc["id"]
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


def v1_trace_status(compare, trace):
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


def v1_run(args, compare, scenario_path, root, classpath, runner, build, observe=True):
    raw = scenario_path.read_bytes()
    doc = json.loads(raw.decode("utf-8"))
    compare.validate_scenario(doc)
    # The id is checked before any path is derived from it or anything is written (review F1).
    run, workdir = v1_run_directory(root, doc.get("id"), observe)
    pack = scenario_path.parent / doc["pack"]
    files = compare.pack_preflight(str(pack))
    for relative, full in files:
        payload = Path(full).read_bytes()
        target = workdir.joinpath(*PurePosixPath(relative).parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(payload)
        verify(target.read_bytes(), sha(payload), "v1-pack-copy " + relative)
    header = {"type": "header", "schema": "shimeji-parity-trace/1", "runtime": "oracle",
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
    if record["timed_out"]:
        # The killed JVM wrote no footer. This one names the timeout, so it is not mistaken for a truncated run.
        with trace.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps({"type": "footer", "ticks": doc["ticks"], "status": "timeout",
                                     "draws": 0, "states": 0, "events": 0}) + "\n")
    status, detail = v1_trace_status(compare, trace)
    if status in ("aborted", "error"):
        # The runner's own refusal names itself; otherwise the first exception line is the source's.
        stderr = [line.strip() for line in record["stderr"].splitlines() if line.strip()]
        refusal = [line for line in stderr if line.startswith("V1_REFUSAL ")]
        detail = refusal[-1] if refusal else (stderr[0] if stderr else detail)
    return {"scenario": doc["id"], "status": status, "detail": detail[:400], "trace": str(trace),
            "trace_sha256": sha(trace.read_bytes()) if trace.is_file() else None, "exit_code": record["exit_code"],
            "timed_out": record["timed_out"], "observe": observe}


def v1_observation_proof(on_trace, off_trace):
    """CONTRACT section 5: the trace-off run's final tick equals the trace-on run's, as do its draw and event totals."""
    def load(path):
        return [json.loads(line) for line in Path(path).read_text(encoding="utf-8").splitlines() if line.strip()]
    on, off = load(on_trace), load(off_trace)
    ticks = on[-1]["ticks"]
    # Both runs must have reached the last tick: an off run that crashed early would otherwise show the same empty
    # final population as a run whose companions all disposed.
    if on[-1].get("status") != "complete" or off[-1].get("status") != "complete" or off[-1].get("ticks") != ticks:
        raise Refusal("V1_OBSERVATION_CHANGED_BEHAVIOUR a run did not complete all its ticks")
    strip = lambda row: {k: v for k, v in row.items() if k != "velocity"}
    final_on = [strip(row) for row in on if row.get("type") == "state" and row["tick"] == ticks]
    final_off = [strip(row) for row in off if row.get("type") == "state" and row["tick"] == ticks]
    # Equal populations pass, an empty one included (every companion disposed: review F4); the traced run's own
    # checks already require each companion that stops early to have its dispose event.
    if final_on != final_off:
        raise Refusal("V1_OBSERVATION_CHANGED_BEHAVIOUR final tick differs")
    if (on[-1]["draws"], on[-1]["events"]) != (off[-1].get("unobservedDraws"), off[-1].get("unobservedEvents")):
        raise Refusal("V1_OBSERVATION_CHANGED_BEHAVIOUR draw or event totals differ")
    return {"scenario": on[0]["scenario"]["id"], "final_states": len(final_on), "draws": on[-1]["draws"]}


def v1(args):
    if args.shared_root is None:
        raise Refusal("V1_NO_SCENARIOS pass --shared-root naming generated synthetic scenarios")
    check_source(args)
    compare = comparer()
    root = args.output_root / ("v1-" + uuid.uuid4().hex[:8])
    root.mkdir(parents=True)
    state, classpath, runner, build = v1_prepare(args, root)
    requested = [path.stem for path in v1_scenarios(args)]
    rows = []
    for path in v1_scenarios(args):
        try:
            rows.append(v1_run(args, compare, path, root, classpath, runner, build))
        except compare.Unjudgeable as failure:
            rows.append({"scenario": path.stem, "status": failure.cls, "detail": str(failure)[:400], "trace": None})
    require_v1_results(requested, rows)
    proof = None
    directed = [row for row in rows if row["status"] == "complete"]
    if directed:
        first = next(path for path in v1_scenarios(args) if path.stem == directed[0]["scenario"])
        off = v1_run(args, compare, first, root, classpath, runner, build, observe=False)
        proof = v1_observation_proof(directed[0]["trace"], off["trace"])
    validate_v1_build(state, (HERE / "edit-spec.json").read_bytes(), HERE / "draft")
    result = {"status": "pass" if all(row["status"] == "complete" for row in rows) else "incomplete",
              "schema": "shimeji-parity-trace/1", "build": build, "results": rows, "observation_proof": proof,
              "counts": {status: sum(1 for row in rows if row["status"] == status) for status in sorted({row["status"] for row in rows})},
              "tool_sha256": sha((HERE / "oracle.py").read_bytes()), "runner_sha256": sha((HERE / V1_RUNNER).read_bytes()),
              "evidence": str(root)}
    write_json(root / "results.json", result)
    write_json(args.output_root / "v1-latest.json", result)
    return result


def v1_failures(args):
    """Failure witnesses on REAL v1 evidence, each on a private copy: the producer's run checks and the comparer's
    behaviour rules must each fail with exactly their named class. The latest V1 run supplies the traces."""
    import copy
    compare = comparer()
    latest = json.loads((args.output_root / "v1-latest.json").read_text(encoding="utf-8"))
    complete = {row["scenario"]: row for row in latest["results"] if row["status"] == "complete"}
    base_id = "S-slice-breed-01" if "S-slice-breed-01" in complete else sorted(complete)[0]
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
    judge("extra event", "extra-event",
          lambda lines: insert_event(lines, {"type": "event", "tick": two, "sid": states[0]["sid"], "kind": "error", "message": "witness"}))
    judge("missing event", "missing-event", lambda lines: drop_first(lines, "birth-request"))
    judge("wrong RNG use: another site", "event-mismatch", draw_site)
    judge("wrong RNG use: an extra draw", "rng-draw-count", extra_draw)
    result = {"status": "pass", "base": base_id, "base_trace_sha256": complete[base_id]["trace_sha256"],
              "witnesses": results, "evidence": str(root)}
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
