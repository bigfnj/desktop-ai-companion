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

# ONE spelling for every fixture path (N-bd-w4-contracts-1-04). mktemp keeps TMPDIR's form, and under Git Bash a
# drive-letter TMPDIR (D:/...) stays D:/..., while every labeling script resolves its own folder with cd and pwd,
# which answers /d/.... The TERM stages compare the mv wrapper's destination, spelled by the script, with
# LABEL_TEST_SIGNAL_TARGET, spelled from this path, so with a drive-letter TMPDIR no destination ever matched and
# every TERM stage failed with "never renamed onto". The fixture is resolved the way the scripts resolve themselves.
# A separate variable, so a failed resolution leaves $fixture naming the folder the EXIT trap removes.
fixture_resolved="$(CDPATH= cd -- "$fixture" && pwd)"
fixture="$fixture_resolved"

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
# BOTH AXES MUST BE ABLE TO VARY (N-audit-1106-140). The duplicate sources are B-source and a-source:
# bytewise 'B' sorts first, while a collating locale (and bash's glob under it) puts 'a' first, so a
# build-input that dropped its bytewise sort would keep B-source under C and a-source under that locale.
# They were a-source and z-source, which every locale orders the same way, so the sort could go unnoticed.
# The locale is chosen by PROBING that it collates differently from C, never by taking the first
# non-C name `locale -a` prints; a host with none says DEGRADED on every run instead of passing quietly.
collating_locale=""
for candidate in en_US.UTF-8 en_US.utf8 $(locale -a 2>/dev/null | LC_ALL=C grep -v -E '^(C|C[.]UTF-8|C[.]utf8|POSIX)$' || true); do
  if [ "$(printf 'B\na\n' | LC_ALL="$candidate" sort 2>/dev/null | sed -n '1p')" = 'a' ]; then
    collating_locale="$candidate"
    break
  fi
done
[ -n "$collating_locale" ] ||
  echo 'label pipeline self-test: DEGRADED: no locale here collates differently from C; the locale legs of the determinism and comm stages run under C' >&2
det_fort="$fixture/determinism/project/src/Fortunes"
det_packs="$fixture/determinism/project/packs"
mkdir -p -- "$det_fort" "$det_packs"
cp -- "$SOURCE_DIR/label-common.sh" "$SOURCE_DIR/label-build-input.sh" "$det_fort/"
cat > "$det_fort/fortunes.txt" <<'EOF'
embedded	general	general	0	Unique embedded fortune.
EOF
: > "$det_fort/labels-store.tsv"
printf 'pack	general	general	0\tShared duplicate fortune.\n' > "$det_packs/B-source.txt"
printf 'pack	general	general	0\tShared duplicate fortune.\n' > "$det_packs/a-source.txt"
(cd / && LC_ALL=C bash "$det_fort/label-build-input.sh") >/dev/null
cp -- "$det_fort/label-input.tsv" "$fixture/deterministic-input.tsv"
cp -- "$det_fort/label-input.meta" "$fixture/deterministic-input.meta"
deterministic_hash="$(sha256_file "$det_fort/label-input.tsv")"
grep -Fqx $'B-source\tShared duplicate fortune.' "$det_fort/label-input.tsv" ||
  { echo 'determinism: the bytewise-lowest source (B-source) did not keep the duplicate text under C' >&2; exit 1; }

# Recreate the duplicate-bearing files in the opposite order and build under the collating locale
# probed above. The generated input must remain byte-for-byte identical.
rm -f -- "$det_packs/a-source.txt" "$det_packs/B-source.txt"
printf 'pack	general	general	0\tShared duplicate fortune.\n' > "$det_packs/a-source.txt"
printf 'pack	general	general	0\tShared duplicate fortune.\n' > "$det_packs/B-source.txt"
alternate_locale="${collating_locale:-C}"
(cd / && LC_ALL="$alternate_locale" bash "$det_fort/label-build-input.sh") >/dev/null
cmp -s "$fixture/deterministic-input.tsv" "$det_fort/label-input.tsv" ||
  { printf 'determinism: label-input.tsv differs under %s from the C build\n' "$alternate_locale" >&2; exit 1; }
