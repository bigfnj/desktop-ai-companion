using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DesktopAICompanion.ModuleKit.Testing;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.WindowSoak
{
    /// <summary>
    /// Opens and closes a module-owned WPF window many times and watches whether the process gives the
    /// resources back. This is the one check that can catch an undisposed HWND, Bitmap or decoded sprite sheet
    /// inside a module's own UI -- tests\runtime-resource-soak.ps1 samples the shipped app from outside and its
    /// churn loop never opens a module window at all.
    ///
    /// Pass criteria, in order of how much each one is worth:
    ///   1. every window is UNREACHABLE after an LOH-compacting GC (a rooted Window is the leak that matters,
    ///      and it is the only signal here that is not a heuristic);
    ///   2. OS handles / GDI / USER are flat across the LAST segment;
    ///   3. private bytes show no upward TREND: the least-squares slope of the sample taken after each
    ///      segment, over every segment, stays under a bound (PrivateBytesJudge, at least four segments).
    /// Segment 1's own growth from the cold start is deliberately excluded from 2 and 3: the first pass
    /// legitimately sets a high watermark because the pet's sprite sheet is large and caches fill. Comparing
    /// segments with each other rather than with a cold start is what makes the signal usable -- and it is
    /// precisely what surfaced the re-decode bug, where a debounced re-analyze decoded a ~15 MB sheet on every
    /// keystroke-settle. Private bytes used to be judged like 2, the last segment against the one before, and
    /// that sat inside its own noise (N-release-150-01): see PrivateBytesJudge for the numbers.
    ///
    /// Everything about the module is reached by reflection: PetStudioWindow is `internal sealed`, so there is
    /// nothing to reference at compile time. A missing member is a hard FAIL, never a skip -- a soak that
    /// quietly stops soaking reads exactly like a soak that passed, which has bitten this repo before.
    /// </summary>
    internal static class Program
    {
        private const string DefaultTypeName = "DesktopAICompanion.PetStudioModule.PetStudioWindow";
        private const string DefaultModuleRelativePath =
            @"build\DesktopAICompanionPortable\bin\Release\x64\modules\petstudio\PetStudio.dll";
        // blue_sheep on purpose: a ~1.15 MB file, of which ~206 KB is the base64 of a 154 KB 640x760 sprite
        // sheet and the rest is animation XML. A small pet would not move private bytes enough for signal 3
        // to mean anything, and this is the pet the original re-decode bug was found on.
        private const string DefaultPetRelativePath = @"Companions\blue_sheep\animations.xml";

        // SetLastError, so a 0 -- the Win32 contract for "the call failed" -- can be reported with its reason
        // rather than read as a flat counter (F388).
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
        private const uint GR_GDIOBJECTS = 0;
        private const uint GR_USEROBJECTS = 1;

        [STAThread]
        internal static int Main(string[] args)
        {
            var sb = new StringBuilder();
            try
            {
                Options options = Options.Parse(args);
                if (options.Error != null)
                {
                    Console.Error.WriteLine(options.Error);
                    return 2;
                }

                // The judge on its own, over recorded and synthetic series: no module, no pet, no window, so it
                // runs anywhere this exe builds (the mutation harness grades it; see PrivateBytesJudge.SelfTest).
                if (options.JudgeSelfTestOnly)
                {
                    bool judged = PrivateBytesJudge.SelfTest(sb);
                    sb.AppendLine("RESULT=" + (judged ? "PASS" : "FAIL"));
                    return judged ? 0 : 1;
                }

                Console.WriteLine("module   : " + options.ModulePath);
                Console.WriteLine("type     : " + options.TypeName);
                Console.WriteLine("pet      : " + options.PetPath);
                Console.WriteLine("plan     : " + options.Segments + " segments x " + options.Cycles + " cycles");
                Console.WriteLine();

                if (!File.Exists(options.ModulePath))
                {
                    Console.Error.WriteLine("FAIL: no module DLL at " + options.ModulePath +
                        " (build it first: .\\build.ps1 -Release)");
                    return 1;
                }
                if (!File.Exists(options.PetPath))
                {
                    Console.Error.WriteLine("FAIL: no pet XML at " + options.PetPath);
                    return 1;
                }

                var driver = WindowDriver.Load(options.ModulePath, options.TypeName);
                string petXml = File.ReadAllText(options.PetPath);

                // NO WPF Application, deliberately. This used to create one "because resource lookup throws
                // without it", and that was false twice over: the default ShutdownMode is OnLastWindowClose,
                // so the first cycle's Close() shut it down and cycles 2..N ran with Application.Current null
                // anyway (measured: every recorded 20-cycle run), and the shipped host shows this window with
                // no WPF Application at all (PetStudioModule shows it from a WinForms host; system and theme
                // resource lookup work without one). So the production condition is "none", and creating one
                // measured cycle 1 under a different condition from the rest (F386).

                return Run(driver, petXml, options, sb) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("EXC: " + ex.GetType().Name + ": " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
            finally
            {
                Console.Write(sb.ToString());
            }
        }

        private static bool Run(WindowDriver driver, string petXml, Options options, StringBuilder sb)
        {
            // THE AWAIT NEEDS SOMEWHERE TO COME BACK TO. Since PetStudio 1.1.18 Analyze() is BeginAnalyze: the
            // parse runs on a pool thread behind `await Task.Run` and the render is the continuation, which
            // resumes on the SynchronizationContext captured at the await. On this thread that was null -- no
            // WPF Application (see Main), and no dispatcher frame is running at the call -- so the continuation
            // ran on a pool thread, touched the window from there and took the process down. Measured at
            // 8eea13a on the default pet: EXC on cycle 0 from the Demand in OpenAnalyzeAndClose (the map was
            // still empty when it was read), then "The calling thread cannot access this object" unhandled,
            // exit 0xE0434352 (RA-341). The shipped host's UI thread has a context; this installs the WPF one,
            // so the continuation is posted to this dispatcher and Pump() runs it here, the way the app's own
            // message loop does. Pumping alone does not do it: with a null context the continuation never
            // reaches this thread at all.
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            // ONE host for the whole run, held by this frame, so a handler or responder the window leaves
            // attached on it keeps the window reachable from a live root -- the way the process-long
            // CompanionHost does in the shipped app, where module windows come and go underneath it. This used
            // to be a fresh RecordingHost per cycle under a comment claiming the opposite: after the cycle the
            // only references to that host were the window's own `_host` field and a dead argument array, so a
            // window rooted through `host.HostShutdown += ...` died TOGETHER with its host, `alive[i].IsAlive`
            // read false, and the one leak the WeakReference check exists for was the one it could not see
            // (RA-342; latent, PetStudioWindow subscribes to nothing on IHost today). Measured 2026-09-30 with
            // a scratch `host.HostShutdown += delegate { Close(); };` in the window's constructor, 2 x 5
            // cycles: shared host, "5 still rooted at cycle 0,1,2,3,4" in both segments; per-cycle host, "1
            // still rooted at cycle 4" -- the last argument's lingering stack slot, which is precisely the
            // "only the newest one surviving is WPF holding the most recently shown window" signature the
            // header above calls benign. The per-cycle shape did not hide the leak outright; it disguised it
            // as the one shape a reader is told to wave through.
            IHost host = new RecordingHost();

            // The judge proves itself first, on every soak, over the series it was calibrated on: nothing in the
            // gate runs the soak, so this is the one place a run of it is guaranteed to exercise the judge whose
            // verdict it prints. A judge that misjudges them leaves this run with no private-bytes verdict to give.
            bool ok = PrivateBytesJudge.SelfTest(sb);
            Sample previous = null;
            var privateAfter = new List<long>();

            for (int segment = 1; segment <= options.Segments; segment++)
            {
                var alive = new List<WeakReference>();
                Sample before = Sample.Take();

                for (int cycle = 0; cycle < options.Cycles; cycle++)
                    alive.Add(driver.OpenAnalyzeAndClose(petXml, host));

                Collect();
                Sample after = Sample.Take();
                // The counters must be READABLE before their flatness means anything. GetGuiResources
                // returns 0 on failure, and a 0 -> 0 pair passed both growth checks: a soak that could
                // not measure printed PASS (F388). Asserted on the post-segment sample only, and that
                // sample is taken AFTER every window of the segment has been closed and after Collect():
                // the reason it must still read > 0 is that WPF keeps process-wide GDI and USER objects
                // once the first window has ever been shown (the dispatcher's message-only HWNDs, cached
                // DCs and brushes), not that a window is on screen -- this comment used to say the latter
                // (R-058). Measured 2026-09-30, blue_sheep, 2 x 20 cycles: gdi 19 / user 45 after each
                // segment, with every window closed. So a 0 there IS a failed call, and it is reported with
                // the failed counter's OWN last error: one read after both calls named the USER call's error
                // for a GDI failure (RA-343). NOT asserted on the cold `before` sample, which legitimately
                // reads GDI 0 before the first window exists. runtime-resource-soak.ps1 makes the same
                // refusal at its first sample.
                ok &= Check(sb, "segment " + segment + ": GUI resource counters readable (gdi " + after.Gdi +
                    ", user " + after.User + (after.Gdi > 0 && after.User > 0 ? "" : after.DescribeFailedCounters()) + ")",
                    after.Gdi > 0 && after.User > 0);

                // Report WHICH cycles are still rooted, not just how many. The distinction is the whole
                // diagnosis: a real leak roots most or all of them, while only the newest one surviving is
                // WPF still holding the most recently shown window.
                var rootedAt = new List<int>();
                for (int i = 0; i < alive.Count; i++)
                    if (alive[i].IsAlive) rootedAt.Add(i);

                Console.WriteLine("segment " + segment + ": " + after.Describe(before));
                ok &= Check(sb, "segment " + segment + ": every window was collected (" +
                    rootedAt.Count + " still rooted" +
                    (rootedAt.Count > 0 ? " at cycle " + string.Join(",", rootedAt.ConvertAll(n => n.ToString(CultureInfo.InvariantCulture)).ToArray()) : "") +
                    ")", rootedAt.Count == 0);

                // The count bounds apply to the LAST segment only: an earlier one legitimately warms caches.
                if (previous != null && segment == options.Segments)
                {
                    ok &= Check(sb, "handles flat across the last segment (" +
                        Delta(previous.Handles, after.Handles) + ")",
                        after.Handles - previous.Handles <= options.MaximumHandleGrowth);
                    ok &= Check(sb, "GDI objects flat across the last segment (" +
                        Delta(previous.Gdi, after.Gdi) + ")",
                        after.Gdi - previous.Gdi <= options.MaximumGdiGrowth);
                    ok &= Check(sb, "USER objects flat across the last segment (" +
                        Delta(previous.User, after.User) + ")",
                        after.User - previous.User <= options.MaximumUserGrowth);
                }

                privateAfter.Add(after.PrivateBytes);
                previous = after;
            }

            if (options.Segments < 2)
                ok &= Check(sb, "at least two segments ran (growth needs a previous segment to compare against)", false);

            // Private bytes are judged once, over every segment, AFTER the loop: a trend needs the whole series,
            // and a run with too few segments for one is refused by the judge rather than judged on one step.
            PrivateBytesJudge.Verdict privateVerdict = PrivateBytesJudge.Judge(privateAfter, options.MaximumPrivateByteSlope);
            ok &= Check(sb, privateVerdict.Text, privateVerdict.Passed);

            sb.AppendLine("RESULT=" + (ok ? "PASS" : "FAIL"));
            return ok;
        }

        private static bool Check(StringBuilder sb, string what, bool condition)
        {
            sb.AppendLine((condition ? "PASS: " : "FAIL: ") + what);
            return condition;
        }

        private static string Delta(long from, long to)
        {
            long d = to - from;
            return from.ToString(CultureInfo.InvariantCulture) + " -> " + to.ToString(CultureInfo.InvariantCulture) +
                ", " + (d >= 0 ? "+" : "") + d.ToString(CultureInfo.InvariantCulture);
        }

        private static string Megabytes(long bytes)
        {
            return Megabytes((double)bytes);
        }

        private static string Megabytes(double bytes)
        {
            double mb = bytes / (1024.0 * 1024.0);
            return (mb >= 0 ? "+" : "") + mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>
        /// The private-bytes verdict: the least-squares slope of the sample taken after each segment's Collect(),
        /// over every segment, against a bound per segment.
        ///
        /// WHY A SLOPE (N-release-150-01). This judged the last segment against the one before under a 24 MiB
        /// bound, and that sat inside its own noise. Cutting v1.5.0 on 2026-10-08 it failed once at +38.7 MB with
        /// handles, GDI and USER flat and every window collected, on a Companion Studio byte-identical to 1.4.0's;
        /// four 4-segment reruns alternating master's 1.4.0 build and the release build all passed, with private
        /// bytes moving by up to 37 MB a segment on BOTH builds and never growing every segment. That is the GC
        /// keeping or returning heap after a compacting collection: a watermark, a LEVEL that swings, and one step
        /// of a level is as likely to read +38 as -37. A leak is the other shape. It adds about the same amount
        /// every segment, so the series climbs, and the slope through it is the leak's rate.
        ///
        /// THE NUMBERS. A level that swings within a range R moves the least-squares slope through n equally spaced
        /// points by at most (R / 2) * sum|x - mean| / sum (x - mean)^2: 0.4 R over four segments, 0.3 R over five,
        /// 0.26 R over six. The largest recorded swing is the failing step, 38.7 MB (the four reruns' levels spanned
        /// 27.0 to 37.1 MB), so a watermark alone moves the slope by at most 15.5 MB a segment over four segments
        /// and 11.6 over five. The recorded negative test, every window rooted, grew 31.4 MB in one segment, so a
        /// leak at that rate under the worst swing still judges at least 15.9 over four segments and 19.8 over
        /// five. The bound sits between the two at 16 MiB a segment. At four segments the two worst cases meet
        /// (15.5 against 15.9), which is why the soak's default is five segments and four is the floor. Left at the
        /// old 24 MiB, the slope would pass that leak under master's recorded swings (+22.6), which is what the
        /// "under master 1.4.0's recorded swings" check below pins.
        ///
        /// Refused: the median of the per-segment steps (with three steps, a level alternating +37, -37, +37 has a
        /// median of +37 and fails, a watermark read as a leak); also requiring growth in EVERY segment (a real
        /// leak under one -37 MB swing would then pass); the median of several runs, the entry's other suggestion,
        /// because a run here is one process and nothing re-runs it. The handle, GDI and USER bounds are not
        /// touched: they are counts that do not swing, and they still judge the last segment against the one
        /// before.
        /// </summary>
        internal static class PrivateBytesJudge
        {
            /// <summary>Segment 1's sample as the base and three segments of change after it.</summary>
            internal const int MinimumSegments = 4;

            internal const long MaximumSlopePerSegment = 16L * 1024 * 1024;

            internal struct Verdict
            {
                internal bool Passed;
                internal bool Refused;
                internal double Slope;
                internal string Text;
            }

            /// <param name="afterSegment">private bytes after each segment's Collect(), segment 1 first</param>
            internal static Verdict Judge(IList<long> afterSegment, long maximumSlopePerSegment)
            {
                const string label = "private bytes show no upward trend across the segments";
                int n = afterSegment == null ? 0 : afterSegment.Count;
                if (n < MinimumSegments)
                {
                    return new Verdict
                    {
                        Passed = false,
                        Refused = true,
                        Slope = double.NaN,
                        Text = label + ": NOT JUDGED, a slope needs at least " + MinimumSegments.ToString(CultureInfo.InvariantCulture) +
                               " segments and " + n.ToString(CultureInfo.InvariantCulture) + " ran",
                    };
                }

                double slope = Slope(afterSegment);
                int grew = 0;
                var relative = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    if (i > 0 && afterSegment[i] > afterSegment[i - 1]) grew++;
                    relative.Add(Megabytes(afterSegment[i] - afterSegment[0]));
                }
                return new Verdict
                {
                    Passed = slope <= maximumSlopePerSegment,
                    Refused = false,
                    Slope = slope,
                    Text = label + " (slope " + Megabytes(slope) + " a segment over " + n.ToString(CultureInfo.InvariantCulture) +
                           " segments, bound " + Megabytes(maximumSlopePerSegment) + "; after each segment against segment 1: " +
                           string.Join(", ", relative.ToArray()) + "; grew in " + grew.ToString(CultureInfo.InvariantCulture) +
                           " of " + (n - 1).ToString(CultureInfo.InvariantCulture) + ")",
                };
            }

            /// <summary>Ordinary least squares over x = 1..n, in bytes per segment.</summary>
            internal static double Slope(IList<long> y)
            {
                int n = y.Count;
                double meanX = (n + 1) / 2.0;
                double meanY = 0;
                for (int i = 0; i < n; i++) meanY += y[i];
                meanY /= n;
                double sxy = 0, sxx = 0;
                for (int i = 0; i < n; i++)
                {
                    double dx = (i + 1) - meanX;
                    sxy += dx * (y[i] - meanY);
                    sxx += dx * dx;
                }
                return sxy / sxx;
            }

            /// <summary>
            /// The judge over the entry's recorded series and synthetic ones, with no module and no window. Each step
            /// is in MB as this harness prints them (MiB), the change after segments 2, 3, ... from a base of 400 MiB.
            /// A verdict that is right for the wrong reason (a refusal read as a fail) does not count: every judged
            /// series must also be judged, not refused.
            /// </summary>
            internal static bool SelfTest(StringBuilder sb)
            {
                bool ok = true;
                // N-release-150-01's four reruns, 4 segments each, alternating the two builds: all passed in life.
                ok &= Expect(sb, "judge: master 1.4.0's recorded run A passes", Steps(4.2, -37.1, 15.8), true);
                ok &= Expect(sb, "judge: master 1.4.0's recorded run B passes", Steps(15.2, -0.4, -30.5), true);
                ok &= Expect(sb, "judge: the v1.5.0 release build's recorded run A passes", Steps(-27.0, 16.8, -5.4), true);
                ok &= Expect(sb, "judge: the v1.5.0 release build's recorded run B passes", Steps(-24.9, -5.6, 14.0), true);
                // The one step that failed the v1.5.0 cut, as the last of three after two recorded swings. The
                // last-segment rule failed exactly this (+38.7 over its 24 MiB); its slope is +6.8.
                ok &= Expect(sb, "judge: the +38.7 MB step that failed the v1.5.0 cut passes as the last of three after two recorded swings",
                    Steps(-37.1, 15.8, 38.7), true);
                // A leak at the recorded rooted-window rate, steady, and under master run A's swings laid over it.
                ok &= Expect(sb, "judge: a steady leak at the rooted-window rate (+31.4 MB every segment) fails",
                    Steps(31.4, 31.4, 31.4), false);
                ok &= Expect(sb, "judge: the same leak under master 1.4.0's recorded swings still fails",
                    Steps(31.4 + 4.2, 31.4 - 37.1, 31.4 + 15.8), false);
                // The bound itself, from both sides.
                ok &= Expect(sb, "judge: steady growth at the bound (+16.0 MB every segment) passes", Steps(16.0, 16.0, 16.0), true);
                ok &= Expect(sb, "judge: steady growth over the bound (+16.5 MB every segment) fails", Steps(16.5, 16.5, 16.5), false);
                // Too few segments for a slope: refused, so a two-segment run can neither pass nor fail on one step.
                Verdict two = Judge(Steps(38.7), MaximumSlopePerSegment);
                ok &= Check(sb, "judge: a two-segment run (the v1.5.0 cut's one +38.7 MB step) is refused, not judged (" + two.Text + ")",
                    two.Refused && !two.Passed);
                Verdict three = Judge(Steps(-1.0, 1.0), MaximumSlopePerSegment);
                ok &= Check(sb, "judge: a three-segment run is refused too (" + three.Text + ")", three.Refused && !three.Passed);
                return ok;
            }

            private static bool Expect(StringBuilder sb, string label, List<long> series, bool shouldPass)
            {
                Verdict verdict = Judge(series, MaximumSlopePerSegment);
                return Check(sb, label + " (" + (verdict.Refused ? "refused: " : "slope ") +
                    (verdict.Refused ? verdict.Text : Megabytes(verdict.Slope) + " a segment") + ")",
                    !verdict.Refused && verdict.Passed == shouldPass);
            }

            private static List<long> Steps(params double[] steps)
            {
                const double mib = 1024.0 * 1024.0;
                var series = new List<long> { 400L * 1024 * 1024 };
                foreach (double step in steps)
                    series.Add(series[series.Count - 1] + (long)Math.Round(step * mib));
                return series;
            }
        }

        /// <summary>Compacting the LOH matters here: the decoded sprite sheet is a large-object allocation, so
        /// an ordinary collection can leave it looking retained when it is merely uncompacted.</summary>
        private static void Collect()
        {
            for (int i = 0; i < 2; i++)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
            }
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        }

        /// <summary>Reflection over the module's window. Every member is resolved once, up front, so a rename
        /// fails the run immediately and loudly instead of quietly reducing what the soak exercises.</summary>
        private sealed class WindowDriver
        {
            private readonly ConstructorInfo _ctor;
            private readonly MethodInfo _setEditorText;
            private readonly MethodInfo _analyze;
            private readonly MethodInfo _selectNode;
            private readonly FieldInfo _nodesById;
            private readonly FieldInfo _analyzeInFlight;

            /// <summary>How long one analysis may take before the soak refuses to wait for it. The default pet
            /// parses in well under a second on a pool thread; this is a hang detector, not a budget.</summary>
            private static readonly TimeSpan AnalysisBound = TimeSpan.FromSeconds(30);

            private WindowDriver(ConstructorInfo ctor, MethodInfo setEditorText, MethodInfo analyze,
                                 MethodInfo selectNode, FieldInfo nodesById, FieldInfo analyzeInFlight)
            {
                _ctor = ctor;
                _setEditorText = setEditorText;
                _analyze = analyze;
                _selectNode = selectNode;
                _nodesById = nodesById;
                _analyzeInFlight = analyzeInFlight;
            }

            internal static WindowDriver Load(string modulePath, string typeName)
            {
                Assembly module = Assembly.LoadFrom(modulePath);
                Type window = module.GetType(typeName);
                if (window == null)
                    throw new InvalidOperationException("no type '" + typeName + "' in " + modulePath);

                const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                ConstructorInfo ctor = window.GetConstructor(Any, null, new[] { typeof(IHost) }, null);
                MethodInfo setEditorText = window.GetMethod("SetEditorText", Any, null, new[] { typeof(string) }, null);
                MethodInfo analyze = window.GetMethod("Analyze", Any, null, Type.EmptyTypes, null);
                MethodInfo selectNode = window.GetMethod("SelectNode", Any, null, new[] { typeof(int) }, null);
                FieldInfo nodesById = window.GetField("_nodesById", Any);
                // The single-flight gate BeginAnalyze holds for the whole analysis (RA-341): 1 from the call
                // until the continuation's finally, which runs in the same dispatcher operation that fills
                // _nodesById, and a remembered rerun re-arms it inside that operation too. Reading it back at
                // 0 after a pump is therefore "the analysis this cycle asked for has rendered".
                FieldInfo analyzeInFlight = window.GetField("_analyzeInFlight", Any);

                Require(ctor != null, typeName + "(IHost)");
                Require(setEditorText != null, "SetEditorText(string)");
                Require(analyze != null, "Analyze()");
                Require(selectNode != null, "SelectNode(int)");
                Require(nodesById != null, "_nodesById");
                Require(analyzeInFlight != null && analyzeInFlight.FieldType == typeof(int), "_analyzeInFlight (int)");

                return new WindowDriver(ctor, setEditorText, analyze, selectNode, nodesById, analyzeInFlight);
            }

            private static void Require(bool found, string member)
            {
                if (!found)
                    throw new InvalidOperationException(
                        "the module no longer exposes " + member + " -- this soak drives it by reflection, so " +
                        "a rename must be followed here rather than silently reducing what is exercised.");
            }

            /// <summary>The same rule applied to what the members DO, not only to whether they exist: a step
            /// this soak exists to drive must not be skipped in silence when its input yields nothing.</summary>
            private static void Demand(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
            }

            /// <summary>
            /// One cycle: build the window, load a pet into it, analyze (which is where the sprite SHEET is
            /// decoded), select an animation (which crops and renders that animation's frames into the detail
            /// pane), show it, close it.
            ///
            /// Returns a WeakReference, never the window itself, and is NoInlining. Both matter: if a strong
            /// reference crossed this boundary it could sit in the caller's stack slot or a callee-saved
            /// register until overwritten, which made the FINAL cycle of every segment look rooted no matter
            /// how many cycles ran (observed at cycle 7 of 8 and cycle 19 of 20). Keeping the only strong
            /// reference inside a frame that is guaranteed to be torn down removes that false positive without
            /// weakening what is asserted.
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal WeakReference OpenAnalyzeAndClose(string petXml, IHost host)
            {
                var window = (Window)_ctor.Invoke(new object[] { host });
                try
                {
                    // Offscreen rather than hidden: a real HWND is created and rendered, which is the thing
                    // being measured, but 40 windows do not flash across the desktop while it runs.
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.ShowInTaskbar = false;
                    window.Left = -32000;
                    window.Top = -32000;

                    _setEditorText.Invoke(window, new object[] { petXml });
                    _analyze.Invoke(window, null);
                    // Analyze() returns at its first await with the map untouched (PetStudio 1.1.18, see Run);
                    // the nodes exist only once its continuation has been pumped through this dispatcher.
                    WaitForAnalysis(window);

                    // A hard failure, never a skip. This was `if (first.HasValue)`: a pet the validator
                    // rejected, an empty file, or a change to the node dictionary's key type produced no
                    // nodes, SelectNode never ran, and the soak reported PASS with flat memory over an
                    // un-analyzed window -- exactly the "quietly stops soaking" shape the header above
                    // refuses for a missing member (F387). Fails on cycle 1 rather than after forty.
                    int? first = FirstNodeId(window);
                    Demand(first.HasValue,
                        "Analyze() produced no animation nodes for the supplied pet, so SelectNode and the frame " +
                        "rendering it drives would have been skipped and this soak would have measured an " +
                        "un-analyzed window. The pet was rejected or has no animations; fix the input or the analyzer.");
                    _selectNode.Invoke(window, new object[] { first.Value });

                    window.Show();
                    Pump();
                }
                finally
                {
                    window.Close();
                    Pump();
                }
                var reference = new WeakReference(window);
                window = null;
                return reference;
            }

            /// <summary>
            /// Pump this dispatcher until the analysis Analyze() started has rendered, i.e. until the module's
            /// in-flight gate reads 0 again (see Load for why that is the completion signal). The pool-thread
            /// parse posts its continuation here through the SynchronizationContext Run installed, and Pump()
            /// runs it; the rerun the module remembers when a second Analyze() arrives mid-flight re-arms the
            /// gate inside that same operation, so it is waited for as well. Bounded, and the bound is a hard
            /// failure rather than a skip: a soak that went on to SelectNode over an empty map would be the
            /// F387 shape again, and one that waited for ever would be a hang nobody can read.
            /// </summary>
            private void WaitForAnalysis(object window)
            {
                var waited = Stopwatch.StartNew();
                while (true)
                {
                    Pump();
                    if ((int)_analyzeInFlight.GetValue(window) == 0) return;
                    Demand(waited.Elapsed < AnalysisBound,
                        "Analyze() did not finish within " + AnalysisBound.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) +
                        " s: its pool-thread parse never posted the render back to this dispatcher (a hang in the " +
                        "analyzer, or the module no longer resumes on the calling context).");
                    Thread.Sleep(10);
                }
            }

            private int? FirstNodeId(object window)
            {
                var nodes = _nodesById.GetValue(window) as IDictionary;
                if (nodes == null) return null;
                foreach (object key in nodes.Keys)
                    if (key is int) return (int)key;
                return null;
            }
        }

        /// <summary>Drain the dispatcher queue so layout, rendering and the close actually happen before the
        /// next cycle starts. Without this the soak measures a backlog rather than a steady state.</summary>
        private static void Pump()
        {
            Dispatcher.CurrentDispatcher.Invoke(
                DispatcherPriority.ContextIdle, new Action(delegate { }));
        }

        private sealed class Sample
        {
            internal long Handles;
            internal long Gdi;
            internal long User;
            internal long PrivateBytes;
            /// <summary>Marshal.GetLastWin32Error read straight after EACH GetGuiResources call, so a 0 count can
            /// say why. The SetLastError stub clears and stores per call, so one read after both calls held the
            /// USER call's error and would have labelled a GDI-only failure with it (RA-343).</summary>
            internal int GdiError;
            internal int UserError;

            internal static Sample Take()
            {
                using (Process self = Process.GetCurrentProcess())
                {
                    self.Refresh();
                    var sample = new Sample { Handles = self.HandleCount };
                    sample.Gdi = GetGuiResources(self.Handle, GR_GDIOBJECTS);
                    sample.GdiError = Marshal.GetLastWin32Error();
                    sample.User = GetGuiResources(self.Handle, GR_USEROBJECTS);
                    sample.UserError = Marshal.GetLastWin32Error();
                    sample.PrivateBytes = self.PrivateMemorySize64;
                    return sample;
                }
            }

            /// <summary>The last Win32 error of whichever counter read 0 (both when both did), for the FAIL line.</summary>
            internal string DescribeFailedCounters()
            {
                string text = "";
                if (Gdi <= 0) text += ", last Win32 error gdi " + GdiError.ToString(CultureInfo.InvariantCulture);
                if (User <= 0) text += ", last Win32 error user " + UserError.ToString(CultureInfo.InvariantCulture);
                return text;
            }

            internal string Describe(Sample before)
            {
                return "handles " + Delta(before.Handles, Handles) +
                    " | gdi " + Delta(before.Gdi, Gdi) +
                    " | user " + Delta(before.User, User) +
                    " | private " + Megabytes(PrivateBytes - before.PrivateBytes);
            }
        }

        private sealed class Options
        {
            internal string ModulePath;
            internal string PetPath;
            internal string TypeName = DefaultTypeName;
            internal int Cycles = 20;
            // Five, not the two this was: the private-byte judge needs PrivateBytesJudge.MinimumSegments (four) for a
            // slope at all, and a fifth narrows what a GC watermark can do to it (from 0.4 to 0.3 of the swing's range;
            // the arithmetic is on PrivateBytesJudge). tests\module-window-soak.ps1 passes its own -Segments.
            internal int Segments = 5;
            internal bool JudgeSelfTestOnly;
            internal string Error;

            // The three COUNT bounds are numerically the ones runtime-resource-soak.ps1 uses; the two harnesses do
            // NOT report on one scale, which is what this comment used to claim (F389). That script judges GDI,
            // USER and handles as a RATE per settled interval of 40 churn cycles and private bytes on raw
            // first-vs-last samples over the whole run under 64 MB; this one judges last-segment TOTALS over 20
            // window cycles, and private bytes by their SLOPE per segment across the run, bounded at 16 MiB a
            // segment (PrivateBytesJudge says why that number): one window's sprite cache is the subject, and the
            // recorded negative test (every window rooted, +31.4 MB in segment 2) would PASS at 64 MB. Read the
            // two harnesses' numbers as two measurements, not one.
            internal long MaximumHandleGrowth = 16;
            internal long MaximumGdiGrowth = 16;
            internal long MaximumUserGrowth = 16;
            internal long MaximumPrivateByteSlope = PrivateBytesJudge.MaximumSlopePerSegment;

            internal static Options Parse(string[] args)
            {
                var options = new Options();
                string root = RepositoryRoot();
                if (root != null)
                {
                    options.ModulePath = Path.Combine(root, DefaultModuleRelativePath);
                    options.PetPath = Path.Combine(root, DefaultPetRelativePath);
                }

                for (int i = 0; args != null && i < args.Length; i++)
                {
                    string name = args[i];
                    string value = i + 1 < args.Length ? args[i + 1] : null;
                    switch (name)
                    {
                        case "--module": options.ModulePath = value; i++; break;
                        case "--pet": options.PetPath = value; i++; break;
                        case "--type": options.TypeName = value; i++; break;
                        case "--cycles":
                            if (!TryParseCount(value, out options.Cycles)) { options.Error = InvalidCount(name, value); return options; }
                            i++; break;
                        case "--segments":
                            if (!TryParseCount(value, out options.Segments)) { options.Error = InvalidCount(name, value); return options; }
                            i++; break;
                        case "--judge-selftest": options.JudgeSelfTestOnly = true; break;
                        default:
                            options.Error = "unknown argument '" + name +
                                "' (expected --module/--pet/--type/--cycles/--segments/--judge-selftest)";
                            return options;
                    }
                }

                // The judge's self-test reads neither file, so it needs no repository root to find them.
                if (!options.JudgeSelfTestOnly &&
                    (string.IsNullOrEmpty(options.ModulePath) || string.IsNullOrEmpty(options.PetPath)))
                    options.Error = "could not locate the repository root; pass --module and --pet explicitly.";
                return options;
            }

            // A count that is missing, does not parse or is not positive is a HARD ERROR, exit 2, the same as an
            // unknown flag. ParseCount used to swap the default in silently: `--segments 0` ran two segments and
            // PASSED while `--segments 1` was honoured and FAILED, and `--cycles 2OO` (letter o) ran twenty
            // cycles under release notes that said two hundred (F390, measured live). The header's own stance is
            // that a soak which quietly reduces what it exercises reads exactly like a soak that passed.
            private static bool TryParseCount(string value, out int parsed)
            {
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0;
            }

            private static string InvalidCount(string name, string value)
            {
                return "invalid value for " + name + ": '" + (value ?? "(missing)") + "' (expected a positive integer)";
            }

            /// <summary>Walk up from the binary looking for ProductVersion.props, the one file that is only
            /// ever at the repository root.</summary>
            private static string RepositoryRoot()
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory != null)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "ProductVersion.props")))
                        return directory.FullName;
                    directory = directory.Parent;
                }
                return null;
            }
        }
    }
}