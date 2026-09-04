using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.Modules;   // ABI ScreenContext / PixelRect (replaces the base ScreenCaptureContext)
using System.Text.Json.Nodes;
using DesktopAICompanion.ModuleKit;   // AtomicFile / CrossSessionLock / UnicodeTextProgress

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Orchestrates one "look at the screen and react" turn:
    /// capture -> (OCR text | downscaled image) -> Ollama -> parse {text, emotion}.
    /// Purely additive: it observes the screen and calls the backend; it never touches the
    /// pet physics engine. Any failure results in a null response (the pet stays silent).
    /// </summary>
    internal sealed class AiBrain : IDisposable
    {
        private readonly ICompanionBrainBackend _backend;
        private readonly AiSettings _settings;
        private readonly string _textModel;
        private readonly string _visionModel;
        private readonly bool _useVision;
        private readonly string _tesseractPath;

        private byte[] _lastFrameSignature;   // change-detection gate (used by the idle loop, phase 3)
        private int _disposeStarted;

        // Vision images are downscaled to this width before sending — full-screen frames make a
        // vision model crawl (tens of seconds). OCR keeps the larger capture for legibility.
        private const int VisionMaxWidth = 896;
        private const int MaximumCaptureWidth = 2048;
        private const int MaximumCaptureHeight = 2048;
        private const int MaximumCapturePixels = 4 * 1024 * 1024;
        private const int MaximumResponseCharacters = 512;
        private const int MaximumEmotionCharacters = 32;

        // U+00AE REGISTERED SIGN, and U+00C2 (capital A with circumflex) -- what that sign's leading UTF-8
        // byte looks like once it has been decoded as ANSI. Written as code points rather than pasted glyphs
        // on purpose: the marker for an encoding bug must not itself depend on how this file gets decoded.
        private const char RegisteredSign = (char)0x00AE;
        private const char AnsiMisdecodeMarker = (char)0x00C2;
        private const int Srccopy = 0x00CC0020;
        private const int Captureblt = 0x40000000;
        private const int Halftone = 4;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool StretchBlt(
            IntPtr destination,
            int destinationX,
            int destinationY,
            int destinationWidth,
            int destinationHeight,
            IntPtr source,
            int sourceX,
            int sourceY,
            int sourceWidth,
            int sourceHeight,
            int rasterOperation);

        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr deviceContext, int stretchMode);

        /// <summary>
        /// Build the system prompt fresh each call so it reflects the current persona (name, user,
        /// personality — backlog 5.5) and the time of day (5.2).
        /// </summary>
        private string BuildSystemPrompt()
        {
            string name        = string.IsNullOrWhiteSpace(_settings.PetName) ? "a tiny desktop pet" : _settings.PetName.Trim();
            string disposition = Dispositions.InstructionForId(_settings.Disposition);
            string userName = string.IsNullOrWhiteSpace(_settings.UserName) ? "" : _settings.UserName.Trim();
            // Allow the configured name but don't force it into every remark, and forbid reading a name
            // off the screen — window titles and paths ("Administrator", "C:\\Users\\Admin", ...) were
            // being picked up as the user's name.
            string user = userName.Length == 0
                ? " You do not know your human's name, so never invent one or read a name, username or handle off the screen."
                : (" Your human is called " + userName + ". Use their name only when it actually fits the " +
                   "remark, not in every single one; when you do use a name, it must be " + userName +
                   " — never invent one or use any other name, username or handle you see on the screen.");

            return
                "You are " + name + ", a tiny pet living on the user's screen. " +
                "Disposition (apply to the remark text only, keep the JSON exactly as specified): " + disposition +
                " Commit to it fully and stay in character in every word." + user +
                " It is currently " + TimeOfDay() + ". " +
                "Look at what is on the screen (described below) and make one short, in-character remark " +
                "about something specific you actually see there — name a program, file, word or detail from it. " +
                "Be vivid and true to your disposition; never generic, off-topic or merely polite. " +
                "Do not repeat anything you have said recently — make every remark new and different. " +
                "Keep it to one or two sentences, about 20 words each (40 words at most) — for a roast or " +
                "insult-comic disposition, a short setup followed by the knockdown lands well; otherwise one " +
                "sentence is often enough. Do not use quotation marks in the remark. " +
                "Never say that you are an AI or a language model. " +
                "Reply ONLY with compact JSON of the form " +
                "{\"text\":\"<your remark>\",\"emotion\":\"<one of: happy, sad, thinking, excited, confused, neutral>\"}.";
        }

        /// <summary>Coarse time-of-day label for the persona (backlog 5.2).</summary>
        private static string TimeOfDay()
        {
            int h = DateTime.Now.Hour;
            if (h < 5)  return "late at night";
            if (h < 12) return "the morning";
            if (h < 17) return "the afternoon";
            if (h < 21) return "the evening";
            return "night";
        }

        public AiBrain(ICompanionBrainBackend backend, AiSettings settings)
        {
            _backend = backend;
            _settings = settings ?? new AiSettings();
            string normalizedModel;
            _textModel = AiModelPolicy.TryNormalize(
                _settings.TextModel, out normalizedModel)
                ? normalizedModel
                : "llama3.1:8b";
            _visionModel = AiModelPolicy.TryNormalize(
                _settings.VisionModel, out normalizedModel)
                ? normalizedModel
                : "gemma3:4b";
            _useVision = _settings.UseVision;
            _tesseractPath = _settings.TesseractPath;
        }

        /// <summary>
        /// Launch-time preparation (fire-and-forget): optionally start the backend server, then
        /// preload the active model so the first ask doesn't pay the cold-start cost. Never throws.
        /// Returns true when the backend is reachable (used to drive the "AI ready" hint).
        /// </summary>
        public async Task<bool> PrepareAsync(CancellationToken ct = default(CancellationToken))
        {
            try
            {
                bool up = _settings.AutoStartServer
                    ? await _backend.EnsureServerAsync(ct).ConfigureAwait(false)
                    : await _backend.IsAvailableAsync(ct).ConfigureAwait(false);

                if (up && _settings.WarmUpDesired)
                    await _backend.WarmUpAsync(_useVision ? _visionModel : _textModel, ct).ConfigureAwait(false);

                return up;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        /// <summary>
        /// Ask the backend to unload this pet's text and vision models. Ollama evicts its
        /// keep-alive models; generic OpenAI-compatible providers intentionally do nothing.
        /// Best-effort.
        /// </summary>
        public async Task UnloadAsync(CancellationToken ct = default(CancellationToken))
        {
            try
            {
                await _backend.UnloadAsync(_textModel, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            if (!string.Equals(_visionModel, _textModel, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await _backend.UnloadAsync(_visionModel, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }

        /// <summary>
        /// React to whatever is on screen. Returns null when the backend is unavailable or errors,
        /// so the caller can simply stay silent without special-casing exceptions.
        /// </summary>
        public async Task<BrainResponse> AskAboutScreenAsync(
            ScreenContext captureContext,
            string petZone = null,
            bool allowVision = true,
            CancellationToken ct = default(CancellationToken))
        {
            try
            {
                if (!await _backend.IsAvailableAsync(ct).ConfigureAwait(false))
                    return null;

                Rectangle captureBounds;
                if (captureContext != null)
                {
                    PixelRect mb = captureContext.MonitorBounds;
                    captureBounds = new Rectangle(mb.X, mb.Y, mb.Width, mb.Height);
                }
                else
                {
                    System.Windows.Forms.Screen primary =
                        System.Windows.Forms.Screen.PrimaryScreen;
                    if (primary == null)
                        throw new InvalidOperationException(
                            "No display is available for screen capture.");
                    captureBounds = primary.Bounds;
                }
                using (Bitmap shot = CaptureScreen(captureBounds, 1280))
                {
                    List<ChatMessage> messages = new List<ChatMessage> { ChatMessage.System(BuildSystemPrompt()) };

                    string model;

                    // Context: the front window (5.1) and the window the pet is standing on (5.6).
                    string win = captureContext != null
                        ? captureContext.WindowTitle
                        : "";
                    string ctx = "";
                    if (!string.IsNullOrWhiteSpace(win))     ctx += "The active window is: " + win + "\n";
                    if (!string.IsNullOrWhiteSpace(petZone)) ctx += "You are standing on the window: " + petZone.Trim() + "\n";
                    if (ctx.Length > 0) ctx += "\n";

                    // Routing (backlog 6.2): vision only for explicit asks; idle stays on the fast
                    // text path since a vision glance can take tens of seconds.
                    if (_useVision && allowVision)
                    {
                        string b64 = ToBase64PngScaled(shot, VisionMaxWidth);
                        messages.Add(ChatMessage.User(ctx + "Look at my screen and react.", new[] { b64 }));
                        model = _visionModel;
                    }
                    else
                    {
                        string ocr = await RunOcrAsync(shot, ct).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(ocr)) ocr = "(the screen has no readable text)";
                        ocr = UnicodeTextProgress.TruncateAtCodePointBoundary(
                            ocr,
                            1500);
                        messages.Add(ChatMessage.User(ctx + "Here is the text currently visible on my screen:\n\n" + ocr, null));
                        model = _textModel;
                    }

                    string raw = await ChatWithRetryAsync(model, messages, ct).ConfigureAwait(false);
                    BrainResponse resp = Parse(raw);
                    return resp;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;   // never crash the app over the AI layer
            }
        }

        private async Task<string> ChatWithRetryAsync(string model, IList<ChatMessage> messages, CancellationToken ct)
        {
            return await ChatWithRetryForDiagnosticsAsync(
                _backend,
                model,
                messages,
                ct).ConfigureAwait(false);
        }

        internal static async Task<string> ChatWithRetryForDiagnosticsAsync(
            ICompanionBrainBackend backend,
            string model,
            IList<ChatMessage> messages,
            CancellationToken ct)
        {
            if (backend == null) throw new ArgumentNullException("backend");
            try
            {
                return await backend.ChatAsync(model, messages, true, ct).ConfigureAwait(false);
            }
            // Retry once on a transient transport/timeout/HTTP failure; a deterministic failure (non-transient
            // 4xx/redirect) is not caught here and propagates. Predicate shared with FallbackBackend.
            catch (Exception ex) when (AiEndpointPolicy.IsRetryable(ex, ct)) { }

            ct.ThrowIfCancellationRequested();
            return await backend.ChatAsync(model, messages, true, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Change-detection gate: true when the screen differs from the last checked frame by more than
        /// <paramref name="thresholdPercent"/> of average luma. First call always returns true. Cheap:
        /// compares a 16x16 grayscale signature.
        /// <para>
        /// Currently has no caller. It backed the module's own idle timer, which was removed in aibrain
        /// 1.2.3 when unprompted commentary moved onto the host's global drop schedule. Kept deliberately
        /// rather than deleted: it is exactly the primitive a "only speak when something changed" option
        /// would need, and it is self-contained.
        /// </para>
        /// </summary>
        public bool ScreenChanged(
            Rectangle captureBounds,
            int thresholdPercent = 4)
        {
            byte[] sig = ComputeSignature(captureBounds);
            if (_lastFrameSignature == null || _lastFrameSignature.Length != sig.Length)
            {
                _lastFrameSignature = sig;
                return true;
            }
            long delta = 0;
            for (int i = 0; i < sig.Length; i++) delta += Math.Abs(sig[i] - _lastFrameSignature[i]);
            _lastFrameSignature = sig;
            double avgDelta = delta / (double)sig.Length;             // 0..255
            return (avgDelta / 255.0 * 100.0) >= thresholdPercent;
        }

        // ---- screen capture ------------------------------------------------

        private static Bitmap CaptureScreen(Rectangle b, int maxWidth)
        {
            if (b.Width <= 0 || b.Height <= 0 ||
                b.Width > 32768 || b.Height > 32768)
                throw new InvalidOperationException("The selected display dimensions are invalid.");

            int targetWidth = Math.Min(
                b.Width,
                Math.Max(1, Math.Min(MaximumCaptureWidth, maxWidth)));
            int targetHeight = Math.Max(
                1,
                (int)Math.Round(b.Height * (targetWidth / (double)b.Width)));
            if (targetHeight > MaximumCaptureHeight)
            {
                targetWidth = Math.Max(
                    1,
                    (int)Math.Round(targetWidth *
                        (MaximumCaptureHeight / (double)targetHeight)));
                targetHeight = MaximumCaptureHeight;
            }
            if ((long)targetWidth * targetHeight > MaximumCapturePixels)
                throw new InvalidOperationException("The screen capture exceeds its pixel budget.");

            Bitmap capture = null;
            IntPtr sourceDc = IntPtr.Zero;
            try
            {
                capture = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
                sourceDc = GetDC(IntPtr.Zero);
                if (sourceDc == IntPtr.Zero)
                    throw new InvalidOperationException("The screen device context is unavailable.");

                using (Graphics graphics = Graphics.FromImage(capture))
                {
                    IntPtr destinationDc = graphics.GetHdc();
                    try
                    {
                        SetStretchBltMode(destinationDc, Halftone);
                        if (!StretchBlt(
                                destinationDc,
                                0,
                                0,
                                targetWidth,
                                targetHeight,
                                sourceDc,
                                b.Left,
                                b.Top,
                                b.Width,
                                b.Height,
                                Srccopy | Captureblt))
                            throw new InvalidOperationException("Screen capture failed.");
                    }
                    finally
                    {
                        graphics.ReleaseHdc(destinationDc);
                    }
                }

                Bitmap result = capture;
                capture = null;
                return result;
            }
            finally
            {
                if (sourceDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, sourceDc);
                if (capture != null) capture.Dispose();
            }
        }

        private static string ToBase64Png(Bitmap bmp)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        /// <summary>
        /// Base64 PNG of the bitmap, first downscaled to <paramref name="maxWidth"/> so a vision
        /// model doesn't choke on a full-screen frame. Returns the unscaled PNG if already small.
        /// </summary>
        private static string ToBase64PngScaled(Bitmap bmp, int maxWidth)
        {
            if (bmp.Width <= maxWidth) return ToBase64Png(bmp);

            int h = (int)(bmp.Height * (maxWidth / (double)bmp.Width));
            using (Bitmap scaled = new Bitmap(maxWidth, h, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, 0, 0, maxWidth, h);
                }
                return ToBase64Png(scaled);
            }
        }

        private static byte[] ComputeSignature(Rectangle captureBounds)
        {
            const int N = 16;
            using (Bitmap shot = CaptureScreen(captureBounds, 256))
            using (Bitmap small = new Bitmap(N, N, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(shot, 0, 0, N, N);
                }
                byte[] sig = new byte[N * N];
                // Read the whole 16x16 in one LockBits pass instead of 256 GetPixel calls.
                // Format24bppRgb stores pixels as B,G,R (3 bytes each); rows are padded to Stride.
                BitmapData data = small.LockBits(
                    new Rectangle(0, 0, N, N),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format24bppRgb);
                try
                {
                    int stride = data.Stride;
                    byte[] pixels = new byte[stride * N];
                    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    int k = 0;
                    for (int y = 0; y < N; y++)
                    {
                        int row = y * stride;
                        for (int x = 0; x < N; x++)
                        {
                            int p = row + x * 3;
                            byte blue  = pixels[p];
                            byte green = pixels[p + 1];
                            byte red   = pixels[p + 2];
                            sig[k++] = (byte)((red * 30 + green * 59 + blue * 11) / 100);
                        }
                    }
                }
                finally
                {
                    small.UnlockBits(data);
                }
                return sig;
            }
        }

        // ---- OCR: tesseract when present, else Windows' built-in engine ----

        /// <summary>
        /// Which engine screen reading will actually use, as a display name — "tesseract.exe (path)",
        /// <see cref="WindowsOcr.DisplayName"/>, or null when neither is available. Surfaced by the
        /// "Test OCR" status so the user can tell WHICH engine produced their results, and therefore
        /// whether installing Tesseract would be an upgrade.
        /// </summary>
        internal string DescribeOcrEngine()
        {
            string exe = null;
            try { exe = ResolveTesseract(); } catch { }
            if (!string.IsNullOrEmpty(exe)) return Path.GetFileName(exe) + " (" + exe + ")";
            return WindowsOcr.IsAvailable ? WindowsOcr.DisplayName : null;
        }

        private async Task<string> RunOcrAsync(Bitmap bmp, CancellationToken ct)
        {
            string exe = ResolveTesseract();
            // No Tesseract anywhere -> fall back to the OS engine rather than going screen-blind.
            if (string.IsNullOrEmpty(exe))
                return await WindowsOcr.RecognizeAsync(bmp, ct).ConfigureAwait(false);
            SweepStaleOcrScratch();
            string tmpPng = Path.Combine(Path.GetTempPath(), "pet_ocr_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                bmp.Save(tmpPng, ImageFormat.Png);

                ProcessStartInfo psi = BuildOcrStartInfo(exe, tmpPng);

                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.EnableRaisingEvents = true;

                    var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    p.Exited += delegate
                    {
                        try { exited.TrySetResult(p.ExitCode); }
                        catch { exited.TrySetResult(-1); }
                    };

                    if (!p.Start()) return "";
                    using (ProcessJob job = ProcessJob.TryAttach(p))
                    {
                        if (p.HasExited) exited.TrySetResult(p.ExitCode);

                        Task<string> stdout = ReadBoundedAsync(p.StandardOutput, 32768);
                        Task<string> stderr = ReadBoundedAsync(p.StandardError, 8192);
                        Task timeout = Task.Delay(TimeSpan.FromSeconds(8), ct);
                        Task finished = await Task.WhenAny(exited.Task, timeout).ConfigureAwait(false);

                        if (finished != exited.Task)
                        {
                            if (job != null) job.Terminate();
                            KillProcessTree(p);
                            ObserveFailure(stdout);
                            ObserveFailure(stderr);
                            ct.ThrowIfCancellationRequested();
                            return "";
                        }

                        int exitCode = await exited.Task.ConfigureAwait(false);
                        Task drain = Task.WhenAll(stdout, stderr);
                        Task drained = await Task.WhenAny(
                            drain,
                            Task.Delay(TimeSpan.FromSeconds(2), ct)).ConfigureAwait(false);
                        if (drained != drain)
                        {
                            if (job != null) job.Terminate();
                            KillProcessTree(p);
                            ObserveFailure(drain);
                            ct.ThrowIfCancellationRequested();
                            return "";
                        }

                        await drain.ConfigureAwait(false);
                        if (exitCode != 0) return "";
                        return CleanOcr(stdout.Result);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return "";   // tesseract missing or failed -> no OCR text
            }
            finally
            {
                try { File.Delete(tmpPng); } catch { }
            }
        }

        /// <summary>
        /// Remove OCR scratch images an earlier run could not.
        ///
        /// <see cref="RunOcrAsync"/> deletes its own PNG in a finally, which covers the normal path and
        /// cancellation. It does NOT reliably cover the timeout path: there the tesseract process tree is
        /// killed and the delete runs immediately after, so a child that has not finished dying can still
        /// hold the handle, File.Delete throws, and the catch swallows it. These are full screenshots, so a
        /// run of timeouts would quietly leave megabytes in %TEMP%.
        ///
        /// Sweeping on the NEXT call fixes that without making the OCR path slower or racier: by then the
        /// process is long gone. One hour, so a concurrently running instance's in-flight file is never
        /// taken out from under it. Best-effort throughout; a file still locked is simply left for later.
        /// </summary>
        private static void SweepStaleOcrScratch()
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
                foreach (string path in Directory.GetFiles(Path.GetTempPath(), "pet_ocr_*.png"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) > cutoff) continue;
                        File.Delete(path);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// The tesseract invocation, as a factory so a self-test can assert the part that silently broke:
        /// the stdout/stderr ENCODING. Tesseract writes UTF-8, but a redirected stream with no explicit
        /// encoding is decoded using <c>GetConsoleOutputCP()</c>, which returns 0 in a GUI process with no
        /// console; .NET then decodes through codepage 0 == CP_ACP, i.e. the system ANSI codepage (1252 on a
        /// typical box). Every non-ASCII glyph on screen therefore reached the model as mojibake -- "as®"
        /// arrived as "asÂ®", "—" as "â€"", "’" as "â€™" -- and the model dutifully quoted the garbage back
        /// at the user. Pinning UTF-8 here is the whole fix.
        ///
        /// Deliberately LENIENT UTF-8 (replacement fallback), unlike the strict <c>UTF8Encoding(false, true)</c>
        /// this codebase uses for durable files: strict throws mid-read, and RunOcrAsync's catch turns any throw
        /// into "" -- one bad byte would blind the pet to the whole screen. A replacement char loses one glyph.
        /// </summary>
        internal static ProcessStartInfo BuildOcrStartInfo(string exe, string imagePath)
        {
            var utf8 = new UTF8Encoding(false);
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "\"" + imagePath + "\" stdout",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = utf8,
                StandardErrorEncoding = utf8,
                CreateNoWindow = true
            };

            // Help tesseract find its language data when running from a portable/toolbox layout.
            try
            {
                string exeDir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    string tessdata = Path.Combine(exeDir, "tessdata");
                    if (Directory.Exists(tessdata))
                        psi.EnvironmentVariables["TESSDATA_PREFIX"] = tessdata;
                }
            }
            catch { }

            return psi;
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters)
        {
            char[] buffer = new char[1024];
            StringBuilder retained = new StringBuilder(Math.Min(maxCharacters, 4096));
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                int remaining = maxCharacters - retained.Length;
                if (remaining > 0)
                    retained.Append(buffer, 0, Math.Min(remaining, read));
            }
            return retained.ToString();
        }

        private static void KillProcessTree(Process process)
        {
            if (process == null) return;
            try
            {
                int processId = process.Id;
                using (Process killer = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                    Arguments = "/PID " + processId + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (killer != null) killer.WaitForExit(2000);
                }
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
            }
        }

        private static void ObserveFailure(Task task)
        {
            if (task == null) return;
            task.ContinueWith(
                delegate(Task failed)
                {
                    if (failed.Exception != null)
                        failed.Exception.Handle(delegate(Exception ignored) { return true; });
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private string ResolveTesseract()
        {
            if (!string.IsNullOrWhiteSpace(_tesseractPath))
                return AiExecutablePolicy.ResolveConfigured(
                    _tesseractPath,
                    "tesseract.exe");

            string[] candidates =
            {
                Environment.ExpandEnvironmentVariables(
                    @"%ProgramFiles%\Tesseract-OCR\tesseract.exe"),
                Environment.ExpandEnvironmentVariables(
                    @"%LOCALAPPDATA%\Programs\Tesseract-OCR\tesseract.exe")
            };
            foreach (string candidate in candidates)
            {
                string resolved = AiExecutablePolicy.ResolveConfigured(
                    candidate,
                    "tesseract.exe");
                if (resolved != null) return resolved;
            }

            return AiExecutablePolicy.ResolveFromPath(
                Environment.GetEnvironmentVariable("PATH"),
                "tesseract.exe");
        }

        /// <summary>Self-test the OCR path (the "Test OCR" button): resolve the tesseract engine, then OCR a
        /// tiny generated image of known text. Returns a "✓ …"/"✗ …" status the pane colours green/red, so
        /// OCR never fails silently. Safe to call on a throwaway AiBrain (no backend needed).</summary>
        internal async Task<string> SelfTestOcrAsync(CancellationToken ct)
        {
            string exe;
            try { exe = ResolveTesseract(); }
            catch { exe = null; }
            bool usingTesseract = !string.IsNullOrWhiteSpace(exe);
            string engine = usingTesseract ? System.IO.Path.GetFileName(exe) : WindowsOcr.DisplayName;
            if (!usingTesseract && !WindowsOcr.IsAvailable)
                return "✗ No OCR engine found — install Tesseract, or add a Windows language pack.";
            try
            {
                // The probe text carries a REGISTERED SIGN on purpose: it is one UTF-8 byte pair (C2 AE), so
                // if the engine's output is ever decoded as ANSI again it comes back as "Â®" and the check
                // below catches it here instead of in a speech bubble. A missed ® is not a failure (OCR
                // accuracy varies); only a mis-DECODED one is.
                using (Bitmap probe = MakeOcrProbeImage("OCR works " + RegisteredSign))
                {
                    string text = await RunOcrAsync(probe, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(text) && text.IndexOf(AnsiMisdecodeMarker) >= 0)
                        return "✗ OCR text is mis-decoded (encoding bug) — using " + engine + ".";
                    string letters = "";
                    if (!string.IsNullOrEmpty(text))
                        foreach (char c in text) if (char.IsLetter(c)) letters += char.ToLowerInvariant(c);
                    if (letters.Length == 0)
                        return usingTesseract
                            ? "✗ Tesseract found but read no text (language data missing?)."
                            : "✗ Windows OCR read no text (no recognizer for your languages?).";
                    // Naming the engine matters: on the Windows fallback the user would otherwise never
                    // learn that installing Tesseract is an option, or which engine produced their results.
                    if (letters.Contains("ocr") || letters.Contains("works"))
                        return "✓ OCR working — using " + engine +
                            (usingTesseract ? "." : ". Install Tesseract for sharper reading.");
                    return "✓ OCR engine ran (" + engine + "); reading is approximate.";
                }
            }
            catch (Exception ex) { return "✗ OCR failed: " + ex.Message; }
        }

        // A small high-contrast image of known text for the OCR self-test.
        private static Bitmap MakeOcrProbeImage(string text)
        {
            var bmp = new Bitmap(320, 80);
            using (Graphics g = Graphics.FromImage(bmp))
            using (var font = new Font("Segoe UI", 28f, FontStyle.Bold))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(text, font, Brushes.Black, new PointF(10f, 15f));
            }
            return bmp;
        }

        private static string CleanOcr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
            return sb.ToString().Trim();
        }

        // ---- response parsing ----------------------------------------------

        private static BrainResponse Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                JsonNode o = JsonNode.Parse(raw);
                string text = SanitizeResponseText(JsonRead.Str(o["text"]));
                string emotion = NormalizeEmotion(JsonRead.Str(o["emotion"]));
                if (!string.IsNullOrWhiteSpace(text))
                    return new BrainResponse(text, emotion);
            }
            catch
            {
                // not JSON -> fall through to plain-text fallback
            }
            string fallback = SanitizeResponseText(raw);
            return string.IsNullOrWhiteSpace(fallback)
                ? null
                : new BrainResponse(fallback, "neutral");
        }

        internal static string SanitizeResponseText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var clean = new StringBuilder(Math.Min(value.Length, MaximumResponseCharacters));
            bool pendingSpace = false;
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    pendingSpace = clean.Length > 0;
                    continue;
                }

                int codeUnits = 1;
                if (char.IsHighSurrogate(c))
                {
                    if (index + 1 >= value.Length ||
                        !char.IsLowSurrogate(value[index + 1]))
                        continue;
                    codeUnits = 2;
                }
                else if (char.IsLowSurrogate(c))
                {
                    continue;
                }

                int spaceUnits = pendingSpace && clean.Length > 0 ? 1 : 0;
                if (clean.Length + spaceUnits + codeUnits > MaximumResponseCharacters)
                    break;
                if (spaceUnits != 0)
                    clean.Append(' ');
                pendingSpace = false;
                clean.Append(c);
                if (codeUnits == 2)
                    clean.Append(value[++index]);
            }
            return clean.ToString().Trim();
        }

        private static string NormalizeEmotion(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumEmotionCharacters)
                return "neutral";
            switch (value.Trim().ToLowerInvariant())
            {
                case "happy":
                case "sad":
                case "thinking":
                case "excited":
                case "confused":
                case "neutral":
                    return value.Trim().ToLowerInvariant();
                default:
                    return "neutral";
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            if (_backend != null) _backend.Dispose();
        }

        /// <summary>
        /// Best-effort job containment for OCR. Closing the job kills descendants, including the
        /// case where the OCR parent exits while a child keeps redirected pipes open forever.
        /// </summary>
        private sealed class ProcessJob : IDisposable
        {
            private const uint JobObjectLimitKillOnJobClose = 0x00002000;
            private const int JobObjectExtendedLimitInformation = 9;
            private IntPtr _handle;

            private ProcessJob(IntPtr handle)
            {
                _handle = handle;
            }

            public static ProcessJob TryAttach(Process process)
            {
                IntPtr job = IntPtr.Zero;
                IntPtr info = IntPtr.Zero;
                try
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    if (job == IntPtr.Zero) return null;

                    var limits = new JobObjectExtendedLimitInformationData();
                    limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                    int size = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformationData));
                    info = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(limits, info, false);
                    if (!SetInformationJobObject(
                            job,
                            JobObjectExtendedLimitInformation,
                            info,
                            (uint)size) ||
                        !AssignProcessToJobObject(job, process.Handle))
                    {
                        CloseHandle(job);
                        job = IntPtr.Zero;
                        return null;
                    }

                    ProcessJob result = new ProcessJob(job);
                    job = IntPtr.Zero;
                    return result;
                }
                catch
                {
                    if (job != IntPtr.Zero) CloseHandle(job);
                    return null;
                }
                finally
                {
                    if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
                }
            }

            public void Terminate()
            {
                if (_handle == IntPtr.Zero) return;
                try { TerminateJobObject(_handle, 1); } catch { }
            }

            public void Dispose()
            {
                IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateJobObject(
                IntPtr jobAttributes,
                string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(
                IntPtr job,
                int informationClass,
                IntPtr information,
                uint informationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(
                IntPtr job,
                IntPtr process);

            [DllImport("kernel32.dll")]
            private static extern bool TerminateJobObject(
                IntPtr job,
                uint exitCode);

            [DllImport("kernel32.dll")]
            private static extern bool CloseHandle(IntPtr handle);

            [StructLayout(LayoutKind.Sequential)]
            private struct IoCounters
            {
                public ulong ReadOperationCount;
                public ulong WriteOperationCount;
                public ulong OtherOperationCount;
                public ulong ReadTransferCount;
                public ulong WriteTransferCount;
                public ulong OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JobObjectBasicLimitInformation
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JobObjectExtendedLimitInformationData
            {
                public JobObjectBasicLimitInformation BasicLimitInformation;
                public IoCounters IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }
        }
    }
}
