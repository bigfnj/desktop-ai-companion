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
            public double Random(int site) { DrawSites.Add(site); return Draws.Count > 0 ? Draws.Dequeue() : 0.5; }
            public void ReportFault(string source, string message) { Faults.Add(message); }
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
            AssertTrue(Eval("true == 1").IsTruthy && Eval("null == undefined").IsTruthy, "Loose equality of bool/number or null/undefined is wrong.");
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
            AssertTrue(ParseError("mascot.foo").Contains("'mascot.foo'"), "An unknown member was not refused by its path.");
            AssertTrue(ParseError("window.close()").Contains("unknown name 'window'"), "A name outside the model was not refused.");
            AssertTrue(ParseError("a = 1", n => n == "a").Contains("only mascot.variables"), "Assignment to anything but a variable was not refused.");
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
    }
}
