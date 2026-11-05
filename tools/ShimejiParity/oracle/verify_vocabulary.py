"""Privately compare vocabulary facts with the verified source; report hashes, never source lines."""

import argparse
import ast
import hashlib
import importlib.util
import json
from pathlib import Path
import re
from types import SimpleNamespace


HERE = Path(__file__).resolve().parent
DICTIONARIES = {"base": "SourceBaseSchemaFacts", "japaneseOverrides": "SourceJapaneseSchemaFacts"}


def canonical_hash(values):
    ordered = sorted(values, key=lambda value: value.encode("utf-16-be", "surrogatepass"))
    text = "".join(key + "\0" + values[key] + "\n" for key in ordered)
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def unescape_property(value):
    result = []
    index = 0
    substitutions = {"t": "\t", "n": "\n", "r": "\r", "f": "\f"}
    while index < len(value):
        char = value[index]
        index += 1
        if char != "\\":
            result.append(char)
            continue
        if index == len(value):
            raise ValueError("VOCABULARY_PROPERTY_TRAILING_ESCAPE")
        char = value[index]
        index += 1
        if char == "u":
            digits = value[index:index + 4]
            if not re.fullmatch("[0-9a-fA-F]{4}", digits):
                raise ValueError("VOCABULARY_PROPERTY_UNICODE_ESCAPE")
            result.append(chr(int(digits, 16)))
            index += 4
        else:
            result.append(substitutions.get(char, char))
    return "".join(result)


def property_facts(text):
    values = {}
    pending = ""
    for physical in text.splitlines():
        line = pending + physical.lstrip(" \t\f")
        pending = ""
        if not line or line.startswith(("#", "!")):
            continue
        if (len(line) - len(line.rstrip("\\"))) % 2:
            pending = line[:-1]
            continue
        split = len(line)
        escaped = False
        for index, char in enumerate(line):
            if not escaped and char in "=: \t\f":
                split = index
                break
            escaped = char == "\\" and not escaped
        end = split
        while end < len(line) and line[end] in " \t\f":
            end += 1
        if end < len(line) and line[end] in "=:":
            end += 1
        while end < len(line) and line[end] in " \t\f":
            end += 1
        values[unescape_property(line[:split])] = unescape_property(line[end:])
    if pending:
        raise ValueError("VOCABULARY_PROPERTY_INCOMPLETE_CONTINUATION")
    return values


def dictionary_facts(text, name):
    declarations = list(re.finditer(r"\b" + re.escape(name) + r"\s*=", text))
    if len(declarations) != 1:
        raise ValueError("VOCABULARY_DICTIONARY_DECLARATION " + name)
    start = text.index("{", declarations[0].end())
    end = text.index("};", start)
    body = text[start + 1:end]
    literal = r'"(?:\\.|[^"\\])*"'
    pattern = re.compile(r"\{\s*(" + literal + r")\s*,\s*(" + literal + r")\s*\}\s*,?")
    values = {}
    ordered = []
    for match in pattern.finditer(body):
        key, value = (json.loads(match.group(index)) for index in (1, 2))
        if key in values:
            raise ValueError("VOCABULARY_DICTIONARY_DUPLICATE " + name)
        ordered.append(key)
        values[key] = value
    if pattern.sub("", body).strip() or not values:
        raise ValueError("VOCABULARY_DICTIONARY_SHAPE " + name)
    if ordered != sorted(ordered, key=lambda value: value.encode("utf-16-be", "surrogatepass")):
        raise ValueError("VOCABULARY_DICTIONARY_ORDER " + name)
    return values


def require_equivalent(expected, actual, label):
    if canonical_hash(expected) != canonical_hash(actual):
        raise ValueError("VOCABULARY_FACTS_DIFFER " + label)


