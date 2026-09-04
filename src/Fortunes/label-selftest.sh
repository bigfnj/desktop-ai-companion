#!/usr/bin/env bash
# Disposable adversarial tests for the labeling pipeline. Never touches the live corpus/progress.
set -euo pipefail

SOURCE_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
fixture="$(mktemp -d "${TMPDIR:-/tmp}/desktopPet-label-selftest.XXXXXX")"
lock_holder_pid=""
cleanup_selftest() {
  local status=$?
  trap - EXIT HUP INT TERM
  set +e
  if [ -n "$lock_holder_pid" ]; then
    kill -TERM "$lock_holder_pid" 2>/dev/null
    wait "$lock_holder_pid" 2>/dev/null
  fi
  rm -rf -- "$fixture"
  exit "$status"
}
trap cleanup_selftest EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

fort="$fixture/project/src/Fortunes"
packs="$fixture/project/packs"
mkdir -p -- "$fort/label-chunks" "$packs" "$fixture/project/packaging"
for script in label-common.sh label-build-input.sh label-next.sh label-ingest.sh label-merge.sh label-apply.sh; do
  cp -- "$SOURCE_DIR/$script" "$fort/$script"
done

cat > "$fort/fortunes.txt" <<'EOF'
srcA	tech	general	0	Alpha fortune text.
srcA	wisdom	edgy	1	Beta fortune text.
EOF
cat > "$packs/test-pack.txt" <<'EOF'
srcB	facts	general	0	Gamma fortune text.
EOF
for dependency in \
  "$fixture/project/catalog.json" \
  "$packs/collections.json" \
  "$fixture/project/packaging/source-assets.json" \
  "$fixture/project/packaging/source-rights-evidence.json" \
  "$fixture/project/THIRD_PARTY_NOTICES.md" \
  "$fixture/project/PROVENANCE.md" \
  "$fixture/project/Readme.md" \
  "$fort/TAXONOMY.md"; do
  printf 'fixture dependency: %s\n' "${dependency##*/}" > "$dependency"
done
cat > "$fort/label-input.tsv" <<'EOF'
embedded	Alpha fortune text.
embedded	Beta fortune text.
test-pack	Gamma fortune text.
EOF
cat > "$fort/label-chunks/chunk001.tsv" <<'EOF'
1	Alpha fortune text.
2	Beta fortune text.
3	Gamma fortune text.
EOF
printf 'Alpha fortune text.\ttech\tquip\n' > "$fort/labels-store.tsv"
: > "$fort/.batchtexts"
: > "$fort/label-batch.txt"

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  else shasum -a 256 "$1" | awk '{print $1}'; fi
}

expect_failure() {
  if "$@" >/dev/null 2>&1; then
    printf 'expected failure but command succeeded: %s\n' "$*" >&2
    exit 1
  fi
}

expect_failure_matching() {
  local expected="$1" output
  shift
  if output="$("$@" 2>&1)"; then
    printf 'expected failure but command succeeded: %s\n' "$*" >&2
    exit 1
  fi
  printf '%s\n' "$output" | grep -Fq "$expected" || {
    printf 'failure did not contain %q: %s\n' "$expected" "$output" >&2
    exit 1
  }
}

expect_no_match() {
  local directory="$1" pattern="$2" found
  found="$(find "$directory" -maxdepth 1 -name "$pattern" -print -quit)"
  [ -z "$found" ] || {
    printf 'unexpected scratch artifact remains: %s\n' "$found" >&2
    exit 1
  }
}

signal_bin="$fixture/signal-bin"
signal_marker="$fixture/signal-marker"
real_mv="$(command -v mv)"
mkdir -p -- "$signal_bin"
cat > "$signal_bin/mv" <<EOF
#!/usr/bin/env bash
"$real_mv" "\$@"
status=\$?
destination=""
for argument in "\$@"; do destination="\$argument"; done
if [ "\$status" -eq 0 ] &&
   [ -n "\${LABEL_TEST_SIGNAL_TARGET:-}" ] &&
   [ "\$destination" = "\$LABEL_TEST_SIGNAL_TARGET" ] &&
   [ ! -e "\$LABEL_TEST_SIGNAL_MARKER" ]; then
  : > "\$LABEL_TEST_SIGNAL_MARKER"
  kill -TERM "\$PPID"
fi
exit "\$status"
EOF
chmod +x "$signal_bin/mv"

# One holder blocks every mutating labeling command through the shared cross-process lock.
lock_ready="$fixture/lock-ready"
lock_release="$fixture/lock-release"
(
  # shellcheck source=/dev/null
  . "$fort/label-common.sh"
  trap label_release_lock EXIT
  label_acquire_lock
  : > "$lock_ready"
  while [ ! -e "$lock_release" ]; do sleep 0.05; done
) &
lock_holder_pid=$!
for ((attempt=0; attempt<200; attempt++)); do
  [ -e "$lock_ready" ] && break
  kill -0 "$lock_holder_pid" 2>/dev/null ||
    { echo "lock holder exited before becoming ready" >&2; exit 1; }
  sleep 0.05
