"""Apply source-neutral edit metadata and build a private draft oracle.

The checked-in specification contains locations, hashes and original hook calls.
Source-shaped replacements and the actual diff are derived only in the private cache.
"""

import ast
import difflib
import json
import os
from pathlib import PurePosixPath
import re
import uuid


HOOKS = "com.group_finity.mascot.parity.DraftParityHooks"
RANDOM_TOKEN = re.compile(r"\bMath\s*\.\s*random\s*\(\s*\)")
HELPERS = ("DraftParityHooks.java", "DraftTraceWriter.java", "NativeFactoryImpl.java", "ScenarioEnvironment.java")
BUILTIN_SITES = {
    "builtin:create-imageset", "builtin:create-facing", "builtin:behavior-select",
    "builtin:no-candidate-drop-x", "builtin:disabled-behavior-drop-x",
    "builtin:out-of-bounds-drop-x", "builtin:dragged-regist", "builtin:regist-facing",
}
RNG_COUNTS = {
    "src/com/group_finity/mascot/Main.java": 2,
    "src/com/group_finity/mascot/config/Configuration.java": 5,
    "src/com/group_finity/mascot/behavior/UserBehavior.java": 2,
    "src/com/group_finity/mascot/action/Dragged.java": 1,
    "src/com/group_finity/mascot/action/Regist.java": 1,
}
RNG_COORDINATES = {
    ("src/com/group_finity/mascot/Main.java", 1290): "builtin:create-imageset",
    ("src/com/group_finity/mascot/Main.java", 1308): "builtin:create-facing",
    ("src/com/group_finity/mascot/config/Configuration.java", 237): "builtin:no-candidate-drop-x",
    ("src/com/group_finity/mascot/config/Configuration.java", 242): "builtin:no-candidate-drop-x",
    ("src/com/group_finity/mascot/config/Configuration.java", 248): "builtin:behavior-select",
    ("src/com/group_finity/mascot/config/Configuration.java", 274): "builtin:disabled-behavior-drop-x",
    ("src/com/group_finity/mascot/config/Configuration.java", 279): "builtin:disabled-behavior-drop-x",
    ("src/com/group_finity/mascot/behavior/UserBehavior.java", 261): "builtin:out-of-bounds-drop-x",
    ("src/com/group_finity/mascot/behavior/UserBehavior.java", 266): "builtin:out-of-bounds-drop-x",
    ("src/com/group_finity/mascot/action/Dragged.java", 104): "builtin:dragged-regist",
    ("src/com/group_finity/mascot/action/Regist.java", 69): "builtin:regist-facing",
}


def normalized(data):
    return data.decode("utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")


def safe_name(name, oracle):
    path = PurePosixPath(name.replace("\\", "/"))
    if path.is_absolute() or ".." in path.parts or any(":" in item for item in path.parts):
        raise oracle.Refusal("DRAFT_SPEC_PATH_REFUSAL " + name)
    if oracle.protected_member(name) or path.suffix != ".java" or not path.parts or path.parts[0] not in oracle.SOURCE_ROOTS:
        raise oracle.Refusal("DRAFT_SPEC_PATH_REFUSAL " + name)
    return name


def edit_line(line, edit, oracle):
    """Transform a verified private line without a checked-in source template."""
    operation = edit["op"]
    newline = "\n" if line.endswith("\n") else ""
    body = line[:-1] if newline else line
    indent = re.match(r"^[ \t]*", body).group() + " " * edit.get("indent", 0)

    def inserted(items):
        return "".join((indent + item if item else "") + "\n" for item in items)

    if operation == "insert-before":
        return inserted(edit["code"]) + line
    if operation == "insert-after":
        return line + ("\n" if not newline else "") + inserted(edit["code"])
    if operation == "wrap-line":
        return inserted(edit["before"]) + line + ("\n" if not newline else "") + inserted(edit["after"])
    if operation == "conditional-assignment":
        left, delimiter, right = body.partition("=")
        value = re.fullmatch(r"\s*(.*?);[ \t]*", right)
        if not delimiter or value is None:
            raise oracle.Refusal("DRAFT_ASSIGNMENT_SHAPE_REFUSAL")
        return left + "= " + edit["condition"] + " ? " + edit["value"] + " : (" + value.group(1) + ");" + newline
    if operation == "wrap-assigned-value":
        return assigned_value(body, newline, edit, oracle)
    if operation == "replace-random-token":
        if edit["site"] not in BUILTIN_SITES:
            raise oracle.Refusal("DRAFT_RNG_SITE_REFUSAL " + edit["site"])
        result, count = RANDOM_TOKEN.subn(lambda _: HOOKS + '.random("' + edit["site"] + '")', line)
        if count != 1:
            raise oracle.Refusal("DRAFT_RNG_LINE_COUNT_REFUSAL")
        return result
    raise oracle.Refusal("DRAFT_OPERATION_REFUSAL " + operation)


