using AgentBroker.Core;

internal static class WaitingMailChecks
{
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Utc = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        internal long Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        internal void Advance(TimeSpan elapsed) { Timestamp += elapsed.Ticks; Utc += elapsed; }
    }
    internal static int Run(string root)
    {
        int passed = 0;
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); passed++; }
        using var fixture = new Fixture(Path.Combine(root, "waiting-main"));
        var store = fixture.Store; var clock = fixture.Clock;
        var first = fixture.Accept();
        var firstObserved = clock.Utc;
        Check(store.ReadMessageWait(first) is null && store.ReadWaitingMail().Sessions.Single(row => row.Session == fixture.Target.Address).BusyCount == 0,
            "WAITING_READ_NO_OBSERVATION");
        fixture.Tick(TimeSpan.Zero);
        for (int i = 0; i < 29; i++) fixture.Tick(TimeSpan.FromSeconds(10));
        fixture.Tick(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Check(fixture.Notices() == 0, "WAITING_BUSY_NOT_BEFORE_DUE");
        var snapshot = store.ReadWaitingMail();
        Check(snapshot.BrokerId == store.BrokerId && snapshot.NoticeAfterSeconds == 300 && snapshot.Sessions.Single(row => row.Session == fixture.Target.Address) is
            { PendingCount: 1, QueuedCount: 1, BlockedCount: 1, BusyCount: 1, UnobservedBlockedCount: 0 }, "WAITING_COUNT_AND_POLICY");
        fixture.Tick(TimeSpan.FromTicks(1));
        Check(fixture.Notices() == 1, "WAITING_BUSY_AT_DUE");
        fixture.Tick(TimeSpan.FromSeconds(10));
        Check(fixture.Notices() == 1, "WAITING_NOTICE_ONCE");
        var notice = store.Messages(fixture.Sender.Address).Single(item => item.Message.Sender.SessionId == BrokerStore.NoticeSessionId);
        Check(notice.Message.Sender.SessionId == BrokerStore.NoticeSessionId && notice.Message.RemainingReplies == 0 &&
            notice.Message.ExpiresAt == clock.Utc + BrokerLimits.MaximumLifetime - TimeSpan.FromSeconds(10) &&
            notice.Message.Text.Contains(first.Message.Id.ToString()) && !notice.Message.Text.Contains(first.Message.Text), "WAITING_NOTICE_SHAPE");
        Check(notice.Message.Text.Contains("latest reason: recipient_busy") && notice.Message.Text.Contains("resets on worker restart") &&
            notice.Message.Text.Contains("get_message_receipt") && notice.Message.Text.Contains("Do not resend automatically") &&
            notice.Message.Text.Contains("not delivery or work completion"), "WAITING_NOTICE_REQUIRED_TEXT");
        store.RecordRecipientReceipt(notice.Message.Id, fixture.Sender.Address, store.BrokerId, MessageStatus.Delivered);
        fixture.Tick(TimeSpan.FromSeconds(10));
        Check(fixture.Notices() == 1, "WAITING_ACK_DEDUP");
        Check(store.ReadMessageWait(first) is { } mainWait &&
            mainWait.Waits.Single() is { Reason: "recipient_busy", Source: "directory" } reason && reason.Count == fixture.DirectoryPasses &&
            reason.FirstObservedAtUtc == firstObserved && reason.LastObservedAtUtc == clock.Utc && mainWait.FirstBusyObservedAtUtc == firstObserved &&
            store.ReadWaitingMail().Sessions.Single(row => row.Session == fixture.Target.Address).OldestBusyObservedAtUtc == firstObserved,
            "WAITING_REASON_FIRST_LAST");

        using (var f = new Fixture(Path.Combine(root, "waiting-notice-domain")))
        {
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero);
            for (int i = 0; i < 30; i++) f.Tick(TimeSpan.FromSeconds(10));
            var advisory = f.Store.Messages(f.Sender.Address).Single(item => item.Message.Sender.SessionId == BrokerStore.NoticeSessionId);
            f.Clock.Advance(TimeSpan.FromHours(4)); f.Renew();
            f.Store.FindReceipt(receipt.Message.Id); // Materialize the distinct terminal Expired event after the advisory.
            var notices = f.Store.Messages(f.Sender.Address).Where(item => item.Message.Sender.SessionId == BrokerStore.NoticeSessionId).ToArray();
            Check(BrokerStore.IsNoticeId(advisory.Message.Id) && advisory.Message.Id != BrokerStore.NoticeId(receipt.Message.Id) &&
                notices.Length == 2 && notices.Select(item => item.Message.Id).Distinct().Count() == 2 &&
                notices.Any(item => item.Message.Id == BrokerStore.NoticeId(receipt.Message.Id)), "WAITING_NOTICE_DOMAIN_AND_TERMINAL");
        }

        // Wall time can move independently: keep the immutable message deadline and live leases meaningful.
        using (var f = new Fixture(Path.Combine(root, "waiting-clock")))
        {
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero);
            f.Clock.Utc += TimeSpan.FromHours(1); f.Renew();
            f.Store.AdmitDueWaitingMailNotices();
            Check(f.Notices() == 0 && f.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds == 0, "WAITING_UTC_JUMP_NOT_THRESHOLD");
            f.Clock.Utc -= TimeSpan.FromHours(2); f.Renew();
            for (int i = 0; i < 30; i++) f.Tick(TimeSpan.FromSeconds(10));
            Check(f.Notices() == 1, "WAITING_MONOTONIC_AFTER_BACKWARD_UTC");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-new")))
        {
            var older = f.Accept(); f.Tick(TimeSpan.Zero);
            for (int i = 0; i < 31; i++) f.Tick(TimeSpan.FromSeconds(10));
            var receipt = f.Accept(createdAt: f.Clock.Utc.AddSeconds(-60)); f.Tick(TimeSpan.Zero);
            bool noBorrowAtAdmission = f.Notices() == 1 && f.Store.ReadMessageWait(older)?.BusyObservedAgeMilliseconds == 310000 &&
                f.Store.ReadMessageWait(receipt) is { BusyObservedAgeMilliseconds: 0 } admitted && admitted.FirstBusyObservedAtUtc == f.Clock.Utc;
            for (int i = 0; i < 29; i++) f.Tick(TimeSpan.FromSeconds(10));
            bool notBeforeOwnThreshold = f.Notices() == 1 && f.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds == 290000;
            f.Tick(TimeSpan.FromSeconds(10));
            Check(noBorrowAtAdmission && notBeforeOwnThreshold && f.Notices() == 2 &&
                f.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds == 300000, "WAITING_NO_RECIPIENT_AGE_BORROW");
            f.Clock.Advance(TimeSpan.FromMinutes(6)); f.Renew(); f.Store.AdmitDueWaitingMailNotices();
            Check(f.Notices() == 2 && f.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds == 0, "WAITING_STALE_GAP_RESETS");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-fresh-boundary")))
        {
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero);
            f.Tick(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
            Check(f.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds == 29999, "WAITING_FRESH_UNTIL_THIRTY_SECONDS");
            f.Tick(TimeSpan.FromSeconds(30));
            Check(f.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds == 0, "WAITING_FRESH_EXACT_THIRTY_SECONDS_RESTARTS");
        }
        foreach (var activity in new[] { SessionActivity.ApprovalRequired, SessionActivity.Interrupted, SessionActivity.Unknown, SessionActivity.Idle })
        {
            using var f = new Fixture(Path.Combine(root, "waiting-nonbusy-" + activity));
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero); // Establish a real Busy episode before the non-busy pause.
            f.Target = f.Target with { Activity = activity };
            for (int i = 0; i <= 31; i++) f.Tick(TimeSpan.FromSeconds(10));
            Check(f.Notices() == 0 && f.Store.ReadWaitingMail().Sessions.Single(row => row.Session == f.Target.Address).BusyCount == 0, "WAITING_NONBUSY_" + activity);
            if (activity != SessionActivity.Idle)
            {
                f.Target = f.Target with { Activity = SessionActivity.Busy };
                f.Tick(TimeSpan.FromSeconds(10));
                Check(f.Notices() == 0 && f.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds == 0,
                    "WAITING_NONBUSY_THEN_BUSY_" + activity);
            }
        }
        foreach (string code in new[] { "native_recipient_awaiting_approval", "native_recipient_activity_unknown", "native_metadata_unavailable", "native_transport_unavailable" })
        {
            using var f = new Fixture(Path.Combine(root, "waiting-adapter-nonbusy-" + code), adapter: "codex-native");
            f.Target = f.Target with { Activity = SessionActivity.Unknown, Capabilities = f.Target.Capabilities | SessionCapabilities.NativeIdleGuard };
            var receipt = f.Accept();
            for (int i = 0; i <= 31; i++)
            {
                if (i > 0) f.Clock.Advance(TimeSpan.FromSeconds(10));
                f.Renew(); f.Store.AdmitDueWaitingMailNotices();
                var claim = f.Store.BeginDeliveryClaim() ?? throw new InvalidOperationException("WAITING_NONBUSY_CLAIM_SETUP");
                if (!f.Store.TryReleaseUnsubmittedClaim(claim, code, out _)) throw new InvalidOperationException("WAITING_NONBUSY_RELEASE_SETUP");
            }
            f.Store.AdmitDueWaitingMailNotices();
            Check(f.Notices() == 0 && f.Store.ReadWaitingMail().Sessions.Single(row => row.Session == f.Target.Address).BusyCount == 0 &&
                f.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds is null, "WAITING_ADAPTER_NONBUSY_" + code);
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-directory-unavailable")))
        {
            var receipt = f.Accept();
            for (int i = 0; i <= 31; i++)
            {
                f.Clock.Advance(TimeSpan.FromSeconds(10));
                f.Store.AdmitDueWaitingMailNotices(); // Deliberately do not renew the one-minute native lease.
            }
            Check(f.Notices() == 0 && f.Store.ReadMessageWait(receipt) is { LatestReason: "recipient_unavailable", BusyObservedAgeMilliseconds: null },
                "WAITING_DIRECTORY_UNAVAILABLE_REASON");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-expiry-barrier")))
        {
            f.Accept(expiresAt: f.Clock.Utc.AddMinutes(5)); f.Tick(TimeSpan.Zero);
            for (int i = 0; i < 29; i++) f.Tick(TimeSpan.FromSeconds(10));
            f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Renew();
            var atDeadline = f.Store.AdmitDueWaitingMailNotices();
            f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Renew();
            var afterDeadline = f.Store.AdmitDueWaitingMailNotices();
            // No Messages/FindReceipt call may expire the Accepted source before these two admission attempts.
            Check(atDeadline.Admitted == 0 && afterDeadline.Admitted == 0, "WAITING_EXPIRED_SOURCE_NOT_ADMITTED");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-race")))
        {
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero);
            for (int i = 0; i < 29; i++) f.Tick(TimeSpan.FromSeconds(10));
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            f.Target = f.Target with { Activity = SessionActivity.Idle }; f.Renew();
            f.Store.AdmitDueWaitingMailNotices();
            Check(f.Notices() == 0, "WAITING_IDLE_BEFORE_CLAIM");
            f.Target = f.Target with { Activity = SessionActivity.Busy }; f.Tick(TimeSpan.Zero);
            for (int i = 0; i < 29; i++) f.Tick(TimeSpan.FromSeconds(10));
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            f.Target = f.Target with { Capabilities = SessionCapabilities.WakeIdle }; f.Renew();
            f.Store.AdmitDueWaitingMailNotices();
            Check(f.Notices() == 0, "WAITING_RECEIVE_REVOKED");
            f.Target = f.Target with { Capabilities = SessionCapabilities.Receive | SessionCapabilities.WakeIdle }; f.Tick(TimeSpan.Zero);
            Check(f.Notices() == 0 && f.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds == 0,
                "WAITING_RECEIVE_REGAIN_RESTARTS");
            Check(f.Store.ReadMessageWait(receipt with { Message = receipt.Message with { ConversationId = Guid.NewGuid() } }) is null,
                "WAITING_EXACT_EXPECTED_ADMISSION");
            Check(f.Store.ReadMessageWait(receipt with { AdmissionEpoch = Guid.NewGuid() }) is null, "WAITING_EXPECTED_EPOCH_ONLY");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-read-activity")))
        {
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero);
            f.Target = f.Target with { Activity = SessionActivity.ApprovalRequired }; f.Renew();
            Check(f.Store.ReadWaitingMail().Sessions.Single(row => row.Session == f.Target.Address).BusyCount == 0 &&
                f.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds is null, "WAITING_READ_RECHECKS_APPROVAL");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-oldest-age")))
        {
            var older = f.Accept(); f.Tick(TimeSpan.Zero);
            for (int i = 0; i < 6; i++) f.Tick(TimeSpan.FromSeconds(10));
            f.Accept(); f.Tick(TimeSpan.Zero);
            var row = f.Store.ReadWaitingMail().Sessions.Single(item => item.Session == f.Target.Address);
            Check(row.BusyCount == 2 && row.OldestBusyObservedAgeMilliseconds == 60000 &&
                row.OldestBusyObservedAtUtc == f.Store.ReadMessageWait(older)?.FirstBusyObservedAtUtc, "WAITING_OLDEST_BUSY_USES_OLDER_MESSAGE");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-adapter"), adapter: "codex-native"))
        {
            f.Target = f.Target with { Activity = SessionActivity.Unknown,
                Capabilities = f.Target.Capabilities | SessionCapabilities.NativeIdleGuard }; f.Renew();
            var receipt = f.Accept();
            for (int i = 0; i <= 30; i++)
            {
                if (i > 0) f.Clock.Advance(TimeSpan.FromSeconds(10));
                f.Renew(); f.Store.AdmitDueWaitingMailNotices();
                var claim = f.Store.BeginDeliveryClaim()!;
                if (i == 0)
                    Check(f.Store.ReadWaitingMail().Sessions.Single(row => row.Session == f.Target.Address) is { PendingCount: 1, QueuedCount: 0 },
                        "WAITING_PENDING_DIFFERS_FROM_QUEUED");
                Check(f.Store.TryReleaseUnsubmittedClaim(claim, "native_context_busy", out _), "WAITING_ADAPTER_MATCHED_RELEASE");
            }
            Check(f.Notices() == 1, "WAITING_UNKNOWN_FRESH_ADAPTER_BUSY");
            var oldClaim = f.Store.BeginDeliveryClaim()!;
            f.Store.TryReleaseUnsubmittedClaim(oldClaim, "native_metadata_unavailable", out _);
            Check(f.Store.ReadMessageWait(receipt) is { BusyObservedAgeMilliseconds: null } history &&
                history.LatestReason == "native_metadata_unavailable" && history.LatestSource == "adapter" &&
                history.Waits.Any(wait => wait.Reason == "native_metadata_unavailable") && history.Waits.Any(wait => wait.Reason == "native_context_busy"),
                "WAITING_FIXED_REASON_DISTINCTIONS");
            Check(!f.Store.TryReleaseUnsubmittedClaim(oldClaim, "native_recipient_busy", out _) &&
                f.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds is null, "WAITING_STALE_CLAIM_NO_OBSERVATION");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-unknown-code")))
        {
            f.Target = f.Target with { Activity = SessionActivity.Unknown, Capabilities = f.Target.Capabilities | SessionCapabilities.NativeIdleGuard }; f.Renew();
            var receipt = f.Accept();
            foreach (string? code in new string?[] { "native_future_vendor_code", null })
            {
                var claim = f.Store.BeginDeliveryClaim() ?? throw new InvalidOperationException("WAITING_UNKNOWN_CODE_CLAIM_SETUP");
                if (!f.Store.TryReleaseUnsubmittedClaim(claim, code, out _)) throw new InvalidOperationException("WAITING_UNKNOWN_CODE_RELEASE_SETUP");
            }
            Check(f.Store.ReadMessageWait(receipt) is { LatestReason: "other_deferred", LatestSource: "adapter" } history &&
                history.Waits is [{ Reason: "other_deferred", Source: "adapter", Count: 2 }], "WAITING_UNKNOWN_CODE_MAPPED");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-write")))
        {
            f.Accept(); f.Tick(TimeSpan.Zero);
            for (int i = 0; i < 29; i++) f.Tick(TimeSpan.FromSeconds(10));
            f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Renew();
            string temporary = Path.Combine(f.Path, "broker-state.json.next"); Directory.CreateDirectory(temporary);
            bool failed = false;
            try { f.Store.AdmitDueWaitingMailNotices(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
            finally { Directory.Delete(temporary); }
            Check(failed && f.Notices() == 0, "WAITING_WRITE_FAILURE_NOT_PUBLISHED");
            f.Store.AdmitDueWaitingMailNotices();
            Check(f.Notices() == 1, "WAITING_WRITE_FAILURE_RETRY");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-resolved")))
        {
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero);
            f.Store.RecordRecipientReceipt(receipt.Message.Id, f.Target.Address, f.Store.BrokerId, MessageStatus.Delivered);
            Check(f.Store.ReadMessageWait(receipt) is null && f.Store.ReadWaitingMail().Sessions.Single(row => row.Session == f.Target.Address).PendingCount == 0, "WAITING_RESOLVED_PRUNED");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-unknown-transition")))
        {
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero);
            for (int i = 0; i < 29; i++) f.Tick(TimeSpan.FromSeconds(10));
            f.Target = f.Target with { Activity = SessionActivity.Unknown, Capabilities = f.Target.Capabilities | SessionCapabilities.NativeIdleGuard };
            f.Tick(TimeSpan.Zero);
            f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Renew();
            var claim = f.Store.BeginDeliveryClaim()!;
            f.Store.TryReleaseUnsubmittedClaim(claim, "native_context_busy", out _);
            f.Store.AdmitDueWaitingMailNotices();
            Check(f.Notices() == 0 && f.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds == 0,
                "WAITING_DIRECTORY_TO_UNKNOWN_RESTARTS");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-cap"), new BrokerLimits(MaximumMessages: 2)))
        {
            var one = f.Accept(); var two = f.Accept(); f.Tick(TimeSpan.Zero);
            var cache = (System.Collections.IDictionary)typeof(BrokerStore).GetField("waitingMail",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Store)!;
            Check(cache.Count == 2, "WAITING_CACHE_BOUND");
            f.Store.RecordRecipientReceipt(one.Message.Id, f.Target.Address, f.Store.BrokerId, MessageStatus.Delivered);
            Check(cache.Count == 1 && f.Store.ReadMessageWait(one) is null && f.Store.ReadMessageWait(two) is not null, "WAITING_CACHE_HISTORY_PRUNED");
            for (int i = 0; i < 30; i++) f.Tick(TimeSpan.FromSeconds(10));
            Check(f.Notices() == 1 && cache.Count == 1, "WAITING_CACHE_NOTICE_NOT_TRACKED");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-batch")))
        {
            for (int i = 0; i < 17; i++) f.Accept();
            f.Tick(TimeSpan.Zero); for (int i = 0; i < 29; i++) f.Tick(TimeSpan.FromSeconds(10));
            f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Renew();
            long commits = f.Store.CommitTimings().Completed;
            var firstPass = f.Store.AdmitDueWaitingMailNotices();
            Check(firstPass.Admitted == 16 && f.Notices() == 16 && f.Store.CommitTimings().Completed == commits + 1, "WAITING_BATCH_ONE_COMMIT");
            var nextPass = f.Store.AdmitDueWaitingMailNotices();
            Check(nextPass.Admitted == 1 && f.Notices() == 17 && f.Store.CommitTimings().Completed == commits + 2, "WAITING_BATCH_REMAINDER");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-reuse")))
        {
            var old = f.Accept(); f.Tick(TimeSpan.Zero);
            f.Clock.Advance(TimeSpan.FromDays(3)); f.Renew();
            f.Store.BeginDelivery(); // Establish actual full-record retention removal before reusing the UUID.
            var fresh = f.Store.Accept(old.Message with { CreatedAt = f.Clock.Utc, ExpiresAt = f.Clock.Utc.AddHours(4) }, f.Store.BrokerId);
            f.Tick(TimeSpan.Zero);
            Check(f.Store.ReadMessageWait(old) is null && f.Store.ReadMessageWait(fresh)?.BusyObservedAgeMilliseconds == 0, "WAITING_REUSED_UUID_NEW_ADMISSION");
        }
        // Ordinary notice records/tombstones, not a new persisted wait schema, deduplicate after restart.
        string restart = Path.Combine(root, "waiting-restart");
        BrokerSession sender, target; Clock restartClock;
        using (var f = new Fixture(restart))
        {
            f.Accept(); f.Tick(TimeSpan.Zero); for (int i = 0; i < 30; i++) f.Tick(TimeSpan.FromSeconds(10));
            sender = f.Sender; target = f.Target; restartClock = f.Clock;
            var n = f.Store.Messages(sender.Address).Single(item => item.Message.Sender.SessionId == BrokerStore.NoticeSessionId);
            f.Store.RecordRecipientReceipt(n.Message.Id, sender.Address, f.Store.BrokerId, MessageStatus.Delivered);
        }
        using (var reopened = new BrokerStore(restart, clock: restartClock))
        {
            for (int i = 0; i <= 30; i++)
            {
                restartClock.Advance(TimeSpan.FromSeconds(10));
                reopened.Register(sender with { LeaseUntil = restartClock.Utc.AddMinutes(1) });
                reopened.Register(target with { LeaseUntil = restartClock.Utc.AddMinutes(1) });
                reopened.AdmitDueWaitingMailNotices();
            }
            Check(reopened.Messages(sender.Address).Count(item => item.Message.Sender.SessionId == BrokerStore.NoticeSessionId) == 1, "WAITING_RESTART_DURABLE_DEDUP");
        }
        return passed;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Path;
        internal readonly Clock Clock = new();
        internal readonly BrokerStore Store;
        internal long DirectoryPasses;
        internal BrokerSession Sender, Target;
        internal Fixture(string path, BrokerLimits? limits = null, string adapter = "claude-native")
        {
            Path = path; Store = new(path, limits, Clock);
            Sender = new(new(Store.BrokerId, "mcp:waiting-sender", Guid.NewGuid()), "Synthetic sender", "mcp",
                SessionCapabilities.Receive | SessionCapabilities.Reply, SessionActivity.Idle, Clock.Utc.AddMinutes(1));
            Target = new(new(Store.BrokerId, (adapter == "codex-native" ? "codex:" : "claude:") + "waiting-target", Guid.NewGuid()), "Synthetic target", adapter,
                SessionCapabilities.Receive | SessionCapabilities.WakeIdle, SessionActivity.Busy, Clock.Utc.AddMinutes(1));
            Renew();
        }
        internal void Renew() { Store.Register(Sender with { LeaseUntil = Clock.Utc.AddMinutes(1) }); Store.Register(Target with { LeaseUntil = Clock.Utc.AddMinutes(1) }); }
        internal MessageReceipt Accept(DateTimeOffset? createdAt = null, DateTimeOffset? expiresAt = null) => Store.Accept(new(Guid.NewGuid(), Guid.NewGuid(), null, Sender.Address, Target.Address,
            "A synthetic waiting request.", createdAt ?? Clock.Utc, expiresAt ?? Clock.Utc.AddHours(4)), Store.BrokerId);
        internal void Tick(TimeSpan elapsed) { Clock.Advance(elapsed); Renew(); Store.AdmitDueWaitingMailNotices(); DirectoryPasses++; }
        internal int Notices() => Store.Messages(Sender.Address).Count(item => item.Message.Sender.SessionId == BrokerStore.NoticeSessionId);
        public void Dispose() => Store.Dispose();
    }
}