done
[ -e "$lock_ready" ] || { echo "lock holder did not become ready" >&2; exit 1; }
expect_failure_matching "label pipeline is already locked" bash "$fort/label-build-input.sh"
expect_failure_matching "label pipeline is already locked" bash "$fort/label-next.sh" 1
expect_failure_matching "label pipeline is already locked" bash "$fort/label-ingest.sh"
expect_failure_matching "label pipeline is already locked" bash "$fort/label-merge.sh"
expect_failure_matching "label pipeline is already locked" bash "$fort/label-apply.sh" --go
: > "$lock_release"
wait "$lock_holder_pid"
lock_holder_pid=""
[ ! -e "$fort/.label-pipeline.lock" ]

# Duplicate source selection is bytewise and independent of creation/traversal order or locale.
det_fort="$fixture/determinism/project/src/Fortunes"
det_packs="$fixture/determinism/project/packs"
mkdir -p -- "$det_fort" "$det_packs"
cp -- "$SOURCE_DIR/label-common.sh" "$SOURCE_DIR/label-build-input.sh" "$det_fort/"
cat > "$det_fort/fortunes.txt" <<'EOF'
embedded	general	general	0	Unique embedded fortune.
EOF
: > "$det_fort/labels-store.tsv"
printf 'pack	general	general	0\tShared duplicate fortune.\n' > "$det_packs/z-source.txt"
printf 'pack	general	general	0\tShared duplicate fortune.\n' > "$det_packs/a-source.txt"
(cd / && LC_ALL=C bash "$det_fort/label-build-input.sh") >/dev/null
cp -- "$det_fort/label-input.tsv" "$fixture/deterministic-input.tsv"
cp -- "$det_fort/label-input.meta" "$fixture/deterministic-input.meta"
deterministic_hash="$(sha256_file "$det_fort/label-input.tsv")"
grep -Fqx $'a-source\tShared duplicate fortune.' "$det_fort/label-input.tsv"

# Recreate the duplicate-bearing files in the opposite order and use a non-C locale when one
# exists. The generated input must remain byte-for-byte identical.
rm -f -- "$det_packs/a-source.txt" "$det_packs/z-source.txt"
printf 'pack	general	general	0\tShared duplicate fortune.\n' > "$det_packs/a-source.txt"
printf 'pack	general	general	0\tShared duplicate fortune.\n' > "$det_packs/z-source.txt"
alternate_locale=""
if command -v locale >/dev/null 2>&1; then
  alternate_locale="$(
    locale -a 2>/dev/null |
      LC_ALL=C awk '
        !found && $0 !~ /^(C|C[.]UTF-8|C[.]utf8|POSIX)$/ {
          candidate=$0
          found=1
        }
        END { if (found) print candidate }
      '
  )"
fi
alternate_locale="${alternate_locale:-C}"
(cd / && LC_ALL="$alternate_locale" bash "$det_fort/label-build-input.sh") >/dev/null
cmp -s "$fixture/deterministic-input.tsv" "$det_fort/label-input.tsv"
cmp -s "$fixture/deterministic-input.meta" "$det_fort/label-input.meta"
[ "$(sha256_file "$det_fort/label-input.tsv")" = "$deterministic_hash" ]

# The two Python in-place transforms stage output and preserve the exact prior bytes when a
# later row fails validation or a resource bound.
maintenance="$fixture/maintenance/Fortunes"
mkdir -p -- "$maintenance"
cp -- "$SOURCE_DIR/classify-corpus.py" "$SOURCE_DIR/strip-authors.py" "$maintenance/"
python_bin="${PYTHON:-python}"

classify_target="$maintenance/classify-target.tsv"
printf 'src\ttech\tA valid first fortune.\nbroken\trow\n' > "$classify_target"
before_transform="$(sha256_file "$classify_target")"
expect_failure "$python_bin" "$maintenance/classify-corpus.py" "$classify_target"
[ "$(sha256_file "$classify_target")" = "$before_transform" ]
expect_no_match "$maintenance" '.classify-target.tsv.*.tmp'
printf 'src\ttech\tA valid first fortune.\n' > "$classify_target"
"$python_bin" "$maintenance/classify-corpus.py" "$classify_target" >/dev/null
awk -F'\t' 'NF != 5 || $3 != "general" || $4 != "0" { exit 1 }' "$classify_target"

