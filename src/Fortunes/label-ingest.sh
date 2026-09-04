#!/usr/bin/env bash
# Validate and atomically ingest one ordered topic/genre batch.
set -euo pipefail

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=label-common.sh
. "$SCRIPT_DIR/label-common.sh"

BT="$LABEL_FORTUNE_DIR/.batchtexts"
BATCH="$LABEL_FORTUNE_DIR/label-batch.txt"
META="$LABEL_FORTUNE_DIR/labels-store.meta"

temp_dir=""
transaction_started=0
committed=0
store_existed=0
meta_existed=0
cleanup() {
  local status=$? rollback_failed=0
  trap - EXIT HUP INT TERM
  set +e

  if [ "$transaction_started" -eq 1 ] && [ "$committed" -eq 0 ] &&
     [ -n "$temp_dir" ]; then
    printf 'ROLLBACK: restoring label ingest inputs and outputs.\n' >&2
    label_restore_file "$LABEL_STORE" "$temp_dir/store.before" "$store_existed" ||
      rollback_failed=1
    label_restore_file "$META" "$temp_dir/meta.before" "$meta_existed" ||
      rollback_failed=1
    label_restore_file "$BATCH" "$temp_dir/batch.before" 1 ||
      rollback_failed=1
    label_restore_file "$BT" "$temp_dir/batchtexts.before" 1 ||
      rollback_failed=1
  fi

  if [ -n "$temp_dir" ]; then rm -rf -- "$temp_dir"; fi
  label_release_lock || rollback_failed=1
  if [ "$rollback_failed" -ne 0 ]; then
    printf 'ERROR: label ingest rollback or lock cleanup was incomplete.\n' >&2
    status=1
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

label_acquire_lock
temp_dir="$(mktemp -d "${TMPDIR:-/tmp}/desktopPet-label-ingest.XXXXXX")"

label_validate_input "$LABEL_INPUT"
[ -f "$BT" ] || label_die "missing .batchtexts; run label-next.sh first"
[ -f "$BATCH" ] || label_die "missing label-batch.txt"
if [ -e "$LABEL_STORE" ] || [ -L "$LABEL_STORE" ]; then
  [ -f "$LABEL_STORE" ] || label_die "label store is not a regular file: $LABEL_STORE"
  cp -- "$LABEL_STORE" "$temp_dir/store.before"
  store_existed=1
else
  : > "$temp_dir/store.before"
fi
label_validate_store "$temp_dir/store.before"

expected="$(label_line_count "$BT")"
got="$(label_line_count "$BATCH")"
[ "$expected" -gt 0 ] || label_die ".batchtexts is empty; nothing to ingest"
[ "$got" -eq "$expected" ] ||
  label_die "count mismatch: expected $expected labels, got $got"

normalized_labels="$temp_dir/labels.tsv"
awk -v topics="$LABEL_TOPICS" -v genres="$LABEL_GENRES" '
  NF != 2 || index(topics, "|" $1 "|") == 0 || index(genres, "|" $2 "|") == 0 {
    printf "invalid label row %d\n", FNR > "/dev/stderr"; bad=1
  }
  NF == 2 { print $1 "\t" $2 }
  END { exit bad ? 1 : 0 }
' "$BATCH" > "$normalized_labels" ||
  label_die "batch must contain exactly one locked topic and genre per line"

awk 'length == 0 { printf "blank batch text at row %d\n", FNR > "/dev/stderr"; bad=1 }
     seen[$0]++ { printf "duplicate batch text at row %d\n", FNR > "/dev/stderr"; bad=1 }
     END { exit bad ? 1 : 0 }' "$BT" ||
  label_die ".batchtexts contains blank or duplicate keys"

cut -f2 "$LABEL_INPUT" | LC_ALL=C sort > "$temp_dir/input.keys"
LC_ALL=C sort "$BT" > "$temp_dir/batch.keys"
cut -f1 "$temp_dir/store.before" | LC_ALL=C sort > "$temp_dir/store.keys"
if [ -n "$(comm -23 "$temp_dir/batch.keys" "$temp_dir/input.keys" | sed -n '1p')" ]; then
  label_die "batch contains a text outside label-input.tsv"
fi
if [ -n "$(comm -12 "$temp_dir/batch.keys" "$temp_dir/store.keys" | sed -n '1p')" ]; then
  label_die "batch attempts to relabel text already in the store"
fi

store_next="$temp_dir/store.next"
meta_next="$temp_dir/meta.next"
batch_next="$temp_dir/batch.next"
batchtexts_next="$temp_dir/batchtexts.next"
cp -- "$temp_dir/store.before" "$store_next"
paste "$BT" "$normalized_labels" >> "$store_next"
label_validate_store "$store_next"

cut -f1 "$store_next" | LC_ALL=C sort > "$temp_dir/new-store.keys"
if [ -n "$(comm -23 "$temp_dir/new-store.keys" "$temp_dir/input.keys" | sed -n '1p')" ]; then
  label_die "staged store contains a text outside label-input.tsv"
fi

{
  printf 'schema=%s\n' "$LABEL_SCHEMA_VERSION"
  printf 'taxonomy=%s\n' "$LABEL_TAXONOMY_VERSION"
  printf 'rows=%s\n' "$(label_line_count "$store_next")"
  printf 'sha256=%s\n' "$(label_sha256 "$store_next")"
  printf 'input_sha256=%s\n' "$(label_sha256 "$LABEL_INPUT")"
} > "$meta_next"
: > "$batch_next"
: > "$batchtexts_next"

if [ -e "$META" ] || [ -L "$META" ]; then
  [ -f "$META" ] || label_die "label metadata is not a regular file: $META"
  cp -- "$META" "$temp_dir/meta.before"
  meta_existed=1
else
  : > "$temp_dir/meta.before"
fi
cp -- "$BATCH" "$temp_dir/batch.before"
cp -- "$BT" "$temp_dir/batchtexts.before"

transaction_started=1
mv -f -- "$store_next" "$LABEL_STORE" ||
  label_die "could not promote the staged label store"
mv -f -- "$meta_next" "$META" ||
  label_die "could not promote the staged label metadata"
mv -f -- "$batch_next" "$BATCH" ||
  label_die "could not clear the ingested label batch"
mv -f -- "$batchtexts_next" "$BT" ||
  label_die "could not clear the ingested batch texts"
committed=1

printf 'ingested=%s total=%s/%s store_sha256=%s\n' \
  "$got" "$(label_line_count "$LABEL_STORE")" "$(label_line_count "$LABEL_INPUT")" \
  "$(label_sha256 "$LABEL_STORE")"
