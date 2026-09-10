#!/usr/bin/env bash
# Assemble the bundled fortune corpus (src/Fortunes/fortunes.txt) from its declared inputs.
#
#   ./build-corpus.sh            # rebuild fortunes.txt
#   ./build-corpus.sh --check    # verify the committed file matches, change nothing
#
# WHAT THIS REPLACED, AND WHY IT HAD TO CHANGE
#
# This script used to build the corpus from a pinned clone of JKirchartz/fortunes: 69 upstream BSD
# fortune files, parsed, deduplicated, classified, and emitted as schema v1
# (source/category/level/prof/text). That script could no longer reproduce the committed corpus, and
# had not been able to for some time:
#
#   * The committed corpus is schema v2 (source/topic/genre/level/prof/text). The topic and genre
#     columns came from a one-time labeling pass driven by label-apply.sh, whose input store
#     (label-input.tsv, labels-store.tsv, label-chunks/) is gitignored and no longer exists. Nothing
#     can regenerate those two columns from the upstream text. The committed rows ARE the labels.
#   * The corpus no longer contains the upstream set. It is two sources: Classic Fortunes and Dad
#     Jokes. Neither is reachable from the old manifest, and 46 of the manifest's 69 entries had
#     already been moved out to downloadable packs.
#
# So running the old script would have deleted the labeled corpus and replaced it with unlabeled v1
# rows from a different source set. It was a loaded gun pointed at the only copy.
#
# WHAT THIS DOES INSTEAD
#
# Every input is already labeled v2 data that lives in the repository, so the build is a pure
# assembly: concatenate, deduplicate on text, sort, validate, write. It is deterministic and
# idempotent -- running it on a clean tree reproduces the committed file byte for byte, which
# --check asserts in CI or by hand.
#
# Inputs are named by source id in corpus-required-files.txt and resolved in this order:
#   1. sources/<id>.tsv   primary labeled data owned by the corpus (no pack carries it)
#   2. ../../packs/<id>.txt   a downloadable pack that also ships in the box
#
# Adding a source to the corpus means adding its id to the manifest and putting labeled v2 rows at
# one of those two paths. There is no path back to the upstream fortune files, by design: that data
# is now carried as packs, and the labels are not regenerable.
set -euo pipefail

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd)"
MANIFEST="$SCRIPT_DIR/corpus-required-files.txt"
OUT="$SCRIPT_DIR/fortunes.txt"
CHECK_ONLY=0
[ "${1:-}" = "--check" ] && CHECK_ONLY=1

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[ -f "$MANIFEST" ] && [ ! -L "$MANIFEST" ] || die "manifest missing or unsafe: $MANIFEST"

# --- resolve the manifest to input files -----------------------------------------------------
ids=()
while IFS= read -r id || [ -n "$id" ]; do
  [ -n "$id" ] || continue
  [[ "$id" =~ ^[A-Za-z0-9._-]+$ ]] || die "invalid manifest entry: '$id'"
  for seen in ${ids[@]+"${ids[@]}"}; do
    [ "$seen" != "$id" ] || die "duplicate manifest entry: $id"
  done
  ids+=("$id")
done < "$MANIFEST"
[ "${#ids[@]}" -gt 0 ] || die "manifest lists no sources"

inputs=()
for id in "${ids[@]}"; do
  primary="$SCRIPT_DIR/sources/$id.tsv"
  pack="$REPO_ROOT/packs/$id.txt"
  if   [ -f "$primary" ]; then inputs+=("$primary")
  elif [ -f "$pack" ];    then inputs+=("$pack")
  else die "no input for source '$id' (looked for sources/$id.tsv and packs/$id.txt)"
  fi
done

stage_dir="$(mktemp -d "$SCRIPT_DIR/.build-corpus.XXXXXX")"
cleanup() {
  local status=$?
  case "$stage_dir" in
    "$SCRIPT_DIR"/.build-corpus.*) rm -rf -- "$stage_dir" ;;
    *) printf 'ERROR: refusing to clean unexpected staging path: %s\n' "$stage_dir" >&2; status=1 ;;
  esac
  exit "$status"
}
trap cleanup EXIT HUP INT TERM

# --- validate every input before combining anything -------------------------------------------
# A row whose source column disagrees with the file it came from would silently mislabel the picker,
# and a short row would shift every later column left. Both are cheap to check and awful to debug.
raw="$stage_dir/raw.tsv"
: > "$raw"
for i in "${!ids[@]}"; do
  id="${ids[$i]}" file="${inputs[$i]}"
  LC_ALL=C awk -F'\t' -v id="$id" -v file="$file" '
    NF != 6 { printf "%s:%d: expected 6 tab-separated fields, found %d\n", file, FNR, NF > "/dev/stderr"; bad=1; exit 2 }
    $1 != id { printf "%s:%d: source column is \"%s\", expected \"%s\"\n", file, FNR, $1, id > "/dev/stderr"; bad=1; exit 2 }
    length($6) < 8 || length($6) > 280 { printf "%s:%d: text length %d outside 8..280\n", file, FNR, length($6) > "/dev/stderr"; bad=1; exit 2 }
    { print }
    END { if (!bad && FNR == 0) { print file ": no rows" > "/dev/stderr"; exit 2 } }
  ' "$file" >> "$raw" || die "input failed validation: $file"
done

# Deduplicate on spoken text, not on the whole row: the same line reaching the bubble twice under two
# source labels is one fortune to a user. Manifest order decides the winner, and the sort is C-locale
# so the result does not depend on the builder's locale.
candidate="$stage_dir/fortunes.txt"
LC_ALL=C awk -F'\t' '!seen[$6]++' "$raw" | LC_ALL=C sort -t"$(printf '\t')" -k1,1 -k6,6 > "$candidate"

rows="$(wc -l < "$candidate")"
[ "$rows" -gt 0 ] || die "assembly produced no rows"

if [ "$CHECK_ONLY" -eq 1 ]; then
  [ -f "$OUT" ] || die "--check: $OUT does not exist"
  if cmp -s "$candidate" "$OUT"; then
    printf 'OK: %s matches its %d input source(s), %s rows.\n' "$(basename "$OUT")" "${#ids[@]}" "$rows"
    exit 0
  fi
  printf 'STALE: %s does not match its declared inputs. Run build-corpus.sh to rebuild.\n' "$OUT" >&2
  diff <(cut -f1 "$OUT" | LC_ALL=C sort | uniq -c) \
       <(cut -f1 "$candidate" | LC_ALL=C sort | uniq -c) >&2 || true
  exit 1
fi

mv -f -- "$candidate" "$OUT"
printf 'Wrote %s: %s rows, %s bytes, from %d source(s).\n' \
  "$(basename "$OUT")" "$rows" "$(wc -c < "$OUT")" "${#ids[@]}"
printf 'Sources:\n'
cut -f1 "$OUT" | LC_ALL=C sort | uniq -c | sort -rn
printf '\nThe corpus is embedded in modules/Fortunes. Rebuild and republish that module for a change\nhere to reach anyone: packaging/New-ModulePublish.ps1 -ModuleId fortunes -Commit\n'