# Malformed UTF-8 is rejected atomically instead of round-tripping into the runtime corpus.
{
  printf 'src\ttech\tA valid first fortune.\n'
  printf 'src\ttech\tA malformed UTF-8 byte: \377.\n'
} > "$classify_target"
before_transform="$(sha256_file "$classify_target")"
expect_failure_matching "invalid UTF-8" \
  "$python_bin" "$maintenance/classify-corpus.py" "$classify_target"
[ "$(sha256_file "$classify_target")" = "$before_transform" ]
expect_no_match "$maintenance" '.classify-target.tsv.*.tmp'

# Reviewed severity-floor terms and phrase rules catch explicit/abusive content without
# classifying neutral biological, same-sex, music, architecture, or surname uses as explicit.
cat > "$classify_target" <<'EOF'
hackers	tech	I've read that male dolphins try to have sex with humans, and female apes solicit sex from humans.
fixture	general	A person had sex with an animal.
fixture	facts	Biologists record the sex of each dolphin.
fixture	facts	The exhibit discusses same-sex pairing in birds.
fixture	general	The cocksucker shouted from the doorway.
fixture	general	That loudmouth is a dickhead.
fixture	creative	Dickinson wrote many poems.
fixture	general	A cocktail umbrella decorated the drink.
Carlin	general	A clean mixed-case source fixture.
YO-MAMA	general	Another clean mixed-case source fixture.
fixture	general	The report says an adult raped the victim.
fixture	general	The witness described sexual assault.
fixture	general	The joke mentioned an erection and a boner.
fixture	general	The story discusses incestuous abuse.
fixture	general	The offender was described as a paedophile.
fixture	general	The offender was described as a pedophile.
fixture	general	The horror story mentioned a necrophiliac.
fixture	general	The court record described molestation.
fixture	general	The scene became an orgy.
fixture	creative	The rapper performed a new song.
fixture	facts	Workers erect a temporary shelter.
fixture	facts	The committee assessed the project.
EOF
"$python_bin" "$maintenance/classify-corpus.py" "$classify_target" >/dev/null
awk -F'\t' '
  NR == 1 && ($3 != "nsfw" || $4 != "1") { exit 1 }
  NR == 2 && ($3 != "nsfw" || $4 != "1") { exit 1 }
  NR == 3 && ($3 != "general" || $4 != "0") { exit 1 }
  NR == 4 && ($3 != "general" || $4 != "0") { exit 1 }
  NR == 5 && ($3 != "nsfw" || $4 != "1") { exit 1 }
  NR == 6 && ($3 != "edgy" || $4 != "1") { exit 1 }
  NR == 7 && ($3 != "general" || $4 != "0") { exit 1 }
  NR == 8 && ($3 != "general" || $4 != "0") { exit 1 }
  NR >= 9 && NR <= 10 && ($3 != "edgy" || $4 != "0") { exit 1 }
  NR >= 11 && NR <= 19 && ($3 != "nsfw" || $4 != "1") { exit 1 }
  NR >= 20 && NR <= 22 && ($3 != "general" || $4 != "0") { exit 1 }
  END { if (NR != 22) exit 1 }
' "$classify_target"

# Both classifier engines consume this exact UTF-8 fixture. It covers compatibility
# decomposition, combining marks, dotted/dotless I, long s, Kelvin sign, and ASCII
# word-boundary controls so their severity floors cannot silently drift.
classifier_parity_fixture="$SOURCE_DIR/classifier-parity-cases.tsv"
awk -F'\t' '
  BEGIN { OFS = "\t" }
  NR == 1 {
    if ($0 != "#!desktop-pet-classifier-parity-v1") exit 1
    next
  }
  NF != 5 || $1 == "" || $2 == "" ||
      ($3 != "general" && $3 != "edgy" && $3 != "nsfw") ||
      ($4 != "0" && $4 != "1") || $5 == "" { exit 1 }
  { print $2, "general", $5 }
  END { if (NR < 2) exit 1 }
' "$classifier_parity_fixture" > "$classify_target"
"$python_bin" "$maintenance/classify-corpus.py" "$classify_target" >/dev/null
awk -F'\t' '
  NR == FNR {
    if (FNR == 1) next
    count++
    source[count] = $2
    level[count] = $3
    prof[count] = $4
    text[count] = $5
    next
  }
  {
    seen++
    if (NF != 5 || $1 != source[seen] || $3 != level[seen] ||
        $4 != prof[seen] || $5 != text[seen]) {
      printf "classifier parity fixture failed at row %d\n", seen > "/dev/stderr"
      exit 1
    }
  }
  END { if (seen != count) exit 1 }
' "$classifier_parity_fixture" "$classify_target"

