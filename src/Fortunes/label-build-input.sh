#!/usr/bin/env bash
# Deterministically build the frozen full-pass input: source-hint<TAB>unique text.
# Existing labeling progress is preserved only while every stored text remains in the input.
set -euo pipefail

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=label-common.sh
. "$SCRIPT_DIR/label-common.sh"

META="$LABEL_FORTUNE_DIR/label-input.meta"
tmp_dir=""
input_tmp=""
meta_tmp=""
store_tmp=""
transaction_started=0
committed=0
input_existed=0
meta_existed=0
store_existed=0
cleanup() {
  local status=$? rollback_failed=0
  trap - EXIT HUP INT TERM
  set +e

  if [ "$transaction_started" -eq 1 ] && [ "$committed" -eq 0 ] &&
     [ -n "$tmp_dir" ]; then
    printf 'ROLLBACK: restoring labeling input and metadata.\n' >&2
    label_restore_file "$LABEL_INPUT" "$tmp_dir/input.before" "$input_existed" ||
      rollback_failed=1
    label_restore_file "$META" "$tmp_dir/meta.before" "$meta_existed" ||
      rollback_failed=1
    if [ "$store_existed" -eq 0 ]; then
      rm -f -- "$LABEL_STORE" || rollback_failed=1
    fi
  fi

  if [ -n "$input_tmp" ]; then rm -f -- "$input_tmp"; fi
  if [ -n "$meta_tmp" ]; then rm -f -- "$meta_tmp"; fi
  if [ -n "$store_tmp" ]; then rm -f -- "$store_tmp"; fi
  if [ -n "$tmp_dir" ]; then rm -rf -- "$tmp_dir"; fi
  label_release_lock || rollback_failed=1
  if [ "$rollback_failed" -ne 0 ]; then
    printf 'ERROR: label input rollback or lock cleanup was incomplete.\n' >&2
    status=1
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

label_acquire_lock
tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/desktopPet-label-input.XXXXXX")"

files=("$LABEL_FORTUNE_DIR/fortunes.txt")
shopt -s nullglob
packs=("$LABEL_PACK_DIR"/*.txt)
shopt -u nullglob
files+=("${packs[@]}")
[ "${#files[@]}" -gt 0 ] || label_die "no corpus files found"

: > "$tmp_dir/all.tsv"
for file in "${files[@]}"; do
  [ -f "$file" ] || label_die "missing corpus file: $file"
  hint="$(basename "$file" .txt)"
  awk -F'\t' -v hint="$hint" '
    NF == 5 { print hint "\t" $5; next }
    NF == 6 { print hint "\t" $6; next }
    { printf "%s: unsupported field count at row %d\n", FILENAME, FNR > "/dev/stderr"; bad=1 }
    END { exit bad ? 1 : 0 }
  ' "$file" >> "$tmp_dir/all.tsv" || label_die "cannot build input from malformed corpus data"
done

# Sort every candidate before deduplication so traversal order and the caller's locale cannot
# decide which source hint survives. The bytewise-lowest hint wins; output stays text-ordered.
LC_ALL=C sort -t $'\t' -k2,2 -k1,1 "$tmp_dir/all.tsv" > "$tmp_dir/all.sorted.tsv"
LC_ALL=C awk -F'\t' '!seen[$2]++' "$tmp_dir/all.sorted.tsv" > "$tmp_dir/input.tsv"
label_validate_input "$tmp_dir/input.tsv"

if [ -e "$LABEL_STORE" ] || [ -L "$LABEL_STORE" ]; then
  [ -f "$LABEL_STORE" ] ||
    label_die "label store is not a regular file: $LABEL_STORE"
  store_existed=1
  if [ -s "$LABEL_STORE" ]; then
    label_validate_store "$LABEL_STORE"
    cut -f1 "$LABEL_STORE" | LC_ALL=C sort > "$tmp_dir/store.keys"
    cut -f2 "$tmp_dir/input.tsv" | LC_ALL=C sort > "$tmp_dir/input.keys"
    if ! comm -12 "$tmp_dir/store.keys" "$tmp_dir/input.keys" |
         cmp -s - "$tmp_dir/store.keys"; then
      label_die "existing store contains texts outside the rebuilt input; refusing to replace input"
    fi
  fi
else
  store_tmp="$(mktemp "$LABEL_STORE.tmp.XXXXXX")"
  : > "$store_tmp"
fi

input_tmp="$(mktemp "$LABEL_INPUT.tmp.XXXXXX")"
meta_tmp="$(mktemp "$META.tmp.XXXXXX")"
cp -- "$tmp_dir/input.tsv" "$input_tmp"
{
  printf 'schema=%s\n' "$LABEL_SCHEMA_VERSION"
  printf 'taxonomy=%s\n' "$LABEL_TAXONOMY_VERSION"
  printf 'rows=%s\n' "$(label_line_count "$input_tmp")"
  printf 'sha256=%s\n' "$(label_sha256 "$input_tmp")"
} > "$meta_tmp"

if [ -e "$LABEL_INPUT" ] || [ -L "$LABEL_INPUT" ]; then
  [ -f "$LABEL_INPUT" ] ||
    label_die "labeling input is not a regular file: $LABEL_INPUT"
  cp -- "$LABEL_INPUT" "$tmp_dir/input.before"
  input_existed=1
else
  : > "$tmp_dir/input.before"
fi
if [ -e "$META" ] || [ -L "$META" ]; then
  [ -f "$META" ] || label_die "label input metadata is not a regular file: $META"
  cp -- "$META" "$tmp_dir/meta.before"
  meta_existed=1
else
  : > "$tmp_dir/meta.before"
fi

transaction_started=1
mv -f -- "$input_tmp" "$LABEL_INPUT" ||
  label_die "could not promote the staged labeling input"
mv -f -- "$meta_tmp" "$META" ||
  label_die "could not promote the staged labeling input metadata"
if [ "$store_existed" -eq 0 ]; then
  mv -f -- "$store_tmp" "$LABEL_STORE" ||
    label_die "could not create the empty label store"
fi
committed=1

printf 'unique fortunes to label: %s (already labeled: %s)\n' \
  "$(label_line_count "$LABEL_INPUT")" "$(label_line_count "$LABEL_STORE")"
printf 'input sha256: %s  taxonomy: %s\n' \
  "$(label_sha256 "$LABEL_INPUT")" "$LABEL_TAXONOMY_VERSION"