def assigned_value(body, newline, edit, oracle):
    """wrap-assigned-value: the assigned value passes through a hooks method that returns it unchanged. The
    expression itself is read from the verified private line, so the specification never holds it."""
    left, delimiter, right = body.partition("=")
    value = re.fullmatch(r"\s*(.*?);[ \t]*", right)
    call = edit.get("call", "")
    if not delimiter or value is None or not call.startswith(HOOKS + "."):
        raise oracle.Refusal("DRAFT_ASSIGNED_VALUE_REFUSAL")
    arguments = "".join(item + ", " for item in edit.get("arguments", []))
    return left + "= " + call + "(" + arguments + "(" + value.group(1) + "));" + newline


def derive_changes(source, spec, oracle):
    if spec.get("schema") != "oracle-edit-spec/1" or spec.get("sourceZipSha256") != oracle.SOURCE_SHA256:
        raise oracle.Refusal("DRAFT_SPEC_VERSION_REFUSAL")
    if spec.get("javaRandomCounts") != RNG_COUNTS:
        raise oracle.Refusal("DRAFT_RNG_COUNT_SPEC_REFUSAL")
    edits = {}
    for edit in spec["edits"]:
        name = safe_name(edit["file"], oracle)
        coordinate = (name, edit["line"])
        if coordinate in edits or not isinstance(edit["line"], int) or edit["line"] < 1:
            raise oracle.Refusal("DRAFT_SPEC_COORDINATE_REFUSAL " + name)
        edits[coordinate] = edit
    if set(spec["files"]) != {name for name, _ in edits}:
        raise oracle.Refusal("DRAFT_SPEC_FILE_SET_REFUSAL")
    changes = {}
    random_sites = []
    for name, witness in spec["files"].items():
        safe_name(name, oracle)
        data = (source / name).read_bytes()
        oracle.verify(data, witness["sha256"], "draft-source-file " + name)
        before = normalized(data)
        lines = before.splitlines(True)
        selected = sorted((number, edit) for (path, number), edit in edits.items() if path == name)
        for number, edit in selected:
            if number > len(lines):
                raise oracle.Refusal("DRAFT_LINE_RANGE_REFUSAL " + name)
            oracle.verify(lines[number - 1].rstrip("\n").encode("utf-8"), edit["lineSha256"],
                          "draft-source-line " + name + ":" + str(number))
        source_random_count = len(RANDOM_TOKEN.findall(before))
        selected_random = [(number, edit) for number, edit in selected if edit["op"] == "replace-random-token"]
        if source_random_count != RNG_COUNTS.get(name, 0) or len(selected_random) != source_random_count:
            raise oracle.Refusal("DRAFT_RNG_FILE_COUNT_REFUSAL " + name)
        # Reverse order preserves every remaining one-based coordinate from the pinned source.
        after = list(lines)
        for number, edit in reversed(selected):
            after[number - 1] = edit_line(lines[number - 1], edit, oracle)
        changed = "".join(after)
        if changed == before:
            raise oracle.Refusal("DRAFT_UNCHANGED_EDIT_REFUSAL " + name)
        changes[name] = (before, changed)
        random_sites.extend({"file": name, "line": number, "site": edit["site"]} for number, edit in selected_random)
    if len(random_sites) != 11 or {item["site"] for item in random_sites} != BUILTIN_SITES:
        raise oracle.Refusal("DRAFT_RNG_SITE_SET_REFUSAL")
    actual_coordinates = {(item["file"], item["line"]): item["site"] for item in random_sites}
    if actual_coordinates != RNG_COORDINATES:
        raise oracle.Refusal("DRAFT_RNG_COORDINATE_REFUSAL")
    helper_root = (oracle.HERE / "draft").resolve()
    for helper_name in HELPERS:
        helper = (helper_root / helper_name).resolve()
        if helper.parent != helper_root:
            raise oracle.Refusal("DRAFT_HELPER_PATH_REFUSAL " + helper_name)
        changes["src/com/group_finity/mascot/parity/" + helper_name] = ("", normalized(helper.read_bytes()))
    return changes, sorted(random_sites, key=lambda item: (item["file"], item["line"]))


