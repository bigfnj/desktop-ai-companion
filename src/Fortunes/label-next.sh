#!/usr/bin/env bash
# Write the next batch of still-unlabeled texts to .batchtexts.
# Usage: ./label-next.sh [batch-size]   (default 500)
set -euo pipefail

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=label-common.sh
. "$SCRIPT_DIR/label-common.sh"

BT="$LABEL_FORTUNE_DIR/.batchtexts"
B="${1:-500}"
case "$B" in
  ''|*[!0-9]*) label_die "batch size must be a positive integer" ;;
esac
[ "$B" -gt 0 ] || label_die "batch size must be greater than zero"

check_dir=""
batch_tmp=""
transaction_started=0
committed=0
batchtexts_existed=0
cleanup() {
  local status=$? rollback_failed=0
  trap - EXIT HUP INT TERM
  set +e

  if [ "$transaction_started" -eq 1 ] && [ "$committed" -eq 0 ] &&
     [ -n "$check_dir" ]; then
    printf 'ROLLBACK: restoring the prior batch-text snapshot.\n' >&2
    label_restore_file \
      "$BT" "$check_dir/batchtexts.before" "$batchtexts_existed" ||
      rollback_failed=1
  fi

  if [ -n "$batch_tmp" ]; then rm -f -- "$batch_tmp"; fi
  if [ -n "$check_dir" ]; then rm -rf -- "$check_dir"; fi
  label_release_lock || rollback_failed=1
  if [ "$rollback_failed" -ne 0 ]; then
    printf 'ERROR: label-next rollback or lock cleanup was incomplete.\n' >&2
    status=1
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

label_acquire_lock
check_dir="$(mktemp -d "${TMPDIR:-/tmp}/desktopPet-label-next.XXXXXX")"

label_validate_input "$LABEL_INPUT"
if [ -e "$LABEL_STORE" ] || [ -L "$LABEL_STORE" ]; then
  [ -f "$LABEL_STORE" ] ||
    label_die "label store is not a regular file: $LABEL_STORE"
  store_source="$LABEL_STORE"
else
  store_source="$check_dir/empty-store.tsv"
  : > "$store_source"
fi
label_validate_store "$store_source"

batch_tmp="$(mktemp "$BT.tmp.XXXXXX")"

cut -f1 "$store_source" | LC_ALL=C sort > "$check_dir/store.keys"
cut -f2 "$LABEL_INPUT" | LC_ALL=C sort > "$check_dir/input.keys"
if [ -s "$check_dir/store.keys" ] &&
   [ -n "$(comm -23 "$check_dir/store.keys" "$check_dir/input.keys" | sed -n '1p')" ]; then
  label_die "store contains a text that is not in label-input.tsv"
fi

# The explicit FILENAME test is required when the store is empty: NR==FNR is ambiguous then.
awk -F'\t' -v storefile="$store_source" -v limit="$B" '
  FILENAME == storefile { done[$1]=1; next }
  !($2 in done) {
    print $2
    emitted++
    if (emitted >= limit) exit
  }
' "$store_source" "$LABEL_INPUT" > "$batch_tmp"

if [ -e "$BT" ] || [ -L "$BT" ]; then
  [ -f "$BT" ] || label_die "batch-text snapshot is not a regular file: $BT"
  cp -- "$BT" "$check_dir/batchtexts.before"
  batchtexts_existed=1
else
  : > "$check_dir/batchtexts.before"
fi

transaction_started=1
mv -f -- "$batch_tmp" "$BT" ||
  label_die "could not promote the staged batch-text snapshot"
committed=1

n="$(label_line_count "$BT")"
labeled="$(label_line_count "$store_source")"
total="$(label_line_count "$LABEL_INPUT")"
if [ "$n" -eq 0 ]; then
  printf '### ALL %s LABELED. Run label-apply.sh. ###\n' "$total"
  exit 0
fi
printf "### BATCH ready: %s lines written to .batchtexts (progress %s/%s). Read it, emit EXACTLY %s 'topic genre' lines IN ORDER. ###\n" \
  "$n" "$labeled" "$total" "$n"