strip_target="$maintenance/strip-target.tsv"
{
  printf 'src\tcreative\tA thoughtful sentence. -- Jane Doe\n'
  printf 'src\tcreative\t'
  awk 'BEGIN { for (i=0; i<70000; i++) printf "x"; printf "\n" }'
} > "$strip_target"
before_transform="$(sha256_file "$strip_target")"
expect_failure "$python_bin" "$maintenance/strip-authors.py" "$strip_target"
[ "$(sha256_file "$strip_target")" = "$before_transform" ]
expect_no_match "$maintenance" '.strip-target.tsv.*.tmp'
{
  printf 'src\tcreative\tA valid first fortune. -- Jane Doe\n'
  printf 'src\tcreative\tA malformed UTF-8 byte: \377.\n'
} > "$strip_target"
before_transform="$(sha256_file "$strip_target")"
expect_failure_matching "invalid UTF-8" \
  "$python_bin" "$maintenance/strip-authors.py" "$strip_target"
[ "$(sha256_file "$strip_target")" = "$before_transform" ]
expect_no_match "$maintenance" '.strip-target.tsv.*.tmp'
printf 'src\tcreative\tA thoughtful sentence. -- Jane Doe\n' > "$strip_target"
"$python_bin" "$maintenance/strip-authors.py" "$strip_target" >/dev/null
grep -Fqx $'src\tcreative\tA thoughtful sentence.' "$strip_target"
printf 'src\tcreative\tDefault corpus sentence. -- Jane Doe\n' \
  > "$maintenance/fortunes.txt"
(cd "$maintenance" && "$python_bin" ./strip-authors.py) >/dev/null
grep -Fqx $'src\tcreative\tDefault corpus sentence.' "$maintenance/fortunes.txt"
[ ! -e "$maintenance/fortunes-sfw.txt" ]
[ ! -e "$maintenance/fortunes-spicy.txt" ]

# The corpus builder requires an exact, clean Git root before it reads curated inputs. Every
# provenance failure preserves prior output and removes any same-directory staging tree.
build_fort="$fixture/build/Fortunes"
build_source="$fixture/build/upstream"
mkdir -p -- "$build_fort" "$build_source"
cp -- "$SOURCE_DIR/build-corpus.sh" "$SOURCE_DIR/classify-corpus.py" \
  "$SOURCE_DIR/strip-authors.py" "$build_fort/"
printf 'classic_philosophy\n' > "$build_fort/corpus-required-files.txt"
printf 'prior\tgeneral\tgeneral\t0\tPreserve this prior output.\n' \
  > "$build_fort/fortunes.txt"
printf 'prior reconstruction evidence\n' > "$build_fort/fortunes.sources.tsv"
cat > "$build_source/classic_philosophy" <<'EOF'
A durable scratch fortune. -- Jane Doe
%
A durable scratch fortune. -- John Roe
%
A second scratch fortune.
EOF
cp -- "$build_source/classic_philosophy" "$fixture/clean-classic-philosophy"
before_build="$(sha256_file "$build_fort/fortunes.txt")"
before_evidence="$(sha256_file "$build_fort/fortunes.sources.tsv")"
test_source_commit="0000000000000000000000000000000000000000"
expect_failure_matching "source directory is not a Git repository root" \
  env PYTHON="$python_bin" FORTUNE_SOURCE_COMMIT="$test_source_commit" \
  bash "$build_fort/build-corpus.sh" "$build_source"
[ "$(sha256_file "$build_fort/fortunes.txt")" = "$before_build" ]
[ "$(sha256_file "$build_fort/fortunes.sources.tsv")" = "$before_evidence" ]
expect_no_match "$build_fort" '.build-corpus.*'

git -C "$build_source" init -q
git -C "$build_source" config core.autocrlf false
git -C "$build_source" add classic_philosophy
git -C "$build_source" -c user.name='DesktopPet Self-Test' \
  -c user.email='desktop-pet-selftest@example.invalid' commit -q -m fixture
actual_source_commit="$(git -C "$build_source" rev-parse HEAD)"
expect_failure_matching "source checkout HEAD is $actual_source_commit, expected $test_source_commit" \
  env PYTHON="$python_bin" FORTUNE_SOURCE_COMMIT="$test_source_commit" \
  bash "$build_fort/build-corpus.sh" "$build_source"
[ "$(sha256_file "$build_fort/fortunes.txt")" = "$before_build" ]
[ "$(sha256_file "$build_fort/fortunes.sources.tsv")" = "$before_evidence" ]

git -C "$build_source" remote add origin \
  'https://github.com/JKirchartz/fortunes.git'

# The exact fixture manifest makes every listed curated input mandatory.
printf 'classic_philosophy\nmissing-required-source\n' \
  > "$build_fort/corpus-required-files.txt"
