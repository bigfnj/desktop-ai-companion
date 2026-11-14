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
        using (var f = new Fixture(Path.Combine(root, "waiting-directory-rate")))
        {
            var receipt = f.Accept(); var observedAt = f.Clock.Utc;
            f.Tick(TimeSpan.Zero);
            for (int i = 0; i < 39; i++) f.Tick(TimeSpan.FromMilliseconds(20));
            Check(f.Store.ReadMessageWait(receipt) is { Waits: [{ Reason: "recipient_busy", Source: "directory", Count: 1 }] } rate &&
                rate.Waits[0].FirstObservedAtUtc == observedAt && rate.Waits[0].LastObservedAtUtc == observedAt,
                "WAITING_DIRECTORY_RATE_BOUND");
            f.Tick(TimeSpan.FromSeconds(2) - TimeSpan.FromMilliseconds(780) - TimeSpan.FromTicks(1));
            Check(f.Store.ReadMessageWait(receipt)?.Waits.Single().Count == 1, "WAITING_DIRECTORY_BEFORE_INTERVAL");
            f.Tick(TimeSpan.FromTicks(1));
            Check(f.Store.ReadMessageWait(receipt) is { BusyObservedAgeMilliseconds: 2000 } atInterval &&
                atInterval.Waits.Single().Count == 2 && atInterval.Waits[0].LastObservedAtUtc == f.Clock.Utc &&
                atInterval.FirstBusyObservedAtUtc == observedAt, "WAITING_DIRECTORY_AT_INTERVAL");
        }
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
        // The 300-second pass follows the 299.9999999-second observation by one tick and is the one throttled pass.
        // All other directory passes below are at least ten seconds apart: 34 passes therefore retain 33 observations.
        Check(store.ReadMessageWait(first) is { } mainWait &&
            mainWait.Waits.Single() is { Reason: "recipient_busy", Source: "directory" } reason && reason.Count == 33 && fixture.DirectoryPasses == 34 &&
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
        // Inspect write-time revocation before the later read-time/notice policy checks can mask it.
        using (var f = new Fixture(Path.Combine(root, "waiting-directory-policy")))
        {
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero);
            f.Target = f.Target with { Capabilities = SessionCapabilities.Receive };
            f.Tick(TimeSpan.FromMilliseconds(200));
            Check(BusyEpisodeEnded(f.Store, receipt) && f.Store.ReadMessageWait(receipt)?.BusyObservedAgeMilliseconds is null,
                "WAITING_DIRECTORY_POLICY_REVOKED_WITHIN_INTERVAL");
            f.Target = f.Target with { Capabilities = SessionCapabilities.Receive | SessionCapabilities.WakeIdle };
            f.Tick(TimeSpan.FromMilliseconds(200));
            Check(f.Store.ReadMessageWait(receipt) is { BusyObservedAgeMilliseconds: 0 } regained &&
                regained.FirstBusyObservedAtUtc == f.Clock.Utc && regained.Waits.Single().Count == 1,
                "WAITING_DIRECTORY_POLICY_REGAIN_RESTARTS");
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
        // Reason-change counting is independent of the write-time policy revocation/regain checks above.
        using (var f = new Fixture(Path.Combine(root, "waiting-directory-transition")))
        {
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero);
            f.Target = f.Target with { Activity = SessionActivity.ApprovalRequired };
            f.Tick(TimeSpan.FromMilliseconds(500));
            Check(f.Store.ReadMessageWait(receipt) is { LatestReason: "recipient_awaiting_approval", BusyObservedAgeMilliseconds: null } changed &&
                changed.Waits.Any(wait => wait.Reason == "recipient_awaiting_approval" && wait.Count == 1),
                "WAITING_DIRECTORY_REASON_CHANGE_ENDS_BUSY");
            f.Target = f.Target with { Activity = SessionActivity.Busy };
            f.Tick(TimeSpan.FromMilliseconds(500));
            Check(f.Store.ReadMessageWait(receipt) is { LatestReason: "recipient_busy", BusyObservedAgeMilliseconds: 0 } resumed &&
                resumed.Waits.Single(wait => wait.Reason == "recipient_busy").Count == 2,
                "WAITING_DIRECTORY_REASON_CHANGE_RESTARTS_BUSY");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-directory-throttled-freshness")))
        {
            var receipt = f.Accept(); var firstAt = f.Clock.Utc;
            f.Tick(TimeSpan.Zero);
            f.Tick(TimeSpan.FromSeconds(1)); // A fresh observation within the two-second counting interval.
            f.Clock.Advance(TimeSpan.FromSeconds(29)); f.Renew();
            Check(f.Store.ReadMessageWait(receipt) is { BusyObservedAgeMilliseconds: 30000 } fresh &&
                fresh.FirstBusyObservedAtUtc == firstAt && fresh.Waits.Single() is { Count: 1 } counted &&
                counted.FirstObservedAtUtc == firstAt && counted.LastObservedAtUtc == firstAt,
                "WAITING_DIRECTORY_THROTTLED_REFRESHES_FRESHNESS");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-adapter-rate"), adapter: "codex-native"))
        {
            f.Target = f.Target with { Activity = SessionActivity.Unknown,
                Capabilities = f.Target.Capabilities | SessionCapabilities.NativeIdleGuard }; f.Renew();
            var receipt = f.Accept();
            for (int i = 0; i < 40; i++)
            {
                var claim = f.Store.BeginDeliveryClaim() ?? throw new InvalidOperationException("WAITING_ADAPTER_RATE_CLAIM_SETUP");
                if (!f.Store.TryReleaseUnsubmittedClaim(claim, "native_context_busy", out _))
                    throw new InvalidOperationException("WAITING_ADAPTER_RATE_RELEASE_SETUP");
            }
            Check(f.Store.ReadMessageWait(receipt) is { Waits: [{ Source: "adapter", Count: 40 }] },
                "WAITING_NON_DIRECTORY_NOT_THROTTLED");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-directory-regression")))
        {
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero); f.Tick(TimeSpan.FromSeconds(2));
            f.Clock.Timestamp = TimeSpan.FromSeconds(1).Ticks;
            f.Tick(TimeSpan.Zero);
            Check(f.Store.ReadMessageWait(receipt) is { BusyObservedAgeMilliseconds: 0 } reset && reset.Waits.Single().Count == 3 &&
                reset.FirstBusyObservedAtUtc == f.Clock.Utc, "WAITING_DIRECTORY_NEGATIVE_ELAPSED_REOBSERVED");
        }
        A7BehaviorChecks(root, Check);
        // The directory throttle compares with the LATEST reason/source, rather than a per-key timestamp.
        // Unknown without NativeIdleGuard is not Ready. A later discovery enabling the guard makes
        // this same row claimable; the matched adapter release then interrupts consecutive directory observations.
        using (var f = new Fixture(Path.Combine(root, "waiting-directory-adapter-interleaving"), adapter: "codex-native"))
        {
            var receipt = f.Accept();
            var firstAt = f.Clock.Utc;
            for (int i = 0; i < 40; i++)
            {
                f.Target = f.Target with { Activity = SessionActivity.Unknown,
                    Capabilities = f.Target.Capabilities & ~SessionCapabilities.NativeIdleGuard };
                f.Tick(i == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(20));
                f.Target = f.Target with { Capabilities = f.Target.Capabilities | SessionCapabilities.NativeIdleGuard };
                f.Renew();
                var claim = f.Store.BeginDeliveryClaim() ?? throw new InvalidOperationException("WAITING_ALTERNATING_CLAIM_SETUP");
                if (!f.Store.TryReleaseUnsubmittedClaim(claim, "native_context_busy", out _))
                    throw new InvalidOperationException("WAITING_ALTERNATING_RELEASE_SETUP");
            }
            var history = f.Store.ReadMessageWait(receipt);
            Check(history is { LatestReason: "native_context_busy", LatestSource: "adapter", BusyObservedAgeMilliseconds: 0 } &&
                history.Waits.Length == 2 &&
                history.Waits.Single(wait => wait.Source == "directory") is
                    { Reason: "recipient_activity_unknown", Count: 40 } directory &&
                directory.FirstObservedAtUtc == firstAt && directory.LastObservedAtUtc == f.Clock.Utc &&
                history.Waits.Single(wait => wait.Source == "adapter") is
                    { Reason: "native_context_busy", Count: 40 } adapter &&
                adapter.FirstObservedAtUtc == firstAt && adapter.LastObservedAtUtc == f.Clock.Utc &&
                f.DirectoryPasses == 40 && f.Notices() == 0,
                "WAITING_DIRECTORY_ADAPTER_INTERLEAVING_REARMS_COUNT");
        }
        return passed;
    }
    private static void A7BehaviorChecks(string root, Action<bool, string> check)
    {
        using (var f = new Fixture(Path.Combine(root, "waiting-a7-ready-per-target")))
        {
            f.Target = f.Target with { Activity = SessionActivity.Idle }; f.Renew();
            f.Accept(); // The first native target is Ready, so a shared memo would incorrectly ready the Busy target too.
            var busy = f.Target with { Address = f.Target.Address with { SessionId = f.Target.Address.SessionId + "-busy" },
                Activity = SessionActivity.Busy };
            f.Store.Register(busy);
            f.Store.Accept(new(Guid.NewGuid(), Guid.NewGuid(), null, f.Sender.Address, busy.Address,
                "A synthetic second-target waiting request.", f.Clock.Utc, f.Clock.Utc.AddHours(4)), f.Store.BrokerId);
            var rows = f.Store.ReadWaitingMail().Sessions;
            check(rows.Single(row => row.Session == f.Target.Address) is
                    { PendingCount: 1, QueuedCount: 1, BlockedCount: 0, BusyCount: 0 } &&
                rows.Single(row => row.Session == busy.Address) is
                    { PendingCount: 1, QueuedCount: 1, BlockedCount: 1, BusyCount: 0 },
                "WAITING_SUMMARY_READY_IS_PER_TARGET");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-a7-equal-value")))
        {
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero);
            var before = f.Store.ReadMessageWait(receipt)!;
            var replacement = ReplaceActual(f.Store, receipt, actual => actual with
            {
                Message = actual.Message with
                {
                    Sender = actual.Message.Sender with { },
                    Recipient = actual.Message.Recipient with { },
                    CreatedAt = actual.Message.CreatedAt.ToOffset(TimeSpan.FromHours(2)),
                    ExpiresAt = actual.Message.ExpiresAt.ToOffset(TimeSpan.FromHours(-3))
                }
            });
            check(replacement.AdmissionEpoch == receipt.AdmissionEpoch &&
                SameWait(before, f.Store.ReadMessageWait(replacement)), "WAITING_IDENTITY_EQUAL_VALUES_KEEP_HISTORY");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-a7-text")))
        {
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero);
            var before = f.Store.ReadMessageWait(receipt)!;
            var replacement = ReplaceActual(f.Store, receipt, actual => actual with
                { Message = actual.Message with { Text = actual.Message.Text + " A different body." } });
            check(SameWait(before, f.Store.ReadMessageWait(replacement)), "WAITING_IDENTITY_TEXT_CHANGE_KEEPS_HISTORY");
            check(f.Store.ReadMessageWait(receipt) is null, "WAITING_EXPECTED_OLD_TEXT_REFUSED");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-a7-actual-admission")))
        {
            var receipt = f.Accept(); f.Tick(TimeSpan.Zero);
            var replacement = ReplaceActual(f.Store, receipt, actual => actual with { AdmissionEpoch = Guid.NewGuid() });
            check(f.Store.ReadMessageWait(replacement) is null, "WAITING_ACTUAL_ADMISSION_CHANGE_INVALIDATES_HISTORY");
        }

        var axes = new (string Name, Func<BrokerMessage, BrokerMessage> Edit)[]
        {
            ("CONVERSATION", message => message with { ConversationId = Guid.NewGuid() }),
            ("REPLY_TO_FROM_NULL", message => message with { ReplyTo = Guid.NewGuid() }),
            ("REPLY_TO_CHANGED", message => message with { ReplyTo = Guid.NewGuid() }),
            ("SENDER_BROKER", message => message with { Sender = message.Sender with { BrokerId = Guid.NewGuid() } }),
            ("SENDER_SESSION", message => message with { Sender = message.Sender with { SessionId = message.Sender.SessionId + "-other" } }),
            ("SENDER_INCARNATION", message => message with { Sender = message.Sender with { Incarnation = Guid.NewGuid() } }),
            ("RECIPIENT_SESSION", message => message with { Recipient = message.Recipient with { SessionId = message.Recipient.SessionId + "-other" } }),
            ("RECIPIENT_INCARNATION", message => message with { Recipient = message.Recipient with { Incarnation = Guid.NewGuid() } }),
            ("CREATED_AT", message => message with { CreatedAt = message.CreatedAt.AddSeconds(-1) }),
            ("EXPIRES_AT", message => message with { ExpiresAt = message.ExpiresAt.AddMinutes(1) }),
            ("REMAINING_REPLIES", message => message with { RemainingReplies = message.RemainingReplies - 1 })
        };
        foreach (var axis in axes)
        {
            using var f = new Fixture(Path.Combine(root, "waiting-a7-identity-" + axis.Name));
            var receipt = f.Accept();
            if (axis.Name == "REPLY_TO_CHANGED")
                receipt = ReplaceActual(f.Store, receipt, actual => actual with { Message = actual.Message with { ReplyTo = Guid.NewGuid() } });
            f.Tick(TimeSpan.Zero);
            var edited = axis.Edit(receipt.Message);
            if (edited.Recipient != receipt.Message.Recipient)
                f.Store.Register(f.Target with { Address = edited.Recipient, LeaseUntil = f.Clock.Utc.AddMinutes(1) });
            var replacement = ReplaceActual(f.Store, receipt, actual => actual with { Message = edited });
            check(f.Store.ReadMessageWait(replacement) is null, "WAITING_IDENTITY_" + axis.Name + "_INVALIDATES_HISTORY");
        }

        using (var f = new Fixture(Path.Combine(root, "waiting-a7-recipient-incarnation")))
        {
            f.Accept();
            f.Target = f.Target with { Address = f.Target.Address with { Incarnation = Guid.NewGuid() } };
            f.Renew();
            f.Accept(); f.Accept();
            // Registration alone neither resolves the old pending message nor supplies a discovery replacement proof.
            var snapshot = f.Store.ReadWaitingMail();
            SessionWaitingMail[] expected =
            [
                new(f.Sender.Address, 0, 0, 0, 0, 0, null, null),
                new(f.Target.Address, 2, 2, 2, 0, 2, null, null)
            ];
            check(snapshot.Sessions.SequenceEqual(expected), "WAITING_SUMMARY_RECIPIENT_INCARNATION_IS_EXACT");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-a7-message-direction")))
        {
            f.Target = f.Target with { Capabilities = f.Target.Capabilities | SessionCapabilities.Reply }; f.Renew();
            f.Accept(); f.Accept();
            f.Store.Accept(new(Guid.NewGuid(), Guid.NewGuid(), null, f.Target.Address, f.Sender.Address,
                "A synthetic reverse-direction request.", f.Clock.Utc, f.Clock.Utc.AddHours(4)), f.Store.BrokerId);
            var snapshot = f.Store.ReadWaitingMail();
            SessionWaitingMail[] expected =
            [
                new(f.Sender.Address, 1, 1, 0, 0, 0, null, null),
                new(f.Target.Address, 2, 2, 2, 0, 2, null, null)
            ];
            check(snapshot.BrokerId == f.Store.BrokerId && snapshot.ObservedAtUtc == f.Clock.Utc &&
                snapshot.NoticeAfterSeconds == 300 && snapshot.Sessions.SequenceEqual(expected),
                "WAITING_SUMMARY_RECIPIENT_NOT_SENDER");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-a7-statuses")))
        {
            // Distinct weights prevent a swapped included/excluded state from cancelling in the total.
            var statusCounts = new (MessageStatus Status, int Count)[]
            {
                (MessageStatus.Accepted, 2), (MessageStatus.Submitting, 3),
                (MessageStatus.Submitted, 5), (MessageStatus.Uncertain, 7),
                (MessageStatus.Held, 11), (MessageStatus.Delivered, 1),
                (MessageStatus.Refused, 1), (MessageStatus.Expired, 1)
            };
            var receipts = new List<(MessageReceipt Receipt, MessageStatus Status)>();
            foreach (var entry in statusCounts)
                for (int i = 0; i < entry.Count; i++) receipts.Add((f.Accept(), entry.Status));
            f.Accept(expiresAt: f.Clock.Utc.AddTicks(1));
            f.Accept(expiresAt: f.Clock.Utc.AddTicks(2));
            // Seed every receipt before editing statuses: no later admission should compact the synthetic terminal rows.
            foreach (var entry in receipts)
                ReplaceActual(f.Store, entry.Receipt, actual => actual with { Status = entry.Status });
            f.Clock.Advance(TimeSpan.FromTicks(2)); // one past deadline and one exactly at it, both excluded
            var snapshot = f.Store.ReadWaitingMail();
            SessionWaitingMail[] expected =
            [
                new(f.Sender.Address, 0, 0, 0, 0, 0, null, null),
                new(f.Target.Address, 17, 2, 2, 0, 2, null, null)
            ];
            check(snapshot.Sessions.SequenceEqual(expected), "WAITING_SUMMARY_MIXED_STATES_AND_EXPIRY");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-a7-observed-nonbusy")))
        {
            f.Target = f.Target with { Activity = SessionActivity.ApprovalRequired }; f.Renew();
            f.Accept(); f.Tick(TimeSpan.Zero);
            f.Accept(); // no observation for this message
            var row = f.Store.ReadWaitingMail().Sessions.Single(item => item.Session == f.Target.Address);
            check(row is { PendingCount: 2, QueuedCount: 2, BlockedCount: 2, BusyCount: 0,
                UnobservedBlockedCount: 1, OldestBusyObservedAtUtc: null, OldestBusyObservedAgeMilliseconds: null },
                "WAITING_SUMMARY_FRESH_NONBUSY_IS_OBSERVED");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-a7-oldest-tie")))
        {
            f.Accept(); f.Tick(TimeSpan.Zero); var firstAt = f.Clock.Utc;
            f.Clock.Utc += TimeSpan.FromSeconds(7); f.Renew();
            f.Accept(); f.Tick(TimeSpan.Zero); // same monotonic time, distinct UTC observation
            var row = f.Store.ReadWaitingMail().Sessions.Single(item => item.Session == f.Target.Address);
            check(row.BusyCount == 2 && row.OldestBusyObservedAgeMilliseconds == 0 && row.OldestBusyObservedAtUtc == firstAt,
                "WAITING_SUMMARY_OLDEST_AGE_TIE_KEEPS_FIRST");
        }
        using (var f = new Fixture(Path.Combine(root, "waiting-a7-oldest-monotonic")))
        {
            f.Accept(); f.Tick(TimeSpan.Zero); var firstAt = f.Clock.Utc;
            f.Clock.Timestamp += TimeSpan.FromSeconds(10).Ticks;
            f.Clock.Utc -= TimeSpan.FromSeconds(5); f.Renew();
            f.Accept(); f.Tick(TimeSpan.Zero);
            var row = f.Store.ReadWaitingMail().Sessions.Single(item => item.Session == f.Target.Address);
            check(row.BusyCount == 2 && row.OldestBusyObservedAgeMilliseconds == 10000 && row.OldestBusyObservedAtUtc == firstAt,
                "WAITING_SUMMARY_OLDEST_USES_MONOTONIC_AGE");
        }
    }

    private static bool BusyEpisodeEnded(BrokerStore store, MessageReceipt receipt)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var cacheField = typeof(BrokerStore).GetField("waitingMail", flags) ??
            throw new InvalidOperationException("WAITING_BUSY_EPISODE_CACHE_SETUP");
        var cache = cacheField.GetValue(store) as System.Collections.IDictionary ??
            throw new InvalidOperationException("WAITING_BUSY_EPISODE_CACHE_SETUP");
        var observation = cache[receipt.Message.Id] ?? throw new InvalidOperationException("WAITING_BUSY_EPISODE_OBSERVATION_SETUP");
        foreach (var name in new[] { "BusyTimestamp", "BusyAt", "AdapterBusyTimestamp" })
        {
            var field = observation.GetType().GetField(name, flags) ??
                throw new InvalidOperationException("WAITING_BUSY_EPISODE_FIELD_SETUP: " + name);
            if (field.GetValue(observation) is not null) return false;
        }
        return true;
    }

    private static bool SameWait(MessageWaitView expected, MessageWaitView? actual) => actual is not null &&
        expected.MessageId == actual.MessageId && expected.LatestReason == actual.LatestReason &&
        expected.LatestSource == actual.LatestSource && expected.FirstBusyObservedAtUtc == actual.FirstBusyObservedAtUtc &&
        expected.BusyObservedAgeMilliseconds == actual.BusyObservedAgeMilliseconds && expected.Waits.SequenceEqual(actual.Waits);

    // All callers are isolated synchronous fixtures with no runtime loops; never mutate a live store this way.
    private static MessageReceipt ReplaceActual(BrokerStore store, MessageReceipt expected, Func<MessageReceipt, MessageReceipt> edit)
    {
        var state = typeof(BrokerStore).GetField("state",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        var messages = (List<MessageReceipt>)state.GetType().GetProperty("Messages")!.GetValue(state)!;
        int index = messages.FindIndex(item => item.Message.Id == expected.Message.Id);
        if (index < 0 || messages[index].AdmissionEpoch != expected.AdmissionEpoch)
            throw new InvalidOperationException("WAITING_IDENTITY_REPLACEMENT_SETUP");
        var replacement = edit(messages[index]); // receipt with-copies preserve the actual admission unless explicitly changed
        if (replacement.Message.Id != expected.Message.Id)
            throw new InvalidOperationException("WAITING_IDENTITY_REPLACEMENT_ID_SETUP");
        messages[index] = replacement;
        return replacement;
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
