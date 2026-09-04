using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using DesktopAICompanion.ModuleKit;   // AtomicFile / CrossSessionLock / UnicodeTextProgress

namespace DesktopAICompanion.Ai
{
    internal sealed class FortuneImportItemResult
    {
        public string SourcePath;
        public string DestinationPath;
        public bool Imported;
        public string Error;
    }

    internal sealed class FortuneImportBatchResult
    {
        private readonly List<FortuneImportItemResult> items =
            new List<FortuneImportItemResult>();

        public IList<FortuneImportItemResult> Items { get { return items; } }

        public int ImportedCount
        {
            get
            {
                int count = 0;
                foreach (FortuneImportItemResult item in items)
                    if (item.Imported) count++;
                return count;
            }
        }

        public int RejectedCount { get { return items.Count - ImportedCount; } }

        internal void Add(FortuneImportItemResult item)
        {
            if (item != null) items.Add(item);
        }
    }

    /// <summary>
    /// Bounded, strict, per-file atomic importer for user fortune packs. All source reads and
    /// validation are intended to run on a worker; the UI remains responsible for explicit
    /// overwrite consent.
    /// </summary>
    internal static class FortuneFileImporter
    {
        private const int CopyBufferBytes = 64 * 1024;
        private const string ImportLockName = ".desktopPet-fortune-import.lock";
        private const string StagingLeafPrefix = ".s";
        private const string BackupLeafPrefix = ".b";
        private const int InternalIdentifierCharacters = 22;
        private const int InternalFileAllocationAttempts = 8;
        private const int ClassicMaxPathCharacters = 259;

        private sealed class ImportLimits
        {
            public int Files;
            public int FileBytes;
            public long TotalBytes;
            public long Entries;
        }

        private sealed class ExistingFile
        {
            public long Bytes;
            public int Entries;
            public long LastWriteUtcTicks;
        }

        private sealed class DirectoryState
        {
            public readonly Dictionary<string, ExistingFile> Files =
                new Dictionary<string, ExistingFile>(
                    StringComparer.OrdinalIgnoreCase);
            public long TotalBytes;
            public long TotalEntries;
        }

        private sealed class StagedImport
        {
            public FortuneImportItemResult Result;
            public string TemporaryPath;
            public bool OverwriteApproved;
            public ExistingFile Existing;
            public long Bytes;
            public int Entries;
        }

        private sealed class CommittedImport
        {
            public StagedImport Staged;
            public string BackupPath;
            public bool CreatedNew;
        }

        private static readonly ImportLimits RuntimeLimits = new ImportLimits {
            Files = FortunePackLoadPolicy.MaximumFiles,
            FileBytes = FortunePackLoadPolicy.MaximumFileBytes,
            TotalBytes = FortunePackLoadPolicy.MaximumTotalBytes,
            Entries = FortunePackLoadPolicy.MaximumEntries
        };

        internal static FortuneImportBatchResult Import(
            IEnumerable<string> sourcePaths,
            string destinationDirectory,
            ISet<string> approvedOverwriteFileNames,
            CancellationToken cancellationToken)
        {
            return ImportCore(
                sourcePaths,
                destinationDirectory,
                approvedOverwriteFileNames,
                cancellationToken,
                RuntimeLimits,
                OpenSourceFile);
        }

        private static FortuneImportBatchResult ImportCore(
            IEnumerable<string> sourcePaths,
            string destinationDirectory,
            ISet<string> approvedOverwriteFileNames,
            CancellationToken cancellationToken,
            ImportLimits limits,
            Func<string, Stream> openSource)
        {
            return ImportCore(
                sourcePaths,
                destinationDirectory,
                approvedOverwriteFileNames,
                cancellationToken,
                limits,
                openSource,
                null);
        }

        private static FortuneImportBatchResult ImportCore(
            IEnumerable<string> sourcePaths,
            string destinationDirectory,
            ISet<string> approvedOverwriteFileNames,
            CancellationToken cancellationToken,
            ImportLimits limits,
            Func<string, Stream> openSource,
            Action<string, string, string, bool> replaceFile)
        {
            if (sourcePaths == null) throw new ArgumentNullException("sourcePaths");
            if (string.IsNullOrWhiteSpace(destinationDirectory))
                throw new ArgumentException(
                    "A destination directory is required.",
                    "destinationDirectory");
            if (limits == null) throw new ArgumentNullException("limits");
            if (openSource == null) throw new ArgumentNullException("openSource");

            string destinationRoot = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(destinationRoot);
            string lockPath = Path.Combine(destinationRoot, ImportLockName);
            var result = new FortuneImportBatchResult();
            var staged = new List<StagedImport>();

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var importLock = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose))
                {
                    DirectoryState baseline = ReadDirectoryState(
                        destinationRoot,
                        limits,
                        cancellationToken);
                    var seenNames = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);