expect_failure_matching "required curated source is missing: missing-required-source" \
  env PYTHON="$python_bin" FORTUNE_SOURCE_COMMIT="$actual_source_commit" \
  bash "$build_fort/build-corpus.sh" "$build_source"
[ "$(sha256_file "$build_fort/fortunes.txt")" = "$before_build" ]
[ "$(sha256_file "$build_fort/fortunes.sources.tsv")" = "$before_evidence" ]
printf 'classic_philosophy\n' > "$build_fort/corpus-required-files.txt"

git -C "$build_source" remote set-url origin \
  'https://example.invalid/not-the-reviewed-repository.git'
expect_failure_matching "remote.origin.url does not identify" \
  env PYTHON="$python_bin" FORTUNE_SOURCE_COMMIT="$actual_source_commit" \
  bash "$build_fort/build-corpus.sh" "$build_source"
git -C "$build_source" remote set-url origin \
  'https://github.com/JKirchartz/fortunes.git'
expect_failure_matching "FORTUNE_SOURCE_REPOSITORY must identify" \
  env PYTHON="$python_bin" FORTUNE_SOURCE_COMMIT="$actual_source_commit" \
  FORTUNE_SOURCE_REPOSITORY='https://example.invalid/attacker.git' \
  bash "$build_fort/build-corpus.sh" "$build_source"

# Curated filesystem bytes must exist as blobs at the pinned commit. This catches both ordinary
# untracked inputs and ignored inputs that `git status --porcelain` intentionally omits.
printf 'An untracked curated fortune.\n' > "$build_source/hacker-questions"
printf 'classic_philosophy\nhacker-questions\n' \
  > "$build_fort/corpus-required-files.txt"
expect_failure_matching "curated source is not tracked at source commit: hacker-questions" \
  env PYTHON="$python_bin" FORTUNE_SOURCE_COMMIT="$actual_source_commit" \
  bash "$build_fort/build-corpus.sh" "$build_source"
[ "$(sha256_file "$build_fort/fortunes.txt")" = "$before_build" ]
[ "$(sha256_file "$build_fort/fortunes.sources.tsv")" = "$before_evidence" ]
rm -f -- "$build_source/hacker-questions"
printf 'classic_philosophy\n' > "$build_fort/corpus-required-files.txt"

printf '/rfc1925\n' >> "$build_source/.git/info/exclude"
printf 'An ignored curated fortune.\n' > "$build_source/rfc1925"
printf 'classic_philosophy\nrfc1925\n' \
  > "$build_fort/corpus-required-files.txt"
[ -z "$(git -C "$build_source" status --porcelain -- rfc1925)" ]
expect_failure_matching "curated source is not tracked at source commit: rfc1925" \
  env PYTHON="$python_bin" FORTUNE_SOURCE_COMMIT="$actual_source_commit" \
  bash "$build_fort/build-corpus.sh" "$build_source"
[ "$(sha256_file "$build_fort/fortunes.txt")" = "$before_build" ]
[ "$(sha256_file "$build_fort/fortunes.sources.tsv")" = "$before_evidence" ]
rm -f -- "$build_source/rfc1925"
printf 'classic_philosophy\n' > "$build_fort/corpus-required-files.txt"

# Raw blob comparison also catches changes hidden from status by assume-unchanged.
git -C "$build_source" update-index --assume-unchanged classic_philosophy
printf '\nA dirty curated fortune.\n' >> "$build_source/classic_philosophy"
[ -z "$(git -C "$build_source" status --porcelain -- classic_philosophy)" ]
expect_failure_matching "curated source does not match pinned Git blob: classic_philosophy" \
  env PYTHON="$python_bin" FORTUNE_SOURCE_COMMIT="$actual_source_commit" \
  bash "$build_fort/build-corpus.sh" "$build_source"
[ "$(sha256_file "$build_fort/fortunes.txt")" = "$before_build" ]
[ "$(sha256_file "$build_fort/fortunes.sources.tsv")" = "$before_evidence" ]
git -C "$build_source" update-index --no-assume-unchanged classic_philosophy
cp -- "$fixture/clean-classic-philosophy" "$build_source/classic_philosophy"
[ -z "$(git -C "$build_source" status --porcelain -- classic_philosophy)" ]

(cd / && PYTHON="$python_bin" FORTUNE_SOURCE_COMMIT="$actual_source_commit" \
  bash "$build_fort/build-corpus.sh" "$build_source") >/dev/null
awk -F'\t' 'NF != 5 { exit 1 } END { if (NR != 2) exit 1 }' \
  "$build_fort/fortunes.txt"
