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
    /// <c>mascot.variables['k']</c>, <c>Math.random()</c> ...). No script engine, no statements, no loops, no
    /// closures and no access beyond that model: every name is resolved when the text is parsed, so an unknown one is
    /// a parse error that names itself, and a parsed program is an immutable tree any number of companions share.
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
                root = parser.ParseExpression();
                parser.ExpectEnd();
            }
            catch (ScriptSyntaxException ex)
            {
                error = ex.Message;
                return false;
            }
            program = new ScriptProgram(source, root, parser.RandomSites);
            return true;
        }

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

            private static readonly string[] Operators =
            {
                "===", "!==", "==", "!=", "<=", ">=", "&&", "||",
                "<", ">", "+", "-", "*", "/", "%", "!", "?", ":", "(", ")", "[", "]", ".", ",", "=",
            };

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
                            tokens.Add(new Token { Kind = TokenKind.Number, Offset = start,
                                Number = (double)long.Parse(s.Substring(hexStart, i - hexStart), NumberStyles.HexNumber, CultureInfo.InvariantCulture) });
                            continue;
                        }
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
                                    case 'u':
                                        if (i + 4 > s.Length) { error = "malformed escape at " + (i - 2); return false; }
                                        int code;
                                        if (!int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                        { error = "malformed escape at " + (i - 2); return false; }
                                        sb.Append((char)code);
                                        i += 4;
                                        break;
                                    default: sb.Append(e); break;   // \\ \' \" and anything else stand for themselves
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
        internal enum ObjectKind { None, Mascot, Environment, Anchor, Cursor, Area, Border, ContextBorder, Variables, Math }

        // ---------------------------------------------------------------- parser + binder

        private sealed class Parser
        {
            private readonly List<Token> _tokens;
            private readonly Func<string, bool> _scope;
            private int _pos;
            private int _depth;
            public readonly List<int> RandomSites = new List<int>();

            public Parser(List<Token> tokens, Func<string, bool> scope) { _tokens = tokens; _scope = scope; }

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

            // precedence climbing: 1 ?: 2 || 3 && 4 == != === !== 5 < <= > >= 6 + - 7 * / %
            public Node ParseExpression()
            {
                Enter();
                try
                {
                    Node test = ParseAssignment();
                    if (IsOp("?"))
                    {
                        _pos++;
                        Node yes = ParseExpression();
                        Expect(":");
                        Node no = ParseExpression();
                        return new ConditionalNode(test, yes, no);
                    }
                    return test;
                }
                finally { Leave(); }
            }

            private Node ParseAssignment()
            {
                Node left = ParseBinary(2);
                if (IsOp("="))
                {
                    int at = Peek.Offset;
                    _pos++;
                    var index = left as VariableIndexNode;
                    if (index == null) throw new ScriptSyntaxException("only mascot.variables['name'] can be assigned (at " + at + ")");
                    Node value = ParseExpression();
                    return new VariablePutNode(index.Key, value);
                }
                return left;
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
                return ParsePostfix();
            }

            // A member chain is bound as it is read: each step must name something the object model has.
            private Node ParsePostfix()
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
                    case "null": case "undefined": return new LiteralNode(ScriptValue.Null);
                    case "NaN": return new LiteralNode(ScriptValue.FromNumber(double.NaN));
                    case "Infinity": return new LiteralNode(ScriptValue.FromNumber(double.PositiveInfinity));
                    case "mascot": return ParseChain(ObjectKind.Mascot, ScriptBorderId.None, ScriptAreaId.None, "mascot", t.Offset);
                    case "Math": return ParseMath(t.Offset);
                }
                if (!_scope(t.Text)) throw new ScriptSyntaxException("unknown name '" + t.Text + "' at " + t.Offset);
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
                        _pos++;
                        Node key = ParseExpression();
                        Expect("]");
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
                                case "variables": kind = ObjectKind.Variables; path = next; continue;
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
                                if (member == "get" && args.Count == 1) return new VariableIndexNode(args[0]);
                                if (member == "put" && args.Count == 2) return new VariablePutNode(args[0], args[1]);
                                throw new ScriptSyntaxException(next + " has the wrong number of arguments (at " + offset + ")");
                            }
                            break;
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
            public string Fault;
            public ScriptRun(IScriptContext context) { Context = context; }
            public ScriptValue Fail(string message) { if (Fault == null) Fault = message; return ScriptValue.Null; }
        }

        private sealed class LiteralNode : Node
        {
            private readonly ScriptValue _value;
            public LiteralNode(ScriptValue value) { _value = value; }
            public override ScriptValue Eval(ScriptRun run) { return _value; }
        }

        private sealed class NameNode : Node
        {
            private readonly string _name;
            public NameNode(string name) { _name = name; }
            public override ScriptValue Eval(ScriptRun run)
            {
                ScriptValue v;
                return run.Context.TryGetName(_name, out v) ? v : run.Fail("'" + _name + "' has no value here");
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
                switch (_op)
                {
                    case "+":
                        if (l.Kind == ScriptValueKind.String || r.Kind == ScriptValueKind.String)
                            return ScriptValue.FromString(l.ToDisplayString() + r.ToDisplayString());
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
                return run.Fail("unknown operator " + _op);
            }

            // JS %: the remainder of TRUNCATED division, so it takes the dividend's sign (-7 % 3 is -1).
            private static double JsRemainder(double a, double b)
            {
                if (double.IsNaN(a) || double.IsNaN(b) || double.IsInfinity(a) || b == 0) return double.NaN;
                if (double.IsInfinity(b)) return a;
                return a - b * Math.Truncate(a / b);
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
                    case MathFunction.Round: return ScriptValue.FromNumber(Math.Floor(a + 0.5));   // JS rounds .5 up
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
                return ScriptValue.FromBool(a.Visible && x >= a.Left && x <= a.Right && y >= a.Top && y <= a.Bottom);
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

        private sealed class VariableIndexNode : Node
        {
            public readonly Node Key;
            public VariableIndexNode(Node key) { Key = key; }
            public override ScriptValue Eval(ScriptRun run)
            {
                ScriptValue v;
                return run.Context.TryGetVariable(Key.Eval(run).ToDisplayString(), out v) ? v : ScriptValue.Null;
            }
        }

        private sealed class VariablePutNode : Node
        {
            private readonly Node _key, _value;
            public VariablePutNode(Node key, Node value) { _key = key; _value = value; }
            public override ScriptValue Eval(ScriptRun run)
            {
                string key = _key.Eval(run).ToDisplayString();
                ScriptValue value = _value.Eval(run);
                run.Context.SetVariable(key, value);
                return value;
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

        internal ScriptProgram(string source, SafeScript.Node root, List<int> randomSites)
        {
            Source = source;
            _root = root;
            RandomSites = randomSites.AsReadOnly();
        }

        /// <summary>Evaluate. A runtime fault yields null and is reported once through <see cref="IScriptContext.ReportFault"/>.</summary>
        public ScriptValue Evaluate(IScriptContext context)
        {
            var run = new SafeScript.ScriptRun(context);
            ScriptValue v = _root.Eval(run);
            if (run.Fault != null)
            {
                context.ReportFault(Source, run.Fault);
                return ScriptValue.Null;
            }
            return v;
        }

        /// <summary>Evaluate as a condition: JS truthiness, and a faulted evaluation is false.</summary>
        public bool EvaluateCondition(IScriptContext context) { return Evaluate(context).IsTruthy; }
    }

    internal enum ScriptValueKind : byte { Null, Number, Boolean, String, Point }

    /// <summary>A SafeScript value: a JS-like number, boolean, string, null, or a point (an anchor or cursor).</summary>
    internal readonly struct ScriptValue
    {
        public readonly ScriptValueKind Kind;
        public readonly double Number;   // the number, the boolean as 1/0, or the point's x
        public readonly double Y;        // the point's y
        public readonly string Text;

        private ScriptValue(ScriptValueKind kind, double number, double y, string text)
        {
            Kind = kind;
            Number = number;
            Y = y;
            Text = text;
        }

        public static readonly ScriptValue Null = new ScriptValue(ScriptValueKind.Null, 0, 0, null);
        public static readonly ScriptValue True = new ScriptValue(ScriptValueKind.Boolean, 1, 0, null);
        public static readonly ScriptValue False = new ScriptValue(ScriptValueKind.Boolean, 0, 0, null);
        public static ScriptValue FromNumber(double n) { return new ScriptValue(ScriptValueKind.Number, n, 0, null); }
        public static ScriptValue FromBool(bool b) { return b ? True : False; }
        public static ScriptValue FromString(string s) { return s == null ? Null : new ScriptValue(ScriptValueKind.String, 0, 0, s); }
        public static ScriptValue FromPoint(double x, double y) { return new ScriptValue(ScriptValueKind.Point, x, y, null); }

        public double X { get { return Number; } }

        public bool IsTruthy
        {
            get
            {
                switch (Kind)
                {
                    case ScriptValueKind.Null: return false;
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
                case ScriptValueKind.String:
                {
                    string t = Text.Trim();
                    if (t.Length == 0) return 0;
                    double d;
                    return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : double.NaN;
                }
                default: return double.NaN;
            }
        }

        public string ToDisplayString()
        {
            switch (Kind)
            {
                case ScriptValueKind.Null: return "null";
                case ScriptValueKind.Boolean: return Number != 0 ? "true" : "false";
                case ScriptValueKind.String: return Text;
                case ScriptValueKind.Point: return "(" + FormatNumber(Number) + ", " + FormatNumber(Y) + ")";
                default: return FormatNumber(Number);
            }
        }

        /// <summary>JS number-to-string for the values a skin can produce: integers without a decimal point.</summary>
        public static string FormatNumber(double n)
        {
            if (double.IsNaN(n)) return "NaN";
            if (double.IsPositiveInfinity(n)) return "Infinity";
            if (double.IsNegativeInfinity(n)) return "-Infinity";
            if (n == Math.Floor(n) && Math.Abs(n) < 1e15) return ((long)n).ToString(CultureInfo.InvariantCulture);
            return n.ToString("R", CultureInfo.InvariantCulture);
        }

        public static bool StrictEquals(ScriptValue a, ScriptValue b)
        {
            if (a.Kind != b.Kind) return false;
            switch (a.Kind)
            {
                case ScriptValueKind.Null: return true;
                case ScriptValueKind.String: return string.Equals(a.Text, b.Text, StringComparison.Ordinal);
                case ScriptValueKind.Point: return a.Number == b.Number && a.Y == b.Y;
                default: return a.Number == b.Number;
            }
        }

        public static bool LooseEquals(ScriptValue a, ScriptValue b)
        {
            if (a.Kind == ScriptValueKind.Null || b.Kind == ScriptValueKind.Null) return a.Kind == b.Kind;
            if (a.Kind == b.Kind) return StrictEquals(a, b);
            if (a.Kind == ScriptValueKind.Point || b.Kind == ScriptValueKind.Point) return false;
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
        /// <summary>One draw in [0, 1) for the Math.random() at <paramref name="site"/> (its character offset).</summary>
        double Random(int site);
        void ReportFault(string source, string message);
    }
}