cmp -s "$fixture/deterministic-input.meta" "$det_fort/label-input.meta"
[ "$(sha256_file "$det_fort/label-input.tsv")" = "$deterministic_hash" ]

# comm walks its inputs in its own locale's order, so every comm in the pipeline runs under the C locale its keys
# were sorted in (N-audit-1106-139). Under a collating locale it mis-paired "Bravo..." and "alpha...", and label-next
# refused a valid store as holding a text outside the input. This drives label-next under the probed locale; the
# source check after it holds the other comm sites, which this stage cannot reach, to the same prefix.
loc_fort="$fixture/locale/project/src/Fortunes"
mkdir -p -- "$loc_fort" "$fixture/locale/project/packs"
cp -- "$SOURCE_DIR/label-common.sh" "$SOURCE_DIR/label-next.sh" "$loc_fort/"
printf 'embedded\tBravo locale fortune.\nembedded\talpha locale fortune.\n' > "$loc_fort/label-input.tsv"
printf 'alpha locale fortune.\ttech\tquip\n' > "$loc_fort/labels-store.tsv"
if ! (cd / && LC_ALL="${collating_locale:-C}" bash "$loc_fort/label-next.sh" 5) >/dev/null 2>"$fixture/locale-next.err"; then
  printf 'comm locale: label-next refused a valid store under %s: %s\n' "${collating_locale:-C}" "$(cat "$fixture/locale-next.err")" >&2
  exit 1
fi
[ "$(cat "$loc_fort/.batchtexts")" = 'Bravo locale fortune.' ] ||
  { echo 'comm locale: label-next did not batch exactly the one unlabeled text' >&2; exit 1; }
# The pipeline's scripts, this one excluded: its own greps name both patterns, and counting them made the witness
# below pass with no real site at all.
pipeline_scripts=()
for script in "$SOURCE_DIR"/label-*.sh; do
  [ "${script##*/}" = label-selftest.sh ] || pipeline_scripts+=("$script")
done
prefixed_comm="$(grep -h 'LC_ALL=C comm -' "${pipeline_scripts[@]}" | grep -c . || true)"
bare_comm="$(grep -n 'comm -' "${pipeline_scripts[@]}" | grep -v 'LC_ALL=C comm -' || true)"
[ "$prefixed_comm" -gt 0 ] && [ -z "$bare_comm" ] ||
  { printf 'comm locale: a pipeline comm runs in the caller locale (prefixed sites seen: %s): %s\n' "$prefixed_comm" "$bare_comm" >&2; exit 1; }

# A taxonomy value is an exact member of its list (N-audit-1106-138). Two adjacent values joined by the list's own
# delimiter ("tech|science") passed the substring test in every validator. The two shared validators are driven
# directly; ingest's and merge's awk use the same label_member, and the source check holds every validator to it.
pipe_store="$fixture/pipe-label-store.tsv"
printf 'A text with a joined topic.\ttech|science\tquip\n' > "$pipe_store"
expect_failure_matching 'labels-store.tsv has invalid' \
  bash -c '. "$1/label-common.sh"; label_validate_store "$2"' _ "$det_fort" "$pipe_store"
printf 'A text with a joined genre.\ttech\tpun|quip\n' > "$pipe_store"
expect_failure_matching 'labels-store.tsv has invalid' \
  bash -c '. "$1/label-common.sh"; label_validate_store "$2"' _ "$det_fort" "$pipe_store"
printf 'src\ttech\tquip\tgeneral|edgy\t0\tA text with a joined level.\n' > "$pipe_store"
expect_failure_matching 'invalid schema-v2 output' \
  bash -c '. "$1/label-common.sh"; label_validate_six_column_file "$2"' _ "$det_fort" "$pipe_store"
