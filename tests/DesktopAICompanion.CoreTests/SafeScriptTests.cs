using System;
using System.Collections.Generic;
using System.Text;

namespace DesktopAICompanion
{
    /// <summary>
    /// SafeScript (SafeExpression v2, grimoire/06 section 6): the sandboxed Shimeji expression subset. Each group pins one
    /// rule a converted skin's behaviour depends on, against a recording context, so a draw, a fault or a border query
    /// that should not happen is visible, not only a wrong value.
    /// </summary>
    internal static partial class Program
    {
        private sealed class FakeScriptContext : IScriptContext
        {
            public double AnchorX = 640, AnchorY = 680, CursorX = 100, CursorY = 200, CursorDX = 3, CursorDY = -1;
            public bool Right, IsDragging;
            public int SameType = 2, All = 3;
            public double Ticks = 42;
            public ScriptArea WorkArea = new ScriptArea { Left = 0, Top = 0, Right = 1280, Bottom = 680, Visible = true };
            public ScriptArea ActiveWindow = new ScriptArea { Left = 100, Top = 100, Right = 500, Bottom = 400, Visible = true };
            public readonly Dictionary<string, ScriptValue> Names = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
            public readonly Dictionary<string, ScriptValue> Vars = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
            public readonly Queue<double> Draws = new Queue<double>();
            public readonly List<int> DrawSites = new List<int>();
            public readonly List<string> Faults = new List<string>();
            public readonly List<ScriptBorderId> BorderQueries = new List<ScriptBorderId>();

            public bool TryGetName(string name, out ScriptValue value) { return Names.TryGetValue(name, out value); }
            public void GetAnchor(out double x, out double y) { x = AnchorX; y = AnchorY; }
            public void GetCursor(out double x, out double y, out double dx, out double dy) { x = CursorX; y = CursorY; dx = CursorDX; dy = CursorDY; }
            public bool LookRight { get { return Right; } }
            public bool Dragging { get { return IsDragging; } }
            public int Count { get { return SameType; } }
            public int TotalCount { get { return All; } }
            public double Time { get { return Ticks; } }
            public ScriptArea GetArea(ScriptAreaId area) { return area == ScriptAreaId.ActiveWindow ? ActiveWindow : WorkArea; }
            public ScriptBorder GetBorder(ScriptBorderId border)
            {
                return border == ScriptBorderId.Floor
                    ? new ScriptBorder { Y = WorkArea.Bottom, Start = WorkArea.Left, End = WorkArea.Right }
                    : new ScriptBorder { X = WorkArea.Left, Start = WorkArea.Top, End = WorkArea.Bottom };
            }
            public bool IsOnBorder(ScriptBorderId border, double x, double y)
            {
                BorderQueries.Add(border);
                if (border == ScriptBorderId.Floor) return y == WorkArea.Bottom && x >= WorkArea.Left && x <= WorkArea.Right;
                if (border == ScriptBorderId.WorkAreaLeft) return x == WorkArea.Left;
                return false;
            }
            public bool TryGetVariable(string key, out ScriptValue value) { return Vars.TryGetValue(key, out value); }
            public void SetVariable(string key, ScriptValue value) { Vars[key] = value; }
            public int VariableCount { get { return Vars.Count; } }
            public double Random(int site) { DrawSites.Add(site); return Draws.Count > 0 ? Draws.Dequeue() : 0.5; }
            public void ReportFault(string source, string message) { Faults.Add(message); }

            public readonly Dictionary<string, ScriptValue> Globals = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
            public bool TryGetGlobal(string name, out ScriptValue value) { return Globals.TryGetValue(name, out value); }
            public void SetGlobal(string name, ScriptValue value) { Globals[name] = value; }
            public int GlobalCount { get { return Globals.Count; } }

            /// <summary>The manager's companions in order; <see cref="Me"/> is the asking one (put it in the list to make it
            /// eligible, as an active companion is).</summary>
            public readonly List<FakeMascot> Manager = new List<FakeMascot>();
            public readonly FakeMascot Me = new FakeMascot { Name = "me" };
            public int Translations;
            public object Self { get { return Me; } }
            public object FindMascotWithAffordance(string affordance)
            {
                foreach (FakeMascot m in Manager) if (m.Offers.Contains(affordance)) return m;
                return null;
            }
            public bool IsReferenceLive(object mascot) { return !((FakeMascot)mascot).Disposed; }
            public void GetMascotAnchor(object mascot, out double x, out double y)
            {
                var m = (FakeMascot)mascot;
                if (ReferenceEquals(m, Me)) { x = AnchorX; y = AnchorY; } else { x = m.X; y = m.Y; }
            }
            public bool GetMascotLookRight(object mascot) { return ReferenceEquals(mascot, Me) ? Right : ((FakeMascot)mascot).Right; }
            public ScriptRect GetMascotBounds(object mascot)
            {
                var m = (FakeMascot)mascot;
                double x, y;
                GetMascotAnchor(mascot, out x, out y);
                return new ScriptRect(x - m.CenterX, y - m.CenterY, m.Width, m.Height);
            }
            public void TranslateAnchor(int dx, int dy) { Translations++; AnchorX += dx; AnchorY += dy; }
        }

        private sealed class FakeMascot
        {
            public string Name;
            public double X, Y;
            public bool Right, Disposed;
            public int Width = 64, Height = 48, CenterX = 32, CenterY = 48;
            public readonly List<string> Offers = new List<string>();
        }

        private static ScriptProgram ParseOrFail(string source, Func<string, bool> scope = null)
        {
            ScriptProgram program;
            string error;
            AssertTrue(SafeScript.TryParse(source, scope, out program, out error), "SafeScript refused '" + source + "': " + error);
            return program;
        }

        private static ScriptValue Eval(string source, FakeScriptContext context = null, Func<string, bool> scope = null)
        {
            return ParseOrFail(source, scope).Evaluate(context ?? new FakeScriptContext());
        }

        private static string ParseError(string source, Func<string, bool> scope = null)
        {
            ScriptProgram program;
            string error;
            AssertFalse(SafeScript.TryParse(source, scope, out program, out error), "SafeScript accepted '" + source + "', which it must refuse.");
            return error ?? "";
        }