[ "$(cut -f5 "$build_fort/fortunes.txt" | LC_ALL=C sort -u | awk 'END { print NR }')" -eq 2 ]
grep -Fqx $'schema\t1' "$build_fort/fortunes.sources.tsv"
grep -Fqx "source_commit	$actual_source_commit" "$build_fort/fortunes.sources.tsv"
grep -Fqx $'curated_blobs_match\t1' "$build_fort/fortunes.sources.tsv"
fixture_manifest_hash="$(sha256_file "$build_fort/corpus-required-files.txt")"
grep -Fqx "source_manifest_sha256	$fixture_manifest_hash" \
  "$build_fort/fortunes.sources.tsv"
source_hash="$(sha256_file "$build_source/classic_philosophy")"
awk -F'\t' -v expected="$source_hash" '
  $1 == "source_file" && $2 == "classic_philosophy" && $4 == expected { found=1 }
  END { exit found ? 0 : 1 }
' "$build_fort/fortunes.sources.tsv"
output_hash="$(sha256_file "$build_fort/fortunes.txt")"
awk -F'\t' -v expected="$output_hash" '
  $1 == "output_file" && $2 == "fortunes.txt" && $4 == 2 && $5 == expected { found=1 }
  END { exit found ? 0 : 1 }
' "$build_fort/fortunes.sources.tsv"

# Invalid UTF-8 in a pinned source fails before promotion and preserves the last valid build.
before_build="$(sha256_file "$build_fort/fortunes.txt")"
before_evidence="$(sha256_file "$build_fort/fortunes.sources.tsv")"
{
  printf 'A valid upstream fortune.\n%%\n'
  printf 'A malformed UTF-8 fortune: \377.\n%%\n'
} > "$build_source/classic_philosophy"
git -C "$build_source" add classic_philosophy
git -C "$build_source" -c user.name='DesktopPet Self-Test' \
  -c user.email='desktop-pet-selftest@example.invalid' commit -q -m invalid-utf8
invalid_utf8_source_commit="$(git -C "$build_source" rev-parse HEAD)"
expect_failure_matching "invalid UTF-8" \
  env PYTHON="$python_bin" FORTUNE_SOURCE_COMMIT="$invalid_utf8_source_commit" \
  bash "$build_fort/build-corpus.sh" "$build_source"
[ "$(sha256_file "$build_fort/fortunes.txt")" = "$before_build" ]
[ "$(sha256_file "$build_fort/fortunes.sources.tsv")" = "$before_evidence" ]
expect_no_match "$build_fort" '.build-corpus.*'

# A committed over-limit input reaches the resource guard but cannot replace the valid build.
{
  printf 'A valid upstream fortune.\n%%\n'
  awk 'BEGIN { for (i=0; i<17000; i++) printf "x"; printf "\n" }'
  printf '%%\n'
} > "$build_source/classic_philosophy"
git -C "$build_source" add classic_philosophy
git -C "$build_source" -c user.name='DesktopPet Self-Test' \
  -c user.email='desktop-pet-selftest@example.invalid' commit -q -m over-limit
limit_source_commit="$(git -C "$build_source" rev-parse HEAD)"
expect_failure_matching "physical line exceeds the byte limit" \
  env PYTHON="$python_bin" FORTUNE_SOURCE_COMMIT="$limit_source_commit" \
  bash "$build_fort/build-corpus.sh" "$build_source"
[ "$(sha256_file "$build_fort/fortunes.txt")" = "$before_build" ]
[ "$(sha256_file "$build_fort/fortunes.sources.tsv")" = "$before_evidence" ]
expect_no_match "$build_fort" '.build-corpus.*'

# TERM after the input rename restores prior input, absent metadata, and store bytes.
input_before="$(sha256_file "$fort/label-input.tsv")"
store_before="$(sha256_file "$fort/labels-store.tsv")"
[ ! -e "$fort/label-input.meta" ]
rm -f -- "$signal_marker"
expect_failure env PATH="$signal_bin:$PATH" \
  LABEL_TEST_SIGNAL_TARGET="$fort/label-input.tsv" \
  LABEL_TEST_SIGNAL_MARKER="$signal_marker" \
  bash "$fort/label-build-input.sh"
[ -e "$signal_marker" ]
[ "$(sha256_file "$fort/label-input.tsv")" = "$input_before" ]
[ "$(sha256_file "$fort/labels-store.tsv")" = "$store_before" ]
[ ! -e "$fort/label-input.meta" ]
[ ! -e "$fort/.label-pipeline.lock" ]
expect_no_match "$fort" '*.label-restore.*'

# Input generation is deterministic and records the frozen schema/taxonomy/hash.
(cd / && bash "$fort/label-build-input.sh") >/dev/null
input_hash="$(sha256_file "$fort/label-input.tsv")"
(cd / && bash "$fort/label-build-input.sh") >/dev/null
[ "$(sha256_file "$fort/label-input.tsv")" = "$input_hash" ]
grep -q '^schema=2$' "$fort/label-input.meta"
grep -q '^taxonomy=2026-07-31$' "$fort/label-input.meta"

