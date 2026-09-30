using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
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

        /// <summary>How many captures reached the recognizer through the raw pixel copy, and how many through the
        /// PNG round trip it replaced. For the self-test: the codec route must be the fallback, not the path.</summary>
        internal static int RawCopiesForDiagnostics;
        internal static int CodecFallbacksForDiagnostics;

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
        /// OCR a bitmap, or "" when unavailable/unreadable. The pixels go to the recognizer as a
        /// <see cref="SoftwareBitmap"/> copied straight from the capture's memory; see
        /// <see cref="ToSoftwareBitmapAsync"/> for why that replaced an in-memory PNG round trip.
        /// </summary>
        public static async Task<string> RecognizeAsync(Bitmap bitmap, CancellationToken ct)
        {
            if (bitmap == null) return "";
            try
            {
                OcrEngine engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (engine == null) return "";
                ct.ThrowIfCancellationRequested();

                using (SoftwareBitmap software = await ToSoftwareBitmapAsync(bitmap, ct).ConfigureAwait(false))
                {
                    OcrResult result = await engine.RecognizeAsync(software).AsTask(ct).ConfigureAwait(false);
                    return result != null ? (result.Text ?? "") : "";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
        }

        /// <summary>
        /// The capture as a <see cref="SoftwareBitmap"/> the recognizer accepts, copied straight from the pixels.
        /// The first version PNG-encoded the bitmap into memory and decoded it back through the WinRT imaging
        /// stack: a codec pass in each direction, per ask, for nothing the recognizer needs (F109). The property
        /// here is that no codec runs. LockBits with Format32bppArgb converts the capture (24bpp) on the way out
        /// and lays the pixels down as B,G,R,A; a 32bpp stride is width*4 exactly, which is the tightly packed
        /// Bgra8 buffer CreateCopyFromBuffer expects, and the row loop below covers a padded stride anyway. The
        /// PNG route stays as the fallback should the copy throw, so a surprise here degrades to the old cost
        /// rather than to no OCR; the self-test asserts that the fallback did not run.
        /// </summary>
        private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap, CancellationToken ct)
        {
            try
            {
                int width = bitmap.Width;
                int height = bitmap.Height;
                int rowBytes = width * 4;
                byte[] pixels = new byte[rowBytes * height];
                BitmapData data = bitmap.LockBits(
                    new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    if (data.Stride == rowBytes)
                        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    else
                        for (int y = 0; y < height; y++)
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                ct.ThrowIfCancellationRequested();
                SoftwareBitmap copied = SoftwareBitmap.CreateCopyFromBuffer(
                    pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
                Interlocked.Increment(ref RawCopiesForDiagnostics);
                return copied;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                Interlocked.Increment(ref CodecFallbacksForDiagnostics);
                // The codec route, kept as the fallback: still no temp file, unlike the Tesseract path, which must
                // hand a real file to a child process.
                using (var memory = new MemoryStream())
                {
                    bitmap.Save(memory, ImageFormat.Png);
                    memory.Position = 0;
                    ct.ThrowIfCancellationRequested();
                    using (IRandomAccessStream stream = memory.AsRandomAccessStream())
                    {
                        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
                        return await decoder.GetSoftwareBitmapAsync().AsTask(ct).ConfigureAwait(false);
                    }
                }
            }
        }
    }
}