                    foreach (string sourcePath in sourcePaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var item = new FortuneImportItemResult {
                            SourcePath = sourcePath
                        };
                        result.Add(item);

                        string sourceFullPath;
                        string fileName;
                        try
                        {
                            sourceFullPath = Path.GetFullPath(sourcePath);
                            fileName = Path.GetFileName(sourceFullPath);
                        }
                        catch (Exception ex)
                        {
                            item.Error = "Invalid source path: " + ex.Message;
                            continue;
                        }
                        if (!string.Equals(
                                Path.GetExtension(fileName),
                                ".txt",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            item.Error = "Only .txt fortune files can be imported.";
                            continue;
                        }
                        if (!seenNames.Add(fileName))
                        {
                            item.Error =
                                "Another selected file has the same destination name.";
                            continue;
                        }

                        string destinationPath = Path.Combine(
                            destinationRoot,
                            fileName);
                        item.DestinationPath = destinationPath;
                        if (string.Equals(
                                sourceFullPath,
                                destinationPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            item.Error =
                                "The selected file is already in the fortunes directory.";
                            continue;
                        }

                        ExistingFile existing;
                        bool destinationExists = baseline.Files.TryGetValue(
                            fileName,
                            out existing);
                        bool overwriteApproved =
                            approvedOverwriteFileNames != null &&
                            approvedOverwriteFileNames.Contains(fileName);
                        if (destinationExists && !overwriteApproved)
                        {
                            item.Error =
                                "A file with this name already exists; overwrite was not approved.";
                            continue;
                        }
                        int minimumCandidateFiles =
                            baseline.Files.Count + (existing == null ? 1 : 0);
                        if (minimumCandidateFiles > limits.Files)
                        {
                            item.Error =
                                "Rejected by runtime limits: file count exceeds " +
                                limits.Files + ".";
                            continue;
                        }

                        string temporaryPath = null;
                        byte[] bytes;
                        long copiedBytes;
                        try
                        {
                            using (Stream source = openSource(sourceFullPath))
                            using (var temporary = CreateUniqueStagingFile(
                                destinationRoot,
                                out temporaryPath))
                            using (var memory = new MemoryStream())
                            {
                                copiedBytes = CopyBounded(
                                    source,
                                    temporary,
                                    memory,
                                    limits.FileBytes,
                                    cancellationToken);
                                temporary.Flush(true);
                                bytes = memory.ToArray();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            TryDeleteFile(temporaryPath);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            TryDeleteFile(temporaryPath);
                            item.Error = "Could not stage the source: " + ex.Message;
                            continue;
                        }

                        int maximumFileEntries = (int)Math.Min(
                            int.MaxValue,
                            Math.Max(0L, limits.Entries));
                        int entryCount;
                        string validationError;
                        if (!FortuneProvider.TryValidateCustomPackBytes(
                                bytes,
                                Path.GetFileNameWithoutExtension(fileName),
                                maximumFileEntries,
                                out entryCount,
                                out validationError))
                        {
                            TryDeleteFile(temporaryPath);
                            item.Error = "Rejected content: " + validationError;
                            continue;
                        }

                        staged.Add(new StagedImport {
                            Result = item,
                            TemporaryPath = temporaryPath,
                            OverwriteApproved = overwriteApproved,
                            Existing = existing,
                            Bytes = copiedBytes,
                            Entries = entryCount
                        });
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    ApplyAggregateAdmission(
                        baseline,
                        staged,
                        limits);
                    if (!DirectoryMatchesSnapshot(
                            destinationRoot,
                            baseline,
                            cancellationToken))
                    {
                        foreach (StagedImport stagedImport in staged)
                            stagedImport.Result.Error =
                                "The fortunes directory changed during import; nothing was committed.";
                        return result;
                    }

                    // Cancellation is honored throughout staging and once more immediately before
                    // commit. The short atomic commit loop then runs to completion so cancellation
                    // cannot leave an avoidable half-written file.
                    cancellationToken.ThrowIfCancellationRequested();
                    var committed = new List<CommittedImport>();
                    Exception commitFailure = null;
                    foreach (StagedImport stagedImport in staged)
                    {
                        try
                        {
                            committed.Add(CommitAtomic(
                                stagedImport,
                                replaceFile));
                            stagedImport.TemporaryPath = null;
                        }
                        catch (Exception ex)
                        {
                            commitFailure = ex;
                            break;
                        }
                    }
                    if (commitFailure == null)
                    {
                        foreach (CommittedImport committedImport in committed)
                        {
                            committedImport.Staged.Result.Imported = true;
                            TryDeleteFile(committedImport.BackupPath);
                        }
                    }
                    else
                    {
                        bool rollbackComplete = RollBackCommittedImports(
                            committed,
                            replaceFile);
                        string error =
                            "Batch commit failed and " +
                            (rollbackComplete
                                ? "all earlier changes were rolled back: "
                                : "rollback could not be fully verified: ") +
                            commitFailure.Message;
                        foreach (StagedImport stagedImport in staged)
                        {
                            stagedImport.Result.Imported = false;
                            stagedImport.Result.Error = error;
                        }
                    }
                }
            }
            finally
            {
                foreach (StagedImport stagedImport in staged)
                    TryDeleteFile(stagedImport.TemporaryPath);
                TryDeleteFile(lockPath);
            }
            return result;
        }

        private static void ApplyAggregateAdmission(
            DirectoryState baseline,
            List<StagedImport> staged,
            ImportLimits limits)
        {
            if (baseline == null) throw new ArgumentNullException("baseline");
            if (staged == null) throw new ArgumentNullException("staged");
            if (limits == null) throw new ArgumentNullException("limits");

            int batchFiles = baseline.Files.Count;
            long batchBytes = baseline.TotalBytes;
            long batchEntries = baseline.TotalEntries;
            foreach (StagedImport item in staged)
            {
                batchFiles += item.Existing == null ? 1 : 0;
                batchBytes = checked(
                    batchBytes -
                    (item.Existing == null ? 0 : item.Existing.Bytes) +
                    item.Bytes);
                batchEntries = checked(
                    batchEntries -
                    (item.Existing == null ? 0 : item.Existing.Entries) +
                    item.Entries);
            }

            string batchError;
            if (TryValidateAggregate(
                    batchFiles,
                    batchBytes,
                    batchEntries,
                    limits,
                    out batchError))
                return;

            // If the complete replacement-aware batch does not fit, retain the historical
            // per-file partial-success behavior. This second pass admits a deterministic prefix
            // against the live baseline and rejects only items that would cross a runtime bound.
            int acceptedFiles = baseline.Files.Count;
            long acceptedBytes = baseline.TotalBytes;
            long acceptedEntries = baseline.TotalEntries;
            for (int index = 0; index < staged.Count;)
            {
                StagedImport item = staged[index];
                int candidateFiles =
                    acceptedFiles + (item.Existing == null ? 1 : 0);
                long candidateBytes = checked(
                    acceptedBytes -
                    (item.Existing == null ? 0 : item.Existing.Bytes) +
                    item.Bytes);
                long candidateEntries = checked(
                    acceptedEntries -
                    (item.Existing == null ? 0 : item.Existing.Entries) +
                    item.Entries);
                string itemError;
                if (TryValidateAggregate(
                        candidateFiles,
                        candidateBytes,
                        candidateEntries,
                        limits,
                        out itemError))
                {
                    acceptedFiles = candidateFiles;
                    acceptedBytes = candidateBytes;
                    acceptedEntries = candidateEntries;
                    index++;
                    continue;
                }

                TryDeleteFile(item.TemporaryPath);
                item.TemporaryPath = null;
                item.Result.Error =
                    "Rejected by runtime limits: " + itemError;
                staged.RemoveAt(index);
            }
        }

        private static FileStream CreateUniqueStagingFile(
            string destinationRoot,
            out string path)
        {
            return CreateUniqueInternalFile(
                destinationRoot,
                StagingLeafPrefix,
                CopyBufferBytes,
                FileOptions.SequentialScan,
                out path);
        }

        private static string ReserveUniqueBackupPath(string destinationRoot)
        {
            string path;
            using (CreateUniqueInternalFile(
                destinationRoot,
                BackupLeafPrefix,
                1,
                FileOptions.None,
                out path))
            {
                // File.Replace and the portable fallback both overwrite this zero-byte
                // reservation. Keeping it present closes the name-allocation race.
            }
            return path;
        }

        private static FileStream CreateUniqueInternalFile(
            string destinationRoot,
            string leafPrefix,
            int bufferSize,
            FileOptions options,
            out string path)
        {
            path = null;
            for (int attempt = 0;
                 attempt < InternalFileAllocationAttempts;
                 attempt++)
            {
                string candidate = Path.Combine(
                    destinationRoot,
                    leafPrefix + NewInternalIdentifier());
                try
                {
                    var stream = new FileStream(
                        candidate,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize,
                        options);
                    path = candidate;
                    return stream;
                }
                catch (IOException)
                {
                    if (!File.Exists(candidate) &&
                        !Directory.Exists(candidate))
                        throw;
                }
                catch (UnauthorizedAccessException)
                {
                    if (!File.Exists(candidate) &&
                        !Directory.Exists(candidate))
                        throw;
                }
            }

            throw new IOException(
                "Could not allocate a unique internal fortune import file.");
        }

        private static string NewInternalIdentifier()
        {
            return Convert.ToBase64String(Guid.NewGuid().ToByteArray())
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static Stream OpenSourceFile(string path)
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferBytes,
                FileOptions.SequentialScan);
        }

