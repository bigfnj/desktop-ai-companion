using System;
using System.Collections.Generic;
using System.Drawing;

namespace DesktopAICompanion.Shimeji
{
    /// <summary>
    /// The resource soak's tick-companion step (N-shimeji-1106-04). The churn's pet step builds the default companion's
    /// CLASSIC form, so tick companions, their windows and their clip decoding (new in 1.9.0 to 1.11.0) ran under no leak
    /// soak. This step does, once per churn cycle, everything the app does for a pack member with a WAV clip: stage the
    /// document through the app's own path, give it a tick host whose windows are real FormCompanions (opacity 0), let
    /// two companions apply the pose that names the clip, play it into an output that decodes it (a mixer with no device,
    /// so the soak makes no sound), then close and dispose all of it. The settled series the churn already samples judges
    /// it; nothing here measures.
    ///
    /// Here rather than in RuntimeResourceChurn because it reuses this self-test's pack member builders, and because
    /// --shimeji-window-selftest runs the same step (CheckResourceChurnTickStep): the soak itself is not a gate step, so a
    /// step that broke would otherwise surface only as a churn failure on the next release soak.
    /// </summary>
    internal static partial class ShimejiWindowSelfTest
    {
        /// <summary>The churn member's type name (its pet id in the app).</summary>
        internal const string ChurnTypeName = "churn-singer";

        private static string _churnMemberText;

        /// <summary>
        /// One tick-companion cycle into <paramref name="output"/>, which the caller owns (started silent). Null when it
        /// ran as the app runs it; otherwise what went wrong, which the churn reports as its failure. Every window, the
        /// host, the staged pair and the clip's live input are gone when this returns, success or not.
        /// </summary>
        internal static string RunResourceChurnTickCycle(AudioOutput output, int cycle)
        {
            if (output == null) return "no audio output to play the clip into";
            if (_churnMemberText == null) _churnMemberText = Serialize(ChurnMemberDoc(), Color.Teal);
            Xml xml = null;
            Animations animations = null;
            TickHost host = null;
            var forms = new List<FormCompanion>();
            string owner = HostTickClips.OwnerOf(new TickClip(ChurnTypeName, "wa", 0f));
            try
            {
                string error;
                if (!StartUp.TryStageRuntime(_churnMemberText, 1.0, out xml, out animations, out error))
                    return "the pack member did not stage: " + error;
                TickType type = TickStaging.Of(xml);
                if (type == null) return "the pack member staged as a classic companion";
                type.Name = ChurnTypeName;
                host = new TickHost(t =>
                    {
                        var form = new FormCompanion(t.Animations, t.Sprites);
                        form.ShowInTaskbar = false;
                        form.OpenHiddenForTick(t.Type.CellWidth, t.Type.CellHeight);
                        forms.Add(form);
                        return form;
                    },
                    form => form.Close(),
                    message => { }) { RenderOpacity = 0.0, Audio = new AppTickAudio(() => output, () => false) };
                // The owner's windows are never floors here, as in every window self-test.
                host.Environment.SearchesWindows = false;
                host.AddType(new TickHostType(ChurnTypeName, type, xml, animations));

                Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                int x = work.Left + work.Width / 2 + (cycle % 2 == 0 ? -100 : 100), floor = work.Bottom;
                host.Spawn(ChurnTypeName, new TickPoint(x - 80, floor), false, "Sing");
                host.Spawn(ChurnTypeName, new TickPoint(x + 80, floor), false, "Sing");
                host.Step();
                host.Step();
                if (forms.Count != 2) return "the two companions opened " + forms.Count + " window(s)";
                if (!output.IsPlaying(owner)) return "the pose's WAV clip did not play into the output";
                return null;
            }
            finally
            {
                if (host != null) host.Dispose();
                foreach (FormCompanion form in forms)
                {
                    try { if (!form.IsDisposed) form.Close(); } catch { }
                    try { form.Dispose(); } catch { }
                }
                // Nothing reads a silent mixer, so the clip never ends by itself: its live input (and the decoded samples
                // it holds) goes here, as a companion's Mute would stop it.
                output.StopOwned(owner);
                if (animations != null) animations.Dispose();
                if (xml != null) xml.Dispose();
            }
        }

        /// <summary>The soak's step, run twice here as the churn runs it, into one silent output: each cycle stages, plays
        /// and closes everything it opened.</summary>
        private static void CheckResourceChurnTickStep(Action<bool, string> check)
        {
            using (var output = new AudioOutput())
            {
                output.StartSilentForTest();
                string first = RunResourceChurnTickCycle(output, 0);
                string second = first == null ? RunResourceChurnTickCycle(output, 1) : null;
                string owner = HostTickClips.OwnerOf(new TickClip(ChurnTypeName, "wa", 0f));
                check(first == null && second == null && !output.IsPlaying(owner),
                    "resource churn: the tick step stages a pack member with a WAV clip, plays the clip and closes what it opened ("
                    + (first ?? second ?? (output.IsPlaying(owner) ? "the clip was left playing" : "two cycles")) + ")");
            }
        }

        /// <summary>Singer: a main pack member whose one behaviour, Sing, holds a pose naming "wa", a WAV clip.</summary>
        private static XmlData.RootNode ChurnMemberDoc()
        {
            XmlData.RootNode doc = PackMember("Singer", "main", null, 1, new[]
            {
                PackRoute(1), PackBehaviour(10, "Sing"),
                PackAct(20, "Sing", null, new[] { PackParam("Duration", "500") }, new XmlData.PoseNode { Frame = 0, Ticks = 1, Sound = "wa" }),
            });
            doc.Header.Features += " " + CompanionFormatFeatures.Wav;
            byte[] wav = AudioOutputSelfTest.SyntheticWave(1, 16, 2, 44100, 441, false);
            doc.Sounds = new XmlData.SoundsNode
            {
                Sound = new[] { new XmlData.SoundNode { SoundId = "wa", Format = "wav", Base64 = Convert.ToBase64String(wav) } },
            };
            return doc;
        }
    }
}
