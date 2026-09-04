using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace DesktopAICompanion
{
    /// <summary>
    /// Main for the application. Once the application is started, this class will create all objects.
    /// </summary>
    class Program
    {
        /// <summary>
        /// StartUp is the main program.
        /// </summary>
        public static StartUp Mainthread;
        internal static bool ResourceChurnSelfTestActive { get; private set; }

#if PORTABLE
        public static LocalData MyData;
        public static string InitialXmlOverride = "";
        private static int restartRequested;
        private static string restartReopenPane;
        /// <summary>Parsed from <c>--reopen-options=&lt;pane&gt;</c> on this launch (set by the previous
        /// instance's <see cref="LaunchReplacement"/> after a module-install restart). Consumed once, after
        /// the tray/host are up, to reopen Settings on the same pane the user was just on.</summary>
        internal static string ReopenOptionsPane;

        /// <summary>
        /// Open the option dialog, to show some options like reset XML animation or load animation from the webpage.
        /// </summary>
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // net10 no longer reads DPI awareness from app.config's ApplicationConfigurationSection
            // (defaults to SystemAware otherwise), so set PerMonitorV2 explicitly before any UI.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // The smart-fortunes ONNX runtime + its System.* deps + the bge-small model live inside the
            // Fortunes MODULE package (modules\fortunes\), not beside the exe: the base ships none of it
            // and the module resolves them from its own folder. Nothing to arrange here.

            // The embed/smart self-tests moved to the Fortunes module with the ONNX engine (S3d): run them
            // via --fortunes-engine-selftest, which loads the module and exercises Embedder + SmartFortunes
            // inside its own AssemblyLoadContext.
            // "Clear all settings and modules" from the installer. Runs before any UI and exits: the MSI
            // invokes it after laying down the new payload, so the next launch is a first launch. The app
            // owns the deleting because AppPaths owns where the data IS -- see FactoryReset.
            if (args != null && Array.IndexOf(args, FactoryReset.Flag) >= 0)
            {
                Environment.Exit(FactoryReset.Run());
            }
            // CompanionTypeRegistry refcount lifecycle (in-process port of pettyperegistry-selftest.ps1).
            if (args != null && Array.IndexOf(args, "--pettyperegistry-selftest") >= 0)
            {
                Environment.Exit(CompanionTypeRegistrySelfTest.Run() ? 0 : 1);
            }
            // Runtime hardening: animation/geometry/limit invariants (in-process reflection half of
            // runtime-hardening-selftest.ps1; the source-text checks stay in the PowerShell script).
            if (args != null && Array.IndexOf(args, "--hardening-selftest") >= 0)
            {
                Environment.Exit(RuntimeHardeningSelfTest.Run() ? 0 : 1);
            }
            // Module audio: the decode seam and the barge-in ramp, asserted with no audio device (a CI runner
            // has none, and opening DirectSound is the live smoke script's job, not a gate's).
            if (args != null && Array.IndexOf(args, "--audio-selftest") >= 0)
            {
                Environment.Exit(AudioOutputSelfTest.Run() ? 0 : 1);
            }
            // Plugin pipeline: loads the bundled test-module DLL via AssemblyLoadContext against a
            // recording host and asserts Init + contributions + event dispatch (S1). Skips-pass if absent.
            // Any module's OWN self-test, by convention rather than registration: --module-selftest=<id>
            // loads modules\<id> through the real loader and invokes its static SelfTest(out string). A new
            // module needs no edit here to become testable; see docs\module-authoring.md.
            if (args != null)
            {
                string conventionModuleId = DesktopAICompanion.Plugins.ModuleConventionSelfTest.FindRequestedId(args);
                if (conventionModuleId != null)
                {
                    Environment.Exit(
                        DesktopAICompanion.Plugins.ModuleConventionSelfTest.Run(conventionModuleId) ? 0 : 1);
                }
            }
            if (args != null && Array.IndexOf(args, "--module-host-selftest") >= 0)
            {
                Environment.Exit(DesktopAICompanion.Plugins.ModuleHostSelfTest.Run() ? 0 : 1);
            }
            // Fortunes module (S3): loads the real Fortunes.dll, proves the embedded welcome corpus parsed in
            // the module's load context and the personalized (Windows-username) welcome fires once on spawn.
            if (args != null && Array.IndexOf(args, "--fortunes-selftest") >= 0)
            {
                Environment.Exit(DesktopAICompanion.Plugins.FortunesModuleSelfTest.Run() ? 0 : 1);
            }
            // Fortunes engine relocation (S3c-1): loads the real Fortunes.dll and reflectively runs the
            // module's engine probe, proving the relocated dumb engine works in the module's load context.
            if (args != null && Array.IndexOf(args, "--fortunes-engine-selftest") >= 0)
            {
                Environment.Exit(DesktopAICompanion.Plugins.FortunesEngineSelfTest.Run() ? 0 : 1);
            }
            // The slow half of the smart suite: a cold-cache warm of a 1,500-line sample, proving Pick serves
            // the warmed prefix before the pool finishes. ~18s, so it gets its own flag rather than padding
            // every local gate run, but CI runs it. This was the base's --smart-progress-selftest, which lost
            // its caller in the S3d move to the module and sat orphaned until the corpus bug turned it up.
            if (args != null && Array.IndexOf(args, "--fortunes-smart-progress-selftest") >= 0)
            {
                Environment.Exit(DesktopAICompanion.Plugins.FortunesEngineSelfTest.RunProgressive() ? 0 : 1);
            }
            // AI-brain module (S4a): loads the real AiBrain.dll in isolation and proves its boundary +
            // DORMANCY (wires nothing, reacts to nothing) so the base still owns the brain until the S4b flip.
            if (args != null && Array.IndexOf(args, "--aibrain-selftest") >= 0)
            {
                Environment.Exit(DesktopAICompanion.Plugins.AiBrainModuleSelfTest.Run() ? 0 : 1);
            }
            // Companion Studio module: loads the real PetStudio.dll and proves its analyzer agrees with the host's
            // own validator -- the claim that justifies source-linking the parser instead of copying it.
            if (args != null && Array.IndexOf(args, "--petstudio-selftest") >= 0)
            {
                Environment.Exit(DesktopAICompanion.Plugins.PetStudioModuleSelfTest.Run() ? 0 : 1);
            }
            // WPF settings shell (S5b): the schema renderer + Load/Save round-trip, headless on the STA thread.
            if (args != null && Array.IndexOf(args, "--wpf-options-selftest") >= 0)
            {
                Environment.Exit(DesktopAICompanion.WpfOptionsSelfTest.Run() ? 0 : 1);
            }
            // Fullscreen-awareness diagnostic: per-monitor scan length + relocation-decision logic.
            if (args != null && Array.IndexOf(args, "--fullscreen-selftest") >= 0)
            {
                Environment.Exit(DesktopAICompanion.FullscreenScan.SelfTest() ? 0 : 1);
            }
            if (args != null && Array.IndexOf(args, "--catalog-selftest") >= 0)
            {
                Environment.Exit(DesktopAICompanion.RemoteCatalogClient.SelfTest() ? 0 : 1);
            }
            if (args != null && Array.IndexOf(args, "--online-selftest") >= 0)
            {
                Environment.Exit(DesktopAICompanion.RemoteCatalogClient.OnlineSelfTest() ? 0 : 1);
            }
            if (args != null)
            {
                const string reopenPrefix = "--reopen-options=";
                foreach (string arg in args)
                    if (arg != null && arg.StartsWith(reopenPrefix, StringComparison.OrdinalIgnoreCase))
                    { ReopenOptionsPane = arg.Substring(reopenPrefix.Length); break; }
            }
            if (args != null)
            {
                foreach (string arg in args)
                {
                    if (arg == null ||
                        !arg.StartsWith("--catalog-parse-file=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string catalogPath = arg.Substring("--catalog-parse-file=".Length);
                    string resultPath = Path.Combine(
                        Path.GetTempPath(), "dp-catalog-parse.txt");
                    try
                    {
                        var parsedCatalog = DesktopAICompanion.RemoteCatalogClient.Parse(
                            File.ReadAllText(catalogPath));
                        File.WriteAllText(resultPath,
                            "catalog_parse=PASS companions=" + parsedCatalog.Pets.Count +
                            " packs=" + parsedCatalog.Packs.Count +
                            " modules=" + parsedCatalog.Modules.Count);
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        try { File.WriteAllText(resultPath, "catalog_parse=FAIL " + ex.Message); }
                        catch { }
                        Environment.Exit(1);
                    }
                }
            }
            if (args != null && Array.IndexOf(args, "--security-selftest") >= 0)
            {
                Environment.Exit(DesktopAICompanion.SecuritySelfTest.Run(Console.Out) ? 0 : 1);
            }

            RuntimeResourceChurnConfiguration resourceChurn = null;
            if (args != null &&
                Array.IndexOf(args, "--resource-churn-selftest") >= 0)
            {
                string churnError;
                if (!RuntimeResourceChurnConfiguration.TryCreate(
                        out resourceChurn,
                        out churnError))
                {
                    Debug.WriteLine(
                        "Resource churn self-test rejected: " + churnError);
                    Environment.Exit(2);
                }
                ResourceChurnSelfTestActive = true;
            }

            IDisposable instanceLease = TryAcquireInstanceSlot();
            if (instanceLease == null)
            {
                MessageBox.Show(
                    "Application is already running! Only 2 instances are allowed.",
                    "Information",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            try
            {
                // Load/migrate mutable settings only after this instance owns a cross-session
                // slot, so a rejected third launch cannot write application data.
                MyData = new LocalData();
                if (!string.IsNullOrWhiteSpace(MyData.SettingsWarning))
                {
                    MessageBox.Show(
                        "DesktopAICompanion could not fully access its settings storage. " +
                        "It will continue with settings held in memory, but changes " +
                        "may not be saved.\r\n\r\n" +
                        MyData.SettingsWarning,
                        "Settings storage unavailable",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }

                // Check and parse the arguments
                const string SearchStringLocalXml = "localxml=";
                const string SearchStringWebXml = "webxml=";
                const string SearchStringInstall = "install=";
			    foreach (string s in args)
                {
                    if (s.StartsWith(SearchStringLocalXml, StringComparison.OrdinalIgnoreCase))
                    {
                        string localXmlPath = s.Substring(SearchStringLocalXml.Length);
                        try
                        {
                            InitialXmlOverride = ReadBoundedUtf8File(
                                localXmlPath,
                                CompanionXmlValidator.MaximumXmlBytes);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(
                                "Could not load the requested local companion: " + ex.Message,
                                "Invalid companion",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                            return;
                        }
                    }
                    else if (s.StartsWith(SearchStringWebXml, StringComparison.OrdinalIgnoreCase) ||
                             s.StartsWith(SearchStringInstall, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show(
                            "Remote and legacy installer companion arguments are disabled. " +
                            "Import a bounded local XML file instead.",
                            "Unsupported companion source",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        return;
                    }
                }

                // Show the system tray icon.
                using (ProcessIcon pi = new ProcessIcon())
                {
                    pi.Display();

                    RuntimeResourceChurn resourceChurnRunner = null;
                    try
                    {
                        Mainthread = new StartUp(pi);
                        if (resourceChurn != null)
                            resourceChurnRunner =
                                new RuntimeResourceChurn(
                                    Mainthread,
                                    resourceChurn);

                        // A restart-to-activate-a-module relaunch: reopen Settings once the message loop is
                        // actually pumping (opening a modal dialog here, before Application.Run(), is not a
                        // supported ordering) rather than synchronously, matching the Timer-deferred pattern
                        // used elsewhere in this codebase for "run once the loop is up" work.
                        if (!string.IsNullOrEmpty(ReopenOptionsPane))
                        {
                            string paneToOpen = ReopenOptionsPane;
                            ReopenOptionsPane = null;
                            var openTimer = new System.Windows.Forms.Timer { Interval = 1 };
                            openTimer.Tick += delegate
                            {
                                openTimer.Stop();
                                openTimer.Dispose();
                                DesktopAICompanion.Wpf.OptionsShell.Open(paneToOpen);
                            };
                            openTimer.Start();
                        }

                        // Make sure the application runs!
                        // WITH a hidden main form: see AppLifetime. Without one, Restart Manager cannot
                        // close the app for an installer, and the windows it does close leave the process
                        // alive with no tray icon.
                        using (var lifetime = new AppLifetime())
                            Application.Run(lifetime);
                    }
                    finally
                    {
                        if (resourceChurnRunner != null)
                            resourceChurnRunner.Dispose();
                        if (Mainthread != null) Mainthread.Dispose();
                        Mainthread = null;
                        ResourceChurnSelfTestActive = false;
                    }
                }
            }
            finally
            {
                CompleteInstanceLifecycle(
                    instanceLease,
                    ConsumeRestartRequest,
                    LaunchReplacement);
            }
        }

        /// <param name="reopenPane">Optional pane title the relaunched instance should reopen Settings on
        /// (e.g. "Modules" after installing a module) — passed through to the replacement process as
        /// <c>--reopen-options=&lt;pane&gt;</c>. Null/empty means the relaunch starts with Settings closed,
        /// same as a normal launch.</param>
        internal static void RequestRestart(string reopenPane = null)
        {
            restartReopenPane = reopenPane;
            Interlocked.Exchange(ref restartRequested, 1);
        }

        /// <summary>The pane title threaded into the next <see cref="LaunchReplacement"/>'s
        /// <c>--reopen-options=</c> argument, for self-test verification only.</summary>
        internal static string RestartReopenPaneForSelfTest { get { return restartReopenPane; } }

        internal static bool TryRequestRestartAfterSave(
            Func<bool> save,
            Action requestRestart)
        {
            if (save == null) throw new ArgumentNullException("save");
            if (requestRestart == null)
                throw new ArgumentNullException("requestRestart");
            if (!save()) return false;
            requestRestart();
            return true;
        }

        internal static void CompleteInstanceLifecycle(
            IDisposable instanceLease,
            Func<bool> consumeRestartRequest,
            Action launchReplacement)
        {
            if (instanceLease == null)
                throw new ArgumentNullException("instanceLease");
            if (consumeRestartRequest == null)
                throw new ArgumentNullException("consumeRestartRequest");
            if (launchReplacement == null)
                throw new ArgumentNullException("launchReplacement");

            instanceLease.Dispose();
            if (consumeRestartRequest())
                launchReplacement();
        }

        private static bool ConsumeRestartRequest()
        {
            return Interlocked.Exchange(ref restartRequested, 0) != 0;
        }

        private static void LaunchReplacement()
        {
            try
            {
                var info = new ProcessStartInfo(Application.ExecutablePath);
                if (!string.IsNullOrEmpty(restartReopenPane))
                    info.ArgumentList.Add("--reopen-options=" + restartReopenPane);
                using (Process replacement = Process.Start(info))
                {
                    // Release only our process handle. The replacement keeps running.
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "DesktopAICompanion closed, but its replacement could not be started: " +
                    ex.Message,
                    "Restart failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static IDisposable TryAcquireInstanceSlot()
        {
            string firstPath = Path.Combine(
                AppPaths.DataRoot,
                ".instance-slot-1");
            IDisposable lease = CrossSessionLock.TryAcquire(
                CrossSessionLock.BuildGlobalMutexName(
                    "Instance1",
                    firstPath),
                firstPath,
                1000);
            if (lease != null) return lease;

            string secondPath = Path.Combine(
                AppPaths.DataRoot,
                ".instance-slot-2");
            return CrossSessionLock.TryAcquire(
                CrossSessionLock.BuildGlobalMutexName(
                    "Instance2",
                    secondPath),
                secondPath,
                1000);
        }

#else

        public static LocalData.LocalData MyData = null;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MyData = new LocalData.LocalData(Windows.Storage.ApplicationData.Current.LocalFolder.Path, Application.ExecutablePath);

            // Show the system tray icon.					
            using (ProcessIcon pi = new ProcessIcon())
            {
                pi.Display();

                Mainthread = new StartUp(pi);

                // Make sure the application runs! (See AppLifetime for why it needs a main form.)
                using (var lifetime = new AppLifetime())
                    Application.Run(lifetime);
            }
        }
#endif

        /// <summary>
        /// Check if application is started from the installation path.
        /// </summary>
        /// <returns>true if the executed application is installed.</returns>
        public static bool IsApplicationInstalled()
        {
            return AppPaths.IsInstalled;
        }

        private static string ReadBoundedUtf8File(string path, int maximumBytes)
        {
            CompanionXmlValidator.RetainedLocalXmlFile retained;
            string pathError;
            if (!CompanionXmlValidator.TryOpenLocalXmlFile(
                    path,
                    out retained,
                    out pathError))
                throw new InvalidDataException(pathError);

            using (retained)
            using (var stream = retained.OpenRead(4096))
            {
                if (stream.Length > maximumBytes)
                    throw new InvalidDataException(
                        "The local companion XML exceeds its size limit.");
                using (var memory = new MemoryStream(
                    (int)Math.Min(stream.Length, maximumBytes)))
                {
                    byte[] buffer = new byte[8192];
                    int total = 0;
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total = checked(total + read);
                        if (total > maximumBytes)
                            throw new InvalidDataException(
                                "The local companion XML exceeds its size limit.");
                        memory.Write(buffer, 0, read);
                    }

                    byte[] bytes = memory.ToArray();
                    int offset = bytes.Length >= 3 &&
                                 bytes[0] == 0xEF &&
                                 bytes[1] == 0xBB &&
                                 bytes[2] == 0xBF
                        ? 3
                        : 0;
                    return new System.Text.UTF8Encoding(false, true).GetString(
                        bytes,
                        offset,
                        bytes.Length - offset);
                }
            }
        }
    }

#if PORTABLE
    internal sealed class RuntimeResourceChurnConfiguration
    {
        internal string MarkerPath;
        internal int Cycles;
        internal int IntervalMilliseconds;
        internal int MinimumDurationMilliseconds;
        internal int ExitDelayMilliseconds;

        internal static bool TryCreate(
            out RuntimeResourceChurnConfiguration configuration,
            out string error)
        {
            configuration = null;
            error = null;
            try
            {
                string configuredRoot =
                    Environment.GetEnvironmentVariable(
                        "DESKTOP_AI_COMPANION_DATA_ROOT");
                if (string.IsNullOrWhiteSpace(configuredRoot) ||
                    !Path.IsPathRooted(configuredRoot))
                    throw new InvalidOperationException(
                        "DESKTOP_AI_COMPANION_DATA_ROOT must name an absolute isolated directory.");

                string root = Path.GetFullPath(configuredRoot)
                    .TrimEnd(Path.DirectorySeparatorChar);
                string temp = Path.GetFullPath(Path.GetTempPath())
                    .TrimEnd(Path.DirectorySeparatorChar);
                string requiredPrefix =
                    temp + Path.DirectorySeparatorChar +
                    "DesktopAICompanion-ResourceSoak-";
                if (!root.StartsWith(
                        requiredPrefix,
                        StringComparison.OrdinalIgnoreCase) ||
                    root.Length <= requiredPrefix.Length)
                    throw new InvalidOperationException(
                        "The resource churn data root must use the temporary " +
                        "DesktopAICompanion-ResourceSoak-* boundary.");

                configuration = new RuntimeResourceChurnConfiguration
                {
                    MarkerPath = Path.Combine(
                        root,
                        "resource-churn-result.json"),
                    Cycles = ReadBoundedInteger(
                        "DESKTOPPET_RESOURCE_CHURN_CYCLES",
                        32,
                        4,
                        1000),
                    IntervalMilliseconds = ReadBoundedInteger(
                        "DESKTOPPET_RESOURCE_CHURN_INTERVAL_MS",
                        250,
                        50,
                        5000),
                    MinimumDurationMilliseconds = ReadBoundedInteger(
                        "DESKTOPPET_RESOURCE_CHURN_MIN_DURATION_MS",
                        10000,
                        1000,
                        900000),
                    ExitDelayMilliseconds = ReadBoundedInteger(
                        "DESKTOPPET_RESOURCE_CHURN_EXIT_DELAY_MS",
                        5000,
                        1000,
                        30000)
                };
                return true;
            }
            catch (Exception ex)
            {
                configuration = null;
                error = ex.Message;
                return false;
            }
        }

        private static int ReadBoundedInteger(
            string name,
            int fallback,
            int minimum,
            int maximum)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            int parsed;
            if (!int.TryParse(value, out parsed) ||
                parsed < minimum ||
                parsed > maximum)
                throw new InvalidOperationException(
                    name + " is outside its diagnostic bound.");
            return parsed;
        }
    }

    internal sealed class RuntimeResourceChurn : IDisposable
    {
        private readonly StartUp runtime;
        private readonly RuntimeResourceChurnConfiguration configuration;
        private readonly System.Windows.Forms.Timer cycleTimer;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private System.Windows.Forms.Timer exitTimer;
        private bool finished;
        private int cycles;
        private int speechAndPetCycles;
        private int trayAndMenuCycles;

        internal RuntimeResourceChurn(
            StartUp runtime,
            RuntimeResourceChurnConfiguration configuration)
        {
            this.runtime = runtime ??
                throw new ArgumentNullException("runtime");
            this.configuration = configuration ??
                throw new ArgumentNullException("configuration");
            cycleTimer = new System.Windows.Forms.Timer
            {
                Interval = configuration.IntervalMilliseconds
            };
            cycleTimer.Tick += CycleTimer_Tick;
            cycleTimer.Start();
        }

        private void CycleTimer_Tick(object sender, EventArgs e)
        {
            cycleTimer.Stop();
            try
            {
                RunCycle(cycles);
                cycles++;
                if (cycles >= configuration.Cycles &&
                    elapsed.ElapsedMilliseconds >=
                        configuration.MinimumDurationMilliseconds)
                {
                    Finish(true, null);
                    return;
                }
                cycleTimer.Start();
            }
            catch (Exception ex)
            {
                Finish(false, ex);
            }
        }

        private void RunCycle(int cycle)
        {
            string astral = char.ConvertFromUtf32(0x1F642);
            string speech =
                "Resource churn " + cycle + " " + astral +
                " exercises text, bubble paint, and companion image ownership.";
            if (!runtime.RunResourceChurnPetCycle(speech))
                throw new InvalidOperationException(
                    "The speech/pet churn path did not complete.");
            speechAndPetCycles++;

            // The About/Help dialogs are now themed WPF windows (the WinForms AboutBox/FormHelp were retired).
            // They aren't exercised in this GDI/handle-leak WinForms soak: showing a modal WPF dialog headlessly
            // in a loop is fragile, and the WPF chrome isn't what this harness measures. The speech/pet/tray/menu
            // churn below is the meaningful coverage.

            if (!runtime.RefreshTrayIconForResourceChurn())
                throw new InvalidOperationException(
                    "The tray icon refresh path did not complete.");
            ContextMenus.RefreshSpeechMenuItem();
            trayAndMenuCycles++;
        }

        private void Finish(bool passed, Exception failure)
        {
            if (finished) return;
            finished = true;
            cycleTimer.Stop();
            elapsed.Stop();
            WriteMarker(passed, failure);
            if (!passed) Environment.ExitCode = 1;

            exitTimer = new System.Windows.Forms.Timer
            {
                Interval = configuration.ExitDelayMilliseconds
            };
            exitTimer.Tick += ExitTimer_Tick;
            exitTimer.Start();
        }

        private void WriteMarker(bool passed, Exception failure)
        {
            string markerDirectory =
                Path.GetDirectoryName(configuration.MarkerPath);
            Directory.CreateDirectory(markerDirectory);
            var result = new JsonObject
            {
                ["result"] = passed ? "PASS" : "FAIL",
                ["cycles"] = cycles,
                ["targetCycles"] = configuration.Cycles,
                ["elapsedMilliseconds"] = elapsed.ElapsedMilliseconds,
                ["minimumDurationMilliseconds"] =
                    configuration.MinimumDurationMilliseconds,
                ["speechAndPetCycles"] = speechAndPetCycles,
                ["trayAndMenuCycles"] = trayAndMenuCycles,
                ["error"] = failure == null
                    ? null
                    : failure.GetType().Name + ": " + failure.Message
            };
            string temporary =
                configuration.MarkerPath + ".tmp-" +
                Process.GetCurrentProcess().Id;
            File.WriteAllText(
                temporary,
                result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            if (File.Exists(configuration.MarkerPath))
                File.Delete(configuration.MarkerPath);
            File.Move(temporary, configuration.MarkerPath);
        }

        private void ExitTimer_Tick(object sender, EventArgs e)
        {
            exitTimer.Stop();
            Application.Exit();
        }

        public void Dispose()
        {
            cycleTimer.Stop();
            cycleTimer.Tick -= CycleTimer_Tick;
            cycleTimer.Dispose();
            if (exitTimer != null)
            {
                exitTimer.Stop();
                exitTimer.Tick -= ExitTimer_Tick;
                exitTimer.Dispose();
                exitTimer = null;
            }
        }
    }
#endif
}