        private static long CopyBounded(
            Stream source,
            Stream stagedDestination,
            MemoryStream validationBytes,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (maximumBytes < 1)
                throw new ArgumentOutOfRangeException("maximumBytes");
            if (source.CanSeek && (source.Length < 1 || source.Length > maximumBytes))
                throw new InvalidDataException(
                    "File size must be between 1 byte and " +
                    maximumBytes + " bytes.");

            var buffer = new byte[CopyBufferBytes];
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                total += read;
                if (total > maximumBytes)
                    throw new InvalidDataException(
                        "File exceeds the " + maximumBytes + "-byte limit.");
                stagedDestination.Write(buffer, 0, read);
                validationBytes.Write(buffer, 0, read);
            }
            if (total < 1)
                throw new InvalidDataException("File is empty.");
            return total;
        }

        private static DirectoryState ReadDirectoryState(
            string destinationRoot,
            ImportLimits limits,
            CancellationToken cancellationToken)
        {
            var state = new DirectoryState();
            foreach (string path in Directory.EnumerateFiles(
                destinationRoot,
                "*.txt",
                SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(path);
                var info = new FileInfo(path);
                var existing = new ExistingFile {
                    Bytes = info.Length,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks
                };
                if (existing.Bytes > 0 &&
                    existing.Bytes <= limits.FileBytes)
                {
                    byte[] bytes;
                    if (TryReadExistingBytes(
                            path,
                            (int)existing.Bytes,
                            cancellationToken,
                            out bytes))
                    {
                        int entries;
                        string error;
                        FortuneProvider.TryValidateCustomPackBytes(
                            bytes,
                            Path.GetFileNameWithoutExtension(fileName),
                            limits.Entries > int.MaxValue
                                ? int.MaxValue
                                : (int)limits.Entries,
                            out entries,
                            out error);
                        existing.Entries = entries;
                    }
                }
                state.Files.Add(fileName, existing);
                state.TotalBytes = checked(state.TotalBytes + existing.Bytes);
                state.TotalEntries = checked(
                    state.TotalEntries + existing.Entries);
            }
            return state;
        }

        private static bool TryReadExistingBytes(
            string path,
            int expectedBytes,
            CancellationToken cancellationToken,
            out byte[] bytes)
        {
            bytes = null;
            try
            {
                using (var source = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferBytes,
                    FileOptions.SequentialScan))
                using (var memory = new MemoryStream(expectedBytes))
                {
                    var buffer = new byte[CopyBufferBytes];
                    int total = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = source.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;
                        total = checked(total + read);
                        if (total > expectedBytes) return false;
                        memory.Write(buffer, 0, read);
                    }
                    if (total != expectedBytes) return false;
                    bytes = memory.ToArray();
                    return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        private static bool DirectoryMatchesSnapshot(
            string destinationRoot,
            DirectoryState expected,
            CancellationToken cancellationToken)
        {
            var current = new Dictionary<string, ExistingFile>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(
                destinationRoot,
                "*.txt",
                SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                current.Add(
                    Path.GetFileName(path),
                    new ExistingFile {
                        Bytes = info.Length,
                        LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks
                    });
            }
            if (current.Count != expected.Files.Count) return false;
            foreach (KeyValuePair<string, ExistingFile> pair in expected.Files)
            {
                ExistingFile actual;
                if (!current.TryGetValue(pair.Key, out actual) ||
                    actual.Bytes != pair.Value.Bytes ||
                    actual.LastWriteUtcTicks != pair.Value.LastWriteUtcTicks)
                    return false;
            }
            return true;
        }

        private static bool TryValidateAggregate(
            int files,
            long bytes,
            long entries,
            ImportLimits limits,
            out string error)
        {
            if (files < 0 || files > limits.Files)
            {
                error = "file count exceeds " + limits.Files + ".";
                return false;
            }
            if (bytes < 0 || bytes > limits.TotalBytes)
            {
                error = "combined size exceeds " + limits.TotalBytes + " bytes.";
                return false;
            }
            if (entries < 0 || entries > limits.Entries)
            {
                error = "combined row count exceeds " + limits.Entries + ".";
                return false;
            }
            error = null;
            return true;
        }

        private static CommittedImport CommitAtomic(
            StagedImport stagedImport,
            Action<string, string, string, bool> replaceFile)
        {
            if (stagedImport == null)
                throw new ArgumentNullException("stagedImport");
            string temporaryPath = stagedImport.TemporaryPath;
            string destinationPath = stagedImport.Result == null
                ? null
                : stagedImport.Result.DestinationPath;
            if (string.IsNullOrEmpty(temporaryPath) ||
                string.IsNullOrEmpty(destinationPath))
                throw new InvalidOperationException(
                    "Import staging metadata is incomplete.");
            if (File.Exists(destinationPath))
            {
                if (!stagedImport.OverwriteApproved)
                    throw new IOException(
                        "The destination appeared without overwrite approval.");
                string backupPath = null;
                try
                {
                    backupPath = ReserveUniqueBackupPath(
                        Path.GetDirectoryName(destinationPath));
                    AtomicFile.ReplaceExisting(
                        temporaryPath,
                        destinationPath,
                        backupPath,
                        CancellationToken.None,
                        replaceFile);
                    return new CommittedImport {
                        Staged = stagedImport,
                        BackupPath = backupPath,
                        CreatedNew = false
                    };
                }
                catch
                {
                    TryDeleteFile(backupPath);
                    throw;
                }
            }

            File.Move(temporaryPath, destinationPath);
            return new CommittedImport {
                Staged = stagedImport,
                CreatedNew = true
            };
        }

        private static bool RollBackCommittedImports(
            IList<CommittedImport> committed,
            Action<string, string, string, bool> replaceFile)
        {
            bool complete = true;
            for (int index = committed.Count - 1; index >= 0; index--)
            {
                CommittedImport item = committed[index];
                string destination = item.Staged.Result.DestinationPath;
                try
                {
                    if (!string.IsNullOrEmpty(item.BackupPath))
                    {
                        if (File.Exists(destination))
                            AtomicFile.ReplaceExisting(
                                item.BackupPath,
                                destination,
                                null,
                                CancellationToken.None,
                                replaceFile);
                        else
                            File.Move(item.BackupPath, destination);
                    }
                    else if (item.CreatedNew && File.Exists(destination))
                    {
                        File.Delete(destination);
                    }
                }
                catch
                {
                    complete = false;
                }
            }
            return complete;
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        internal static bool RunSelfTest(StringBuilder output)
        {
            if (output == null) throw new ArgumentNullException("output");
            string root = Path.Combine(
                Path.GetTempPath(),
                "DesktopAICompanion-fortune-import-" + Guid.NewGuid().ToString("N"));
            bool ok = true;
            try
            {
                Directory.CreateDirectory(root);
                ok = TestPerFileBoundary(root, output) && ok;
                ok = TestAggregateBoundary(root, output) && ok;
                ok = TestReplacementAwareBatch(root, output) && ok;
                ok = TestPortableReplacementFallback(root, output) && ok;
                ok = TestClassicMaxPathInternalFiles(root, output) && ok;
                ok = TestFileCountBoundary(root, output) && ok;
                ok = TestEntryBoundary(root, output) && ok;
                ok = TestContentAndNameRejections(root, output) && ok;
                ok = TestDuplicateNames(root, output) && ok;
                ok = TestLaterCandidateAfterAggregateRejection(root, output) && ok;
                ok = TestFullDirectoryRejectsBeforeSourceOpen(root, output) && ok;
                ok = TestReadFailurePreservesDestination(root, output) && ok;
                ok = TestCommitFailureRollsBackBatch(root, output) && ok;
                ok = TestCancellationCleanup(root, output) && ok;
            }
            catch (Exception ex)
            {
                ok = false;
                output.AppendLine(
                    "IMPORT EXC: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                catch (Exception ex)
                {
                    ok = false;
                    output.AppendLine("IMPORT CLEANUP EXC: " + ex.Message);
                }
            }
            output.AppendLine("custom_import=" + (ok ? "PASS" : "FAIL"));
            return ok;
        }

        private static bool TestPortableReplacementFallback(
            string root,
            StringBuilder output)
        {
            string caseRoot = CreateCase(root, "portable-replace-fallback");
            string destination = Path.Combine(caseRoot, "replace.txt");
            string temporary = Path.Combine(caseRoot, "replacement.tmp");
            byte[] original = Encoding.UTF8.GetBytes(
                "An original portable fortune that must be restored.");
            byte[] replacement = Encoding.UTF8.GetBytes(
                "A replacement portable fortune that must commit.");
            File.WriteAllBytes(destination, original);
            File.WriteAllBytes(temporary, replacement);

            var staged = new StagedImport {
                Result = new FortuneImportItemResult {
                    DestinationPath = destination
                },
                TemporaryPath = temporary,
                OverwriteApproved = true
            };
            Action<string, string, string, bool> unsupportedReplace =
                delegate
                {
                    throw new PlatformNotSupportedException(
                        "fault-injected File.Replace rejection");
                };

            CommittedImport committed = CommitAtomic(
                staged,
                unsupportedReplace);
            bool committedThroughFallback =
                ByteArraysEqual(replacement, File.ReadAllBytes(destination)) &&
                !File.Exists(temporary) &&
                !string.IsNullOrEmpty(committed.BackupPath) &&
                File.Exists(committed.BackupPath) &&
                ByteArraysEqual(
                    original,
                    File.ReadAllBytes(committed.BackupPath));

            bool rolledBackThroughFallback = RollBackCommittedImports(
                new[] { committed },
                unsupportedReplace);
            bool ok = committedThroughFallback &&
                rolledBackThroughFallback &&
                ByteArraysEqual(original, File.ReadAllBytes(destination)) &&
                !File.Exists(committed.BackupPath);
            if (!ok)
                output.AppendLine(
                    "IMPORT FAIL portable replacement/rollback fallback");
            return ok;
        }

        private static bool TestClassicMaxPathInternalFiles(
            string root,
            StringBuilder output)
        {
            const int destinationRootCharacters = 208;
            string sourceRoot = CreateCase(root, "classic-max-sources");
            string destination;
            try
            {
                destination = CreateDirectoryWithExactPathLength(
                    root,
                    destinationRootCharacters);
            }
            catch (Exception ex)
            {
                output.AppendLine(
                    "IMPORT FAIL classic MAX_PATH fixture: " + ex.Message);
                return false;
            }

            string legacyIdentifier = new string('0', 32);
            string shortIdentifier =
                new string('0', InternalIdentifierCharacters);
            string legacyStagingPath = Path.Combine(
                destination,
                ".fortune-import-" + legacyIdentifier + ".tmp");
            string legacyBackupPath =
                Path.Combine(destination, "first.txt") +
                ".import-backup-" + legacyIdentifier;
            string shortStagingPath = Path.Combine(
                destination,
                StagingLeafPrefix + shortIdentifier);
            string shortBackupPath = Path.Combine(
                destination,
                BackupLeafPrefix + shortIdentifier);
            bool layoutOk =
                legacyStagingPath.Length > ClassicMaxPathCharacters &&
                legacyBackupPath.Length > ClassicMaxPathCharacters &&
                shortStagingPath.Length <= ClassicMaxPathCharacters &&
                shortBackupPath.Length <= ClassicMaxPathCharacters &&
                Path.GetFileName(shortStagingPath).Length <=
                    ImportLockName.Length &&
                Path.GetFileName(shortBackupPath).Length <=
                    ImportLockName.Length &&
                Path.Combine(destination, ImportLockName).Length <=
                    ClassicMaxPathCharacters;
            if (!layoutOk)
            {
                output.AppendLine(
                    "IMPORT FAIL classic MAX_PATH fixture did not cross " +
                    "the legacy internal-name boundary");
                return false;
            }

            string newSource = Path.Combine(sourceRoot, "new-file.txt");
            WriteValidFileWithExactBytes(newSource, 256);
            FortuneImportBatchResult newFileResult = ImportCore(
                new[] { newSource },
                destination,
                null,
                CancellationToken.None,
                RuntimeLimits,
                OpenSourceFile);
            bool newFileOk =
                newFileResult.ImportedCount == 1 &&
                newFileResult.RejectedCount == 0 &&
                File.Exists(Path.Combine(destination, "new-file.txt")) &&
                Directory.GetFiles(destination).Length == 1;
            if (!newFileOk)
                output.AppendLine(
                    "IMPORT FAIL classic MAX_PATH new-file staging");

            string firstDestination = Path.Combine(
                destination,
                "first.txt");
            string blockedDestination = Path.Combine(
                destination,
                "blocked.txt");
            WriteValidFileWithExactBytes(firstDestination, 512);
            WriteValidFileWithExactBytes(blockedDestination, 256);
            string firstSource = Path.Combine(sourceRoot, "first.txt");
            string blockedSource = Path.Combine(sourceRoot, "blocked.txt");
            WriteValidFileWithExactBytes(firstSource, 256);
            WriteValidFileWithExactBytes(blockedSource, 512);
            var approved = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase) {
                    "first.txt",
                    "blocked.txt"
                };

            string firstBackupPath = null;
            string blockedBackupPath = null;
            bool rollbackObserved = false;
            FortuneImportBatchResult replacementResult = ImportCore(
                new[] { firstSource, blockedSource },
                destination,
                approved,
                CancellationToken.None,
                RuntimeLimits,
                OpenSourceFile,
                delegate(
                    string temporaryPath,
                    string destinationPath,
                    string backupPath,
                    bool ignoreMetadataErrors)
                {
                    if (string.Equals(
                            destinationPath,
                            firstDestination,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrEmpty(backupPath))
                        {
                            rollbackObserved =
                                string.Equals(
                                    temporaryPath,
                                    firstBackupPath,
                                    StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            firstBackupPath = backupPath;
                        }
                    }
                    if (string.Equals(
                            destinationPath,
                            blockedDestination,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        blockedBackupPath = backupPath;
                        throw new UnauthorizedAccessException(
                            "fault-injected near-MAX_PATH commit failure");
                    }
                    File.Replace(
                        temporaryPath,
                        destinationPath,
                        backupPath,
                        ignoreMetadataErrors);
                });

            bool backupPathsOk =
                IsShortInternalPath(
                    firstBackupPath,
                    destination,
                    BackupLeafPrefix) &&
                IsShortInternalPath(
                    blockedBackupPath,
                    destination,
                    BackupLeafPrefix);
            bool replacementOk =
                replacementResult.ImportedCount == 0 &&
                replacementResult.RejectedCount == 2 &&
                backupPathsOk &&
                rollbackObserved &&
                new FileInfo(firstDestination).Length == 512 &&
                new FileInfo(blockedDestination).Length == 256 &&
                !File.Exists(firstBackupPath) &&
                !File.Exists(blockedBackupPath) &&
                Directory.GetFiles(destination).Length == 3;
            if (!replacementOk)
                output.AppendLine(
                    "IMPORT FAIL classic MAX_PATH replacement backup/rollback");
            return newFileOk && replacementOk;
        }

        private static string CreateDirectoryWithExactPathLength(
            string parent,
            int pathLength)
        {
            string parentPath = Path.GetFullPath(parent).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            int segmentLength = pathLength - parentPath.Length - 1;
            if (segmentLength < 1 || segmentLength > 240)
                throw new InvalidOperationException(
                    "The self-test root cannot form the requested path length.");

            string path = Path.Combine(
                parentPath,
                new string('m', segmentLength));
            if (path.Length != pathLength)
                throw new InvalidOperationException(
                    "The self-test path length was not deterministic.");
            Directory.CreateDirectory(path);
            return path;
        }

        private static bool IsShortInternalPath(
            string path,
            string expectedDirectory,
            string expectedPrefix)
        {
            return !string.IsNullOrEmpty(path) &&
                path.Length <= ClassicMaxPathCharacters &&
                string.Equals(
                    Path.GetDirectoryName(path),
                    expectedDirectory,
                    StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(path).StartsWith(
                    expectedPrefix,
                    StringComparison.Ordinal) &&
                Path.GetFileName(path).Length ==
                    expectedPrefix.Length + InternalIdentifierCharacters;
        }

        private static bool TestPerFileBoundary(
            string root,
            StringBuilder output)
        {
            string caseRoot = CreateCase(root, "per-file");
            string source = Path.Combine(caseRoot, "exact.txt");
            string destination = Path.Combine(caseRoot, "destination");
            WriteValidFileWithExactBytes(
                source,
                FortunePackLoadPolicy.MaximumFileBytes);
            FortuneImportBatchResult exact = Import(
                new[] { source },
                destination,
                null,
                CancellationToken.None);

            string oversized = Path.Combine(caseRoot, "oversized.txt");
            WriteValidFileWithExactBytes(
                oversized,
                FortunePackLoadPolicy.MaximumFileBytes + 1);
            FortuneImportBatchResult over = Import(
                new[] { oversized },
                Path.Combine(caseRoot, "over-destination"),
                null,
                CancellationToken.None);
            bool ok = exact.ImportedCount == 1 &&
                exact.RejectedCount == 0 &&
                over.ImportedCount == 0 &&
                over.RejectedCount == 1;
            if (!ok) output.AppendLine("IMPORT FAIL per-file exact/+1 boundary");
            return ok;
        }

        private static bool TestAggregateBoundary(
            string root,
            StringBuilder output)
        {
            string exactRoot = CreateCase(root, "aggregate-exact");
            var exactSources = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                string path = Path.Combine(exactRoot, "exact-" + i + ".txt");
                WriteValidFileWithExactBytes(
                    path,
                    FortunePackLoadPolicy.MaximumFileBytes);
                exactSources.Add(path);
            }
            FortuneImportBatchResult exact = Import(
                exactSources,
                Path.Combine(exactRoot, "destination"),
                null,
                CancellationToken.None);

            string overRoot = CreateCase(root, "aggregate-over");
            var overSources = new List<string>();
            int[] sizes = {
                FortunePackLoadPolicy.MaximumFileBytes,
                FortunePackLoadPolicy.MaximumFileBytes,
                FortunePackLoadPolicy.MaximumFileBytes,
                FortunePackLoadPolicy.MaximumFileBytes - 255,
                256
            };
            for (int i = 0; i < sizes.Length; i++)
            {
                string path = Path.Combine(overRoot, "over-" + i + ".txt");
                WriteValidFileWithExactBytes(path, sizes[i]);
                overSources.Add(path);
            }
            FortuneImportBatchResult over = Import(
                overSources,
                Path.Combine(overRoot, "destination"),
                null,
                CancellationToken.None);
            bool ok = exact.ImportedCount == 4 &&
                exact.RejectedCount == 0 &&
                over.ImportedCount == 4 &&
                over.RejectedCount == 1;
            if (!ok) output.AppendLine("IMPORT FAIL aggregate exact/+1 boundary");
            return ok;
        }

        private static bool TestReplacementAwareBatch(
            string root,
            StringBuilder output)
        {
            string caseRoot = CreateCase(root, "replacement-aware-batch");
            string destination = Path.Combine(caseRoot, "destination");
            Directory.CreateDirectory(destination);
            WriteValidFileWithExactBytes(
                Path.Combine(destination, "replace.txt"),
                512);
            WriteValidFileWithExactBytes(
                Path.Combine(destination, "retained.txt"),
                256);

            string newSource = Path.Combine(caseRoot, "new.txt");
            WriteValidFileWithExactBytes(newSource, 256);
            string replacementRoot = Path.Combine(caseRoot, "replacement");
            Directory.CreateDirectory(replacementRoot);
            string replacementSource =
                Path.Combine(replacementRoot, "replace.txt");
            WriteValidFileWithExactBytes(replacementSource, 256);

            var approved = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase) { "replace.txt" };
            var limits = new ImportLimits {
                Files = 3,
                FileBytes = 1024,
                TotalBytes = 768,
                Entries = 16
            };
            FortuneImportBatchResult result = ImportCore(
                // The new file intentionally comes first. A sequential admission pass would
                // reject it before noticing that the later replacement frees exactly 256 bytes.
                new[] { newSource, replacementSource },
                destination,
                approved,
                CancellationToken.None,
                limits,
                OpenSourceFile);
            long finalBytes = 0;
            foreach (string path in Directory.EnumerateFiles(
                destination,
                "*.txt",
                SearchOption.TopDirectoryOnly))
                finalBytes += new FileInfo(path).Length;
            bool ok = result.ImportedCount == 2 &&
                result.RejectedCount == 0 &&
                Directory.GetFiles(destination, "*.txt").Length == 3 &&
                finalBytes == limits.TotalBytes;
            if (!ok)
                output.AppendLine(
                    "IMPORT FAIL replacement-aware selected-batch admission");
            return ok;
        }

        private static bool TestFileCountBoundary(
            string root,
            StringBuilder output)
        {
            string caseRoot = CreateCase(root, "file-count");
            var sources = new List<string>();
            for (int i = 0;
                 i < FortunePackLoadPolicy.MaximumFiles + 1;
                 i++)
            {
                string path = Path.Combine(
                    caseRoot,
                    "count-" + i.ToString("D3") + ".txt");
                WriteValidFileWithExactBytes(path, 256);
                sources.Add(path);
            }
            FortuneImportBatchResult result = Import(
                sources,
                Path.Combine(caseRoot, "destination"),
                null,
                CancellationToken.None);
            bool ok =
                result.ImportedCount == FortunePackLoadPolicy.MaximumFiles &&
                result.RejectedCount == 1;
            if (!ok) output.AppendLine("IMPORT FAIL 128/129 file boundary");
            return ok;
        }

        private static bool TestEntryBoundary(
            string root,
            StringBuilder output)
        {
            string caseRoot = CreateCase(root, "entry-count");
            string exact = Path.Combine(caseRoot, "rows-exact.txt");
            WriteRows(exact, FortunePackLoadPolicy.MaximumEntries);
            FortuneImportBatchResult exactResult = Import(
                new[] { exact },
                Path.Combine(caseRoot, "exact-destination"),
                null,
                CancellationToken.None);

            string over = Path.Combine(caseRoot, "rows-over.txt");
            WriteRows(over, FortunePackLoadPolicy.MaximumEntries + 1);
            FortuneImportBatchResult overResult = Import(
                new[] { over },
                Path.Combine(caseRoot, "over-destination"),
                null,
                CancellationToken.None);
            bool ok = exactResult.ImportedCount == 1 &&
                overResult.ImportedCount == 0 &&
                overResult.RejectedCount == 1;
            if (!ok) output.AppendLine("IMPORT FAIL 100000/100001 row boundary");
            return ok;
        }

        private static bool TestContentAndNameRejections(
            string root,
            StringBuilder output)
        {
            string caseRoot = CreateCase(root, "invalid-content");
            string nonText = Path.Combine(caseRoot, "not-text.bin");
            File.WriteAllText(nonText, "A valid fortune in the wrong file type.");
            string invalidUtf8 = Path.Combine(caseRoot, "invalid-utf8.txt");
            File.WriteAllBytes(
                invalidUtf8,
                new byte[] { 0x41, 0x20, 0xFF, 0x20, 0x42 });
            string invalidContent = Path.Combine(caseRoot, "invalid-content.txt");
            File.WriteAllText(invalidContent, "short");
            FortuneImportBatchResult result = Import(
                new[] { nonText, invalidUtf8, invalidContent },
                Path.Combine(caseRoot, "destination"),
                null,
                CancellationToken.None);
            bool ok = result.ImportedCount == 0 && result.RejectedCount == 3;
            if (!ok) output.AppendLine("IMPORT FAIL extension/UTF-8/content rejection");
            return ok;
        }

        private static bool TestDuplicateNames(
            string root,
            StringBuilder output)
        {
            string caseRoot = CreateCase(root, "duplicates");
            string firstRoot = Path.Combine(caseRoot, "one");
            string secondRoot = Path.Combine(caseRoot, "two");
            Directory.CreateDirectory(firstRoot);
            Directory.CreateDirectory(secondRoot);
            string first = Path.Combine(firstRoot, "same.txt");
            string second = Path.Combine(secondRoot, "same.txt");
            WriteValidFileWithExactBytes(first, 256);
            WriteValidFileWithExactBytes(second, 256);
            FortuneImportBatchResult result = Import(
                new[] { first, second },
                Path.Combine(caseRoot, "destination"),
                null,
                CancellationToken.None);
            bool ok = result.ImportedCount == 1 && result.RejectedCount == 1;
            if (!ok) output.AppendLine("IMPORT FAIL duplicate basename accounting");
            return ok;
        }

        private static bool TestLaterCandidateAfterAggregateRejection(
            string root,
            StringBuilder output)
        {
            string caseRoot = CreateCase(
                root,
                "later-candidate-after-aggregate-rejection");
            string destination = Path.Combine(caseRoot, "destination");
            Directory.CreateDirectory(destination);
            string baseline = Path.Combine(destination, "baseline.txt");
            WriteValidFileWithExactBytes(baseline, 512);

            string rejectedSource = Path.Combine(
                caseRoot,
                "aggregate-rejected.txt");
            WriteValidFileWithExactBytes(rejectedSource, 768);
            string acceptedSource = Path.Combine(
                caseRoot,
                "later-fitting.txt");
            WriteValidFileWithExactBytes(acceptedSource, 256);

            var limits = new ImportLimits {
                Files = 2,
                FileBytes = 1024,
                TotalBytes = 1024,
                Entries = 16
            };
            FortuneImportBatchResult result = ImportCore(
                new[] { rejectedSource, acceptedSource },
                destination,
                null,
                CancellationToken.None,
                limits,
                OpenSourceFile);

            bool ok =
                result.ImportedCount == 1 &&
                result.RejectedCount == 1 &&
                File.Exists(baseline) &&
                !File.Exists(Path.Combine(
                    destination,
                    Path.GetFileName(rejectedSource))) &&
                File.Exists(Path.Combine(
                    destination,
                    Path.GetFileName(acceptedSource))) &&
                Directory.GetFiles(destination, "*.txt").Length == 2;
            if (!ok)
                output.AppendLine(
                    "IMPORT FAIL later fitting candidate after aggregate rejection");
            return ok;
        }

        private static bool TestFullDirectoryRejectsBeforeSourceOpen(
            string root,
            StringBuilder output)
        {
            string caseRoot = CreateCase(root, "full-directory");
            string destination = Path.Combine(caseRoot, "destination");
            Directory.CreateDirectory(destination);
            WriteValidFileWithExactBytes(
                Path.Combine(destination, "existing-one.txt"),
                256);
            WriteValidFileWithExactBytes(
                Path.Combine(destination, "existing-two.txt"),
                256);
            string source = Path.Combine(caseRoot, "new-file.txt");
            WriteValidFileWithExactBytes(source, 256);
            var limits = new ImportLimits {
                Files = 2,
                FileBytes = 1024,
                TotalBytes = 4096,
                Entries = 16
            };
            int sourceOpenCount = 0;
            FortuneImportBatchResult result = ImportCore(
                new[] { source },
                destination,
                null,
                CancellationToken.None,
                limits,
                delegate(string path)
                {
                    sourceOpenCount++;
                    return OpenSourceFile(path);
                });
            bool ok = result.ImportedCount == 0 &&
                result.RejectedCount == 1 &&
                sourceOpenCount == 0 &&
                !File.Exists(Path.Combine(destination, "new-file.txt"));
            if (!ok)
                output.AppendLine(
                    "IMPORT FAIL full directory performed unnecessary source I/O");
            return ok;
        }

        private static bool TestReadFailurePreservesDestination(
            string root,
            StringBuilder output)
        {
            string caseRoot = CreateCase(root, "read-failure");
            string source = Path.Combine(caseRoot, "existing.txt");
            WriteValidFileWithExactBytes(source, 256);
            string destination = Path.Combine(caseRoot, "destination");
            Directory.CreateDirectory(destination);
            string existing = Path.Combine(destination, "existing.txt");
            byte[] original = Encoding.UTF8.GetBytes(
                "An existing fortune that must survive a failed import.");
            File.WriteAllBytes(existing, original);
            var approved = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase) { "existing.txt" };
            FortuneImportBatchResult result = ImportCore(
                new[] { source },
                destination,
                approved,
                CancellationToken.None,
                RuntimeLimits,
                delegate(string path)
                {
                    return new ThrowingReadStream(
                        new FileStream(
                            path,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read),
                        64);
                });
            byte[] actual = File.ReadAllBytes(existing);
            bool ok = result.ImportedCount == 0 &&
                result.RejectedCount == 1 &&
                ByteArraysEqual(original, actual);
            if (!ok) output.AppendLine("IMPORT FAIL read-failure atomic preservation");
            return ok;
        }

        private static bool TestCommitFailureRollsBackBatch(
            string root,
            StringBuilder output)
        {
            string caseRoot = CreateCase(root, "commit-rollback");
            string destination = Path.Combine(caseRoot, "destination");
            Directory.CreateDirectory(destination);
            string firstDestination = Path.Combine(destination, "first.txt");
            string blockedDestination = Path.Combine(destination, "blocked.txt");
            WriteValidFileWithExactBytes(firstDestination, 512);
            WriteValidFileWithExactBytes(blockedDestination, 256);

            string sources = Path.Combine(caseRoot, "sources");
            Directory.CreateDirectory(sources);
            string firstSource = Path.Combine(sources, "first.txt");
            string blockedSource = Path.Combine(sources, "blocked.txt");
            WriteValidFileWithExactBytes(firstSource, 256);
            WriteValidFileWithExactBytes(blockedSource, 512);
            var approved = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase) {
                    "first.txt",
                    "blocked.txt"
                };

            FortuneImportBatchResult result = ImportCore(
                new[] { firstSource, blockedSource },
                destination,
                approved,
                CancellationToken.None,
                RuntimeLimits,
                OpenSourceFile,
                delegate(
                    string temporaryPath,
                    string destinationPath,
                    string backupPath,
                    bool ignoreMetadataErrors)
                {
                    if (string.Equals(
                            destinationPath,
                            blockedDestination,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException(
                            "fault-injected second commit failure");
                    }
                    File.Replace(
                        temporaryPath,
                        destinationPath,
                        backupPath,
                        ignoreMetadataErrors);
                });

            bool ok = result.ImportedCount == 0 &&
                result.RejectedCount == 2 &&
                new FileInfo(firstDestination).Length == 512 &&
                new FileInfo(blockedDestination).Length == 256 &&
                Directory.GetFiles(destination).Length == 2;
            if (!ok)
                output.AppendLine(
                    "IMPORT FAIL commit failure did not roll back the admitted batch");
            return ok;
        }

        private static bool TestCancellationCleanup(
            string root,
            StringBuilder output)
        {
            string caseRoot = CreateCase(root, "cancellation");
            string source = Path.Combine(caseRoot, "cancel.txt");
            WriteValidFileWithExactBytes(source, 1024);
            string destination = Path.Combine(caseRoot, "destination");
            var cancellation = new CancellationTokenSource();
            bool cancelled = false;
            try
            {
                ImportCore(
                    new[] { source },
                    destination,
                    null,
                    cancellation.Token,
                    RuntimeLimits,
                    delegate(string path)
                    {
                        return new CancellingReadStream(
                            new FileStream(
                                path,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read),
                            cancellation);
                    });
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            finally
            {
                cancellation.Dispose();
            }
            string[] leftovers = Directory.Exists(destination)
                ? Directory.GetFiles(destination)
                : new string[0];
            bool ok = cancelled && leftovers.Length == 0;
            if (!ok) output.AppendLine("IMPORT FAIL cancellation cleanup");
            return ok;
        }

        private static string CreateCase(string root, string name)
        {
            string path = Path.Combine(root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void WriteValidFileWithExactBytes(
            string path,
            int byteCount)
        {
            if (byteCount < 256)
                throw new ArgumentOutOfRangeException("byteCount");
            var record = new byte[256];
            for (int i = 0; i < record.Length - 1; i++)
                record[i] = (byte)'A';
            record[record.Length - 1] = (byte)'\n';
            using (var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                int remaining = byteCount;
                while (remaining >= record.Length)
                {
                    stream.Write(record, 0, record.Length);
                    remaining -= record.Length;
                }
                for (int i = 0; i < remaining; i++)
                    stream.WriteByte((byte)' ');
            }
        }

        private static void WriteRows(string path, int rows)
        {
            var record = new byte[32];
            for (int i = 0; i < record.Length - 1; i++)
                record[i] = (byte)'R';
            record[record.Length - 1] = (byte)'\n';
            using (var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                for (int i = 0; i < rows; i++)
                    stream.Write(record, 0, record.Length);
            }
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        private sealed class ThrowingReadStream : Stream
        {
            private readonly Stream inner;
            private readonly int bytesBeforeFailure;
            private int bytesRead;

            public ThrowingReadStream(Stream inner, int bytesBeforeFailure)
            {
                this.inner = inner;
                this.bytesBeforeFailure = bytesBeforeFailure;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (bytesRead >= bytesBeforeFailure)
                    throw new IOException("Injected read failure.");
                int permitted = Math.Min(
                    count,
                    bytesBeforeFailure - bytesRead);
                int read = inner.Read(buffer, offset, permitted);
                bytesRead += read;
                return read;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return inner.CanSeek; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return inner.Length; } }
            public override long Position
            {
                get { return inner.Position; }
                set { throw new NotSupportedException(); }
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }
            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }
            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class CancellingReadStream : Stream
        {
            private readonly Stream inner;
            private readonly CancellationTokenSource cancellation;
            private bool cancelled;

            public CancellingReadStream(
                Stream inner,
                CancellationTokenSource cancellation)
            {
                this.inner = inner;
                this.cancellation = cancellation;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = inner.Read(buffer, offset, Math.Min(count, 64));
                if (!cancelled)
                {
                    cancelled = true;
                    cancellation.Cancel();
                }
                return read;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return inner.CanSeek; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return inner.Length; } }
            public override long Position
            {
                get { return inner.Position; }
                set { throw new NotSupportedException(); }
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }
            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }
            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
