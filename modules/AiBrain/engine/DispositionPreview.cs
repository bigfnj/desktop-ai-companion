using System;
using System.Collections.Generic;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// The synthetic screens a persona audition is run against.
    ///
    /// WHY NOT THE REAL SCREEN. A live capture makes all five samples remark on the same window, so the
    /// five differ by sampling noise and the thing being auditioned -- the VOICE -- is the one variable
    /// held constant. Five canned scenes invert that: the scene varies, the persona is what the user is
    /// actually comparing, and a character that only works on code or only works when idle is visible
    /// immediately.
    ///
    /// It also removes the audition's dependence on the screen-reading stack entirely. No capture, no
    /// downscale, no OCR engine, no vision model, so a persona can be auditioned on a machine where
    /// Tesseract is missing or the vision model was never pulled, and a disappointing sample is the
    /// persona's fault rather than a 6px-text problem.
    ///
    /// These are deliberately DESCRIPTIONS in the same shape the live path builds
    /// (see AskAboutScreenAsync's `ctx`), not prompts: the persona instruction, the word budget and the
    /// JSON contract all still come from AiBrain.BuildSystemPrompt, so an audition is graded by the
    /// real prompt rather than a parallel copy that can drift.
    ///
    /// Kept out of the prompt builder on purpose, per the backlog item that asked for this.
    /// </summary>
    internal static class DispositionScenes
    {
        internal struct Scene
        {
            /// <summary>Short label shown beside the sample, so the user can see WHAT it reacted to.</summary>
            public string Label;
            /// <summary>The screen description, phrased exactly as the live path phrases it.</summary>
            public string Context;
        }

        /// <summary>
        /// Five scenes, chosen to be maximally different from each other rather than representative:
        /// focused work, passive watching, nothing at all, dry numbers, and idle browsing. A persona
        /// that cannot find something to say about an empty desktop is worth knowing about before it is
        /// the one running all day.
        /// </summary>
        internal static readonly Scene[] All =
        {
            new Scene
            {
                Label = "a code editor",
                Context =
                    "The active window is: AiBrain.cs - desktop-ai-companion - Visual Studio Code\n" +
                    "Also open on this screen: msedge, WindowsTerminal\n",
            },
            new Scene
            {
                Label = "a video playing",
                Context =
                    "The active window is: How To Make Sourdough Bread At Home - YouTube - Edge\n" +
                    "Also open on this screen: spotify\n",
            },
            new Scene
            {
                Label = "an empty desktop",
                Context =
                    "There is no active window; the desktop is empty.\n",
            },
            new Scene
            {
                Label = "a spreadsheet",
                Context =
                    "The active window is: Q3-budget-FINAL-v7.xlsx - Excel\n" +
                    "Also open on this screen: outlook, teams\n",
            },
            new Scene
            {
                Label = "late-night browsing",
                Context =
                    "The active window is: are ferrets nocturnal - Google Search - Edge\n" +
                    "Also open on this screen: msedge, msedge, msedge\n",
            },
        };

        /// <summary>
        /// The user-facing instruction appended to a scene. Mirrors the live OCR path's phrasing ("Here
        /// is the text currently visible on my screen") closely enough that the model is doing the same
        /// job, while being honest that this is a described screen rather than a captured one.
        /// </summary>
        internal const string Instruction = "React to what is on my screen.";
    }

    /// <summary>One audition sample: which scene produced it, and either a remark or why there isn't one.</summary>
    internal sealed class DispositionSample
    {
        public string SceneLabel { get; private set; }
        /// <summary>The remark, or null when this sample failed.</summary>
        public string Text { get; private set; }
        /// <summary>An error CATEGORY when <see cref="Text"/> is null, never a raw message.</summary>
        public string Error { get; private set; }
        public long ElapsedMs { get; private set; }

        public DispositionSample(string sceneLabel, string text, string error, long elapsedMs)
        {
            SceneLabel = sceneLabel ?? "";
            Text = text;
            Error = error;
            ElapsedMs = elapsedMs;
        }

        public bool Ok { get { return Text != null; } }
    }

    /// <summary>
    /// One audition run: the samples, and WHICH model produced them.
    ///
    /// <see cref="Advisory"/> exists because AiModelPolicy.ChooseModel substitutes rather than refusing:
    /// an inventory that lacks the configured model yields the first usable one instead, with a reason.
    /// That is the right runtime behaviour (a companion that talks beats one that sulks), but for an
    /// audition it is a trap -- the user would be judging a persona on a model they did not choose, and
    /// concluding the character is dull when the model is. Silently swapping a model is exactly how
    /// BUG-002 stayed hidden, so the swap is carried out to the pane rather than only to the log.
    /// </summary>
    internal sealed class DispositionAudition
    {
        public IReadOnlyList<DispositionSample> Samples { get; private set; }
        /// <summary>The model that actually answered, or null when none could be chosen.</summary>
        public string ModelUsed { get; private set; }
        /// <summary>Non-null when the model was substituted or unusable; already user-facing prose.</summary>
        public string Advisory { get; private set; }

        public DispositionAudition(
            IReadOnlyList<DispositionSample> samples, string modelUsed, string advisory)
        {
            Samples = samples ?? new List<DispositionSample>();
            ModelUsed = modelUsed;
            Advisory = advisory;
        }
    }
}
