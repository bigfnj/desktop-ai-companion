using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DesktopAICompanion
{
    /// <summary>
    /// SafeExpression v2 (grimoire/06 section 6): the sandboxed, expression-only subset of the scripts Shimeji-EE
    /// skins carry in <c>${...}</c> and <c>#{...}</c> values, over the source's fixed object model
    /// (<c>mascot.anchor.x</c>, <c>mascot.environment.cursor.x</c>, <c>mascot.environment.floor.isOn(mascot.anchor)</c>,
    /// <c>mascot.variables['k']</c>, <c>Math.random()</c> ...). No script engine, no loops, no closures and no access
    /// beyond that model. Since the 'statements' feature a program may be a sequence of statements (<c>var</c>
    /// declarations and expressions, separated by <c>;</c>) with the ES5 completion value, update and compound operators,
    /// script globals shared by one action activation's scripts, undefined, typeof, and member chains on values
    /// (companion references, points, rectangles) resolved when they run. Every static name is still resolved when the
    /// text is parsed, so an unknown one is a parse error that names itself, and a parsed program is an immutable tree
    /// any number of companions share.
    ///
    /// Semantics follow JavaScript where a Shimeji script can tell the difference: numbers are doubles, <c>+</c>
    /// concatenates when either side is a string, <c>&amp;&amp;</c> / <c>||</c> return an operand, truthiness is JS
    /// truthiness, and a division by zero is an infinity. A runtime fault (a member read on null, a wrong argument
    /// kind) yields null and is reported once through the context, never thrown at the caller.
    /// </summary>
    internal static class SafeScript
    {
        public const int MaximumLength = 4096;
        public const int MaximumTokens = 1024;
        public const int MaximumDepth = 32;

        // The three limits above bound the work of a PARSE; these two bound what EVALUATIONS can build up
        // (N-audit-1106-026). A value outlives its evaluation only in mascot.variables, so an expression evaluated every
        // tick could grow memory without limit: mascot.variables['s'] = mascot.variables['s'] + mascot.variables['s']
        // doubles a string per tick (past a gigabyte within thirty ticks), and a key built from Math.random() adds a name
        // per tick. Both are refused as a ScriptFault (the expression yields null and is reported), not truncated, so a
        // pack never runs on a value it did not compute. The source runtime has no such limits. Together they hold one
        // companion's variables to about 4 MB (256 names, each name and each string value at most 4096 UTF-16 chars).

        /// <summary>The longest string an evaluation may build: no longer than the longest expression may be.</summary>
        public const int MaximumStringLength = 4096;

        /// <summary>The most names one companion's mascot.variables may hold. Rewriting a name it holds is always allowed.</summary>
        public const int MaximumVariables = 256;

        /// <summary>Parse <paramref name="source"/>. Bare names other than the reserved ones (mascot, Math, true,
        /// false, null) must be accepted by <paramref name="scope"/> (an action's parameters and locals, or the
        /// behaviour-selection constants); a null scope accepts none.</summary>
        public static bool TryParse(string source, Func<string, bool> scope, out ScriptProgram program, out string error)
        {
            program = null;
            error = null;
            if (source == null) { error = "no expression"; return false; }
            if (source.Length > MaximumLength) { error = "expression longer than " + MaximumLength + " characters"; return false; }
            List<Token> tokens;
            if (!Lexer.TryLex(source, out tokens, out error)) return false;
            if (tokens.Count > MaximumTokens) { error = "expression has more than " + MaximumTokens + " tokens"; return false; }
            var parser = new Parser(tokens, scope ?? (n => false));
            Node root;
            try
            {
                root = parser.ParseProgram();
            }
            catch (ScriptSyntaxException ex)
            {
                error = ex.Message;
                return false;
            }
            program = new ScriptProgram(source, root, parser.RandomSites, parser.UsesVariables, parser.UsesStatements, parser.AbsentProperties,
                parser.Globals());
            return true;
        }

        /// <summary>The most script globals (<c>var</c> names and assigned bare names) one store may hold (SPEC rule 10):
        /// a new name past it is a ScriptFault, as a new mascot.variables name past <see cref="MaximumVariables"/> is.</summary>
        public const int MaximumGlobals = 64;

        internal sealed class ScriptSyntaxException : Exception
        {
            public ScriptSyntaxException(string message) : base(message) { }
        }

        // ---------------------------------------------------------------- lexer

        internal enum TokenKind { Number, String, Name, Op, End }

        internal struct Token
        {
            public TokenKind Kind;
            public string Text;
            public double Number;
            public int Offset;
        }

        private static class Lexer
        {
            private const char Backslash = (char)92;
            private const char SingleQuote = (char)39;
            private const char DoubleQuote = (char)34;

            // Longest first: "++" and "+=" before "+", so "a++" is one update operator and "1--1" is the syntax error JS
            // makes of it, not 1 - -1.
            private static readonly string[] Operators =
            {
                "===", "!==", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=",
                "<", ">", "+", "-", "*", "/", "%", "!", "?", ":", "(", ")", "[", "]", ".", ",", "=", ";",
            };

            private static bool IsHexRun(string s, int at, int width)
            {
                for (int k = 0; k < width; k++)
                    if (!Uri.IsHexDigit(s[at + k])) return false;
                return true;
            }

            public static bool TryLex(string s, out List<Token> tokens, out string error)
            {
                tokens = new List<Token>();
                error = null;
                int i = 0;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (char.IsWhiteSpace(c)) { i++; continue; }
                    int start = i;
                    if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                    {
                        if (c == '0' && i + 1 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X'))
                        {
                            i += 2;
                            int hexStart = i;
                            while (i < s.Length && Uri.IsHexDigit(s[i])) i++;
                            if (i == hexStart) { error = "malformed hex number at " + start; return false; }
                            // Unsigned, as JS reads it (0xffffffffffffffff is 18446744073709551615, never -1). Past 16
                            // digits a ulong cannot hold the value exactly, so the literal is refused by name.
                            ulong hex;
                            if (i - hexStart > 16 || !ulong.TryParse(s.Substring(hexStart, i - hexStart), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hex))
                            { error = "hex number too large at " + start; return false; }
                            tokens.Add(new Token { Kind = TokenKind.Number, Offset = start, Number = (double)hex });
                            continue;
                        }
                        // A leading zero followed by a digit is legacy octal in the source engine's non-strict JS (010 is 8,
                        // while 08 and 09 fall back to decimal). Read as decimal here, 010 was a silently different 10, so the
                        // form is refused by name, as octal string escapes are below (N-audit-1106-245). 0, 0.5 and 0e1 still
                        // read: only a DIGIT after the zero is the octal form.
                        if (c == '0' && i + 1 < s.Length && char.IsDigit(s[i + 1]))
                        { error = "legacy octal numbers are outside the supported subset (at " + start + ")"; return false; }
                        while (i < s.Length && char.IsDigit(s[i])) i++;
                        if (i < s.Length && s[i] == '.') { i++; while (i < s.Length && char.IsDigit(s[i])) i++; }
                        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                        {
                            int save = i;
                            i++;
                            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                            if (i < s.Length && char.IsDigit(s[i])) { while (i < s.Length && char.IsDigit(s[i])) i++; }
                            else i = save;
                        }
                        double value;
                        if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        { error = "malformed number at " + start; return false; }
                        tokens.Add(new Token { Kind = TokenKind.Number, Number = value, Offset = start });
                        continue;
                    }
                    if (c == SingleQuote || c == DoubleQuote)
                    {
                        char quote = c;
                        i++;
                        var sb = new StringBuilder();
                        bool closed = false;
                        while (i < s.Length)
                        {
                            char d = s[i];
                            if (d == quote) { closed = true; i++; break; }
                            if (d == Backslash)
                            {
                                if (i + 1 >= s.Length) break;
                                char e = s[i + 1];
                                i += 2;
                                switch (e)
                                {
                                    case 'n': sb.Append('\n'); break;
                                    case 't': sb.Append('\t'); break;
                                    case 'r': sb.Append('\r'); break;
                                    case 'b': sb.Append('\b'); break;
                                    case 'f': sb.Append('\f'); break;
                                    case 'v': sb.Append('\v'); break;
                                    case 'x':
                                    case 'u':
                                    {
                                        int width = e == 'x' ? 2 : 4;
                                        int code;
                                        if (i + width > s.Length || !IsHexRun(s, i, width)
                                            || !int.TryParse(s.Substring(i, width), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                                        { error = "malformed escape at " + (i - 2); return false; }
                                        sb.Append((char)code);
                                        i += width;
                                        break;
                                    }
                                    case '0':
                                        // \0 is NUL only when no digit follows; \01 and \1..\9 are legacy octal, refused.
                                        if (i < s.Length && char.IsDigit(s[i])) { error = "octal escapes are outside the supported subset (at " + (i - 2) + ")"; return false; }
                                        sb.Append('\0');
                                        break;
                                    case '1': case '2': case '3': case '4': case '5': case '6': case '7': case '8': case '9':
                                        error = "octal escapes are outside the supported subset (at " + (i - 2) + ")";
                                        return false;
                                    case '\r':
                                        // A line continuation contributes nothing; \r\n counts as one line terminator.
                                        if (i < s.Length && s[i] == '\n') i++;
                                        break;
                                    case '\n': case '\u2028': case '\u2029': break;
                                    default: sb.Append(e); break;   // \\ \' \" and any other character stand for themselves
                                }
                                continue;
                            }
                            sb.Append(d);
                            i++;
                        }
                        if (!closed) { error = "unterminated string at " + start; return false; }
                        tokens.Add(new Token { Kind = TokenKind.String, Text = sb.ToString(), Offset = start });
                        continue;
                    }
                    if (char.IsLetter(c) || c == '_' || c == '$')
                    {
                        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '$')) i++;
                        tokens.Add(new Token { Kind = TokenKind.Name, Text = s.Substring(start, i - start), Offset = start });
                        continue;
                    }
                    string op = null;
                    foreach (string candidate in Operators)
                        if (string.CompareOrdinal(s, i, candidate, 0, candidate.Length) == 0) { op = candidate; break; }
                    if (op == null) { error = "unexpected character '" + c + "' at " + i; return false; }
                    tokens.Add(new Token { Kind = TokenKind.Op, Text = op, Offset = i });
                    i += op.Length;
                }
                tokens.Add(new Token { Kind = TokenKind.End, Offset = s.Length });
                return true;
            }
        }

        // ---------------------------------------------------------------- object model

        /// <summary>The kinds of thing a member chain can name before it reaches a value.</summary>
        internal enum ObjectKind { None, Mascot, Environment, Anchor, Cursor, Area, Border, ContextBorder, Variables, Math, Manager }

        /// <summary>
        /// The public Java surface of each object the model names (SPEC rule 4): every bean property and method name a
        /// script could reach on it in the pinned source (Mascot.java, MascotEnvironment.java, Area.java, ComplexArea.java,
        /// FloorCeiling.java, Wall.java, Location.java, Manager.java) or the JDK (java.awt.Point, java.awt.Rectangle,
        /// java.lang.ref.WeakReference, java.lang.Object). Nashorn reads an ABSENT property of a Java object as undefined, so
        /// a member outside the surface reads Undefined; a member on it that the model does not run stays refused, never
        /// read as undefined. Any get/set/is accessor name counts as present whatever the class, so a gap in these lists
        /// can only refuse a script, never turn a real value into undefined.
        /// </summary>
        internal static class JavaSurface
        {
            private const string ObjectMembers = "class getClass equals hashCode toString wait notify notifyAll";

            private static readonly HashSet<string> Mascot = Set("manager anchor image lookRight bounds time behavior count totalCount "
                + "environment affordances hotspots imageSet sound paused dragging hotspotClicked cursorPosition variables "
                + "apply dispose bringToFront");
            private static readonly HashSet<string> Environment = Set("workArea activeIE activeIETitle ceiling complexScreen cursor "
                + "floor screen wall moveActiveIE minimizeActiveIE restoreIE refreshWorkArea");
            // Area and ComplexArea together: workArea, screen and activeIE are Areas, complexScreen a ComplexArea.
            private static readonly HashSet<string> Area = Set("visible left top right bottom dleft dtop dright dbottom leftBorder "
                + "topBorder rightBorder bottomBorder width height set contains toRectangle retain areas");
            // FloorCeiling, Wall and the Border interface (isOn, move) together: a contextual border is any of them.
            private static readonly HashSet<string> Border = Set("area bottom y left right DY DLeft DRight width isOn move x top DX "
                + "DTop DBottom height");
            private static readonly HashSet<string> Point = Set("x y location move translate distance distanceSq clone");
            private static readonly HashSet<string> Location = Set("x y dx dy set");
            private static readonly HashSet<string> Manager = Set("exitOnLastRemoved paused count run start stop add remove "
                + "setBehaviorAll remainOne remainNone togglePauseAll mascotWithAffordance hasOverlappingMascotsAtPoint disposeAll");
            private static readonly HashSet<string> WeakReference = Set("get clear enqueue enqueued");
            private static readonly HashSet<string> Rectangle = Set("x y width height bounds bounds2D location size minX minY maxX maxY "
                + "centerX centerY empty frame pathIterator reshape move translate resize contains inside intersects intersection "
                + "union add grow outcode createIntersection createUnion intersectsLine clone");

            private static HashSet<string> Set(string names)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (string n in (names + " " + ObjectMembers).Split(' '))
                    if (n.Length > 0) set.Add(n);
                return set;
            }

            private static bool IsAccessor(string name)
            {
                foreach (string prefix in new[] { "get", "set", "is" })
                    if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal) && char.IsUpper(name[prefix.Length]))
                        return true;
                return false;
            }

            private static HashSet<string> Of(ObjectKind kind)
            {
                switch (kind)
                {
                    case ObjectKind.Mascot: return Mascot;
                    case ObjectKind.Environment: return Environment;
                    case ObjectKind.Area: return Area;
                    case ObjectKind.Border: case ObjectKind.ContextBorder: return Border;
                    case ObjectKind.Anchor: return Point;
                    case ObjectKind.Cursor: return Location;
                    case ObjectKind.Manager: return Manager;
                    default: return null;
                }
            }

            /// <summary>Whether <paramref name="kind"/> is a Java object whose absent members read undefined. The variables map
            /// (a Map: any name reads map.get) and Math (a JS object) are not.</summary>
            public static bool HasSurface(ObjectKind kind) { return Of(kind) != null; }

            /// <summary>The member is on the static object's Java surface (present), so it is never read as undefined.</summary>
            public static bool Has(ObjectKind kind, string name) { return IsAccessor(name) || Of(kind).Contains(name); }

            /// <summary>The member is on the Java surface of the value kind (a reference, a point, a rectangle).</summary>
            public static bool Has(ScriptValueKind kind, string name)
            {
                if (IsAccessor(name)) return true;
                switch (kind)
                {
                    case ScriptValueKind.MascotRef: return Mascot.Contains(name);
                    case ScriptValueKind.WeakRef: return WeakReference.Contains(name);
                    case ScriptValueKind.Point: return Point.Contains(name);
                    case ScriptValueKind.Bounds: return Rectangle.Contains(name);
                    default: return true;
                }
            }

            /// <summary>The member is on the surface of SOME value kind a dynamic chain can hold.</summary>
            public static bool HasOnAnyValue(string name)
            {
                return IsAccessor(name) || Mascot.Contains(name) || WeakReference.Contains(name) || Point.Contains(name) || Rectangle.Contains(name);
            }
        }

        /// <summary>The members a dynamic chain runs, by value kind (SPEC rules 5-7, 9).</summary>
        private static bool IsModelledValueMember(string name)
        {
            switch (name)
            {
                case "anchor": case "lookRight": case "bounds": case "x": case "y": case "width": case "height": return true;
                default: return false;
            }
        }

        // ES5.1 keywords and future reserved words (sections 7.6.1.1-2): no statement or operator they begin is in the
        // subset, so each is refused by name rather than read as an unknown variable.
        private static readonly HashSet<string> ReservedWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "break", "case", "catch", "continue", "debugger", "default", "delete", "do", "else", "finally", "for", "function",
            "if", "in", "instanceof", "new", "return", "switch", "this", "throw", "try", "typeof", "var", "void", "while", "with",
            "class", "const", "enum", "export", "extends", "import", "super", "implements", "interface", "let", "package",
            "private", "protected", "public", "static", "yield",
        };

        // The engine's global object (ES5.1 section 15.1, B.2, and Nashorn's own): typeof of one is "object" or
        // "function" in the source, never "undefined", and no value here models them.
        private static readonly HashSet<string> EngineGlobals = new HashSet<string>((
            "arguments eval parseInt parseFloat isNaN isFinite decodeURI decodeURIComponent encodeURI encodeURIComponent escape "
            + "unescape Object Function Array String Boolean Number Date RegExp JSON Error EvalError RangeError ReferenceError "
            + "SyntaxError TypeError URIError print load loadWithNewGlobal exit quit Java JavaImporter JSAdapter Packages java javax "
            + "javafx com edu net org ArrayBuffer DataView Int8Array Uint8Array Uint8ClampedArray Int16Array Uint16Array Int32Array "
            + "Uint32Array Float32Array Float64Array context engine action").Split(' '), StringComparer.Ordinal);

        private static bool IsReservedLiteral(string name)
        {
            switch (name)
            {
                case "mascot": case "Math": case "true": case "false": case "null": case "undefined": case "NaN": case "Infinity": return true;
                default: return false;
            }
        }

        // ---------------------------------------------------------------- parser + binder

        private sealed class Parser
        {
            private readonly List<Token> _tokens;
            private readonly Func<string, bool> _scope;
            private int _pos;
            private int _depth;
            public readonly List<int> RandomSites = new List<int>();
            /// <summary>Whether the expression reads or writes mascot.variables (the 'variables' feature).</summary>
            public bool UsesVariables;
            /// <summary>Whether the program uses anything beyond the expression-only grammar (the 'statements' feature):
            /// a statement separator, var, an update or compound operator, an assignment to a name, undefined, the
            /// manager, bounds, translate or a dynamic member chain. Set at each such site.</summary>
            public bool UsesStatements;
            /// <summary>Every absent Java property the program reads, by path, in source order (SPEC rule 4).</summary>
            public readonly List<string> AbsentProperties = new List<string>();
            private readonly HashSet<string> _declared = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> _assigned = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> _typeofNames = new HashSet<string>(StringComparer.Ordinal);
            private readonly List<Token> _reads = new List<Token>();

            public Parser(List<Token> tokens, Func<string, bool> scope) { _tokens = tokens; _scope = scope; }

            public List<string> Globals()
            {
                var all = new List<string>(_declared);
                foreach (string n in _assigned) if (!_declared.Contains(n)) all.Add(n);
                all.Sort(StringComparer.Ordinal);
                return all;
            }

            /// <summary>
            /// Program = statements separated by ';' (empty ones allowed); a statement is a var declaration list or an
            /// expression (SPEC rule 1). A lone expression keeps the expression-only tree. A bare name is checked after the
            /// whole program is read, since a var or an assignment anywhere in it makes the name the program's own
            /// (hoisting): one the program neither declares nor assigns must be in the caller's scope (rule 2).
            /// </summary>
            public Node ParseProgram()
            {
                var statements = new List<Node>();
                bool separated = false;
                try
                {
                    while (Peek.Kind != TokenKind.End)
                    {
                        if (IsOp(";")) { _pos++; separated = true; continue; }
                        if (Peek.Kind == TokenKind.Name && Peek.Text == "var") statements.Add(ParseVar());
                        else statements.Add(ParseExpression());
                        if (Peek.Kind == TokenKind.End) break;
                        if (!IsOp(";")) throw new ScriptSyntaxException("unexpected '" + (Peek.Text ?? "?") + "' at " + Peek.Offset);
                    }
                }
                catch (ScriptSyntaxException)
                {
                    // An unknown name read before the syntax error is the first fault in source order ("window.close()"
                    // is refused for window, not for close).
                    CheckReads();
                    throw;
                }
                if (statements.Count == 0 && !separated) throw new ScriptSyntaxException("no expression");
                CheckReads();
                if (!separated && statements.Count == 1 && !(statements[0] is VarNode)) return statements[0];
                UsesStatements = true;
                var hoisted = new List<string>(_declared);
                return new ProgramNode(statements.ToArray(), hoisted.ToArray());
            }

            // A name is the program's to read when it declares or assigns it, when the caller's scope holds it, or when the
            // program tests it with typeof: `typeof marker == 'undefined' ? -1 : marker` reads a global another action's
            // script may have declared, and the source's own guard decides at run time (an unresolved read there is the
            // source's ReferenceError, a fault here too). typeof of an engine global is refused, so this admits none.
            private void CheckReads()
            {
                foreach (Token read in _reads)
                    if (!_declared.Contains(read.Text) && !_assigned.Contains(read.Text) && !_typeofNames.Contains(read.Text) && !_scope(read.Text))
                        throw new ScriptSyntaxException("unknown name '" + read.Text + "' at " + read.Offset);
            }

            // var a = e, b
            private Node ParseVar()
            {
                _pos++;
                UsesStatements = true;
                var names = new List<string>();
                var inits = new List<Node>();
                while (true)
                {
                    Token name = Next();
                    if (name.Kind != TokenKind.Name) throw new ScriptSyntaxException("expected a variable name at " + name.Offset);
                    CheckAssignable(name.Text, name.Offset);
                    _declared.Add(name.Text);
                    Node init = null;
                    if (IsOp("=")) { _pos++; init = ParseExpression(); }
                    names.Add(name.Text);
                    inits.Add(init);
                    if (IsOp(",")) { _pos++; continue; }
                    break;
                }
                return new VarNode(names.ToArray(), inits.ToArray());
            }

            /// <summary>A name a script may declare or assign: not a keyword, and not one of the names the model binds
            /// statically (assigning mascot would shadow the companion every later chain is bound to).</summary>
            private static void CheckAssignable(string name, int offset)
            {
                if (ReservedWords.Contains(name)) throw new ScriptSyntaxException("'" + name + "' is outside the supported subset (at " + offset + ")");
                switch (name)
                {
                    case "mascot": case "Math": case "true": case "false": case "null": case "undefined": case "NaN": case "Infinity":
                        throw new ScriptSyntaxException("'" + name + "' cannot be assigned (at " + offset + ")");
                }
            }

            private Token Peek { get { return _tokens[_pos]; } }
            private bool IsOp(string op) { return Peek.Kind == TokenKind.Op && Peek.Text == op; }
            private Token Next() { return _tokens[_pos++]; }

            private void Expect(string op)
            {
                if (!IsOp(op)) throw new ScriptSyntaxException("expected '" + op + "' at " + Peek.Offset);
                _pos++;
            }

            public void ExpectEnd()
            {
                if (Peek.Kind != TokenKind.End) throw new ScriptSyntaxException("unexpected '" + (Peek.Text ?? "?") + "' at " + Peek.Offset);
            }

            private void Enter()
            {
                if (++_depth > MaximumDepth) throw new ScriptSyntaxException("expression nested deeper than " + MaximumDepth);
            }

            private void Leave() { _depth--; }

            // precedence climbing: 0 = op= 1 ?: 2 || 3 && 4 == != === !== 5 < <= > >= 6 + - 7 * / % 8 unary 9 postfix
            public Node ParseExpression()
            {
                Enter();
                try
                {
                    Node test = ParseBinary(2);
                    if (IsOp("?"))
                    {
                        _pos++;
                        Node yes = ParseExpression();
                        Expect(":");
                        Node no = ParseExpression();
                        return new ConditionalNode(test, yes, no);
                    }
                    if (Peek.Kind == TokenKind.Op && IsAssignmentOperator(Peek.Text))
                    {
                        Token op = Next();
                        IReference target = Reference(test, op.Offset);
                        Node value = ParseExpression();
                        // mascot.variables[k] = v is the expression-only form; every other assignment is a statement-grammar one.
                        if (op.Text == "=" && target is VariableIndexNode) return new VariablePutNode(((VariableIndexNode)target).Key, value, false);
                        UsesStatements = true;
                        return new AssignNode(target, op.Text == "=" ? null : op.Text.Substring(0, 1), value);
                    }
                    return test;
                }
                finally { Leave(); }
            }

            private static bool IsAssignmentOperator(string op)
            {
                return op == "=" || op == "+=" || op == "-=" || op == "*=" || op == "/=" || op == "%=";
            }

            /// <summary>An assignment or update target (SPEC rule 3): mascot.variables[k] (or .get(k), as Nashorn's map
            /// linkage reads it), or a bare name, which then names a script global.</summary>
            private IReference Reference(Node node, int offset)
            {
                var index = node as VariableIndexNode;
                // .get(k) is a call, which JS cannot assign to (Nashorn refuses it when it compiles the script).
                if (index != null && !index.IsGetCall) return index;
                var name = node as NameNode;
                if (name != null)
                {
                    CheckAssignable(name.Name, offset);
                    _assigned.Add(name.Name);
                    UsesStatements = true;
                    return name;
                }
                throw new ScriptSyntaxException("only mascot.variables['name'] or a script name can be assigned (at " + offset + ")");
            }

            private static int Precedence(string op)
            {
                switch (op)
                {
                    case "||": return 2;
                    case "&&": return 3;
                    case "==": case "!=": case "===": case "!==": return 4;
                    case "<": case "<=": case ">": case ">=": return 5;
                    case "+": case "-": return 6;
                    case "*": case "/": case "%": return 7;
                    default: return -1;
                }
            }

            private Node ParseBinary(int minPrecedence)
            {
                Node left = ParseUnary();
                while (Peek.Kind == TokenKind.Op)
                {
                    string op = Peek.Text;
                    int p = Precedence(op);
                    if (p < minPrecedence || p < 0) break;
                    _pos++;
                    Enter();
                    Node right;
                    try { right = ParseBinary(p + 1); }
                    finally { Leave(); }
                    left = op == "&&" || op == "||" ? (Node)new LogicalNode(op == "&&", left, right) : new BinaryNode(op, left, right);
                }
                return left;
            }

            private Node ParseUnary()
            {
                if (IsOp("!")) { _pos++; Enter(); try { return new NotNode(ParseUnary()); } finally { Leave(); } }
                if (IsOp("-")) { _pos++; Enter(); try { return new NegateNode(ParseUnary()); } finally { Leave(); } }
                if (IsOp("+")) { _pos++; Enter(); try { return new PlusNode(ParseUnary()); } finally { Leave(); } }
                if (Peek.Kind == TokenKind.Name && Peek.Text == "typeof")
                {
                    // ES5 11.4.3. A bare name under typeof that nothing resolves reads "undefined" with no error, so it is
                    // not checked against the scope; a name of the engine's own global object would be "object" or
                    // "function" there, which nothing here models, so it is refused.
                    Token op = Next();
                    UsesStatements = true;
                    Enter();
                    try
                    {
                        if (Peek.Kind == TokenKind.Name && !IsReservedLiteral(Peek.Text) && !ReservedWords.Contains(Peek.Text)
                            && !(_pos + 1 < _tokens.Count && _tokens[_pos + 1].Kind == TokenKind.Op
                                 && (_tokens[_pos + 1].Text == "." || _tokens[_pos + 1].Text == "(" || _tokens[_pos + 1].Text == "[")))
                        {
                            Token name = Next();
                            if (EngineGlobals.Contains(name.Text))
                                throw new ScriptSyntaxException("typeof '" + name.Text + "' names the engine's global object, which is outside the supported subset (at " + name.Offset + ")");
                            if (IsOp("++") || IsOp("--")) throw new ScriptSyntaxException("an update under typeof is outside the supported subset (at " + Peek.Offset + ")");
                            _typeofNames.Add(name.Text);
                            return new TypeofNode(new NameNode(name.Text), true);
                        }
                        return new TypeofNode(ParseUnary(), false);
                    }
                    finally { Leave(); }
                }
                if (IsOp("++") || IsOp("--"))
                {
                    Token op = Next();
                    Enter();
                    try
                    {
                        IReference target = Reference(ParseUnary(), op.Offset);
                        UsesStatements = true;
                        return new UpdateNode(target, op.Text == "++" ? 1 : -1, true);
                    }
                    finally { Leave(); }
                }
                return ParsePostfix();
            }

            /// <summary>A primary, then any dynamic member chain on its value (SPEC rule 9), then a postfix update.</summary>
            private Node ParsePostfix()
            {
                Node node = ParsePrimary();
                while (true)
                {
                    if (IsOp("."))
                    {
                        Token dot = Next();
                        Token member = Next();
                        if (member.Kind != TokenKind.Name) throw new ScriptSyntaxException("expected a member name at " + member.Offset);
                        UsesStatements = true;
                        if (IsOp("("))
                        {
                            List<Node> args = ParseArguments();
                            bool modelled = (member.Text == "get" && args.Count == 0) || (member.Text == "translate" && args.Count == 2);
                            if (!modelled) throw new ScriptSyntaxException("'." + member.Text + "(...)' with " + args.Count + " argument(s) is not part of the object model (at " + dot.Offset + ")");
                            node = new DynamicCallNode(node, member.Text, args.ToArray(), dot.Offset);
                            continue;
                        }
                        if (!IsModelledValueMember(member.Text))
                        {
                            if (JavaSurface.HasOnAnyValue(member.Text))
                                throw new ScriptSyntaxException("'." + member.Text + "' is not part of the object model (at " + dot.Offset + ")");
                            AbsentProperties.Add("." + member.Text);
                        }
                        node = new DynamicMemberNode(node, member.Text, dot.Offset);
                        continue;
                    }
                    if (IsOp("[")) throw new ScriptSyntaxException("only mascot.variables can be indexed (at " + Peek.Offset + ")");
                    break;
                }
                if (IsOp("++") || IsOp("--"))
                {
                    Token op = Next();
                    IReference target = Reference(node, op.Offset);
                    UsesStatements = true;
                    return new UpdateNode(target, op.Text == "++" ? 1 : -1, false);
                }
                return node;
            }

            // A member chain is bound as it is read: each step must name something the object model has.
            private Node ParsePrimary()
            {
                Token t = Next();
                if (t.Kind == TokenKind.Number) return new LiteralNode(ScriptValue.FromNumber(t.Number));
                if (t.Kind == TokenKind.String) return new LiteralNode(ScriptValue.FromString(t.Text));
                if (t.Kind == TokenKind.Op && t.Text == "(")
                {
                    Node inner = ParseExpression();
                    Expect(")");
                    return inner;
                }
                if (t.Kind != TokenKind.Name) throw new ScriptSyntaxException("unexpected '" + (t.Text ?? "end") + "' at " + t.Offset);

                switch (t.Text)
                {
                    case "true": return new LiteralNode(ScriptValue.True);
                    case "false": return new LiteralNode(ScriptValue.False);
                    // undefined is its own kind (SPEC rule 4): not null (undefined === null is false, undefined + 1 is NaN).
                    case "undefined": UsesStatements = true; return new LiteralNode(ScriptValue.Undefined);
                    case "null": return new LiteralNode(ScriptValue.Null);
                    case "NaN": return new LiteralNode(ScriptValue.FromNumber(double.NaN));
                    case "Infinity": return new LiteralNode(ScriptValue.FromNumber(double.PositiveInfinity));
                    case "mascot": return ParseChain(ObjectKind.Mascot, ScriptBorderId.None, ScriptAreaId.None, "mascot", t.Offset);
                    case "Math": return ParseMath(t.Offset);
                }
                if (ReservedWords.Contains(t.Text)) throw new ScriptSyntaxException("'" + t.Text + "' is outside the supported subset (at " + t.Offset + ")");
                // Checked against the scope once the whole program is read (ParseProgram): a var or an assignment later in
                // the program makes the name its own.
                _reads.Add(t);
                return new NameNode(t.Text);
            }

            private string MemberName()
            {
                if (IsOp("."))
                {
                    _pos++;
                    Token name = Next();
                    if (name.Kind != TokenKind.Name) throw new ScriptSyntaxException("expected a member name at " + name.Offset);
                    return name.Text;
                }
                return null;
            }

            private List<Node> ParseArguments()
            {
                Expect("(");
                var args = new List<Node>();
                if (!IsOp(")"))
                {
                    while (true)
                    {
                        args.Add(ParseExpression());
                        if (IsOp(",")) { _pos++; continue; }
                        break;
                    }
                }
                Expect(")");
                return args;
            }

            private Node ParseMath(int offset)
            {
                string fn = MemberName();
                if (fn == null) throw new ScriptSyntaxException("Math needs a member at " + offset);
                if (fn == "PI") return new LiteralNode(ScriptValue.FromNumber(Math.PI));
                if (fn == "E") return new LiteralNode(ScriptValue.FromNumber(Math.E));
                if (!IsOp("(")) throw new ScriptSyntaxException("Math." + fn + " is not a value (at " + offset + ")");
                List<Node> args = ParseArguments();
                if (fn == "random")
                {
                    if (args.Count != 0) throw new ScriptSyntaxException("Math.random takes no arguments (at " + offset + ")");
                    RandomSites.Add(offset);
                    return new RandomNode(offset);
                }
                MathFunction f;
                if (!MathNode.TryResolve(fn, args.Count, out f))
                    throw new ScriptSyntaxException("Math." + fn + " with " + args.Count + " argument(s) is not supported (at " + offset + ")");
                return new MathNode(f, args.ToArray());
            }

            private Node ParseChain(ObjectKind kind, ScriptBorderId border, ScriptAreaId area, string path, int offset)
            {
                while (true)
                {
                    if (IsOp("["))
                    {
                        if (kind != ObjectKind.Variables) throw new ScriptSyntaxException("'" + path + "' cannot be indexed (at " + Peek.Offset + ")");
                        int at = Peek.Offset;
                        _pos++;
                        Node key = ParseExpression();
                        Expect("]");
                        // Nashorn links an index whose name is one of VariableMap's own bean properties to the getter, not
                        // to the map (measured: ['empty'] reads isEmpty()), which nothing here models.
                        var literal = key as LiteralNode;
                        if (literal != null && literal.Value.Kind == ScriptValueKind.String
                            && (literal.Value.Text == "empty" || literal.Value.Text == "class" || literal.Value.Text == "rawMap"))
                            throw new ScriptSyntaxException("mascot.variables['" + literal.Value.Text + "'] reads the map's own property in the source, which is outside the supported subset (at " + at + ")");
                        return new VariableIndexNode(key);
                    }
                    string member = MemberName();
                    if (member == null)
                    {
                        switch (kind)
                        {
                            case ObjectKind.Anchor: return new PointNode(PointSource.Anchor);
                            case ObjectKind.Cursor: return new PointNode(PointSource.Cursor);
                            default: throw new ScriptSyntaxException("'" + path + "' is an object, not a value (at " + offset + ")");
                        }
                    }
                    string next = path + "." + member;
                    switch (kind)
                    {
                        case ObjectKind.Mascot:
                            switch (member)
                            {
                                case "anchor": kind = ObjectKind.Anchor; path = next; continue;
                                case "environment": kind = ObjectKind.Environment; path = next; continue;
                                case "variables": kind = ObjectKind.Variables; path = next; UsesVariables = true; continue;
                                // getManager(): only its getMascotWithAffordance (SPEC rule 5).
                                case "manager": kind = ObjectKind.Manager; path = next; UsesStatements = true; continue;
                                // getBounds(): a rectangle value whose members a dynamic chain reads (SPEC rule 7).
                                case "bounds": UsesStatements = true; return new BoundsNode();
                                case "lookRight": return new MascotValueNode(MascotValue.LookRight);
                                case "dragging": return new MascotValueNode(MascotValue.Dragging);
                                case "count": return new MascotValueNode(MascotValue.Count);
                                case "totalCount": return new MascotValueNode(MascotValue.TotalCount);
                                case "time": return new MascotValueNode(MascotValue.Time);
                            }
                            break;
                        case ObjectKind.Environment:
                            switch (member)
                            {
                                case "cursor": kind = ObjectKind.Cursor; path = next; continue;
                                case "workArea": kind = ObjectKind.Area; area = ScriptAreaId.WorkArea; path = next; continue;
                                case "screen": kind = ObjectKind.Area; area = ScriptAreaId.Screen; path = next; continue;
                                case "complexScreen": kind = ObjectKind.Area; area = ScriptAreaId.ComplexScreen; path = next; continue;
                                case "activeIE": kind = ObjectKind.Area; area = ScriptAreaId.ActiveWindow; path = next; continue;
                                case "floor": kind = ObjectKind.ContextBorder; border = ScriptBorderId.Floor; path = next; continue;
                                case "ceiling": kind = ObjectKind.ContextBorder; border = ScriptBorderId.Ceiling; path = next; continue;
                                case "wall": kind = ObjectKind.ContextBorder; border = ScriptBorderId.Wall; path = next; continue;
                            }
                            break;
                        case ObjectKind.Anchor:
                            if (member == "x") return new PointMemberNode(PointSource.Anchor, PointMember.X);
                            if (member == "y") return new PointMemberNode(PointSource.Anchor, PointMember.Y);
                            if (member == "translate" && IsOp("("))
                            {
                                // java.awt.Point.translate on the companion's live anchor (SPEC rule 8).
                                List<Node> args = ParseArguments();
                                if (args.Count != 2) throw new ScriptSyntaxException(next + " takes (dx, dy) (at " + offset + ")");
                                UsesStatements = true;
                                return new TranslateNode(args[0], args[1]);
                            }
                            break;
                        case ObjectKind.Manager:
                            if (member == "getMascotWithAffordance" && IsOp("("))
                            {
                                List<Node> args = ParseArguments();
                                if (args.Count != 1) throw new ScriptSyntaxException(next + " takes one affordance (at " + offset + ")");
                                return new AffordanceNode(args[0]);
                            }
                            break;
                        case ObjectKind.Cursor:
                            switch (member)
                            {
                                case "x": return new PointMemberNode(PointSource.Cursor, PointMember.X);
                                case "y": return new PointMemberNode(PointSource.Cursor, PointMember.Y);
                                case "dx": return new PointMemberNode(PointSource.Cursor, PointMember.DX);
                                case "dy": return new PointMemberNode(PointSource.Cursor, PointMember.DY);
                            }
                            break;
                        case ObjectKind.Area:
                            switch (member)
                            {
                                case "leftBorder": kind = ObjectKind.Border; border = BorderOf(area, 0); path = next; continue;
                                case "topBorder": kind = ObjectKind.Border; border = BorderOf(area, 1); path = next; continue;
                                case "rightBorder": kind = ObjectKind.Border; border = BorderOf(area, 2); path = next; continue;
                                case "bottomBorder": kind = ObjectKind.Border; border = BorderOf(area, 3); path = next; continue;
                                case "contains":
                                {
                                    List<Node> args = ParseArguments();
                                    if (args.Count != 2) throw new ScriptSyntaxException(next + " takes (x, y) (at " + offset + ")");
                                    return new AreaContainsNode(area, args[0], args[1]);
                                }
                            }
                            AreaMember am;
                            if (AreaValueNode.TryResolve(member, out am)) return new AreaValueNode(area, am);
                            break;
                        case ObjectKind.Border:
                        case ObjectKind.ContextBorder:
                            if (member == "isOn")
                            {
                                List<Node> args = ParseArguments();
                                if (args.Count != 1) throw new ScriptSyntaxException(next + " takes one point (at " + offset + ")");
                                return new BorderIsOnNode(border, args[0]);
                            }
                            BorderMember bm;
                            if (BorderValueNode.TryResolve(member, out bm)) return new BorderValueNode(border, bm);
                            break;
                        case ObjectKind.Variables:
                            if (member == "get" || member == "put")
                            {
                                List<Node> args = ParseArguments();
                                if (member == "get" && args.Count == 1) return new VariableIndexNode(args[0], true);
                                if (member == "put" && args.Count == 2) return new VariablePutNode(args[0], args[1], true);
                                throw new ScriptSyntaxException(next + " has the wrong number of arguments (at " + offset + ")");
                            }
                            break;
                    }
                    // Nashorn reads a property a Java object does not have as undefined (SPEC rule 4); a method call on one
                    // (absent or unmodelled) and a property the object has but the model does not run stay refused.
                    // A member of that undefined is the source's TypeError at every evaluation, so a chain that goes on past
                    // an absent property is refused here rather than faulting each time it runs.
                    if (!IsOp("(") && !IsOp(".") && !IsOp("[") && JavaSurface.HasSurface(kind) && !JavaSurface.Has(kind, member))
                    {
                        UsesStatements = true;
                        AbsentProperties.Add(next);
                        return new LiteralNode(ScriptValue.Undefined);
                    }
                    throw new ScriptSyntaxException("'" + next + "' is not part of the object model (at " + offset + ")");
                }
            }

            // side: 0 left, 1 top, 2 right, 3 bottom
            private static ScriptBorderId BorderOf(ScriptAreaId area, int side)
            {
                return (ScriptBorderId)((int)ScriptBorderId.WorkAreaLeft + ((int)area - (int)ScriptAreaId.WorkArea) * 4 + side);
            }
        }

        // ---------------------------------------------------------------- tree

        internal abstract class Node
        {
            public abstract ScriptValue Eval(ScriptRun run);
        }

        /// <summary>One evaluation: the context plus the first fault, so a faulting tree reports once and yields null.</summary>
        internal sealed class ScriptRun
        {
            public readonly IScriptContext Context;
            public ScriptRun(IScriptContext context) { Context = context; }

            /// <summary>A runtime fault ABORTS the expression, as the source's compiled eval throws: nothing after it
            /// runs, so no later random draw or variable write happens. The caller reports it and gets null.</summary>
            public ScriptValue Fail(string message) { throw new ScriptFault(message); }
        }

        internal sealed class ScriptFault : Exception
        {
            public ScriptFault(string message) : base(message) { }
        }

        private sealed class LiteralNode : Node
        {
            public readonly ScriptValue Value;
            public LiteralNode(ScriptValue value) { Value = value; }
            public override ScriptValue Eval(ScriptRun run) { return Value; }
        }

        /// <summary>An assignment or update target: a prepared reference (its key evaluated once, as JS evaluates a
        /// reference before the right-hand side), then a read and a write through it.</summary>
        internal interface IReference
        {
            string Prepare(ScriptRun run);
            ScriptValue Get(ScriptRun run, string key);
            void Put(ScriptRun run, string key, ScriptValue value);
        }

        /// <summary>
        /// A bare name (SPEC rule 2): a script global first (Nashorn's per-Bindings global object), then the action's
        /// bindings (its parameters and locals, or the selection constants). Writing one always writes the global, so a
        /// parameter assigned or updated becomes a global that shadows it for the rest of the store's life.
        /// </summary>
        private sealed class NameNode : Node, IReference
        {
            public readonly string Name;
            public NameNode(string name) { Name = name; }

            public bool TryResolve(ScriptRun run, out ScriptValue value)
            {
                if (run.Context.TryGetGlobal(Name, out value)) return true;
                return run.Context.TryGetName(Name, out value);
            }

            public override ScriptValue Eval(ScriptRun run)
            {
                ScriptValue v;
                return TryResolve(run, out v) ? v : run.Fail("'" + Name + "' has no value here");
            }

            public string Prepare(ScriptRun run) { return Name; }
            public ScriptValue Get(ScriptRun run, string key) { return Eval(run); }
            public void Put(ScriptRun run, string key, ScriptValue value) { SetGlobal(run, Name, value); }

            internal static void SetGlobal(ScriptRun run, string name, ScriptValue value)
            {
                ScriptValue held;
                if (!run.Context.TryGetGlobal(name, out held) && run.Context.GlobalCount >= MaximumGlobals)
                    run.Fail("the script already holds " + MaximumGlobals + " globals; a new name is refused");
                run.Context.SetGlobal(name, value);
            }
        }

        /// <summary>
        /// A program of statements (SPEC rule 1). Declared names are hoisted first: one the store does not hold yet is
        /// created as undefined, so an initializer that reads its own name reads undefined, and a name the store already
        /// holds keeps its value (a var is not a reset). The value is the last value-producing statement's, else undefined.
        /// </summary>
        private sealed class ProgramNode : Node
        {
            private readonly Node[] _statements;
            private readonly string[] _hoisted;
            public ProgramNode(Node[] statements, string[] hoisted) { _statements = statements; _hoisted = hoisted; }
            public override ScriptValue Eval(ScriptRun run)
            {
                foreach (string name in _hoisted)
                {
                    ScriptValue held;
                    if (!run.Context.TryGetGlobal(name, out held)) NameNode.SetGlobal(run, name, ScriptValue.Undefined);
                }
                ScriptValue completion = ScriptValue.Undefined;
                foreach (Node statement in _statements)
                {
                    ScriptValue v = statement.Eval(run);
                    if (!(statement is VarNode)) completion = v;
                }
                return completion;
            }
        }

        /// <summary>var a = e, b: each initializer in order, written to the global it names; no completion value.</summary>
        private sealed class VarNode : Node
        {
            private readonly string[] _names;
            private readonly Node[] _inits;
            public VarNode(string[] names, Node[] inits) { _names = names; _inits = inits; }
            public override ScriptValue Eval(ScriptRun run)
            {
                for (int i = 0; i < _names.Length; i++)
                    if (_inits[i] != null) NameNode.SetGlobal(run, _names[i], _inits[i].Eval(run));
                return ScriptValue.Undefined;
            }
        }

        /// <summary>= and op= on a reference (SPEC rule 3): the reference first, then (op=) its old value, then the
        /// right-hand side, then the write; the value is the value written.</summary>
        private sealed class AssignNode : Node
        {
            private readonly IReference _target;
            private readonly string _op;   // null for =, else one of + - * / %
            private readonly Node _value;
            public AssignNode(IReference target, string op, Node value) { _target = target; _op = op; _value = value; }
            public override ScriptValue Eval(ScriptRun run)
            {
                string key = _target.Prepare(run);
                ScriptValue result;
                if (_op == null) result = _value.Eval(run);
                else
                {
                    ScriptValue old = _target.Get(run, key);
                    result = BinaryNode.Apply(run, _op, old, _value.Eval(run));
                }
                _target.Put(run, key, result);
                return result;
            }
        }

        /// <summary>Prefix and postfix ++ / --: ToNumber of the old value plus or minus one is written; a postfix yields
        /// ToNumber(old), a prefix the new value. null++ writes 1; undefined++ writes NaN.</summary>
        private sealed class UpdateNode : Node
        {
            private readonly IReference _target;
            private readonly int _delta;
            private readonly bool _prefix;
            public UpdateNode(IReference target, int delta, bool prefix) { _target = target; _delta = delta; _prefix = prefix; }
            public override ScriptValue Eval(ScriptRun run)
            {
                string key = _target.Prepare(run);
                double old = _target.Get(run, key).ToNumber();
                ScriptValue next = ScriptValue.FromNumber(old + _delta);
                _target.Put(run, key, next);
                return _prefix ? next : ScriptValue.FromNumber(old);
            }
        }

        /// <summary>typeof (ES5 11.4.3): "undefined", "boolean", "number", "string", and "object" for null and for every
        /// Java object. Under typeof a bare name nothing resolves is "undefined", not an error.</summary>
        private sealed class TypeofNode : Node
        {
            private readonly Node _operand;
            private readonly bool _bareName;
            public TypeofNode(Node operand, bool bareName) { _operand = operand; _bareName = bareName; }
            public override ScriptValue Eval(ScriptRun run)
            {
                ScriptValue v;
                if (_bareName) { if (!((NameNode)_operand).TryResolve(run, out v)) v = ScriptValue.Undefined; }
                else v = _operand.Eval(run);
                switch (v.Kind)
                {
                    case ScriptValueKind.Undefined: return ScriptValue.FromString("undefined");
                    case ScriptValueKind.Boolean: return ScriptValue.FromString("boolean");
                    case ScriptValueKind.Number: return ScriptValue.FromString("number");
                    case ScriptValueKind.String: return ScriptValue.FromString("string");
                    default: return ScriptValue.FromString("object");
                }
            }
        }

        /// <summary>mascot.getBounds(): the companion's bounds as a rectangle value (SPEC rule 7).</summary>
        private sealed class BoundsNode : Node
        {
            public override ScriptValue Eval(ScriptRun run) { return ScriptValue.FromBounds(run.Context.GetMascotBounds(run.Context.Self)); }
        }

        /// <summary>mascot.anchor.translate(dx, dy) (SPEC rule 8): both arguments, then the move on the live anchor,
        /// each converted as Nashorn passes a number to a Java int (ToInt32); the call yields undefined.</summary>
        private sealed class TranslateNode : Node
        {
            private readonly Node _dx, _dy;
            public TranslateNode(Node dx, Node dy) { _dx = dx; _dy = dy; }
            public override ScriptValue Eval(ScriptRun run)
            {
                int dx = ToInt32(_dx.Eval(run).ToNumber()), dy = ToInt32(_dy.Eval(run).ToNumber());
                run.Context.TranslateAnchor(dx, dy);
                return ScriptValue.Undefined;
            }
        }

        /// <summary>
        /// A JS number as Nashorn passes it to a Java int parameter, measured on the pinned JDK 8 (P4 Nashorn pre-review,
        /// 2026-11-24): NaN and the infinities are 0; a finite value is truncated to a Java long (saturating) and its low
        /// 32 bits taken. Inside the long range that is ES5 ToInt32 (1.9 is 1, 2^32 + 1 is 1); beyond it the saturation
        /// shows (1e40 is -1, where JS's 1e40 | 0 is 0).
        /// </summary>
        internal static int ToInt32(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return 0;
            long l;
            if (d >= 9223372036854775807.0) l = long.MaxValue;
            else if (d <= -9223372036854775808.0) l = long.MinValue;
            else l = (long)d;
            return unchecked((int)l);
        }

        /// <summary>Manager.getMascotWithAffordance(ToString(e)) (SPEC rule 5): a weak reference to the first companion in
        /// manager order whose offers contain it, the asking one included, or null. A null argument matches nothing.</summary>
        private sealed class AffordanceNode : Node
        {
            private readonly Node _affordance;
            public AffordanceNode(Node affordance) { _affordance = affordance; }
            public override ScriptValue Eval(ScriptRun run)
            {
                ScriptValue a = _affordance.Eval(run);
                if (a.Kind == ScriptValueKind.Null) return ScriptValue.Null;
                object found = run.Context.FindMascotWithAffordance(a.ToDisplayString());
                return found == null ? ScriptValue.Null : ScriptValue.FromWeakRef(found);
            }
        }

        private static string KindName(ScriptValueKind kind)
        {
            switch (kind)
            {
                case ScriptValueKind.MascotRef: return "a mascot reference";
                case ScriptValueKind.WeakRef: return "a weak reference";
                case ScriptValueKind.Point: return "a point";
                case ScriptValueKind.Bounds: return "a rectangle";
                case ScriptValueKind.Number: return "a number";
                case ScriptValueKind.String: return "a string";
                case ScriptValueKind.Boolean: return "a boolean";
                case ScriptValueKind.Undefined: return "undefined";
                default: return "null";
            }
        }

        /// <summary>A member read resolved by the value's kind at run time (SPEC rules 6, 7, 9). A read on null or
        /// undefined is the source's TypeError and aborts the evaluation; an absent property of a Java object reads
        /// undefined; a present one the model does not run, or any member of a primitive, faults.</summary>
        private sealed class DynamicMemberNode : Node
        {
            private readonly Node _target;
            private readonly string _member;
            private readonly int _offset;
            public DynamicMemberNode(Node target, string member, int offset) { _target = target; _member = member; _offset = offset; }
            public override ScriptValue Eval(ScriptRun run)
            {
                ScriptValue t = _target.Eval(run);
                IScriptContext c = run.Context;
                switch (t.Kind)
                {
                    case ScriptValueKind.Null:
                    case ScriptValueKind.Undefined:
                        return run.Fail("TypeError: cannot read '" + _member + "' of " + KindName(t.Kind) + " (at " + _offset + ")");
                    case ScriptValueKind.MascotRef:
                        switch (_member)
                        {
                            case "anchor": { double x, y; c.GetMascotAnchor(t.Ref, out x, out y); return ScriptValue.FromPoint(x, y); }
                            case "lookRight": return ScriptValue.FromBool(c.GetMascotLookRight(t.Ref));
                            case "bounds": return ScriptValue.FromBounds(c.GetMascotBounds(t.Ref));
                        }
                        break;
                    case ScriptValueKind.Point:
                        if (_member == "x") return ScriptValue.FromNumber(t.X);
                        if (_member == "y") return ScriptValue.FromNumber(t.Y);
                        break;
                    case ScriptValueKind.Bounds:
                    {
                        var r = (ScriptRect)t.Ref;
                        switch (_member)
                        {
                            case "x": return ScriptValue.FromNumber(r.X);
                            case "y": return ScriptValue.FromNumber(r.Y);
                            case "width": return ScriptValue.FromNumber(r.Width);
                            case "height": return ScriptValue.FromNumber(r.Height);
                        }
                        break;
                    }
                    case ScriptValueKind.WeakRef:
                        break;
                    default:
                        return run.Fail("'." + _member + "' of " + KindName(t.Kind) + " is not part of the object model (at " + _offset + ")");
                }
                if (JavaSurface.Has(t.Kind, _member))
                    return run.Fail("'." + _member + "' of " + KindName(t.Kind) + " is not part of the object model (at " + _offset + ")");
                return ScriptValue.Undefined;
            }
        }

        /// <summary>A method call resolved by the value's kind at run time: WeakReference.get() (a disposed companion reads
        /// null, the GC-dependent approximation of SPEC rule 5). The target is read first and a null or undefined one
        /// faults before the arguments run, as JS's TypeError does; any other call runs its arguments, then faults.</summary>
        private sealed class DynamicCallNode : Node
        {
            private readonly Node _target;
            private readonly string _member;
            private readonly Node[] _args;
            private readonly int _offset;
            public DynamicCallNode(Node target, string member, Node[] args, int offset) { _target = target; _member = member; _args = args; _offset = offset; }
            public override ScriptValue Eval(ScriptRun run)
            {
                ScriptValue t = _target.Eval(run);
                if (t.Kind == ScriptValueKind.Null || t.Kind == ScriptValueKind.Undefined)
                    return run.Fail("TypeError: cannot call '" + _member + "' of " + KindName(t.Kind) + " (at " + _offset + ")");
                foreach (Node a in _args) a.Eval(run);
                if (t.Kind == ScriptValueKind.WeakRef && _member == "get")
                {
                    object m = ((ScriptWeakRef)t.Ref).Target;
                    return run.Context.IsReferenceLive(m) ? ScriptValue.FromMascot(m) : ScriptValue.Null;
                }
                if (t.Kind == ScriptValueKind.Point && _member == "translate")
                    return run.Fail("translate on a copied point (not the companion's own anchor) is not modelled (at " + _offset + ")");
                return run.Fail("'." + _member + "()' of " + KindName(t.Kind) + " is not part of the object model (at " + _offset + ")");
            }
        }

        private sealed class ConditionalNode : Node
        {
            private readonly Node _test, _yes, _no;
            public ConditionalNode(Node test, Node yes, Node no) { _test = test; _yes = yes; _no = no; }
            public override ScriptValue Eval(ScriptRun run) { return _test.Eval(run).IsTruthy ? _yes.Eval(run) : _no.Eval(run); }
        }

        private sealed class LogicalNode : Node
        {
            private readonly bool _and;
            private readonly Node _left, _right;
            public LogicalNode(bool and, Node left, Node right) { _and = and; _left = left; _right = right; }
            public override ScriptValue Eval(ScriptRun run)
            {
                ScriptValue l = _left.Eval(run);
                if (_and) return l.IsTruthy ? _right.Eval(run) : l;
                return l.IsTruthy ? l : _right.Eval(run);
            }
        }

        private sealed class NotNode : Node
        {
            private readonly Node _operand;
            public NotNode(Node operand) { _operand = operand; }
            public override ScriptValue Eval(ScriptRun run) { return ScriptValue.FromBool(!_operand.Eval(run).IsTruthy); }
        }

        private sealed class NegateNode : Node
        {
            private readonly Node _operand;
            public NegateNode(Node operand) { _operand = operand; }
            public override ScriptValue Eval(ScriptRun run) { return ScriptValue.FromNumber(-_operand.Eval(run).ToNumber()); }
        }

        private sealed class PlusNode : Node
        {
            private readonly Node _operand;
            public PlusNode(Node operand) { _operand = operand; }
            public override ScriptValue Eval(ScriptRun run) { return ScriptValue.FromNumber(_operand.Eval(run).ToNumber()); }
        }

        private sealed class BinaryNode : Node
        {
            private readonly string _op;
            private readonly Node _left, _right;
            public BinaryNode(string op, Node left, Node right) { _op = op; _left = left; _right = right; }

            public override ScriptValue Eval(ScriptRun run)
            {
                ScriptValue l = _left.Eval(run), r = _right.Eval(run);
                return Apply(run, _op, l, r);
            }

            /// <summary>The operator on two evaluated operands; shared with compound assignment (a op= b).</summary>
            internal static ScriptValue Apply(ScriptRun run, string op, ScriptValue l, ScriptValue r)
            {
                switch (op)
                {
                    case "+":
                        // A Java object's ToPrimitive is its toString in Nashorn, so a reference or a rectangle concatenates.
                        if (l.Kind == ScriptValueKind.String || r.Kind == ScriptValueKind.String || l.IsJavaObject || r.IsJavaObject)
                            return Concatenate(run, l.ToDisplayString(), r.ToDisplayString());
                        return ScriptValue.FromNumber(l.ToNumber() + r.ToNumber());
                    case "-": return ScriptValue.FromNumber(l.ToNumber() - r.ToNumber());
                    case "*": return ScriptValue.FromNumber(l.ToNumber() * r.ToNumber());
                    case "/": return ScriptValue.FromNumber(l.ToNumber() / r.ToNumber());
                    case "%": return ScriptValue.FromNumber(JsRemainder(l.ToNumber(), r.ToNumber()));
                    case "<": return Compare(l, r, c => c < 0);
                    case "<=": return Compare(l, r, c => c <= 0);
                    case ">": return Compare(l, r, c => c > 0);
                    case ">=": return Compare(l, r, c => c >= 0);
                    case "==": return ScriptValue.FromBool(ScriptValue.LooseEquals(l, r));
                    case "!=": return ScriptValue.FromBool(!ScriptValue.LooseEquals(l, r));
                    case "===": return ScriptValue.FromBool(ScriptValue.StrictEquals(l, r));
                    case "!==": return ScriptValue.FromBool(!ScriptValue.StrictEquals(l, r));
                }
                return run.Fail("unknown operator " + op);
            }

            // The one operation that makes a value longer than its operands, so the one place a string's length is
            // bounded (MaximumStringLength, N-audit-1106-026). Measured before the concatenation is built.
            private static ScriptValue Concatenate(ScriptRun run, string left, string right)
            {
                long length = (long)left.Length + right.Length;
                if (length > MaximumStringLength)
                    return run.Fail("+ would build a string of " + length + " characters; the limit is " + MaximumStringLength);
                return ScriptValue.FromString(left + right);
            }

            // JS %: the remainder of TRUNCATED division, so it takes the dividend's sign (-7 % 3 is -1, -6 % 3 is -0).
            // C#'s % on doubles is that operation computed exactly (fmod), with no overflowing a / b quotient.
            private static double JsRemainder(double a, double b)
            {
                if (double.IsNaN(a) || double.IsNaN(b) || double.IsInfinity(a) || b == 0) return double.NaN;
                if (double.IsInfinity(b)) return a;
                return a % b;
            }

            private static ScriptValue Compare(ScriptValue l, ScriptValue r, Func<int, bool> test)
            {
                if (l.Kind == ScriptValueKind.String && r.Kind == ScriptValueKind.String)
                    return ScriptValue.FromBool(test(string.CompareOrdinal(l.Text, r.Text)));
                double a = l.ToNumber(), b = r.ToNumber();
                if (double.IsNaN(a) || double.IsNaN(b)) return ScriptValue.False;
                return ScriptValue.FromBool(test(a.CompareTo(b)));
            }
        }

        private sealed class RandomNode : Node
        {
            private readonly int _site;
            public RandomNode(int site) { _site = site; }
            public override ScriptValue Eval(ScriptRun run) { return ScriptValue.FromNumber(run.Context.Random(_site)); }
        }

        internal enum MathFunction { Abs, Min, Max, Floor, Ceil, Round, Sqrt, Pow, Sin, Cos, Tan, Atan, Atan2, Exp, Log, Sign }

        private sealed class MathNode : Node
        {
            private readonly MathFunction _f;
            private readonly Node[] _args;
            public MathNode(MathFunction f, Node[] args) { _f = f; _args = args; }

            /// <summary>JS Math.round: the nearest integer, halves toward +Infinity, -0 kept for -0.5 &lt;= x &lt; 0, and an
            /// already-integral large value returned as is (floor(x + 0.5) rounds 2^52 + 1 up and 0.49999999999999994
            /// to 1, both wrong).</summary>
            public static double JsRound(double x)
            {
                if (double.IsNaN(x) || double.IsInfinity(x) || x == 0) return x;
                if (x > 0 && x < 0.5) return 0.0;
                if (x < 0 && x >= -0.5) return -0.0;
                double f = Math.Floor(x);
                return x - f >= 0.5 ? f + 1 : f;
            }

            public static bool TryResolve(string name, int arity, out MathFunction f)
            {
                switch (name)
                {
                    case "abs": f = MathFunction.Abs; return arity == 1;
                    case "min": f = MathFunction.Min; return arity >= 1;
                    case "max": f = MathFunction.Max; return arity >= 1;
                    case "floor": f = MathFunction.Floor; return arity == 1;
                    case "ceil": f = MathFunction.Ceil; return arity == 1;
                    case "round": f = MathFunction.Round; return arity == 1;
                    case "sqrt": f = MathFunction.Sqrt; return arity == 1;
                    case "pow": f = MathFunction.Pow; return arity == 2;
                    case "sin": f = MathFunction.Sin; return arity == 1;
                    case "cos": f = MathFunction.Cos; return arity == 1;
                    case "tan": f = MathFunction.Tan; return arity == 1;
                    case "atan": f = MathFunction.Atan; return arity == 1;
                    case "atan2": f = MathFunction.Atan2; return arity == 2;
                    case "exp": f = MathFunction.Exp; return arity == 1;
                    case "log": f = MathFunction.Log; return arity == 1;
                    case "sign": f = MathFunction.Sign; return arity == 1;
                }
                f = MathFunction.Abs;
                return false;
            }

            public override ScriptValue Eval(ScriptRun run)
            {
                double a = _args[0].Eval(run).ToNumber();
                switch (_f)
                {
                    case MathFunction.Abs: return ScriptValue.FromNumber(Math.Abs(a));
                    case MathFunction.Floor: return ScriptValue.FromNumber(Math.Floor(a));
                    case MathFunction.Ceil: return ScriptValue.FromNumber(Math.Ceiling(a));
                    case MathFunction.Round: return ScriptValue.FromNumber(JsRound(a));
                    case MathFunction.Sqrt: return ScriptValue.FromNumber(Math.Sqrt(a));
                    case MathFunction.Sin: return ScriptValue.FromNumber(Math.Sin(a));
                    case MathFunction.Cos: return ScriptValue.FromNumber(Math.Cos(a));
                    case MathFunction.Tan: return ScriptValue.FromNumber(Math.Tan(a));
                    case MathFunction.Atan: return ScriptValue.FromNumber(Math.Atan(a));
                    case MathFunction.Exp: return ScriptValue.FromNumber(Math.Exp(a));
                    case MathFunction.Log: return ScriptValue.FromNumber(Math.Log(a));
                    case MathFunction.Sign: return ScriptValue.FromNumber(double.IsNaN(a) ? double.NaN : Math.Sign(a));
                    case MathFunction.Pow: return ScriptValue.FromNumber(Math.Pow(a, _args[1].Eval(run).ToNumber()));
                    case MathFunction.Atan2: return ScriptValue.FromNumber(Math.Atan2(a, _args[1].Eval(run).ToNumber()));
                    case MathFunction.Min:
                    case MathFunction.Max:
                    {
                        double best = a;
                        for (int i = 1; i < _args.Length; i++)
                        {
                            double v = _args[i].Eval(run).ToNumber();
                            if (double.IsNaN(v) || double.IsNaN(best)) best = double.NaN;
                            else if (_f == MathFunction.Min ? v < best : v > best) best = v;
                        }
                        return ScriptValue.FromNumber(best);
                    }
                }
                return run.Fail("unknown Math function");
            }
        }

        internal enum PointSource { Anchor, Cursor }
        internal enum PointMember { X, Y, DX, DY }

        private sealed class PointNode : Node
        {
            private readonly PointSource _source;
            public PointNode(PointSource source) { _source = source; }
            public override ScriptValue Eval(ScriptRun run)
            {
                double x, y, dx, dy;
                if (_source == PointSource.Anchor) run.Context.GetAnchor(out x, out y);
                else run.Context.GetCursor(out x, out y, out dx, out dy);
                return ScriptValue.FromPoint(x, y);
            }
        }

        private sealed class PointMemberNode : Node
        {
            private readonly PointSource _source;
            private readonly PointMember _member;
            public PointMemberNode(PointSource source, PointMember member) { _source = source; _member = member; }
            public override ScriptValue Eval(ScriptRun run)
            {
                double x, y, dx = 0, dy = 0;
                if (_source == PointSource.Anchor) run.Context.GetAnchor(out x, out y);
                else run.Context.GetCursor(out x, out y, out dx, out dy);
                switch (_member)
                {
                    case PointMember.X: return ScriptValue.FromNumber(x);
                    case PointMember.Y: return ScriptValue.FromNumber(y);
                    case PointMember.DX: return ScriptValue.FromNumber(dx);
                    default: return ScriptValue.FromNumber(dy);
                }
            }
        }

        internal enum MascotValue { LookRight, Dragging, Count, TotalCount, Time }

        private sealed class MascotValueNode : Node
        {
            private readonly MascotValue _value;
            public MascotValueNode(MascotValue value) { _value = value; }
            public override ScriptValue Eval(ScriptRun run)
            {
                IScriptContext c = run.Context;
                switch (_value)
                {
                    case MascotValue.LookRight: return ScriptValue.FromBool(c.LookRight);
                    case MascotValue.Dragging: return ScriptValue.FromBool(c.Dragging);
                    case MascotValue.Count: return ScriptValue.FromNumber(c.Count);
                    case MascotValue.TotalCount: return ScriptValue.FromNumber(c.TotalCount);
                    default: return ScriptValue.FromNumber(c.Time);
                }
            }
        }

        internal enum AreaMember { Left, Top, Right, Bottom, Width, Height, Visible, DLeft, DTop, DRight, DBottom }

        private sealed class AreaValueNode : Node
        {
            private readonly ScriptAreaId _area;
            private readonly AreaMember _member;
            public AreaValueNode(ScriptAreaId area, AreaMember member) { _area = area; _member = member; }

            public static bool TryResolve(string name, out AreaMember m)
            {
                switch (name)
                {
                    case "left": m = AreaMember.Left; return true;
                    case "top": m = AreaMember.Top; return true;
                    case "right": m = AreaMember.Right; return true;
                    case "bottom": m = AreaMember.Bottom; return true;
                    case "width": m = AreaMember.Width; return true;
                    case "height": m = AreaMember.Height; return true;
                    case "visible": m = AreaMember.Visible; return true;
                    case "dleft": m = AreaMember.DLeft; return true;
                    case "dtop": m = AreaMember.DTop; return true;
                    case "dright": m = AreaMember.DRight; return true;
                    case "dbottom": m = AreaMember.DBottom; return true;
                }
                m = AreaMember.Left;
                return false;
            }

            public override ScriptValue Eval(ScriptRun run)
            {
                ScriptArea a = run.Context.GetArea(_area);
                switch (_member)
                {
                    case AreaMember.Left: return ScriptValue.FromNumber(a.Left);
                    case AreaMember.Top: return ScriptValue.FromNumber(a.Top);
                    case AreaMember.Right: return ScriptValue.FromNumber(a.Right);
                    case AreaMember.Bottom: return ScriptValue.FromNumber(a.Bottom);
                    case AreaMember.Width: return ScriptValue.FromNumber(a.Right - a.Left);
                    case AreaMember.Height: return ScriptValue.FromNumber(a.Bottom - a.Top);
                    case AreaMember.Visible: return ScriptValue.FromBool(a.Visible);
                    case AreaMember.DLeft: return ScriptValue.FromNumber(a.DLeft);
                    case AreaMember.DTop: return ScriptValue.FromNumber(a.DTop);
                    case AreaMember.DRight: return ScriptValue.FromNumber(a.DRight);
                    default: return ScriptValue.FromNumber(a.DBottom);
                }
            }
        }

        private sealed class AreaContainsNode : Node
        {
            private readonly ScriptAreaId _area;
            private readonly Node _x, _y;
            public AreaContainsNode(ScriptAreaId area, Node x, Node y) { _area = area; _x = x; _y = y; }
            public override ScriptValue Eval(ScriptRun run)
            {
                ScriptArea a = run.Context.GetArea(_area);
                double x = _x.Eval(run).ToNumber(), y = _y.Eval(run).ToNumber();
                // Area.contains ignores visibility: a hidden window still has its geometry (Area.java 147-149).
                return ScriptValue.FromBool(x >= a.Left && x <= a.Right && y >= a.Top && y <= a.Bottom);
            }
        }

        internal enum BorderMember { X, Y, Left, Right, Top, Bottom, Width, Height }

        private sealed class BorderValueNode : Node
        {
            private readonly ScriptBorderId _border;
            private readonly BorderMember _member;
            public BorderValueNode(ScriptBorderId border, BorderMember member) { _border = border; _member = member; }

            public static bool TryResolve(string name, out BorderMember m)
            {
                switch (name)
                {
                    case "x": m = BorderMember.X; return true;
                    case "y": m = BorderMember.Y; return true;
                    case "left": m = BorderMember.Left; return true;
                    case "right": m = BorderMember.Right; return true;
                    case "top": m = BorderMember.Top; return true;
                    case "bottom": m = BorderMember.Bottom; return true;
                    case "width": m = BorderMember.Width; return true;
                    case "height": m = BorderMember.Height; return true;
                }
                m = BorderMember.X;
                return false;
            }

            public override ScriptValue Eval(ScriptRun run)
            {
                ScriptBorder b = run.Context.GetBorder(_border);
                switch (_member)
                {
                    case BorderMember.X: return ScriptValue.FromNumber(b.X);
                    case BorderMember.Y: return ScriptValue.FromNumber(b.Y);
                    case BorderMember.Left: return ScriptValue.FromNumber(b.Start);
                    case BorderMember.Top: return ScriptValue.FromNumber(b.Start);
                    case BorderMember.Right: return ScriptValue.FromNumber(b.End);
                    case BorderMember.Bottom: return ScriptValue.FromNumber(b.End);
                    default: return ScriptValue.FromNumber(b.End - b.Start);
                }
            }
        }

        private sealed class BorderIsOnNode : Node
        {
            private readonly ScriptBorderId _border;
            private readonly Node _point;
            public BorderIsOnNode(ScriptBorderId border, Node point) { _border = border; _point = point; }
            public override ScriptValue Eval(ScriptRun run)
            {
                ScriptValue p = _point.Eval(run);
                if (p.Kind != ScriptValueKind.Point) return run.Fail("isOn needs a point");
                return ScriptValue.FromBool(run.Context.IsOnBorder(_border, p.X, p.Y));
            }
        }

        /// <summary>mascot.variables[k] (or .get(k), which is a call and so never a target). A missing key reads null, as
        /// Map.get returns it; as a target, a new key past <see cref="MaximumVariables"/> faults.</summary>
        private sealed class VariableIndexNode : Node, IReference
        {
            public readonly Node Key;
            public readonly bool IsGetCall;
            public VariableIndexNode(Node key, bool isGetCall = false) { Key = key; IsGetCall = isGetCall; }
            public override ScriptValue Eval(ScriptRun run) { return Get(run, Prepare(run)); }

            public string Prepare(ScriptRun run) { return Key.Eval(run).ToDisplayString(); }

            public ScriptValue Get(ScriptRun run, string key)
            {
                ScriptValue v;
                return run.Context.TryGetVariable(key, out v) ? v : ScriptValue.Null;
            }

            public void Put(ScriptRun run, string key, ScriptValue value)
            {
                ScriptValue previous;
                if (!run.Context.TryGetVariable(key, out previous) && run.Context.VariableCount >= MaximumVariables)
                    run.Fail("mascot.variables already holds " + MaximumVariables + " names; a new name is refused");
                run.Context.SetVariable(key, value);
            }
        }

        /// <summary>
        /// A write to mascot.variables. Bracket assignment returns the value written, as a JS assignment does.
        /// <c>.put(key, value)</c> returns what the source's VariableMap.put returns: null for a new key. For a key that
        /// already had a value the source returns its Java wrapper, which has no SafeScript meaning; this returns the
        /// previous value instead, and the converter marks an expression that uses that result as approximate.
        /// </summary>
        private sealed class VariablePutNode : Node
        {
            private readonly Node _key, _value;
            private readonly bool _isPutCall;
            public VariablePutNode(Node key, Node value, bool isPutCall) { _key = key; _value = value; _isPutCall = isPutCall; }
            public override ScriptValue Eval(ScriptRun run)
            {
                string key = _key.Eval(run).ToDisplayString();
                ScriptValue value = _value.Eval(run);
                ScriptValue previous;
                bool had = run.Context.TryGetVariable(key, out previous);
                // A NEW name past MaximumVariables is refused, after both operands ran as the source order has them; a
                // name already held is rewritten as ever (N-audit-1106-026).
                if (!had && run.Context.VariableCount >= MaximumVariables)
                    return run.Fail("mascot.variables already holds " + MaximumVariables + " names; a new name is refused");
                run.Context.SetVariable(key, value);
                if (!_isPutCall) return value;
                return had ? previous : ScriptValue.Null;
            }
        }
    }

    /// <summary>A parsed SafeScript expression: immutable, shareable, evaluated against a context.</summary>
    internal sealed class ScriptProgram
    {
        private readonly SafeScript.Node _root;
        public string Source { get; }
        /// <summary>The character offsets of every Math.random() site, in source order: each evaluation of a site is one
        /// draw, which the parity trace logs with that offset.</summary>
        public IReadOnlyList<int> RandomSites { get; }
        /// <summary>Whether the expression reads or writes mascot.variables, so the validator can require the 'variables'
        /// feature from what was parsed rather than from the text.</summary>
        public bool UsesVariables { get; }

        /// <summary>Whether the program uses the statement grammar or the extended object model (statements, var, an
        /// update or compound operator, an assignment to a name, undefined, typeof, the manager, bounds, translate, an
        /// absent property or a dynamic member chain), so the validator can require the 'statements' feature from what
        /// was parsed rather than from the text.</summary>
        public bool UsesStatements { get; }

        /// <summary>Every absent Java property the program reads (it reads undefined there, as Nashorn reads it), by path,
        /// in source order: the converter names each in a warning.</summary>
        public IReadOnlyList<string> AbsentProperties { get; }

        /// <summary>The script globals the program declares (var) or assigns (a bare name), sorted: a sibling script of
        /// the same action reads them from the store they share (SPEC rule 2), so a caller may add them to that
        /// sibling's scope.</summary>
        public IReadOnlyList<string> Globals { get; }

        internal ScriptProgram(string source, SafeScript.Node root, List<int> randomSites, bool usesVariables = false,
            bool usesStatements = false, List<string> absentProperties = null, List<string> globals = null)
        {
            Source = source;
            _root = root;
            RandomSites = randomSites.AsReadOnly();
            UsesVariables = usesVariables;
            UsesStatements = usesStatements;
            AbsentProperties = (absentProperties ?? new List<string>()).AsReadOnly();
            Globals = (globals ?? new List<string>()).AsReadOnly();
        }

        /// <summary>Evaluate. A runtime fault yields null and is reported once through <see cref="IScriptContext.ReportFault"/>.
        /// An undefined result reaches the caller as null, as Nashorn's eval hands undefined to Java.</summary>
        public ScriptValue Evaluate(IScriptContext context)
        {
            var run = new SafeScript.ScriptRun(context);
            try
            {
                ScriptValue v = _root.Eval(run);
                return v.Kind == ScriptValueKind.Undefined ? ScriptValue.Null : v;
            }
            catch (SafeScript.ScriptFault fault)
            {
                context.ReportFault(Source, fault.Message);
                return ScriptValue.Null;
            }
        }

        /// <summary>Evaluate as a condition: JS truthiness, and a faulted evaluation is false.</summary>
        public bool EvaluateCondition(IScriptContext context) { return Evaluate(context).IsTruthy; }
    }

    /// <summary>Value kinds. Undefined and the Java object kinds (a weak reference, a mascot reference, a rectangle) were
    /// added after the first five, which keep their numbers.</summary>
    internal enum ScriptValueKind : byte { Null, Number, Boolean, String, Point, Undefined, WeakRef, MascotRef, Bounds }

    /// <summary>A java.awt.Rectangle as a script reads it (x, y, width, height); a new one per read, as getBounds builds one.</summary>
    internal sealed class ScriptRect
    {
        public readonly double X, Y, Width, Height;
        public ScriptRect(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; }
    }

    /// <summary>A WeakReference a script holds: one object per getMascotWithAffordance call, as the source builds one per
    /// call, so two lookups of the same companion are not the same reference.</summary>
    internal sealed class ScriptWeakRef
    {
        public readonly object Target;
        public ScriptWeakRef(object target) { Target = target; }
    }

    /// <summary>A SafeScript value: a JS-like number, boolean, string, null or undefined, a point (an anchor or cursor),
    /// or a Java object: a weak reference, a mascot reference (an opaque handle the context reads) or a rectangle.</summary>
    internal readonly struct ScriptValue
    {
        public readonly ScriptValueKind Kind;
        public readonly double Number;   // the number, the boolean as 1/0, or the point's x
        public readonly double Y;        // the point's y
        public readonly string Text;
        public readonly object Ref;      // a ScriptWeakRef, a mascot handle, or a ScriptRect

        private ScriptValue(ScriptValueKind kind, double number, double y, string text, object reference = null)
        {
            Kind = kind;
            Number = number;
            Y = y;
            Text = text;
            Ref = reference;
        }

        public static readonly ScriptValue Null = new ScriptValue(ScriptValueKind.Null, 0, 0, null);
        public static readonly ScriptValue Undefined = new ScriptValue(ScriptValueKind.Undefined, double.NaN, 0, null);
        public static readonly ScriptValue True = new ScriptValue(ScriptValueKind.Boolean, 1, 0, null);
        public static readonly ScriptValue False = new ScriptValue(ScriptValueKind.Boolean, 0, 0, null);
        public static ScriptValue FromNumber(double n) { return new ScriptValue(ScriptValueKind.Number, n, 0, null); }
        public static ScriptValue FromBool(bool b) { return b ? True : False; }
        public static ScriptValue FromString(string s) { return s == null ? Null : new ScriptValue(ScriptValueKind.String, 0, 0, s); }
        public static ScriptValue FromPoint(double x, double y) { return new ScriptValue(ScriptValueKind.Point, x, y, null); }
        public static ScriptValue FromWeakRef(object mascot) { return new ScriptValue(ScriptValueKind.WeakRef, 0, 0, null, new ScriptWeakRef(mascot)); }
        public static ScriptValue FromMascot(object mascot) { return mascot == null ? Null : new ScriptValue(ScriptValueKind.MascotRef, 0, 0, null, mascot); }
        public static ScriptValue FromBounds(ScriptRect r) { return r == null ? Null : new ScriptValue(ScriptValueKind.Bounds, 0, 0, null, r); }

        public double X { get { return Number; } }

        /// <summary>A weak reference, a mascot reference or a rectangle: a Java object a script can hold.</summary>
        public bool IsJavaObject { get { return Kind == ScriptValueKind.WeakRef || Kind == ScriptValueKind.MascotRef || Kind == ScriptValueKind.Bounds; } }

        /// <summary>null or undefined: the two values a member read faults on and that loosely equal each other.</summary>
        public bool IsNullish { get { return Kind == ScriptValueKind.Null || Kind == ScriptValueKind.Undefined; } }

        public bool IsTruthy
        {
            get
            {
                switch (Kind)
                {
                    case ScriptValueKind.Null: return false;
                    case ScriptValueKind.Undefined: return false;
                    case ScriptValueKind.Boolean: return Number != 0;
                    case ScriptValueKind.Number: return Number != 0 && !double.IsNaN(Number);
                    case ScriptValueKind.String: return Text.Length > 0;
                    default: return true;
                }
            }
        }

        public double ToNumber()
        {
            switch (Kind)
            {
                case ScriptValueKind.Null: return 0;
                case ScriptValueKind.Boolean:
                case ScriptValueKind.Number: return Number;
                case ScriptValueKind.String: return StringToNumber(Text);
                default: return double.NaN;
            }
        }

        /// <summary>
        /// ES5.1 ToNumber on a string (section 9.3.1), which Nashorn implements: JS white space trimmed, empty is 0, a
        /// 0x/0X hex integer (unsigned), or an optionally signed decimal or Infinity (case-sensitive); anything else is
        /// NaN. .NET's parser differs on hex, on "infinity" in any case, and on the white space set.
        /// </summary>
        public static double StringToNumber(string text)
        {
            int start = 0, end = text.Length;
            while (start < end && IsJsWhiteSpace(text[start])) start++;
            while (end > start && IsJsWhiteSpace(text[end - 1])) end--;
            if (start == end) return 0;
            string t = text.Substring(start, end - start);
            if (t.Length > 2 && t[0] == '0' && (t[1] == 'x' || t[1] == 'X'))
            {
                var value = System.Numerics.BigInteger.Zero;
                for (int i = 2; i < t.Length; i++)
                {
                    int digit = HexDigit(t[i]);
                    if (digit < 0) return double.NaN;
                    value = value * 16 + digit;
                }
                return (double)value;
            }
            int p = 0;
            if (t[0] == '+' || t[0] == '-') p = 1;
            string body = t.Substring(p);
            if (body == "Infinity") return t[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
            int digits = 0, k = 0;
            while (k < body.Length && body[k] >= '0' && body[k] <= '9') { k++; digits++; }
            if (k < body.Length && body[k] == '.')
            {
                k++;
                while (k < body.Length && body[k] >= '0' && body[k] <= '9') { k++; digits++; }
            }
            if (digits == 0) return double.NaN;
            if (k < body.Length && (body[k] == 'e' || body[k] == 'E'))
            {
                k++;
                if (k < body.Length && (body[k] == '+' || body[k] == '-')) k++;
                int exp = 0;
                while (k < body.Length && body[k] >= '0' && body[k] <= '9') { k++; exp++; }
                if (exp == 0) return double.NaN;
            }
            if (k != body.Length) return double.NaN;
            double d;
            return double.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out d) ? d : double.NaN;
        }

        private static bool IsJsWhiteSpace(char c)
        {
            // StrWhiteSpaceChar: TAB VT FF SP NBSP BOM, any Zs, and the line terminators LF CR LS PS.
            return c == '\t' || c == '\v' || c == '\f' || c == ' ' || c == '\u00A0' || c == '\uFEFF'
                || c == '\n' || c == '\r' || c == '\u2028' || c == '\u2029'
                || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        public string ToDisplayString()
        {
            switch (Kind)
            {
                case ScriptValueKind.Null: return "null";
                case ScriptValueKind.Boolean: return Number != 0 ? "true" : "false";
                case ScriptValueKind.String: return Text;
                case ScriptValueKind.Point: return "(" + FormatNumber(Number) + ", " + FormatNumber(Y) + ")";
                case ScriptValueKind.Undefined: return "undefined";
                case ScriptValueKind.Bounds:
                {
                    var r = (ScriptRect)Ref;
                    return "java.awt.Rectangle[x=" + FormatNumber(r.X) + ",y=" + FormatNumber(r.Y) + ",width=" + FormatNumber(r.Width)
                        + ",height=" + FormatNumber(r.Height) + "]";
                }
                // The source's toString of these carries an identity hash or a manager id, which nothing here reproduces.
                case ScriptValueKind.WeakRef: return "java.lang.ref.WeakReference";
                case ScriptValueKind.MascotRef: return "mascot";
                default: return FormatNumber(Number);
            }
        }

        /// <summary>
        /// JS Number.prototype.toString (ES5.1 section 9.8.1): the shortest digits that round-trip, written plainly
        /// for a decimal exponent from -6 to 21 and as d.ddde+n outside it; -0 is "0". String concatenation and
        /// variable keys go through this, so its thresholds and spelling must be JS's, not .NET's "1E+15".
        /// </summary>
        public static string FormatNumber(double m)
        {
            if (double.IsNaN(m)) return "NaN";
            if (m == 0) return "0";
            if (m < 0) return "-" + FormatNumber(-m);
            if (double.IsPositiveInfinity(m)) return "Infinity";
            // .NET's "R" is the shortest round-trip form; only its layout is rewritten.
            string r = m.ToString("R", CultureInfo.InvariantCulture);
            int e = 0;
            string mantissa = r;
            int ei = r.IndexOfAny(new[] { 'E', 'e' });
            if (ei >= 0)
            {
                e = int.Parse(r.Substring(ei + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                mantissa = r.Substring(0, ei);
            }
            int dot = mantissa.IndexOf('.');
            string whole = dot >= 0 ? mantissa.Substring(0, dot) : mantissa;
            string all = whole + (dot >= 0 ? mantissa.Substring(dot + 1) : "");
            string digits = all.TrimStart('0');
            int n = whole.Length + e - (all.Length - digits.Length);   // value = 0.digits x 10^n
            digits = digits.TrimEnd('0');
            int k = digits.Length;
            if (k <= n && n <= 21) return digits + new string('0', n - k);
            if (0 < n && n <= 21) return digits.Substring(0, n) + "." + digits.Substring(n);
            if (-6 < n && n <= 0) return "0." + new string('0', -n) + digits;
            string exponent = (n - 1 >= 0 ? "+" : "-") + Math.Abs(n - 1).ToString(CultureInfo.InvariantCulture);
            return k == 1 ? digits + "e" + exponent : digits.Substring(0, 1) + "." + digits.Substring(1) + "e" + exponent;
        }

        public static bool StrictEquals(ScriptValue a, ScriptValue b)
        {
            if (a.Kind != b.Kind) return false;
            switch (a.Kind)
            {
                case ScriptValueKind.Null: return true;
                case ScriptValueKind.Undefined: return true;
                case ScriptValueKind.String: return string.Equals(a.Text, b.Text, StringComparison.Ordinal);
                case ScriptValueKind.Point: return a.Number == b.Number && a.Y == b.Y;
                // Java objects compare by identity: one WeakReference per lookup, one Rectangle per getBounds, one
                // Mascot per companion.
                case ScriptValueKind.WeakRef:
                case ScriptValueKind.MascotRef:
                case ScriptValueKind.Bounds: return ReferenceEquals(a.Ref, b.Ref);
                default: return a.Number == b.Number;
            }
        }

        public static bool LooseEquals(ScriptValue a, ScriptValue b)
        {
            // ES5 11.9.3: null and undefined equal each other and nothing else.
            if (a.IsNullish || b.IsNullish) return a.IsNullish && b.IsNullish;
            if (a.Kind == b.Kind) return StrictEquals(a, b);
            if (a.Kind == ScriptValueKind.Point || b.Kind == ScriptValueKind.Point || a.IsJavaObject || b.IsJavaObject) return false;
            return a.ToNumber() == b.ToNumber();
        }

        public override string ToString() { return ToDisplayString(); }
    }

    internal enum ScriptAreaId { None = 0, WorkArea = 1, Screen = 2, ComplexScreen = 3, ActiveWindow = 4 }

    /// <summary>Borders: the environment's contextual floor/ceiling/wall, then four per area (left, top, right, bottom)
    /// in <see cref="ScriptAreaId"/> order.</summary>
    internal enum ScriptBorderId
    {
        None = 0, Floor = 1, Ceiling = 2, Wall = 3,
        WorkAreaLeft = 4, WorkAreaTop, WorkAreaRight, WorkAreaBottom,
        ScreenLeft, ScreenTop, ScreenRight, ScreenBottom,
        ComplexScreenLeft, ComplexScreenTop, ComplexScreenRight, ComplexScreenBottom,
        ActiveWindowLeft, ActiveWindowTop, ActiveWindowRight, ActiveWindowBottom,
    }

    /// <summary>An Area as a script sees it (environment/Area.java): edges, visibility and the edges' movement since the
    /// previous tick.</summary>
    internal struct ScriptArea
    {
        public double Left, Top, Right, Bottom, DLeft, DTop, DRight, DBottom;
        public bool Visible;

        public ScriptArea(double left, double top, double right, double bottom, bool visible,
            double dleft = 0, double dtop = 0, double dright = 0, double dbottom = 0)
        {
            Left = left; Top = top; Right = right; Bottom = bottom; Visible = visible;
            DLeft = dleft; DTop = dtop; DRight = dright; DBottom = dbottom;
        }
    }

    /// <summary>A border as a script sees it: a horizontal one (floor/ceiling) has Y and spans Start..End in x; a
    /// vertical one (wall) has X and spans Start..End in y.</summary>
    internal struct ScriptBorder
    {
        public double X, Y, Start, End;

        public ScriptBorder(double x, double y, double start, double end)
        {
            X = x; Y = y; Start = start; End = end;
        }
    }

    /// <summary>What an evaluation reads. Implemented by the host per companion (live state) and by tests; every
    /// member is a plain read except <see cref="Random"/> (one draw from the companion's stream) and
    /// <see cref="SetVariable"/>.</summary>
    internal interface IScriptContext
    {
        bool TryGetName(string name, out ScriptValue value);
        void GetAnchor(out double x, out double y);
        void GetCursor(out double x, out double y, out double dx, out double dy);
        bool LookRight { get; }
        bool Dragging { get; }
        int Count { get; }
        int TotalCount { get; }
        double Time { get; }
        ScriptArea GetArea(ScriptAreaId area);
        ScriptBorder GetBorder(ScriptBorderId border);
        bool IsOnBorder(ScriptBorderId border, double x, double y);
        bool TryGetVariable(string key, out ScriptValue value);
        void SetVariable(string key, ScriptValue value);
        /// <summary>How many names the companion's mascot.variables holds (bounded by SafeScript.MaximumVariables).</summary>
        int VariableCount { get; }
        /// <summary>One draw in [0, 1) for the Math.random() at <paramref name="site"/> (its character offset).</summary>
        double Random(int site);
        void ReportFault(string source, string message);

        // ---- the statement grammar and the extended object model (SPEC rules 2, 5-8)

        /// <summary>A script global (a var or an assigned bare name) of the store this evaluation shares with the other
        /// scripts of the same action activation (Nashorn's per-Bindings global). A bare name reads it before the
        /// bindings (<see cref="TryGetName"/>).</summary>
        bool TryGetGlobal(string name, out ScriptValue value);
        void SetGlobal(string name, ScriptValue value);
        /// <summary>How many globals the store holds (bounded by SafeScript.MaximumGlobals).</summary>
        int GlobalCount { get; }

        /// <summary>The asking companion, as a reference to it would hold it (an opaque handle).</summary>
        object Self { get; }
        /// <summary>Manager.getMascotWithAffordance: the first active companion in manager order whose current offers
        /// contain <paramref name="affordance"/> exactly, the asking one included; null when none does. A plain lookup,
        /// which the source's scan actions observe and a script's call does not.</summary>
        object FindMascotWithAffordance(string affordance);
        /// <summary>WeakReference.get: false once the companion has been disposed. The source keeps returning it until the
        /// collector clears the reference, which nothing here can time, so a disposed one reads as cleared.</summary>
        bool IsReferenceLive(object mascot);
        /// <summary>A companion's anchor (a copy), facing and Mascot.getBounds, by handle.</summary>
        void GetMascotAnchor(object mascot, out double x, out double y);
        bool GetMascotLookRight(object mascot);
        ScriptRect GetMascotBounds(object mascot);
        /// <summary>java.awt.Point.translate on the asking companion's live anchor (int arithmetic, wrapping).</summary>
        void TranslateAnchor(int dx, int dy);
    }
}
