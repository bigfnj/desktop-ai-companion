using AgentBroker.TestSupport;
using AgentBroker.Worker.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class BrokerIdentityRenewalChecks
{
    internal static Task RunAsync(Action<bool, string> check)
    {
        Console.WriteLine("WITNESS: BROKER_IDENTITY_RENEWAL_EXECUTED");
        var scratch = OwnedScratch.Create("identity-renewal");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string root = scratch.Root;
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        try
        {
            ProbeReplacementForms(Path.Combine(root, "replacement-diagnostic"));
            Guid defaultId = Guid.NewGuid(); string defaultDir = Own("default-replacement", defaultId);
            string defaultPath = Path.Combine(defaultDir, "tls-identity.dat");
            string defaultKey, defaultPin; byte[] defaultAnchor; DateTimeOffset defaultDue;
            using (var original = BrokerIdentity.LoadOrCreate(defaultDir, defaultId, now))
            {
                defaultKey = original.KeyName; defaultPin = original.Fingerprint;
                defaultAnchor = original.Certificate.RawData; defaultDue = original.RenewalDueAt;
            }
            byte[] defaultBefore = File.ReadAllBytes(defaultPath); Exception? defaultFailure = null;
            using (var locked = new FileStream(defaultPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Exercise the shipped three-argument default. A raw File.Replace reversion throws its primary
                // IOException directly, while the actual helper wraps its failed native fallback.
                try { using var rejected = BrokerIdentity.LoadOrCreate(defaultDir, defaultId, defaultDue); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { defaultFailure = error; }
            }
            DescribeReplacement("default-renewal-locked", defaultFailure, null, primaryObserved: false);
            check(defaultFailure is IOException { InnerException: Win32Exception nativeFailure } && nativeFailure.NativeErrorCode is 5 or 32,
                "IDENTITY_DEFAULT_REPLACEMENT_WIRING: the real default renewal reaches the helper and wraps an actual locked-destination native failure");
            check(File.ReadAllBytes(defaultPath).SequenceEqual(defaultBefore) && CngKey.Exists(defaultKey) &&
                Directory.GetFiles(defaultDir, "identity-*.tmp").Length == 0,
                "IDENTITY_DEFAULT_REPLACEMENT_PRESERVED: a default-path sharing failure retains exact prior ciphertext and key and cleans its temporary");
            using (var reloaded = BrokerIdentity.LoadOrCreate(defaultDir, defaultId, defaultDue.AddTicks(-1)))
                check(reloaded.KeyName == defaultKey && reloaded.Fingerprint == defaultPin && reloaded.Certificate.RawData.SequenceEqual(defaultAnchor),
                    "IDENTITY_DEFAULT_REPLACEMENT_USABLE: the original default-path identity remains usable after the locked renewal");

            Guid partialId = Guid.NewGuid(); string partialDir = Own("missing-destination", partialId);
            string partialPath = Path.Combine(partialDir, "tls-identity.dat");
            string partialKey, partialPin; byte[] partialAnchor; DateTimeOffset partialDue;
            using (var original = BrokerIdentity.LoadOrCreate(partialDir, partialId, now))
            {
                partialKey = original.KeyName; partialPin = original.Fingerprint;
                partialAnchor = original.Certificate.RawData; partialDue = original.RenewalDueAt;
            }
            string? retainedTemporary = null; byte[]? retainedCiphertext = null; int partialCalls = 0; bool partialFailed = false;
            try { using var rejected = BrokerIdentity.LoadOrCreate(partialDir, partialId, partialDue, (temporary, destination) =>
            {
                partialCalls++; retainedTemporary = temporary; retainedCiphertext = File.ReadAllBytes(temporary);
                File.Delete(destination);
                throw new IOException("Synthetic partial replacement: destination removed before replacement publication.");
            }); }
            catch (IOException) { partialFailed = true; }
            check(partialFailed && partialCalls == 1 && !File.Exists(partialPath) && CngKey.Exists(partialKey) &&
                retainedTemporary is not null && File.Exists(retainedTemporary) && retainedCiphertext is not null &&
                File.ReadAllBytes(retainedTemporary).SequenceEqual(retainedCiphertext) && Directory.GetFiles(partialDir, "identity-*.tmp").Length == 1,
                "IDENTITY_MISSING_DEST_TEMP_PRESERVED: failed renewal preserves its exact sealed temporary when the destination has disappeared");
            using (var metadata = Read(retainedTemporary!))
            using (var current = X509CertificateLoader.LoadCertificate(metadata.RootElement.GetProperty("CurrentCertificateDER").GetBytesFromBase64()))
            using (var anchorCertificate = X509CertificateLoader.LoadCertificate(partialAnchor))
            using (var anchorPublic = anchorCertificate.GetRSAPublicKey())
            using (var key = CngKey.Open(partialKey))
            using (var rsa = new RSACng(key))
                check(metadata.RootElement.GetProperty("Version").GetInt32() == 2 &&
                    metadata.RootElement.GetProperty("BrokerId").GetGuid() == partialId && metadata.RootElement.GetProperty("KeyName").GetString() == partialKey &&
                    metadata.RootElement.GetProperty("OriginalAnchorDER").GetBytesFromBase64().SequenceEqual(partialAnchor) &&
                    CertificateContinuity.MatchesPinnedCertificate(current, partialId, partialPin, partialDue) &&
                    anchorPublic!.ExportSubjectPublicKeyInfo().SequenceEqual(rsa.ExportSubjectPublicKeyInfo()),
                    "IDENTITY_MISSING_DEST_BINDING: retained sealed metadata preserves the exact broker, key and original anchor rather than inventing a new identity");
            bool orphanRejected = false;
            try { using var rejected = BrokerIdentity.LoadOrCreate(partialDir, partialId, partialDue); }
            catch (CryptographicException) { orphanRejected = true; }
            check(orphanRejected && !File.Exists(partialPath) && CngKey.Exists(partialKey) &&
                File.ReadAllBytes(retainedTemporary!).SequenceEqual(retainedCiphertext!),
                "IDENTITY_MISSING_DEST_FAIL_CLOSED: startup preserves the orphaned original key and recovery file without promoting metadata automatically");
            // The synthetic recovery copy is removed only by this test's explicit cleanup.
            File.Delete(retainedTemporary!);

            Guid creationId = Guid.NewGuid(); string creationDir = Own("creation-failure", creationId);
            Directory.CreateDirectory(Path.Combine(creationDir, "tls-identity.dat"));
            bool creationFailed = false;
            try { using var rejected = BrokerIdentity.LoadOrCreate(creationDir, creationId, now); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { creationFailed = true; }
            check(creationFailed && !CngKey.Exists(BrokerIdentity.KeyNameFor(creationDir, creationId)) &&
                Directory.GetFiles(creationDir, "identity-*.tmp").Length == 0 && Directory.Exists(Path.Combine(creationDir, "tls-identity.dat")),
                "IDENTITY_CREATE_FAILURE_CLEAN: failed initial creation still deletes its own key and temporary and preserves the pre-existing obstacle");

            Guid id = Guid.NewGuid();
            string directory = Own("lifecycle", id);
            string path = Path.Combine(directory, "tls-identity.dat");
            string fingerprint, keyName;
            byte[] anchor, initialBytes;
            DateTimeOffset due;
            using (var original = BrokerIdentity.LoadOrCreate(directory, id, now))
            {
                fingerprint = original.Fingerprint; keyName = original.KeyName; anchor = original.Certificate.RawData;
                due = original.RenewalDueAt;
                using var metadata = Read(path);
                check(metadata.RootElement.GetProperty("Version").GetInt32() == 1 &&
                    metadata.RootElement.GetProperty("Certificate").GetBytesFromBase64().SequenceEqual(anchor),
                    "IDENTITY_V1_COMPATIBLE: first creation retains the original metadata format");
                check(fingerprint == original.Certificate.GetCertHashString(HashAlgorithmName.SHA256) &&
                    due == new DateTimeOffset(original.Certificate.NotAfter.ToUniversalTime()).AddDays(-90),
                    "IDENTITY_RENEWAL_DUE: original pin and explicit ninety-day boundary are exposed");
                using var key = CngKey.Open(keyName);
                bool exportDenied = false;
                try { _ = key.Export(CngKeyBlobFormat.Pkcs8PrivateBlob); } catch (CryptographicException) { exportDenied = true; }
                check(!key.IsMachineKey && key.ExportPolicy == CngExportPolicies.None && exportDenied,
                    "IDENTITY_KEY_POLICY: the original key remains current-user and nonexportable");
            }
            initialBytes = File.ReadAllBytes(path);
            using (var before = BrokerIdentity.LoadOrCreate(directory, id, due.AddTicks(-1)))
                check(before.Certificate.RawData.SequenceEqual(anchor) && File.ReadAllBytes(path).SequenceEqual(initialBytes),
                    "IDENTITY_THRESHOLD_BEFORE: a certificate with more than ninety days remaining is not rewritten");
            byte[] firstLeaf, firstProof;
            DateTimeOffset nextDue;
            using (var renewed = BrokerIdentity.LoadOrCreate(directory, id, due))
            {
                firstLeaf = renewed.Certificate.RawData; nextDue = renewed.RenewalDueAt;
                using var metadata = Read(path);
                check(renewed.Fingerprint == fingerprint && renewed.KeyName == keyName && !firstLeaf.SequenceEqual(anchor),
                    "IDENTITY_RENEWAL_STABLE: due renewal changes the leaf while preserving the original pin and key name");
                var proof = renewed.Certificate.Extensions[CertificateContinuity.AnchorExtensionOid];
                check(proof is not null && !proof.Critical && proof.RawData.Length <= CertificateContinuity.MaximumProofBytes,
                    "IDENTITY_RENEWAL_PROOF: the current leaf carries one bounded noncritical original-anchor proof");
                firstProof = proof!.RawData;
                check(metadata.RootElement.GetProperty("Version").GetInt32() == 2 &&
                    metadata.RootElement.GetProperty("OriginalAnchorDER").GetBytesFromBase64().SequenceEqual(anchor) &&
                    metadata.RootElement.GetProperty("CurrentCertificateDER").GetBytesFromBase64().SequenceEqual(firstLeaf),
                    "IDENTITY_V2_METADATA: renewal atomically retains the original anchor and current leaf");
                using var original = X509CertificateLoader.LoadCertificate(anchor);
                using var oldPublic = original.GetRSAPublicKey();
                using var newPublic = renewed.Certificate.GetRSAPublicKey();
                using var signing = renewed.Certificate.GetRSAPrivateKey();
                byte[] challenge = Encoding.UTF8.GetBytes("Synthetic identity renewal contract.");
                check(oldPublic!.ExportSubjectPublicKeyInfo().SequenceEqual(newPublic!.ExportSubjectPublicKeyInfo()) &&
                    oldPublic.VerifyData(challenge, signing!.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                    "IDENTITY_SAME_KEY: the renewed leaf proves possession of the original private key");
                check(CertificateContinuity.MatchesPinnedCertificate(renewed.Certificate, id, fingerprint, due) &&
                    renewed.Certificate.NotAfter.ToUniversalTime() == due.AddYears(5).UtcDateTime,
                    "IDENTITY_LIVE_VALIDITY: the renewed five-year leaf validates against its original pin");
            }
            byte[] renewedBytes = File.ReadAllBytes(path);
            using (var reload = BrokerIdentity.LoadOrCreate(directory, id, due.AddMinutes(1)))
                check(reload.Fingerprint == fingerprint && reload.Certificate.RawData.SequenceEqual(firstLeaf) &&
                    File.ReadAllBytes(path).SequenceEqual(renewedBytes),
                    "IDENTITY_V2_RELOAD: ordinary reload does not rotate again or change metadata");
            using (var second = BrokerIdentity.LoadOrCreate(directory, id, nextDue))
                check(second.Fingerprint == fingerprint && second.Certificate.Extensions[CertificateContinuity.AnchorExtensionOid]!.RawData.SequenceEqual(firstProof) &&
                    second.Certificate.RawData.Length <= CertificateContinuity.MaximumLeafBytes,
                    "IDENTITY_REPEAT_RENEWAL: later renewal carries the same bounded original proof without nesting");

            using (var current = BrokerIdentity.LoadOrCreate(directory, id, nextDue.AddMinutes(1)))
            {
                byte[] beforeFailure = File.ReadAllBytes(path);
                int replaceCalls = 0;
                bool failed = false;
                try { using var rejected = BrokerIdentity.LoadOrCreate(directory, id, current.RenewalDueAt,
                    (temporary, destination) => { replaceCalls++; throw new IOException("Synthetic atomic replacement failure."); }); }
                catch (IOException) { failed = true; }
                check(failed && replaceCalls == 1 && File.ReadAllBytes(path).SequenceEqual(beforeFailure) && CngKey.Exists(keyName) &&
                    Directory.GetFiles(directory, "identity-*.tmp").Length == 0,
                    "IDENTITY_REPLACE_FAILURE: failed atomic replacement preserves the original metadata and key and removes its owned temporary file");
                using var afterFailure = BrokerIdentity.LoadOrCreate(directory, id, nextDue.AddMinutes(2));
                check(afterFailure.Certificate.RawData.SequenceEqual(current.Certificate.RawData),
                    "IDENTITY_REPLACE_RECOVERY: the original identity remains usable after a failed replacement");
            }

            Guid fallbackId = Guid.NewGuid(); string fallbackDir = Own("replace-fallback", fallbackId);
            string fallbackPath = Path.Combine(fallbackDir, "tls-identity.dat");
            string fallbackKey, fallbackPin; byte[] fallbackAnchor; DateTimeOffset fallbackDue;
            using (var initial = BrokerIdentity.LoadOrCreate(fallbackDir, fallbackId, now))
            {
                fallbackKey = initial.KeyName; fallbackPin = initial.Fingerprint; fallbackAnchor = initial.Certificate.RawData;
                fallbackDue = initial.RenewalDueAt;
            }
            int primaryCalls = 0; byte[]? stagedBytes = null; (uint Volume, ulong Index)? stagedIdentity = null;
            bool primaryContract = false;
            BrokerIdentity? fallbackRenewed = null;
            try { fallbackRenewed = BrokerIdentity.LoadOrCreate(fallbackDir, fallbackId, fallbackDue,
                (temporary, destination) => BrokerIdentity.ReplaceMetadata(temporary, destination, (source, target, backup, ignoreMetadataErrors) =>
                {
                    primaryCalls++;
                    primaryContract = source == temporary && target == destination && backup is null && ignoreMetadataErrors &&
                        Path.GetDirectoryName(source) == Path.GetDirectoryName(target);
                    stagedBytes = File.ReadAllBytes(source); stagedIdentity = FileStamp(source);
                    throw new IOException("Synthetic primary replacement failure before mutation.");
                })); }
            catch (IOException) { }
            check(fallbackRenewed is not null,
                "IDENTITY_FALLBACK_COMPLETES: a primary IOException reaches the real atomic fallback and completes renewal");
            using (var renewed = fallbackRenewed!)
            {
                check(primaryCalls == 1 && primaryContract,
                    "IDENTITY_FALLBACK_PRIMARY_ONCE: the actual helper attempts its primary once with no backup and same-directory paths");
                check(stagedBytes is not null && File.ReadAllBytes(fallbackPath).SequenceEqual(stagedBytes),
                    "IDENTITY_FALLBACK_EXACT_BYTES: a real native fallback publishes exactly the flushed temporary metadata");
                check(stagedIdentity is not null && FileStamp(fallbackPath) == stagedIdentity && Directory.GetFiles(fallbackDir, "identity-*.tmp").Length == 0,
                    "IDENTITY_FALLBACK_RENAME: native fallback preserves source file identity rather than copying its bytes and deleting it");
                using var metadata = Read(fallbackPath);
                check(renewed.Fingerprint == fallbackPin && renewed.KeyName == fallbackKey &&
                    metadata.RootElement.GetProperty("OriginalAnchorDER").GetBytesFromBase64().SequenceEqual(fallbackAnchor) &&
                    metadata.RootElement.GetProperty("CurrentCertificateDER").GetBytesFromBase64().SequenceEqual(renewed.Certificate.RawData),
                    "IDENTITY_FALLBACK_SAME_KEY_PIN: fallback renewal preserves the original anchor, key and exact renewed leaf");

                byte[] beforeFailure = File.ReadAllBytes(fallbackPath); int failedPrimaryCalls = 0; Exception? failure = null;
                using (var locked = new FileStream(fallbackPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try
                    {
                        using var rejected = BrokerIdentity.LoadOrCreate(fallbackDir, fallbackId, renewed.RenewalDueAt,
                            (temporary, destination) => BrokerIdentity.ReplaceMetadata(temporary, destination, (_, _, _, _) =>
                            {
                                failedPrimaryCalls++;
                                throw new IOException("Synthetic primary failure before the actual sharing-denied fallback.");
                            }));
                    }
                    catch (Exception error) when (error is IOException or Win32Exception) { failure = error; }
                }
                check(failure is IOException { InnerException: Win32Exception } && failedPrimaryCalls == 1,
                    "IDENTITY_FALLBACK_NATIVE_IO: a real locked-destination native failure surfaces as IOException with its Win32 cause");
                check(File.ReadAllBytes(fallbackPath).SequenceEqual(beforeFailure) && CngKey.Exists(fallbackKey) &&
                    Directory.GetFiles(fallbackDir, "identity-*.tmp").Length == 0,
                    "IDENTITY_FALLBACK_FAILURE_PRESERVED: failed fallback preserves exact prior ciphertext and original key and cleans its own temporary");
                using var recovered = BrokerIdentity.LoadOrCreate(fallbackDir, fallbackId, fallbackDue.AddMinutes(1));
                using var beforePublic = renewed.Certificate.GetRSAPublicKey(); using var afterPublic = recovered.Certificate.GetRSAPublicKey();
                using var afterPrivate = recovered.Certificate.GetRSAPrivateKey();
                byte[] challenge = Encoding.UTF8.GetBytes("Synthetic post-failure key possession.");
                check(recovered.Fingerprint == renewed.Fingerprint && recovered.Certificate.RawData.SequenceEqual(renewed.Certificate.RawData) &&
                    beforePublic!.ExportSubjectPublicKeyInfo().SequenceEqual(afterPublic!.ExportSubjectPublicKeyInfo()) &&
                    beforePublic.VerifyData(challenge, afterPrivate!.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                    "IDENTITY_FALLBACK_FAILURE_USABLE: the previous certificate and same private-key identity remain usable after failure");
            }

            Guid expiredId = Guid.NewGuid(); string expiredDir = Own("expired-legacy", expiredId);
            string expiredPin;
            using (var expired = BrokerIdentity.LoadOrCreate(expiredDir, expiredId, now.AddYears(-6))) expiredPin = expired.Fingerprint;
            using (var recovered = BrokerIdentity.LoadOrCreate(expiredDir, expiredId, now))
                check(recovered.Fingerprint == expiredPin && BrokerIdentity.IsFor(recovered.Certificate, expiredId),
                    "IDENTITY_EXPIRED_LEGACY: a correctly keyed expired legacy identity renews without changing its pin");

            Guid futureId = Guid.NewGuid(); string futureDir = Own("future", futureId);
            using (var future = BrokerIdentity.LoadOrCreate(futureDir, futureId, now.AddDays(1))) { }
            PreservedFailure(futureDir, futureId, now, "IDENTITY_FUTURE_REJECTED: future-invalid metadata is preserved without renewal");

            byte[] good = File.ReadAllBytes(path);
            var invalids = new[] {
                JsonSerializer.SerializeToUtf8Bytes(new { Version = 2, BrokerId = Guid.NewGuid(), KeyName = keyName,
                    OriginalAnchorDER = anchor, CurrentCertificateDER = firstLeaf }),
                JsonSerializer.SerializeToUtf8Bytes(new { Version = 2, BrokerId = id, KeyName = keyName,
                    OriginalAnchorDER = new byte[CertificateContinuity.MaximumAnchorBytes + 1], CurrentCertificateDER = firstLeaf }),
                JsonSerializer.SerializeToUtf8Bytes(new { Version = 2, BrokerId = id, KeyName = keyName,
                    OriginalAnchorDER = anchor, CurrentCertificateDER = new byte[CertificateContinuity.MaximumLeafBytes + 1] }),
                Encoding.UTF8.GetBytes("{\"Version\":2,\"Version\":1}"),
                Encoding.UTF8.GetBytes("{\"Version\":3}"),
                Encoding.UTF8.GetBytes("{\"Version\":\"2\"}")
            };
            foreach (byte[] invalid in invalids)
            {
                File.WriteAllBytes(path, UserProtection.Seal(invalid));
                PreservedFailure(directory, id, nextDue.AddMinutes(2),
                    "IDENTITY_INVALID_METADATA: mismatching, oversized, duplicate or unknown metadata cannot replace identity");
            }
            File.WriteAllBytes(path, good);
            Guid wrongKeyId = id; string wrongKeyDir = Own("wrong-key", wrongKeyId);
            string ownKeyName;
            using (var own = BrokerIdentity.LoadOrCreate(wrongKeyDir, wrongKeyId, now)) ownKeyName = own.KeyName;
            File.WriteAllBytes(Path.Combine(wrongKeyDir, "tls-identity.dat"), UserProtection.Seal(JsonSerializer.SerializeToUtf8Bytes(
                new { Version = 1, BrokerId = wrongKeyId, KeyName = ownKeyName, Certificate = anchor })));
            PreservedFailure(wrongKeyDir, wrongKeyId, now, "IDENTITY_ID_KEY_MISMATCH: an original anchor with a different private key cannot replace identity");
            check(!Directory.GetFiles(root, "identity-*.tmp", SearchOption.AllDirectories).Any(),
                "IDENTITY_TEMP_CLEAN: identity operations leave no owned replacement temporaries");
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            foreach (string keyName in keys)
            {
                try { if (CngKey.Exists(keyName)) { using var key = CngKey.Open(keyName); key.Delete(); } }
                catch (Exception error) { cleanupFailures.Add(error); }
            }
            try { scratch.Dispose(); } catch (Exception error) { cleanupFailures.Add(error); }
            bool cleaned = keys.All(keyName => !CngKey.Exists(keyName)) && !Directory.Exists(root);
            Console.WriteLine("IDENTITY_OWNED_RESOURCES_CLEAN=" + cleaned);
            if (!cleaned) cleanupFailures.Add(new IOException("Identity fixture resources remain."));
            if (cleanupFailures.Count != 0) throw new AggregateException("Identity fixture cleanup failed.", cleanupFailures);
        }
        check(keys.All(keyName => !CngKey.Exists(keyName)) && !Directory.Exists(root),
            "IDENTITY_FIXTURE_CLEAN: all invocation-owned keys and the exact scratch root are absent");
        return Task.CompletedTask;

        string Own(string name, Guid brokerId)
        {
            string directory = Path.Combine(root, name);
            keys.Add(BrokerIdentity.KeyNameFor(directory, brokerId));
            return directory;
        }
        void PreservedFailure(string directory, Guid brokerId, DateTimeOffset at, string label)
        {
            string path = Path.Combine(directory, "tls-identity.dat");
            byte[] before = File.ReadAllBytes(path);
            bool rejected = false; int replacements = 0;
            try { using var ignored = BrokerIdentity.LoadOrCreate(directory, brokerId, at,
                (_, _) => { replacements++; throw new InvalidOperationException("Unexpected replacement."); }); }
            catch (Exception error) when (error is InvalidDataException or CryptographicException) { rejected = true; }
            check(rejected && replacements == 0 && File.ReadAllBytes(path).SequenceEqual(before) &&
                CngKey.Exists(BrokerIdentity.KeyNameFor(directory, brokerId)), label);
        }
    }

    private static void ProbeReplacementForms(string directory)
    {
        Console.WriteLine("WITNESS: IDENTITY_REPLACE_DIAGNOSTIC_EXECUTED");
        foreach (string mode in new[] { "replace-false", "replace-true", "helper" })
        {
            string own = Path.Combine(directory, mode); Directory.CreateDirectory(own);
            string destination = Path.Combine(own, "destination.dat"), temporary = Path.Combine(own, "replacement.dat");
            byte[] expected = Encoding.UTF8.GetBytes("Synthetic fresh-copy replacement bytes.");
            File.WriteAllText(destination, "Synthetic original bytes."); File.WriteAllBytes(temporary, expected);
            Exception? primary = null, failure = null;
            try
            {
                if (mode == "helper") BrokerIdentity.ReplaceMetadata(temporary, destination, (source, target, backup, ignoreErrors) =>
                {
                    try { File.Replace(source, target, backup, ignoreErrors); }
                    catch (Exception error) { primary = error; throw; }
                });
                else File.Replace(temporary, destination, null, mode == "replace-true");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { failure = error; }
            DescribeReplacement(mode, failure, mode == "helper" ? primary : failure);
            Console.WriteLine("IDENTITY_REPLACE_DIAGNOSTIC mode=" + mode + " destinationExists=" + File.Exists(destination) +
                " temporaryExists=" + File.Exists(temporary) + " publishedExactBytes=" +
                (File.Exists(destination) && File.ReadAllBytes(destination).SequenceEqual(expected)));
        }
    }

    private static void DescribeReplacement(string mode, Exception? failure, Exception? primary, bool primaryObserved = true)
    {
        string Describe(Exception? error) => error is null ? "success" : error.GetType().Name + ":HResult=0x" +
            unchecked((uint)error.HResult).ToString("X8", System.Globalization.CultureInfo.InvariantCulture) +
            (error is Win32Exception native ? ":NativeErrorCode=" + native.NativeErrorCode : "") +
            (error.InnerException is null ? "" : ":Inner=" + Describe(error.InnerException));
        Console.WriteLine("IDENTITY_REPLACE_DIAGNOSTIC mode=" + mode + " result=" + Describe(failure) +
            " primary=" + (primaryObserved ? Describe(primary) : "not_observed"));
    }

    private static JsonDocument Read(string path)
    {
        byte[] plain = UserProtection.Unseal(File.ReadAllBytes(path));
        try { using var input = new MemoryStream(plain, writable: false); return JsonDocument.Parse(input); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WrittenLow, WrittenHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    private static (uint Volume, ulong Index) FileStamp(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var information)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (information.Volume, (ulong)information.IndexHigh << 32 | information.IndexLow);
    }
}
