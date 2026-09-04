using System.Collections.Generic;

namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// The fortune-relevant settings the engine reads, split out of the base <c>AiSettings</c> when the
    /// engine moved into the Fortunes module (S3). Field names/types mirror the old <c>AiSettings</c>
    /// fortune block exactly, so the relocated <see cref="FortuneProvider"/> is unchanged apart from the
    /// type name. Populated by the module from <c>host.GetSettings</c> when the engine goes live (S3d); the
    /// self-tests construct it directly.
    /// </summary>
    /// <summary>
    /// The content tiers a selection admits. Fortunes are tagged general / edgy / nsfw, and these four
    /// choices are the combinations worth offering, ordered from tamest to broadest (with "spicy only" as
    /// the deliberate outlier that excludes tame content). Persisted as these exact strings.
    /// </summary>
    public static class ContentLevels
    {
        public const string Clean = "clean";              // general
        public const string CleanEdgy = "cleanEdgy";      // general + edgy
        public const string Everything = "everything";    // general + edgy + nsfw
        public const string SpicyOnly = "spicyOnly";      // edgy + nsfw (no tame lines)

        public static bool IsKnown(string value)
        {
            return value == Clean || value == CleanEdgy || value == Everything || value == SpicyOnly;
        }
    }

    public sealed class FortuneSettings
    {
        // One ordered choice replacing the old SpicyFortunes + SpicyTier + SpicyOnly trio (see
        // ContentLevels). Those three had 16 combinations, several contradictory, and their names did not
        // describe what they did: "Edgy + NSFW" actually meant general+edgy+nsfw (i.e. everything), while
        // "True NSFW only" meant general+nsfw — it silently DROPPED edgy while keeping tame content.
        public string ContentLevel = ContentLevels.Clean;

        // Orthogonal to the level: a word filter over recognized profanity / explicit sexual content,
        // applied on top of whichever tiers ContentLevel admits.
        public bool NoProfanity = false;
        public bool SmartFortunes = true;
        public List<string> DisabledSources = new List<string>();
        public List<string> DisabledGenres = new List<string>();
    }
}