        private static void TestSafeScriptArithmeticAndPrecedence()
        {
            AssertEqual(7.0, Eval("1 + 2 * 3").Number, "* does not bind tighter than +.");
            AssertEqual(9.0, Eval("(1 + 2) * 3").Number, "Parentheses do not group.");
            AssertEqual(-1.0, Eval("-7 % 3").Number, "% is not the truncated (JS) remainder: -7 % 3 must be -1.");
            AssertEqual(2.0, Eval("5 % 3").Number, "% is not the truncated (JS) remainder: 5 % 3 must be 2 (an IEEE remainder gives -1).");
            AssertEqual(2.5, Eval("10 / 4").Number, "Division is not floating point.");
            AssertTrue(double.IsPositiveInfinity(Eval("1 / 0").Number), "1 / 0 is not Infinity, as JS makes it.");
            AssertEqual(5.0, Eval("10 - 3 - 2").Number, "- is not left-associative.");
            AssertEqual(0x10, (int)Eval("0x10").Number, "Hex literals are not read.");
            AssertEqual(1500.0, Eval("1.5e3").Number, "Exponent literals are not read.");
        }

        private static void TestSafeScriptLogicAndComparison()
        {
            AssertTrue(Eval("true || false && false").IsTruthy, "&& does not bind tighter than ||.");
            AssertFalse(Eval("(true || false) && false").IsTruthy, "Grouped logic is wrong.");
            AssertEqual("x", Eval("'' || 'x'").Text, "|| does not return its operand, as JS does.");
            AssertEqual(0.0, Eval("0 && 1").Number, "&& does not return its falsy left operand.");
            AssertTrue(Eval("!0").IsTruthy && !Eval("!1").IsTruthy, "! is not JS truthiness.");
            AssertTrue(Eval("2 <= 2").IsTruthy && !Eval("2 < 2").IsTruthy, "The <= / < boundary is wrong.");
            AssertTrue(Eval("3 >= 3").IsTruthy && !Eval("3 > 3").IsTruthy, "The >= / > boundary is wrong.");
            AssertTrue(Eval("'a' < 'b'").IsTruthy, "Strings do not compare ordinally.");
            AssertFalse(Eval("NaN < 1").IsTruthy || Eval("NaN >= 1").IsTruthy, "A NaN comparison is not false.");
            AssertTrue(Eval("1 == '1'").IsTruthy && !Eval("1 === '1'").IsTruthy, "== is not loose or === is not strict.");
            AssertTrue(Eval("true == 1").IsTruthy && Eval("null == null").IsTruthy && !Eval("null == 0").IsTruthy, "Loose equality of bool/number or null is wrong.");
            AssertTrue(Eval("1 != 2").IsTruthy && !Eval("2 !== 2").IsTruthy, "!= or !== is wrong.");
            AssertEqual("b", Eval("1 > 2 ? 'a' : 'b'").Text, "The conditional operator picks the wrong arm.");
            AssertEqual("c", Eval("false ? 'a' : false ? 'b' : 'c'").Text, "Nested conditionals do not associate to the right.");
            AssertEqual("3x", Eval("1 + 2 + 'x'").Text, "+ does not go left to right before concatenating.");
            AssertEqual("n1", Eval("'n' + 1").Text, "+ does not concatenate when one side is a string.");
        }

        private static void TestSafeScriptObjectModel()
        {
            var c = new FakeScriptContext();
            AssertTrue(Eval("mascot.anchor.x < mascot.environment.workArea.right - 100", c).IsTruthy, "anchor/workArea members are not read.");
            AssertTrue(Eval("mascot.environment.floor.isOn(mascot.anchor)", c).IsTruthy, "The contextual floor test does not see the anchor on the floor.");
            AssertEqual(ScriptBorderId.Floor, c.BorderQueries[c.BorderQueries.Count - 1], "floor.isOn did not ask the context about the FLOOR.");
            c.AnchorX = 0;
            AssertTrue(Eval("mascot.environment.workArea.leftBorder.isOn(mascot.anchor)", c).IsTruthy, "workArea.leftBorder.isOn does not ask about the work area's left border.");
            AssertEqual(ScriptBorderId.WorkAreaLeft, c.BorderQueries[c.BorderQueries.Count - 1], "leftBorder resolved to the wrong border id.");
            AssertTrue(Eval("mascot.environment.activeIE.visible && mascot.environment.activeIE.width == 400", c).IsTruthy, "activeIE members are not the active window's.");
            AssertTrue(Eval("mascot.environment.activeIE.contains(200, 200)", c).IsTruthy && !Eval("mascot.environment.activeIE.contains(50, 50)", c).IsTruthy, "Area.contains is wrong.");
            AssertEqual(3.0, Eval("mascot.environment.cursor.dx", c).Number, "cursor.dx is not read.");
            AssertTrue(Eval("mascot.totalCount < 50 && mascot.count == 2", c).IsTruthy, "count / totalCount are not read.");
            AssertFalse(Eval("mascot.lookRight", c).IsTruthy, "lookRight is not read.");
            c.Right = true;
            AssertTrue(Eval("mascot.lookRight", c).IsTruthy, "lookRight does not follow the context.");
            AssertEqual(680.0, Eval("mascot.environment.floor.y", c).Number, "A border's y is not read.");
            AssertFalse(Eval("mascot.dragging", c).IsTruthy, "dragging read true for a companion not being dragged.");
            c.IsDragging = true;
            AssertTrue(Eval("mascot.dragging", c).IsTruthy, "dragging does not follow the context.");
        }

        private static void TestSafeScriptVariablesAndNames()
        {
            var c = new FakeScriptContext();
            AssertEqual(5.0, Eval("mascot.variables['k'] = 5", c).Number, "An index assignment does not yield its value.");
            AssertEqual(5.0, c.Vars["k"].Number, "An index assignment was not stored.");
            AssertEqual(6.0, Eval("mascot.variables.get('k') + 1", c).Number, "variables.get does not read the stored value.");
            Eval("mascot.variables.put('j', 'x')", c);
            AssertEqual("x", c.Vars["j"].Text, "variables.put was not stored.");
            AssertEqual(ScriptValueKind.Null, Eval("mascot.variables['missing']", c).Kind, "A missing variable is not null.");
            Func<string, bool> scope = n => n == "TargetX" || n == "Speed";
            c.Names["TargetX"] = ScriptValue.FromNumber(300);
            AssertEqual(310.0, Eval("TargetX + 10", c, scope).Number, "A parameter in scope is not read through the context.");
            AssertTrue(ParseError("TargetY + 1", scope).Contains("unknown name 'TargetY'"), "An out-of-scope name is not refused by name.");
            AssertEqual(ScriptValueKind.Null, Eval("Speed", c, scope).Kind, "A scoped name with no value is not null.");
            AssertEqual(1, c.Faults.Count, "A scoped name with no value was not reported as a fault exactly once.");
        }