def spec_negative_cases(source, original, oracle, derive=derive_changes, only=None):
    results = []
    labels = ("source-pin", "file-sha", "line-sha", "line-range", "rng-count-spec", "rng-operation", "rng-coordinate", "unknown-op",
              "assigned-call")
    for label in labels:
        if only is not None and label not in only:
            continue
        spec = json.loads(json.dumps(original))
        if label == "source-pin":
            spec["sourceZipSha256"] = "0" * 64
            expected = "DRAFT_SPEC_VERSION_REFUSAL"
        elif label == "file-sha":
            spec["files"][next(iter(spec["files"]))]["sha256"] = "0" * 64
            expected = "CHECKSUM_REFUSAL draft-source-file"
        elif label == "line-sha":
            spec["edits"][0]["lineSha256"] = "0" * 64
            expected = "CHECKSUM_REFUSAL draft-source-line"
        elif label == "line-range":
            spec["edits"][0]["line"] = 999999
            expected = "DRAFT_LINE_RANGE_REFUSAL"
        elif label == "rng-count-spec":
            spec["javaRandomCounts"][next(iter(spec["javaRandomCounts"]))] = 99
            expected = "DRAFT_RNG_COUNT_SPEC_REFUSAL"
        elif label == "rng-operation":
            spec["edits"][-1]["op"] = "insert-before"
            spec["edits"][-1]["code"] = [";"]
            expected = "DRAFT_RNG_FILE_COUNT_REFUSAL"
        elif label == "rng-coordinate":
            matching = [edit for edit in spec["edits"] if edit["file"].endswith("/Configuration.java")
                        and edit["line"] in (237, 248)]
            matching[0]["site"], matching[1]["site"] = matching[1]["site"], matching[0]["site"]
            expected = "DRAFT_RNG_COORDINATE_REFUSAL"
        elif label == "assigned-call":
            # A wrapped value may pass only through the hooks class, never through an arbitrary method.
            next(edit for edit in spec["edits"] if edit["op"] == "wrap-assigned-value")["call"] = "java.lang.System.identityHashCode"
            expected = "DRAFT_ASSIGNED_VALUE_REFUSAL"
        else:
            spec["edits"][0]["op"] = "unexpected"
            expected = "DRAFT_OPERATION_REFUSAL"
        try:
            derive(source, spec, oracle)
        except oracle.Refusal as failure:
            if not str(failure).startswith(expected):
                raise oracle.Refusal("DRAFT_GUARD_WRONG_REFUSAL " + label)
            results.append({"case": label, "status": "refused", "witness": expected})
        else:
            raise oracle.Refusal("DRAFT_GUARD_DID_NOT_FIRE " + label)
    return results


