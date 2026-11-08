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
# --check asserts. tests/Invoke-OfflineScriptChecks.ps1 runs --check in Git Bash and fails on its
# exit code, and the gate (tests/run-gate.ps1) and CI (.github/workflows/build.yml) both call that
# runner, so a corpus that drifts from its inputs (packs/dadjokes.txt above all) fails there. Until
# N-audit-1106-137 nothing ran it, while this header said "in CI".
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
RULE_SOURCE="$REPO_ROOT/modules/Fortunes/engine/FortuneProvider.cs"   # the module's row rule (see the row check)
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
#
# THE TEXT IS COUNTED THE WAY THE MODULE COUNTS IT (N-bd-w7-fortunes-1-01). The module decodes a row's
# text column (WebUtility.HtmlDecode, at most twice), drops the zero-width characters, trims, and then
# requires 8 to 280 user-perceived characters (extended grapheme clusters, as StringInfo walks them) and
# no control character: FortuneProvider.DecodeScrapedText and IsValidFortuneText. One row that fails
# refuses the WHOLE embedded corpus, so the module would start with nothing to say. This check used to
# be `LC_ALL=C awk length($6)`, the BYTE length of the undecoded column, and it was wrong both ways:
# three CJK ideographs (9 bytes), seven emoji (28 bytes) or "&amp;&amp;&amp;&amp;" (20 bytes, four
# characters once decoded) passed here and would have emptied the corpus at load, while a line of 100
# ideographs (300 bytes, 100 characters) was refused here although the module loads it.
#
# perl takes the module's steps in order. Git Bash ships it; `\X` is its extended grapheme cluster;
# html_decode is WebUtility.HtmlDecode's walk, over the same 253 HTML 4 entity names, taken from
# HTML::Entities' table and honoured only with the ";" WebUtility requires (that module's own
# decode_entities also expands a bare "&amp", which the module leaves as it is). One rule differs:
# perl's `\X` joins an Indic conjunct (Unicode 15.1's GB9c) and .NET 10's StringInfo does not, and
# perl 5.42 joins more than the rule says (measured: KA VIRAMA SSA written twice is one cluster to
# perl and four to .NET). A ZWNJ after each conjunct linker (InCB=Linker) switches that rule off and
# changes nothing else, since a ZWNJ only ever extends the cluster before it; it goes into a copy that
# is counted, never into the row. Measured when this changed, against .NET 10's WebUtility and
# StringInfo: the same decoded text and the same count for every row of both inputs, for 60 synthetic
# rows (CJK, emoji, flags, ZWJ families, skin tones, keycaps, combining marks, Indic conjuncts, Hangul
# jamo, and every numeric entity form above), and for one row per name in the entity table plus eight
# names it does not hold. Rejected: gawk in a UTF-8 locale, which counts
# code points and so still counts a flag, or an emoji with its variation selector, as two; and running
# the module's own parser, which needs a build this offline check does not have. A later Unicode
# version on either side can still move a count; the module self-test's embedded_rows line
# (ValidateEmbeddedForSelfTest) parses the shipped file with the module's own code and stays the
# backstop for that.
#
# THE REST OF THE MODULE'S ROW RULE IS CHECKED TOO (N-bd-w8-fortunes-1-01). TryParseTaggedContent and
# TryParseCurrentRow refuse the whole embedded corpus over one line longer than MaximumTaggedLineCharacters
# (string.Length, so UTF-16 code units), a source longer than MaximumSourceCharacters, a level, topic or genre
# outside FortuneTaxonomy's sets (compared ordinally), or a profanity flag other than "0" or "1". This check used
# to stop at the text, so a row labelled with a topic the module lacks passed here and only the module
# self-test's embedded_rows line noticed. The rules are taken in the module's order. The three sets and the two
# limits are READ FROM THE MODULE'S SOURCE ($RULE_SOURCE): each declaration must occur exactly once in the shape
# below, and every set member must be a plain string literal, or the build stops and names what it could not
# read. Rejected: a copy of the lists here. The taxonomy has grown once already (health-body, TAXONOMY.md), and
# a copy that kept a member the module dropped would admit the very row this check exists to refuse. The flag
# is the one rule copied, because it is the schema's boolean encoding rather than a list that grows. The source
# needs only its length checked: it must equal a manifest id, and the manifest grammar above already limits that
# to ASCII with no space or control character. Not checked here: the module's whole-file caps (MaximumTaggedRows,
# MaximumTaggedContentCharacters), which the corpus is far below; embedded_rows is the backstop for those.
perl -MHTML::Entities -e 1 2>/dev/null ||
  die "the row check needs perl with HTML::Entities (Git for Windows ships both), to count text as the module does"
