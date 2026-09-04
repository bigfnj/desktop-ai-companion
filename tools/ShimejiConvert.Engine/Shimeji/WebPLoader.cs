using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DesktopAICompanion.Tools.ShimejiConvert.Shimeji
{
    /// <summary>
    /// Decodes an Android-Shimeji bundle sprite (a WebP with alpha) into a <see cref="System.Drawing.Bitmap"/>
    /// the existing compositor understands.
    ///
    /// WebP is decoded through the bundled reference decoder (native/dwebp.exe, libwebp -- see the NOTICE beside
    /// it), NOT through WIC: the Windows Imaging Component's WebP codec decodes to opaque BGR32 on some machines
    /// and silently drops the alpha channel, which turned a converted pet's transparent background into an
    /// opaque black box. dwebp streams a PNG to stdout with alpha intact, which WIC then decodes faithfully
    /// (PNG alpha is not affected). PNG/JPEG inputs (used by the self-tests) go straight through WIC.
    ///
    /// Deliberately no NuGet/managed-native dependency; the one small, self-contained, BSD-licensed exe is the
    /// least-surprising way to get correct WebP alpha on any Windows box without a Store codec.
    /// </summary>
    public static class WebPLoader
    {
        /// <summary>A loader (matching <see cref="SpriteSheetBuilder"/>'s <c>Func&lt;string,Bitmap&gt;</c>) that
        /// reads a pose image name like "/0005.webp" from <paramref name="spritesDir"/> and decodes it.</summary>
        public static Func<string, Bitmap> ForDirectory(string spritesDir)
        {
            if (string.IsNullOrEmpty(spritesDir)) throw new ArgumentNullException("spritesDir");
            return delegate(string image)
            {
                string name = (image ?? "").TrimStart('/', '\\');
                string path = Path.Combine(spritesDir, name);
                if (!File.Exists(path)) throw new FileNotFoundException("Sprite not found: " + path);
                return Decode(path);
            };
        }

        /// <summary>Decode a single image file to a Format32bppArgb bitmap, preserving its alpha channel. WebP
        /// goes through dwebp; everything else (PNG/JPEG) goes through WIC.</summary>
        public static Bitmap Decode(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                byte[] png = DwebpToPng(path);
                using (var ms = new MemoryStream(png, false))
                    return DecodeStream(ms);
            }
            using (FileStream fs = File.OpenRead(path))
                return DecodeStream(fs);
        }

        /// <summary>WIC-decode an image stream (straight BGRA) into a detached Format32bppArgb bitmap.</summary>
        private static Bitmap DecodeStream(Stream source)
        {
            System.Windows.Media.Imaging.BitmapDecoder decoder =
                System.Windows.Media.Imaging.BitmapDecoder.Create(
                    source,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            if (decoder.Frames == null || decoder.Frames.Count == 0)
                throw new InvalidOperationException("no frames decoded from the image stream");

            // Force straight BGRA (not premultiplied Pbgra32) so the bytes map 1:1 onto Format32bppArgb.
            System.Windows.Media.Imaging.FormatConvertedBitmap converted =
                new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            converted.CopyPixels(pixels, stride, 0);

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(pixels, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }

        /// <summary>Run the bundled dwebp on a .webp file and return the PNG bytes it streams to stdout.</summary>
        private static byte[] DwebpToPng(string webpPath)
        {
            string exe = FindDwebp();
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // stdout carries the binary PNG, read raw via BaseStream; Latin1 is byte-preserving so the
                // (unused) text reader can never mangle a byte, and it satisfies the redirect-encoding invariant.
                StandardOutputEncoding = System.Text.Encoding.Latin1,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            psi.ArgumentList.Add(webpPath);
            psi.ArgumentList.Add("-quiet");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("-");          // write the decoded PNG to stdout

            using (Process p = Process.Start(psi))
            {
                // Drain stderr on another thread so a chatty decoder can never deadlock the stdout read.
                Task<string> err = p.StandardError.ReadToEndAsync();
                var outBytes = new MemoryStream();
                p.StandardOutput.BaseStream.CopyTo(outBytes);
                if (!p.WaitForExit(30000))
                {
                    try { p.Kill(); } catch { }
                    throw new InvalidOperationException("dwebp timed out decoding " + webpPath);
                }
                if (p.ExitCode != 0)
                {
                    string detail = "";
                    try { detail = err.Result; } catch { }
                    throw new InvalidOperationException(
                        "dwebp failed (" + p.ExitCode + ") on " + webpPath + ": " + (detail ?? "").Trim());
                }
                byte[] png = outBytes.ToArray();
                if (png.Length == 0)
                    throw new InvalidOperationException("dwebp produced no output for " + webpPath);
                return png;
            }
        }

        private static string _dwebpPath;

        /// <summary>Locate the bundled dwebp.exe beside the converter (output\native\dwebp.exe, or flat).</summary>
        private static string FindDwebp()
        {
            if (_dwebpPath != null) return _dwebpPath;
            var roots = new[]
            {
                AppContext.BaseDirectory,
                Path.GetDirectoryName(typeof(WebPLoader).Assembly.Location),
            };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (string rel in new[] { Path.Combine("native", "dwebp.exe"), "dwebp.exe" })
                {
                    string candidate = Path.Combine(root, rel);
                    if (File.Exists(candidate)) { _dwebpPath = candidate; return candidate; }
                }
            }
            throw new FileNotFoundException(
                "dwebp.exe (the bundled WebP decoder) was not found next to the converter, so WebP sprites " +
                "cannot be decoded with their alpha channel. Expected it at native\\dwebp.exe.");
        }
    }
}
