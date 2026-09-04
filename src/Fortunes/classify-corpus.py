#!/usr/bin/env python3
"""Tag each corpus line with a content level and a sensitive-content flag.

Reads a tab-separated corpus whose LAST field is the fortune text and whose FIRST field is
the source collection. It preserves either the explicit schema-v2 taxonomy:

    source<TAB>topic<TAB>genre<TAB>level<TAB>prof<TAB>text

or the legacy-v1 build-stage layout:

    source<TAB>category<TAB>level<TAB>prof<TAB>text

    level = general | edgy | nsfw
      nsfw    -> explicit sexual / graphic language
      edgy    -> profanity or crude/offensive humor (incl. inherently-adult sources)
      general -> everything else
    prof  = 1 if the text contains profanity or explicit sexual content, else 0

Idempotent: existing level/prof columns are ignored and recomputed from the text. Schema-v2
topic/genre fields are preserved exactly; the three-column build input becomes legacy v1.
Unknown field layouts are rejected instead of guessed.
Input and output must be valid UTF-8. Malformed input is rejected without replacing the
original file.
"""
import os
import re
import stat
import sys
import tempfile
import unicodedata

MAX_INPUT_BYTES = 256 * 1024 * 1024
MAX_OUTPUT_BYTES = 512 * 1024 * 1024
MAX_LINE_BYTES = 64 * 1024
MAX_ROWS = 2_000_000

# Python and .NET intentionally share this small, deterministic Unicode fold:
# compatibility-decompose, remove combining marks, map dotless i, then lowercase
# ASCII. The classifier vocabulary is ASCII, so avoiding engine-specific Unicode
# case-insensitive regex rules keeps build-time and runtime results identical.
_COMBINING_CATEGORIES = {"Mn", "Mc", "Me"}


def canonicalize(value):
    folded = []
    for char in unicodedata.normalize("NFKD", value or ""):
        if unicodedata.category(char) in _COMBINING_CATEGORIES:
            continue
        if char == "\u0131":
            char = "i"
        if "A" <= char <= "Z":
            char = chr(ord(char) + (ord("a") - ord("A")))
        folded.append(char)
    return "".join(folded)


_LEFT = r"(?<![a-z0-9_])"
_RIGHT = r"(?![a-z0-9_])"
_SPACE = r"[ \t\r\n\f\v]+"
_TAIL = r"[a-z0-9_]*"

# Explicit sexual / graphic -> nsfw.
NSFW = re.compile(_LEFT + r"("
    r"pussy|cocks|dicks|cocksuck[a-z0-9_]*|penis|penises|vagina[a-z0-9_]*|cums|cumming|jizz|"
    r"blow ?jobs?|hand ?jobs?|rim ?jobs?|masturbat[a-z0-9_]*|porn[a-z0-9_]*|"
    r"rap(?:e|ed|es|ing|ists?)|"
    r"dildo[a-z0-9_]*|orgasms?|semen|ejaculat[a-z0-9_]*|horny|clit[a-z0-9_]*|nipples?|"
    r"titties|titty|slut[a-z0-9_]*|whore[a-z0-9_]*|cunt[a-z0-9_]*|nsfw|hentai|dominatrix|"
    r"fetish[a-z0-9_]*|genital[a-z0-9_]*|scrotum|testicles?|foreskin|blowie|creampie|cumshot|"
    r"deepthroat|felch[a-z0-9_]*|fisting|gangbang|bukkake|blow job|handjob|jack him off|"
    r"erections?|boners?|incest[a-z0-9_]*|(?:ped|paed)ophil[a-z0-9_]*|necrophil[a-z0-9_]*|"
    r"molest[a-z0-9_]*|org(?:y|ies)"
    r")" + _RIGHT)

# Explicit descriptions that do not use anatomical or profanity keywords. Keep this
# phrase-based so neutral uses such as "biological sex" and "same-sex pairing" remain general.
EXPLICIT_SEX = re.compile(
    _LEFT + r"(?:"
    r"(?:have|has|had|having|solicit(?:s|ed|ing)?)" + _SPACE + r"sex|"
    r"sex" + _SPACE + r"(?:with|from)|"
    r"sexual(?:ly)?" + _SPACE + r"assault(?:s|ed|ing)?|"
    r"(?:bestiality|zoophil" + _TAIL + r")"
    r")" + _RIGHT
)

# Profanity that is crude but not explicitly sexual -> edgy.
EDGY = re.compile(_LEFT + r"("
    r"fuck[a-z0-9_]*|shit[a-z0-9_]*|bitch[a-z0-9_]*|asshole[a-z0-9_]*|ass|"
    r"arse[a-z0-9_]*|damn|goddamn[a-z0-9_]*|bastard[a-z0-9_]*|"
    r"piss[a-z0-9_]*|nigg[a-z0-9_]*|fag|faggot[a-z0-9_]*|retard[a-z0-9_]*|"
    r"douche[a-z0-9_]*|pricks?|wank[a-z0-9_]*|bollocks|twat[a-z0-9_]*|"
    r"jackass|dumbass|motherfuck[a-z0-9_]*|bullshit|"
    r"dick(?:heads?|wads?|bags?|faces?)?|cock|boob[a-z0-9_]*|tits"
    r")" + _RIGHT)

