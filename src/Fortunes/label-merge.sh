#!/usr/bin/env bash
# Validate every chunk and atomically rebuild labels-store.tsv only after full success.
set -euo pipefail

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=label-common.sh
. "$SCRIPT_DIR/label-common.sh"

CHUNKS="$LABEL_FORTUNE_DIR/label-chunks"
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
    printf 'ROLLBACK: restoring the prior merged label store and metadata.\n' >&2
    label_restore_file "$LABEL_STORE" "$temp_dir/store.before" "$store_existed" ||
      rollback_failed=1
    label_restore_file "$META" "$temp_dir/meta.before" "$meta_existed" ||
      rollback_failed=1
  fi

  if [ -n "$temp_dir" ]; then rm -rf -- "$temp_dir"; fi
  label_release_lock || rollback_failed=1
  if [ "$rollback_failed" -ne 0 ]; then
    printf 'ERROR: label merge rollback or lock cleanup was incomplete.\n' >&2
    status=1
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

label_acquire_lock
temp_dir="$(mktemp -d "${TMPDIR:-/tmp}/desktopPet-label-merge.XXXXXX")"

label_validate_input "$LABEL_INPUT"
[ -d "$CHUNKS" ] || label_die "missing chunk directory: $CHUNKS"

store_tmp="$temp_dir/store.next"
meta_tmp="$temp_dir/meta.next"
: > "$store_tmp"

shopt -s nullglob
chunk_files=("$CHUNKS"/chunk*.tsv)
output_files=("$CHUNKS"/chunk*.out)
shopt -u nullglob
[ "${#chunk_files[@]}" -gt 0 ] || label_die "no chunk*.tsv files found"

passed=0
failed=0
missing=0
failure_details=()
for out in "${output_files[@]}"; do
  name="$(basename "$out" .out)"
  if [ ! -f "$CHUNKS/$name.tsv" ]; then
    failed=$((failed + 1))
    failure_details+=("$name(extra-output)")
  fi
done
for tsv in "${chunk_files[@]}"; do
  name="$(basename "$tsv" .tsv)"
  out="$CHUNKS/$name.out"
  expected="$(label_line_count "$tsv")"

  if [ ! -f "$out" ]; then
    missing=$((missing + 1))
    failure_details+=("$name(missing)")
    continue
  fi

  got="$(label_line_count "$out")"
  if [ "$got" -ne "$expected" ]; then
    failed=$((failed + 1))
    failure_details+=("$name(count=$got/$expected)")
    continue
  fi

  if ! awk -F'\t' '
      NF != 2 || $1 != FNR || $2 == "" { bad=1 }
      END { exit bad ? 1 : 0 }
    ' "$tsv"; then
    failed=$((failed + 1))
    failure_details+=("$name(invalid-input-order)")
    continue
  fi

  if ! awk -v topics="$LABEL_TOPICS" -v genres="$LABEL_GENRES" '
      NF != 3 || $1 != FNR ||
        index(topics, "|" $2 "|") == 0 ||
        index(genres, "|" $3 "|") == 0 { bad=1 }
      END { exit bad ? 1 : 0 }
    ' "$out"; then
    failed=$((failed + 1))
    failure_details+=("$name(invalid-labels-or-order)")
    continue
  fi

  paste <(cut -f2 "$tsv") <(awk '{ print $2 "\t" $3 }' "$out") >> "$store_tmp"
  passed=$((passed + 1))
done

printf 'chunks: passed=%s failed=%s missing=%s\n' "$passed" "$failed" "$missing"
if [ "${#failure_details[@]}" -gt 0 ]; then
  printf 'NEEDS REDO:'
  printf ' %s' "${failure_details[@]}"
  printf '\n'
fi
if [ "$failed" -gt 0 ] || [ "$missing" -gt 0 ]; then
  printf 'INCOMPLETE: prior store was not modified.\n' >&2
  exit 1
fi

label_validate_store "$store_tmp"
cut -f1 "$store_tmp" > "$temp_dir/merged-order"
cut -f2 "$LABEL_INPUT" > "$temp_dir/input-order"
if ! cmp -s "$temp_dir/merged-order" "$temp_dir/input-order"; then
  label_die "chunk texts do not exactly reproduce label-input.tsv order; prior store was not modified"
fi
label_assert_exact_store_keys "$LABEL_INPUT" "$store_tmp" "$temp_dir"

{
  printf 'schema=%s\n' "$LABEL_SCHEMA_VERSION"
  printf 'taxonomy=%s\n' "$LABEL_TAXONOMY_VERSION"
  printf 'rows=%s\n' "$(label_line_count "$store_tmp")"
  printf 'sha256=%s\n' "$(label_sha256 "$store_tmp")"
  printf 'input_sha256=%s\n' "$(label_sha256 "$LABEL_INPUT")"
} > "$meta_tmp"

if [ -e "$LABEL_STORE" ] || [ -L "$LABEL_STORE" ]; then
  [ -f "$LABEL_STORE" ] || label_die "label store is not a regular file: $LABEL_STORE"
  cp -- "$LABEL_STORE" "$temp_dir/store.before"
  store_existed=1
else
  : > "$temp_dir/store.before"
fi
if [ -e "$META" ] || [ -L "$META" ]; then
  [ -f "$META" ] || label_die "label metadata is not a regular file: $META"
  cp -- "$META" "$temp_dir/meta.before"
  meta_existed=1
else
  : > "$temp_dir/meta.before"
fi

transaction_started=1
mv -f -- "$store_tmp" "$LABEL_STORE"
mv -f -- "$meta_tmp" "$META"
committed=1
printf 'COMPLETE: %s labels, exact key set, store_sha256=%s\n' \
  "$(label_line_count "$LABEL_STORE")" "$(label_sha256 "$LABEL_STORE")"
