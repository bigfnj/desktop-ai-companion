#!/usr/bin/env bash
# Build the bundled fortune corpus from a reviewed clone of JKirchartz/fortunes.
# The upstream repository declares the Unlicense.
# That repository-level label does not establish redistribution rights for every quotation or
# datfile entry. Retain source-by-source evidence; a successful corpus build is not rights clearance.
#
#   git clone https://github.com/JKirchartz/fortunes.git <srcdir>
#   git -C <srcdir> checkout <reviewed-40-character-commit>
#   FORTUNE_SOURCE_COMMIT=<reviewed-40-character-commit> ./build-corpus.sh <srcdir>
#
# Build-stage output is the explicit legacy-v1 layout. Schema v2
# (source/topic/genre/level/prof/text) remains the target after the blocked labeling pass is
# complete; current releases intentionally pin and validate this v1 corpus.
#
# Output: one fortune per line in fortunes.txt, tab-separated:
#   source<TAB>category<TAB>level<TAB>prof<TAB>text
#     source   = the origin collection (e.g. SimpsonsChalkboard) — powers the per-source picker
#     category = legacy coarse grouping used only as migration compatibility metadata
#                (tech / wisdom / creative / whimsy / facts / work / observations / general)
#     level    = general | edgy | nsfw   — content severity (classify-corpus.py)
#     prof     = 1 for recognized profanity or explicit sexual content, else 0
#     text     = the fortune, normalized to a single bubble-sized line (8..280 chars)
#
# Reconstruction evidence is written to fortunes.sources.tsv. It records the required source
# commit, repository, pinned-blob verification result, every consumed input's size/hash, and
# output hash.
#
# The single tagged file lets FortuneProvider filter everything at runtime (spicy tier,
# conservative profanity/explicit-content filter, and per-source selection) instead of shipping
# pre-split sfw/spicy files.
set -euo pipefail
SRC_ARG="${1:?usage: build-corpus.sh <clone-of-JKirchartz-fortunes>}"
SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
[ -d "$SRC_ARG" ] || {
  printf 'ERROR: source directory does not exist: %s\n' "$SRC_ARG" >&2
  exit 1
}
SRC="$(CDPATH= cd -- "$SRC_ARG" && pwd -P)"
OUT="$SCRIPT_DIR"
PYTHON_BIN="${PYTHON:-python}"
SOURCE_COMMIT="${FORTUNE_SOURCE_COMMIT:-}"
CANONICAL_SOURCE_REPOSITORY="https://github.com/JKirchartz/fortunes.git"
REQUESTED_SOURCE_REPOSITORY="${FORTUNE_SOURCE_REPOSITORY:-$CANONICAL_SOURCE_REPOSITORY}"
SOURCE_REPOSITORY="$CANONICAL_SOURCE_REPOSITORY"
SOURCE_MANIFEST="$SCRIPT_DIR/corpus-required-files.txt"
EVIDENCE_OUT="$OUT/fortunes.sources.tsv"

# Maintenance-tool resource limits. These are deliberately far above the curated corpus while
# keeping a malicious or accidentally wrong clone from consuming unbounded memory or disk.
MAX_SOURCE_FILE_BYTES=$((64 * 1024 * 1024))
MAX_TOTAL_SOURCE_BYTES=$((256 * 1024 * 1024))
MAX_SOURCE_LINE_BYTES=$((16 * 1024))
MAX_ENTRY_BYTES=$((64 * 1024))
MAX_STAGE_BYTES=$((512 * 1024 * 1024))
MAX_STAGE_ROWS=2000000

die() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
  else
    die "sha256sum or shasum is required"
  fi
}

normalize_source_repository() {
  case "$1" in
    https://github.com/JKirchartz/fortunes|\
https://github.com/JKirchartz/fortunes/|\
https://github.com/JKirchartz/fortunes.git|\
https://github.com/JKirchartz/fortunes.git/|\
git@github.com:JKirchartz/fortunes.git|\
ssh://git@github.com/JKirchartz/fortunes.git)
      printf '%s\n' "$CANONICAL_SOURCE_REPOSITORY"
      ;;
    *) return 1 ;;
  esac
}