printf 'A text with one topic.\ttech\tquip\n' > "$pipe_store"
bash -c '. "$1/label-common.sh"; label_validate_store "$2"' _ "$det_fort" "$pipe_store" ||
  { echo 'taxonomy: a valid single-member store row was refused' >&2; exit 1; }
substring_tests="$(grep -n 'index(\(topics\|genres\|levels\)' "${pipeline_scripts[@]}" || true)"
[ -z "$substring_tests" ] ||
  { printf 'taxonomy: a validator still tests membership by substring: %s\n' "$substring_tests" >&2; exit 1; }

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
# The builder reads the module's sets and limits from the module's source (N-bd-w8-fortunes-1-01), so the fixture
# carries the real file: every row below is judged by the rule the module ships with.
rule_copy="$build_root/modules/Fortunes/engine/FortuneProvider.cs"
mkdir -p -- "${rule_copy%/*}"
cp -- "$SOURCE_DIR/../../modules/Fortunes/engine/FortuneProvider.cs" "$rule_copy"
cp -- "$rule_copy" "$fixture/rule-original.cs"
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
# Every level and both profanity flags appear, so a builder that refuses one of them fails the clean build below.
# (These rows used to carry a level of "0" or "1" and a topic of "facts", none of which the module loads.)
printf 'alpha\ttech\tquip\tgeneral\t0\tA durable scratch fortune.\nalpha\tlife\tdark\tedgy\t1\tA second scratch fortune.\n' \
  > "$build_fort/sources/alpha.tsv"
printf 'beta\tscience\tfact\tgeneral\t0\tA durable scratch fortune.\nbeta\thealth-body\tuplifting\tnsfw\t0\tA pack-only fortune line.\n' \
  > "$build_root/packs/beta.txt"
cp -- "$build_fort/sources/alpha.tsv" "$fixture/clean-alpha.tsv"

# Row validation refuses before anything is combined: a short row, a source column that disagrees
# with its file, and a text outside the 8..280 bound.
printf 'alpha\ttech\tquip\tgeneral\tFive fields only.\n' > "$build_fort/sources/alpha.tsv"
expect_failure_matching "expected 6 tab-separated fields" bash "$build_fort/build-corpus.sh"
build_preserved
printf 'gamma\ttech\tquip\tgeneral\t0\tA row filed under the wrong source.\n' > "$build_fort/sources/alpha.tsv"
expect_failure_matching 'source column is "gamma"' bash "$build_fort/build-corpus.sh"
build_preserved
printf 'alpha\ttech\tquip\tgeneral\t0\tshort\n' > "$build_fort/sources/alpha.tsv"
expect_failure_matching "text length 5 outside 8..280" bash "$build_fort/build-corpus.sh"
build_preserved

