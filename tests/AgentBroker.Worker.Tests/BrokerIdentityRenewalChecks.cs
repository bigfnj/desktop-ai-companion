using AgentBroker.TestSupport;
using AgentBroker.Worker.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

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

    private static JsonDocument Read(string path)
    {
        byte[] plain = UserProtection.Unseal(File.ReadAllBytes(path));
        try { using var input = new MemoryStream(plain, writable: false); return JsonDocument.Parse(input); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