# A missing chunk output must fail without truncating the prior store.
before_store="$(sha256_file "$fort/labels-store.tsv")"
expect_failure bash "$fort/label-merge.sh"
[ "$(sha256_file "$fort/labels-store.tsv")" = "$before_store" ] ||
  { echo "merge failure changed prior store" >&2; exit 1; }

# An unexpected output with no corresponding input chunk is also fatal and atomic.
printf '1 tech quip\n' > "$fort/label-chunks/chunk999.out"
expect_failure bash "$fort/label-merge.sh"
[ "$(sha256_file "$fort/labels-store.tsv")" = "$before_store" ] ||
  { echo "extra-output failure changed prior store" >&2; exit 1; }
rm -f -- "$fort/label-chunks/chunk999.out"

# A complete, ordered, locked chunk set replaces the store.
cat > "$fort/label-chunks/chunk001.out" <<'EOF'
1 tech quip
2 life dark
3 science fact
EOF

# TERM after the store rename restores both the prior store and metadata, then releases the lock.
merge_store_before="$(sha256_file "$fort/labels-store.tsv")"
merge_meta_existed=0
merge_meta_before=""
if [ -f "$fort/labels-store.meta" ]; then
  merge_meta_existed=1
  merge_meta_before="$(sha256_file "$fort/labels-store.meta")"
fi
rm -f -- "$signal_marker"
expect_failure env PATH="$signal_bin:$PATH" \
  LABEL_TEST_SIGNAL_TARGET="$fort/labels-store.tsv" \
  LABEL_TEST_SIGNAL_MARKER="$signal_marker" \
  bash "$fort/label-merge.sh"
[ -e "$signal_marker" ]
[ "$(sha256_file "$fort/labels-store.tsv")" = "$merge_store_before" ]
if [ "$merge_meta_existed" -eq 1 ]; then
  [ "$(sha256_file "$fort/labels-store.meta")" = "$merge_meta_before" ]
else
  [ ! -e "$fort/labels-store.meta" ]
fi
[ ! -e "$fort/.label-pipeline.lock" ]
expect_no_match "$fort" '*.label-restore.*'

(cd / && bash "$fort/label-merge.sh") >/dev/null
[ "$(awk 'END {print NR}' "$fort/labels-store.tsv")" -eq 3 ]

# Default apply refuses before staging because schema conversion would invalidate dependent pins.
expect_failure_matching "generate and review --emit-plan" \
  bash "$fort/label-apply.sh" --go
apply_plan="$fixture/label-apply-plan.tsv"
(cd / && bash "$fort/label-apply.sh" --emit-plan) > "$apply_plan"
grep -Fqx $'expected_output_schema\t2' "$apply_plan"
grep -Fqx $'acknowledge_metadata_finalization\ttrue' "$apply_plan"
grep -Fq $'dependency\tcatalog.json\t' "$apply_plan"
cp -- "$apply_plan" "$fixture/stale-label-apply-plan.tsv"
sed 's/^label_store_sha256\t[0-9a-f]*$/label_store_sha256\t0000000000000000000000000000000000000000000000000000000000000000/' \
  "$fixture/stale-label-apply-plan.tsv" > "$fixture/stale-plan-next.tsv"
mv -f -- "$fixture/stale-plan-next.tsv" "$fixture/stale-label-apply-plan.tsv"
expect_failure_matching "plan is stale, incomplete, duplicated, or unexpected" \
  bash "$fort/label-apply.sh" --go \
    --metadata-plan "$fixture/stale-label-apply-plan.tsv" \
    --acknowledge-metadata-finalization

# Applying a complete store promotes exact six-column data and preserves non-label fields.
before_invariants="$fixture/before-invariants"
{
  awk -F'\t' '{print $1 "\t" $3 "\t" $4 "\t" $5}' "$fort/fortunes.txt"
  awk -F'\t' '{print $1 "\t" $3 "\t" $4 "\t" $5}' "$packs/test-pack.txt"
} > "$before_invariants"

# TERM after the first corpus rename restores every target and releases the shared lock.
apply_embedded_before="$(sha256_file "$fort/fortunes.txt")"
apply_pack_before="$(sha256_file "$packs/test-pack.txt")"
rm -f -- "$signal_marker"
expect_failure env PATH="$signal_bin:$PATH" \
  LABEL_TEST_SIGNAL_TARGET="$fort/fortunes.txt" \
  LABEL_TEST_SIGNAL_MARKER="$signal_marker" \
  bash "$fort/label-apply.sh" --go \
    --metadata-plan "$apply_plan" \
    --acknowledge-metadata-finalization