# The text is counted as the module counts it (N-bd-w7-fortunes-1-01): decoded, zero-width characters
# dropped, trimmed, then 8 to 280 user-perceived characters with no control character. The old check
# counted the raw column's BYTES, so every refused row below except the five-byte one above passed it,
# and one such row refuses the whole embedded corpus at load; each row pins one step of the rule. Built
# from octal UTF-8 bytes, never literals: U+4F60 is \344\275\240, U+1F600 is \360\237\230\200, the flag
# U+1F1EF U+1F1F5 is \360\237\207\257\360\237\207\265, and the Devanagari conjunct KA VIRAMA SSA is
# \340\244\225\340\245\215\340\244\267.
cjk='\344\275\240'
emoji='\360\237\230\200'
flag='\360\237\207\257\360\237\207\265'
kvs='\340\244\225\340\245\215\340\244\267'
# refuse_row's $1: the row after "alpha<TAB>", as a printf format; refuse_text's $1: the text column alone. $2: the
# refusal it must draw; $3: the step of the rule the row pins, named when the builder admits the row, so a failure
# says which step went.
refuse_text() { refuse_row "tech\tquip\tgeneral\t0\t$1" "$2" "$3"; }
refuse_row() {
  local output
  printf "alpha\t$1\n" > "$build_fort/sources/alpha.tsv"
  if output="$(bash "$build_fort/build-corpus.sh" 2>&1)"; then
    printf 'build-corpus admitted a row the module refuses: %s\n' "$3" >&2
    exit 1
  fi
  printf '%s\n' "$output" | grep -Fq "$2" || {
    printf 'failure did not contain %q: %s\n' "$2" "$output" >&2
    exit 1
  }
  build_preserved
}
refuse_text "$cjk$cjk$cjk" "text length 3 outside 8..280" "three ideographs, 9 bytes"
refuse_text "$emoji$emoji$emoji$emoji$emoji$emoji$emoji" "text length 7 outside 8..280" "seven emoji, 28 bytes"
refuse_text "$flag$flag$flag$flag" "text length 4 outside 8..280" "four flags, 8 code points"
refuse_text '&amp;&amp;&amp;&amp;' "text length 4 outside 8..280" "four escaped ampersands, decoded"
refuse_text 'abc&amp;#x200B;&amp;#x200B;&amp;#x200B;&amp;#x200B;&amp;#x200B;' "text length 3 outside 8..280" \
  "three letters and five twice-escaped zero-width spaces, decoded twice and dropped"
refuse_text '&nbsp;&nbsp;&nbsp;short' "text length 5 outside 8..280" "escaped no-break spaces, trimmed after decoding"
refuse_text 'A tab&#9;hides inside this fortune text.' "text holds a control character" "an escaped tab"
refuse_text 'Not UTF-8 at all: \377 is a stray byte.' "not valid UTF-8" "a stray byte"

# The rest of the module's row rule (N-bd-w8-fortunes-1-01): TryParseCurrentRow refuses a level, topic or genre
# outside FortuneTaxonomy's sets (ordinal, so case counts) and a profanity flag other than 0 or 1, and
# TryParseTaggedContent a line over MaximumTaggedLineCharacters, counted in UTF-16 code units. The builder checked
# none of them, so every row below passed it and would have emptied the embedded corpus at load. U+1F44D U+1F3FD
# (a thumbs-up with a skin tone, one character) is \360\237\221\215\360\237\217\275: four code units, so 260 of
# them make a line of 546 code points and 1066 code units. The padding is a twice-escaped zero-width space, which
# the module decodes and drops, so it lengthens the line and not the text: 81 of them and a 26-character text
# make a line of exactly 1024 code units after the 26 of "alpha<TAB>tech<TAB>quip<TAB>general<TAB>0<TAB>".
thumb='\360\237\221\215\360\237\217\275'
zw81="$(printf '&amp;#x200B;%.0s' $(seq 1 81))"
refuse_row 'Tech\tquip\tgeneral\t0\tA topic spelled in the wrong case.' 'unknown topic "Tech"' "a topic outside the set, by case"
refuse_row 'tech\tpuns\tgeneral\t0\tA genre the module does not have.' 'unknown genre "puns"' "a genre outside the set"
refuse_row 'tech\tquip\tspicy\t0\tA level the module does not have.' 'unknown level "spicy"' "a level outside the set"
refuse_row 'tech\tquip\tgeneral\t2\tA flag that is neither 0 nor 1.' 'profanity flag is "2"' "a profanity flag of 2"
refuse_row 'tech\tquip\tgeneral\t01\tA flag with a leading zero.' 'profanity flag is "01"' "a profanity flag of 01"
refuse_text "$(printf "$thumb%.0s" $(seq 1 260))" "line is 1066 UTF-16 code units, over the module limit of 1024" \
  "a line of 546 code points and 1066 UTF-16 code units"
