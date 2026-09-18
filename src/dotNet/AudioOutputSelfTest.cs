using System;
using System.IO;
using System.Text;
using NAudio.Wave;

namespace DesktopAICompanion
{
    /// <summary>
    /// --audio-selftest: the module-audio path, asserted WITHOUT an audio device.
    ///
    /// Everything here is deliberately device-independent, because the interesting parts are the decode seam
    /// and the barge-in ramp, and CI runners have no playback device. Opening DirectSound is not covered and
    /// cannot be: that is what the live smoke script is for.
    ///
    /// Why this exists at all: PlaySound's contract is "false means nothing will be heard", and every caller
    /// is expected to fall back to a bubble on false. That makes the difference between "returned null" and
    /// "threw" load-bearing, and it is invisible to every other gate.
    ///
    /// The shared notification sound (IHost.PlayNotificationSound, the Preferences picker) is asserted here
    /// for the same reason and one more: its three gates are ORDERED, and every wrong order still produces
    /// a silent notification, so nothing a user or another test could observe tells the two apart.
    /// </summary>
    internal static class AudioOutputSelfTest
    {
        public static bool Run()
        {
            var sb = new StringBuilder();
            bool ok = true;
            try
            {
                ok &= DecodesRealAudio(sb);
                ok &= RejectsRubbish(sb);
                ok &= FadeEndsTheInput(sb);
                ok &= NotificationSoundSettingRoundTrips(sb);
                ok &= NotificationGatesInOrder(sb);
                ok &= NotificationPicksAreValidatedAtPickTime(sb);
                ok &= NotificationFallsBackToTheBuiltIn(sb);
                ok &= NotificationActionsAreOfferedInOrder(sb);
                // ModuleKit's WavAudio is asserted in CoreTests instead: the host does not reference ModuleKit
                // (it is a library that ships inside each MODULE), so it cannot be exercised from here.
            }
            catch (Exception ex) { ok = false; sb.AppendLine("EXC: " + ex.GetType().Name + ": " + ex.Message); }

            sb.AppendLine("RESULT=" + (ok ? "PASS" : "FAIL"));
            Console.Write(sb.ToString());
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "dp-audio-selftest.txt"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return ok;
        }

        private static bool DecodesRealAudio(StringBuilder sb)
        {
            bool ok = true;

            // Mono at 22050 must come back resampled to 44100 AND upmixed to stereo: 1 second in, 44100
            // frames x 2 channels out. This is the single assertion proving ToMixFormat is actually applied --
            // a source at the wrong rate would otherwise play at the wrong speed, which no other test notices.
            float[] mono = Tone(22050, 1, 22050);
            byte[] monoWav = PcmWav(mono, 22050, 1);
            float[] decodedMono = AudioOutput.DecodeModuleAudio(monoWav);
            ok &= Check(sb, "22050 mono WAV decodes", decodedMono != null && decodedMono.Length > 0);
            if (decodedMono != null)
            {
                int frames = decodedMono.Length / 2;
                // Resamplers round; a couple of frames either way is fine, an octave out is not.
                ok &= Check(sb,
                    "22050 mono is resampled to 44100 and upmixed to stereo (" + frames + " frames)",
                    Math.Abs(frames - 44100) <= 64);
            }

            // Already at the mixer format: length must be preserved exactly.
            float[] stereo = Tone(44100, 2, 4410);
            float[] decodedStereo = AudioOutput.DecodeModuleAudio(PcmWav(stereo, 44100, 2));
            ok &= Check(sb, "44100 stereo WAV decodes to its own length",
                decodedStereo != null && Math.Abs(decodedStereo.Length - stereo.Length) <= 4);

            return ok;
        }

        private static bool RejectsRubbish(StringBuilder sb)
        {
            bool ok = true;
            ok &= Check(sb, "null is rejected", AudioOutput.DecodeModuleAudio(null) == null);
            ok &= Check(sb, "a 4-byte buffer is rejected", AudioOutput.DecodeModuleAudio(new byte[4]) == null);
            ok &= Check(sb, "random bytes are rejected",
                AudioOutput.DecodeModuleAudio(Encoding.ASCII.GetBytes("not audio at all, honestly")) == null);

            // RIFF header with the wrong form type: sniffed as not-WAV rather than handed to a reader.
            byte[] wrongForm = PcmWav(Tone(44100, 1, 128), 44100, 1);
            wrongForm[11] = (byte)'X';   // "WAVE" -> "WAVX"
            ok &= Check(sb, "RIFF with a non-WAVE form type is rejected",
                AudioOutput.DecodeModuleAudio(wrongForm) == null);

            // More than two channels: rejected explicitly, so the caller gets false and can show a bubble,
            // rather than the mixer throwing into a silent catch.
            ok &= Check(sb, "a 3-channel WAV is rejected", AudioOutput.DecodeModuleAudio(ThreeChannelWav()) == null);

            // Over the encoded cap. Built as a bare oversized RIFF header so the test does not allocate 16 MB
            // of samples just to be told no.
            var oversized = new byte[AudioOutput.MaximumModuleAudioBytes + 1];
            oversized[0] = (byte)'R'; oversized[1] = (byte)'I'; oversized[2] = (byte)'F'; oversized[3] = (byte)'F';
            oversized[8] = (byte)'W'; oversized[9] = (byte)'A'; oversized[10] = (byte)'V'; oversized[11] = (byte)'E';
            ok &= Check(sb, "an over-cap buffer is rejected", AudioOutput.DecodeModuleAudio(oversized) == null);

            return ok;
        }