        /// <summary>N-audit-1106-026: what evaluations build is bounded, not only what a parse costs. A string doubled through
        /// mascot.variables every tick, or a new name every tick, used to grow without limit; both now fault at a fixed cap
        /// and leave the stored state as it was. WITNESSES: a string of exactly the cap, and rewriting a name at the
        /// name cap, still work.</summary>
        private static void TestSafeScriptEvaluationBounds()
        {
            var c = new FakeScriptContext();
            int cap = SafeScript.MaximumStringLength;
            c.Vars["s"] = ScriptValue.FromString(new string('a', cap - 1));
            AssertEqual(cap, Eval("mascot.variables['s'] + 'b'", c).Text.Length, "WITNESS: a string of exactly the cap was refused.");
            AssertEqual(0, c.Faults.Count, "WITNESS: a string of exactly the cap was reported as a fault.");
            c.Vars["s"] = ScriptValue.FromString(new string('a', cap));
            AssertEqual(ScriptValueKind.Null, Eval("mascot.variables['s'] + 'b'", c).Kind, "A string one past the cap was built.");
            AssertTrue(c.Faults.Count == 1 && c.Faults[0].Contains("limit is " + cap), "A string past the cap was not reported as a fault naming the limit.");

            c.Faults.Clear();
            c.Vars["s"] = ScriptValue.FromString("ab");
            ScriptProgram doubling = ParseOrFail("mascot.variables['s'] = mascot.variables['s'] + mascot.variables['s']");
            for (int tick = 0; tick < 40; tick++) doubling.Evaluate(c);
            AssertTrue(c.Vars["s"].Text.Length <= cap && c.Vars["s"].Text.Length >= cap / 2,
                "A string doubled every tick was not held at the cap (" + c.Vars["s"].Text.Length + " characters).");
            AssertTrue(c.Faults.Count > 0, "A string doubled past the cap was not reported.");

            var names = new FakeScriptContext();
            ScriptProgram fresh = ParseOrFail("mascot.variables['k' + n] = 1", name => name == "n");
            for (int i = 0; i < SafeScript.MaximumVariables; i++)
            {
                names.Names["n"] = ScriptValue.FromNumber(i);
                fresh.Evaluate(names);
            }
            AssertEqual(SafeScript.MaximumVariables, names.Vars.Count, "Names up to the cap were not all stored.");
            AssertEqual(0, names.Faults.Count, "WITNESS: names up to the cap were reported as faults.");
            names.Names["n"] = ScriptValue.FromNumber(SafeScript.MaximumVariables);
            fresh.Evaluate(names);
            AssertTrue(names.Vars.Count == SafeScript.MaximumVariables && !names.Vars.ContainsKey("k" + SafeScript.MaximumVariables),
                "A name past the cap was stored (" + names.Vars.Count + " names).");
            AssertTrue(names.Faults.Count == 1 && names.Faults[0].Contains("a new name is refused"), "A name past the cap was not reported as a fault.");
            names.Names["n"] = ScriptValue.FromNumber(3);
            AssertEqual(1.0, fresh.Evaluate(names).Number, "WITNESS: rewriting a name already held was refused at the cap.");
        }

        private static void TestSafeScriptRandomAndMath()
        {
            var c = new FakeScriptContext();
            c.Draws.Enqueue(0.9);
            ScriptProgram p = ParseOrFail("Math.random() < 0.5 && Math.random() < 0.5");
            AssertEqual(2, p.RandomSites.Count, "The program does not list both Math.random sites.");
            AssertFalse(p.EvaluateCondition(c), "0.9 < 0.5 was read as true.");
            AssertEqual(1, c.DrawSites.Count, "Short-circuit && still drew for its right side: a skipped site must not consume a draw.");
            c.Draws.Enqueue(0.1); c.Draws.Enqueue(0.2);
            AssertTrue(p.EvaluateCondition(c), "Two draws under 0.5 were not read as true.");
            AssertEqual(3, c.DrawSites.Count, "Each evaluated site must draw exactly once.");
            AssertTrue(c.DrawSites[1] != c.DrawSites[2], "Two sites reported the same offset.");
            AssertEqual(1.0, Eval("Math.min(3, 1, 2)").Number, "Math.min is wrong.");
            AssertEqual(3.0, Eval("Math.round(2.5)").Number, "Math.round(2.5) is not 3.");
            AssertEqual(-2.0, Eval("Math.round(-2.5)").Number, "Math.round(-2.5) is not -2, as JS rounds half up.");
            AssertEqual(-2.0, Eval("Math.floor(-1.5)").Number, "Math.floor is wrong.");
            AssertEqual(4.0, Eval("Math.abs(-4)").Number, "Math.abs is wrong.");
            AssertEqual(Math.PI, Eval("Math.PI").Number, "Math.PI is wrong.");
            AssertTrue(ParseError("Math.random(1)").Contains("Math.random takes no arguments"), "Math.random with an argument was not refused.");
            AssertTrue(ParseError("Math.eval(1)").Contains("Math.eval"), "An unknown Math function was not refused by name.");
        }

        private static void TestSafeScriptRefusalsAndFaults()
        {
            AssertTrue(ParseError("mascot.imageSet").Contains("'mascot.imageSet'"), "A member the source has but the model does not run was not refused by its path.");
            AssertTrue(ParseError("window.close()").Contains("unknown name 'window'"), "A name outside the model was not refused.");
            AssertTrue(ParseError("1 = 2").Contains("can be assigned"), "Assignment to a literal was not refused.");
            AssertEqual(ScriptValueKind.Point, Eval("mascot.anchor").Kind, "The anchor is a point value, not an error.");
            AssertTrue(ParseError("mascot.environment").Contains("is an object, not a value"), "An object used as a value was not refused.");
            AssertTrue(ParseError(new string('1', SafeScript.MaximumLength + 1)).Contains("longer than"), "An over-long expression was not refused.");
            AssertTrue(ParseError(new string('(', 40) + "1" + new string(')', 40)).Contains("nested deeper"), "Over-deep nesting was not refused.");
            var many = new StringBuilder("1");
            for (int i = 0; i < 600; i++) many.Append("+1");
            AssertTrue(ParseError(many.ToString()).Contains("tokens"), "An expression over the token limit was not refused.");
            AssertTrue(ParseError("'open").Contains("unterminated string"), "An unterminated string was not refused.");
            AssertTrue(ParseError("1 +").Length > 0, "A dangling operator was not refused.");

            char bs = (char)92;
            AssertEqual("a'b", Eval("'a" + bs + "'b'").Text, "An escaped quote inside a string is not read.");

            var c = new FakeScriptContext();
            ScriptProgram fault = ParseOrFail("mascot.environment.floor.isOn(1)");
            AssertFalse(fault.EvaluateCondition(c), "A faulting condition was not false.");
            AssertEqual(1, c.Faults.Count, "A runtime fault was not reported exactly once.");

            // One tree, two companions: the tree is shared and holds no evaluation state.
            ScriptProgram shared = ParseOrFail("mascot.anchor.x > 500");
            var left = new FakeScriptContext { AnchorX = 100 };
            var right = new FakeScriptContext { AnchorX = 900 };
            AssertTrue(!shared.EvaluateCondition(left) && shared.EvaluateCondition(right) && !shared.EvaluateCondition(left),
                "A shared program carried state from one companion's evaluation to another's.");
        }