[ -n "$SOURCE_COMMIT" ] ||
  die "set FORTUNE_SOURCE_COMMIT to the reviewed 40-character upstream commit"
[[ "$SOURCE_COMMIT" =~ ^[0-9A-Fa-f]{40}$ ]] ||
  die "FORTUNE_SOURCE_COMMIT must be exactly 40 hexadecimal characters"
SOURCE_COMMIT="$(printf '%s' "$SOURCE_COMMIT" | LC_ALL=C tr 'A-F' 'a-f')"
normalized_requested_repository="$(
  normalize_source_repository "$REQUESTED_SOURCE_REPOSITORY"
)" || die "FORTUNE_SOURCE_REPOSITORY must identify $CANONICAL_SOURCE_REPOSITORY"
[ "$normalized_requested_repository" = "$CANONICAL_SOURCE_REPOSITORY" ] ||
  die "FORTUNE_SOURCE_REPOSITORY did not normalize to the canonical repository"

GIT_BIN="$(type -P git 2>/dev/null || true)"
[ -n "$GIT_BIN" ] && [ -x "$GIT_BIN" ] ||
  die "an executable Git client is required to verify the source checkout"
git_bin_name="${GIT_BIN##*/}"
git_bin_dir="$(CDPATH= cd -- "$(dirname -- "$GIT_BIN")" && pwd -P)" ||
  die "cannot resolve the Git executable directory: $GIT_BIN"
GIT_BIN="$git_bin_dir/$git_bin_name"
[ -x "$GIT_BIN" ] ||
  die "resolved Git client is not executable: $GIT_BIN"

# Git replacement objects can make a reviewed commit ID resolve to attacker-controlled commit/tree
# bytes while rev-parse still reports the reviewed ID. Keep replacement lookup disabled on every
# provenance query, and reject repositories that carry standard replacement refs at all.
git_provenance() {
  GIT_NO_REPLACE_OBJECTS=1 "$GIT_BIN" --no-replace-objects "$@"
}

git_root="$(git_provenance -C "$SRC" rev-parse --show-toplevel 2>/dev/null)" ||
  die "source directory is not a Git repository root: $SRC"
git_root="$(CDPATH= cd -- "$git_root" && pwd -P)" ||
  die "cannot resolve source Git repository root: $git_root"
[ "$git_root" = "$SRC" ] ||
  die "source directory must be the exact Git repository root: $SRC (found $git_root)"