        /// <summary>A 16-bit PCM WAV from interleaved floats. Written here rather than reused from ModuleKit's
        /// WavAudio because the host does not reference ModuleKit (that library ships inside each MODULE).
        /// Keeping the fixture independent also means a bug in that helper cannot make the host's decoder look
        /// correct -- WavAudio is asserted separately in CoreTests.</summary>
        private static byte[] PcmWav(float[] interleaved, int sampleRate, int channels)
        {
            if (interleaved == null) return null;
            int dataBytes = interleaved.Length * 2;
            int blockAlign = channels * 2;
            using (var ms = new MemoryStream(44 + dataBytes))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(sampleRate * blockAlign);
                w.Write((short)blockAlign);
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                foreach (float s in interleaved)
                {
                    float c = s > 1f ? 1f : (s < -1f ? -1f : s);
                    w.Write((short)Math.Round(c * short.MaxValue));
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        private static bool FadeEndsTheInput(StringBuilder sb)
        {
            bool ok = true;
            WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

            // A long buffer that would otherwise keep playing for a second: after FadeOutAndEnd it must ramp
            // out and then report end-of-stream, which is how NAudio drops the input. Read in small chunks so
            // the ramp is exercised across more than one call.
            var source = new AudioOutput.CachedSampleProvider(new float[44100 * 2], format, 0);
            source.FadeOutAndEnd();
            int total = 0;
            var buffer = new float[128];
            for (int i = 0; i < 100; i++)
            {
                int n = source.Read(buffer.AsSpan());
                total += n;
                if (n == 0) break;
            }
            ok &= Check(sb, "a faded input ends quickly instead of playing on (" + total + " samples)",
                total > 0 && total <= 441 * 2 + 128);
            ok &= Check(sb, "a faded input then reports end-of-stream", source.Read(buffer.AsSpan()) == 0);

            // A chunk smaller than the ramp must still terminate rather than looping forever.
            var small = new AudioOutput.CachedSampleProvider(new float[44100 * 2], format, 0);
            small.FadeOutAndEnd();
            var tiny = new float[16];
            int guard = 0;
            while (small.Read(tiny.AsSpan()) > 0 && guard < 1000) guard++;
            ok &= Check(sb, "the ramp terminates even when read in chunks smaller than it", guard < 1000);

            // Untouched providers still play in full: the fade must not have changed normal playback.
            var normal = new AudioOutput.CachedSampleProvider(new float[1000], format, 0);
            int played = 0;
            var chunk = new float[256];
            int n2;
            while ((n2 = normal.Read(chunk.AsSpan())) > 0) played += n2;
            ok &= Check(sb, "an un-faded input still plays to completion", played == 1000);
            return ok;
        }

        /// <summary>
        /// The chosen-notification-sound setting, through the REAL store and the real accessors rather
        /// than through a stub, because the interesting cases are all normalization: what an older file
        /// with no such key reads as, and what happens to a path the setting must not keep.
        /// </summary>
        private static bool NotificationSoundSettingRoundTrips(StringBuilder sb)
        {
            bool ok = true;
            string root = null;
            try
            {
                root = DesktopAICompanion.Plugins.SelfTestScratch.Create("audio");
                string settings = Path.Combine(root, "settings.json");

                ok &= Check(sb, "a fresh document defaults to the built-in sound",
                    AppSettingsDocument.CreateDefault().NotificationSoundPath == "");

                // Absent, written as RAW JSON with the key missing rather than as a document with the
                // field left null. Those are different bytes on disk, and "absent" is the state every
                // settings file already out there is in -- a test that only covers null would pass on a
                // build that read a missing key as something else entirely.
                File.WriteAllText(settings, "{\"schemaVersion\":2}", new UTF8Encoding(false));
                ok &= Check(sb, "a settings file with no notificationSoundPath reads as the built-in",
                    new AppSettingsStore(settings, null).Load().NotificationSoundPath == "");

                string chosen = Path.Combine(root, "chime.wav");
                File.WriteAllBytes(chosen, PcmWav(Tone(44100, 1, 4410), 44100, 1));

                var store = new AppSettingsStore(settings, null);
                AppSettingsDocument doc = store.Load();
                doc.NotificationSoundPath = "  " + chosen + "  ";   // trimmed on normalize
                ok &= Check(sb, "a document carrying a chosen sound saves", store.Save(doc));
                ok &= Check(sb, "the chosen sound round-trips through save/load",
                    new AppSettingsStore(settings, null).Load().NotificationSoundPath == chosen);

                // A relative path resolves against the process working directory, which for a
                // shell-launched app is wherever the user happened to be standing. Refused back to the
                // built-in, and asserted here because nothing at play time could tell the difference
                // between "resolved somewhere odd" and "the file is missing".
                var store2 = new AppSettingsStore(settings, null);
                AppSettingsDocument relative = store2.Load();
                relative.NotificationSoundPath = "chime.wav";
                ok &= Check(sb, "a document carrying a relative path saves", store2.Save(relative));
                ok &= Check(sb, "a relative path falls back to the built-in",
                    new AppSettingsStore(settings, null).Load().NotificationSoundPath == "");

                // The same trip through LocalData, which is the only way the app ever touches this.
                var data = new LocalData(new AppSettingsStore(settings, null));
                ok &= Check(sb, "LocalData starts on the built-in", data.GetNotificationSoundPath() == "");
                data.SetNotificationSoundPath(chosen);
                ok &= Check(sb, "LocalData writes the chosen sound through to disk",
                    new LocalData(new AppSettingsStore(settings, null)).GetNotificationSoundPath() == chosen);
                data.SetNotificationSoundPath("");
                ok &= Check(sb, "clearing it returns to the built-in",
                    new LocalData(new AppSettingsStore(settings, null)).GetNotificationSoundPath() == "");
            }
            catch (Exception ex)
            {
                ok = Check(sb, "the notification-sound setting round-trip ran (" +
                    ex.GetType().Name + ": " + ex.Message + ")", false);
            }
            finally
            {
                string detail;
                if (root != null) DesktopAICompanion.Plugins.SelfTestScratch.TryRelease(root, out detail);
            }
            return ok;
        }

        /// <summary>
        /// The three layers PlayNotificationSound honours, and the ORDER it honours them in.
        ///
        /// Every case here passes a null output, so a build that consulted the device first would answer
        /// "no device" to all of them and still, correctly, never play -- which is why the outcome and not
        /// the silence is what gets asserted. A bool return cannot tell a muted user from a dead sound
        /// card, and neither can the person holding the laptop.
        /// </summary>
        private static bool NotificationGatesInOrder(StringBuilder sb)
        {
            bool ok = true;
            string root = null;
            try
            {
                root = DesktopAICompanion.Plugins.SelfTestScratch.Create("audio");
                var data = new LocalData(new AppSettingsStore(Path.Combine(root, "settings.json"), null));

                ok &= Check(sb, "no settings at all means no notification",
                    NotificationSound.Play(null, null, "module") == NotificationOutcome.NoSettings);

                data.SetNotificationSoundsEnabled(false);
                data.SetVolume(1.0);
                ok &= Check(sb, "the notificationSounds switch being off refuses first",
                    NotificationSound.Play(data, null, "module") == NotificationOutcome.SwitchedOff);

                data.SetNotificationSoundsEnabled(true);
                data.SetVolume(0.0);
                ok &= Check(sb, "master volume 0 refuses",
                    NotificationSound.Play(data, null, "module") == NotificationOutcome.Muted);

                // Both gates open, so it gets as far as the device it has not got. Without this case the
                // two above pass for a Play() that refuses everything it is ever handed.
                data.SetVolume(0.5);
                ok &= Check(sb, "with both settings open it reaches the output device",
                    NotificationSound.Play(data, null, "module") == NotificationOutcome.NoDevice);
            }
            catch (Exception ex)
            {
                ok = Check(sb, "the notification gate order ran (" +
                    ex.GetType().Name + ": " + ex.Message + ")", false);
            }
            finally
            {
                string detail;
                if (root != null) DesktopAICompanion.Plugins.SelfTestScratch.TryRelease(root, out detail);
            }
            return ok;
        }

        /// <summary>
        /// A file that is not playable audio is refused WHERE THE USER IS -- in the Preferences picker --
        /// and not at play time, where the only report available is silence, days later, from a reminder
        /// nobody is standing next to.
        /// </summary>
        private static bool NotificationPicksAreValidatedAtPickTime(StringBuilder sb)
        {
            bool ok = true;
            string root = null;
            try
            {
                root = DesktopAICompanion.Plugins.SelfTestScratch.Create("audio");
                string rubbish = Path.Combine(root, "not-really.wav");
                File.WriteAllBytes(rubbish, Encoding.ASCII.GetBytes("an audio extension and nothing else"));
                string real = Path.Combine(root, "real.wav");
                File.WriteAllBytes(real, PcmWav(Tone(44100, 2, 4410), 44100, 2));
                string empty = Path.Combine(root, "empty.wav");
                File.WriteAllBytes(empty, new byte[0]);
                string gone = Path.Combine(root, "never-existed.wav");

                ok &= Check(sb, "a real WAV passes pick-time validation", NotificationSound.Validate(real) == null);
                ok &= Check(sb, "a file with an audio extension and no audio in it is refused",
                    NotificationSound.Validate(rubbish) != null);
                ok &= Check(sb, "an empty file is refused", NotificationSound.Validate(empty) != null);
                ok &= Check(sb, "a missing file is refused", NotificationSound.Validate(gone) != null);

                // Guarded rather than assumed: --audio-selftest exits long before Program.MyData exists,
                // and if that ever changes the call below would write a scratch path into the real user's
                // settings.json. It must fail here instead of quietly editing someone's preferences.
                bool headless = Program.MyData == null;
                ok &= Check(sb, "the self-test runs before settings exist, so nothing here can persist", headless);
                if (headless)
                {
                    string refused = DesktopAICompanion.Wpf.OptionsShell.ApplyNotificationSoundChoice(rubbish);
                    ok &= Check(sb, "the picker refuses an undecodable file: " + refused,
                        refused != null && refused.StartsWith("✗", StringComparison.Ordinal) &&
                        refused.IndexOf("can play", StringComparison.OrdinalIgnoreCase) >= 0);

                    // The same call with a real file gets PAST validation and stops at the next step
                    // instead. Without this the check above is satisfied by a picker that refuses
                    // everything, which is a different bug wearing the same green tick.
                    string accepted = DesktopAICompanion.Wpf.OptionsShell.ApplyNotificationSoundChoice(real);
                    ok &= Check(sb, "...and lets a real file through to the settings step: " + accepted,
                        accepted != null &&
                        accepted.IndexOf("settings are unavailable", StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }
            catch (Exception ex)
            {
                ok = Check(sb, "pick-time validation ran (" +
                    ex.GetType().Name + ": " + ex.Message + ")", false);
            }
            finally
            {
                string detail;
                if (root != null) DesktopAICompanion.Plugins.SelfTestScratch.TryRelease(root, out detail);
            }
            return ok;
        }

        /// <summary>
        /// The built-in chime, and the two ways it gets used: nothing chosen, and a choice that has
        /// stopped being playable. Asserted on the samples themselves because there is no device here and
        /// because "it played" would not have caught a chime that is 0.75 s of silence.
        /// </summary>
        private static bool NotificationFallsBackToTheBuiltIn(StringBuilder sb)
        {
            bool ok = true;
            float[] builtIn = NotificationSound.BuiltInChime(44100, 2);
            ok &= Check(sb, "the built-in chime is 0.75 s of stereo at the mixer rate (" + builtIn.Length + " samples)",
                builtIn.Length == (int)(44100 * 0.75) * 2);

            float peak = 0f;
            for (int i = 0; i < builtIn.Length; i++) { float a = Math.Abs(builtIn[i]); if (a > peak) peak = a; }
            ok &= Check(sb, "the built-in chime is audible and leaves the mixer headroom (peak " +
                peak.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ")",
                peak > 0.4f && peak <= 0.8f);
            ok &= Check(sb, "the built-in chime starts and ends at silence (no click)",
                builtIn.Length > 0 && Math.Abs(builtIn[0]) < 0.0001f &&
                Math.Abs(builtIn[builtIn.Length - 1]) < 0.0001f);

            ok &= Check(sb, "an empty path means no chosen bytes", NotificationSound.ReadChosen("") == null);
            ok &= Check(sb, "nothing chosen plays the built-in",
                ReferenceEquals(NotificationSound.Resolve(null, builtIn), builtIn));
            // A chosen file the picker accepted can still rot: renamed, re-saved as something else, or
            // replaced by a sync client. Silence would be the wrong answer to that -- the notification's
            // whole job is to be heard, and the built-in is how the user finds out something changed.
            ok &= Check(sb, "a chosen file that no longer decodes falls back to the built-in",
                ReferenceEquals(
                    NotificationSound.Resolve(Encoding.ASCII.GetBytes("not audio any more"), builtIn), builtIn));

            // The other direction, or all three above are satisfied by a Resolve that ignores the choice.
            float[] custom = NotificationSound.Resolve(PcmWav(Tone(44100, 2, 4410), 44100, 2), builtIn);
            ok &= Check(sb, "a decodable chosen file is what plays, not the built-in",
                custom != null && !ReferenceEquals(custom, builtIn) && Math.Abs(custom.Length - 4410 * 2) <= 4);
            return ok;
        }

        /// <summary>
        /// The Sound card's buttons. Both halves of the choice have to be REACHABLE — a picker with no way
        /// back to the default is a setting the user cannot undo — and "Choose" has to sit above "Test
        /// sound" so the card reads in the order the task is performed.
        ///
        /// Here rather than in the options-shell self-test because what it pins is this feature's UI
        /// contract, not the renderer's, and because a label or an ordering is exactly the kind of thing a
        /// later edit reshuffles without noticing. Asserted on POSITION, not presence: three buttons all
        /// existing in the wrong order is the failure this is for.
        /// </summary>
        private static bool NotificationActionsAreOfferedInOrder(StringBuilder sb)
        {
            bool ok = true;
            try
            {
                DesktopAICompanion.Modules.OptionsPane prefs =
                    DesktopAICompanion.Wpf.OptionsShell.BuildPreferencesPane();
                var sound = new System.Collections.Generic.List<string>();
                if (prefs != null && prefs.Actions != null)
                    foreach (DesktopAICompanion.Modules.PaneAction a in prefs.Actions)
                        if (a != null && string.Equals(a.Group, "Sound", StringComparison.Ordinal))
                            sound.Add(a.Label ?? "");

                int choose = sound.FindIndex(delegate (string l) { return l.StartsWith("Choose notification sound", StringComparison.Ordinal); });
                int builtIn = sound.FindIndex(delegate (string l) { return l.IndexOf("built-in", StringComparison.OrdinalIgnoreCase) >= 0; });
                int test = sound.IndexOf("Test sound");
                int device = sound.FindIndex(delegate (string l) { return l.IndexOf("output device", StringComparison.OrdinalIgnoreCase) >= 0; });

                ok &= Check(sb, "the Sound card offers a way to choose a notification sound", choose >= 0);
                ok &= Check(sb, "...and a way back to the built-in", builtIn >= 0);
                ok &= Check(sb, "...with Choose above Test sound", choose >= 0 && test > choose);
                // The 440 Hz tone keeps a button of its own. It ignores mute and the master volume by
                // design, which is the one thing "Test sound" must no longer do now that it previews the
                // real notification sound -- two jobs, two buttons, neither able to answer for the other.
                ok &= Check(sb, "...and the output-device tone is still reachable separately",
                    device >= 0 && device != test);

                bool shows = false;
                if (prefs != null && prefs.Schema != null)
                    foreach (DesktopAICompanion.Modules.SettingField f in prefs.Schema)
                        if (f != null && f.Id == "notificationSoundInfo" &&
                            f.Kind == DesktopAICompanion.Modules.SettingKind.Info) shows = true;
                ok &= Check(sb, "the pane says which notification sound is currently chosen", shows);
            }
            catch (Exception ex)
            {
                ok = Check(sb, "the preferences actions could be built (" +
                    ex.GetType().Name + ": " + ex.Message + ")", false);
            }
            return ok;
        }

        /// <summary>Interleaved sine, loud enough that a decode failure would not look like success.</summary>
        private static float[] Tone(int sampleRate, int channels, int frames)
        {
            var buf = new float[frames * channels];
            for (int f = 0; f < frames; f++)
            {
                float s = (float)(Math.Sin(2.0 * Math.PI * 440.0 * f / sampleRate) * 0.5);
                for (int c = 0; c < channels; c++) buf[f * channels + c] = s;
            }
            return buf;
        }

        /// <summary>A structurally valid 3-channel PCM WAV, which WavAudio deliberately will not build.</summary>
        private static byte[] ThreeChannelWav()
        {
            const int channels = 3, rate = 44100, frames = 64;
            int dataBytes = frames * channels * 2;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                for (int i = 0; i < frames * channels; i++) w.Write((short)0);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static bool Check(StringBuilder sb, string what, bool condition)
        {
            sb.AppendLine((condition ? "PASS: " : "FAIL: ") + what);
            return condition;
        }
    }
}