def self_test():
    fixture = "# comment\nb=old\na=one\\\n  two\nb:new\nspace\\ key = \\u65e5\\tend\n"
    assert property_facts(fixture) == {"a": "onetwo", "b": "new", "space key": "\u65e5\tend"}, "VOCABULARY_PROPERTIES_WITNESS"
    assert canonical_hash({"b": "2", "a": "1"}) == canonical_hash({"a": "1", "b": "2"}), "VOCABULARY_SORT_WITNESS"
    require_equivalent({"a": "one"}, {"a": "one"}, "positive")
    try:
        require_equivalent({"a": "one"}, {"a": "changed"}, "synthetic-mutation")
    except ValueError as error:
        assert str(error) == "VOCABULARY_FACTS_DIFFER synthetic-mutation"
    else:
        raise AssertionError("VOCABULARY_DIFFERENCE_WITNESS")


def mutation_witness():
    tree = ast.parse((HERE / "verify_vocabulary.py").read_text(encoding="utf-8"))
    targets = [node for node in ast.walk(tree) if isinstance(node, ast.FunctionDef) and node.name == "require_equivalent"]
    guards = [node for node in ast.walk(targets[0]) if isinstance(node, ast.If)]
    if len(targets) != 1 or len(guards) != 1:
        raise AssertionError("VOCABULARY_MUTATION_TARGET_WITNESS")
    guards[0].test = ast.Constant(value=False)
    namespace = {"__file__": str(HERE / "verify_vocabulary.py"), "__name__": "vocabulary_guard_mutation"}
    exec(compile(ast.fix_missing_locations(tree), str(HERE / "verify_vocabulary.py"), "exec"), namespace)
    try:
        namespace["self_test"]()
    except AssertionError as error:
        if str(error) != "VOCABULARY_DIFFERENCE_WITNESS":
            raise
        return {"mutation": "vocabulary-difference-guard", "status": "FIRED", "witness": str(error)}
    raise AssertionError("VOCABULARY_MUTATION_SURVIVED")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cache-root", type=Path, required=True)
    parser.add_argument("--converter-parser", type=Path, required=True)
    args = parser.parse_args()
    self_test()
    mutation = mutation_witness()
    module_spec = importlib.util.spec_from_file_location("oracle_vocabulary_source", HERE / "oracle.py")
    oracle = importlib.util.module_from_spec(module_spec)
    module_spec.loader.exec_module(oracle)
    oracle.validate_storage_roots(SimpleNamespace(cache_root=args.cache_root, output_root=args.cache_root))
    source = oracle.check_source(args)
    if args.converter_parser.suffix.lower() != ".cs" or oracle.protected_member(str(args.converter_parser.resolve())):
        raise ValueError("VOCABULARY_CONVERTER_PATH_REFUSAL")
    facts_text = args.converter_parser.read_text(encoding="utf-8-sig")
    source_files = {"base": source / "conf" / "schema.properties",
                    "japaneseOverrides": source / "conf" / "schema_ja.properties"}
    source_facts = {label: property_facts(path.read_text(encoding="utf-8-sig")) for label, path in source_files.items()}
    converter_facts = {label: dictionary_facts(facts_text, name) for label, name in DICTIONARIES.items()}
    for label in DICTIONARIES:
        require_equivalent(source_facts[label], converter_facts[label], label)
    for collection in (source_facts, converter_facts):
        collection["japaneseResolved"] = dict(collection["base"], **collection["japaneseOverrides"])
    require_equivalent(source_facts["japaneseResolved"], converter_facts["japaneseResolved"], "japaneseResolved")
    hashes = {label: {"pairs": len(values), "sha256": canonical_hash(values)} for label, values in source_facts.items()}
    expected = json.loads((HERE / "vocabulary-hashes.json").read_text(encoding="utf-8"))
    if expected.get("sourceZipSha256") != oracle.SOURCE_SHA256 or expected.get("vocabularies") != hashes:
        raise ValueError("VOCABULARY_PINNED_HASHES_DIFFER")
    print(json.dumps({"status": "pass", "sourceZipSha256": oracle.SOURCE_SHA256,
                      "canonicalization": "ordinal key + NUL + read name + LF, UTF-8 without BOM",
                      "vocabularies": hashes, "mutation": mutation}, indent=2))


if __name__ == "__main__":
    main()
