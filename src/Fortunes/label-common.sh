#!/usr/bin/env bash
# Shared validation and transaction helpers for the fortune labeling pipeline.
set -euo pipefail

LABEL_SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
LABEL_PROJECT_ROOT="$(CDPATH= cd -- "$LABEL_SCRIPT_DIR/../.." && pwd)"
LABEL_FORTUNE_DIR="$LABEL_SCRIPT_DIR"
LABEL_PACK_DIR="$LABEL_PROJECT_ROOT/packs"
LABEL_INPUT="$LABEL_FORTUNE_DIR/label-input.tsv"
LABEL_STORE="$LABEL_FORTUNE_DIR/labels-store.tsv"
LABEL_TAXONOMY_VERSION="2026-07-31"
LABEL_SCHEMA_VERSION="2"

LABEL_TOPICS="|tech|science|work-money|love|family|faith|society|food|nature|arts|health-body|life|"
LABEL_GENRES="|tv-quote|observation|joke|pun|quip|aphorism|wisdom|fact|insult|verse|dark|uplifting|"
LABEL_LEVELS="|general|edgy|nsfw|"
LABEL_LOCK_DIR="$LABEL_FORTUNE_DIR/.label-pipeline.lock"
LABEL_LOCK_HELD=0
LABEL_LOCK_TOKEN=""

label_die() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

label_acquire_lock() {
  [ "$LABEL_LOCK_HELD" -eq 0 ] ||
    label_die "label pipeline lock is already held by this process"

  LABEL_LOCK_TOKEN="pid=$$;shell=${BASHPID:-$$};command=${0##*/}"
  if ! mkdir -- "$LABEL_LOCK_DIR" 2>/dev/null; then
    owner=""
    if [ -f "$LABEL_LOCK_DIR/owner" ]; then
      IFS= read -r owner < "$LABEL_LOCK_DIR/owner" || owner=""
    fi
    if [ -n "$owner" ]; then
      label_die "label pipeline is already locked ($owner)"
    fi
    label_die "label pipeline is already locked: $LABEL_LOCK_DIR"
  fi

  LABEL_LOCK_HELD=1
  if ! printf '%s\n' "$LABEL_LOCK_TOKEN" > "$LABEL_LOCK_DIR/owner"; then
    LABEL_LOCK_HELD=0
    rm -f -- "$LABEL_LOCK_DIR/owner"
    rmdir -- "$LABEL_LOCK_DIR" 2>/dev/null || true
    label_die "could not record label pipeline lock ownership"
  fi
}

label_release_lock() {
  local owner=""
  [ "$LABEL_LOCK_HELD" -eq 1 ] || return 0

  if [ -f "$LABEL_LOCK_DIR/owner" ]; then
    IFS= read -r owner < "$LABEL_LOCK_DIR/owner" || owner=""
  fi
  if [ -z "$owner" ]; then
    rm -f -- "$LABEL_LOCK_DIR/owner"
    if ! rmdir -- "$LABEL_LOCK_DIR"; then
      printf 'ERROR: could not remove incomplete label pipeline lock: %s\n' \
        "$LABEL_LOCK_DIR" >&2
      LABEL_LOCK_HELD=0
      return 1
    fi
    LABEL_LOCK_HELD=0
    return 0
  fi
  if [ "$owner" != "$LABEL_LOCK_TOKEN" ]; then
    printf 'ERROR: refusing to release a label lock owned by another process: %s\n' \
      "$LABEL_LOCK_DIR" >&2
    LABEL_LOCK_HELD=0
    return 1
  fi

  rm -f -- "$LABEL_LOCK_DIR/owner"
  if ! rmdir -- "$LABEL_LOCK_DIR"; then
    printf 'ERROR: could not remove label pipeline lock: %s\n' "$LABEL_LOCK_DIR" >&2
    LABEL_LOCK_HELD=0
    return 1
  fi
  LABEL_LOCK_HELD=0
}

label_restore_file() {
  local target="$1" backup="$2" existed="$3" restore
  if [ "$existed" -eq 1 ]; then
    restore="$(mktemp "$target.label-restore.XXXXXX")" || return 1
    if ! cp -- "$backup" "$restore" || ! mv -f -- "$restore" "$target"; then
      rm -f -- "$restore"
      return 1
    fi
  else
    rm -f -- "$target" || return 1
  fi
}

label_line_count() {
  awk 'END { print NR + 0 }' "$1"
}

label_sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
  else
    label_die "sha256sum or shasum is required"
  fi
}

label_validate_input() {
  local file="$1"
  [ -f "$file" ] || label_die "missing labeling input: $file"
  awk -F'\t' '
    NF != 2 || $1 == "" || $2 == "" { printf "invalid input row %d\n", FNR > "/dev/stderr"; bad=1 }
    seen[$2]++ { printf "duplicate input text at row %d\n", FNR > "/dev/stderr"; bad=1 }
    END { exit bad ? 1 : 0 }
  ' "$file" || label_die "label-input.tsv must contain unique source-hint<TAB>text rows"
}

label_validate_store() {
  local file="$1"
  [ -f "$file" ] || label_die "missing label store: $file"
  awk -F'\t' -v topics="$LABEL_TOPICS" -v genres="$LABEL_GENRES" '
    NF != 3 || $1 == "" ||
      index(topics, "|" $2 "|") == 0 ||
      index(genres, "|" $3 "|") == 0 {
        printf "invalid store row %d\n", FNR > "/dev/stderr"; bad=1
      }
    seen[$1]++ { printf "duplicate store text at row %d\n", FNR > "/dev/stderr"; bad=1 }
    END { exit bad ? 1 : 0 }
  ' "$file" || label_die "labels-store.tsv has invalid, duplicate, or unlocked labels"
}

label_assert_exact_store_keys() {
  local input="$1" store="$2" temp_dir="$3"
  label_validate_input "$input"
  label_validate_store "$store"
  cut -f2 "$input" | LC_ALL=C sort > "$temp_dir/input.keys"
  cut -f1 "$store" | LC_ALL=C sort > "$temp_dir/store.keys"
  if ! cmp -s "$temp_dir/input.keys" "$temp_dir/store.keys"; then
    comm -23 "$temp_dir/input.keys" "$temp_dir/store.keys" | sed -n '1,5p' |
      sed 's/^/missing: /' >&2
    comm -13 "$temp_dir/input.keys" "$temp_dir/store.keys" | sed -n '1,5p' |
      sed 's/^/extra: /' >&2
    label_die "label store key set does not exactly match label input"
  fi
}

label_validate_six_column_file() {
  local file="$1"
  awk -F'\t' -v topics="$LABEL_TOPICS" -v genres="$LABEL_GENRES" -v levels="$LABEL_LEVELS" '
    NF != 6 || $1 == "" || $6 == "" ||
      index(topics, "|" $2 "|") == 0 ||
      index(genres, "|" $3 "|") == 0 ||
      index(levels, "|" $4 "|") == 0 ||
      ($5 != "0" && $5 != "1") {
        printf "%s: invalid schema-v2 row %d\n", FILENAME, FNR > "/dev/stderr"; bad=1
      }
    END { exit bad ? 1 : 0 }
  ' "$file" || label_die "invalid schema-v2 output: $file"
}
