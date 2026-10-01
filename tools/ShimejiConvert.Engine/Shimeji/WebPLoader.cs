using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
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
    /// opaque black box. dwebp streams its decode to stdout as a PAM -- a seven-line header and the raw RGBA
    /// samples -- which is copied straight into the bitmap. It used to stream a PNG instead, which cost a full
    /// deflate on dwebp's side and a WIC PNG decode on this side for every distinct sprite, both existing only
    /// as transport (measured in the audit at 7-10 ms per cartoon sprite and ~35 ms per noisy one on the dwebp
    /// half alone, once per distinct sprite of an import); the PAM carries the same bytes with neither (F460).
    /// PNG/JPEG inputs (used by the self-tests) still go through WIC, which decodes PNG alpha faithfully.
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
                int pamLength;
                byte[] pam = DwebpToPam(path, out pamLength);
                return DecodePam(pam, pamLength, path);
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

        // The largest side this decoder will accept from a PAM header. libwebp itself caps a WebP at 16383
        // per side, so nothing real is refused; it exists to keep the allocation arithmetic honest against a
        // header that lies.
        private const int MaxPamSide = 16384;
        // dwebp's header is under a hundred bytes; a header that has not ended by here is not dwebp's.
        private const int MaxPamHeaderBytes = 1024;

        /// <summary>
        /// Turn dwebp's PAM into a Format32bppArgb bitmap. The header is seven ASCII lines --
        /// <c>P7</c>, <c>WIDTH w</c>, <c>HEIGHT h</c>, <c>DEPTH 4</c>, <c>MAXVAL 255</c>,
        /// <c>TUPLTYPE RGB_ALPHA</c>, <c>ENDHDR</c> -- followed by width*height*4 bytes of straight (not
        /// premultiplied) RGBA, top row first. GDI+ wants BGRA, so R and B swap on the way in. Anything but the
        /// 8-bit RGB_ALPHA shape is refused loudly rather than misread as pixels. <paramref name="pam"/> may be
        /// longer than the payload it holds (a stream's buffer), so every read is bounded by
        /// <paramref name="length"/>, never by the array (R-074).
        /// </summary>
        private static Bitmap DecodePam(byte[] pam, int length, string webpPath)
        {
            if (pam == null || length > pam.Length) throw new ArgumentException("PAM length exceeds its buffer", "length");
            if (length < 3 || pam[0] != (byte)'P' || pam[1] != (byte)'7')
                throw new InvalidOperationException("dwebp did not write a PAM (no P7 magic) for " + webpPath);

            int width = 0, height = 0, depth = 0, maxval = 0;
            string tupltype = null;
            int pos = 0;
            bool ended = false;
            while (pos < length && pos < MaxPamHeaderBytes)
            {
                int nl = Array.IndexOf(pam, (byte)'\n', pos, Math.Min(length, MaxPamHeaderBytes) - pos);
                if (nl < 0) break;
                string line = System.Text.Encoding.ASCII.GetString(pam, pos, nl - pos).Trim();
                pos = nl + 1;
                if (line.Length == 0 || line[0] == '#' || line == "P7") continue;
                if (line == "ENDHDR") { ended = true; break; }
                int space = line.IndexOf(' ');
                string key = space < 0 ? line : line.Substring(0, space);
                string value = space < 0 ? "" : line.Substring(space + 1).Trim();
                switch (key)
                {
                    case "WIDTH": width = HeaderInt(value, key, webpPath); break;
                    case "HEIGHT": height = HeaderInt(value, key, webpPath); break;
                    case "DEPTH": depth = HeaderInt(value, key, webpPath); break;
                    case "MAXVAL": maxval = HeaderInt(value, key, webpPath); break;
                    case "TUPLTYPE": tupltype = value; break;
                    default: break;   // a header key this decoder does not know is not a reason to refuse
                }
            }
            if (!ended)
                throw new InvalidOperationException("dwebp's PAM header for " + webpPath + " did not end within "
                    + MaxPamHeaderBytes + " bytes");
            if (depth != 4 || maxval != 255 || !string.Equals(tupltype, "RGB_ALPHA", StringComparison.Ordinal))
                throw new InvalidOperationException("dwebp wrote a PAM this decoder does not read for " + webpPath
                    + " (DEPTH " + depth + ", MAXVAL " + maxval + ", TUPLTYPE " + (tupltype ?? "(none)")
                    + "); 8-bit RGB_ALPHA is the only shape the alpha path accepts");
            if (width <= 0 || height <= 0 || width > MaxPamSide || height > MaxPamSide)
                throw new InvalidOperationException("dwebp's PAM for " + webpPath + " declares an impossible size "
                    + width + "x" + height);
            long expected = (long)width * height * 4;
            if ((long)length - pos < expected)
                throw new InvalidOperationException("dwebp's PAM payload for " + webpPath + " is short: "
                    + (length - pos) + " of " + expected + " bytes");

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int rowBytes = width * 4;
                    var row = new byte[rowBytes];
                    for (int y = 0; y < height; y++)
                    {
                        int src = pos + y * rowBytes;
                        for (int x = 0; x < rowBytes; x += 4)
                        {
                            row[x + 0] = pam[src + x + 2];   // B <- R's slot
                            row[x + 1] = pam[src + x + 1];   // G
                            row[x + 2] = pam[src + x + 0];   // R <- B's slot
                            row[x + 3] = pam[src + x + 3];   // A, straight
                        }
                        Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                throw;
            }
        }

        private static int HeaderInt(string value, string key, string webpPath)
        {
            int parsed;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                throw new InvalidOperationException("dwebp's PAM header for " + webpPath + " has a non-numeric "
                    + key + " (" + value + ")");
            return parsed;
        }

        /// <summary>Run the bundled dwebp on a .webp file and return the PAM bytes it streams to stdout. The
        /// array is the stream's own buffer and may be longer than the payload; <paramref name="length"/> is
        /// the payload. Handing the buffer over instead of ToArray() removes one whole copy of the raw RGBA
        /// (1 MiB for a 512px sprite, 256 MiB for an 8192px background) from the transient the decode holds
        /// before the bitmap exists (R-074). A property, not a measured saving.</summary>
        private static byte[] DwebpToPam(string webpPath, out int length)
        {
            string exe = FindDwebp();
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // stdout carries the binary PAM, read raw via BaseStream; Latin1 is byte-preserving so the
                // (unused) text reader can never mangle a byte, and it satisfies the redirect-encoding invariant.
                StandardOutputEncoding = System.Text.Encoding.Latin1,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            psi.ArgumentList.Add(webpPath);
            psi.ArgumentList.Add("-quiet");
            psi.ArgumentList.Add("-pam");       // raw RGBA behind a text header: no PNG encode, no PNG decode
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("-");          // write it to stdout

            using (Process p = Process.Start(psi))
            {
                // Drain stderr on another thread so a chatty decoder can never deadlock the stdout read.
                Task<string> err = p.StandardError.ReadToEndAsync();
                var outBytes = new MemoryStream();

                // BOUND THE READ, NOT ONLY THE WAIT. This was a synchronous CopyTo, which blocks until
                // stdout reaches EOF -- and for dwebp, EOF means the process exited. So the copy had to
                // finish before WaitForExit(30000) was reached at all, which put the timeout after the
                // only thing it could usefully bound. A dwebp that hung WITHOUT closing stdout hung the
                // converter permanently, and the 30 seconds this line exists for never applied to it.
                // The PAM is larger than the PNG was (four bytes a pixel, 1 MiB for a 512px sprite), and the
                // bound is unchanged: it is a wall-clock bound on a hung child, not a size.
                const int TimeoutMs = 30000;
                Task copy = p.StandardOutput.BaseStream.CopyToAsync(outBytes);
                if (!copy.Wait(TimeoutMs))
                {
                    // Kill(true) takes the tree, matching Engine.cs. dwebp spawns nothing today, but a
                    // killed parent leaving a child holding the pipe is how this fix quietly comes undone.
                    try { p.Kill(true); } catch { }
                    throw new InvalidOperationException("dwebp timed out decoding " + webpPath);
                }
                // stdout is at EOF here, so the process is already finishing: this second bound covers
                // teardown, not work, which is why it is short rather than another 30 seconds.
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(true); } catch { }
                    throw new InvalidOperationException("dwebp timed out exiting after decoding " + webpPath);
                }
                if (p.ExitCode != 0)
                {
                    string detail = "";
                    try { detail = err.Result; } catch { }
                    throw new InvalidOperationException(
                        "dwebp failed (" + p.ExitCode + ") on " + webpPath + ": " + (detail ?? "").Trim());
                }
                if (outBytes.Length == 0)
                    throw new InvalidOperationException("dwebp produced no output for " + webpPath);
                ArraySegment<byte> segment;
                if (outBytes.TryGetBuffer(out segment) && segment.Offset == 0)
                {
                    length = segment.Count;
                    return segment.Array;
                }
                // A MemoryStream this method created is always exposable; the copy is the fallback the API
                // contract requires, not a path anything here reaches.
                byte[] pam = outBytes.ToArray();
                length = pam.Length;
                return pam;
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
