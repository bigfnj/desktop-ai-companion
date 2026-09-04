#!/usr/bin/env bash
# Apply a complete text->topic/genre store as schema-v2 data:
# source<TAB>topic<TAB>genre<TAB>level<TAB>prof<TAB>text.
# Every file is staged and validated before any target is atomically replaced.
set -euo pipefail

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=label-common.sh
. "$SCRIPT_DIR/label-common.sh"

apply_mode=""
metadata_plan=""
metadata_acknowledged=0
while [ "$#" -gt 0 ]; do
  case "$1" in
    --go)
      [ -z "$apply_mode" ] || label_die "choose exactly one apply mode"
      apply_mode="go"
      ;;
    --emit-plan)
      [ -z "$apply_mode" ] || label_die "choose exactly one apply mode"
      apply_mode="emit"
      ;;
    --metadata-plan)
      shift
      [ "$#" -gt 0 ] || label_die "--metadata-plan requires a path"
      metadata_plan="$1"
      ;;
    --acknowledge-metadata-finalization)
      metadata_acknowledged=1
      ;;
    *)
      label_die "unknown label-apply argument: $1"
      ;;
  esac
  shift
done
[ -n "$apply_mode" ] ||
  label_die "usage: label-apply.sh --emit-plan | --go --metadata-plan PATH --acknowledge-metadata-finalization"
if [ "$apply_mode" = "emit" ]; then
  [ -z "$metadata_plan" ] && [ "$metadata_acknowledged" -eq 0 ] ||
    label_die "--emit-plan does not accept apply acknowledgements"
fi

# Schema conversion invalidates every corpus/pack hash and the documents that identify those
# bytes. Applying labels never edits these dependent files automatically; the reviewed plan and
# explicit finalization acknowledgement make that follow-up work impossible to overlook.
DEPENDENT_METADATA=(
  "catalog.json"
  "packs/collections.json"
  "packaging/source-assets.json"
  "packaging/source-rights-evidence.json"
  "THIRD_PARTY_NOTICES.md"
  "PROVENANCE.md"
  "Readme.md"
  "src/Fortunes/TAXONOMY.md"
)