        /// <summary>The places where a JS engine (Nashorn, ES5.1) and a naive C# reading disagree, each pinned by the
        /// input that tells them apart.</summary>
        private static void TestSafeScriptJsEdges()
        {
            // A fault ABORTS the expression: nothing after it runs, so it draws nothing and writes nothing.
            var c = new FakeScriptContext();
            AssertEqual(ScriptValueKind.Null, Eval("mascot.environment.floor.isOn(1) || Math.random() < 2", c).Kind, "A faulting expression did not yield null.");
            AssertEqual(1, c.Faults.Count, "The fault was not reported exactly once.");
            AssertEqual(0, c.DrawSites.Count, "A fault did not abort the expression: the random site after it still drew.");
            Eval("mascot.environment.floor.isOn(1) || (mascot.variables['w'] = 1)", c);
            AssertFalse(c.Vars.ContainsKey("w"), "A fault did not abort the expression: the variable write after it still ran.");

            // .put returns the previous value (null for a new key); an index assignment returns what it wrote.
            var v = new FakeScriptContext();
            AssertEqual(ScriptValueKind.Null, Eval("mascot.variables.put('k', 5)", v).Kind, "put on a new key did not return null, as VariableMap.put does.");
            AssertEqual(5.0, Eval("mascot.variables.put('k', 6)", v).Number, "put on an existing key did not return the previous value.");
            AssertEqual(7.0, Eval("mascot.variables['k'] = 7", v).Number, "An index assignment did not return the value written.");

            // Area.contains ignores visibility: a hidden window keeps its geometry.
            var hidden = new FakeScriptContext();
            hidden.ActiveWindow.Visible = false;
            AssertTrue(Eval("mascot.environment.activeIE.contains(200, 200)", hidden).IsTruthy, "contains on a hidden window was false; the source ignores visibility.");

            AssertTrue(Eval("undefined == null").IsTruthy && !Eval("undefined === null").IsTruthy, "undefined is not loosely equal and strictly unequal to null.");

            // Hex literals are unsigned, and one too long for a ulong is refused by name rather than overflowing.
            AssertEqual(18446744073709551615.0, Eval("0xffffffffffffffff").Number, "0xffffffffffffffff did not read as 18446744073709551615 (a signed read gives -1).");
            AssertTrue(ParseError("0x1ffffffffffffffff").Contains("hex number too large"), "A 17-digit hex literal was not refused by name.");

            // A leading-zero integer is legacy octal in the source engine (010 is 8), so it is refused by name rather than
            // read as decimal 10 (N-audit-1106-245). A lone zero, a zero fraction and a zero exponent still read.
            AssertTrue(ParseError("010").Contains("legacy octal numbers"), "010 was not refused by name (decimal reads it as 10, the source engine as 8).");
            AssertTrue(ParseError("1 + 08").Contains("legacy octal numbers"), "08 after an operator was not refused by name.");
            AssertEqual(0.5, Eval("0.5").Number, "WITNESS: 0.5 no longer reads.");
            AssertEqual(10.0, Eval("0 + 10 + 0e1").Number, "WITNESS: a lone 0, 10 and 0e1 no longer read.");

            // String to number is ES5.1's: hex strings read, Infinity is case-sensitive, junk is NaN, blank is 0.
            AssertEqual(16.0, Eval("'0x10' * 1").Number, "'0x10' did not convert to 16.");
            AssertEqual(12.0, Eval("' 12 ' * 1").Number, "A padded decimal string did not convert.");
            AssertTrue(double.IsNaN(Eval("'infinity' * 1").Number), "'infinity' converted; JS reads only 'Infinity'.");
            AssertTrue(double.IsPositiveInfinity(Eval("'Infinity' * 1").Number), "'Infinity' did not convert.");
            AssertTrue(double.IsNaN(Eval("'-0x10' * 1").Number), "'-0x10' converted; a signed hex string is NaN in JS.");
            AssertTrue(double.IsNaN(Eval("'12px' * 1").Number), "'12px' converted; JS reads it as NaN.");
            AssertEqual(0.0, Eval("'' * 1").Number, "A blank string did not convert to 0.");

            // Escapes: \b \f \v \0 \xHH mean what JS says; octal escapes are refused.
            char bs = (char)92;
            AssertEqual("\b", Eval("'" + bs + "b'").Text, "An escaped b is not a backspace.");
            AssertEqual("A", Eval("'" + bs + "x41'").Text, "An escaped x41 is not 'A'.");
            AssertEqual("\0", Eval("'" + bs + "0'").Text, "An escaped 0 is not NUL.");
            AssertTrue(ParseError("'" + bs + "1'").Contains("octal escapes"), "An octal escape was not refused.");

            // % keeps the dividend's sign, -0 included, and never overflows a quotient.
            AssertTrue(double.IsNegativeInfinity(Eval("1 / (-6 % 3)").Number), "-6 % 3 is not -0.");
            double r = Eval("1e308 % 1e-308").Number;
            AssertTrue(!double.IsNaN(r) && r >= 0 && r < 1e-308, "1e308 % 1e-308 is not the exact small remainder (a quotient overflowed).");

            // Math.round: -0 for -0.5 <= x < 0, large integers unchanged, 0.49999999999999994 rounds down.
            AssertTrue(double.IsNegativeInfinity(Eval("1 / Math.round(-0.1)").Number), "Math.round(-0.1) is not -0.");
            AssertEqual(4503599627370497.0, Eval("Math.round(4503599627370497)").Number, "Math.round moved an integer above 2^52.");
            AssertEqual(0.0, Eval("Math.round(0.49999999999999994)").Number, "Math.round(0.49999999999999994) is not 0.");

            // Number to string is JS's: plain from 1e-6 to 1e21, exponent written e+n outside, -0 is "0".
            AssertEqual("1e+21", Eval("'' + 1e21").Text, "1e21 is not written 1e+21.");
            AssertEqual("100000000000000000000", Eval("'' + 1e20").Text, "1e20 is not written out in full.");
            AssertEqual("1000000000000000", Eval("'' + 1e15").Text, "1e15 is not written out in full (.NET writes 1E+15).");
            AssertEqual("0.000001", Eval("'' + 0.000001").Text, "0.000001 is not written plainly.");
            AssertEqual("1.5e-7", Eval("'' + 1.5e-7").Text, "1.5e-7 is not written 1.5e-7.");
            AssertEqual("123.45", Eval("'' + 123.45").Text, "123.45 is not written plainly.");
            AssertEqual("0", Eval("'' + (-6 % 3)").Text, "-0 is not written 0.");
        }