refuse_text "A line a unit over the cap.$zw81" "line is 1025 UTF-16 code units" "a line one code unit over the cap"
# A source one character over MaximumSourceCharacters (the manifest grammar keeps an id ASCII).
long_id="$(printf 'a%.0s' $(seq 1 129))"
printf '%s\n' "$long_id" > "$build_fort/corpus-required-files.txt"
printf '%s\ttech\tquip\tgeneral\t0\tA source id one character too long.\n' "$long_id" > "$build_fort/sources/$long_id.tsv"
expect_failure_matching "source is 129 characters, over the module limit of 128" bash "$build_fort/build-corpus.sh"
build_preserved
rm -f -- "$build_fort/sources/$long_id.tsv"
printf 'alpha\nbeta\n' > "$build_fort/corpus-required-files.txt"

# The sets and limits are read from the module's source, and what cannot be read stops the build: a missing source,
# a set or limit in another shape, declared twice, or holding something other than plain literals (a block comment
# around a member included). A member commented out with // is not a member. $1: a perl substitution that makes the
# fixture's copy of the source from the original; $2: what the build must say.
refuse_rule() {
  perl -0pe "$1" -- "$fixture/rule-original.cs" > "$rule_copy"
  if cmp -s "$fixture/rule-original.cs" "$rule_copy"; then
    printf 'the substitution changed nothing in the module source: %s\n' "$1" >&2
    exit 1
  fi
  expect_failure_matching "$2" bash "$build_fort/build-corpus.sh"
  build_preserved
}
printf 'alpha\ttech\tquip\tgeneral\t0\tA row for the rule checks.\n' > "$build_fort/sources/alpha.tsv"
rm -f -- "$rule_copy"
expect_failure_matching "cannot read the module's row rule from" bash "$build_fort/build-corpus.sh"
build_preserved
refuse_rule 's/TopicSet = new HashSet<string>\(/TopicSet = BuildSet(/' "cannot read TopicSet in the shape"
refuse_rule 's/\z/\nstatic class Twin { static readonly HashSet<string> GenreSet = new HashSet<string>(new[] { "x" }); }\n/' \
  "cannot read GenreSet in the shape"
refuse_rule 's/"life" \}/"life", \/* "animals", *\/ }/' "cannot read TopicSet in the shape"
refuse_rule 's/(const int MaximumSourceCharacters = )128;/${1}MaxSource;/' "cannot read MaximumSourceCharacters in the shape"
perl -0pe 's/"life" \}/"life", \/\/ "animals",\n }/' -- "$fixture/rule-original.cs" > "$rule_copy"
grep -Fq '"life", // "animals",' "$rule_copy" || { echo "the commented member never reached the module source copy" >&2; exit 1; }
refuse_row 'animals\tquip\tgeneral\t0\tA topic the module has only in a comment.' 'unknown topic "animals"' \
  "a set member commented out with //"
cp -- "$fixture/rule-original.cs" "$rule_copy"

# WITNESS: rows the module loads. 100 ideographs are 300 bytes and 100 characters, which the old check
# refused; four conjuncts are eight characters to .NET 10, which has no Unicode 15.1 conjunct rule,
# while perl's own \X makes them one. A line of exactly 1024 code units and a source of exactly 128 characters sit
# on the module's limits, which refuse only what is longer.
id128="$(printf 'a%.0s' $(seq 1 128))"
printf 'alpha\nbeta\n%s\n' "$id128" > "$build_fort/corpus-required-files.txt"
printf '%s\ttech\tquip\tgeneral\t0\tA source id exactly at its limit.\n' "$id128" > "$build_fort/sources/$id128.tsv"
printf "alpha\ttech\tquip\tgeneral\t0\t%s\nalpha\ttech\tquip\tgeneral\t0\t$kvs$kvs$kvs$kvs\nalpha\ttech\tquip\tgeneral\t0\t%s\n" \
  "$(printf "$cjk%.0s" $(seq 1 100))" "A line exactly at the cap.$zw81" > "$build_fort/sources/alpha.tsv"
