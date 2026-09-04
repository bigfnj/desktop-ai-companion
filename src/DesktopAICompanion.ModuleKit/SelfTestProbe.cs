using System;
using System.Text;

namespace DesktopAICompanion.ModuleKit
{
    /// <summary>
    /// The scaffolding a module's engine probe repeats: collect PASS/FAIL lines, remember whether anything
    /// failed, and hand back one report string.
    ///
    /// Why a module needs this shape at all: the host keeps NO compile-time reference to any module, so a
    /// module's self-test is reached by reflection across the AssemblyLoadContext boundary. The convention
    /// the existing probes follow — and the one the host's reflection expects — is a public static entry
    /// point on a public type:
    /// <code>
    /// public static bool Run(out string detail)
    /// {
    ///     var probe = new SelfTestProbe();
    ///     probe.Check("the corpus loaded", corpus.Length &gt; 0);
    ///     return probe.Finish(out detail);
    /// }
    /// </code>
    /// Report a genuine "nothing to test here" with <see cref="Skip"/>: the gate treats a SKIP as a failure
    /// (tests\run-gate.ps1), because a self-test that quietly skips looks exactly like one that passed.
    /// </summary>
    public sealed class SelfTestProbe
    {
        private readonly StringBuilder _report = new StringBuilder();
        private bool _ok = true;

        /// <summary>Record an assertion. Returns the condition, so a caller can short-circuit:
        /// <c>if (!probe.Check("loaded", x != null)) return probe.Finish(out detail);</c></summary>
        public bool Check(string name, bool condition)
        {
            _report.AppendLine((condition ? "PASS: " : "FAIL: ") + name);
            if (!condition) _ok = false;
            return condition;
        }

        /// <summary>Record an assertion whose evaluation may throw; the throw becomes the failure.</summary>
        public bool Check(string name, Func<bool> condition)
        {
            bool result;
            try { result = condition != null && condition(); }
            catch (Exception ex)
            {
                _report.AppendLine("FAIL: " + name + " -- " + ex.GetType().Name + ": " + ex.Message);
                _ok = false;
                return false;
            }
            return Check(name, result);
        }

        /// <summary>A free-text line that is neither a pass nor a fail (context for whoever reads the log).</summary>
        public void Note(string text) { _report.AppendLine("  " + (text ?? "")); }

        /// <summary>Record that the test could not run. The gate FAILS on a SKIP line, on purpose.</summary>
        public void Skip(string reason) { _report.AppendLine("SKIP: " + (reason ?? "")); }

        /// <summary>Record an exception that aborted the run.</summary>
        public void Exception(Exception ex)
        {
            if (ex == null) return;
            _report.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message);
            _ok = false;
        }

        /// <summary>Whether every recorded assertion passed so far.</summary>
        public bool Passed { get { return _ok; } }

        /// <summary>Stamp the RESULT line and hand back the whole report.</summary>
        public bool Finish(out string detail)
        {
            _report.AppendLine(_ok ? "RESULT=PASS" : "RESULT=FAIL");
            detail = _report.ToString();
            return _ok;
        }
    }
}