        // ---- the statement grammar and the extended object model (SafeScript SPEC rules 1-10), synthetic scripts only

        /// <summary>Rule 1: a program is statements separated by ';', its value the last value-producing statement's.</summary>
        private static void TestSafeScriptStatements()
        {
            var c = new FakeScriptContext();
            AssertTrue(Eval("mascot.variables['k'] = 0; mascot.variables['j'] = 1; true", c).IsTruthy, "A statement sequence did not yield its last value.");
            AssertTrue(c.Vars["k"].Number == 0 && c.Vars["j"].Number == 1, "The statements before the last did not run in order.");

            // No final expression: the trailing ';' is an empty statement, so the completion is the last assignment's 0.
            ScriptProgram noFinal = ParseOrFail("mascot.variables['k'] ++; mascot.variables['a'] = 0;");
            ScriptValue v = noFinal.Evaluate(c);
            AssertTrue(v.Kind == ScriptValueKind.Number && v.Number == 0, "A program ending in ';' did not complete with its last statement's value (0).");
            AssertFalse(noFinal.EvaluateCondition(c), "A completion value of 0 read as a true condition.");

            // A trailing postfix statement completes with the OLD number (oracle: a Duration of n++; is 3, then 4).
            var d = new FakeScriptContext();
            d.Vars["n"] = ScriptValue.FromNumber(3);
            ScriptProgram post = ParseOrFail("mascot.variables['n']++;");
            AssertEqual(3.0, post.Evaluate(d).Number, "A trailing postfix statement did not complete with the old value.");
            AssertEqual(4.0, d.Vars["n"].Number, "The postfix statement did not store the new value.");

            AssertEqual(ScriptValueKind.Null, Eval("var x = 1;", c).Kind, "A program with no value-producing statement did not reach the caller as null (undefined).");
            AssertEqual(ScriptValueKind.Null, Eval(";", c).Kind, "An empty program did not reach the caller as null.");
            AssertTrue(Eval(";; true ;;", c).IsTruthy, "Empty statements around an expression changed its value.");
            AssertEqual(2.0, Eval("mascot.variables['z'] = 2; var y = 5", c).Number, "A var statement produced a completion value; it has none.");
            AssertTrue(ParseError("1 2").Contains("unexpected"), "Two expressions with no ';' between them were accepted.");
            AssertTrue(ParseError("if (true) 1").Contains("'if' is outside"), "A keyword statement was not refused by name.");

            // The 'statements' feature: every construct beyond the expression grammar flags it, and none of the old ones do.
            string[] beyond =
            {
                "1;", "var a = 1", "mascot.variables['k']++", "--mascot.variables['k']", "mascot.variables['k'] += 1",
                "TargetX = 1", "undefined", "typeof 1", "mascot.environment.bottom", "mascot.bounds.width",
                "mascot.manager.getMascotWithAffordance('x')", "mascot.anchor.translate(1, 1)", "mascot.variables['k'].get()",
            };
            foreach (string source in beyond)
                AssertTrue(ParseOrFail(source, n => n == "TargetX").UsesStatements, "'" + source + "' did not flag the statements feature.");
            string[] within = { "1 + 2", "mascot.variables['k'] = 1", "mascot.variables.put('k', 1)", "TargetX > 1 ? 'a' : 'b'", "mascot.anchor" };
            foreach (string source in within)
                AssertFalse(ParseOrFail(source, n => n == "TargetX").UsesStatements, "'" + source + "' flagged the statements feature; the expression grammar runs it.");
        }