[ -e "$signal_marker" ]
[ "$(sha256_file "$fort/fortunes.txt")" = "$apply_embedded_before" ]
[ "$(sha256_file "$packs/test-pack.txt")" = "$apply_pack_before" ]
[ ! -e "$fort/.label-pipeline.lock" ]
expect_no_match "$fort" '*.label-restore.*'

(cd / && bash "$fort/label-apply.sh" --go \
  --metadata-plan "$apply_plan" \
  --acknowledge-metadata-finalization) >/dev/null
awk -F'\t' 'NF != 6 {exit 1}' "$fort/fortunes.txt" "$packs/test-pack.txt"
{
  awk -F'\t' '{print $1 "\t" $4 "\t" $5 "\t" $6}' "$fort/fortunes.txt"
  awk -F'\t' '{print $1 "\t" $4 "\t" $5 "\t" $6}' "$packs/test-pack.txt"
} > "$fixture/after-invariants"
cmp -s "$before_invariants" "$fixture/after-invariants"

# An incomplete store is fatal and leaves every promoted target byte-for-byte unchanged.
head -n 2 "$fort/labels-store.tsv" > "$fixture/incomplete-store"
mv -f -- "$fixture/incomplete-store" "$fort/labels-store.tsv"
embedded_hash="$(sha256_file "$fort/fortunes.txt")"
pack_hash="$(sha256_file "$packs/test-pack.txt")"
expect_failure bash "$fort/label-apply.sh" --go \
  --metadata-plan "$apply_plan" \
  --acknowledge-metadata-finalization
[ "$(sha256_file "$fort/fortunes.txt")" = "$embedded_hash" ]
[ "$(sha256_file "$packs/test-pack.txt")" = "$pack_hash" ]

# TERM after batch-text promotion restores the prior snapshot and releases the lock.
: > "$fort/labels-store.tsv"
next_texts_before="$(sha256_file "$fort/.batchtexts")"
rm -f -- "$signal_marker"
expect_failure env PATH="$signal_bin:$PATH" \
  LABEL_TEST_SIGNAL_TARGET="$fort/.batchtexts" \
  LABEL_TEST_SIGNAL_MARKER="$signal_marker" \
  bash "$fort/label-next.sh" 2
[ -e "$signal_marker" ]
[ "$(sha256_file "$fort/.batchtexts")" = "$next_texts_before" ]
[ ! -e "$fort/.label-pipeline.lock" ]
expect_no_match "$fort" '*.label-restore.*'

# Empty-store batching exercises the FILENAME-based fix; invalid ingest remains atomic.
(cd / && bash "$fort/label-next.sh" 2) >/dev/null
[ "$(awk 'END {print NR}' "$fort/.batchtexts")" -eq 2 ]
printf 'not-a-topic quip\nlife dark\n' > "$fort/label-batch.txt"
before_store="$(sha256_file "$fort/labels-store.tsv")"
expect_failure bash "$fort/label-ingest.sh"
[ "$(sha256_file "$fort/labels-store.tsv")" = "$before_store" ]

# Tabs are accepted as label separators and are normalized before paste. TERM after store
# promotion restores the store, metadata, batch, and batch-text snapshot byte-for-byte.
printf 'tech\tquip\nlife\tdark\n' > "$fort/label-batch.txt"
ingest_store_before="$(sha256_file "$fort/labels-store.tsv")"
ingest_meta_before="$(sha256_file "$fort/labels-store.meta")"
ingest_batch_before="$(sha256_file "$fort/label-batch.txt")"
ingest_texts_before="$(sha256_file "$fort/.batchtexts")"
rm -f -- "$signal_marker"
expect_failure env PATH="$signal_bin:$PATH" \
  LABEL_TEST_SIGNAL_TARGET="$fort/labels-store.tsv" \
  LABEL_TEST_SIGNAL_MARKER="$signal_marker" \
  bash "$fort/label-ingest.sh"
[ -e "$signal_marker" ]
[ "$(sha256_file "$fort/labels-store.tsv")" = "$ingest_store_before" ]
[ "$(sha256_file "$fort/labels-store.meta")" = "$ingest_meta_before" ]
[ "$(sha256_file "$fort/label-batch.txt")" = "$ingest_batch_before" ]
[ "$(sha256_file "$fort/.batchtexts")" = "$ingest_texts_before" ]
[ ! -e "$fort/.label-pipeline.lock" ]
expect_no_match "$fort" '*.label-restore.*'

(cd / && bash "$fort/label-ingest.sh") >/dev/null
[ "$(awk 'END {print NR}' "$fort/labels-store.tsv")" -eq 2 ]
[ ! -s "$fort/label-batch.txt" ]
[ ! -s "$fort/.batchtexts" ]

echo "label pipeline self-test: PASS"