if ! (cd / && bash "$build_fort/build-corpus.sh") >/dev/null 2>&1 ||
   [ "$(grep -c '^alpha' "$build_fort/fortunes.txt")" != 3 ] ||
   [ "$(grep -c "^$id128" "$build_fort/fortunes.txt")" != 1 ]; then
  echo "build-corpus refused a row the module loads (100 ideographs, four conjuncts, a 1024-unit line or a 128-character source)" >&2
  exit 1
fi
rm -f -- "$build_fort/sources/$id128.tsv"
printf 'alpha\nbeta\n' > "$build_fort/corpus-required-files.txt"
cp -- "$fixture/clean-alpha.tsv" "$build_fort/sources/alpha.tsv"

# A clean build: four input rows, one text shared by alpha and beta, so three rows come out, the
# duplicate resolved in manifest order (alpha keeps it), sorted C-locale by source then text.
(cd / && bash "$build_fort/build-corpus.sh") >/dev/null
grep -Fqx $'alpha\ttech\tquip\tgeneral\t0\tA durable scratch fortune.' "$build_fort/fortunes.txt"
# An `if`, not a bare `! grep`: set -e ignores a negated command, so that form could never stop the script
# (N-audit-1106-141). Ahead of the row counts so a kept duplicate is reported as itself.
if grep -Fq $'beta\tscience\tfact\tgeneral\t0\tA durable scratch fortune.' "$build_fort/fortunes.txt"; then
  echo "build-corpus kept beta's copy of a text alpha already supplies" >&2
  exit 1
fi
awk -F'\t' 'NF != 6 { exit 1 } END { if (NR != 3) exit 1 }' "$build_fort/fortunes.txt"
[ "$(cut -f1 "$build_fort/fortunes.txt" | LC_ALL=C sort | uniq -c | awk '{print $1":"$2}' | paste -sd, -)" = "2:alpha,1:beta" ]
expect_no_match "$build_fort" '.build-corpus.*'

# --check passes on the file it just wrote, refuses a drifted one without rewriting it, and refuses
# to compare against a missing one.
(cd / && bash "$build_fort/build-corpus.sh" --check) | grep -Fq 'OK: fortunes.txt matches its 2 input source(s), 3 rows.'
built_hash="$(sha256_file "$build_fort/fortunes.txt")"
printf 'beta\tscience\tfact\tgeneral\t0\tA row added by hand.\n' >> "$build_fort/fortunes.txt"
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

# A complete, ordered, locked chunk set replaces the store.
cat > "$fort/label-chunks/chunk001.out" <<'EOF'
1 tech quip
2 life dark
3 science fact
EOF

# An unexpected output with no corresponding input chunk is also fatal and atomic. It runs AFTER chunk001.out
# exists: run before it, merge failed on the missing chunk, so deleting the extra-output guard left this case green
# (N-audit-1106-142). The match is the guard's own NEEDS REDO entry.
printf '1 tech quip\n' > "$fort/label-chunks/chunk999.out"
expect_failure_matching 'chunk999(extra-output)' bash "$fort/label-merge.sh"
[ "$(sha256_file "$fort/labels-store.tsv")" = "$before_store" ] ||
  { echo "extra-output failure changed prior store" >&2; exit 1; }
rm -f -- "$fort/label-chunks/chunk999.out"

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
# WHICH label each text received, not only how many rows came out (N-audit-1106-143). No two texts share a topic or a
# genre here, so a merge that paired a chunk line with the wrong output line keeps the count, the shape and the
# taxonomy and still fails. The store follows label-input.tsv's order, which merge itself enforces.
printf 'Alpha fortune text.\ttech\tquip\nBeta fortune text.\tlife\tdark\nGamma fortune text.\tscience\tfact\n' \
  > "$fixture/expected-merged-store.tsv"