# Sources whose humor is inherently adult even when the wording is clean.
EDGY_SOURCES = {"yo-mama", "carlin"}

def classify(text, source):
    canonical_text = canonicalize(text)
    canonical_source = canonicalize(source)
    explicit = bool(
        NSFW.search(canonical_text) or EXPLICIT_SEX.search(canonical_text))
    edgy = bool(EDGY.search(canonical_text))
    prof = 1 if (explicit or edgy) else 0
    if explicit:
        level = "nsfw"
    elif edgy or canonical_source in EDGY_SOURCES:
        level = "edgy"
    else:
        level = "general"
    return level, prof

def _target_state(path):
    target = os.path.abspath(path)
    info = os.lstat(target)
    if stat.S_ISLNK(info.st_mode):
        raise ValueError(f"{path}: refusing to replace a symbolic link")
    if not stat.S_ISREG(info.st_mode):
        raise ValueError(f"{path}: expected a regular file")
    if info.st_size > MAX_INPUT_BYTES:
        raise ValueError(
            f"{path}: input is {info.st_size} bytes; limit is {MAX_INPUT_BYTES}")
    return target, info


def _state_fingerprint(info):
    return (
        info.st_dev,
        info.st_ino,
        info.st_size,
        info.st_mtime_ns,
        stat.S_IMODE(info.st_mode),
    )


def _assert_unchanged(path, original):
    try:
        current = os.lstat(path)
    except FileNotFoundError as exc:
        raise RuntimeError(f"{path}: input disappeared during classification") from exc
    if _state_fingerprint(current) != _state_fingerprint(original):
        raise RuntimeError(f"{path}: input changed during classification")


def _bounded_lines(path):
    total_bytes = 0
    with open(path, "rb") as reader:
        for line_number in range(1, MAX_ROWS + 2):
            raw = reader.readline(MAX_LINE_BYTES + 1)
            if not raw:
                return
            total_bytes += len(raw)
            if total_bytes > MAX_INPUT_BYTES:
                raise ValueError(
                    f"{path}: input grew beyond the {MAX_INPUT_BYTES}-byte limit")
            if len(raw) > MAX_LINE_BYTES:
                raise ValueError(
                    f"{path}:{line_number}: line exceeds {MAX_LINE_BYTES} bytes")
            if line_number > MAX_ROWS:
                raise ValueError(f"{path}: row count exceeds {MAX_ROWS}")
            try:
                line = raw.decode("utf-8")
            except UnicodeDecodeError as exc:
                raise ValueError(
                    f"{path}:{line_number}: invalid UTF-8 at byte "
                    f"{exc.start + 1}") from exc
            yield line_number, line


def process(path):
    target, original = _target_state(path)
    parent = os.path.dirname(target)
    prefix = f".{os.path.basename(target)}."
    descriptor, staged = tempfile.mkstemp(
        prefix=prefix, suffix=".tmp", dir=parent)
    counts = {"general": 0, "edgy": 0, "nsfw": 0}
    prof_n = 0
    row_count = 0
    output_bytes = 0
    try:
        os.chmod(staged, stat.S_IMODE(original.st_mode))
        with os.fdopen(
                descriptor, "w", encoding="utf-8",
                errors="strict", newline="\n") as writer:
            descriptor = -1
            for line_number, line in _bounded_lines(target):
                parts = line.rstrip("\n").split("\t")
                if len(parts) not in (3, 5, 6):
                    raise ValueError(
                        f"{path}:{line_number}: expected 3, legacy 5, or "
                        f"schema-v2 6 fields; got {len(parts)}")
                source = parts[0]
                text = parts[-1]
                level, prof = classify(text, source)
                if len(parts) == 6:
                    # Exact schema v2: source, topic, genre, level, prof, text.
                    fields = [
                        source, parts[1], parts[2], level, str(prof), text]
                else:
                    # Build-stage or explicit legacy v1.
                    fields = [source, parts[1], level, str(prof), text]
                output_line = "\t".join(fields) + "\n"
                output_bytes += len(output_line.encode("utf-8"))
                if output_bytes > MAX_OUTPUT_BYTES:
                    raise ValueError(
                        f"{path}: output exceeds {MAX_OUTPUT_BYTES} bytes")
                writer.write(output_line)
                counts[level] += 1
                prof_n += prof
                row_count += 1
            writer.flush()
            os.fsync(writer.fileno())
        _assert_unchanged(target, original)
        os.replace(staged, target)
        staged = None
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        if staged is not None:
            try:
                os.unlink(staged)
            except FileNotFoundError:
                pass
    print(f"{path}: general={counts['general']} edgy={counts['edgy']} nsfw={counts['nsfw']} "
          f"profane={prof_n} total={row_count}")

if __name__ == '__main__':
    for f in (sys.argv[1:] or ['fortunes.txt']):
        process(f)
