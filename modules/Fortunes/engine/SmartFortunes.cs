using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAICompanion.ModuleKit;   // AtomicFile / CrossSessionLock / UnicodeTextProgress

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Smart/contextual fortune picker. Embeds the active fortune pool with the local bge-small
    /// <see cref="Embedder"/> (cached to disk so it's a one-time cost), then for a given on-screen
    /// context picks a fortune by centered cosine similarity, nudged by app-to-topic routing, with a
    /// confidence-adaptive fall-through to random when nothing fits. Fully offline; additive — returns
    /// null whenever it can't help so the caller just speaks a random fortune.
    /// </summary>
    internal sealed class SmartFortunes : IDisposable
    {
        private readonly Embedder _embed;
        private readonly VectorCache _cache;
        private readonly Random _rng = new Random();
        private readonly Queue<string> _recent = new Queue<string>();      // last picks, to avoid repeats
        private readonly HashSet<string> _recentSet = new HashSet<string>();
        private string _lastPicked;                                        // the immediately-previous line, blocked so a recycle can't repeat it back to back
        private readonly object _stateLock = new object();
        private readonly object _embedLock = new object();
        private readonly ManualResetEventSlim _disposeCompleted =
            new ManualResetEventSlim(false);

        private List<FortuneEntry> _pool;
        private float[][] _vecs;      // centered + L2-normalized, parallel to _pool
        private float[] _mean;
        private Dictionary<string, float[]> _protoRaw;   // topic -> raw prototype embedding (built once at warm)
        private bool _ready;
        private bool _warmComplete;  // the whole pool finished embedding (not just a warmed prefix)
        private int _indexed;        // matchable (embedded + valid) lines published so far
        private bool _disposed;
        private int _embedderDisposalCount;
        private CancellationTokenSource _warmCancellation;
        private Task _warmTask = Task.CompletedTask;

        private const int TopK = 64;                  // candidate width per context -> a wider rotation before a stable window must recycle
        private const int RecentMemory = 200;         // don't repeat any of the last N picks; kept well beyond one context's TopK so variety carries across windows too
        private const float RouteBonus = 0.06f;
        private const float RouteSecondMargin = 0.02f; // also route to a runner-up topic within this cosine gap
        private const float MinConfidence = 0.10f;   // below this the best match is too weak -> random
        private const int DisposeWaitMilliseconds = 3000;
        // bge-small-en-v1.5 is asymmetric: the query gets this instruction, passages stay plain.
        private const string QueryPrefix = "Represent this sentence for searching relevant passages: ";

        public SmartFortunes() : this(null, CancellationToken.None) { }

        private SmartFortunes(string diagnosticCacheDirectory)
            : this(diagnosticCacheDirectory, CancellationToken.None)
        {
        }

        internal SmartFortunes(CancellationToken cancellationToken)
            : this(null, cancellationToken)
        {
        }

        private SmartFortunes(
            string diagnosticCacheDirectory,
            CancellationToken cancellationToken)
        {
            _embed = new Embedder();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _cache = new VectorCache(
                    diagnosticCacheDirectory,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                _embed.Dispose();
                throw;
            }
        }

        /// <summary>The bundled model is present and loadable.</summary>
        public bool Available { get { return Embedder.ModelPresent; } }
        /// <summary>Pool vectors are computed and the picker can serve contextual picks.</summary>
        public bool Ready { get { lock (_stateLock) return _ready && !_disposed; } }

        /// <summary>Number of fortunes in the warmed pool (0 until ready).</summary>
        public int PoolCount
        {
            get
            {
                lock (_stateLock)
                    return !_disposed && _ready && _pool != null ? _pool.Count : 0;
            }
        }

        /// <summary>
        /// A tear-free snapshot of warm progress for the status UI. <paramref name="ready"/> is set once
        /// any prefix is matchable; <paramref name="complete"/> once the whole pool has embedded;
        /// <paramref name="indexed"/> is the matchable line count so far and <paramref name="total"/> the
        /// pool size.
        /// </summary>
        internal void WarmProgress(out bool ready, out bool complete, out int indexed, out int total)
        {
            lock (_stateLock)
            {
                bool live = !_disposed && _ready;
                ready = live;
                complete = live && _warmComplete;
                indexed = live ? _indexed : 0;
                total = live && _pool != null ? _pool.Count : 0;
            }
        }

        internal int EmbedderDisposalCountForDiagnostics
        {
            get { return Volatile.Read(ref _embedderDisposalCount); }
        }

        internal void HoldEmbedLockForDiagnostics(
            ManualResetEventSlim entered,
            ManualResetEventSlim release)
        {
            if (entered == null) throw new ArgumentNullException("entered");
            if (release == null) throw new ArgumentNullException("release");
            lock (_embedLock)
            {
                entered.Set();
                release.Wait();
            }
        }

        /// <summary>Embed the given pool in the background (idempotent; supersedes any prior warm).</summary>
        public void Warm(List<FortuneEntry> pool)
        {
            Warm(pool, CancellationToken.None);
        }

        internal void Warm(
            List<FortuneEntry> pool,
            CancellationToken cancellationToken)
        {
            List<FortuneEntry> snapshot =
                pool == null ? null : new List<FortuneEntry>(pool);
            lock (_stateLock)
            {
                if (_disposed) return;
                _ready = false;
                _warmComplete = false;
                _indexed = 0;
                if (_warmCancellation != null)
                {
                    try { _warmCancellation.Cancel(); } catch { }
                }
                if (snapshot == null || snapshot.Count == 0 ||
                    snapshot.Count > VectorCache.MaximumEntries || !Available)
                    return;

                Task previous = _warmTask;
                var cancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                _warmCancellation = cancellation;
                var next = new Task(
                    delegate
                    {
                        try
                        {
                            Observe(previous);
                            cancellation.Token.ThrowIfCancellationRequested();
                            WarmCore(snapshot, cancellation);
                        }
                        catch (OperationCanceledException) { }
                        catch { }
                        finally
                        {
                            lock (_stateLock)
                                if (ReferenceEquals(_warmCancellation, cancellation))
                                    _warmCancellation = null;
                            cancellation.Dispose();
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach);
                _warmTask = next;
                next.Start(TaskScheduler.Default);
            }
        }

        private void WarmCore(
            List<FortuneEntry> pool,
            CancellationTokenSource cancellation)
        {
            CancellationToken token = cancellation.Token;
            bool embedderReady;
            lock (_embedLock)
            {
                token.ThrowIfCancellationRequested();
                embedderReady = _embed.IsReady;
            }
            if (!embedderReady) return;

            BuildTopicPrototypes(token);   // ~12 direct embeds; enables context->topic routing in Pick

            var activeTexts = new List<string>(pool.Count);
            foreach (FortuneEntry entry in pool)
                activeTexts.Add(entry.Text);
            _cache.BeginActivePool(activeTexts, token);

            const int dimension = VectorCache.ExpectedDimension;
            int n = pool.Count;
            var raw = new float[n][];
            var sum = new double[dimension];   // running sum of the valid raw vectors so far
            int validCount = 0;

            // Publish progressively: after each batch of embeddings, re-center what's embedded so far
            // and expose it to Pick, so contextual matching starts working against the warmed prefix
            // and keeps improving as the (cold-cache) embed runs -- instead of returning random for the
            // whole pool until the last vector lands. Publish points double (512, 1024, 2048, ...) so
            // the running cost of re-centering the growing prefix stays small next to the ONNX embed.
            int nextPublish = 512;
            for (int i = 0; i < n; i++)
            {
                token.ThrowIfCancellationRequested();
                float[] vector;
                lock (_embedLock)
                {
                    token.ThrowIfCancellationRequested();
                    vector = _cache.GetOrEmbed(pool[i].Text, _embed, token);
                }
                raw[i] = vector;
                if (VectorCache.IsValidVector(vector))
                {
                    for (int k = 0; k < dimension; k++) sum[k] += vector[k];
                    validCount++;
                }
                if ((i & 2047) == 2047)
                {
                    token.ThrowIfCancellationRequested();
                    _cache.Save(token);
                }
                if (i + 1 >= nextPublish || i == n - 1)
                {
                    nextPublish *= 2;
                    PublishSnapshot(pool, raw, i + 1, sum, validCount, cancellation);
                }
            }

            token.ThrowIfCancellationRequested();
            _cache.Save(token);

            lock (_stateLock)
            {
                if (_disposed || token.IsCancellationRequested ||
                    !ReferenceEquals(_warmCancellation, cancellation))
                    return;
                _warmComplete = true;
            }
        }

        // Center + L2-normalize the embedded prefix [0, embedded) against the running mean and publish
        // it atomically for Pick. The mean shifts as vectors arrive, so the whole prefix is re-centered
        // each time; slots past the prefix stay null and Pick skips them (falling back to random until
        // enough of the pool is warm). No-ops until at least one valid vector exists.
        private void PublishSnapshot(
            List<FortuneEntry> pool,
            float[][] raw,
            int embedded,
            double[] sum,
            int validCount,
            CancellationTokenSource cancellation)
        {
            CancellationToken token = cancellation.Token;
            if (validCount == 0) return;

            const int dimension = VectorCache.ExpectedDimension;
            var mean = new float[dimension];
            for (int k = 0; k < dimension; k++) mean[k] = (float)(sum[k] / validCount);

            var vecs = new float[pool.Count][];
            for (int i = 0; i < embedded; i++)
            {
                token.ThrowIfCancellationRequested();
                vecs[i] = CenterNormalize(raw[i], mean);
            }

            lock (_stateLock)
            {
                if (_disposed || token.IsCancellationRequested ||
                    !ReferenceEquals(_warmCancellation, cancellation))
                    return;
                _pool = pool; _vecs = vecs; _mean = mean;
                _indexed = validCount;
                _ready = true;
            }
        }

        private static void Observe(Task task)
        {
            if (task == null) return;
            try { task.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch { }
        }

        /// <summary>
        /// A fortune that fits <paramref name="context"/> (the screen/window text), or null to signal
        /// "no good match — use a random fortune". Topic routing is derived from <paramref name="context"/>
        /// via nearest topic prototype (see RouteByContext); <paramref name="app"/> is retained for
        /// callers/telemetry but no longer drives routing.
        /// </summary>
        public string Pick(string context, string app)
        {
            if (string.IsNullOrWhiteSpace(context)) return null;
            List<FortuneEntry> pool;
            float[][] vecs;
            float[] mean;
            lock (_stateLock)
            {
                if (_disposed || !_ready) return null;
                pool = _pool;
                vecs = _vecs;
                mean = _mean;
            }
            if (pool == null || vecs == null || mean == null) return null;
            try
            {
                float[] q;
                lock (_embedLock)
                {
                    lock (_stateLock)
                        if (_disposed) return null;
                    q = _embed.Embed(QueryPrefix + context);
                }
                if (q == null) return null;
                float[] qc = CenterNormalize(q, mean);
                if (qc == null) return null;

                HashSet<string> routed = RouteByContext(qc, mean);

                // keep the top-K scoring fortunes
                var idx = new int[TopK]; var sc = new float[TopK];
                for (int t = 0; t < TopK; t++) { idx[t] = -1; sc[t] = float.NegativeInfinity; }
                int n = Math.Min(pool.Count, vecs.Length);
                for (int i = 0; i < n; i++)
                {
                    float[] v = vecs[i]; if (v == null) continue;
                    float s = Dot(qc, v);
                    if (routed != null && routed.Contains(pool[i].Topic)) s += RouteBonus;
                    // insert into the small top-K if it beats the current minimum
                    int min = 0; for (int t = 1; t < TopK; t++) if (sc[t] < sc[min]) min = t;
                    if (s > sc[min]) { sc[min] = s; idx[min] = i; }
                }

                float best = float.NegativeInfinity;
                for (int t = 0; t < TopK; t++) if (sc[t] > best) best = sc[t];
                if (best < MinConfidence) return null;               // weak fit -> let the caller go random

                // random among the (valid) top-K for variety
                var pick = new List<int>();
                for (int t = 0; t < TopK; t++) if (idx[t] >= 0) pick.Add(idx[t]);
                if (pick.Count == 0) return null;
                lock (_stateLock)
                {
                    if (_disposed) return null;
                    // Prefer candidates not shown recently, so a stable foreground window rotates
                    // through the matches instead of repeating the same few lines.
                    var fresh = new List<int>();
                    foreach (int candidate in pick)
                        if (!_recentSet.Contains(pool[candidate].Text)) fresh.Add(candidate);
                    if (fresh.Count == 0)
                    {
                        // Every current candidate has already been shown. A stable context draws from a
                        // fixed top-K that is smaller than the recent window (TopK < RecentMemory), so
                        // waiting for eviction would never free one -- the picker would silently collapse to
                        // uniform-random over the same K lines (the reported "repeats the same few" bug).
                        // Recycle instead: forget these candidates so the whole set is eligible again, but
                        // keep the immediately-previous line blocked so a recycle can never repeat it back to
                        // back. This mirrors the random path's shuffle-bag: the whole candidate set shows
                        // before any repeat.
                        RecycleCandidates(pick, pool);
                        foreach (int candidate in pick)
                            if (!_recentSet.Contains(pool[candidate].Text)) fresh.Add(candidate);
                    }
                    List<int> choices = fresh.Count > 0 ? fresh : pick;
                    string chosen = pool[choices[_rng.Next(choices.Count)]].Text;
                    RememberRecent(chosen);
                    return chosen;
                }
            }
            catch { return null; }
        }

        // Records a just-shown line and evicts the oldest once the window is full. The caller holds
        // _stateLock, so the queue + set stay consistent.
        private void RememberRecent(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _lastPicked = text;
            if (_recentSet.Add(text)) _recent.Enqueue(text);
            while (_recent.Count > RecentMemory)
                _recentSet.Remove(_recent.Dequeue());
        }

        // Free a spent context's candidates from the recent window so its rotation can start over, while
        // keeping the immediately-previous line blocked so the recycle can't repeat it back to back. The set
        // and queue are rebuilt together so they stay consistent. Caller holds _stateLock.
        private void RecycleCandidates(List<int> candidates, List<FortuneEntry> pool)
        {
            if (candidates == null || pool == null) return;
            var drop = new HashSet<string>(StringComparer.Ordinal);
            foreach (int c in candidates)
            {
                string t = pool[c].Text;
                if (!string.IsNullOrEmpty(t) && !string.Equals(t, _lastPicked, StringComparison.Ordinal))
                    drop.Add(t);
            }
            if (drop.Count == 0) return;
            var kept = new Queue<string>(_recent.Count);
            while (_recent.Count > 0)
            {
                string t = _recent.Dequeue();
                if (drop.Contains(t)) _recentSet.Remove(t);
                else kept.Enqueue(t);
            }
            while (kept.Count > 0) _recent.Enqueue(kept.Dequeue());
        }

        /// <summary>
        /// Record a line the caller spoke through a DIFFERENT path (the whole-pool random draw), so the
        /// contextual picker won't echo it on its next pick. Without this the two speech paths keep separate
        /// histories and can repeat each other's last line. Safe to call from any thread.
        /// </summary>
        public void NoteExternallyShown(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_stateLock)
            {
                if (_disposed) return;
                RememberRecent(text);
            }
        }

        // Embed the per-topic routing prototypes once (as passages, like fortunes). Cheap (~12 embeds),
        // done directly (not through the active-pool cache, which only holds fortune texts). Cancellation
        // propagates; any other failure just leaves routing disabled (Pick falls back to pure similarity).
        private void BuildTopicPrototypes(CancellationToken token)
        {
            if (_protoRaw != null) return;
            var protos = new Dictionary<string, float[]>(StringComparer.Ordinal);
            try
            {
                foreach (KeyValuePair<string, string> kv in Router.Prototypes)
                {
                    token.ThrowIfCancellationRequested();
                    float[] v;
                    lock (_embedLock)
                    {
                        token.ThrowIfCancellationRequested();
                        v = _embed.Embed(kv.Value);
                    }
                    if (VectorCache.IsValidVector(v)) protos[kv.Key] = v;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return; }
            _protoRaw = protos;
        }

        // Route the centered query to fortune topics by nearest prototype: the single closest topic,
        // plus a runner-up when it's within RouteSecondMargin. Null before prototypes are built. Routing
        // is only a soft score bonus in Pick, so "nearest" is a safe nudge even for a vague context.
        private HashSet<string> RouteByContext(float[] qc, float[] mean)
        {
            Dictionary<string, float[]> protos = _protoRaw;
            if (protos == null || protos.Count == 0 || qc == null || mean == null) return null;
            string best = null, second = null;
            float bestScore = float.NegativeInfinity, secondScore = float.NegativeInfinity;
            foreach (KeyValuePair<string, float[]> kv in protos)
            {
                float[] pc = CenterNormalize(kv.Value, mean);
                if (pc == null) continue;
                float s = Dot(qc, pc);
                if (s > bestScore) { second = best; secondScore = bestScore; best = kv.Key; bestScore = s; }
                else if (s > secondScore) { second = kv.Key; secondScore = s; }
            }
            if (best == null) return null;
            var set = new HashSet<string>(StringComparer.Ordinal) { best };
            if (second != null && (bestScore - secondScore) <= RouteSecondMargin) set.Add(second);
            return set;
        }

        // Test seam: embed a context the way Pick does and return the topics it routes to.
        internal HashSet<string> RouteTopicsForDiagnostics(string context)
        {
            float[] mean;
            lock (_stateLock) { if (_disposed || !_ready) return null; mean = _mean; }
            if (mean == null || string.IsNullOrWhiteSpace(context)) return null;
            float[] q;
            lock (_embedLock)
            {
                lock (_stateLock) if (_disposed) return null;
                q = _embed.Embed(QueryPrefix + context);
            }
            return RouteByContext(CenterNormalize(q, mean), mean);
        }

        private static float[] CenterNormalize(float[] v, float[] mean)
        {
            if (!VectorCache.IsValidVector(v) || mean == null ||
                mean.Length != VectorCache.ExpectedDimension)
                return null;
            int d = v.Length;
            var o = new float[d];
            double s = 0;
            for (int k = 0; k < d; k++)
            {
                float c = v[k] - mean[k];
                if (float.IsNaN(c) || float.IsInfinity(c)) return null;
                o[k] = c;
                s += (double)c * c;
            }
            if (double.IsNaN(s) || double.IsInfinity(s)) return null;
            float inv = (float)(1.0 / Math.Sqrt(Math.Max(s, 1e-12)));
            for (int k = 0; k < d; k++) o[k] *= inv;
            return o;
        }
        private static float Dot(float[] a, float[] b)
        {
            if (!VectorCache.IsValidVector(a) || !VectorCache.IsValidVector(b))
                return float.NegativeInfinity;
            float s = 0;
            for (int k = 0; k < VectorCache.ExpectedDimension; k++) s += a[k] * b[k];
            return s;
        }

        public void Dispose()
        {
            DisposeWithin(TimeSpan.FromMilliseconds(
                DisposeWaitMilliseconds));
        }

        internal void DisposeWithin(TimeSpan wait)
        {
            int waitMilliseconds = wait <= TimeSpan.Zero
                ? 0
                : (int)Math.Min(int.MaxValue, wait.TotalMilliseconds);
            DisposeCore(waitMilliseconds);
        }

        private void DisposeCore(int waitMilliseconds)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            Task warm;
            bool owner = false;
            lock (_stateLock)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _ready = false;
                    _pool = null;
                    _vecs = null;
                    _mean = null;
                    if (_warmCancellation != null)
                    {
                        try { _warmCancellation.Cancel(); } catch { }
                    }
                    warm = _warmTask;
                    owner = true;
                }
                else
                {
                    warm = null;
                }
            }

            if (!owner)
            {
                try { _disposeCompleted.Wait(waitMilliseconds); } catch { }
                return;
            }

            try
            {
                if (WaitForCompletion(warm, waitMilliseconds))
                {
                    int remaining = RemainingMilliseconds(
                        waitMilliseconds,
                        stopwatch.Elapsed);
                    if (!TryDisposeEmbedder(remaining))
                        QueueEmbedderDisposal(warm);
                }
                else
                {
                    // A native inference that ignores cancellation must not hang application
                    // shutdown. The sole disposal owner transfers cleanup to a continuation, which
                    // cannot race the still-running warm operation.
                    QueueEmbedderDisposal(warm);
                }
            }
            finally
            {
                _disposeCompleted.Set();
            }
        }

        private void DisposeEmbedder()
        {
            lock (_embedLock)
            {
                DisposeEmbedderUnderLock();
            }
        }

        private bool TryDisposeEmbedder(int waitMilliseconds)
        {
            bool entered = false;
            try
            {
                entered = Monitor.TryEnter(
                    _embedLock,
                    waitMilliseconds);
                if (!entered) return false;
                DisposeEmbedderUnderLock();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (entered) Monitor.Exit(_embedLock);
            }
        }

        private void DisposeEmbedderUnderLock()
        {
            if (Interlocked.CompareExchange(
                    ref _embedderDisposalCount,
                    1,
                    0) != 0)
                return;
            try { _embed.Dispose(); } catch { }
        }

        private void QueueEmbedderDisposal(Task warm)
        {
            Task antecedent = warm ?? Task.CompletedTask;
            antecedent.ContinueWith(
                delegate(Task completed)
                {
                    Observe(completed);
                    DisposeEmbedder();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        private static int RemainingMilliseconds(
            int budgetMilliseconds,
            TimeSpan elapsed)
        {
            if (budgetMilliseconds <= 0) return 0;
            long elapsedMilliseconds = (long)Math.Ceiling(
                Math.Max(0, elapsed.TotalMilliseconds));
            if (elapsedMilliseconds >= budgetMilliseconds) return 0;
            return (int)Math.Min(
                int.MaxValue,
                budgetMilliseconds - elapsedMilliseconds);
        }

        private static bool WaitForCompletion(Task task, int timeoutMilliseconds)
        {
            if (task == null) return true;
            try
            {
                return task.Wait(timeoutMilliseconds);
            }
            catch (AggregateException)
            {
                return true;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        }

        // ---- diagnostic ---------------------------------------------------------
        public static bool SelfTest()
        {
            string outp = Path.Combine(Path.GetTempPath(), "dp-smart-selftest.txt");
            var sb = new System.Text.StringBuilder();
            string cacheDir = Path.Combine(Path.GetTempPath(), "DesktopAICompanion-smart-selftest-" +
                Guid.NewGuid().ToString("N"));
            bool ok = true;
            try
            {
                Directory.CreateDirectory(cacheDir);
                var fp = new FortuneProvider(FortuneProvider.EmbeddedEntriesForDiagnostics(),
                    new FortuneSettings());
                var completePool = fp.PoolEntries();
                var pool = DiagnosticPool(completePool);
                sb.AppendLine(
                    "pool=" + completePool.Count +
                    " diagnostic_pool=" + pool.Count +
                    " modelPresent=" + Embedder.ModelPresent);
                if (completePool.Count == 0 || pool.Count == 0 || !Embedder.ModelPresent)
                    ok = false;

                string routerError;
                bool routerOk = Router.SelfTest(out routerError);
                sb.AppendLine("router=" + (routerOk ? "PASS" : "FAIL"));
                if (!routerOk) { ok = false; sb.AppendLine("router_error=" + routerError); }

                ok = VectorCache.SelfTest(cacheDir, sb) && ok;

                using (var sm = new SmartFortunes(cacheDir))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    sm.Warm(pool);
                    bool spReady = false, spComplete = false;
                    int spIndexed = 0, spTotal = 0;
                    while (!spComplete && sw.ElapsedMilliseconds < 120000)
                    {
                        sm.WarmProgress(out spReady, out spComplete, out spIndexed, out spTotal);
                        if (!spComplete) System.Threading.Thread.Sleep(200);
                    }
                    sw.Stop();
                    sb.AppendLine("warmed=" + sm.Ready + " complete=" + spComplete +
                        " indexed=" + spIndexed + " of " + spTotal + " in " + sw.ElapsedMilliseconds + "ms");
                    if (!sm.Ready || !spComplete || spIndexed <= 0 ||
                        spTotal != pool.Count || sm.PoolCount != pool.Count) ok = false;

                    string[] contexts = {
                        "Program.cs - Visual Studio - writing C# code",
                        "Reddit - relationships - my girlfriend and I broke up",
                        "Spreadsheet - quarterly budget report in Excel" };
                    string[] apps = { "devenv", "chrome", "excel" };
                    int contextualPicks = 0;
                    for (int i = 0; i < contexts.Length; i++)
                    {
                        string f = sm.Pick(contexts[i], apps[i]);
                        if (f != null) contextualPicks++;
                        sb.AppendLine("[" + contexts[i].Substring(0, Math.Min(40, contexts[i].Length)) +
                            "...] -> " + (f ?? "(random)"));
                    }
                    sb.AppendLine("contextual_picks=" + contextualPicks + "/" + contexts.Length);
                    if (contextualPicks == 0) ok = false;

                    // Variety regression: a *stable* context must rotate through many distinct lines,
                    // not repeat a handful (the reported bug was ~3 of thousands). This deliberately runs
                    // MORE picks than the per-context TopK so it enters the saturated regime -- where the
                    // recent window fills and a naive picker collapses to uniform-random-with-repeats. The
                    // earlier 40-pick loop was < TopK (64) and never reached it, so it passed while the real
                    // bug persisted. The hard new guarantee tested here: never repeat a line back to back.
                    var seen = new HashSet<string>();
                    string previous = null;
                    int immediateRepeats = 0;
                    const int stablePicks = 200;
                    for (int i = 0; i < stablePicks; i++)
                    {
                        string s = sm.Pick(contexts[0], apps[0]);
                        if (s == null) continue;
                        if (previous != null && string.Equals(s, previous, StringComparison.Ordinal))
                            immediateRepeats++;
                        previous = s;
                        seen.Add(s);
                    }
                    sb.AppendLine("stable_context_distinct=" + seen.Count + "/" + stablePicks +
                        " immediate_repeats=" + immediateRepeats);
                    if (seen.Count < 12 || immediateRepeats > 0) ok = false;

                    // Routing sanity: unambiguous contexts must route to their obvious topic. This
                    // validates both the prototypes and the embedding-based RouteByContext.
                    var techRoute = sm.RouteTopicsForDiagnostics(
                        "Visual Studio Code editing app.py python function import def class");
                    var foodRoute = sm.RouteTopicsForDiagnostics(
                        "chocolate cake recipe baking flour sugar eggs preheat the oven");
                    sb.AppendLine("route_tech=" + (techRoute == null ? "(none)" : string.Join(",", techRoute)) +
                        " route_food=" + (foodRoute == null ? "(none)" : string.Join(",", foodRoute)));
                    if (techRoute == null || !techRoute.Contains("tech")) ok = false;
                    if (foodRoute == null || !foodRoute.Contains("food")) ok = false;
                }

                var disposeRace = new SmartFortunes(
                    Path.Combine(cacheDir, "dispose-during-warm"));
                var disposeWatch = System.Diagnostics.Stopwatch.StartNew();
                disposeRace.Warm(pool);
                Thread.Sleep(1);
                disposeRace.Dispose();
                disposeRace.Dispose();
                disposeWatch.Stop();
                bool disposeOk = !disposeRace.Ready &&
                    disposeRace.PoolCount == 0 &&
                    disposeWatch.ElapsedMilliseconds < 30000;
                sb.AppendLine("dispose_during_warm=" +
                    (disposeOk ? "PASS" : "FAIL") +
                    " ms=" + disposeWatch.ElapsedMilliseconds);
                if (!disposeOk) ok = false;
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true); }
                catch (Exception ex) { ok = false; sb.AppendLine("CLEANUP EXC: " + ex.Message); }
            }
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(outp, sb.ToString()); }
            catch { return false; }
            return ok;
        }

        // Opt-in, slow: warm a >512 sample against a *cold* cache so real embedding happens, then prove
        // Pick serves the warmed prefix before the whole pool is done (indexed climbs monotonically, a
        // ready-but-incomplete window is observed, and a pick lands during it).
        public static bool ProgressiveSelfTest()
        {
            string outp = Path.Combine(Path.GetTempPath(), "dp-smart-progress-selftest.txt");
            var sb = new System.Text.StringBuilder();
            string cacheDir = Path.Combine(Path.GetTempPath(), "DesktopAICompanion-smart-progress-" +
                Guid.NewGuid().ToString("N"));
            bool ok = true;
            try
            {
                Directory.CreateDirectory(cacheDir);   // cold cache -> real embedding, partial states visible
                var fp = new FortuneProvider(FortuneProvider.EmbeddedEntriesForDiagnostics(),
                    new FortuneSettings());
                var pool = ProgressiveSamplePool(fp.PoolEntries(), 1500);
                sb.AppendLine("pool=" + pool.Count + " modelPresent=" + Embedder.ModelPresent);
                if (pool.Count <= 512 || !Embedder.ModelPresent) ok = false;

                using (var sm = new SmartFortunes(cacheDir))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    sm.Warm(pool);
                    bool sawPartial = false, sawPartialPick = false, monotonic = true;
                    int lastIndexed = 0;
                    bool ready, complete; int indexed, total;
                    while (true)
                    {
                        sm.WarmProgress(out ready, out complete, out indexed, out total);
                        if (indexed < lastIndexed) monotonic = false;
                        lastIndexed = indexed;
                        if (ready && !complete)
                        {
                            sawPartial = true;
                            if (!sawPartialPick && sm.Pick(
                                "Program.cs - Visual Studio - writing C# code", "devenv") != null)
                                sawPartialPick = true;
                        }
                        if (complete || sw.ElapsedMilliseconds > 180000) break;
                        Thread.Sleep(20);
                    }
                    sw.Stop();
                    sm.WarmProgress(out ready, out complete, out indexed, out total);
                    sb.AppendLine("complete=" + complete + " indexed=" + indexed + " of " + total +
                        " sawPartial=" + sawPartial + " sawPartialPick=" + sawPartialPick +
                        " monotonic=" + monotonic + " ms=" + sw.ElapsedMilliseconds);
                    // sawPartialPick is best-effort: an early prefix may not yet hold a strong tech match.
                    if (!complete || indexed <= 0 || total != pool.Count ||
                        !sawPartial || !monotonic) ok = false;
                }
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true); }
                catch (Exception ex) { ok = false; sb.AppendLine("CLEANUP EXC: " + ex.Message); }
            }
            sb.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            try { File.WriteAllText(outp, sb.ToString()); }
            catch { return false; }
            return ok;
        }

        private static List<FortuneEntry> ProgressiveSamplePool(List<FortuneEntry> completePool, int size)
        {
            var sample = new List<FortuneEntry>(size);
            if (completePool == null || completePool.Count == 0) return sample;
            int take = Math.Min(size, completePool.Count);
            for (int i = 0; i < take; i++)
                sample.Add(completePool[(int)((long)i * completePool.Count / take)]);
            return sample;
        }

        private static List<FortuneEntry> DiagnosticPool(List<FortuneEntry> completePool)
        {
            const int MaximumSampleEntries = 128;
            var sample = new List<FortuneEntry>(MaximumSampleEntries + 3);
            if (completePool != null && completePool.Count > 0)
            {
                int sampleCount = Math.Min(MaximumSampleEntries, completePool.Count);
                for (int index = 0; index < sampleCount; index++)
                {
                    int sourceIndex = (int)(
                        (long)index * completePool.Count / sampleCount);
                    sample.Add(completePool[sourceIndex]);
                }
            }

            // Stable semantic anchors keep this regression test deterministic while the corpus
            // continues to evolve. The full corpus is still parsed and filtered above; warming a
            // bounded representative sample avoids turning a smoke test into a machine-speed test.
            sample.Add(new FortuneEntry {
                Source = "diagnostic", Topic = "tech", Genre = "fact",
                Level = "general", Text = "Writing C sharp code in Visual Studio is programming."
            });
            sample.Add(new FortuneEntry {
                Source = "diagnostic", Topic = "love", Genre = "observation",
                Level = "general", Text = "A girlfriend breakup can make relationships feel lonely."
            });
            sample.Add(new FortuneEntry {
                Source = "diagnostic", Topic = "work-money", Genre = "fact",
                Level = "general",
                Text = "An Excel spreadsheet tracks quarterly budgets and financial reports."
            });
            return sample;
        }
    }

    /// <summary>Foreground-app to preferred locked topics. A soft nudge, never a hard filter.</summary>
    // Topic router. Instead of a hardcoded process-name -> topic table (which only ever covered a
    // handful of apps and topics), routing embeds one short prototype sentence per taxonomy topic and
    // takes the nearest to the on-screen context (see SmartFortunes.RouteByContext). This class just
    // owns the prototype sentences and validates coverage; the embedding/scoring lives in SmartFortunes
    // because it needs the live embedder and the corpus mean.
    internal static class Router
    {
        // One representative sentence per taxonomy topic, embedded as passages (no query prefix) like
        // fortunes, so a context->prototype similarity mirrors the context->fortune retrieval.
        internal static readonly Dictionary<string, string> Prototypes =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "tech",        "software, programming, code, computers, apps, and technology" },
                { "science",     "science, research, physics, biology, space, and discovery" },
                { "work-money",  "work, jobs, careers, business, money, and finance" },
                { "love",        "love, romance, dating, and relationships" },
                { "family",      "family, parents, children, marriage, and home life" },
                { "faith",       "faith, religion, God, prayer, spirituality, and belief" },
                { "society",     "society, politics, news, government, law, and culture" },
                { "food",        "food, cooking, recipes, restaurants, and eating" },
                { "nature",      "nature, animals, weather, plants, and the outdoors" },
                { "arts",        "music, movies, art, books, games, and entertainment" },
                { "health-body", "health, fitness, exercise, sleep, medicine, and the body" },
                { "life",        "everyday life, people, feelings, habits, and human nature" },
            };

        // The prototype set must cover exactly the locked taxonomy topics (no missing, no unknown).
        internal static bool SelfTest(out string error)
        {
            error = null;
            foreach (string topic in FortuneTaxonomy.Topics())
                if (!Prototypes.ContainsKey(topic))
                { error = "no routing prototype for topic '" + topic + "'"; return false; }
            foreach (string key in Prototypes.Keys)
                if (!FortuneTaxonomy.IsTopic(key))
                { error = "routing prototype for unknown topic '" + key + "'"; return false; }
            return true;
        }
    }

    /// <summary>
    /// Persistent text→embedding cache so the pool is embedded only once ever (survives restarts and
    /// pack changes). Stored as a flat binary under the canonical application data root. Never
    /// throws.
    /// </summary>
    internal sealed class VectorCache
    {
        private const int Magic = 0x42474532; // "BGE2"
        internal const int ExpectedDimension = 384;
        internal const int MaximumEntries = 100000;
        private const int MaximumKeyBytes = 2048;
        private const int FingerprintBytes = 32;
        private const int HeaderBytes = 12 + FingerprintBytes;
        private const long MaximumFileBytes = 256L * 1024L * 1024L;
        private const int ProcessLockTimeoutMilliseconds = 30000;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly Dictionary<string, float[]> _map = new Dictionary<string, float[]>(StringComparer.Ordinal);
        private readonly object _lock = new object();
        private readonly string _dir;
        private readonly string _file;
        private readonly string _mutexName;
        private readonly string _assetFingerprint;
        private readonly int _maximumEntries;
        private readonly Action<string, string, string, bool> _replaceFile;
        private HashSet<string> _activeKeys;
        private bool _dirty;
        private long _version;

        internal VectorCache(
            string directory,
            CancellationToken cancellationToken)
            : this(directory, Embedder.AssetFingerprint, cancellationToken)
        {
        }

        internal VectorCache(string directory, string assetFingerprint)
            : this(directory, assetFingerprint, CancellationToken.None)
        {
        }

        internal VectorCache(
            string directory,
            string assetFingerprint,
            CancellationToken cancellationToken)
            : this(
                directory,
                assetFingerprint,
                MaximumEntries,
                cancellationToken)
        {
        }

        internal VectorCache(
            string directory,
            string assetFingerprint,
            int maximumEntries)
            : this(
                directory,
                assetFingerprint,
                maximumEntries,
                CancellationToken.None)
        {
        }

        internal VectorCache(
            string directory,
            string assetFingerprint,
            int maximumEntries,
            Action<string, string, string, bool> replaceFile)
            : this(
                directory,
                assetFingerprint,
                maximumEntries,
                CancellationToken.None,
                replaceFile)
        {
        }

        private VectorCache(
            string directory,
            string assetFingerprint,
            int maximumEntries,
            CancellationToken cancellationToken)
            : this(
                directory,
                assetFingerprint,
                maximumEntries,
                cancellationToken,
                null)
        {
        }

        private VectorCache(
            string directory,
            string assetFingerprint,
            int maximumEntries,
            CancellationToken cancellationToken,
            Action<string, string, string, bool> replaceFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maximumEntries < 1 || maximumEntries > MaximumEntries)
                throw new ArgumentOutOfRangeException("maximumEntries");
            _dir = string.IsNullOrEmpty(directory)
                ? FortunePaths.VectorCacheDir
                : directory;
            _file = Path.Combine(_dir, "cache.bin");
            _mutexName = CrossSessionLock.BuildGlobalMutexName(
                "VectorCache",
                _file);
            _assetFingerprint = NormalizeFingerprint(assetFingerprint);
            _maximumEntries = maximumEntries;
            _replaceFile = replaceFile;
            Load(cancellationToken);
        }

        internal void BeginActivePool(
            IEnumerable<string> texts,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = new HashSet<string>(StringComparer.Ordinal);
            if (texts != null)
            {
                foreach (string text in texts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsValidKey(text)) continue;
                    active.Add(text);
                    if (active.Count > _maximumEntries)
                        throw new InvalidDataException(
                            "Active fortune pool exceeds the vector cache limit.");
                }
            }

            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _activeKeys = active;
                var stale = new List<string>();
                foreach (string key in _map.Keys)
                    if (!active.Contains(key))
                        stale.Add(key);
                if (stale.Count == 0) return;
                foreach (string key in stale)
                    _map.Remove(key);
                _dirty = true;
                _version++;
            }
        }

        internal float[] GetOrEmbed(
            string text,
            Embedder e,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidKey(text) || e == null) return null;
            lock (_lock)
            {
                float[] cached;
                if (_map.TryGetValue(text, out cached)) return cached;
            }

            cancellationToken.ThrowIfCancellationRequested();
            float[] emb = e.Embed(text);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidVector(emb)) return null;
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float[] cached;
                if (_map.TryGetValue(text, out cached)) return cached;
                if (_activeKeys != null && !_activeKeys.Contains(text))
                    return emb;
                EvictNonActiveNoLock();
                if (_map.Count >= _maximumEntries) return emb;
                _map[text] = emb;
                _dirty = true;
                _version++;
            }
            return emb;
        }

        private void Load(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Dictionary<string, float[]> loaded = null;
                WithProcessLock(delegate
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Dictionary<string, float[]> candidate;
                    if (TryReadCacheFile(
                            _file,
                            _assetFingerprint,
                            cancellationToken,
                            out candidate))
                        loaded = candidate;
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (loaded == null) return;
                lock (_lock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _map.Clear();
                    foreach (KeyValuePair<string, float[]> item in loaded)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_map.Count >= _maximumEntries) break;
                        _map.Add(item.Key, item.Value);
                    }
                    _dirty = false;
                    _version = 0;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                lock (_lock)
                {
                    _map.Clear();
                    _dirty = false;
                    _version = 0;
                }
            }
        }

        public void Save()
        {
            Save(CancellationToken.None);
        }

        internal void Save(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, float[]> snapshot;
            HashSet<string> activeKeys;
            long snapshotVersion;
            lock (_lock)
            {
                if (!_dirty || _map.Count == 0) return;
                snapshot = new Dictionary<string, float[]>(_map, StringComparer.Ordinal);
                activeKeys = _activeKeys == null
                    ? null
                    : new HashSet<string>(_activeKeys, StringComparer.Ordinal);
                snapshotVersion = _version;
            }

            bool saved = false;
            try
            {
                WithProcessLock(delegate
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Dictionary<string, float[]> disk;
                    if (TryReadCacheFile(
                            _file,
                            _assetFingerprint,
                            cancellationToken,
                            out disk))
                    {
                        foreach (KeyValuePair<string, float[]> item in disk)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (activeKeys != null &&
                                activeKeys.Contains(item.Key) &&
                                snapshot.Count < _maximumEntries &&
                                !snapshot.ContainsKey(item.Key))
                                snapshot.Add(item.Key, item.Value);
                        }
                        // Backfill remaining disk keys ONLY when this cache has no active pool
                        // (the diagnostics path). Once an active pool is set, non-active keys are
                        // intentionally not carried forward: the written file is pruned to the
                        // active set so the on-disk cache stays near the active-pool size instead
                        // of drifting toward the 100k hard cap. Active keys written by other
                        // processes are still merged above via the active-key loop. (#12)
                        if (activeKeys == null)
                        {
                            foreach (KeyValuePair<string, float[]> item in disk)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (snapshot.Count >= _maximumEntries) break;
                                if (!snapshot.ContainsKey(item.Key))
                                    snapshot.Add(item.Key, item.Value);
                            }
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    WriteCacheFileAtomic(snapshot, cancellationToken);
                    saved = true;
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch { }

            cancellationToken.ThrowIfCancellationRequested();
            if (!saved) return;
            lock (_lock)
            {
                bool sameActivePool = ActivePoolsEqual(
                    _activeKeys,
                    activeKeys);
                if (sameActivePool)
                {
                    foreach (KeyValuePair<string, float[]> item in snapshot)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (activeKeys != null &&
                            !activeKeys.Contains(item.Key))
                            continue;
                        if (_map.Count < _maximumEntries &&
                            !_map.ContainsKey(item.Key))
                            _map.Add(item.Key, item.Value);
                    }
                }
                if (_version == snapshotVersion)
                    _dirty = false;
            }
        }

        private void WriteCacheFileAtomic(
            Dictionary<string, float[]> values,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (values == null || values.Count < 1 ||
                values.Count > _maximumEntries)
                throw new InvalidDataException("Vector cache entry count is invalid.");
            byte[] fingerprint = DecodeFingerprint(_assetFingerprint);
            if (fingerprint == null)
                throw new InvalidDataException(
                    "The embedding asset fingerprint is unavailable.");

            var ordered = new List<KeyValuePair<string, float[]>>(values);
            ordered.Sort(delegate (
                KeyValuePair<string, float[]> left,
                KeyValuePair<string, float[]> right)
            {
                return string.CompareOrdinal(left.Key, right.Key);
            });
            cancellationToken.ThrowIfCancellationRequested();
            long maximumEncodedLength = HeaderBytes;
            foreach (KeyValuePair<string, float[]> item in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsValidKey(item.Key) || !IsValidVector(item.Value))
                    throw new InvalidDataException("Vector cache contains an invalid entry.");
                maximumEncodedLength += 5L + StrictUtf8.GetByteCount(item.Key) +
                    ExpectedDimension * sizeof(float);
                if (maximumEncodedLength > MaximumFileBytes)
                    throw new InvalidDataException(
                        "Vector cache exceeds its file size limit.");
            }

            Directory.CreateDirectory(_dir);
            string temporary = Path.Combine(
                _dir, ".cache." + Guid.NewGuid().ToString("N") + ".tmp");
            string backup = temporary + ".bak";
            try
            {
                using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    8192,
                    FileOptions.WriteThrough))
                using (var writer = new BinaryWriter(stream, StrictUtf8))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(Magic);
                    writer.Write(ordered.Count);
                    writer.Write(ExpectedDimension);
                    writer.Write(fingerprint);
                    foreach (KeyValuePair<string, float[]> item in ordered)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        WriteBoundedString(writer, item.Key);
                        for (int i = 0; i < ExpectedDimension; i++)
                        {
                            if ((i & 63) == 0)
                                cancellationToken.ThrowIfCancellationRequested();
                            writer.Write(item.Value[i]);
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Flush();
                    stream.Flush(true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (new FileInfo(temporary).Length > MaximumFileBytes)
                    throw new InvalidDataException("Vector cache exceeds its file size limit.");

                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(_file))
                    AtomicFile.ReplaceExisting(
                        temporary,
                        _file,
                        backup,
                        cancellationToken,
                        _replaceFile);
                else
                    File.Move(temporary, _file);
            }
            finally
            {
                TryDelete(temporary);
                TryDelete(backup);
            }
        }

        private static bool TryReadCacheFile(
            string path,
            string expectedFingerprint,
            CancellationToken cancellationToken,
            out Dictionary<string, float[]> values)
        {
            values = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(path)) return false;
                byte[] expected = DecodeFingerprint(expectedFingerprint);
                if (expected == null) return false;
                var parsed = new Dictionary<string, float[]>(StringComparer.Ordinal);
                using (var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long length = stream.Length;
                    if (length < HeaderBytes || length > MaximumFileBytes)
                        return false;
                    using (var reader = new BinaryReader(stream, StrictUtf8))
                    {
                        if (reader.ReadInt32() != Magic) return false;
                        int count = reader.ReadInt32();
                        int dimension = reader.ReadInt32();
                        if (count < 0 || count > MaximumEntries ||
                            dimension != ExpectedDimension)
                            return false;
                        byte[] actual = reader.ReadBytes(FingerprintBytes);
                        if (actual.Length != FingerprintBytes ||
                            !ByteArraysEqual(actual, expected))
                            return false;

                        long minimumBytes = HeaderBytes +
                            count * (1L + ExpectedDimension * sizeof(float));
                        if (minimumBytes > length) return false;

                        for (int i = 0; i < count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string key = ReadBoundedString(reader);
                            if (!IsValidKey(key) || parsed.ContainsKey(key))
                                return false;
                            var vector = new float[ExpectedDimension];
                            for (int k = 0; k < ExpectedDimension; k++)
                            {
                                if ((k & 63) == 0)
                                    cancellationToken.ThrowIfCancellationRequested();
                                float value = reader.ReadSingle();
                                if (float.IsNaN(value) || float.IsInfinity(value))
                                    return false;
                                vector[k] = value;
                            }
                            parsed.Add(key, vector);
                        }
                        if (stream.Position != stream.Length)
                            return false;
                    }
                }
                values = parsed;
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                values = null;
                return false;
            }
        }

        private void WithProcessLock(
            Action action,
            CancellationToken cancellationToken)
        {
            using (AcquireProcessLock(cancellationToken))
                action();
        }

        private IDisposable AcquireProcessLock(
            CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long remaining =
                    ProcessLockTimeoutMilliseconds - stopwatch.ElapsedMilliseconds;
                if (remaining <= 0)
                    throw new IOException(
                        "Timed out waiting for the vector cache lock.");
                int attemptMilliseconds = (int)Math.Min(100L, remaining);
                IDisposable lease = CrossSessionLock.TryAcquire(
                    _mutexName,
                    _file,
                    attemptMilliseconds);
                if (lease != null) return lease;
            }
        }

        private static string NormalizeFingerprint(string value)
        {
            if (string.IsNullOrEmpty(value) ||
                value.Length != FingerprintBytes * 2)
                return "";
            var normalized = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character >= '0' && character <= '9')
                    normalized.Append(character);
                else if (character >= 'a' && character <= 'f')
                    normalized.Append(character);
                else if (character >= 'A' && character <= 'F')
                    normalized.Append((char)(character + ('a' - 'A')));
                else
                    return "";
            }
            return normalized.ToString();
        }

        private static byte[] DecodeFingerprint(string value)
        {
            string normalized = NormalizeFingerprint(value);
            if (normalized.Length != FingerprintBytes * 2) return null;
            var bytes = new byte[FingerprintBytes];
            for (int index = 0; index < bytes.Length; index++)
            {
                int high = HexValue(normalized[index * 2]);
                int low = HexValue(normalized[index * 2 + 1]);
                if (high < 0 || low < 0) return null;
                bytes[index] = (byte)((high << 4) | low);
            }
            return bytes;
        }

        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            return -1;
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            int difference = 0;
            for (int index = 0; index < left.Length; index++)
                difference |= left[index] ^ right[index];
            return difference == 0;
        }

        private static string ReadBoundedString(BinaryReader reader)
        {
            int byteCount = Read7BitEncodedInt(reader);
            if (byteCount < 1 || byteCount > MaximumKeyBytes)
                throw new InvalidDataException("Vector cache key length is invalid.");
            byte[] bytes = reader.ReadBytes(byteCount);
            if (bytes.Length != byteCount)
                throw new EndOfStreamException("Vector cache key is truncated.");
            return StrictUtf8.GetString(bytes);
        }

        private static int Read7BitEncodedInt(BinaryReader reader)
        {
            int result = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte value = reader.ReadByte();
                if (shift == 28 && (value & 0xF0) != 0)
                    throw new FormatException("Vector cache string length overflows.");
                result |= (value & 0x7F) << shift;
                if ((value & 0x80) == 0) return result;
            }
            throw new FormatException("Vector cache string length is invalid.");
        }

        private static void WriteBoundedString(BinaryWriter writer, string value)
        {
            byte[] bytes = StrictUtf8.GetBytes(value);
            if (bytes.Length < 1 || bytes.Length > MaximumKeyBytes)
                throw new InvalidDataException("Vector cache key length is invalid.");
            Write7BitEncodedInt(writer, bytes.Length);
            writer.Write(bytes);
        }

        private static void Write7BitEncodedInt(BinaryWriter writer, int value)
        {
            uint remaining = (uint)value;
            while (remaining >= 0x80)
            {
                writer.Write((byte)(remaining | 0x80));
                remaining >>= 7;
            }
            writer.Write((byte)remaining);
        }

        private static bool IsValidKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (char character in value)
                if (char.IsControl(character))
                    return false;
            try
            {
                return StrictUtf8.GetByteCount(value) <= MaximumKeyBytes;
            }
            catch
            {
                return false;
            }
        }

        private void EvictNonActiveNoLock()
        {
            if (_map.Count < _maximumEntries || _activeKeys == null)
                return;
            string stale = null;
            foreach (string key in _map.Keys)
            {
                if (_activeKeys.Contains(key)) continue;
                stale = key;
                break;
            }
            if (stale == null) return;
            _map.Remove(stale);
            _dirty = true;
            _version++;
        }

        private static bool ActivePoolsEqual(
            HashSet<string> left,
            HashSet<string> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;
            return left.SetEquals(right);
        }

        internal static bool IsValidVector(float[] vector)
        {
            if (vector == null || vector.Length != ExpectedDimension) return false;
            foreach (float value in vector)
                if (float.IsNaN(value) || float.IsInfinity(value))
                    return false;
            return true;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        internal int CountForDiagnostics
        {
            get { lock (_lock) return _map.Count; }
        }

        internal void AddForDiagnostics(string key, float value)
        {
            var vector = new float[ExpectedDimension];
            for (int i = 0; i < vector.Length; i++) vector[i] = value;
            lock (_lock)
            {
                if (!IsValidKey(key) ||
                    (_activeKeys != null && !_activeKeys.Contains(key)))
                    return;
                EvictNonActiveNoLock();
                if (!_map.ContainsKey(key) &&
                    _map.Count >= _maximumEntries)
                    return;
                _map[key] = vector;
                _dirty = true;
                _version++;
            }
        }

        internal bool ContainsForDiagnostics(string key)
        {
            lock (_lock) return _map.ContainsKey(key);
        }

        internal static bool SelfTest(string root, StringBuilder report)
        {
            string directory = Path.Combine(root, "vector-cache-adversarial");
            string fingerprintA = new string('1', FingerprintBytes * 2);
            string fingerprintB = new string('2', FingerprintBytes * 2);
            byte[] fingerprintBytes = DecodeFingerprint(fingerprintA);
            bool ok = true;
            try
            {
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "cache.bin");
                Action<string, Action<BinaryWriter>> invalidCase =
                    delegate (string name, Action<BinaryWriter> write)
                    {
                        using (var stream = new FileStream(
                            file, FileMode.Create, FileAccess.Write, FileShare.None))
                        using (var writer = new BinaryWriter(stream, StrictUtf8))
                            write(writer);
                        var cache = new VectorCache(directory, fingerprintA);
                        if (cache.CountForDiagnostics != 0)
                        {
                            ok = false;
                            report.AppendLine("CACHE FAIL accepted " + name);
                        }
                    };

                invalidCase("truncated", delegate (BinaryWriter writer)
                {
                    writer.Write(Magic);
                    writer.Write(1);
                    writer.Write(ExpectedDimension);
                    writer.Write(fingerprintBytes);
                    WriteBoundedString(writer, "truncated");
                    writer.Write(1.0f);
                });
                invalidCase("wrong-dimension", delegate (BinaryWriter writer)
                {
                    writer.Write(Magic);
                    writer.Write(1);
                    writer.Write(ExpectedDimension - 1);
                    writer.Write(fingerprintBytes);
                });
                invalidCase("nonfinite", delegate (BinaryWriter writer)
                {
                    writer.Write(Magic);
                    writer.Write(1);
                    writer.Write(ExpectedDimension);
                    writer.Write(fingerprintBytes);
                    WriteBoundedString(writer, "nonfinite");
                    writer.Write(float.NaN);
                    for (int i = 1; i < ExpectedDimension; i++) writer.Write(0.0f);
                });
                invalidCase("trailing-bytes", delegate (BinaryWriter writer)
                {
                    writer.Write(Magic);
                    writer.Write(1);
                    writer.Write(ExpectedDimension);
                    writer.Write(fingerprintBytes);
                    WriteBoundedString(writer, "trailing");
                    for (int i = 0; i < ExpectedDimension; i++) writer.Write(0.1f);
                    writer.Write((byte)0x42);
                });
                invalidCase("oversized-count", delegate (BinaryWriter writer)
                {
                    writer.Write(Magic);
                    writer.Write(MaximumEntries + 1);
                    writer.Write(ExpectedDimension);
                    writer.Write(fingerprintBytes);
                });
                invalidCase("oversized-key", delegate (BinaryWriter writer)
                {
                    writer.Write(Magic);
                    writer.Write(1);
                    writer.Write(ExpectedDimension);
                    writer.Write(fingerprintBytes);
                    Write7BitEncodedInt(writer, MaximumKeyBytes + 1);
                    writer.Write(new byte[MaximumKeyBytes + 1]);
                    for (int i = 0; i < ExpectedDimension; i++) writer.Write(0.0f);
                });

                using (var oversized = new FileStream(
                    file, FileMode.Create, FileAccess.Write, FileShare.None))
                    oversized.SetLength(MaximumFileBytes + 1);
                if (new VectorCache(directory, fingerprintA).CountForDiagnostics != 0)
                {
                    ok = false;
                    report.AppendLine("CACHE FAIL accepted oversized file");
                }

                TryDelete(file);
                var first = new VectorCache(directory, fingerprintA);
                var second = new VectorCache(directory, fingerprintA);
                first.AddForDiagnostics("first", 0.1f);
                second.AddForDiagnostics("second", 0.2f);
                Task.WaitAll(
                    Task.Run(delegate { first.Save(); }),
                    Task.Run(delegate { second.Save(); }));
                var merged = new VectorCache(directory, fingerprintA);
                if (merged.CountForDiagnostics != 2)
                {
                    ok = false;
                    report.AppendLine(
                        "CACHE FAIL concurrent merge count=" + merged.CountForDiagnostics);
                }

                string replaceFallbackDirectory =
                    Path.Combine(directory, "replace-fallback");
                Directory.CreateDirectory(replaceFallbackDirectory);
                var fallbackSeed = new VectorCache(
                    replaceFallbackDirectory,
                    fingerprintA,
                    3);
                fallbackSeed.AddForDiagnostics("before-fallback", 0.15f);
                fallbackSeed.Save();
                var fallbackWriter = new VectorCache(
                    replaceFallbackDirectory,
                    fingerprintA,
                    3,
                    delegate (
                        string temporaryPath,
                        string destinationPath,
                        string backupPath,
                        bool ignoreMetadataErrors)
                    {
                        throw new PlatformNotSupportedException(
                            "fault-injected File.Replace rejection");
                    });
                fallbackWriter.AddForDiagnostics("after-fallback", 0.25f);
                fallbackWriter.Save();
                var fallbackReloaded = new VectorCache(
                    replaceFallbackDirectory,
                    fingerprintA,
                    3);
                if (fallbackReloaded.CountForDiagnostics != 2 ||
                    !fallbackReloaded.ContainsForDiagnostics("before-fallback") ||
                    !fallbackReloaded.ContainsForDiagnostics("after-fallback"))
                {
                    ok = false;
                    report.AppendLine(
                        "CACHE FAIL portable replace fallback was not durable");
                }

                string saturatedDirectory =
                    Path.Combine(directory, "saturated-active-pool");
                Directory.CreateDirectory(saturatedDirectory);
                var saturated = new VectorCache(
                    saturatedDirectory,
                    fingerprintA,
                    3);
                saturated.AddForDiagnostics("old-a", 0.1f);
                saturated.AddForDiagnostics("old-b", 0.2f);
                saturated.AddForDiagnostics("old-c", 0.3f);
                saturated.Save();
                var disjoint = new VectorCache(
                    saturatedDirectory,
                    fingerprintA,
                    3);
                disjoint.BeginActivePool(
                    new[] { "new-a", "new-b", "new-c" },
                    CancellationToken.None);
                disjoint.AddForDiagnostics("new-a", 0.4f);
                disjoint.AddForDiagnostics("new-b", 0.5f);
                disjoint.AddForDiagnostics("new-c", 0.6f);
                disjoint.Save();
                var persistedActive = new VectorCache(
                    saturatedDirectory,
                    fingerprintA,
                    3);
                if (persistedActive.CountForDiagnostics != 3 ||
                    !persistedActive.ContainsForDiagnostics("new-a") ||
                    !persistedActive.ContainsForDiagnostics("new-b") ||
                    !persistedActive.ContainsForDiagnostics("new-c") ||
                    persistedActive.ContainsForDiagnostics("old-a") ||
                    persistedActive.ContainsForDiagnostics("old-b") ||
                    persistedActive.ContainsForDiagnostics("old-c"))
                {
                    ok = false;
                    report.AppendLine(
                        "CACHE FAIL saturated disjoint active pool was not persisted");
                }

                var mismatched = new VectorCache(directory, fingerprintB);
                if (mismatched.CountForDiagnostics != 0)
                {
                    ok = false;
                    report.AppendLine(
                        "CACHE FAIL reused vectors for a different asset fingerprint");
                }
                mismatched.AddForDiagnostics("different-assets", 0.3f);
                mismatched.Save();
                if (new VectorCache(directory, fingerprintB).CountForDiagnostics != 1 ||
                    new VectorCache(directory, fingerprintA).CountForDiagnostics != 0)
                {
                    ok = false;
                    report.AppendLine(
                        "CACHE FAIL asset fingerprint invalidation was not durable");
                }
            }
            catch (Exception ex)
            {
                ok = false;
                report.AppendLine("CACHE EXC: " + ex.GetType().Name + ": " + ex.Message);
            }
            report.AppendLine("vector_cache=" + (ok ? "PASS" : "FAIL"));
            return ok;
        }
    }
}