def spec_guard_witnesses(source, spec, oracle):
    negatives = spec_negative_cases(source, spec, oracle)
    own_path = oracle.HERE / "draft_patch.py"
    own_text = own_path.read_text(encoding="utf-8")
    mutations = []
    # Mutate this tool's own AST in memory. No source-cache file or tracked file changes.
    for case, kind, label in (
        ("source-pin", "condition", "DRAFT_SPEC_VERSION_REFUSAL"),
        ("file-sha", "verify", "draft-source-file "),
        ("line-sha", "verify", "draft-source-line "),
        ("rng-count-spec", "condition", "DRAFT_RNG_COUNT_SPEC_REFUSAL"),
        ("rng-coordinate", "condition", "DRAFT_RNG_COORDINATE_REFUSAL"),
        ("assigned-call", "condition", "DRAFT_ASSIGNED_VALUE_REFUSAL"),
    ):
        class DisableGuard(ast.NodeTransformer):
            count = 0

            def visit_If(self, node):
                self.generic_visit(node)
                if kind == "condition" and any(
                    isinstance(item, ast.Raise) and isinstance(item.exc, ast.Call)
                    and isinstance(item.exc.func, ast.Attribute) and item.exc.func.attr == "Refusal"
                    and any(isinstance(value, ast.Constant) and value.value == label for value in ast.walk(item.exc))
                    for statement in node.body for item in ast.walk(statement)
                ):
                    node.test = ast.Constant(value=False)
                    self.count += 1
                return node

            def visit_Expr(self, node):
                self.generic_visit(node)
                if kind == "verify" and isinstance(node.value, ast.Call) and isinstance(node.value.func, ast.Attribute) \
                        and node.value.func.attr == "verify" and any(
                            isinstance(item, ast.Constant) and item.value == label for item in ast.walk(node.value)):
                    self.count += 1
                    return ast.copy_location(ast.Pass(), node)
                return node

        disable = DisableGuard()
        tree = disable.visit(ast.parse(own_text, filename=str(own_path)))
        if disable.count != 1:
            raise oracle.Refusal("DRAFT_GUARD_MUTATION_TARGET_REFUSAL " + case)
        namespace = {"__name__": "oracle_draft_guard_mutation", "__file__": str(own_path)}
        exec(compile(ast.fix_missing_locations(tree), str(own_path), "exec"), namespace)
        try:
            spec_negative_cases(source, spec, oracle, derive=namespace["derive_changes"], only={case})
        except oracle.Refusal as failure:
            witness = "DRAFT_GUARD_DID_NOT_FIRE " + case
            if str(failure) != witness:
                raise oracle.Refusal("DRAFT_GUARD_MUTATION_WRONG_WITNESS " + case)
            mutations.append({"mutation": case, "status": "FIRED", "witness": witness})
        else:
            raise oracle.Refusal("DRAFT_GUARD_MUTATION_SURVIVED " + case)
    return {"status": "pass", "negative_inputs": negatives, "mutations": mutations, "filesystem_mutations": 0,
            "tool_sha256": oracle.sha(own_path.read_bytes())}


