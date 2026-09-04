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
    ///   3. the last segment's private bytes barely move.
    /// Segment 1 is deliberately excluded from 2 and 3: the first pass legitimately sets a high watermark
    /// because the pet's sprite sheet is large and caches fill. Comparing segment N against segment N-1 rather
    /// than against a cold start is what makes this signal usable -- and it is precisely what surfaced the
    /// re-decode bug, where a debounced re-analyze decoded a ~15 MB sheet on every keystroke-settle.
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
        // blue_sheep on purpose: ~1.1 MB of base64 sprite sheet. A small pet would not move private bytes
        // enough for signal 3 to mean anything, and this is the pet the original re-decode bug was found on.
        private const string DefaultPetRelativePath = @"Companions\blue_sheep\animations.xml";

        [DllImport("user32.dll")]
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

                // A WPF Application must exist before a Window is constructed, or resource lookup throws.
                if (Application.Current == null) new Application();

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
            bool ok = true;
            Sample previous = null;

            for (int segment = 1; segment <= options.Segments; segment++)
            {
                var alive = new List<WeakReference>();
                Sample before = Sample.Take();

                for (int cycle = 0; cycle < options.Cycles; cycle++)
                    alive.Add(driver.OpenAnalyzeAndClose(petXml));

                Collect();
                Sample after = Sample.Take();

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

                // Growth bounds apply to the LAST segment only: an earlier one legitimately warms caches.
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
                    ok &= Check(sb, "private bytes settled across the last segment (" +
                        Megabytes(after.PrivateBytes - previous.PrivateBytes) + ")",
                        after.PrivateBytes - previous.PrivateBytes <= options.MaximumPrivateByteGrowth);
                }

                previous = after;
            }

            if (options.Segments < 2)
                ok &= Check(sb, "at least two segments ran (growth needs a previous segment to compare against)", false);

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
            double mb = bytes / (1024.0 * 1024.0);
            return (mb >= 0 ? "+" : "") + mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
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

            private WindowDriver(ConstructorInfo ctor, MethodInfo setEditorText, MethodInfo analyze,
                                 MethodInfo selectNode, FieldInfo nodesById)
            {
                _ctor = ctor;
                _setEditorText = setEditorText;
                _analyze = analyze;
                _selectNode = selectNode;
                _nodesById = nodesById;
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

                Require(ctor != null, typeName + "(IHost)");
                Require(setEditorText != null, "SetEditorText(string)");
                Require(analyze != null, "Analyze()");
                Require(selectNode != null, "SelectNode(int)");
                Require(nodesById != null, "_nodesById");

                return new WindowDriver(ctor, setEditorText, analyze, selectNode, nodesById);
            }

            private static void Require(bool found, string member)
            {
                if (!found)
                    throw new InvalidOperationException(
                        "the module no longer exposes " + member + " -- this soak drives it by reflection, so " +
                        "a rename must be followed here rather than silently reducing what is exercised.");
            }

            /// <summary>
            /// One cycle: build the window, load a pet into it, analyze, select an animation (which is what
            /// decodes sprite frames), show it, close it.
            ///
            /// Returns a WeakReference, never the window itself, and is NoInlining. Both matter: if a strong
            /// reference crossed this boundary it could sit in the caller's stack slot or a callee-saved
            /// register until overwritten, which made the FINAL cycle of every segment look rooted no matter
            /// how many cycles ran (observed at cycle 7 of 8 and cycle 19 of 20). Keeping the only strong
            /// reference inside a frame that is guaranteed to be torn down removes that false positive without
            /// weakening what is asserted.
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal WeakReference OpenAnalyzeAndClose(string petXml)
            {
                var window = (Window)_ctor.Invoke(new object[] { NewHost() });
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

                    int? first = FirstNodeId(window);
                    if (first.HasValue) _selectNode.Invoke(window, new object[] { first.Value });

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

            private int? FirstNodeId(object window)
            {
                var nodes = _nodesById.GetValue(window) as IDictionary;
                if (nodes == null) return null;
                foreach (object key in nodes.Keys)
                    if (key is int) return (int)key;
                return null;
            }

            private static IHost NewHost()
            {
                // A fresh host per cycle, so a handler the window leaves attached keeps its window alive and
                // the WeakReference check sees it. Sharing one host would hide exactly that leak.
                return new RecordingHost();
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

            internal static Sample Take()
            {
                using (Process self = Process.GetCurrentProcess())
                {
                    self.Refresh();
                    return new Sample
                    {
                        Handles = self.HandleCount,
                        Gdi = GetGuiResources(self.Handle, GR_GDIOBJECTS),
                        User = GetGuiResources(self.Handle, GR_USEROBJECTS),
                        PrivateBytes = self.PrivateMemorySize64,
                    };
                }
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
            internal int Segments = 2;
            internal string Error;

            // Same bounds as runtime-resource-soak.ps1, so the two harnesses report on one scale.
            internal long MaximumHandleGrowth = 16;
            internal long MaximumGdiGrowth = 16;
            internal long MaximumUserGrowth = 16;
            internal long MaximumPrivateByteGrowth = 24L * 1024 * 1024;

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
                        case "--cycles": options.Cycles = ParseCount(value, options.Cycles); i++; break;
                        case "--segments": options.Segments = ParseCount(value, options.Segments); i++; break;
                        default:
                            options.Error = "unknown argument '" + name +
                                "' (expected --module/--pet/--type/--cycles/--segments)";
                            return options;
                    }
                }

                if (string.IsNullOrEmpty(options.ModulePath) || string.IsNullOrEmpty(options.PetPath))
                    options.Error = "could not locate the repository root; pass --module and --pet explicitly.";
                return options;
            }

            private static int ParseCount(string value, int fallback)
            {
                int parsed;
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0
                    ? parsed
                    : fallback;
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