cmp -s "$fixture/expected-merged-store.tsv" "$fort/labels-store.tsv" ||
  { printf 'merge: a text did not receive its own chunk label: %s\n' "$(tr '\t\n' ' |' < "$fort/labels-store.tsv")" >&2; exit 1; }

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
# And each row carries its OWN text's labels (N-audit-1106-143): the invariant check above holds only the columns
# apply must not touch, so a pipeline that gave every text another text's valid labels passed it.
if ! { grep -Fqx $'srcA\ttech\tquip\tgeneral\t0\tAlpha fortune text.' "$fort/fortunes.txt" &&
       grep -Fqx $'srcA\tlife\tdark\tedgy\t1\tBeta fortune text.' "$fort/fortunes.txt" &&
       grep -Fqx $'srcB\tscience\tfact\tgeneral\t0\tGamma fortune text.' "$packs/test-pack.txt"; }; then
  printf 'apply: a corpus row does not carry its own text'"'"'s labels: %s\n' \
    "$(cat "$fort/fortunes.txt" "$packs/test-pack.txt" | tr '\t\n' ' |')" >&2
  exit 1
fi

# An incomplete store is fatal and leaves every promoted target byte-for-byte unchanged. The plan is FRESH for this
# state (N-audit-1106-144): emitted after the apply above, so its target hashes are the files as they now are, with
# only its store hash swapped for the incomplete store's (--emit-plan itself refuses an incomplete store). The case
# reused the plan that apply had already made stale, so with label-apply's completeness check removed it still
# failed, on the plan, and could not say which guard fired. The match is the completeness guard's own message.
fresh_plan="$fixture/fresh-label-apply-plan.tsv"
(cd / && bash "$fort/label-apply.sh" --emit-plan) > "$fresh_plan"
head -n 2 "$fort/labels-store.tsv" > "$fixture/incomplete-store"
mv -f -- "$fixture/incomplete-store" "$fort/labels-store.tsv"
incomplete_store_hash="$(sha256_file "$fort/labels-store.tsv")"
sed "s/^label_store_sha256\t[0-9a-f]*\$/label_store_sha256\t$incomplete_store_hash/" "$fresh_plan" \
  > "$fixture/fresh-plan-next.tsv"
mv -f -- "$fixture/fresh-plan-next.tsv" "$fresh_plan"
grep -Fqx "label_store_sha256"$'\t'"$incomplete_store_hash" "$fresh_plan" ||
  { echo 'incomplete store: the fresh plan does not carry the incomplete store hash' >&2; exit 1; }
embedded_hash="$(sha256_file "$fort/fortunes.txt")"
pack_hash="$(sha256_file "$packs/test-pack.txt")"
expect_failure_matching "label store key set does not exactly match label input" \
  bash "$fort/label-apply.sh" --go \
    --metadata-plan "$fresh_plan" \
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
cp -- "$fort/.batchtexts" "$fixture/ingest-batch-texts"
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
# Line N of the batch labels line N of the batch texts (N-audit-1106-143). The two texts and their labels differ, so
# an ingest that paired them the other way round keeps the count and the taxonomy and still fails here.
ingest_first="$(sed -n '1p' "$fixture/ingest-batch-texts")"
ingest_second="$(sed -n '2p' "$fixture/ingest-batch-texts")"
[ -n "$ingest_first" ] && [ -n "$ingest_second" ] && [ "$ingest_first" != "$ingest_second" ] ||
  { echo 'ingest: the batch did not hold two distinct texts to pair' >&2; exit 1; }
if ! { grep -Fqx "$ingest_first"$'\ttech\tquip' "$fort/labels-store.tsv" &&
       grep -Fqx "$ingest_second"$'\tlife\tdark' "$fort/labels-store.tsv"; }; then
  printf 'ingest: a text did not receive the label on its own batch line: %s\n' \
    "$(tr '\t\n' ' |' < "$fort/labels-store.tsv")" >&2
  exit 1
fi
[ ! -s "$fort/label-batch.txt" ]
[ ! -s "$fort/.batchtexts" ]

echo "label pipeline self-test: PASS"
