using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace DesktopAICompanion.ModuleKit
{
    /// <summary>
    /// Coordinates durable per-user files across console and RDP sessions. A Global named mutex with a
    /// current-user ACL provides the normal fast path, while a same-directory file lease is always held as
    /// the cross-session fail-safe when the Global namespace is restricted.
    ///
    /// Lifted from the app's own AppSettingsStore, which two modules had already copied verbatim; it lives
    /// here so the next one does not make a third copy. Use it around any file a module must not corrupt
    /// when a second session (or a second instance) writes at the same time.
    /// </summary>
    public static class CrossSessionLock
    {
        private const int RetryMilliseconds = 25;

        /// <summary>A stable Global mutex name for one file, scoped to the current user so two accounts on
        /// the same machine never contend. Category separates unrelated locks (e.g. "settings", "cache").</summary>
        public static string BuildGlobalMutexName(string category, string path)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("A lock category is required.", "category");
            string normalized = Path.GetFullPath(path).ToUpperInvariant();
            string user = CurrentUserSid();
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(user + "\n" + normalized));
                var suffix = new StringBuilder(32);
                for (int index = 0; index < 16; index++)
                    suffix.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
                return @"Global\DesktopAICompanion." + category + "." + suffix;
            }
        }

        /// <summary>Acquire or throw. Dispose the returned lease to release.</summary>
        public static IDisposable Acquire(string mutexName, string dataPath, int timeoutMilliseconds, string description)
        {
            IDisposable lease = TryAcquire(mutexName, dataPath, timeoutMilliseconds);
            if (lease == null)
                throw new IOException(
                    "Timed out waiting for the " +
                    (string.IsNullOrWhiteSpace(description) ? "application data" : description) +
                    " lock.");
            return lease;
        }

        /// <summary>Acquire, or return null on timeout / an unusable name. Dispose to release.</summary>
        public static IDisposable TryAcquire(string mutexName, string dataPath, int timeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(mutexName) ||
                !mutexName.StartsWith(@"Global\", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(dataPath) ||
                timeoutMilliseconds < 0)
                return null;

            var stopwatch = Stopwatch.StartNew();
            bool mutexAvailable;
            IDisposable mutexLease = TryAcquireGlobalMutex(mutexName, timeoutMilliseconds, out mutexAvailable);
            if (mutexAvailable && mutexLease == null)
                return null;

            int remaining = RemainingMilliseconds(timeoutMilliseconds, stopwatch.ElapsedMilliseconds);
            IDisposable fileLease = TryAcquireFileLease(dataPath + ".lock", remaining);
            if (fileLease == null)
            {
                if (mutexLease != null) mutexLease.Dispose();
                return null;
            }
            return new CompositeLease(fileLease, mutexLease);
        }

        private static IDisposable TryAcquireGlobalMutex(string name, int timeoutMilliseconds, out bool available)
        {
            available = false;
            Mutex mutex = null;
            try
            {
                bool created;
                try
                {
                    // net10: the net48 `new Mutex(bool,string,out bool,MutexSecurity)` overload moved
                    // to MutexAcl.Create (System.Threading), preserving the current-user ACL intent.
                    mutex = MutexAcl.Create(false, name, out created, CurrentUserMutexSecurity());
                }
                catch (UnauthorizedAccessException)
                {
                    mutex = MutexAcl.OpenExisting(name, MutexRights.Synchronize | MutexRights.Modify);
                }
                available = true;
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(timeoutMilliseconds);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                if (!acquired)
                {
                    mutex.Dispose();
                    return null;
                }
                return new MutexLease(mutex);
            }
            catch (Exception ex)
            {
                if (mutex != null) mutex.Dispose();
                if (ex is OutOfMemoryException) throw;
                available = false;
                return null;
            }
        }

        private static IDisposable TryAcquireFileLease(string lockPath, int timeoutMilliseconds)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    string directory = Path.GetDirectoryName(Path.GetFullPath(lockPath));
                    if (string.IsNullOrEmpty(directory)) return null;
                    Directory.CreateDirectory(directory);
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None, 1, FileOptions.None);
                }
                catch (IOException)
                {
                    if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds) return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
                Thread.Sleep(Math.Min(RetryMilliseconds,
                    Math.Max(1, RemainingMilliseconds(timeoutMilliseconds, stopwatch.ElapsedMilliseconds))));
            }
        }

        private static int RemainingMilliseconds(int timeout, long elapsed)
        {
            if (elapsed >= timeout) return 0;
            return (int)Math.Min(int.MaxValue, timeout - elapsed);
        }

        private static MutexSecurity CurrentUserMutexSecurity()
        {
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            if (user == null)
                throw new UnauthorizedAccessException("The current Windows user has no security identifier.");
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(
                user, MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
            return security;
        }

        private static string CurrentUserSid()
        {
            try
            {
                SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
                return user == null ? "" : user.Value;
            }
            catch
            {
                return "";
            }
        }

        private sealed class MutexLease : IDisposable
        {
            private Mutex _mutex;

            public MutexLease(Mutex mutex) { _mutex = mutex; }

            public void Dispose()
            {
                Mutex mutex = Interlocked.Exchange(ref _mutex, null);
                if (mutex == null) return;
                try { mutex.ReleaseMutex(); }
                finally { mutex.Dispose(); }
            }
        }

        private sealed class CompositeLease : IDisposable
        {
            private IDisposable _fileLease;
            private IDisposable _mutexLease;

            public CompositeLease(IDisposable fileLease, IDisposable mutexLease)
            {
                _fileLease = fileLease;
                _mutexLease = mutexLease;
            }

            public void Dispose()
            {
                IDisposable fileLease = Interlocked.Exchange(ref _fileLease, null);
                IDisposable mutexLease = Interlocked.Exchange(ref _mutexLease, null);
                try
                {
                    if (fileLease != null) fileLease.Dispose();
                }
                finally
                {
                    if (mutexLease != null) mutexLease.Dispose();
                }
            }
        }
    }
}
