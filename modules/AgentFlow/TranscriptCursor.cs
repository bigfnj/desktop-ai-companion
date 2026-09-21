using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DesktopAICompanion.AgentFlow
{
    /// <summary>
    /// Everything a transcript reader has to remember between records.
    ///
    /// Extracted so the whole-file reader and the incremental cursor can share ONE fold. That is
    /// the point: two implementations of "what does this record mean" would have to be kept in
    /// agreement by a test, and this way they cannot disagree, because there is only one. The
    /// differential test below then measures the thing that CAN differ -- where the byte stream was
    /// cut -- rather than re-deriving the parser twice.
    /// </summary>
    internal sealed class FoldState
    {
        public readonly Dictionary<string, OutstandingCall> Pending =
            new Dictionary<string, OutstandingCall>(StringComparer.Ordinal);

        /// <summary>Claude's `permissionMode`, or Codex's `approval_policy`. Carried forward
        /// POSITIONALLY: a Claude permission-mode record has no timestamp, so file order is the
        /// only ordering there is.</summary>
        public string Mode;
        public string Cwd;
        public bool SawAnyCall;

        /// <summary>Calls completed since the last snapshot drained this. Not the whole history:
        /// the module tallies each call id once and remembers that it has, so a completed call is
        /// only interesting on the tick it completes.</summary>
        public readonly List<OutstandingCall> CompletedSinceSnapshot = new List<OutstandingCall>();

        public void Reset()
        {
            Pending.Clear();
            CompletedSinceSnapshot.Clear();
            Mode = null;
            Cwd = null;
            SawAnyCall = false;
        }

        public void Complete(string id)
        {
            OutstandingCall finished;
            if (Pending.TryGetValue(id, out finished)) CompletedSinceSnapshot.Add(finished);
            Pending.Remove(id);
        }
    }

    /// <summary>
    /// One append-only transcript, read forward from wherever we stopped last time.
    ///
    /// THE PROBLEM THIS SOLVES. The reader used to open every active transcript at byte zero on
    /// every 10-second tick and re-parse the whole thing. Measured on the maintainer's box
    /// 2026-09-21: 44.7 MB and 15,947 records, re-read six times a minute, to learn about the few
    /// hundred bytes that had been appended. That is O(history) work for O(new bytes) of
    /// information, and it grew all day.
    ///
    /// WHY BYTES AND NOT StreamReader.ReadLine. A StreamReader buffers ahead, so its underlying
    /// stream Position after a line is not that line's end and cannot be stored as a resume point.
    /// This scans for the newline BYTE (0x0A) instead, which is safe in UTF-8 by construction: a
    /// continuation byte is 0x80-0xBF and a lead byte is 0xC0 or above, so 0x0A can never occur
    /// inside a multi-byte character. That is what makes "split the stream anywhere" safe, and it
    /// is why the mid-character case in the differential test can never fail rather than merely
    /// happening not to.
    ///
    /// THE OFFSET ONLY EVER ADVANCES PAST A COMPLETE LINE. The largest record measured on that box
    /// is 1.3 MB, which cannot be written atomically, so reading while the agent is mid-write is
    /// ordinary rather than exotic. A partial tail is left unconsumed and picked up next tick, well
    /// inside the 30-second stall threshold.
    /// </summary>
    internal sealed class TranscriptCursor
    {
        /// <summary>Read granularity. Large enough that a quiet tick is one syscall, small enough
        /// that a cold 44 MB read does not allocate 44 MB.</summary>
        private const int ChunkBytes = 64 * 1024;

        /// <summary>A guard, not a tuning knob: a single line longer than this is not a transcript
        /// record, it is a runaway or a corrupted file, and buffering it would be the bug.</summary>
        private const int MaxLineBytes = 16 * 1024 * 1024;

        /// <summary>How much of the file head identifies it. Enough to cover a whole first
        /// record's worth of distinguishing prefix without being a second read of note.</summary>
        private const int HeadBytes = 64;

        private readonly string _path;
        private readonly string _agent;
        private readonly FoldState _state = new FoldState();

        private long _offset;
        private DateTime _createdUtc;
        private bool _seenOnce;

        /// <summary>
        /// The first bytes of the file we are resuming into, so a replacement is detectable.
        ///
        /// Creation time alone is NOT enough, and believing it would be the subtle bug here. NTFS
        /// FILE TUNNELLING re-gives a recreated file its predecessor's creation timestamp when the
        /// name is reused within about fifteen seconds, so "same name, same creation time" does not
        /// mean "same file". A replacement that is also SHORTER is caught by the length check; one
        /// that is longer or equal would otherwise be resumed into at a stale offset, splicing the
        /// tail of one file onto the state of another.
        ///
        /// Compared only when we were going to open the file anyway, so it costs a seek and 64
        /// bytes rather than a syscall on an idle tick.
        /// </summary>
        private byte[] _head;

        internal TranscriptCursor(string path, string agent)
        {
            _path = path;
            _agent = agent;
        }

        internal string Path { get { return _path; } }

        /// <summary>Test seam: how far into the file the cursor has committed to.</summary>
        internal long Offset { get { return _offset; } }

        /// <summary>
        /// Fold in whatever has been appended, and hand back an immutable view of the session.
        ///
        /// The snapshot matters more than it looks. `Detection` holds a live reference to its
        /// `AgentSession` and that reference crosses to the UI thread, so handing out the cursor's
        /// own mutable state would have the poll worker rewriting an object the UI is reading.
        /// Everything returned here is either a fresh list or an immutable scalar.
        ///
        /// <paramref name="resetReason"/> is non-null when the cursor had to start over. Rare, and
        /// never silent: a reset means something happened to the file, and "AgentFlow forgot this
        /// session" with no reason recorded is indistinguishable from a bug.
        /// </summary>
        internal AgentSession Advance(out string resetReason)
        {
            resetReason = null;

            long length;
            DateTime createdUtc, writtenUtc;
            if (!Stat(out length, out createdUtc, out writtenUtc))
            {
                // Gone or unreadable. Report what we already folded rather than inventing an empty
                // session: a transcript that vanishes mid-poll has not un-happened.
                return Snapshot(writtenUtc);
            }

            if (_seenOnce)
            {
                if (length < _offset)
                    resetReason = "it was truncated (" + length.ToString(Culture) + " bytes, cursor was at "
                                  + _offset.ToString(Culture) + ")";
                else if (createdUtc != _createdUtc)
                    resetReason = "it was replaced (a different file now has this name)";
            }

            if (resetReason != null)
            {
                _state.Reset();
                _offset = 0;
                _head = null;
            }
            _createdUtc = createdUtc;
            _seenOnce = true;

            if (length > _offset)
            {
                string swapped = Consume(length);
                if (swapped != null && resetReason == null) resetReason = swapped;
            }
            return Snapshot(writtenUtc);
        }

        private static readonly System.Globalization.CultureInfo Culture =
            System.Globalization.CultureInfo.InvariantCulture;

        private bool Stat(out long length, out DateTime createdUtc, out DateTime writtenUtc)
        {
            length = 0;
            createdUtc = default(DateTime);
            writtenUtc = DateTime.UtcNow;
            try
            {
                var info = new FileInfo(_path);
                if (!info.Exists) return false;
                length = info.Length;
                createdUtc = info.CreationTimeUtc;
                writtenUtc = info.LastWriteTimeUtc;
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>Read from the committed offset to <paramref name="length"/>, folding whole
        /// lines and leaving any partial tail for next time.</summary>
        /// <summary>Returns a reset reason when the file turned out not to be the one we were
        /// resuming into; null on the ordinary path.</summary>
        private string Consume(long length)
        {
            string swapped = null;
            try
            {
                // ReadWrite share for the same reason the whole-file reader needs it: the agent
                // holds this file open for append, and a plain open would throw a sharing violation.
                using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                {
                    // Identity first, while the handle is open and it is nearly free.
                    byte[] head = ReadHead(stream, length);
                    if (_head != null && !SameHead(_head, head))
                    {
                        _state.Reset();
                        _offset = 0;
                        swapped = "its first bytes changed, so it is a different file reusing the name";
                    }
                    _head = head;

                    stream.Seek(_offset, SeekOrigin.Begin);

                    var chunk = new byte[ChunkBytes];
                    var line = new MemoryStream();
                    long consumed = 0;                 // bytes committed, always ending on a newline
                    long pendingBytes = 0;             // bytes buffered into `line`, not yet committed
                    long remaining = length - _offset;

                    while (remaining > 0)
                    {
                        int want = remaining < chunk.Length ? (int)remaining : chunk.Length;
                        int got = stream.Read(chunk, 0, want);
                        if (got <= 0) break;
                        remaining -= got;

                        for (int i = 0; i < got; i++)
                        {
                            if (chunk[i] != (byte)'\n')
                            {
                                // A line this long is not a record. Drop it rather than buffer it,
                                // and let the offset move past it so we do not re-read it for ever.
                                if (pendingBytes < MaxLineBytes) line.WriteByte(chunk[i]);
                                pendingBytes++;
                                continue;
                            }

                            if (pendingBytes <= MaxLineBytes) FoldLine(line);
                            line.SetLength(0);
                            consumed += pendingBytes + 1;   // the line plus its newline
                            pendingBytes = 0;
                        }
                    }

                    _offset += consumed;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return swapped;
        }

        private static byte[] ReadHead(FileStream stream, long length)
        {
            int want = length < HeadBytes ? (int)length : HeadBytes;
            var head = new byte[want];
            stream.Seek(0, SeekOrigin.Begin);
            int read = 0;
            while (read < want)
            {
                int got = stream.Read(head, read, want - read);
                if (got <= 0) break;
                read += got;
            }
            if (read == want) return head;
            var shorter = new byte[read];
            Array.Copy(head, shorter, read);
            return shorter;
        }

        private static bool SameHead(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            // A file that has GROWN past a short head is not a different file: compare only
            // the overlap, or every transcript would look replaced on its second read.
            int n = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < n; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private void FoldLine(MemoryStream line)
        {
            if (line.Length == 0) return;                      // blank line, as the old reader did
            string text;
            try { text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length); }
            catch (ArgumentException) { return; }
            if (text.Length > 0 && text[text.Length - 1] == '\r')
                text = text.Substring(0, text.Length - 1);
            if (text.Length == 0) return;

            JsonElement record;
            if (!TranscriptReader.TryParseForFold(text, out record)) return;
            if (_agent == TranscriptReader.AgentCodex) TranscriptReader.FoldCodexRecord(record, _state);
            else TranscriptReader.FoldClaudeRecord(record, _state);
        }

        private AgentSession Snapshot(DateTime writtenUtc)
        {
            var session = new AgentSession
            {
                Agent = _agent,
                Path = _path,
                SessionId = System.IO.Path.GetFileNameWithoutExtension(_path),
                Cwd = _state.Cwd,
                Mode = _state.Mode,
                SawAnyCall = _state.SawAnyCall,
                LastWriteUtc = writtenUtc,
            };
            foreach (OutstandingCall call in _state.Pending.Values) session.Outstanding.Add(call);
            foreach (OutstandingCall call in _state.CompletedSinceSnapshot) session.NoteCompleted(call);
            _state.CompletedSinceSnapshot.Clear();
            return session;
        }
    }

    /// <summary>
    /// The live cursors, one per transcript, pruned to whatever is still inside the active window.
    ///
    /// Bounded the same way every other set in this module is: against what the current tick can
    /// see on disk, not against process uptime. That is the idiom `_approvalsCounted` and
    /// `NotifyBudget.Retain` already use, and it is why a machine that has run a hundred sessions
    /// today holds cursors for the one or two still being written.
    /// </summary>
    internal sealed class SessionCache
    {
        private readonly Dictionary<string, TranscriptCursor> _cursors =
            new Dictionary<string, TranscriptCursor>(StringComparer.OrdinalIgnoreCase);

        internal int Count { get { return _cursors.Count; } }

        internal TranscriptCursor For(string path, string agent)
        {
            TranscriptCursor cursor;
            if (_cursors.TryGetValue(path, out cursor)) return cursor;
            cursor = new TranscriptCursor(path, agent);
            _cursors[path] = cursor;
            return cursor;
        }

        /// <summary>Drop cursors for transcripts no longer in the active window.</summary>
        internal void Retain(ICollection<string> livePaths)
        {
            if (livePaths == null) return;
            var drop = new List<string>();
            foreach (string path in _cursors.Keys)
                if (!livePaths.Contains(path)) drop.Add(path);
            foreach (string path in drop) _cursors.Remove(path);
        }
    }
}