        /// <summary>Rule 2: var and bare-name writes go to the store the context gives, read before the bindings, hoisted.</summary>
        private static void TestSafeScriptGlobals()
        {
            // The self-referencing first declaration: marker exists, undefined, while its initializer runs.
            ScriptProgram marker = ParseOrFail("var marker = typeof marker == 'undefined' ? 1 : marker + 1; mascot.variables['declared'] = marker; true");
            var c = new FakeScriptContext();
            marker.Evaluate(c);
            AssertEqual(1.0, c.Vars["declared"].Number, "A first declaration did not see its own name as undefined.");
            marker.Evaluate(c);
            AssertEqual(2.0, c.Vars["declared"].Number, "A second evaluation in the same store did not see the global the first left.");
            var fresh = new FakeScriptContext();
            marker.Evaluate(fresh);
            AssertEqual(1.0, fresh.Vars["declared"].Number, "A fresh store saw another store's global.");

            // A sibling script reads the global through the scope its caller builds from the declaring script's Globals.
            AssertEqual("marker", string.Join(",", marker.Globals), "Globals did not list the declared name.");
            AssertTrue(ParseError("marker + 1").Contains("unknown name 'marker'"), "A name no script declares and no scope holds was not refused.");
            var declaredBySibling = new HashSet<string>(marker.Globals, StringComparer.Ordinal);
            ScriptProgram sibling = ParseOrFail("typeof marker == 'undefined' ? -1 : marker", declaredBySibling.Contains);
            AssertEqual(2.0, sibling.Evaluate(c).Number, "A sibling script did not read the shared global.");
            AssertEqual(-1.0, sibling.Evaluate(new FakeScriptContext()).Number, "A sibling script in a store without the global did not read it as undefined.");
            AssertEqual(-1.0, Eval("typeof other == 'undefined' ? -1 : 1").Number, "typeof of a name nothing declares was not 'undefined'.");
            // A name the program tests with typeof is the program's to read (another action's global, guarded by the
            // source itself): no parse error, "undefined" in a store without it, its value in one with it.
            ScriptProgram guarded = ParseOrFail("mascot.variables['other'] = typeof marker == 'undefined' ? -1 : marker; true");
            var without = new FakeScriptContext();
            guarded.Evaluate(without);
            AssertTrue(without.Vars["other"].Number == -1 && without.Faults.Count == 0, "A typeof-guarded read of an absent global did not take the guard.");
            guarded.Evaluate(c);
            AssertEqual(2.0, c.Vars["other"].Number, "A typeof-guarded read did not read the global where it exists.");
            var unguarded = new FakeScriptContext();
            AssertEqual(ScriptValueKind.Null, Eval("typeof marker == 'string' ? 1 : marker", unguarded).Kind, "An unresolved read past the guard did not fault.");
            AssertTrue(unguarded.Faults.Count == 1, "An unresolved read past the guard was not reported (the source's ReferenceError).");
            AssertTrue(ParseError("typeof action").Contains("engine's global object"), "typeof action (the source's own binding) was not refused.");
            AssertTrue(ParseError("typeof Java").Contains("engine's global object"), "typeof of an engine global was not refused.");

            // A global shadows a binding of the same name; the binding itself is never written.
            var p = new FakeScriptContext();
            p.Names["count"] = ScriptValue.FromNumber(0);
            Func<string, bool> scope = n => n == "count";
            ScriptProgram once = ParseOrFail("count++ == 0", scope);
            AssertTrue(once.EvaluateCondition(p), "count++ == 0 was false on its first evaluation.");
            AssertFalse(once.EvaluateCondition(p), "count++ == 0 was true again: the parameter was read again instead of the global.");
            AssertTrue(p.Names["count"].Number == 0 && p.Globals["count"].Number == 2, "The update wrote the binding, or the global did not carry.");
            AssertEqual(7.0, Eval("x = 7; x", p).Number, "An assigned bare name was not readable later in the program.");
            // Hoisting: a var shadows the binding of its name from the program's start, so its initializer reads undefined.
            var h = new FakeScriptContext();
            h.Names["speed"] = ScriptValue.FromNumber(5);
            AssertTrue(double.IsNaN(Eval("var speed = speed + 1; speed", h, n => n == "speed").Number), "A var did not shadow the binding before its initializer ran (hoisting).");
            var h2 = new FakeScriptContext();
            h2.Names["speed"] = ScriptValue.FromNumber(5);
            AssertEqual(6.0, Eval("speed = speed + 1; speed", h2, n => n == "speed").Number, "WITNESS: an undeclared assignment did not read the binding first.");
            AssertEqual(0, p.Faults.Count, "WITNESS: the global reads reported faults.");
            AssertTrue(ParseError("mascot = 1").Length > 0 && ParseError("var mascot = 1").Contains("cannot be assigned"), "Assigning or declaring mascot was not refused.");
            AssertTrue(ParseError("var Math = 1").Contains("cannot be assigned"), "Declaring Math was not refused.");

            // Rule 10: at most MaximumGlobals names per store; rewriting a held one is always allowed.
            var full = new FakeScriptContext();
            for (int i = 0; i < SafeScript.MaximumGlobals; i++) full.Globals["g" + i] = ScriptValue.FromNumber(i);
            AssertEqual(ScriptValueKind.Null, Eval("fresh = 1", full).Kind, "A global past the cap was written.");
            AssertTrue(full.Faults.Count == 1 && full.Faults[0].Contains("a new name is refused") && !full.Globals.ContainsKey("fresh"),
                "A global past the cap was not reported, or was stored.");
            AssertEqual(5.0, Eval("g3 = 5", full, n => n == "g3").Number, "WITNESS: rewriting a held global was refused at the cap.");
        }

        /// <summary>Rule 3: ++ -- and op= on mascot.variables, names and missing keys.</summary>
        private static void TestSafeScriptUpdates()
        {
            var c = new FakeScriptContext();
            AssertEqual(0.0, Eval("mascot.variables['n']++", c).Number, "A postfix ++ on a missing key did not yield ToNumber(null), 0.");
            AssertEqual(1.0, c.Vars["n"].Number, "null++ did not store 1.");
            AssertEqual(2.0, Eval("++mascot.variables['n']", c).Number, "A prefix ++ did not yield the new value.");
            Eval("mascot.variables['n']--; mascot.variables['n']--; true", c);
            AssertEqual(0.0, c.Vars["n"].Number, "Two postfix decrements did not take 2 off.");
            AssertEqual(-1.0, Eval("--mascot.variables['n']", c).Number, "A prefix -- did not yield the new value.");
            c.Vars["s"] = ScriptValue.FromString("4");
            AssertEqual(4.0, Eval("mascot.variables['s']++", c).Number, "A postfix ++ on a string did not yield ToNumber of it.");
            AssertEqual(ScriptValueKind.Number, c.Vars["s"].Kind, "A string ++ did not store a number.");
            AssertTrue(double.IsNaN(Eval("mascot.variables['u'] = undefined; mascot.variables['u']++; mascot.variables['u']", c).Number), "undefined++ did not store NaN.");

            c.Vars["t"] = ScriptValue.FromNumber(10);
            AssertEqual(13.0, Eval("mascot.variables['t'] += 3", c).Number, "+= did not yield the sum.");
            AssertEqual(26.0, Eval("mascot.variables['t'] *= 2", c).Number, "*= is wrong.");
            AssertEqual(20.0, Eval("mascot.variables['t'] -= 6", c).Number, "-= is wrong.");
            AssertEqual(5.0, Eval("mascot.variables['t'] /= 4", c).Number, "/= is wrong.");
            AssertEqual(1.0, Eval("mascot.variables['t'] %= 2", c).Number, "%= is wrong.");
            AssertEqual("ab", Eval("mascot.variables['w'] = 'a'; mascot.variables['w'] += 'b'", c).Text, "+= on a string did not concatenate.");
            AssertEqual(6.0, Eval("x = 2; x *= 3", c).Number, "op= on a script global is wrong.");

            // The key is read once: a random key draws once for an update.
            var r = new FakeScriptContext();
            Eval("mascot.variables['k' + Math.random()]++", r);
            AssertEqual(1, r.DrawSites.Count, "An update evaluated its key more than once.");

            AssertTrue(ParseError("mascot.anchor.x = 1").Contains("can be assigned"), "Assigning a point member was not refused.");
            AssertTrue(ParseError("mascot.variables.get('k') = 1").Contains("can be assigned"), "Assigning a call was not refused.");
            AssertTrue(ParseError("1++").Contains("can be assigned"), "Updating a literal was not refused.");
            AssertTrue(ParseError("1--1").Length > 0, "1--1 was read as 1 - -1; JS refuses it.");
            AssertTrue(ParseError("mascot.variables['empty']++").Contains("map's own property"), "A key the source links to VariableMap.isEmpty was not refused.");
            AssertEqual(1.0, Eval("mascot.variables['empty2'] = 1").Number, "WITNESS: an ordinary key that starts like a map property was refused.");
            AssertEqual(2.0, Eval("1 - -1").Number, "WITNESS: 1 - -1 no longer reads.");
        }

