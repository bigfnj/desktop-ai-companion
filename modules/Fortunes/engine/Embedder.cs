using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Local sentence embedder (bge-small-en-v1.5, ONNX int8) powering smart/contextual fortunes.
    /// NOT a text generator: it turns text into vectors so a fortune can be matched to the current
    /// screen context. Fully offline, no API, no keys, no download — the model, vocab and ONNX runtime
    /// ship inside the Fortunes module package itself (which is why it is ~30 MB), and load from the
    /// module's own folder, NOT the app folder. Never throws; degrades to not-ready if anything is
    /// missing, so the pet simply falls back to random fortunes. CLS-pooled + L2-normalized (bge recipe).
    /// </summary>
    internal sealed class Embedder : IDisposable
    {
        private readonly object _lock = new object();
        private InferenceSession _session;
        private Dictionary<string, int> _vocab;
        private bool _tried;
        private bool _disposed;
        private const string Unk = "[UNK]", Cls = "[CLS]", Sep = "[SEP]";
        private const int ExpectedDimension = 384;
        private const int MaximumVocabEntries = 100000;
        private const int MaximumVocabTokenCharacters = 256;
        private const long MaximumVocabBytes = 8L * 1024L * 1024L;
        private const long MaximumModelBytes = 512L * 1024L * 1024L;
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly Lazy<string> AssetFingerprintValue =
            new Lazy<string>(
                ComputeAssetFingerprint,
                LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// The folder holding this module's own files — <c>modules\fortunes\</c>, not the app root.
        /// Resolved from the executing assembly (this module's DLL) precisely so the model travels with
        /// the module package: a module can be installed/removed at runtime, so its assets can't live
        /// beside the exe. Named "AppDir" historically, from when the engine lived in the base.
        /// </summary>
        private static string AppDir
        {
            get
            {
                string d = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(d)) d = AppDomain.CurrentDomain.BaseDirectory;
                return Path.GetFullPath(d);
            }
        }
        /// <summary>bge-small ONNX model, shipped inside the module package (see <see cref="AppDir"/>).</summary>
        public static string ModelPath { get { return Path.Combine(AppDir, "bge-small.onnx"); } }
        public static string VocabPath { get { return Path.Combine(AppDir, "bge-small.vocab.txt"); } }

        /// <summary>
        /// SHA-256 identity of both model and vocabulary contents. Vector caches bind to this value,
        /// so replacing either asset cannot silently reuse embeddings from another vocabulary space.
        /// </summary>
        internal static string AssetFingerprint
        {
            get
            {
                try { return AssetFingerprintValue.Value; }
                catch { return ""; }
            }
        }

        /// <summary>Model files present in the module folder? (doesn't force a load)</summary>
        public static bool ModelPresent
        {
            get
            {
                try
                {
                    var model = new FileInfo(ModelPath);
                    var vocab = new FileInfo(VocabPath);
                    return model.Exists && model.Length > 0 &&
                           model.Length <= MaximumModelBytes &&
                           vocab.Exists && vocab.Length > 0 &&
                           vocab.Length <= MaximumVocabBytes;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static string ComputeAssetFingerprint()
        {
            if (!ModelPresent) return "";
            byte[] modelHash;
            byte[] vocabularyHash;
            using (SHA256 sha = SHA256.Create())
            using (var model = new FileStream(
                ModelPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65536,
                FileOptions.SequentialScan))
                modelHash = sha.ComputeHash(model);
            using (SHA256 sha = SHA256.Create())
            using (var vocabulary = new FileStream(
                VocabPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65536,
                FileOptions.SequentialScan))
                vocabularyHash = sha.ComputeHash(vocabulary);

            byte[] domain = StrictUtf8.GetBytes(
                "DesktopAICompanion.EmbeddingAssets.v1\n");
            byte[] combined = new byte[
                domain.Length + modelHash.Length + vocabularyHash.Length];
            Buffer.BlockCopy(domain, 0, combined, 0, domain.Length);
            Buffer.BlockCopy(
                modelHash,
                0,
                combined,
                domain.Length,
                modelHash.Length);
            Buffer.BlockCopy(
                vocabularyHash,
                0,
                combined,
                domain.Length + modelHash.Length,
                vocabularyHash.Length);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] result = sha.ComputeHash(combined);
                var text = new StringBuilder(result.Length * 2);
                for (int index = 0; index < result.Length; index++)
                    text.Append(result[index].ToString("x2"));
                return text.ToString();
            }
        }

        /// <summary>Vector dimension (bge-small = 384). Valid after the first successful embed.</summary>
        public int Dim { get; private set; }

        /// <summary>True once the model is loaded and ready to embed.</summary>
        public bool IsReady
        {
            get
            {
                EnsureLoaded();
                lock (_lock)
                    return !_disposed && _session != null && _vocab != null;
            }
        }

        private void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_tried || _disposed) return;
                _tried = true;
                InferenceSession session = null;
                try
                {
                    if (!ModelPresent) return;

                    Dictionary<string, int> vocab;
                    string error;
                    if (!TryLoadVocabulary(VocabPath, out vocab, out error)) return;

                    session = new InferenceSession(ModelPath);
                    if (session.InputMetadata == null ||
                        session.InputMetadata.Count == 0 ||
                        session.OutputMetadata == null ||
                        session.OutputMetadata.Count == 0)
                        return;
                    _vocab = vocab;
                    _session = session;
                    session = null;
                }
                catch
                {
                    _session = null;
                    _vocab = null;
                }
                finally
                {
                    if (session != null)
                    {
                        try { session.Dispose(); } catch { }
                    }
                }
            }
        }

        /// <summary>Embed text to a unit-length vector, or null when the embedder isn't ready.</summary>
        public float[] Embed(string text)
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                lock (_lock)
                {
                    if (_disposed || _session == null || _vocab == null) return null;
                    long[] ids = Encode(text);
                    int n = ids.Length;
                    var inputIds = new DenseTensor<long>(ids, new[] { 1, n });
                    var mask = new DenseTensor<long>(Enumerable.Repeat(1L, n).ToArray(), new[] { 1, n });
                    var types = new DenseTensor<long>(new long[n], new[] { 1, n });

                    var feeds = new List<NamedOnnxValue>();
                    foreach (string key in _session.InputMetadata.Keys)
                    {
                        if (key.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0) feeds.Add(NamedOnnxValue.CreateFromTensor(key, mask));
                        else if (key.IndexOf("type", StringComparison.OrdinalIgnoreCase) >= 0) feeds.Add(NamedOnnxValue.CreateFromTensor(key, types));
                        else feeds.Add(NamedOnnxValue.CreateFromTensor(key, inputIds));
                    }
                    using (var results = _session.Run(feeds))
                    {
                        var t = results.First().AsTensor<float>();
                        int dims = t.Dimensions.Length;
                        if (dims != 2 && dims != 3) return null;
                        int hidden = t.Dimensions[dims - 1];
                        if (hidden != ExpectedDimension) return null;
                        var v = new float[hidden];
                        if (dims == 3) for (int k = 0; k < hidden; k++) v[k] = t[0, 0, k];   // CLS token
                        else           for (int k = 0; k < hidden; k++) v[k] = t[0, k];
                        if (!Normalize(v)) return null;
                        Dim = hidden;
                        return v;
                    }
                }
            }
            catch { return null; }
        }

        // ---- minimal BERT (uncased) WordPiece ----------------------------------
        private long[] Encode(string text)
        {
            var ids = new List<long> { _vocab[Cls] };
            foreach (string word in BasicTokenize(text))
                foreach (string piece in WordPiece(word))
                {
                    int id;
                    ids.Add(_vocab.TryGetValue(piece, out id) ? id : _vocab[Unk]);
                }
            ids.Add(_vocab[Sep]);
            if (ids.Count > 256) ids = ids.Take(255).Concat(new long[] { _vocab[Sep] }).ToList();
            return ids.ToArray();
        }

        private static IEnumerable<string> BasicTokenize(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text.ToLowerInvariant())
            {
                if (char.IsWhiteSpace(c)) { if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } }
                else if (char.IsPunctuation(c) || char.IsSymbol(c)) { if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); } yield return c.ToString(); }
                else sb.Append(c);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        private IEnumerable<string> WordPiece(string word)
        {
            if (word.Length > 100) { yield return Unk; yield break; }
            int start = 0;
            var pieces = new List<string>();
            while (start < word.Length)
            {
                int end = word.Length;
                string cur = null;
                while (start < end)
                {
                    string sub = (start > 0 ? "##" : "") + word.Substring(start, end - start);
                    if (_vocab.ContainsKey(sub)) { cur = sub; break; }
                    end--;
                }
                if (cur == null) { yield return Unk; yield break; }
                pieces.Add(cur);
                start = end;
            }
            foreach (string p in pieces) yield return p;
        }

        internal static bool TryLoadVocabulary(
            string path,
            out Dictionary<string, int> vocabulary,
            out string error)
        {
            vocabulary = null;
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                    throw new InvalidDataException("Vocabulary path must be absolute.");
                string canonical = Path.GetFullPath(path);
                using (var stream = new FileStream(
                    canonical,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    8192,
                    FileOptions.SequentialScan))
                {
                    if (stream.Length < 1 || stream.Length > MaximumVocabBytes)
                        throw new InvalidDataException(
                            "Vocabulary file size is outside its allowed range.");

                    var parsed = new Dictionary<string, int>(StringComparer.Ordinal);
                    using (var reader = new StreamReader(
                        stream,
                        StrictUtf8,
                        false,
                        8192))
                    {
                        string token;
                        int id = 0;
                        while ((token = reader.ReadLine()) != null)
                        {
                            if (id >= MaximumVocabEntries)
                                throw new InvalidDataException(
                                    "Vocabulary contains too many entries.");
                            if (!IsValidVocabularyToken(token))
                                throw new InvalidDataException(
                                    "Vocabulary contains an invalid token at index " + id + ".");
                            if (parsed.ContainsKey(token))
                                throw new InvalidDataException(
                                    "Vocabulary contains a duplicate token.");
                            parsed.Add(token, id++);
                        }
                    }

                    if (!parsed.ContainsKey(Unk) ||
                        !parsed.ContainsKey(Cls) ||
                        !parsed.ContainsKey(Sep))
                        throw new InvalidDataException(
                            "Vocabulary is missing a required special token.");
                    vocabulary = parsed;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                vocabulary = null;
                return false;
            }
        }

        private static bool IsValidVocabularyToken(string token)
        {
            if (string.IsNullOrEmpty(token) ||
                token.Length > MaximumVocabTokenCharacters)
                return false;
            for (int index = 0; index < token.Length; index++)
            {
                char character = token[index];
                if (char.IsControl(character) || char.IsWhiteSpace(character))
                    return false;
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= token.Length ||
                        !char.IsLowSurrogate(token[index + 1]))
                        return false;
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
                else if (char.GetUnicodeCategory(character) ==
                         System.Globalization.UnicodeCategory.Format)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool Normalize(float[] v)
        {
            if (v == null || v.Length != ExpectedDimension) return false;
            double s = 0;
            foreach (float x in v)
            {
                if (float.IsNaN(x) || float.IsInfinity(x)) return false;
                s += (double)x * x;
            }
            if (double.IsNaN(s) || double.IsInfinity(s)) return false;
            float inv = (float)(1.0 / Math.Sqrt(Math.Max(s, 1e-12)));
            for (int i = 0; i < v.Length; i++) v[i] *= inv;
            return true;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                InferenceSession session = _session;
                _session = null;
                _vocab = null;
                try { if (session != null) session.Dispose(); } catch { }
            }
        }

        /// <summary>Diagnostic: load the model and write status + cosines to a temp file.</summary>
        public static bool SelfTest()
        {
            string outp = Path.Combine(Path.GetTempPath(), "dp-embed-selftest.txt");
            var sb = new StringBuilder();
            bool ok = false;
            bool vocabularyOk = VocabularySelfTest(sb);
            bool concurrentFirstUseOk = ConcurrentFirstUseSelfTest(sb);
            try
            {
                using (var e = new Embedder())
                {
                    sb.AppendLine("ModelPresent=" + ModelPresent + " dir=" + AppDir);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ready = e.IsReady;
                    sw.Stop();
                    sb.AppendLine("IsReady=" + ready + " loadMs=" + sw.ElapsedMilliseconds);
                    if (ready)
                    {
                        float[] a = e.Embed("I love programming in C sharp");
                        float[] b = e.Embed("Writing code in dot net is really fun");
                        float[] c = e.Embed("The weather outside is freezing cold today");
                        sb.AppendLine("dim=" + (a == null ? 0 : a.Length));
                        float related = Dot(a, b);
                        float unrelated = Dot(a, c);
                        sb.AppendLine("cos(code,code)=" + related.ToString("F4") + " (expect HIGH)");
                        sb.AppendLine("cos(code,weather)=" + unrelated.ToString("F4") + " (expect LOW)");
                        ok = vocabularyOk && concurrentFirstUseOk &&
                            a != null && b != null && c != null && a.Length == e.Dim &&
                            b.Length == e.Dim && c.Length == e.Dim && related > unrelated;
                    }
                }
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message);
            }
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(outp, sb.ToString()); }
            catch { return false; }
            return ok;
        }

        private static bool ConcurrentFirstUseSelfTest(StringBuilder report)
        {
            if (!ModelPresent)
            {
                report.AppendLine("concurrent_first_use=SKIP (model unavailable)");
                return true;
            }

            const int workers = 4;
            bool ok = true;
            try
            {
                using (var embedder = new Embedder())
                using (var ready = new CountdownEvent(workers))
                using (var start = new ManualResetEventSlim(false))
                {
                    var tasks = new Task<float[]>[workers];
                    for (int i = 0; i < workers; i++)
                    {
                        int worker = i;
                        tasks[i] = Task.Run(delegate
                        {
                            ready.Signal();
                            start.Wait();
                            return embedder.Embed(
                                "concurrent first use worker " + worker);
                        });
                    }
                    if (!ready.Wait(TimeSpan.FromSeconds(10)))
                    {
                        ok = false;
                        start.Set();
                    }
                    else
                    {
                        start.Set();
                    }
                    if (!Task.WaitAll(tasks, TimeSpan.FromMinutes(2)))
                    {
                        ok = false;
                    }
                    else
                    {
                        foreach (Task<float[]> task in tasks)
                            ok &= task.Status == TaskStatus.RanToCompletion &&
                                  task.Result != null &&
                                  task.Result.Length == ExpectedDimension;
                    }
                }
            }
            catch
            {
                ok = false;
            }
            report.AppendLine(
                "concurrent_first_use=" + (ok ? "PASS" : "FAIL"));
            return ok;
        }

        private static bool VocabularySelfTest(StringBuilder report)
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "DesktopAICompanion-vocab-selftest-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "vocab.txt");
            bool ok = true;
            try
            {
                Directory.CreateDirectory(directory);
                Dictionary<string, int> parsed;
                string error;

                File.WriteAllText(
                    path,
                    "[UNK]\n[CLS]\n[SEP]\nhello\n",
                    StrictUtf8);
                ok &= TryLoadVocabulary(path, out parsed, out error) &&
                      parsed.Count == 4 &&
                      parsed[Unk] == 0 &&
                      parsed[Cls] == 1 &&
                      parsed[Sep] == 2;

                File.WriteAllText(
                    path,
                    "[UNK]\n[CLS]\n[SEP]\nhello\nhello\n",
                    StrictUtf8);
                ok &= !TryLoadVocabulary(path, out parsed, out error);

                File.WriteAllText(path, "[UNK]\n[CLS]\nhello\n", StrictUtf8);
                ok &= !TryLoadVocabulary(path, out parsed, out error);

                File.WriteAllBytes(path, new byte[] { 0xC3, 0x28 });
                ok &= !TryLoadVocabulary(path, out parsed, out error);

                using (var oversized = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                    oversized.SetLength(MaximumVocabBytes + 1);
                ok &= !TryLoadVocabulary(path, out parsed, out error);
            }
            catch
            {
                ok = false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch
                {
                    ok = false;
                }
            }
            report.AppendLine("vocab_validation=" + (ok ? "PASS" : "FAIL"));
            return ok;
        }

        private static float Dot(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 0f;
            float d = 0; for (int i = 0; i < a.Length; i++) d += a[i] * b[i]; return d;
        }
    }
}
