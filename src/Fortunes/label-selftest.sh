#!/usr/bin/env bash
# Disposable adversarial tests for the labeling pipeline. Never touches the live corpus/progress.
set -euo pipefail

SOURCE_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
fixture="$(mktemp -d "${TMPDIR:-/tmp}/desktopPet-label-selftest.XXXXXX")"
lock_holder_pid=""
signal_pid=""
signal_release=""
cleanup_selftest() {
  local status=$?
  trap - EXIT HUP INT TERM
  set +e
  if [ -n "$signal_pid" ]; then
    [ -n "$signal_release" ] && : > "$signal_release"
    kill -TERM "$signal_pid" 2>/dev/null
    wait "$signal_pid" 2>/dev/null
  fi
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

# A PATH `mv` that, once, after a successful rename onto LABEL_TEST_SIGNAL_TARGET, writes a marker and
# blocks until released. The labeling script is then provably inside its transaction, waiting on this
# child, which is when expect_term_rollback signals it. The wrapper's wait is bounded so a self-test
# that dies mid-stage cannot leave it spinning.
signal_bin="$fixture/signal-bin"
signal_marker="$fixture/signal-marker"
signal_release="$fixture/signal-release"
signal_stderr="$fixture/signal-stderr"
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
  for ((attempt=0; attempt<400; attempt++)); do
    [ -e "\$LABEL_TEST_SIGNAL_RELEASE" ] && break
    sleep 0.05
  done
fi
exit "\$status"
EOF
chmod +x "$signal_bin/mv"

# Interrupt a labeling command deterministically and assert its documented rollback contract.
# The command runs in the background with the blocking mv on PATH. The marker is the proof it is
# inside its work: its first rename onto $target has landed and it is blocked in that mv. TERM then
# goes to the command's own PID, the wrapper is released, and the outcome the scripts document is
# asserted: the signal is held until the child returns (nothing rolled back before the release), exit
# 143 (their `trap 'exit 143' TERM`), and the ROLLBACK line their cleanup prints before restoring. Each
# stage's byte-for-byte checks follow the call. The kill precedes the release, and the release reaches
# the wrapper only through its 50 ms poll, so the signal is pending in the script before its child can
# return. (The retired wrapper sent TERM to its own $PPID from inside the rename and raced the script's
# completion: it passed or failed with the machine's load, N-scripts-pack-02.)
expect_term_rollback() {
  local target="$1" message="$2" status=0 attempt
  shift 2
  rm -f -- "$signal_marker" "$signal_release" "$signal_stderr"
  PATH="$signal_bin:$PATH" \
    LABEL_TEST_SIGNAL_TARGET="$target" \
    LABEL_TEST_SIGNAL_MARKER="$signal_marker" \
    LABEL_TEST_SIGNAL_RELEASE="$signal_release" \
    "$@" >/dev/null 2>"$signal_stderr" &
  signal_pid=$!
  for ((attempt=0; attempt<400; attempt++)); do
    [ -e "$signal_marker" ] && break
    kill -0 "$signal_pid" 2>/dev/null || break
    sleep 0.05
  done
  if [ ! -e "$signal_marker" ]; then
    : > "$signal_release"
    wait "$signal_pid" 2>/dev/null || true
    signal_pid=""
    printf 'TERM stage: %s never renamed onto %s: %s\n' "$*" "$target" "$(cat "$signal_stderr")" >&2
    exit 1
  fi
  kill -TERM "$signal_pid"
  # The handler must HOLD the signal until the rename in flight returns. Without `trap 'exit 143' TERM`
  # bash dies at once and runs the EXIT trap under its still-running child; that cleanup rolls back and
  # prints ROLLBACK before this release, and the signal death still reports 143, so status, message and
  # restored bytes could not tell the handler's absence apart (measured 2026-10-01: five handler
  # mutants survived a stage that asserted only those). A ROLLBACK line before the release is the
  # script unwinding mid-command; a held signal prints nothing until the child returns.
  for ((attempt=0; attempt<20; attempt++)); do
    if grep -Fq -- 'ROLLBACK:' "$signal_stderr"; then
      : > "$signal_release"
      wait "$signal_pid" 2>/dev/null || true
      signal_pid=""
      printf 'TERM stage: %s unwound while its rename was still in flight: %s\n' "$*" "$(cat "$signal_stderr")" >&2
      exit 1
    fi
    sleep 0.05
  done
  : > "$signal_release"
  wait "$signal_pid" || status=$?
  signal_pid=""
  if [ "$status" -ne 143 ]; then
    printf 'TERM stage: %s exited %s instead of 143: %s\n' "$*" "$status" "$(cat "$signal_stderr")" >&2
    exit 1
  fi
  grep -Fq -- "$message" "$signal_stderr" || {
    printf 'TERM stage: %s did not report %s: %s\n' "$*" "$message" "$(cat "$signal_stderr")" >&2
    exit 1
  }
}

# A rollback check that names the file it found changed, instead of a bare test that exits 1 in silence.
expect_same_hash() {
  local path="$1" expected="$2" actual
  actual="$(sha256_file "$path")"
  [ "$actual" = "$expected" ] || {
    printf 'rollback left %s changed (sha256 %s, expected %s)\n' "$path" "$actual" "$expected" >&2
    exit 1
  }
}

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

# Each transform carries its own behavioural check (F321: one layout per file; F323: the summary
# figures do not overlap). Run them before the fixtures below lean on the transforms.
"$python_bin" "$maintenance/classify-corpus.py" --selfcheck >/dev/null
"$python_bin" "$maintenance/strip-authors.py" --selfcheck >/dev/null

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

# The corpus builder assembles fortunes.txt from the labeled inputs that live in the repository
# (sources/<id>.tsv beside it, or <repo root>/packs/<id>.txt); there is no upstream checkout any more.
# This block used to hold the RETIRED builder to its provenance contract (a clean Git root at a pinned
# commit, tracked curated blobs, a reviewed remote), so it failed at its first expectation on the
# current builder while the gate never ran this script (N-records-01). Every refusal below must
# preserve the prior output and leave no same-directory staging tree behind, which is the property
# the old block also held the builder to.
build_root="$fixture/build"
build_fort="$build_root/src/Fortunes"
mkdir -p -- "$build_fort/sources" "$build_root/packs"
cp -- "$SOURCE_DIR/build-corpus.sh" "$build_fort/"
printf 'prior\tgeneral\tgeneral\t0\t0\tPreserve this prior output.\n' > "$build_fort/fortunes.txt"
before_build="$(sha256_file "$build_fort/fortunes.txt")"
build_preserved() {
  [ "$(sha256_file "$build_fort/fortunes.txt")" = "$before_build" ]
  expect_no_match "$build_fort" '.build-corpus.*'
}

# A manifest id with no input at either path.
printf 'alpha\n' > "$build_fort/corpus-required-files.txt"
expect_failure_matching "no input for source 'alpha'" bash "$build_fort/build-corpus.sh"
build_preserved

# An id the manifest grammar refuses, and a duplicate.
printf 'alpha\nbad entry!\n' > "$build_fort/corpus-required-files.txt"
expect_failure_matching "invalid manifest entry" bash "$build_fort/build-corpus.sh"
build_preserved
printf 'alpha\nalpha\n' > "$build_fort/corpus-required-files.txt"
expect_failure_matching "duplicate manifest entry: alpha" bash "$build_fort/build-corpus.sh"
build_preserved

# Inputs at both resolution paths: a primary source beside the builder and a downloadable pack.
printf 'alpha\nbeta\n' > "$build_fort/corpus-required-files.txt"
printf 'alpha\ttech\tquip\t0\t0\tA durable scratch fortune.\nalpha\tlife\tdark\t1\t0\tA second scratch fortune.\n' \
  > "$build_fort/sources/alpha.tsv"
printf 'beta\tfacts\tgeneral\t0\t0\tA durable scratch fortune.\nbeta\tfacts\tgeneral\t0\t0\tA pack-only fortune line.\n' \
  > "$build_root/packs/beta.txt"
cp -- "$build_fort/sources/alpha.tsv" "$fixture/clean-alpha.tsv"

# Row validation refuses before anything is combined: a short row, a source column that disagrees
# with its file, and a text outside the 8..280 bound.
printf 'alpha\ttech\tquip\t0\tFive fields only.\n' > "$build_fort/sources/alpha.tsv"
expect_failure_matching "expected 6 tab-separated fields" bash "$build_fort/build-corpus.sh"
build_preserved
printf 'gamma\ttech\tquip\t0\t0\tA row filed under the wrong source.\n' > "$build_fort/sources/alpha.tsv"
expect_failure_matching 'source column is "gamma"' bash "$build_fort/build-corpus.sh"
build_preserved
printf 'alpha\ttech\tquip\t0\t0\tshort\n' > "$build_fort/sources/alpha.tsv"
expect_failure_matching "text length 5 outside 8..280" bash "$build_fort/build-corpus.sh"
build_preserved
cp -- "$fixture/clean-alpha.tsv" "$build_fort/sources/alpha.tsv"

# A clean build: four input rows, one text shared by alpha and beta, so three rows come out, the
# duplicate resolved in manifest order (alpha keeps it), sorted C-locale by source then text.
(cd / && bash "$build_fort/build-corpus.sh") >/dev/null
awk -F'\t' 'NF != 6 { exit 1 } END { if (NR != 3) exit 1 }' "$build_fort/fortunes.txt"
[ "$(cut -f1 "$build_fort/fortunes.txt" | LC_ALL=C sort | uniq -c | awk '{print $1":"$2}' | paste -sd, -)" = "2:alpha,1:beta" ]
grep -Fqx $'alpha\ttech\tquip\t0\t0\tA durable scratch fortune.' "$build_fort/fortunes.txt"
! grep -Fq $'beta\tfacts\tgeneral\t0\t0\tA durable scratch fortune.' "$build_fort/fortunes.txt"
expect_no_match "$build_fort" '.build-corpus.*'

# --check passes on the file it just wrote, refuses a drifted one without rewriting it, and refuses
# to compare against a missing one.
(cd / && bash "$build_fort/build-corpus.sh" --check) | grep -Fq 'OK: fortunes.txt matches its 2 input source(s), 3 rows.'
built_hash="$(sha256_file "$build_fort/fortunes.txt")"
printf 'beta\tfacts\tgeneral\t0\t0\tA row added by hand.\n' >> "$build_fort/fortunes.txt"
drifted_hash="$(sha256_file "$build_fort/fortunes.txt")"
expect_failure_matching "STALE: " bash "$build_fort/build-corpus.sh" --check
[ "$(sha256_file "$build_fort/fortunes.txt")" = "$drifted_hash" ]
rm -f -- "$build_fort/fortunes.txt"
# The expectation must not start with a dash: expect_failure_matching greps it without `--`.
expect_failure_matching "fortunes.txt does not exist" bash "$build_fort/build-corpus.sh" --check
(cd / && bash "$build_fort/build-corpus.sh") >/dev/null
[ "$(sha256_file "$build_fort/fortunes.txt")" = "$built_hash" ]


# TERM while the input rename is in flight restores prior input, absent metadata, and store bytes.
input_before="$(sha256_file "$fort/label-input.tsv")"
store_before="$(sha256_file "$fort/labels-store.tsv")"
[ ! -e "$fort/label-input.meta" ]
expect_term_rollback "$fort/label-input.tsv" \
  'ROLLBACK: restoring labeling input and metadata.' \
  bash "$fort/label-build-input.sh"
expect_same_hash "$fort/label-input.tsv" "$input_before"
expect_same_hash "$fort/labels-store.tsv" "$store_before"
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

# TERM while the store rename is in flight restores both the prior store and metadata, then releases the lock.
merge_store_before="$(sha256_file "$fort/labels-store.tsv")"
merge_meta_existed=0
merge_meta_before=""
if [ -f "$fort/labels-store.meta" ]; then
  merge_meta_existed=1
  merge_meta_before="$(sha256_file "$fort/labels-store.meta")"
fi
expect_term_rollback "$fort/labels-store.tsv" \
  'ROLLBACK: restoring the prior merged label store and metadata.' \
  bash "$fort/label-merge.sh"
expect_same_hash "$fort/labels-store.tsv" "$merge_store_before"
if [ "$merge_meta_existed" -eq 1 ]; then
  expect_same_hash "$fort/labels-store.meta" "$merge_meta_before"
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

# TERM while the first corpus rename is in flight restores every target and releases the shared lock.
apply_embedded_before="$(sha256_file "$fort/fortunes.txt")"
apply_pack_before="$(sha256_file "$packs/test-pack.txt")"
expect_term_rollback "$fort/fortunes.txt" \
  'ROLLBACK: restoring 1 corpus file(s).' \
  bash "$fort/label-apply.sh" --go \
    --metadata-plan "$apply_plan" \
    --acknowledge-metadata-finalization
expect_same_hash "$fort/fortunes.txt" "$apply_embedded_before"
expect_same_hash "$packs/test-pack.txt" "$apply_pack_before"
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

# TERM while the batch-text promotion is in flight restores the prior snapshot and releases the lock.
: > "$fort/labels-store.tsv"
next_texts_before="$(sha256_file "$fort/.batchtexts")"
expect_term_rollback "$fort/.batchtexts" \
  'ROLLBACK: restoring the prior batch-text snapshot.' \
  bash "$fort/label-next.sh" 2
expect_same_hash "$fort/.batchtexts" "$next_texts_before"
[ ! -e "$fort/.label-pipeline.lock" ]
expect_no_match "$fort" '*.label-restore.*'

# Empty-store batching exercises the FILENAME-based fix; invalid ingest remains atomic.
(cd / && bash "$fort/label-next.sh" 2) >/dev/null
[ "$(awk 'END {print NR}' "$fort/.batchtexts")" -eq 2 ]
printf 'not-a-topic quip\nlife dark\n' > "$fort/label-batch.txt"
before_store="$(sha256_file "$fort/labels-store.tsv")"
expect_failure bash "$fort/label-ingest.sh"
[ "$(sha256_file "$fort/labels-store.tsv")" = "$before_store" ]

# Tabs are accepted as label separators and are normalized before paste. TERM while the store
# promotion is in flight restores the store, metadata, batch, and batch-text snapshot byte-for-byte.
printf 'tech\tquip\nlife\tdark\n' > "$fort/label-batch.txt"
ingest_store_before="$(sha256_file "$fort/labels-store.tsv")"
ingest_meta_before="$(sha256_file "$fort/labels-store.meta")"
ingest_batch_before="$(sha256_file "$fort/label-batch.txt")"
ingest_texts_before="$(sha256_file "$fort/.batchtexts")"
expect_term_rollback "$fort/labels-store.tsv" \
  'ROLLBACK: restoring label ingest inputs and outputs.' \
  bash "$fort/label-ingest.sh"
expect_same_hash "$fort/labels-store.tsv" "$ingest_store_before"
expect_same_hash "$fort/labels-store.meta" "$ingest_meta_before"
expect_same_hash "$fort/label-batch.txt" "$ingest_batch_before"
expect_same_hash "$fort/.batchtexts" "$ingest_texts_before"
[ ! -e "$fort/.label-pipeline.lock" ]
expect_no_match "$fort" '*.label-restore.*'

(cd / && bash "$fort/label-ingest.sh") >/dev/null
[ "$(awk 'END {print NR}' "$fort/labels-store.tsv")" -eq 2 ]
[ ! -s "$fort/label-batch.txt" ]
[ ! -s "$fort/.batchtexts" ]

echo "label pipeline self-test: PASS"