        /// <summary>Rule 4: undefined, typeof, and absent Java properties read as undefined.</summary>
        private static void TestSafeScriptUndefined()
        {
            AssertTrue(double.IsNaN(Eval("undefined + 1").Number), "undefined + 1 is not NaN.");
            AssertEqual("aundefined", Eval("'a' + undefined").Text, "A string plus undefined did not concatenate 'undefined'.");
            AssertTrue(Eval("!undefined").IsTruthy, "undefined is not falsy.");
            AssertFalse(Eval("undefined < 1").IsTruthy || Eval("undefined >= 1").IsTruthy, "A comparison with undefined was true.");
            AssertTrue(Eval("undefined === undefined").IsTruthy && !Eval("undefined == 0").IsTruthy, "Equality of undefined is wrong.");
            AssertEqual("undefined", Eval("typeof undefined").Text, "typeof undefined is wrong.");
            AssertEqual("object", Eval("typeof null").Text, "typeof null is not 'object'.");
            AssertEqual("number", Eval("typeof 1").Text, "typeof of a number is wrong.");
            AssertEqual("string", Eval("typeof 'a'").Text, "typeof of a string is wrong.");
            AssertEqual("boolean", Eval("typeof (1 < 2)").Text, "typeof of a boolean is wrong.");
            AssertEqual("object", Eval("typeof mascot.bounds").Text, "typeof of a Java object is not 'object'.");

            var c = new FakeScriptContext();
            ScriptProgram absent = ParseOrFail("mascot.environment.activeIE.bottom < mascot.environment.bottom");
            AssertFalse(absent.EvaluateCondition(c), "A comparison with an absent property was true.");
            AssertEqual(0, c.Faults.Count, "An absent property read was reported as a fault.");
            AssertEqual("mascot.environment.bottom", string.Join(",", absent.AbsentProperties), "The absent property was not recorded by its path.");
            AssertEqual("undefined", Eval("typeof mascot.environment.bottom", c).Text, "typeof of an absent property is not 'undefined'.");
            AssertEqual(0, ParseOrFail("mascot.environment.activeIE.bottom").AbsentProperties.Count, "WITNESS: a modelled property was recorded as absent.");
            AssertTrue(ParseError("mascot.environment.activeIETitle").Contains("not part of the object model"), "A present but unmodelled property read as undefined.");
            AssertTrue(ParseError("mascot.environment.bottom()").Contains("not part of the object model"), "A call of an absent method was not refused.");
            AssertTrue(ParseError("mascot.getAnchor").Contains("not part of the object model"), "An accessor name read as an absent property.");
            AssertTrue(ParseError("mascot.environment.bottom.x").Contains("not part of the object model"), "A chain past an absent property (always a TypeError) was not refused.");
            AssertEqual(ScriptValueKind.Null, Eval("var u = mascot.environment.bottom; u.x", c).Kind, "A member read on undefined did not fault.");
            AssertTrue(c.Faults.Count == 1 && c.Faults[0].Contains("TypeError"), "A member read on undefined was not reported as the source's TypeError.");
        }

        /// <summary>Rules 5-7 and 9: the affordance lookup, weak and mascot references, bounds, and member chains on values.</summary>
        private static void TestSafeScriptReferences()
        {
            var c = new FakeScriptContext { AnchorX = 640, AnchorY = 460 };
            var beta = new FakeMascot { Name = "beta", X = 668, Y = 476, Right = true };
            beta.Offers.Add("Signal");
            c.Manager.Add(c.Me);
            c.Manager.Add(beta);
            Func<string, bool> none = n => false;
            const string Find = "var peer = mascot.manager.getMascotWithAffordance('Signal'); ";
            const string Live = "peer != null && peer.get() != null";

            // Peer: Alpha does not offer, so Beta is found.
            AssertTrue(Eval(Find + Live, c, none).IsTruthy, "An offering peer was not found.");
            AssertEqual(668.0, Eval(Find + Live + " ? peer.get().anchor.x : -1", c, none).Number, "The peer's anchor x was not read.");
            AssertEqual(476.0, Eval(Find + Live + " ? peer.get().anchor.y : -1", c, none).Number, "The peer's anchor y was not read.");
            AssertTrue(Eval(Find + Live + " ? peer.get().lookRight : false", c, none).IsTruthy, "The peer's facing was not read.");
            // Self wins: the asking companion is first in manager order and now offers too.
            c.Me.Offers.Add("Signal");
            AssertEqual(640.0, Eval(Find + Live + " ? peer.get().anchor.x : -1", c, none).Number, "The asking companion did not win as first in manager order.");
            // Manager order decides, not who asks.
            c.Manager.Reverse();
            AssertEqual(668.0, Eval(Find + Live + " ? peer.get().anchor.x : -1", c, none).Number, "The first offering companion in manager order was not chosen.");
            // Miss: null, and the guard takes the fallback with no fault.
            var miss = new FakeScriptContext();
            AssertEqual(-1.0, Eval(Find + Live + " ? peer.get().anchor.x : -1", miss, none).Number, "A miss did not take the fallback.");
            AssertEqual(0, miss.Faults.Count, "WITNESS: a guarded miss was reported as a fault.");
            AssertEqual(ScriptValueKind.Null, Eval(Find + "peer.get()", miss, none).Kind, "A call on a missed (null) reference did not fault.");
            AssertTrue(miss.Faults.Count == 1 && miss.Faults[0].Contains("TypeError"), "A call on null was not the source's TypeError.");
            // A disposed companion reads as a cleared reference.
            beta.Disposed = true;
            c.Me.Offers.Clear();
            AssertFalse(Eval(Find + Live, c, none).IsTruthy, "A disposed companion's reference did not read as cleared.");
            beta.Disposed = false;

            // A reference kept in mascot.variables.
            var v = new FakeScriptContext();
            v.Manager.Add(beta);
            AssertTrue(Eval("mascot.variables['k'] = mascot.manager.getMascotWithAffordance('Signal'); mascot.variables['k'] != null && mascot.variables['k'].get() != null", v).IsTruthy,
                "A reference stored in mascot.variables did not read back.");
            AssertEqual(ScriptValueKind.WeakRef, v.Vars["k"].Kind, "mascot.variables did not hold the weak reference.");
            // The manager held as a value (var m = mascot.getManager(); m.getMascotWithAffordance(...)).
            AssertTrue(Eval("var m = mascot.manager; var r = m.getMascotWithAffordance('Signal'); r != null && r.get() != null", v, none).IsTruthy,
                "A lookup through a manager held in a var did not find the offering companion.");
            AssertEqual("object", Eval("typeof mascot.manager", v).Text, "typeof of the manager is not 'object'.");
            AssertTrue(Eval("mascot.manager === mascot.manager", v).IsTruthy, "Two reads of the manager were not the same object.");
            AssertTrue(ParseError("var m = mascot.manager; m.count").Contains("not part of the object model"), "A present, unmodelled manager property was not refused.");
            AssertFalse(Eval("mascot.manager.getMascotWithAffordance('Signal') === mascot.manager.getMascotWithAffordance('Signal')", v).IsTruthy,
                "Two lookups returned the same reference object.");
            AssertTrue(Eval(Find + "peer.get() === peer.get()", v, none).IsTruthy, "Two gets of one reference were not the same companion.");

            // Bounds: x, y, width, height of the companion's own image, and of a reference.
            var b = new FakeScriptContext { AnchorX = 640, AnchorY = 440 };
            b.Me.Width = 48; b.Me.Height = 40; b.Me.CenterX = 24; b.Me.CenterY = 40;
            AssertEqual(8.0, Eval("mascot.bounds.width - mascot.bounds.height", b).Number, "mascot.bounds width/height are wrong.");
            AssertTrue(Eval("mascot.bounds.x == 616 && mascot.bounds.y == 400", b).IsTruthy, "mascot.bounds x/y are not anchor minus image anchor.");
            AssertEqual(64.0, Eval(Find + "peer.get().bounds.width", v, none).Number, "A reference's bounds were not read.");

            // Members on values: absent reads undefined (recorded), present-but-unmodelled refuses, primitives fault.
            ScriptProgram odd = ParseOrFail(Find + "peer.get().nickname", none);
            AssertEqual(ScriptValueKind.Null, odd.Evaluate(v).Kind, "An absent property of a reference did not read undefined.");
            AssertEqual(".nickname", string.Join(",", odd.AbsentProperties), "An absent property of a value was not recorded.");
            AssertTrue(ParseError(Find + "peer.get().imageSet", none).Contains("not part of the object model"), "A present, unmodelled property of a reference was not refused.");
            AssertTrue(ParseError(Find + "peer.get().dispose()", none).Contains("not part of the object model"), "An unmodelled method of a reference was not refused.");
            var prim = new FakeScriptContext();
            AssertEqual(ScriptValueKind.Null, Eval("var n = 1; n.width", prim, none).Kind, "A member of a number did not fault.");
            AssertEqual(1, prim.Faults.Count, "A member of a number was not reported.");
            AssertTrue(ParseError("var a = 1; a[0]", none).Contains("only mascot.variables can be indexed"), "Indexing a value was not refused.");
        }

