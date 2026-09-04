using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Screen reading via Windows' own OCR engine (<c>Windows.Media.Ocr</c>), used as the fallback when no
    /// Tesseract executable resolves. It ships with the OS — nothing to download, install, redistribute or
    /// keep patched — which is what makes screen reading work on a fresh box. Tesseract still wins when
    /// present (generally better on dense or small text), so this is deliberately the second choice.
    ///
    /// Availability is not guaranteed: the engine needs a recognizer for one of the user's languages, and a
    /// Windows install with no matching language pack has none. Every member is defensive and returns
    /// "unavailable"/"" rather than throwing, so the caller degrades to no OCR exactly as it does when
    /// Tesseract is missing.
    /// </summary>
    internal static class WindowsOcr
    {
        /// <summary>Human-readable engine name for status text (so the user can tell which engine ran).</summary>
        public const string DisplayName = "Windows built-in OCR";

        /// <summary>True when the OS can give us a recognizer for the user's languages. Cheap; no image work.</summary>
        public static bool IsAvailable
        {
            get
            {
                try { return OcrEngine.TryCreateFromUserProfileLanguages() != null; }
                catch { return false; }
            }
        }

        /// <summary>
        /// OCR a bitmap, or "" when unavailable/unreadable. Encodes to PNG in memory and decodes through
        /// the WinRT imaging stack because <see cref="OcrEngine"/> takes a <see cref="SoftwareBitmap"/> —
        /// no temp file, unlike the Tesseract path which must hand a real file to a child process.
        /// </summary>
        public static async Task<string> RecognizeAsync(Bitmap bitmap, CancellationToken ct)
        {
            if (bitmap == null) return "";
            try
            {
                OcrEngine engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (engine == null) return "";

                using (var memory = new MemoryStream())
                {
                    bitmap.Save(memory, ImageFormat.Png);
                    memory.Position = 0;
                    ct.ThrowIfCancellationRequested();

                    using (IRandomAccessStream stream = memory.AsRandomAccessStream())
                    {
                        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
                        using (SoftwareBitmap software = await decoder.GetSoftwareBitmapAsync().AsTask(ct).ConfigureAwait(false))
                        {
                            OcrResult result = await engine.RecognizeAsync(software).AsTask(ct).ConfigureAwait(false);
                            return result != null ? (result.Text ?? "") : "";
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
        }
    }
}