row_check='
use strict; use warnings;
use Encode ();
use HTML::Entities ();
my ($rule_file, $id, $file) = @ARGV;
# The sets and limits of the module, read from its source. Given the source alone, the program reads them and
# stops. (No apostrophe anywhere in this program: the shell holds it in single quotes.)
sub unreadable { print STDERR "$rule_file: cannot read $_[0] in the shape this check reads\n"; exit 2 }
open(my $rule_in, "<:raw", $rule_file) or do { print STDERR "$rule_file: cannot read: $!\n"; exit 2 };
my $code = do { local $/; <$rule_in> };
close $rule_in;
my %member;
for my $set (qw(TopicSet GenreSet LevelSet)) {
  my @found = $code =~ /\b$set\s*=\s*new\s+HashSet<string>\(\s*new\s*\[\]\s*\{([^{}]*)\}/g;
  unreadable($set) if @found != 1;
  (my $items = $found[0]) =~ s{//[^\n]*}{}g;      # a member commented out is not a member
  my @values = $items =~ /"([^"\\]*)"/g;
  (my $rest = $items) =~ s/"[^"\\]*"|[\s,]//g;    # anything else (a constant, an escape) cannot be read
  unreadable($set) if !@values || length $rest;
  my %values = map { ($_, 1) } @values;
  $member{$set} = \%values;
}
my %limit;
for my $name (qw(MaximumTaggedLineCharacters MaximumSourceCharacters)) {
  my @found = $code =~ /\bconst\s+int\s+$name\s*=\s*([0-9]+)\s*;/g;
  unreadable($name) if @found != 1;
  $limit{$name} = $found[0];
}
exit 0 unless defined $file;
my %named = map { my $name = $_; $name =~ s/;\z//; ($name => $HTML::Entities::entity2char{$_}) }
            keys %HTML::Entities::entity2char;
my $ws = "[\t\n\x0B\f\r ]";   # NumberStyles.Integer white space
my $linker = eval { qr/(\p{InCB=Linker})/ } || qr/(?!)/;   # a perl older than Unicode 15.1 has no GB9c to undo
sub html_decode {             # WebUtility.HtmlDecode, step for step
  my ($s) = @_;
  my ($out, $i) = ("", 0);
  while ($i < length $s) {
    my $ch = substr($s, $i, 1);
    if ($ch eq "&" && substr($s, $i + 1) =~ /\A([^;&]*);/) {
      my ($entity, $after) = ($1, $i + length($1) + 2);
      if (length($entity) > 1 && substr($entity, 0, 1) eq "#") {
        my $v;
        if ($entity =~ /\A#[xX]0*([0-9A-Fa-f]{1,8})\z/) { $v = hex $1 }
        elsif ($entity =~ /\A#$ws*(?:\+|-(?=0+$ws*\z))?0*([0-9]{1,10})$ws*\z/ && $1 <= 4294967295) { $v = $1 }
        if (defined $v && ($v < 0xD800 || ($v > 0xDFFF && $v <= 0x10FFFF))) { $out .= chr $v; $i = $after; next }
      } else {
        $out .= exists $named{$entity} ? $named{$entity} : "&$entity;";
        $i = $after;
        next;
      }
    }
    $out .= $ch;
    $i++;
  }
  return $out;
}
sub refuse { printf STDERR "%s:%d: %s\n", $file, $., $_[0]; exit 2 }
open(my $in, "<:raw", $file) or do { print STDERR "$file: cannot read: $!\n"; exit 2 };
my $rows = 0;
while (my $line = <$in>) {
  $rows++;
  chomp $line;
  # The module reads the file with a throwing UTF-8 decoder, so one bad byte anywhere empties the corpus.
  my $decoded = eval { Encode::decode("UTF-8", $line, Encode::FB_CROAK | Encode::LEAVE_SRC) };
  refuse("not valid UTF-8") unless defined $decoded;
  # string.Length: a character beyond the BMP is a surrogate pair, two code units.
  my $units = length($decoded) + (() = $decoded =~ /[\x{10000}-\x{10FFFF}]/g);
  refuse(sprintf "line is %d UTF-16 code units, over the module limit of %d", $units, $limit{MaximumTaggedLineCharacters})
    if $units > $limit{MaximumTaggedLineCharacters};
  my @f = split /\t/, $line, -1;
  refuse(sprintf "expected 6 tab-separated fields, found %d", scalar @f) if @f != 6;
  refuse(sprintf "source column is \"%s\", expected \"%s\"", $f[0], $id) if $f[0] ne $id;
  refuse(sprintf "source is %d characters, over the module limit of %d", length($f[0]), $limit{MaximumSourceCharacters})
    if length($f[0]) > $limit{MaximumSourceCharacters};
  refuse(sprintf "unknown level \"%s\"", $f[3]) unless $member{LevelSet}{$f[3]};
  refuse(sprintf "profanity flag is \"%s\", expected 0 or 1", $f[4]) unless $f[4] eq "0" || $f[4] eq "1";
  my $text = Encode::decode("UTF-8", $f[5]);
  for (1 .. 2) { last if index($text, "&") < 0; my $d = html_decode($text); last if $d eq $text; $text = $d }
  $text =~ tr/\x{200B}\x{200C}\x{200D}\x{FEFF}//d;
  $text =~ s/\A\p{White_Space}+|\p{White_Space}+\z//g;
  (my $counted = $text) =~ s/$linker/$1\x{200C}/g;
  my $characters = () = $counted =~ /\X/g;
  refuse(sprintf "text length %d outside 8..280 characters", $characters) if $characters < 8 || $characters > 280;
  refuse("text holds a control character") if $text =~ /\p{Cc}/;
  refuse(sprintf "unknown topic \"%s\"", $f[1]) unless $member{TopicSet}{$f[1]};
  refuse(sprintf "unknown genre \"%s\"", $f[2]) unless $member{GenreSet}{$f[2]};
  print $line, "\n";
}
if ($rows == 0) { print STDERR "$file: no rows\n"; exit 2 }
'
perl -e "$row_check" -- "$RULE_SOURCE" || die "cannot read the module's row rule from $RULE_SOURCE"
raw="$stage_dir/raw.tsv"
: > "$raw"
for i in "${!ids[@]}"; do
  id="${ids[$i]}" file="${inputs[$i]}"
  perl -e "$row_check" -- "$RULE_SOURCE" "$id" "$file" >> "$raw" || die "input failed validation: $file"
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