        /// <summary>Rule 8: translate moves the live anchor, in argument order, visible to the rest of the evaluation.</summary>
        private static void TestSafeScriptTranslate()
        {
            var c = new FakeScriptContext { AnchorX = 640, AnchorY = 440 };
            AssertEqual(643.0, Eval("mascot.anchor.translate(3, -2); mascot.anchor.x", c).Number, "The move was not visible to the rest of the evaluation.");
            AssertEqual(438.0, c.AnchorY, "translate did not move y.");
            AssertEqual(ScriptValueKind.Null, Eval("mascot.anchor.translate(0, 0)", c).Kind, "translate did not yield undefined.");
            AssertEqual(2, c.Translations, "translate ran a different number of times than it was called.");
            // ToInt32: truncation toward zero, NaN as 0, wrap past 2^32.
            var t = new FakeScriptContext { AnchorX = 0, AnchorY = 0 };
            Eval("mascot.anchor.translate(1.9, -1.9)", t);
            AssertTrue(t.AnchorX == 1 && t.AnchorY == -1, "translate did not truncate toward zero.");
            Eval("mascot.anchor.translate(NaN, 4294967297)", t);
            AssertTrue(t.AnchorX == 1 && t.AnchorY == 0, "translate did not convert NaN to 0 and wrap 2^32 + 1 to 1.");
            // Past the long range Nashorn saturates before narrowing (measured: 1e40 moves x by -1; -1e40 by 0).
            Eval("mascot.anchor.translate(1e40, -1e40)", t);
            AssertTrue(t.AnchorX == 0 && t.AnchorY == 0, "translate did not saturate 1e40 to a long before narrowing (-1), or -1e40 (0).");
            Eval("mascot.anchor.translate(Infinity, -2147483649)", t);
            AssertTrue(t.AnchorX == 0 && t.AnchorY == 2147483647, "translate did not read Infinity as 0 or wrap -2147483649 to 2147483647.");

            // The oracle's translate probe, its first evaluation: count = 0 reads the parameter, moves (0, 6), then count is 1.
            var p = new FakeScriptContext { AnchorX = 640, AnchorY = 440 };
            p.Names["count"] = ScriptValue.FromNumber(0);
            p.Names["Scale"] = ScriptValue.FromNumber(6);
            p.Names["Across"] = ScriptValue.FromNumber(2);
            p.Names["Down"] = ScriptValue.FromNumber(3);
            ScriptProgram probe = ParseOrFail("mascot.anchor.translate(Math.round(Scale * Math.sin(count / Across)), Math.round(Scale * Math.cos(count / Down))); count++; true",
                n => n == "count" || n == "Scale" || n == "Across" || n == "Down");
            AssertTrue(probe.EvaluateCondition(p), "The translate probe did not complete true.");
            AssertTrue(p.AnchorX == 640 && p.AnchorY == 446 && p.Globals["count"].Number == 1, "The first evaluation did not move (0, 6) and leave count 1.");
            probe.EvaluateCondition(p);
            AssertTrue(p.AnchorX == 643 && p.AnchorY == 452, "The second evaluation did not move by count 1 (3, 6): oracle [643,452].");

            // A copied point: translate on it faults rather than moving anything.
            var r = new FakeScriptContext();
            var other = new FakeMascot { X = 5, Y = 5 };
            other.Offers.Add("S");
            r.Manager.Add(other);
            AssertEqual(ScriptValueKind.Null, Eval("var t = mascot.manager.getMascotWithAffordance('S'); t.get().anchor.translate(1, 1)", r, n => false).Kind,
                "translate on a reference's copied anchor did not fault.");
            AssertTrue(r.Faults.Count == 1 && other.X == 5 && r.Translations == 0, "A copied anchor's translate moved something or was not reported.");
        }
    }
}