temp_dir=""
next_files=()
targets=()
promoted=0
promotion_inflight=-1
committed=0
cleanup() {
  local status=$? rollback_failed=0 rollback_last rollback
  trap - EXIT HUP INT TERM
  set +e

  if [ "$committed" -eq 0 ] && [ -n "$temp_dir" ]; then
    rollback_last=$((promoted - 1))
    if [ "$promotion_inflight" -ge "$promoted" ]; then
      rollback_last="$promotion_inflight"
    fi
    if [ "$rollback_last" -ge 0 ]; then
      printf 'ROLLBACK: restoring %s corpus file(s).\n' \
        "$((rollback_last + 1))" >&2
      for ((rollback=0; rollback<=rollback_last; rollback++)); do
        label_restore_file \
          "${targets[$rollback]}" "$temp_dir/backup/$rollback.tsv" 1 ||
          rollback_failed=1
      done
    fi
  fi

  if [ "${#next_files[@]}" -gt 0 ]; then rm -f -- "${next_files[@]}"; fi
  if [ -n "$temp_dir" ]; then rm -rf -- "$temp_dir"; fi
  label_release_lock || rollback_failed=1
  if [ "$rollback_failed" -ne 0 ]; then
    printf 'ERROR: label apply rollback or lock cleanup was incomplete.\n' >&2
    status=1
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

label_acquire_lock
temp_dir="$(mktemp -d "${TMPDIR:-/tmp}/desktopPet-label-apply.XXXXXX")"

label_assert_exact_store_keys "$LABEL_INPUT" "$LABEL_STORE" "$temp_dir"

targets=("$LABEL_FORTUNE_DIR/fortunes.txt")
shopt -s nullglob
packs=("$LABEL_PACK_DIR"/*.txt)
shopt -u nullglob
targets+=("${packs[@]}")
[ "${#targets[@]}" -gt 0 ] || label_die "no corpus files found"

mkdir -p -- "$temp_dir/stage" "$temp_dir/backup"
: > "$temp_dir/all-corpus-texts"
staged=()

expected_plan="$temp_dir/expected-metadata-plan.tsv"
{
  printf 'schema\t1\n'
  printf 'operation\tschema-v2-finalization\n'
  printf 'label_input_sha256\t%s\n' "$(label_sha256 "$LABEL_INPUT")"
  printf 'label_store_sha256\t%s\n' "$(label_sha256 "$LABEL_STORE")"
  printf 'expected_output_schema\t%s\n' "$LABEL_SCHEMA_VERSION"
  printf 'acknowledge_metadata_finalization\ttrue\n'
  for target in "${targets[@]}"; do
    relative_target="${target#"$LABEL_PROJECT_ROOT"/}"
    [ "$relative_target" != "$target" ] ||
      label_die "corpus target is outside the project root: $target"
    printf 'target\t%s\t%s\n' \
      "$relative_target" "$(label_sha256 "$target")"
  done
  for relative_dependency in "${DEPENDENT_METADATA[@]}"; do
    dependency="$LABEL_PROJECT_ROOT/$relative_dependency"
    [ -f "$dependency" ] && [ ! -L "$dependency" ] ||
      label_die "dependent metadata is missing or unsafe: $relative_dependency"
    printf 'dependency\t%s\t%s\n' \
      "$relative_dependency" "$(label_sha256 "$dependency")"
  done
} > "$expected_plan"

if [ "$apply_mode" = "emit" ]; then
  cat "$expected_plan"
  exit 0
fi

[ -n "$metadata_plan" ] ||
  label_die "schema conversion refused: generate and review --emit-plan, then supply --metadata-plan"
[ "$metadata_acknowledged" -eq 1 ] ||
  label_die "schema conversion refused: --acknowledge-metadata-finalization is required"
[ -f "$metadata_plan" ] && [ ! -L "$metadata_plan" ] ||
  label_die "metadata finalization plan is missing or unsafe: $metadata_plan"
plan_bytes="$(wc -c < "$metadata_plan")"
[ "$plan_bytes" -gt 0 ] && [ "$plan_bytes" -le 1048576 ] ||
  label_die "metadata finalization plan size is outside 1..1048576 bytes"
awk -F'\t' '
  ($1 == "target" || $1 == "dependency") && NF == 3 { next }
  ($1 == "schema" ||
   $1 == "operation" ||
   $1 == "label_input_sha256" ||
   $1 == "label_store_sha256" ||
   $1 == "expected_output_schema" ||
   $1 == "acknowledge_metadata_finalization") && NF == 2 { next }
  { bad=1 }
  END { exit bad ? 1 : 0 }
' "$metadata_plan" ||
  label_die "metadata finalization plan has an invalid row shape"
LC_ALL=C sort "$expected_plan" > "$temp_dir/expected-plan.sorted"
LC_ALL=C sort "$metadata_plan" > "$temp_dir/supplied-plan.sorted"
cmp -s "$temp_dir/expected-plan.sorted" "$temp_dir/supplied-plan.sorted" ||
  label_die "metadata finalization plan is stale, incomplete, duplicated, or unexpected"

for index in "${!targets[@]}"; do
  target="${targets[$index]}"
  [ -f "$target" ] || label_die "missing corpus file: $target"
  stage="$temp_dir/stage/$index.tsv"
  invariant_before="$temp_dir/before-$index.tsv"
  invariant_after="$temp_dir/after-$index.tsv"

  # Reject malformed or mixed source schemas before applying any labels.
  awk -F'\t' '
    NR == 1 { schema=NF }
    (NF != 5 && NF != 6) || NF != schema { bad=1 }
    NF == 5 && ($1 == "" || $3 !~ /^(general|edgy|nsfw)$/ || $4 !~ /^[01]$/ || $5 == "") { bad=1 }
    NF == 6 && ($1 == "" || $4 !~ /^(general|edgy|nsfw)$/ || $5 !~ /^[01]$/ || $6 == "") { bad=1 }
    END { exit bad ? 1 : 0 }
  ' "$target" || label_die "malformed or mixed schema in $target"

  awk -F'\t' 'NF == 5 { print $5 } NF == 6 { print $6 }' "$target" \
    >> "$temp_dir/all-corpus-texts"

  if ! awk -F'\t' -v store="$LABEL_STORE" '
      BEGIN {
        while ((getline line < store) > 0) {
          count=split(line, label, "\t")
          if (count != 3 || (label[1] in seen)) { fatal=1; continue }
          seen[label[1]]=1; topic[label[1]]=label[2]; genre[label[1]]=label[3]
        }
        close(store)
      }
      {
        if (NF == 5) { source=$1; level=$3; prof=$4; text=$5 }
        else if (NF == 6) { source=$1; level=$4; prof=$5; text=$6 }
        else { fatal=1; next }
        if (!(text in seen)) {
          printf "%s: unmatched text at row %d\n", FILENAME, FNR > "/dev/stderr"
          fatal=1
          next
        }
        print source "\t" topic[text] "\t" genre[text] "\t" level "\t" prof "\t" text
      }
      END { exit fatal ? 1 : 0 }
    ' "$target" > "$stage"; then
    label_die "label application failed for $target; no targets were modified"
  fi

  label_validate_six_column_file "$stage"
  [ "$(label_line_count "$stage")" -eq "$(label_line_count "$target")" ] ||
    label_die "row count changed while staging $target"

  awk -F'\t' '
    NF == 5 { print $1 "\t" $3 "\t" $4 "\t" $5 }
    NF == 6 { print $1 "\t" $4 "\t" $5 "\t" $6 }
  ' "$target" > "$invariant_before"
  awk -F'\t' '{ print $1 "\t" $4 "\t" $5 "\t" $6 }' "$stage" > "$invariant_after"
  cmp -s "$invariant_before" "$invariant_after" ||
    label_die "source/level/profanity/text changed while staging $target"

  cp -- "$target" "$temp_dir/backup/$index.tsv"
  staged+=("$stage")
done

# The frozen labeling input must itself be the exact unique text set being promoted.
LC_ALL=C sort -u "$temp_dir/all-corpus-texts" > "$temp_dir/corpus.keys"
cut -f2 "$LABEL_INPUT" | LC_ALL=C sort > "$temp_dir/input.keys"
if ! cmp -s "$temp_dir/corpus.keys" "$temp_dir/input.keys"; then
  comm -23 "$temp_dir/input.keys" "$temp_dir/corpus.keys" | sed -n '1,5p' |
    sed 's/^/missing-from-corpus: /' >&2
  comm -13 "$temp_dir/input.keys" "$temp_dir/corpus.keys" | sed -n '1,5p' |
    sed 's/^/extra-in-corpus: /' >&2
  label_die "corpus text set changed since label-input.tsv was frozen"
fi

# Copy staged bytes beside each destination first. The subsequent rename is atomic per file.
for index in "${!targets[@]}"; do
  next="$(mktemp "${targets[$index]}.label-next.XXXXXX")"
  cp -- "${staged[$index]}" "$next"
  next_files+=("$next")
done

for index in "${!targets[@]}"; do
  promotion_inflight="$index"
  mv -f -- "${next_files[$index]}" "${targets[$index]}" ||
    label_die "promotion failed for ${targets[$index]}"
  promoted=$((index + 1))
  promotion_inflight=-1
done
next_files=()
committed=1

printf 'APPLIED: schema=v%s taxonomy=%s files=%s rows=%s unique_texts=%s store_sha256=%s\n' \
  "$LABEL_SCHEMA_VERSION" "$LABEL_TAXONOMY_VERSION" "${#targets[@]}" \
  "$(label_line_count "$temp_dir/all-corpus-texts")" \
  "$(label_line_count "$LABEL_INPUT")" "$(label_sha256 "$LABEL_STORE")"
printf 'FINALIZATION REQUIRED: refresh and review dependent hashes/notices: %s\n' \
  "${DEPENDENT_METADATA[*]}"
