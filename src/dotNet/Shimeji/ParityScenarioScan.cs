using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The byte-level candidate scanner of signed revision H for N-parity-1107-03 (the CANDIDATE-SCAN profile,
    /// DECLARATION-DRAFT.md :48 and :56-62, control rows :191-195). It answers one question about the exact bytes a
    /// scenario read returned: does the root object carry a completed <c>"schema":"shimeji-parity-scenario/2"</c>
    /// member, and where and why did scanning stop. The answer is three facts: Candidate, a Reason that is exactly one
    /// of bom, utf8, lexical, depth, eof or none, and an absolute zero-based byte Offset.
    ///
    /// One forward pass over the bytes, in input order, and nothing else:
    /// - Every raw scalar is decoded by Unicode Table 3-7 before the grammar uses it, inside unrelated strings and
    ///   names too (H :58, LEXICAL-AT-OFFSET). An ill-formed one is utf8 at the first byte of its first maximal subpart
    ///   (D93b), which is where the current scalar began, even when the rejection is only knowable at a later
    ///   continuation byte or at EOF (H :60). Nothing is replaced and nothing is decoded ahead of the grammar, so an
    ///   earlier JSON stop is never overtaken by a later encoding fault.
    /// - The grammar is RFC 8259 with no extensions: numbers are lexed, never converted (1e100000 is a number), and a
    ///   backslash-u escape is one UTF-16 code unit, so an escaped unpaired surrogate is lexically fine (H :56).
    /// - Containers are counted with the root at depth 1 and at most 64 open (H :58). Grammar position is decided
    ///   before depth: only a bracket where a value may begin opens a container, and only that one can be a depth stop;
    ///   a bracket anywhere else is lexical at any depth (H :193).
    /// - Only an immediate member of a root OBJECT is a candidate site. Its decoded name and its string value are
    ///   compared with the ASCII literals code unit by code unit, ordinal and case-sensitive, so an escaped unpaired
    ///   surrogate compares unequal without stopping (H :56, :195). A completed marker is sticky: it survives any later
    ///   stop, and it needs no following delimiter (H :48, :194).
    ///
    /// Pure: no I/O, no exception as control flow, no state outside one call. It reads nothing, refuses nothing and
    /// routes nothing yet; admission is wired to it later, and only after revision H's joint final signature.
    ///
    /// Rejected alternatives: System.Text.Json's Utf8JsonReader, because its offsets are token starts, its depth default
    /// and its handling of an unpaired surrogate are its own, and H :62 says parser defaults are not this seam's
    /// specification; a whole-file UTF-8 decode first, because it reports a later encoding fault over an earlier
    /// JSON stop (H :58); and a recursive or textual marker search, because a nested or string-valued lookalike must
    /// not be a candidate (H :48).
    /// </summary>
    internal static class ParityScenarioScan
    {
        internal const string ReasonBom = "bom";
        internal const string ReasonUtf8 = "utf8";
        internal const string ReasonLexical = "lexical";
        internal const string ReasonDepth = "depth";
        internal const string ReasonEof = "eof";
        internal const string ReasonNone = "none";

        /// <summary>The most containers open at once, the root container counting as the first (H :58).</summary>
        internal const int MaximumDepth = 64;

        /// <summary>The immediate root-member name, compared by decoded code unit.</summary>
        internal const string SchemaMemberName = "schema";

        /// <summary>The completed string value that makes the document a candidate.</summary>
        internal const string CandidateMarker = "shimeji-parity-scenario/2";

        internal sealed class Result
        {
            public readonly bool Candidate;
            public readonly string Reason;
            public readonly int Offset;

            internal Result(bool candidate, string reason, int offset)
            {
                Candidate = candidate;
                Reason = reason;
                Offset = offset;
            }
        }

        internal static Result Scan(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            var scanner = new Scanner(bytes);
            scanner.Run();
            return new Result(scanner.Candidate, scanner.Reason, scanner.Offset);
        }

        /// <summary>
        /// The length of the well-formed UTF-8 scalar that starts at <paramref name="at"/> (Unicode Table 3-7), or, when
        /// the bytes there are ill-formed, the NEGATED length of the first maximal subpart (D93b): the lead byte and the
        /// continuation bytes that still extend it, ending before the first byte that cannot, or at EOF. A byte that
        /// begins no well-formed sequence (80..C1, F5..FF) is a maximal subpart of one byte.
        /// </summary>
        internal static int ScalarLength(byte[] bytes, int at)
        {
            byte lead = bytes[at];
            if (lead < 0x80) return 1;
            int continuations;
            int low = 0x80;
            int high = 0xBF;
            if (lead >= 0xC2 && lead <= 0xDF) continuations = 1;
            else if (lead == 0xE0) { continuations = 2; low = 0xA0; }
            else if (lead >= 0xE1 && lead <= 0xEC) continuations = 2;
            else if (lead == 0xED) { continuations = 2; high = 0x9F; }
            else if (lead == 0xEE || lead == 0xEF) continuations = 2;
            else if (lead == 0xF0) { continuations = 3; low = 0x90; }
            else if (lead >= 0xF1 && lead <= 0xF3) continuations = 3;
            else if (lead == 0xF4) { continuations = 3; high = 0x8F; }
            else return -1;
            for (int index = 1; index <= continuations; index++)
            {
                // Truncated at EOF, or cut short by a byte that cannot continue it: the subpart is what came before.
                if (at + index == bytes.Length) return -index;
                byte next = bytes[at + index];
                if (next < low || next > high) return -index;
                // Table 3-7 narrows the second byte only (E0, ED, F0, F4); every later one is 80..BF.
                low = 0x80;
                high = 0xBF;
            }
            return continuations + 1;
        }

        private enum Expect
        {
            RootValue,
            Value,
            ValueOrEnd,
            NameOrEnd,
            Name,
            Colon,
            CommaOrEnd,
            AfterRoot,
        }

        private sealed class Scanner
        {
            private readonly byte[] _bytes;
            // One entry per open container, true for an object; Count is the depth, the root being 1.
            private readonly List<bool> _containers = new List<bool>();
            private Expect _expect = Expect.RootValue;
            private int _pos;
            // Set by a root-member name that decoded to "schema", spent by the value that follows its colon.
            private bool _schemaValueNext;
            // The literal the current string is compared with, or null when it is no candidate site.
            private string _target;
            private int _matched;
            private bool _same;

            public bool Candidate;
            public string Reason;
            public int Offset;

            internal Scanner(byte[] bytes)
            {
                _bytes = bytes;
            }

            internal void Run()
            {
                // H :60: only an initial COMPLETE EF BB BF is bom at 0. A partial one is an ill-formed scalar (utf8 at
                // 0), and a U+FEFF anywhere later is an ordinary scalar, lexical outside a string.
                if (_bytes.Length >= 3 && _bytes[0] == 0xEF && _bytes[1] == 0xBB && _bytes[2] == 0xBF)
                {
                    Stop(ReasonBom, 0);
                    return;
                }
                while (Step()) { }
            }

            /// <summary>Records the one stop of this scan and returns false, which ends it: nothing after a stop is
            /// read, so no later marker can be discovered (H :48).</summary>
            private bool Stop(string reason, int offset)
            {
                Reason = reason;
                Offset = offset;
                return false;
            }

            /// <summary>Decodes the scalar at the current position. Ill-formed is utf8 at the start of its first maximal
            /// subpart, which is this position (H :60, D93b), never the byte where the rejection became knowable.</summary>
            private bool NextScalar(out int length)
            {
                length = ScalarLength(_bytes, _pos);
                if (length > 0) return true;
                return Stop(ReasonUtf8, _pos);
            }

            /// <summary>The byte here cannot extend the document. It is decoded first, because the encoding rule takes
            /// precedence at the same position (H :60): ill-formed is utf8, anything else is lexical here.</summary>
            private bool Reject()
            {
                if (_bytes[_pos] >= 0x80)
                {
                    int rejectedLength;
                    if (!NextScalar(out rejectedLength)) return false;
                }
                return Stop(ReasonLexical, _pos);
            }

            private bool Step()
            {
                if (_pos == _bytes.Length) return AtEnd();
                byte next = _bytes[_pos];
                if (IsWhitespace(next)) { _pos++; return true; }
                // No JSON token begins with a non-ASCII scalar, so one here is utf8 or lexical, in every state; after
                // the root that is H :60's next-scalar rule.
                if (next >= 0x80) return Reject();
                switch (_expect)
                {
                    case Expect.AfterRoot:
                        return Reject();
                    case Expect.NameOrEnd:
                        if (next == (byte)'}') return Close();
                        goto case Expect.Name;
                    case Expect.Name:
                        if (next != (byte)'"') return Reject();
                        return LexString(true);
                    case Expect.Colon:
                        if (next != (byte)':') return Reject();
                        _pos++;
                        _expect = Expect.Value;
                        return true;
                    case Expect.CommaOrEnd:
                        bool inObject = _containers[_containers.Count - 1];
                        if (next == (byte)',')
                        {
                            _pos++;
                            _expect = inObject ? Expect.Name : Expect.Value;
                            return true;
                        }
                        if (next == (inObject ? (byte)'}' : (byte)']')) return Close();
                        return Reject();
                    case Expect.ValueOrEnd:
                        if (next == (byte)']') return Close();
                        return Value(next);
                    default:
                        return Value(next);
                }
            }

            /// <summary>Well-formed input ran out. Only a complete root (and whitespace) is none; anything short of it,
            /// empty and whitespace-only input included, is eof. Both are at input length (H :60).</summary>
            private bool AtEnd()
            {
                if (_expect == Expect.AfterRoot) return Stop(ReasonNone, _bytes.Length);
                return Stop(ReasonEof, _bytes.Length);
            }

            /// <summary>A value begins here: this is the only place a bracket opens a container.</summary>
            private bool Value(byte first)
            {
                bool markerSite = _schemaValueNext;
                _schemaValueNext = false;
                switch (first)
                {
                    case (byte)'{':
                        return Open(true);
                    case (byte)'[':
                        return Open(false);
                    case (byte)'"':
                        return LexString(false, markerSite);
                    case (byte)'t':
                        return LexLiteral("true");
                    case (byte)'f':
                        return LexLiteral("false");
                    case (byte)'n':
                        return LexLiteral("null");
                    default:
                        if (first == (byte)'-' || IsDigit(first)) return LexNumber();
                        return Reject();
                }
            }

            private bool Open(bool isObject)
            {
                // Grammar first, then depth: Value() is reached only where a value may begin, so this bracket really
                // would open container Count + 1, and 65 is the depth stop at its opening byte (H :58, :193).
                if (_containers.Count == MaximumDepth) return Stop(ReasonDepth, _pos);
                _containers.Add(isObject);
                _pos++;
                _expect = isObject ? Expect.NameOrEnd : Expect.ValueOrEnd;
                return true;
            }

            private bool Close()
            {
                _containers.RemoveAt(_containers.Count - 1);
                _pos++;
                ValueDone();
                return true;
            }

            private void ValueDone()
            {
                _expect = _containers.Count == 0 ? Expect.AfterRoot : Expect.CommaOrEnd;
            }

            private bool LexString(bool isName)
            {
                return LexString(isName, false);
            }

            /// <summary>
            /// A string token, name or value, from its opening quote. A root-object member name is compared with
            /// "schema"; the string value that follows such a name is compared with the marker. Escapes are decoded to
            /// code units for that comparison only, raw scalars are validated as they come, and a raw control character
            /// is lexical (RFC 8259 section 7).
            /// </summary>
            private bool LexString(bool isName, bool markerSite)
            {
                if (isName) _target = _containers.Count == 1 && _containers[0] ? SchemaMemberName : null;
                else _target = markerSite ? CandidateMarker : null;
                _matched = 0;
                _same = true;
                _pos++;
                while (true)
                {
                    if (_pos == _bytes.Length) return Stop(ReasonEof, _bytes.Length);
                    byte unit = _bytes[_pos];
                    if (unit == (byte)'"')
                    {
                        _pos++;
                        bool equal = _target != null && _same && _matched == _target.Length;
                        if (isName)
                        {
                            _schemaValueNext = equal;
                            _expect = Expect.Colon;
                        }
                        else
                        {
                            if (equal) Candidate = true;
                            ValueDone();
                        }
                        return true;
                    }
                    if (unit == (byte)'\\')
                    {
                        if (!LexEscape()) return false;
                    }
                    else if (unit < 0x20)
                    {
                        return Stop(ReasonLexical, _pos);
                    }
                    else if (unit < 0x80)
                    {
                        FeedUnit(unit);
                        _pos++;
                    }
                    else
                    {
                        int contentLength;
                        if (!NextScalar(out contentLength)) return false;
                        // Non-ASCII never equals an ASCII literal; its code units need not be fed one by one.
                        _same = false;
                        _pos += contentLength;
                    }
                }
            }

            /// <summary>From the backslash to the last byte of the escape, leaving the position after it.</summary>
            private bool LexEscape()
            {
                _pos++;
                if (_pos == _bytes.Length) return Stop(ReasonEof, _bytes.Length);
                switch (_bytes[_pos])
                {
                    case (byte)'"': FeedUnit('"'); break;
                    case (byte)'\\': FeedUnit('\\'); break;
                    case (byte)'/': FeedUnit('/'); break;
                    case (byte)'b': FeedUnit(0x08); break;
                    case (byte)'f': FeedUnit(0x0C); break;
                    case (byte)'n': FeedUnit(0x0A); break;
                    case (byte)'r': FeedUnit(0x0D); break;
                    case (byte)'t': FeedUnit(0x09); break;
                    case (byte)'u':
                        int escapedUnit = 0;
                        for (int digit = 0; digit < 4; digit++)
                        {
                            _pos++;
                            if (_pos == _bytes.Length) return Stop(ReasonEof, _bytes.Length);
                            int value = HexValue(_bytes[_pos]);
                            if (value < 0) return Reject();
                            escapedUnit = (escapedUnit << 4) | value;
                        }
                        // One UTF-16 code unit, compared as it is: an unpaired surrogate is unequal to every ASCII
                        // literal and is no stop, because no scalar is decoded from it (H :56, :195).
                        FeedUnit(escapedUnit);
                        break;
                    default:
                        return Reject();
                }
                _pos++;
                return true;
            }

            private void FeedUnit(int codeUnit)
            {
                if (_target == null || !_same) return;
                if (_matched < _target.Length && _target[_matched] == codeUnit) _matched++;
                else _same = false;
            }

            private bool LexLiteral(string word)
            {
                for (int index = 0; index < word.Length; index++)
                {
                    if (_pos == _bytes.Length) return Stop(ReasonEof, _bytes.Length);
                    if (_bytes[_pos] != (byte)word[index]) return Reject();
                    _pos++;
                }
                ValueDone();
                return true;
            }

            /// <summary>RFC 8259 section 6, lexically: <c>-? (0 | [1-9][0-9]*) (. [0-9]+)? ([eE] [+-]? [0-9]+)?</c>. No
            /// conversion and so no magnitude limit (H :56). A byte that cannot continue a number is left for the
            /// position after the value, except where the number is not yet complete.</summary>
            private bool LexNumber()
            {
                if (_bytes[_pos] == (byte)'-') _pos++;
                if (_pos == _bytes.Length) return Stop(ReasonEof, _bytes.Length);
                if (_bytes[_pos] == (byte)'0')
                {
                    _pos++;
                    // A leading zero takes no digit after it: 01 is lexical at the second digit (H :191).
                    if (_pos < _bytes.Length && IsDigit(_bytes[_pos])) return Stop(ReasonLexical, _pos);
                }
                else if (IsDigit(_bytes[_pos]))
                {
                    SkipDigits();
                }
                else
                {
                    return Reject();
                }
                if (_pos < _bytes.Length && _bytes[_pos] == (byte)'.')
                {
                    _pos++;
                    if (_pos == _bytes.Length) return Stop(ReasonEof, _bytes.Length);
                    if (!IsDigit(_bytes[_pos])) return Reject();
                    SkipDigits();
                }
                if (_pos < _bytes.Length && (_bytes[_pos] == (byte)'e' || _bytes[_pos] == (byte)'E'))
                {
                    _pos++;
                    if (_pos < _bytes.Length && (_bytes[_pos] == (byte)'+' || _bytes[_pos] == (byte)'-')) _pos++;
                    if (_pos == _bytes.Length) return Stop(ReasonEof, _bytes.Length);
                    if (!IsDigit(_bytes[_pos])) return Reject();
                    SkipDigits();
                }
                ValueDone();
                return true;
            }

            private void SkipDigits()
            {
                while (_pos < _bytes.Length && IsDigit(_bytes[_pos])) _pos++;
            }

            private static bool IsDigit(byte value)
            {
                return value >= (byte)'0' && value <= (byte)'9';
            }

            private static bool IsWhitespace(byte value)
            {
                return value == 0x20 || value == 0x09 || value == 0x0A || value == 0x0D;
            }

            private static int HexValue(byte value)
            {
                if (value >= (byte)'0' && value <= (byte)'9') return value - '0';
                if (value >= (byte)'a' && value <= (byte)'f') return value - 'a' + 10;
                if (value >= (byte)'A' && value <= (byte)'F') return value - 'A' + 10;
                return -1;
            }
        }
    }
}