def run(args, oracle):
    source = oracle.check_source(args)
    spec_path = oracle.HERE / "edit-spec.json"
    spec_bytes = spec_path.read_bytes()
    spec = json.loads(spec_bytes.decode("utf-8"))
    changes, random_sites = derive_changes(source, spec, oracle)
    guards = spec_guard_witnesses(source, spec, oracle)
    root = args.cache_root / "draft-builds" / uuid.uuid4().hex[:8]
    private = root / "source"
    inventory = oracle.extract((args.cache_root / "shimejieesrc.zip").read_bytes(), private)
    oracle.write_json(root / "edit-spec-guards.json", guards)
    for name in inventory:
        if name.endswith(".java"):
            target = private / name
            target.write_bytes(normalized(target.read_bytes()).encode("utf-8"))
    file_witnesses = []
    for name, (before, after) in sorted(changes.items()):
        target = private / name
        if before:
            oracle.verify(target.read_bytes(), oracle.sha(before.encode("utf-8")), "draft-pre-apply " + name)
        elif target.exists():
            raise oracle.Refusal("DRAFT_NEW_FILE_EXISTS " + name)
        file_witnesses.append({"file": name, "beforeSha256": oracle.sha(before.encode("utf-8")) if before else None,
                               "expectedAfterSha256": oracle.sha(after.encode("utf-8"))})
    patch = "".join("".join(difflib.unified_diff(before.splitlines(True), after.splitlines(True),
                                               fromfile="a/" + name if before else "/dev/null", tofile="b/" + name, n=0))
                    for name, (before, after) in sorted(changes.items()))
    # The diff necessarily contains upstream context. It belongs only in this private build folder.
    patch_path = root / "source-test-seams.draft.patch"
    patch_path.write_bytes(patch.encode("utf-8"))
    saved_ceiling = os.environ.get("GIT_CEILING_DIRECTORIES")
    os.environ["GIT_CEILING_DIRECTORIES"] = str(private.parent.resolve())
    try:
        # Per-process options keep the verified LF bytes independent of the caller's Git settings.
        apply_command = ["git", "-c", "core.autocrlf=false", "-c", "core.eol=lf", "apply", "--unidiff-zero"]
        checked = oracle.command(apply_command + ["--check", str(patch_path)], private)
        oracle.write_json(root / "patch-check.json", checked)
        oracle.require_command(checked, "DRAFT_PATCH_CHECK_FAILED " + str(root / "patch-check.json"))
        applied = oracle.command(apply_command + [str(patch_path)], private)
        oracle.write_json(root / "patch-apply.json", applied)
        oracle.require_command(applied, "DRAFT_PATCH_APPLY_FAILED " + str(root / "patch-apply.json"))
    finally:
        if saved_ceiling is None:
            os.environ.pop("GIT_CEILING_DIRECTORIES", None)
        else:
            os.environ["GIT_CEILING_DIRECTORIES"] = saved_ceiling
    for witness in file_witnesses:
        actual = (private / witness["file"]).read_bytes()
        witness["actualAfterSha256"] = oracle.verify(actual, witness["expectedAfterSha256"], "draft-post-apply " + witness["file"])
    oracle.write_json(root / "file-witnesses.json", file_witnesses)
    classes = root / "classes"
    classes.mkdir()
    files = sorted(private / name for name in set(inventory) | set(changes)
                   if name.endswith(".java") and PurePosixPath(name).parts[0] in oracle.SOURCE_ROOTS)
    response = root / "sources.args"
    response.write_bytes(("\n".join('"' + str(path).replace("\\", "/") + '"' for path in files) + "\n").encode("utf-8"))
    compiler = oracle.command([args.jdk_root / "bin" / "javac.exe", "-encoding", "UTF-8", "-source", "1.6", "-target", "1.6",
                               "-g", "-classpath", str(private / "lib" / "*"), "-d", classes, "@" + str(response)], private)
    oracle.write_json(root / "compile.json", compiler)
    oracle.require_command(compiler, "DRAFT_BUILD_FAILED " + str(root / "compile.json"))
    artifact = root / "Shimeji-draft.jar"
    jar = oracle.command([args.jdk_root / "bin" / "jar.exe", "cfm", artifact, private / "MANIFEST.MF", "-C", classes, "."], private)
    oracle.write_json(root / "jar.json", jar)
    oracle.require_command(jar, "DRAFT_JAR_FAILED " + str(root / "jar.json"))
    oracle.check_source(args)
    result = {"status": "draft-compiles", "patch": str(patch_path), "patch_sha256": oracle.sha(patch_path.read_bytes()),
              "edit_spec_sha256": oracle.sha(spec_bytes), "file_witnesses": str(root / "file-witnesses.json"),
              "edit_spec_guards": str(root / "edit-spec-guards.json"),
              "private_build": str(root), "java_random_sites": random_sites,
              "contract_status": "provisional-script-sites-and-event-fields",
              "artifact": str(artifact), "artifact_sha256": oracle.sha(artifact.read_bytes()), "source": str(private),
              "patch_applied_from_private_file": all(row["actualAfterSha256"] == row["expectedAfterSha256"] for row in file_witnesses),
              "note": "Java RNG site names follow the builtin table; script call-site identity and event assembly remain draft."}
    oracle.write_json(root / "draft.json", result)
    oracle.write_json(args.cache_root / "latest-draft.json", result)
    return result