replace_refs="$(
  git_provenance -C "$SRC" for-each-ref --format='%(refname)' refs/replace 2>/dev/null
)" || die "cannot inspect source repository for prohibited refs/replace/* entries"
[ -z "$replace_refs" ] ||
  die "source repository contains prohibited Git replacement ref: ${replace_refs%%$'\n'*}"
detected_source_commit="$(
  git_provenance -C "$SRC" rev-parse --verify 'HEAD^{commit}' 2>/dev/null
)" || die "source checkout does not have a valid HEAD commit"
detected_source_commit="$(
  printf '%s' "$detected_source_commit" | LC_ALL=C tr 'A-F' 'a-f'
)"
[ "$detected_source_commit" = "$SOURCE_COMMIT" ] ||
  die "source checkout HEAD is $detected_source_commit, expected $SOURCE_COMMIT"
git_provenance -C "$SRC" cat-file -e "$SOURCE_COMMIT^{commit}" 2>/dev/null ||
  die "reviewed source commit is not reachable as a commit object: $SOURCE_COMMIT"
git_provenance -C "$SRC" merge-base --is-ancestor \
  "$SOURCE_COMMIT" HEAD 2>/dev/null ||
  die "reviewed source commit is not reachable from source checkout HEAD"

origin_urls="$(
  git_provenance -C "$SRC" config --get-all remote.origin.url 2>/dev/null
)" || die "source repository must configure remote.origin.url"
case "$origin_urls" in
  *$'\n'*) die "source repository has multiple remote.origin.url values" ;;
esac
normalized_origin="$(
  normalize_source_repository "$origin_urls"
)" || die "remote.origin.url does not identify $CANONICAL_SOURCE_REPOSITORY"
[ "$normalized_origin" = "$CANONICAL_SOURCE_REPOSITORY" ] ||
  die "remote.origin.url did not normalize to the canonical repository"

verify_source_blob() {
  local relative="$1" path="$SRC/$1" tree_entry metadata object_mode
  local object_type blob_oid extra worktree_oid
  tree_entry="$(
    git_provenance -C "$SRC" ls-tree "$SOURCE_COMMIT" -- "$relative" 2>/dev/null
  )" || die "cannot inspect curated source at source commit: $relative"
  [ -n "$tree_entry" ] ||
    die "curated source is not tracked at source commit: $relative"
  metadata="${tree_entry%%$'\t'*}"
  IFS=' ' read -r object_mode object_type blob_oid extra <<< "$metadata"
  [ -n "$blob_oid" ] && [ -z "$extra" ] ||
    die "cannot parse curated source tree entry: $relative"
  [ "$object_type" = "blob" ] ||
    die "curated source is not a Git blob at source commit: $relative"
  worktree_oid="$(
    git_provenance -C "$SRC" hash-object --no-filters -- "$path" 2>/dev/null
  )" || die "cannot hash curated source bytes: $relative"
  [ "$worktree_oid" = "$blob_oid" ] ||
    die "curated source does not match pinned Git blob: $relative"
}

# The exact curated input set is a reviewed, repository-owned manifest. A production build may not
# silently shrink because an upstream file is absent. Order only decides which source owns a
# cross-source duplicate.
[ -f "$SOURCE_MANIFEST" ] && [ ! -L "$SOURCE_MANIFEST" ] ||
  die "required source manifest is missing or unsafe: $SOURCE_MANIFEST"
ALL_FILES=()
while IFS= read -r required_file || [ -n "$required_file" ]; do
  [[ "$required_file" =~ ^[A-Za-z0-9._-]+$ ]] ||
    die "invalid required source manifest entry: '$required_file'"
  for existing_file in "${ALL_FILES[@]}"; do
    [ "$existing_file" != "$required_file" ] ||
      die "duplicate required source manifest entry: $required_file"
  done
  ALL_FILES+=("$required_file")
  [ "${#ALL_FILES[@]}" -le 512 ] ||
    die "required source manifest exceeds 512 entries"
done < "$SOURCE_MANIFEST"
[ "${#ALL_FILES[@]}" -gt 0 ] ||
  die "required source manifest contains no inputs"
source_manifest_sha256="$(sha256_file "$SOURCE_MANIFEST")"

catof() {
  case "$1" in
    epigrams_in_programming|hackers|hacker-questions|lwall-quotes|ComputerDictionary|rfc1925|enkiv2s-glossary-of-tech-industry-terms) echo tech ;;
    classic_philosophy|modern_philosophy|tao|montaigne|HeraclitusFragments|SimoneWeil|jung|Gurdjieff|mencken|korzybski|Paine|Rousseau|Bakunin|immortal_consciousness|existentialriddles|friedman_12-structures|Twenty_Lessons_On_Tyranny|bruno-latour|haraway|Schlesinger|invisiblestates|predictions|brecht_dances-events-puzzles|Kerouac-Modern-Prose|subgenius|RAW|BibleAbridged|higgins_metadramas) echo wisdom ;;
    authors|artists|wblake|ogden_nash|stevenson|Jenny_Holzer|ObliqueStrategies|ObscureSorrows|rhetorical-devices|anathem-glossary|EnglishAsSheIsSpoke|racter|critics) echo creative ;;
    MrRogers|handey|groucho|pirate|SimpsonsChalkboard|FerengiRulesOfAcquisition|redgreen|SeventyMaximsOfMaximallyEffectiveMercenaries|actualcookies|entertainers|AClaude|Andromeda|carlin|chuckfacts|yo-mama|conalnet) echo whimsy ;;
    realfacts|PA-historical-markers) echo facts ;;
    godin|activists) echo work ;;
    showerthoughts) echo observations ;;
    *) echo general ;;
  esac
}

# Normalized entries (one per line, no tags, no filtering) from one BSD fortune file.
parse_entries() {
  local f="$1" path="$SRC/$1"
  [ -f "$path" ] ||
    die "required curated source disappeared before parsing: $f"
  LC_ALL=C awk \
    -v max_file="$MAX_SOURCE_FILE_BYTES" \
    -v max_line="$MAX_SOURCE_LINE_BYTES" \
    -v max_entry="$MAX_ENTRY_BYTES" '
      function reject(message) {
        printf "%s:%d: %s\n", FILENAME, FNR, message > "/dev/stderr"
        bad=1
        exit 2
      }
      BEGIN { e=""; total=0; bad=0 }
      {
        total += length($0) + 1
        if (total > max_file) reject("input grew beyond the per-file byte limit")
        if (length($0) > max_line) reject("physical line exceeds the byte limit")
      }
      /^%[ \t\r]*$/ {
        if (e!="") print e
        e=""
        next
      }
      {
        l=$0
        gsub(/\r/,"",l)
        candidate=length(e) + (e=="" ? 0 : 1) + length(l)
        if (candidate > max_entry) reject("fortune entry exceeds the byte limit")
        e=(e=="") ? l : (e " " l)
      }
      END {
        if (!bad && e!="") print e
        if (bad) exit 2
      }' "$path" |
    LC_ALL=C sed -E 's/[[:space:]]+/ /g; s/^ +//; s/ +$//' |
    LC_ALL=C awk 'length>=8 && length<=280'
}

# source<TAB>category<TAB>text  (level/prof are added later by classify-corpus.py)
emit() {
  local f="$1" c; c="$(catof "$f")"
  parse_entries "$f" |
    LC_ALL=C awk -v src="$f" -v c="$c" '{print src "\t" c "\t" $0}'
}

source_count=0
total_source_bytes=0
for f in "${ALL_FILES[@]}"; do
  path="$SRC/$f"
  if [ -L "$path" ]; then
    die "refusing symbolic-link source: $path"
  fi
  [ -e "$path" ] || die "required curated source is missing: $f"
  [ -f "$path" ] || die "source is not a regular file: $path"
  verify_source_blob "$f"
  if size="$(stat -c '%s' -- "$path" 2>/dev/null)"; then
    :
  elif size="$(stat -f '%z' "$path" 2>/dev/null)"; then
    :
  else
    die "cannot determine source size: $path"
  fi
  case "$size" in
    ''|*[!0-9]*) die "invalid source size for $path: $size" ;;
  esac
  [ "$size" -le "$MAX_SOURCE_FILE_BYTES" ] ||
    die "source exceeds $MAX_SOURCE_FILE_BYTES bytes: $path"
  total_source_bytes=$((total_source_bytes + size))
  [ "$total_source_bytes" -le "$MAX_TOTAL_SOURCE_BYTES" ] ||
    die "sources exceed the $MAX_TOTAL_SOURCE_BYTES-byte aggregate limit"
  source_count=$((source_count + 1))
done
[ "$source_count" -eq "${#ALL_FILES[@]}" ] ||
  die "required curated source count changed during validation"
if [ -e "$OUT/fortunes.txt" ] || [ -L "$OUT/fortunes.txt" ]; then
  [ -f "$OUT/fortunes.txt" ] && [ ! -L "$OUT/fortunes.txt" ] ||
    die "output target is not a regular file: $OUT/fortunes.txt"
fi
if [ -e "$EVIDENCE_OUT" ] || [ -L "$EVIDENCE_OUT" ]; then
  [ -f "$EVIDENCE_OUT" ] && [ ! -L "$EVIDENCE_OUT" ] ||
    die "evidence target is not a regular file: $EVIDENCE_OUT"
fi

stage_dir=""
promotion_started=0
committed=0
corpus_existed=0
evidence_existed=0
restore_build_file() {
  local target="$1" backup="$2" existed="$3" restore
  if [ "$existed" -eq 1 ]; then
    restore="$(mktemp "$target.build-restore.XXXXXX")" || return 1
    if ! cp -- "$backup" "$restore" || ! mv -f -- "$restore" "$target"; then
      rm -f -- "$restore"
      return 1
    fi
  else
    rm -f -- "$target" || return 1
  fi
}
cleanup() {
  local status=$? rollback_failed=0
  trap - EXIT HUP INT TERM
  set +e

  if [ "$promotion_started" -eq 1 ] && [ "$committed" -eq 0 ] &&
     [ -n "$stage_dir" ]; then
    printf 'ROLLBACK: restoring prior corpus and reconstruction evidence.\n' >&2
    restore_build_file \
      "$OUT/fortunes.txt" "$stage_dir/fortunes.before" "$corpus_existed" ||
      rollback_failed=1
    restore_build_file \
      "$EVIDENCE_OUT" "$stage_dir/evidence.before" "$evidence_existed" ||
      rollback_failed=1
  fi

  if [ -n "$stage_dir" ]; then
    case "$stage_dir" in
      "$OUT"/.build-corpus.*) rm -rf -- "$stage_dir" ;;
      *)
        printf 'ERROR: refusing to clean unexpected staging path: %s\n' "$stage_dir" >&2
        rollback_failed=1
        ;;
    esac
  fi
  if [ "$rollback_failed" -ne 0 ]; then
    printf 'ERROR: corpus build rollback or staging cleanup was incomplete.\n' >&2
    status=1
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
stage_dir="$(mktemp -d "$OUT/.build-corpus.XXXXXX")"

evidence_candidate="$stage_dir/fortunes.sources.tsv"
source_evidence="$stage_dir/source-files.tsv"
: > "$source_evidence"
for f in "${ALL_FILES[@]}"; do
  path="$SRC/$f"
  [ -f "$path" ] || die "required curated source disappeared: $f"
  if size="$(stat -c '%s' -- "$path" 2>/dev/null)"; then
    :
  else
    size="$(stat -f '%z' "$path")"
  fi
  printf 'source_file\t%s\t%s\t%s\n' \
    "$f" "$size" "$(sha256_file "$path")" >> "$source_evidence"
done
{
  printf 'schema\t1\n'
  printf 'source_repository\t%s\n' "$SOURCE_REPOSITORY"
  printf 'source_commit\t%s\n' "$SOURCE_COMMIT"
  printf 'source_manifest_sha256\t%s\n' "$source_manifest_sha256"
  printf 'curated_blobs_match\t1\n'
  printf 'source_count\t%s\n' "$source_count"
  printf 'source_total_bytes\t%s\n' "$total_source_bytes"
  cat "$source_evidence"
} > "$evidence_candidate"

raw="$stage_dir/raw.tsv"
if ! (
  for f in "${ALL_FILES[@]}"; do
    emit "$f" || exit $?
  done
) | LC_ALL=C awk \
      -v max_bytes="$MAX_STAGE_BYTES" \
      -v max_rows="$MAX_STAGE_ROWS" '
        {
          next_bytes=bytes + length($0) + 1
          next_rows=rows + 1
          if (next_bytes > max_bytes || next_rows > max_rows) {
            print "staged corpus exceeds the configured resource limit" > "/dev/stderr"
            bad=1
            exit 2
          }
          bytes=next_bytes
          rows=next_rows
          print
        }
        END {
          if (!bad && rows == 0) {
            print "curated sources produced no eligible entries" > "/dev/stderr"
            exit 2
          }
          if (bad) exit 2
        }' > "$raw"; then
  die "failed to parse the source corpus"
fi

source_evidence_after="$stage_dir/source-files.after.tsv"
: > "$source_evidence_after"
for f in "${ALL_FILES[@]}"; do
  path="$SRC/$f"
  [ -f "$path" ] || die "required curated source disappeared after parsing: $f"
  verify_source_blob "$f"
  if size="$(stat -c '%s' -- "$path" 2>/dev/null)"; then
    :
  else
    size="$(stat -f '%z' "$path")"
  fi
  printf 'source_file\t%s\t%s\t%s\n' \
    "$f" "$size" "$(sha256_file "$path")" >> "$source_evidence_after"
done
cmp -s "$source_evidence" "$source_evidence_after" ||
  die "source inputs changed while the corpus was being parsed"
[ "$(sha256_file "$SOURCE_MANIFEST")" = "$source_manifest_sha256" ] ||
  die "required source manifest changed while the corpus was being parsed"

candidate="$stage_dir/fortunes.txt"
if ! LC_ALL=C awk -F'\t' '!seen[$3]++' "$raw" |
     LC_ALL=C sort > "$candidate"; then
  die "failed to deduplicate or sort the staged corpus"
fi

# Strip trailing author/attribution bylines (the pet does not speak "-- Neil Gaiman" tags).
"$PYTHON_BIN" "$SCRIPT_DIR/strip-authors.py" "$candidate"
# Author stripping can collapse distinct attributed rows to the same spoken text. Deduplicate
# again on the normalized text before classification; the prior C-locale sort makes ownership
# deterministic when two sources collapse to the same text.
post_strip="$stage_dir/fortunes.post-strip.tsv"
if ! LC_ALL=C awk -F'\t' '!seen[$3]++' "$candidate" |
     LC_ALL=C sort > "$post_strip"; then
  die "failed to deduplicate the normalized staged corpus"
fi
mv -f -- "$post_strip" "$candidate"
# Tag each line with content level (general/edgy/nsfw) + a profanity flag.
"$PYTHON_BIN" "$SCRIPT_DIR/classify-corpus.py" "$candidate"

if output_bytes="$(stat -c '%s' -- "$candidate" 2>/dev/null)"; then
  :
else
  output_bytes="$(stat -f '%z' "$candidate")"
fi
output_rows="$(awk 'END { print NR + 0 }' "$candidate")"
output_sha256="$(sha256_file "$candidate")"
printf 'output_file\tfortunes.txt\t%s\t%s\t%s\n' \
  "$output_bytes" "$output_rows" "$output_sha256" >> "$evidence_candidate"

if [ -f "$OUT/fortunes.txt" ]; then
  cp -- "$OUT/fortunes.txt" "$stage_dir/fortunes.before"
  corpus_existed=1
else
  : > "$stage_dir/fortunes.before"
fi
if [ -f "$EVIDENCE_OUT" ]; then
  cp -- "$EVIDENCE_OUT" "$stage_dir/evidence.before"
  evidence_existed=1
else
  : > "$stage_dir/evidence.before"
fi

# Promote corpus and evidence as one rollback-protected transaction. Each rename is atomic and
# the signal traps restore both prior files if the process stops between them.
promotion_started=1
mv -f -- "$candidate" "$OUT/fortunes.txt"
mv -f -- "$evidence_candidate" "$EVIDENCE_OUT"
committed=1

echo "Total entries: $(wc -l < "$OUT/fortunes.txt")   ($(wc -c < "$OUT/fortunes.txt") bytes)"
echo "Levels:";     cut -f3 "$OUT/fortunes.txt" | LC_ALL=C sort | uniq -c
echo "Profanity:";  cut -f4 "$OUT/fortunes.txt" | LC_ALL=C sort | uniq -c
echo "Categories:"; cut -f2 "$OUT/fortunes.txt" | LC_ALL=C sort | uniq -c | sort -rn
echo "Sources:";    cut -f1 "$OUT/fortunes.txt" | LC_ALL=C sort | uniq -c | sort -rn
echo "Evidence: $EVIDENCE_OUT"
