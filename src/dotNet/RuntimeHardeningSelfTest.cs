using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace DesktopAICompanion
{
    /// <summary>
    /// In-process port of the former tests\pettyperegistry-selftest.ps1. The PowerShell harness
    /// LoadFrom-ed the shipped assembly under Windows PowerShell 5.1 (.NET Framework); on .NET 10 no
    /// PowerShell hosts a net10 assembly, so the registry-lifecycle assertions run here as the
    /// --pettyperegistry-selftest flag, using the app's own internal types directly.
    /// </summary>
    internal static class CompanionTypeRegistrySelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            void Check(string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); if (!cond) ok = false; }

            FieldInfo dispX = typeof(Xml).GetField("disposed", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo dispA = typeof(Animations).GetField("disposed", BindingFlags.NonPublic | BindingFlags.Instance);
            bool Disposed(FieldInfo f, object o) { return (bool)f.GetValue(o); }

            try
            {
                var reg = new CompanionTypeRegistry();
                CompanionTypeRegistry.Entry tmp;

                var x1 = new Xml(1); var a1 = new Animations(x1);
                CompanionTypeRegistry.Entry e1 = reg.Add("pink_sheep", x1, a1);
                Check("Add starts at refcount 0", e1.RefCount == 0);
                Check("entry is registered", reg.TryGet("pink_sheep", out tmp));
                reg.Increment(e1); reg.Increment(e1);
                Check("two Increments -> refcount 2", e1.RefCount == 2);
                reg.Decrement(e1);
                Check("one Decrement -> still alive at 1", e1.RefCount == 1 && reg.TryGet("pink_sheep", out tmp));
                Check("pair NOT disposed while refcount > 0", !Disposed(dispX, x1));
                reg.Decrement(e1);
                Check("removed from registry at refcount 0", !reg.TryGet("pink_sheep", out tmp));
                Check("Xml+Animations disposed exactly at zero", Disposed(dispX, x1) && Disposed(dispA, a1));

                bool threw = false;
                try { reg.Decrement(e1); } catch { threw = true; }
                Check("double-Decrement past zero is safe", !threw);

                // --- tray icon promotion (Windows 11 hidden-icons flyout) ---
                Check("absent IsPromoted -> promote", TrayPromotion.ShouldPromote(null));
                Check("IsPromoted=0 is the user hiding it -> leave alone", !TrayPromotion.ShouldPromote(0));
                Check("IsPromoted=1 is already shown -> leave alone", !TrayPromotion.ShouldPromote(1));
                // Two spellings of the SAME path, differing only in case, because that is the property under
                // test. The rename pass broke this by rewriting the mixed-case literal and leaving the
                // lowercase one, so the pair stopped describing one path and the check failed against
                // correct code.
                Check("exact path matches",
                    TrayPromotion.PathMatches(@"C:\P\DesktopAICompanion.exe", @"c:\p\desktopaicompanion.exe"));
                // The machine this shipped from held eight entries all named DesktopAICompanion.exe, so a filename or
                // suffix match would promote some other copy's icon.
                Check("a different copy of the same exe does NOT match",
                    !TrayPromotion.PathMatches(@"D:\build\DesktopAICompanion.exe", @"C:\P\DesktopAICompanion.exe"));
                Check("a packaged {GUID} path does NOT match",
                    !TrayPromotion.PathMatches(@"{6D809377}\WindowsApps\x\DesktopAICompanion.exe", @"C:\P\DesktopAICompanion.exe"));
                Check("null recorded path does NOT match", !TrayPromotion.PathMatches(null, @"C:\P\DesktopAICompanion.exe"));

                string scratch = @"Software\DesktopAICompanion\SelfTest\NotifyIconSettings";
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(scratch, false);
                try
                {
                    using (var settings = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(scratch))
                    {
                        string detail;
                        Check("no entry yet -> retry, do not claim success",
                            !TrayPromotion.TryPromoteIn(settings, @"C:\P\DesktopAICompanion.exe", "Pearl", out detail));

                        using (var other = settings.CreateSubKey("111"))
                        {
                            other.SetValue("ExecutablePath", @"D:\other\DesktopAICompanion.exe");
                        }
                        Check("a same-named exe at another path is not mistaken for ours",
                            !TrayPromotion.TryPromoteIn(settings, @"C:\P\DesktopAICompanion.exe", "Pearl", out detail));
                        using (var other = settings.OpenSubKey("111"))
                            Check("...and that other entry is left untouched", other.GetValue("IsPromoted") == null);

                        using (var mine = settings.CreateSubKey("222"))
                        {
                            mine.SetValue("ExecutablePath", @"C:\P\DesktopAICompanion.exe");
                            mine.SetValue("InitialTooltip", "eSheep Desktop Pet");
                        }
                        Check("our entry with IsPromoted absent -> promoted",
                            TrayPromotion.TryPromoteIn(settings, @"C:\P\DesktopAICompanion.exe", "Pearl Desktop Pet", out detail));
                        using (var mine = settings.OpenSubKey("222"))
                        {
                            Check("...IsPromoted written as 1", 1.Equals(mine.GetValue("IsPromoted")));
                            Check("...and the stale cached label corrected",
                                "Pearl Desktop Pet".Equals(mine.GetValue("InitialTooltip") as string));
                        }

                        using (var mine = settings.OpenSubKey("222", true))
                        {
                            mine.SetValue("IsPromoted", 0, Microsoft.Win32.RegistryValueKind.DWord);
                            mine.SetValue("InitialTooltip", "eSheep Desktop Pet");
                        }
                        Check("a deliberately hidden icon settles without a rewrite",
                            TrayPromotion.TryPromoteIn(settings, @"C:\P\DesktopAICompanion.exe", "Ruby Desktop Pet", out detail));
                        using (var mine = settings.OpenSubKey("222"))
                        {
                            Check("...IsPromoted stays 0, the pet does not overrule the user",
                                0.Equals(mine.GetValue("IsPromoted")));
                            // The label is not a user preference, so it is corrected even for an icon the
                            // user keeps hidden -- that is precisely where a wrong label costs them the find.
                            Check("...but the label is still corrected on a hidden icon",
                                "Ruby Desktop Pet".Equals(mine.GetValue("InitialTooltip") as string));
                        }
                    }
                    string noKey;
                    Check("no NotifyIconSettings at all (pre-Win11) settles, never retries",
                        TrayPromotion.TryPromoteIn(null, @"C:\P\DesktopAICompanion.exe", "Pearl", out noKey));
                }
                finally
                {
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\DesktopAICompanion\SelfTest", false);
                }

                // --- autostart survives the product rename ---
                // Task Manager's Startup tab shows the registry VALUE NAME, so it had to be renamed with
                // everything else. The old entry does not remove itself and points into the old install
                // directory, so without this it is a startup item that silently fails forever while the
                // Options checkbox reads "off" for someone who had switched it on.
                string startupScratch = @"Software\DesktopAICompanion\SelfTest\Run";
                string previousRedirect = Environment.GetEnvironmentVariable("DESKTOP_AI_COMPANION_STARTUP_TEST_KEY");
                Environment.SetEnvironmentVariable("DESKTOP_AI_COMPANION_STARTUP_TEST_KEY", startupScratch);
                try
                {
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(startupScratch, false);
                    using (var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(startupScratch))
                        run.SetValue("DesktopPet AI Edition", "\"C:\\old\\path\\DesktopAICompanion.exe\"");
                    Check("a legacy autostart entry still reads as ENABLED after the rename",
                        StartupRegistration.IsEnabled());

                    StartupRegistration.Set(true);
                    using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startupScratch))
                    {
                        Check("enabling writes the new value name", run.GetValue("Desktop AI Companion") != null);
                        Check("...and removes the legacy one, so the dead entry cannot linger",
                            run.GetValue("DesktopPet AI Edition") == null);
                    }

                    // Disabling has to clear the legacy name too, or the app still starts with Windows from
                    // an entry the user just switched off.
                    using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startupScratch, true))
                        run.SetValue("DesktopPet AI Edition", "\"C:\\old\\path\\DesktopAICompanion.exe\"");
                    StartupRegistration.Set(false);
                    using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startupScratch))
                    {
                        Check("disabling clears BOTH names",
                            run.GetValue("Desktop AI Companion") == null &&
                            run.GetValue("DesktopPet AI Edition") == null);
                    }
                    Check("nothing registered reads as disabled", !StartupRegistration.IsEnabled());
                }
                finally
                {
                    Environment.SetEnvironmentVariable("DESKTOP_AI_COMPANION_STARTUP_TEST_KEY", previousRedirect);
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\DesktopAICompanion\SelfTest", false);
                }

                var x2 = new Xml(2); var a2 = new Animations(x2);
                CompanionTypeRegistry.Entry e2 = reg.Add("red_sheep", x2, a2);
                reg.DropIfUnused(e2);
                Check("DropIfUnused disposes an unspawned type", !reg.TryGet("red_sheep", out tmp) && Disposed(dispX, x2));

                var x3 = new Xml(1); var a3 = new Animations(x3);
                CompanionTypeRegistry.Entry e3 = reg.Add("blue_sheep", x3, a3);
                reg.Increment(e3); reg.DropIfUnused(e3);
                Check("DropIfUnused leaves an in-use type alone", reg.TryGet("blue_sheep", out tmp) && !Disposed(dispX, x3));

                reg.DisposeAll();
                Check("DisposeAll disposes remaining pairs", !reg.TryGet("blue_sheep", out tmp) && Disposed(dispX, x3));

                // --- re-staging an id that is already registered ---
                // Displacing an UNREFERENCED entry must free it: nothing else owns that pair, so skipping
                // the dispose leaks it outright.
                var reg2 = new CompanionTypeRegistry();
                var xOldFree = new Xml(1); var aOldFree = new Animations(xOldFree);
                reg2.Add("green_sheep", xOldFree, aOldFree);
                var xNewFree = new Xml(1); var aNewFree = new Animations(xNewFree);
                CompanionTypeRegistry.Entry replacedFree = reg2.Add("green_sheep", xNewFree, aNewFree);
                Check("re-staging disposes a displaced UNREFERENCED pair",
                    Disposed(dispX, xOldFree) && Disposed(dispA, aOldFree));
                Check("re-staging keeps the new pair alive and registered",
                    !Disposed(dispX, xNewFree) && reg2.TryGet("green_sheep", out tmp) && ReferenceEquals(tmp, replacedFree));

                // Displacing an entry that live pets still BORROW must not free it: FormCompanion never disposes
                // its Xml/Animations, so disposing here would pull the sprites out from under a live pet.
                var xOldBusy = new Xml(1); var aOldBusy = new Animations(xOldBusy);
                CompanionTypeRegistry.Entry busy = reg2.Add("orange_sheep", xOldBusy, aOldBusy);
                reg2.Increment(busy);
                var xNewBusy = new Xml(1); var aNewBusy = new Animations(xNewBusy);
                CompanionTypeRegistry.Entry fresh = reg2.Add("orange_sheep", xNewBusy, aNewBusy);
                Check("re-staging does NOT dispose a displaced pair a live pet still borrows",
                    !Disposed(dispX, xOldBusy) && !Disposed(dispA, aOldBusy));

                // The regression this guards: DisposeEntry used to remove by KEY, so the displaced entry
                // reaching zero evicted the NEW entry from the map. A live pet's type then vanished from the
                // registry and the next spawn staged a third duplicate copy of the same pet.
                reg2.Decrement(busy);
                Check("the displaced pair is freed when ITS last pet closes", Disposed(dispX, xOldBusy));
                Check("...without evicting the entry that now owns the id",
                    reg2.TryGet("orange_sheep", out tmp) && ReferenceEquals(tmp, fresh) && !Disposed(dispX, xNewBusy));
                reg2.Increment(fresh); reg2.Decrement(fresh);
                Check("the current entry still disposes normally at zero",
                    !reg2.TryGet("orange_sheep", out tmp) && Disposed(dispX, xNewBusy));
                reg2.DisposeAll();

                // --- the on-screen mix, the single choke point for persistence AND the tray ---
                var xMix = new Xml(1); var aMix = new Animations(xMix);
                var installed = new CompanionTypeRegistry.Entry { Id = "pearl", Xml = xMix, Animations = aMix };
                var preview = new CompanionTypeRegistry.Entry { Id = "preview:abc", IsTransient = true };
                System.Collections.Generic.List<CompanionCountEntry> mix = StartUp.DeriveOnScreenMix(
                    new[] { null, null, preview, installed });
                Check("mix counts active-type pets under \"\" in first-appearance order",
                    mix.Count == 2 && mix[0].Id == "" && mix[0].Count == 2);
                Check("mix counts an installed type under its id", mix[1].Id == "pearl" && mix[1].Count == 1);
                Check("a TRANSIENT (preview) pet never reaches the mix",
                    mix.TrueForAll(e => e.Id.IndexOf("preview", StringComparison.OrdinalIgnoreCase) < 0));
                Check("a screen holding only previews yields an EMPTY mix (nothing to persist)",
                    StartUp.DeriveOnScreenMix(new[] { preview }).Count == 0);
                Check("no pets yields an empty mix", StartUp.DeriveOnScreenMix(new CompanionTypeRegistry.Entry[0]).Count == 0);
                aMix.Dispose(); xMix.Dispose();

                if (ok) sb.AppendLine("PASS: CompanionTypeRegistry lifetime self-test.");
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }

            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-pettyperegistry-selftest.txt"), sb.ToString()); } catch { }
            return ok;
        }
    }

    /// <summary>
    /// In-process port of the reflection half of tests\runtime-hardening-selftest.ps1 (the animation/
    /// geometry/runtime-limit invariants). Runs as the --hardening-selftest flag. AnimationRuntimeLimits
    /// (public static math) is called directly; non-public members are exercised by reflection over this
    /// assembly, mirroring the original harness exactly. The source-text invariant checks remain in the
    /// PowerShell script (they read .cs files, which the shipped app cannot).
    /// </summary>
    internal static class RuntimeHardeningSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            void Check(string name, bool cond) { sb.AppendLine((cond ? "PASS: " : "FAIL: ") + name); if (!cond) ok = false; }
            void CheckRejects(string name, Action action)
            {
                try { action(); Check(name, false); }
                catch (Exception ex)
                {
                    Exception inner = ex;
                    while (inner.InnerException != null) inner = inner.InnerException;
                    Check(name, inner is InvalidDataException);
                }
            }

            Assembly asm = typeof(RuntimeHardeningSelfTest).Assembly;
            const BindingFlags PubInstance = BindingFlags.Public | BindingFlags.Instance;
            const BindingFlags NpStatic = BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags NpInstance = BindingFlags.NonPublic | BindingFlags.Instance;

            try
            {
                // ---- AnimationRuntimeLimits: direct calls (public static math) ----
                Check("no-repeat total steps", AnimationRuntimeLimits.CalculateTotalSteps(3, 1, 0) == 3);
                Check("repeat total steps", AnimationRuntimeLimits.CalculateTotalSteps(3, 1, 1) == 5);
                Check("repeat clamp", AnimationRuntimeLimits.CalculateTotalSteps(3, 1, int.MaxValue) == 2003);
                Check("total-step cap", AnimationRuntimeLimits.CalculateTotalSteps(16384, 0, 1000) == 1000000);
                Check("one-frame last step", AnimationRuntimeLimits.LastStepIndex(1) == 0);
                Check("multi-frame last step", AnimationRuntimeLimits.LastStepIndex(5) == 4);
                Check("one-frame interpolation divisor", AnimationRuntimeLimits.InterpolationSteps(1) == 1);
                Check("endpoint interpolation divisor", AnimationRuntimeLimits.InterpolationSteps(5) == 4);

                var frames = new int[5];
                for (int i = 0; i < 5; i++) frames[i] = AnimationRuntimeLimits.SequenceFrameIndex(i, 3, 1);
                Check("repeat-from frame order", string.Join(",", frames) == "0,1,2,1,2");

                Check("negative coordinate clamp", AnimationRuntimeLimits.ClampLocalPosition((long)int.MinValue, 1920) == -8192);
                Check("positive coordinate clamp", AnimationRuntimeLimits.ClampLocalPosition((long)int.MaxValue, 1920) == 10112);
                Check("normal mirror arithmetic", AnimationRuntimeLimits.MirrorLocalX(100, 1920, 64) == 1756);

                int rightParent = AnimationRuntimeLimits.MirrorLocalX(100, 1920, 64);
                int canonParent = AnimationRuntimeLimits.CanonicalParentX(rightParent, true, 1920, 64);
                Check("flipped parent is canonicalized before child expression evaluation", canonParent == 100);
                int leftChild = canonParent + 64 + 10;
                int rightChild = AnimationRuntimeLimits.MirrorLocalX(leftChild, 1920, 32);
                Check("child placement has left-right symmetry with one full-screen mirror", leftChild + rightChild == 1888);

                Check("overflow-safe mirror arithmetic", AnimationRuntimeLimits.MirrorLocalX(int.MinValue, 1920, 64) == 10112);
                Check("maximum monitor extent never wraps", AnimationRuntimeLimits.ClampLocalPosition((long)int.MaxValue + 1L, int.MaxValue) == int.MaxValue);
                Check("maximum-width mirror never wraps", AnimationRuntimeLimits.MirrorLocalX(-100, int.MaxValue, 0) == int.MaxValue);
                Check("positive infinite virtual coordinate clamps", AnimationRuntimeLimits.ClampVirtualPosition(double.PositiveInfinity, int.MaxValue, int.MaxValue) == (double)int.MaxValue);

                Check("first absolute clipping cut", AnimationRuntimeLimits.ClipCut(14.0, 64) == 14);
                Check("second absolute clipping cut", AnimationRuntimeLimits.ClipCut(24.0, 64) == 24);
                Check("first visible clipping extent", 64 - AnimationRuntimeLimits.ClipCut(14.0, 64) == 50);
                Check("second visible clipping extent is not cumulative", 64 - AnimationRuntimeLimits.ClipCut(24.0, 64) == 40);
                Check("large positive clipping jump clamps to full extent", AnimationRuntimeLimits.ClipCut(32768.0, 64) == 64);
                Check("negative clipping amount is ignored", AnimationRuntimeLimits.ClipCut(-32768.0, 64) == 0);
                Check("bottom clipping cut", AnimationRuntimeLimits.ClipCut(24.0, 64) == 24);
                Check("simultaneous horizontal cuts retain viewport slice", 40 - AnimationRuntimeLimits.ClipCut(10.0, 40) - AnimationRuntimeLimits.ClipCut(10.0, 40) == 20);
                Check("positive form coordinate saturation", AnimationRuntimeLimits.ClampFormCoordinate(double.PositiveInfinity) == int.MaxValue);
                Check("negative form coordinate saturation", AnimationRuntimeLimits.ClampFormCoordinate(double.NegativeInfinity) == int.MinValue);

                Check("exact full left cut is outside", AnimationRuntimeLimits.IsSpriteFullyOutside(-64.0, 100.0, 64, 64, 0, 0, 1920, 1080));
                Check("exact full right cut is outside", AnimationRuntimeLimits.IsSpriteFullyOutside(1920.0, 100.0, 64, 64, 0, 0, 1920, 1080));
                Check("exact full top cut is outside", AnimationRuntimeLimits.IsSpriteFullyOutside(100.0, -64.0, 64, 64, 0, 0, 1920, 1080));
                Check("exact full bottom cut is outside", AnimationRuntimeLimits.IsSpriteFullyOutside(100.0, 1080.0, 64, 64, 0, 0, 1920, 1080));
                Check("one-pixel inward slice remains visible", !AnimationRuntimeLimits.IsSpriteFullyOutside(-63.0, 100.0, 64, 64, 0, 0, 1920, 1080));
                Check("extreme monitor edge arithmetic does not wrap", AnimationRuntimeLimits.IsSpriteFullyOutside(4294967294.0, 4294967294.0, 64, 64, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue));

                // ---- TValue evaluator ownership + scale isolation + weight + sprite budget (reflection) ----
                Type xmlT = asm.GetType("DesktopAICompanion.Xml", true);
                Type tvalueT = asm.GetType("DesktopAICompanion.TValue", true);
                Type animT = asm.GetType("DesktopAICompanion.Animations", true);
                MethodInfo xmlDispose = xmlT.GetMethod("Dispose", PubInstance);
                object xmlOne = Activator.CreateInstance(xmlT, new object[] { 1 });
                object xmlTwo = Activator.CreateInstance(xmlT, new object[] { 2 });
                try
                {
                    MethodInfo compute = xmlT.GetMethod("GetXMLCompute", PubInstance);
                    object valueOne = compute.Invoke(xmlOne, new object[] { "scale", "ownership-one" });
                    object valueTwo = compute.Invoke(xmlTwo, new object[] { "scale", "ownership-two" });
                    FieldInfo evaluator = tvalueT.GetField("Evaluator", NpInstance);
                    Check("first TValue evaluator ownership", ReferenceEquals(xmlOne, evaluator.GetValue(valueOne)));
                    Check("second TValue evaluator ownership", ReferenceEquals(xmlTwo, evaluator.GetValue(valueTwo)));
                    MethodInfo getRaw = tvalueT.GetMethod("GetRawValue", PubInstance);
                    Check("first evaluator scale isolation", (int)getRaw.Invoke(valueOne, new object[] { -1 }) == 1);
                    Check("second evaluator scale isolation", (int)getRaw.Invoke(valueTwo, new object[] { -1 }) == 2);

                    object animations = Activator.CreateInstance(animT, new object[] { xmlOne });
                    try
                    {
                        MethodInfo nextWeight = animT.GetMethod("NextWeight", NpInstance);
                        long upper = (long)int.MaxValue + 1L;
                        bool inRange = true;
                        for (int i = 0; i < 2048; i++)
                        {
                            long sample = Convert.ToInt64(nextWeight.Invoke(animations, new object[] { upper }));
                            if (sample < 0 || sample >= upper) { inRange = false; break; }
                        }
                        Check("weight selection above int.MaxValue", inRange);
                    }
                    finally { animT.GetMethod("Dispose", PubInstance).Invoke(animations, new object[0]); }

                    MethodInfo validateBudget = xmlT.GetMethod("ValidateSpriteBudget", NpStatic);
                    validateBudget.Invoke(null, new object[] { 32, 32, 128, 128 });
                    Check("exact sprite pixel budget accepted", true);
                    CheckRejects("oversized generated-frame count rejected", () => validateBudget.Invoke(null, new object[] { 41, 25, 1, 1 }));
                    CheckRejects("oversized generated-pixel budget rejected", () => validateBudget.Invoke(null, new object[] { 32, 32, 256, 256 }));
                }
                finally
                {
                    xmlDispose.Invoke(xmlOne, new object[0]);
                    xmlDispose.Invoke(xmlTwo, new object[0]);
                }

                // ---- bundled pet decodes at 4x within budgets (reflection) ----
                Type resourcesT = asm.GetType("DesktopAICompanion.Properties.Resources", true);
                PropertyInfo animProp = resourcesT.GetProperty("animations", NpStatic);
                string bundledXml = (string)animProp.GetValue(null, new object[0]);
                object scaledXml = Activator.CreateInstance(xmlT, new object[] { 4 });
                try
                {
                    MethodInfo tryRead = xmlT.GetMethod("TryReadXml", PubInstance);
                    Check("bundled pet decodes at requested 4x scale", (bool)tryRead.Invoke(scaledXml, new object[] { bundledXml, null }));
                    PropertyInfo spriteCountP = xmlT.GetProperty("SpriteCount", BindingFlags.Instance | BindingFlags.NonPublic);
                    int spriteCount = (int)spriteCountP.GetValue(scaledXml, new object[0]);
                    Check("bundled pet generated-frame budget", spriteCount <= 1024);
                    long sw = Convert.ToInt64(MemberValue(xmlT, scaledXml, "spriteWidth"));
                    long sh = Convert.ToInt64(MemberValue(xmlT, scaledXml, "spriteHeight"));
                    Check("bundled pet generated-pixel budget", ((long)spriteCount * sw * sh) <= (16L * 1024L * 1024L));
                }
                finally { xmlDispose.Invoke(scaledXml, new object[0]); }

                // ---- bundled pet honours a SUB-1 scale (the size slider going below 1x) ----
                object oneXxml = Activator.CreateInstance(xmlT, new object[] { 1.0 });
                object halfXml = Activator.CreateInstance(xmlT, new object[] { 0.5 });
                try
                {
                    MethodInfo tryRead2 = xmlT.GetMethod("TryReadXml", PubInstance);
                    tryRead2.Invoke(oneXxml, new object[] { bundledXml, null });
                    tryRead2.Invoke(halfXml, new object[] { bundledXml, null });
                    long w1 = Convert.ToInt64(MemberValue(xmlT, oneXxml, "spriteWidth"));
                    long wHalf = Convert.ToInt64(MemberValue(xmlT, halfXml, "spriteWidth"));
                    Check("sub-1 scale shrinks the frame below 1x", wHalf >= 1 && wHalf < w1);
                    Check("0.5x is about half the 1x width", System.Math.Abs(wHalf * 2 - w1) <= 2);
                    Check("sub-1 keeps the 'scale' expression integer >= 1",
                        (int)xmlT.GetProperty("ScaleFactor", PubInstance).GetValue(halfXml, null) == 1);
                }
                finally { xmlDispose.Invoke(oneXxml, new object[0]); xmlDispose.Invoke(halfXml, new object[0]); }

                // ---- speech bubble yields/restores topmost around fullscreen (reflection) ----
                Type speechT = asm.GetType("DesktopAICompanion.FormSpeech", true);
                var speech = (Form)Activator.CreateInstance(speechT, true);
                try
                {
                    MethodInfo setSuppressed = speechT.GetMethod("SetFullscreenSuppressed", NpInstance);
                    setSuppressed.Invoke(speech, new object[] { true });
                    Check("speech bubble yields to fullscreen", !speech.TopMost);
                    setSuppressed.Invoke(speech, new object[] { false });
                    Check("speech bubble restores topmost after fullscreen", speech.TopMost);
                }
                finally { speech.Dispose(); }

                // ---- FormCompanion.ChildBudget per-root + process-global caps, reuse, prune (reflection) ----
                Type formT = asm.GetType("DesktopAICompanion.FormCompanion", true);
                Type budgetT = formT.GetNestedType("ChildBudget", BindingFlags.NonPublic);
                MethodInfo tryAcquire = budgetT.GetMethod("TryAcquire", PubInstance);
                MethodInfo release = budgetT.GetMethod("Release", PubInstance);
                FieldInfo activeF = budgetT.GetField("active", NpInstance);

                object[] budgets = { Activator.CreateInstance(budgetT, true), Activator.CreateInstance(budgetT, true), Activator.CreateInstance(budgetT, true) };
                int[] held = { 0, 0, 0 };
                try
                {
                    for (int i = 0; i < 32; i++) { if (!(bool)tryAcquire.Invoke(budgets[0], new object[0])) throw new Exception("first root stopped at " + i); held[0]++; }
                    Check("per-root child cap", !(bool)tryAcquire.Invoke(budgets[0], new object[0]));
                    for (int i = 0; i < 32; i++) { if (!(bool)tryAcquire.Invoke(budgets[1], new object[0])) throw new Exception("process stopped at " + (i + 32)); held[1]++; }
                    Check("process-global child cap", !(bool)tryAcquire.Invoke(budgets[2], new object[0]));
                    release.Invoke(budgets[0], new object[0]); held[0]--;
                    Check("released global child slot reusable", (bool)tryAcquire.Invoke(budgets[2], new object[0])); held[2]++;
                }
                finally
                {
                    for (int b = 0; b < budgets.Length; b++) while (held[b] > 0) { release.Invoke(budgets[b], new object[0]); held[b]--; }
                }

                object parentForm = null;
                object pruneBudget = Activator.CreateInstance(budgetT, true);
                var disposedChildren = new System.Collections.Generic.List<IDisposable>();
                try
                {
                    parentForm = Activator.CreateInstance(formT);
                    FieldInfo childsF = formT.GetField("childs", NpInstance);
                    var childs = (System.Collections.IList)childsF.GetValue(parentForm);
                    ConstructorInfo childCtor = null;
                    foreach (ConstructorInfo c in formT.GetConstructors(NpInstance)) if (c.GetParameters().Length == 8) { childCtor = c; break; }
                    if (childCtor == null) throw new Exception("private 8-arg child FormCompanion constructor not found");

                    for (int i = 0; i < 2; i++)
                    {
                        if (!(bool)tryAcquire.Invoke(pruneBudget, new object[0])) throw new Exception("unable to reserve child slot " + i);
                        object child = childCtor.Invoke(new object[] { null, null, parentForm, Point.Empty, false, 0, 1, pruneBudget });
                        childs.Add(child);
                        ((IDisposable)child).Dispose();
                        disposedChildren.Add((IDisposable)child);
                    }

                    formT.GetMethod("PruneClosedChildren", NpInstance).Invoke(parentForm, new object[0]);
                    Check("adjacent disposed children pruned once", childs.Count == 0);
                    Check("disposed-child budget slots released", (int)activeF.GetValue(pruneBudget) == 0);
                }
                finally
                {
                    foreach (IDisposable child in disposedChildren) { try { child.Dispose(); } catch { } }
                    if (parentForm != null) { try { ((IDisposable)parentForm).Dispose(); } catch { } }
                    int activeSlots = (int)activeF.GetValue(pruneBudget);
                    while (activeSlots-- > 0) release.Invoke(pruneBudget, new object[0]);
                }

                // ---- ReadBoundedPetXml: BOM decode + oversized rejection (reflection) ----
                MethodInfo readBounded = formT.GetMethod("ReadBoundedPetXml", NpStatic);
                string tempFile = Path.Combine(Path.GetTempPath(), "DesktopAICompanion-bounded-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    File.WriteAllText(tempFile, "<root />", new UTF8Encoding(true));
                    Check("bounded UTF-8 BOM decode", (string)readBounded.Invoke(null, new object[] { tempFile }) == "<root />");
                    using (FileStream fs = File.Open(tempFile, FileMode.Create, FileAccess.Write, FileShare.None)) fs.SetLength(CompanionXmlValidator.MaximumXmlBytes + 1L);
                    CheckRejects("maximum-plus-one XML read rejected", () => readBounded.Invoke(null, new object[] { tempFile }));
                }
                finally { try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { } }

                // Tile bleed on the smooth-downscale path. Scaling a sub-rectangle of a bigger bitmap makes
                // the interpolation kernel sample PAST the source rectangle, so a downscaled frame picked up
                // a column of the neighbouring tile: the reported dark line down the left edge of a pet's
                // fall frame. Only converted (alpha) pets being downscaled take that path.
                //
                // The fixture makes the bleed unmissable: tile 0 is solid black, tile 1 solid white, and
                // tile 1 is downscaled. Any dark pixel in the result came from across the boundary, because
                // nothing in tile 1 is dark.
                using (var sheet = new Bitmap(64, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(sheet))
                    {
                        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                        g.FillRectangle(Brushes.Black, 0, 0, 32, 32);
                        g.FillRectangle(Brushes.White, 32, 0, 32, 32);
                    }
                    using (Bitmap staged = Xml.StageOneTileForDiagnostics(sheet, 2, 1, 1, 16, 16, true))
                    {
                        int darkest = 255;
                        for (int y = 0; y < staged.Height; y++)
                            for (int x = 0; x < staged.Width; x++)
                            {
                                Color c = staged.GetPixel(x, y);
                                if (c.A == 0) continue;
                                if (c.R < darkest) darkest = c.R;
                            }
                        Check("smooth downscale does not sample across the tile boundary (darkest="
                              + darkest + ")", darkest >= 250);
                    }
                }

                // Drag swing. A converted pet's drag animation carries up to 7 poses, one per horizontal
                // offset band between its body and the cursor, and the pet SWINGS from your hand. Positional
                // lag cannot drive it (the drag branch snaps the pet's centre onto the cursor every tick, so
                // lag is always zero), so it is driven by cursor VELOCITY instead. Frame 0 is the body
                // trailing furthest LEFT, so moving the cursor RIGHT must select a LOW index.
                Check("swing: still cursor hangs at the centre pose",
                    FormCompanion.DragSwingFrameIndexFor(0.0, 7) == 3);
                Check("swing: cursor moving RIGHT trails the body left (frame 0)",
                    FormCompanion.DragSwingFrameIndexFor(40.0, 7) == 0);
                Check("swing: cursor moving LEFT trails the body right (last frame)",
                    FormCompanion.DragSwingFrameIndexFor(-40.0, 7) == 6);
                Check("swing: a gentle nudge does not jump straight to the extreme",
                    FormCompanion.DragSwingFrameIndexFor(4.0, 7) > 0 &&
                    FormCompanion.DragSwingFrameIndexFor(4.0, 7) < 3);
                Check("swing: the mapping is monotonic across the range",
                    FormCompanion.DragSwingFrameIndexFor(-18.0, 7) >= FormCompanion.DragSwingFrameIndexFor(-9.0, 7) &&
                    FormCompanion.DragSwingFrameIndexFor(-9.0, 7) >= FormCompanion.DragSwingFrameIndexFor(0.0, 7) &&
                    FormCompanion.DragSwingFrameIndexFor(0.0, 7) >= FormCompanion.DragSwingFrameIndexFor(9.0, 7) &&
                    FormCompanion.DragSwingFrameIndexFor(9.0, 7) >= FormCompanion.DragSwingFrameIndexFor(18.0, 7));
                // A single-frame drag is most Android bundles, and it must not index out of range.
                Check("swing: a single-pose drag stays on frame 0",
                    FormCompanion.DragSwingFrameIndexFor(999.0, 1) == 0 &&
                    FormCompanion.DragSwingFrameIndexFor(-999.0, 1) == 0);
                Check("swing: an absurd velocity clamps instead of overflowing",
                    FormCompanion.DragSwingFrameIndexFor(100000.0, 5) == 0 &&
                    FormCompanion.DragSwingFrameIndexFor(-100000.0, 5) == 4);

                // Gaze. A converted pet's "sit and look at the mouse" animation is tagged faceCursor, and the
                // host aims it as the animation starts. The comparison is against the CHARACTER's centre, not
                // the window's, so these assertions are about the rule and the insets are tested separately.
                //
                // The sign is the whole thing and it is easy to get backwards, so it is pinned in both
                // directions rather than asserted once: unmirrored sprite art is LEFT-facing, the engine
                // mirrors for rightward, and "cursor is left of me" therefore means "do not mirror".
                Check("gaze: a cursor left of the character faces left",
                    FormCompanion.ShouldFaceLeft(100.0, 500.0));
                Check("gaze: a cursor right of the character faces right",
                    !FormCompanion.ShouldFaceLeft(900.0, 500.0));
                // Dead centre must not flip on rounding noise. Either answer is defensible; what matters is
                // that it is STABLE, because a pet standing under the pointer would otherwise strobe.
                Check("gaze: a cursor exactly on the centre is stable",
                    FormCompanion.ShouldFaceLeft(500.0, 500.0) == FormCompanion.ShouldFaceLeft(500.0, 500.0) &&
                    !FormCompanion.ShouldFaceLeft(500.0, 500.0));
                Check("gaze: a character off the left of the screen still aims correctly",
                    !FormCompanion.ShouldFaceLeft(10.0, -120.0) && FormCompanion.ShouldFaceLeft(-300.0, -120.0));

                // Window EDGE discrimination. The host used to raise a bare WINDOW at all three window
                // borders, so a pet could not tell "I walked off the left side" from "I landed on the top".
                // It now raises WINDOW plus a discriminator, and these assertions pin both halves of the
                // bargain: the new values narrow, and the old one still catches everything.
                TNextAnimation.TOnly onLeft = TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_LEFT;
                TNextAnimation.TOnly onRight = TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_RIGHT;
                TNextAnimation.TOnly onTop = TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_TOP;

                // The compatibility half. 955 window edges ship in the hand-authored pets and every one of
                // them says `only="window"`; if any of these three went false those pets would stop
                // transitioning at a window and simply walk off it.
                Check("window: a generic window edge still fires at all three window borders",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW, onLeft) &&
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW, onRight) &&
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW, onTop));
                Check("window: horizontal+ still fires at a window border",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.HORIZONTAL_, onTop));

                // The discrimination half, stated as three exclusions rather than three inclusions, because
                // an implementation that simply matched everything would pass the inclusions.
                Check("window: a left-edge animation fires on the left edge only",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_LEFT, onLeft) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_LEFT, onRight) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_LEFT, onTop));
                Check("window: a right-edge animation fires on the right edge only",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_RIGHT, onRight) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_RIGHT, onLeft) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_RIGHT, onTop));
                Check("window: a top-edge animation fires on the top edge only",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_TOP, onTop) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_TOP, onLeft) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_TOP, onRight));

                // A window edge must not leak into a SCREEN edge, which is a different situation entirely.
                Check("window: window edges do not fire at screen borders",
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_LEFT, TNextAnimation.TOnly.VERTICAL) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_TOP, TNextAnimation.TOnly.HORIZONTAL) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_RIGHT, TNextAnimation.TOnly.TASKBAR));

                // NONE is "no flag given" and is taken everywhere. The three new bits all sit inside its
                // 0x7F mask, so a mistake here would silently turn every unconditional edge into a gated one.
                Check("window: an unconditional edge is still taken at every window border",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.NONE, onLeft) &&
                    TNextAnimation.Eligible(TNextAnimation.TOnly.NONE, onRight) &&
                    TNextAnimation.Eligible(TNextAnimation.TOnly.NONE, onTop) &&
                    TNextAnimation.Eligible(TNextAnimation.TOnly.NONE, TNextAnimation.TOnly.NONE));

                // Vocabulary: the attribute the converter writes has to reach the flag the host matches.
                Check("window: the only= vocabulary maps to the right flags",
                    Xml.ParseOnlyFlag("window-left") == TNextAnimation.TOnly.WINDOW_LEFT &&
                    Xml.ParseOnlyFlag("window-right") == TNextAnimation.TOnly.WINDOW_RIGHT &&
                    Xml.ParseOnlyFlag("window-top") == TNextAnimation.TOnly.WINDOW_TOP &&
                    Xml.ParseOnlyFlag("window") == TNextAnimation.TOnly.WINDOW &&
                    Xml.ParseOnlyFlag("vertical") == TNextAnimation.TOnly.VERTICAL);
                // ...and the validator has to let such a pet in at all. These two lists are maintained
                // separately, and a value the parser understands but the validator refuses does not degrade:
                // it rejects the entire pet.
                Check("window: the validator accepts the window-edge vocabulary",
                    CompanionXmlValidator.IsAllowedOnly("window-left") &&
                    CompanionXmlValidator.IsAllowedOnly("window-right") &&
                    CompanionXmlValidator.IsAllowedOnly("window-top") &&
                    CompanionXmlValidator.IsAllowedOnly("window") &&
                    CompanionXmlValidator.IsAllowedOnly("vertical"));
                // The accept list is a list, not a prefix match. It first used "window-bottom" as the example
                // of something unimplemented, which Phase E then implemented -- so the example is now a
                // plausible NEAR-MISS instead, which is the shape a real mistake takes.
                Check("window: the validator still refuses a value nothing implements",
                    !CompanionXmlValidator.IsAllowedOnly("window-side") &&
                    !CompanionXmlValidator.IsAllowedOnly("window-") &&
                    !CompanionXmlValidator.IsAllowedOnly("sideways"));

                // Gripping the SIDE of a window. The opt-in rule is the whole safety story: 955 window edges
                // ship in the hand-authored pets saying only="window", and every one of them would be
                // recruited into this behaviour by a bit test rather than an exact match.
                Check("grip: only an explicit window-left/right edge takes hold",
                    FormCompanion.GripFor(TNextAnimation.TOnly.WINDOW_LEFT) == FormCompanion.WindowGrip.Left &&
                    FormCompanion.GripFor(TNextAnimation.TOnly.WINDOW_RIGHT) == FormCompanion.WindowGrip.Right);
                Check("grip: the old generic window edge does NOT take hold",
                    FormCompanion.GripFor(TNextAnimation.TOnly.WINDOW) == FormCompanion.WindowGrip.None &&
                    FormCompanion.GripFor(TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_LEFT) == FormCompanion.WindowGrip.None &&
                    FormCompanion.GripFor(TNextAnimation.TOnly.NONE) == FormCompanion.WindowGrip.None &&
                    FormCompanion.GripFor(TNextAnimation.TOnly.HORIZONTAL_) == FormCompanion.WindowGrip.None);

                // Where a gripping pet sits. The insets are the character's transparent padding, and putting
                // one on the wrong side leaves the padding against the glass and the character floating a
                // hundred pixels off it -- exactly the bug the screen-edge inset work existed to fix.
                // Window 400..900, form 256 wide, 30px of padding on the left and 20 on the right.
                Check("grip: the left grip puts the character's left edge on the window's left edge",
                    FormCompanion.GripPositionX(FormCompanion.WindowGrip.Left, 400, 900, 256, 30, 20) == 370.0);
                Check("grip: the right grip puts the character's right edge on the window's right edge",
                    FormCompanion.GripPositionX(FormCompanion.WindowGrip.Right, 400, 900, 256, 30, 20) == 664.0);
                // 900 - 256 + 20 = 664, so the character's right edge (664 + 256 - 20) lands on 900.
                Check("grip: the right grip's character edge really is the window edge",
                    FormCompanion.GripPositionX(FormCompanion.WindowGrip.Right, 400, 900, 256, 30, 20) + 256 - 20 == 900.0);
                Check("grip: the left grip's character edge really is the window edge",
                    FormCompanion.GripPositionX(FormCompanion.WindowGrip.Left, 400, 900, 256, 30, 20) + 30 == 400.0);
                // A pet whose sprite fills its frame (every hand-authored pet) sits flush either way.
                Check("grip: a zero-inset pet sits flush against both sides",
                    FormCompanion.GripPositionX(FormCompanion.WindowGrip.Left, 400, 900, 256, 0, 0) == 400.0 &&
                    FormCompanion.GripPositionX(FormCompanion.WindowGrip.Right, 400, 900, 256, 0, 0) == 644.0);

                // Hanging from a window's UNDERSIDE.
                Check("hang: only an explicit window-bottom edge takes hold",
                    FormCompanion.GripFor(TNextAnimation.TOnly.WINDOW_BOTTOM) == FormCompanion.WindowGrip.Bottom &&
                    FormCompanion.GripFor(TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_BOTTOM) == FormCompanion.WindowGrip.None);
                // The pet's visible TOP goes against the window's bottom edge, so its own top padding comes
                // off. Getting this backwards buries the character inside the window by twice the padding.
                Check("hang: the character's top edge lands on the window's bottom edge",
                    FormCompanion.GripPositionY(500, 40) == 460.0 &&
                    FormCompanion.GripPositionY(500, 40) + 40 == 500.0);
                Check("hang: a zero-inset pet hangs flush",
                    FormCompanion.GripPositionY(500, 0) == 500.0);

                // ---- VELOCITY SCALING ----
                // A moving animation must still move at every scale. ScaleD rounds to an int, so a walk of 2
                // at 25% is 0.5 and Math.Round's BANKER'S rounding makes it exactly 0 -- the pet plays its
                // walk cycle on the spot for ever. Reported on a 25% Luffy; it hits any pet whose walk is
                // 1 or 2 px/step, which is most of them.
                Check("scale: a walk of 2 at 25% does not freeze (the reported bug)",
                    ScalePolicy.ScaleVelocity(2, 0.25) != 0);
                Check("scale: ...and neither does 1, or the negative of either",
                    ScalePolicy.ScaleVelocity(1, 0.25) != 0 &&
                    ScalePolicy.ScaleVelocity(-1, 0.25) != 0 &&
                    ScalePolicy.ScaleVelocity(-2, 0.25) != 0);
                Check("scale: direction is preserved when clamped to one pixel",
                    ScalePolicy.ScaleVelocity(-2, 0.25) < 0 && ScalePolicy.ScaleVelocity(2, 0.25) > 0);
                // ...but a STILL pose must not be given motion it never had.
                Check("scale: zero stays zero at every scale",
                    ScalePolicy.ScaleVelocity(0, 0.25) == 0 &&
                    ScalePolicy.ScaleVelocity(0, 4.0) == 0 &&
                    ScalePolicy.ScaleVelocity(0, 1.0) == 0);
                // A velocity large enough to survive rounding is left exactly as ScaleD computed it.
                Check("scale: a velocity that survives rounding is untouched",
                    ScalePolicy.ScaleVelocity(8, 0.25) == ScalePolicy.ScaleD(8, 0.25) &&
                    ScalePolicy.ScaleVelocity(10, 2.0) == ScalePolicy.ScaleD(10, 2.0));
                // The banker's-rounding case specifically: .5 exactly, which rounds to EVEN (0), not away.
                Check("scale: the exact .5 case is what bit us, and is covered",
                    ScalePolicy.ScaleD(2, 0.25) == 0 && ScalePolicy.ScaleVelocity(2, 0.25) == 1);

                // ---- PER-PET MONITOR PIN ----
                // Pinning is stored per pet TYPE and validated against the CURRENT screen list on every read.
                // A pin to an unplugged display must read as UNPINNED, or the pet would be hidden for ever on
                // a monitor that no longer exists -- a setting that makes a pet disappear permanently is worse
                // than one that is ignored.
                {
                    var store = new AppSettingsStore(
                        Path.Combine(Path.GetTempPath(), "dp-pinprobe-selftest-" + Guid.NewGuid().ToString("N") + ".json"),
                        new string[0]);
                    var d = new LocalData(store);

                    Check("pin: a pet is unpinned by default", d.GetPetMonitor("hornet", 2) == -1);
                    d.SetPetMonitor("hornet", 1);
                    Check("pin: a pinned pet reports its screen", d.GetPetMonitor("hornet", 2) == 1);
                    Check("pin: the pin is per TYPE, not global", d.GetPetMonitor("pearl", 2) == -1);
                    // The unplugged-monitor case, which is the whole reason this is validated at READ time.
                    Check("pin: a pin to a screen that is gone reads as unpinned",
                        d.GetPetMonitor("hornet", 1) == -1);
                    Check("pin: ...and returns when the screen comes back",
                        d.GetPetMonitor("hornet", 2) == 1);
                    d.SetPetMonitor("hornet", -1);
                    Check("pin: a negative display unpins", d.GetPetMonitor("hornet", 2) == -1);
                    Check("pin: an empty id is refused rather than stored", !d.SetPetMonitor("", 0));
                    // Re-pinning must REPLACE, not accumulate a second row for the same pet.
                    d.SetPetMonitor("hornet", 0);
                    d.SetPetMonitor("hornet", 1);
                    Check("pin: re-pinning replaces rather than duplicating", d.GetPetMonitor("hornet", 2) == 1);
                }

                // ---- PET DISPLAY NAMES ----
                // A pet id must never reach the user. The tray holds only ids, so it resolves them through
                // DisplayNameForId; the trap is that the generic fallback TITLE-CASES a folder id, which
                // turns the built-in into "ESheep" and a converted skin into "Shimeji 3x56f4pl".
                Check("name: the built-in keeps its own casing rather than being title-cased",
                    CompanionCatalog.DisplayNameForId(CompanionCatalog.BuiltInPetId) == CompanionCatalog.BuiltInPetId);
                // A FIXED absent id, deliberately: the built-in returns before the cache is consulted, so
                // asserting against it proves nothing about caching, and a random id never hits the cache
                // twice. This one is not installed, so it falls to the prettifier, whose answer differs from
                // the id -- which is what makes a cache that stored the id instead of the name detectable.
                const string absent = "dp_selftest_absent_pet";
                string firstLook = CompanionCatalog.DisplayNameForId(absent);
                string secondLook = CompanionCatalog.DisplayNameForId(absent);
                Check("name: an id that is not installed still answers rather than throwing",
                    !string.IsNullOrEmpty(firstLook));
                Check("name: the cache returns the NAME on a repeat lookup, not the id",
                    secondLook == firstLook && secondLook == CompanionCatalog.PrettyName(absent) &&
                    secondLook != absent);
                // PrettyName is the fallback the tray must NOT be using for a pet with a header.
                Check("name: the prettifier is what would have produced the bad label",
                    CompanionCatalog.PrettyName("eSheep") == "ESheep");
                // The two DisplayName branches, so the distinction the whole fix rests on is shown to be
                // real: supplying the header name keeps it, and supplying null LOSES it to the folder id.
                // This is the runtime half; the source invariant in tests\runtime-hardening-selftest.ps1
                // asserts DisplayNameForId actually passes ReadHeaderNameForId rather than null, because a
                // build output carries no bundled pets and any assertion over INSTALLED pets would pass
                // vacuously on a clean runner -- which is how a resolver nobody wires up survives.
                Check("name: a supplied header name wins over the folder id",
                    CompanionCatalog.DisplayName("shimeji-abc123", "Monkey D. Luffy") == "Monkey D. Luffy");
                Check("name: ...and supplying null is exactly what loses it",
                    CompanionCatalog.DisplayName("shimeji-abc123", null) == "Shimeji Abc123");

                // ---- FACTORY RESET ----
                // The installer's "clear all settings and modules" empties directories. The only failure
                // that matters is emptying the WRONG one, so the refusal rules are asserted directly and
                // each dangerous shape gets its own case rather than one blanket "handles bad input".
                string why;
                Check("reset: refuses an empty path", !FactoryReset.IsSafeToWipe("", out why));
                Check("reset: refuses null", !FactoryReset.IsSafeToWipe(null, out why));
                Check("reset: refuses whitespace", !FactoryReset.IsSafeToWipe("   ", out why));
                Check("reset: refuses a drive root", !FactoryReset.IsSafeToWipe(@"C:\", out why));
                Check("reset: refuses a drive root without the slash", !FactoryReset.IsSafeToWipe(@"C:", out why));
                Check("reset: refuses one level below the root", !FactoryReset.IsSafeToWipe(@"C:\Users", out why));
                Check("reset: refuses a relative path", !FactoryReset.IsSafeToWipe("data", out why));
                foreach (Environment.SpecialFolder guarded in new[]
                {
                    Environment.SpecialFolder.UserProfile,
                    Environment.SpecialFolder.MyDocuments,
                    Environment.SpecialFolder.Desktop,
                    Environment.SpecialFolder.LocalApplicationData,
                    Environment.SpecialFolder.ApplicationData,
                })
                {
                    string p = Environment.GetFolderPath(guarded);
                    if (string.IsNullOrEmpty(p)) continue;
                    Check("reset: refuses " + guarded, !FactoryReset.IsSafeToWipe(p, out why));
                    // ...and with a trailing separator, which is a different string and the same folder.
                    Check("reset: refuses " + guarded + " with a trailing slash",
                        !FactoryReset.IsSafeToWipe(p + Path.DirectorySeparatorChar, out why));
                }
                // The two it MUST allow, or the feature silently does nothing.
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                    Check("reset: allows the app's own data root under LocalAppData",
                        FactoryReset.IsSafeToWipe(Path.Combine(localAppData, "DesktopAICompanion"), out why));
                Check("reset: allows a modules folder beside the exe",
                    FactoryReset.IsSafeToWipe(Path.Combine(AppContext.BaseDirectory, "modules"), out why));
                Check("reset: a refusal always says why",
                    !FactoryReset.IsSafeToWipe(@"C:\", out why) && !string.IsNullOrEmpty(why));

                // ---- APP UPDATE CHECK ----
                // Version comparison read as text gets 1.9.10 vs 1.9.9 backwards, which is exactly the pair
                // this project is about to hit. All pure, so it is checked here rather than against a network.
                Check("update: a higher patch is newer",
                    AppUpdateCheck.IsNewer("1.9.8", "1.9.7"));
                Check("update: 1.9.10 beats 1.9.9 (string compare gets this wrong)",
                    AppUpdateCheck.IsNewer("1.9.10", "1.9.9"));
                Check("update: the same version is not newer",
                    !AppUpdateCheck.IsNewer("1.9.7", "1.9.7"));
                Check("update: an older version is not newer",
                    !AppUpdateCheck.IsNewer("1.9.6", "1.9.7"));
                Check("update: missing components count as zero, so 1.9 == 1.9.0",
                    !AppUpdateCheck.IsNewer("1.9", "1.9.0") && !AppUpdateCheck.IsNewer("1.9.0", "1.9"));
                Check("update: a 4th component still compares",
                    AppUpdateCheck.IsNewer("1.9.7.1", "1.9.7"));
                // Garbage must never nag: a mangled catalog is a silent no-answer, not a phantom update.
                Check("update: unparseable text never claims an update",
                    !AppUpdateCheck.IsNewer("banana", "1.9.7") &&
                    !AppUpdateCheck.IsNewer("1.9.8-beta", "1.9.7") &&
                    !AppUpdateCheck.IsNewer("", "1.9.7") &&
                    !AppUpdateCheck.IsNewer(null, "1.9.7"));
                Check("update: a leading v is tolerated (tags carry one)",
                    AppUpdateCheck.IsNewer("v1.9.8", "1.9.7"));

                // The throttle. "At most once a day" is the whole consent story for an unprompted request.
                DateTimeOffset now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
                Check("update: disabled never checks",
                    !AppUpdateCheck.ShouldCheck(false, DateTimeOffset.MinValue, now));
                Check("update: never checked before does check",
                    AppUpdateCheck.ShouldCheck(true, DateTimeOffset.MinValue, now));
                // WEEKLY, matching the module and companion checks rather than being twelve times chattier
                // than the pane next to it claims. This was an hour, for a real reason kept in the comment
                // on CheckInterval: the stamp is written even on a NEGATIVE answer, so a long interval
                // blinds a fresh install. The escape hatch is what makes a week acceptable -- opening
                // Preferences re-checks on a one-minute floor, asserted below -- not the interval itself.
                Check("update: checked 8 days ago re-checks",
                    AppUpdateCheck.ShouldCheck(true, now.AddDays(-8), now));
                Check("update: checked 6 days ago does not re-check",
                    !AppUpdateCheck.ShouldCheck(true, now.AddDays(-6), now));
                Check("update: the launch interval is weekly, matching content",
                    AppUpdateCheck.CheckInterval == AppUpdateCheck.ContentInterval &&
                    AppUpdateCheck.CheckInterval == TimeSpan.FromDays(7));
                // Opening Preferences is an explicit "tell me" and may refresh sooner, because the footer is
                // the only surface the answer ever appears on. A floor stops it spinning on open/close.
                Check("update: opening Preferences may refresh inside the launch interval",
                    AppUpdateCheck.ShouldCheck(true, now.AddMinutes(-5), now, AppUpdateCheck.InteractiveInterval));
                Check("update: an interactive refresh still has a floor",
                    !AppUpdateCheck.ShouldCheck(true, now.AddSeconds(-10), now, AppUpdateCheck.InteractiveInterval));
                Check("update: the interactive floor is shorter than the launch interval",
                    AppUpdateCheck.InteractiveInterval < AppUpdateCheck.CheckInterval);
                // A clock that jumped backwards must not lock the check out until the future arrives.
                Check("update: a future stamp is treated as never checked",
                    AppUpdateCheck.ShouldCheck(true, now.AddDays(30), now));

                // What the footer renders. The arrow form is the clickable state; anything else is the plain
                // muted stamp, so "no update" and "garbled answer" look identical to the user.
                Check("update: the footer offers the jump when newer",
                    AppUpdateCheck.FooterText("1.9.7", "1.9.8") == "1.9.7 → 1.9.8" &&
                    AppUpdateCheck.OffersUpdate("1.9.7", "1.9.8"));
                Check("update: the footer is the plain version when current",
                    AppUpdateCheck.FooterText("1.9.7", "") == "v1.9.7" &&
                    !AppUpdateCheck.OffersUpdate("1.9.7", "") &&
                    AppUpdateCheck.FooterText("1.9.7", "1.9.6") == "v1.9.7");

                // Reading the version out of the catalog. Absent block = an older catalog = nothing to report.
                Check("update: the catalog's app.version is read",
                    RemoteCatalogClient.ParseAppVersion("{\"app\":{\"version\":\"1.9.8\"}}") == "1.9.8");
                Check("update: a catalog with no app block reports nothing",
                    RemoteCatalogClient.ParseAppVersion("{\"pets\":[]}") == "" &&
                    RemoteCatalogClient.ParseAppVersion("{}") == "" &&
                    RemoteCatalogClient.ParseAppVersion("not json at all") == "" &&
                    RemoteCatalogClient.ParseAppVersion("") == "");
                Check("update: a non-string version is refused rather than coerced",
                    RemoteCatalogClient.ParseAppVersion("{\"app\":{\"version\":19.8}}") == "");

                // LETTING GO. The underside grip pins y to 0 and BOTH of its release conditions test y, so
                // anything that keeps the grip while wanting to move vertically is stuck for ever. That
                // shipped: a pet reaching `fall` through a ceiling pose's own <next> edge (weight 25 of 105 on
                // every pass) hung under the window playing the falling animation and never came down.
                Check("release: entering fall drops a grip on every side",
                    FormCompanion.GripMustRelease(FormCompanion.WindowGrip.Bottom, true, 0, 0) &&
                    FormCompanion.GripMustRelease(FormCompanion.WindowGrip.Left, true, 0, 0) &&
                    FormCompanion.GripMustRelease(FormCompanion.WindowGrip.Right, true, 0, 0));
                // The structural half: ANY vertically-moving animation, not just fall, because the pin is what
                // makes it a trap rather than the animation's name.
                Check("release: an underside grip cannot survive vertical motion",
                    FormCompanion.GripMustRelease(FormCompanion.WindowGrip.Bottom, false, 10, 10) &&
                    FormCompanion.GripMustRelease(FormCompanion.WindowGrip.Bottom, false, 0, 20) &&
                    FormCompanion.GripMustRelease(FormCompanion.WindowGrip.Bottom, false, -5, 0));
                // ...and a genuine hanging pose keeps hold, or the feature would be gone rather than fixed.
                Check("release: a hanging pose (vy = 0) keeps its underside grip",
                    !FormCompanion.GripMustRelease(FormCompanion.WindowGrip.Bottom, false, 0, 0));
                // Climbing DOWN a window's side is vertical by design, and that branch does not pin y, so it
                // self-heals through its own gripRect.Bottom test. Releasing here would delete Phase D.
                Check("release: a side grip survives vertical motion (climbing down is the point)",
                    !FormCompanion.GripMustRelease(FormCompanion.WindowGrip.Left, false, 10, 10) &&
                    !FormCompanion.GripMustRelease(FormCompanion.WindowGrip.Right, false, 0, 20));
                Check("release: with no grip there is nothing to release",
                    !FormCompanion.GripMustRelease(FormCompanion.WindowGrip.None, true, 10, 10) &&
                    !FormCompanion.GripMustRelease(FormCompanion.WindowGrip.None, false, 0, 0));

                // PET FRESHNESS. The Pets pane used to diff the catalog by ID alone, so a pet you already had
                // was filtered out however much its CONTENT had changed: a corrected pet reached new downloads
                // only, and the pane reported "you already have every available pet" for ever. There is no
                // version field on a pet, so the comparison is the catalog's own sha256 against the installed
                // file -- which is exact, because the installer writes the very bytes the hash was verified
                // against. The whole table is asserted here; every input is a value, so none of it needs a
                // disk or a network.
                const string A = "aaaa", B = "bbbb";
                Check("freshness: an uninstalled pet is a download, not an update",
                    CompanionProvenance.Classify("", A, "") == CompanionFreshness.NotInstalled);
                Check("freshness: matching the catalog is up to date",
                    CompanionProvenance.Classify(A, A, A) == CompanionFreshness.UpToDate &&
                    CompanionProvenance.Classify(A, A, "") == CompanionFreshness.UpToDate);
                // The case the whole feature exists for: content changed under an id that is already installed.
                Check("freshness: differing from the catalog while matching the stamp is a clean update",
                    CompanionProvenance.Classify(A, B, A) == CompanionFreshness.UpdateAvailable);
                Check("freshness: differing from BOTH means the user edited it",
                    CompanionProvenance.Classify(A, B, "cccc") == CompanionFreshness.LocallyModified);
                // Absent provenance must NOT be assumed safe. A pet placed by hand or authored in Companion Studio
                // has no stamp, and quietly overwriting it would be the one unrecoverable thing here.
                Check("freshness: no stamp is unknown provenance, not a clean update",
                    CompanionProvenance.Classify(A, B, "") == CompanionFreshness.UnknownProvenance);
                // A pet the catalog does not list is not out of date, it is simply not ours; offering to
                // "update" it would offer to replace it with nothing.
                Check("freshness: a pet absent from the catalog is left alone",
                    CompanionProvenance.Classify(A, "", "") == CompanionFreshness.UpToDate);
                Check("freshness: hashes compare case- and whitespace-insensitively",
                    CompanionProvenance.Classify(" AAAA ", "aaaa", "") == CompanionFreshness.UpToDate);

                Check("freshness: exactly the three differing states are offered as updates",
                    CompanionProvenance.IsStale(CompanionFreshness.UpdateAvailable) &&
                    CompanionProvenance.IsStale(CompanionFreshness.LocallyModified) &&
                    CompanionProvenance.IsStale(CompanionFreshness.UnknownProvenance) &&
                    !CompanionProvenance.IsStale(CompanionFreshness.UpToDate) &&
                    !CompanionProvenance.IsStale(CompanionFreshness.NotInstalled));
                // The confirm prompt is driven by this, so a state that CAN lose work must be in it and a
                // state that cannot must not (or the update nags on every clean pet).
                Check("freshness: only the states that can lose work ask before overwriting",
                    CompanionProvenance.UpdateWouldDiscardChanges(CompanionFreshness.LocallyModified) &&
                    CompanionProvenance.UpdateWouldDiscardChanges(CompanionFreshness.UnknownProvenance) &&
                    !CompanionProvenance.UpdateWouldDiscardChanges(CompanionFreshness.UpdateAvailable) &&
                    !CompanionProvenance.UpdateWouldDiscardChanges(CompanionFreshness.UpToDate));
                // A warned state's wording has to actually warn, or the prompt is a shrug.
                Check("freshness: a warned state says the update replaces something",
                    CompanionProvenance.Describe(CompanionFreshness.LocallyModified).IndexOf("replaces", StringComparison.Ordinal) >= 0 &&
                    CompanionProvenance.Describe(CompanionFreshness.UnknownProvenance).IndexOf("replaces", StringComparison.Ordinal) >= 0 &&
                    CompanionProvenance.Describe(CompanionFreshness.UpdateAvailable).IndexOf("replaces", StringComparison.Ordinal) < 0);

                // Hashing agrees with itself across the two entry points, since the install path stamps from
                // the downloaded BYTES and the check path reads the FILE back.
                string probeDir = Path.Combine(Path.GetTempPath(), "dp-freshness-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(probeDir);
                    byte[] payload = Encoding.UTF8.GetBytes("<animations/>\n");
                    string probeFile = Path.Combine(probeDir, "animations.xml");
                    File.WriteAllBytes(probeFile, payload);
                    Check("freshness: hashing bytes and hashing the same file on disk agree",
                        CompanionProvenance.HashBytes(payload) == CompanionProvenance.HashFile(probeFile) &&
                        CompanionProvenance.HashBytes(payload).Length == 64);
                    CompanionProvenance.WriteStamp(probeDir, CompanionProvenance.HashBytes(payload));
                    Check("freshness: a written stamp reads back and classifies as up to date",
                        CompanionProvenance.ReadStamp(probeDir) == CompanionProvenance.HashBytes(payload) &&
                        CompanionProvenance.Classify(CompanionProvenance.HashFile(probeFile),
                            CompanionProvenance.HashBytes(payload), CompanionProvenance.ReadStamp(probeDir)) == CompanionFreshness.UpToDate);
                    // The stamp must not be mistaken for pet content by anything that scans the folder.
                    Check("freshness: the stamp is not named animations.xml",
                        CompanionProvenance.StampFileName != "animations.xml");
                    Check("freshness: a missing file and a missing stamp hash to empty, never throw",
                        CompanionProvenance.HashFile(Path.Combine(probeDir, "nope.xml")) == "" &&
                        CompanionProvenance.ReadStamp(Path.Combine(probeDir, "nodir")) == "");
                }
                finally
                {
                    try { if (Directory.Exists(probeDir)) Directory.Delete(probeDir, true); } catch { }
                }

                // WINDOW_BOTTOM is 0x80 and so falls OUTSIDE the 0x7F that NONE happens to equal. That makes
                // the NONE short-circuit in Eligible load-bearing rather than defensive: without it an
                // unconditional edge would be the one kind that stopped firing under a window.
                TNextAnimation.TOnly under = TNextAnimation.TOnly.WINDOW | TNextAnimation.TOnly.WINDOW_BOTTOM;
                Check("hang: an unconditional edge is still taken under a window",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.NONE, under));
                Check("hang: the generic window edge still fires under a window",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW, under));
                Check("hang: a bottom-edge animation fires under a window only",
                    TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_BOTTOM, under) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_BOTTOM, onTop) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_BOTTOM, onLeft) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_BOTTOM, TNextAnimation.TOnly.HORIZONTAL));
                Check("hang: the other window edges do not fire under a window",
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_TOP, under) &&
                    !TNextAnimation.Eligible(TNextAnimation.TOnly.WINDOW_LEFT, under));
                Check("hang: the only= vocabulary and the validator agree on window-bottom",
                    Xml.ParseOnlyFlag("window-bottom") == TNextAnimation.TOnly.WINDOW_BOTTOM &&
                    CompanionXmlValidator.IsAllowedOnly("window-bottom"));

                // The rising-boundary test. Asymmetric with the descending one on purpose: landing rests ON
                // a surface, rising must pass THROUGH it.
                Check("hang: a rising step that passes the window's bottom edge is a hit",
                    DesktopGeometry.CrossesAscendingBoundary(520.0, -30.0, 500));
                Check("hang: a rising step that stops short is not",
                    !DesktopGeometry.CrossesAscendingBoundary(520.0, -10.0, 500));
                Check("hang: a pet already above the edge is not re-caught by it",
                    !DesktopGeometry.CrossesAscendingBoundary(480.0, -30.0, 500));
                Check("hang: a DOWNWARD step never catches an underside",
                    !DesktopGeometry.CrossesAscendingBoundary(480.0, 30.0, 500) &&
                    !DesktopGeometry.CrossesAscendingBoundary(520.0, 30.0, 500));
                Check("hang: a stationary pet never catches an underside",
                    !DesktopGeometry.CrossesAscendingBoundary(520.0, 0.0, 500));
                Check("hang: nonsense movement is rejected rather than propagated",
                    !DesktopGeometry.CrossesAscendingBoundary(double.NaN, -30.0, 500) &&
                    !DesktopGeometry.CrossesAscendingBoundary(520.0, double.NegativeInfinity, 500));

                if (ok) sb.AppendLine("PASS: focused runtime hardening regression harness.");
            }
            catch (Exception ex)
            {
                ok = false;
                Exception inner = ex; while (inner.InnerException != null) inner = inner.InnerException;
                sb.AppendLine("EXC: " + inner.GetType().Name + ": " + inner.Message);
            }

            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dp-hardening-selftest.txt"), sb.ToString()); } catch { }
            return ok;
        }

        private static object MemberValue(Type t, object instance, string name)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(instance);
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) return p.GetValue(instance, new object[0]);
            throw new MissingMemberException(t.FullName + "." + name);
        }
    }
}
