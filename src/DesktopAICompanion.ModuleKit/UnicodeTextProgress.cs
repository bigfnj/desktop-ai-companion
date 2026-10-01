namespace DesktopAICompanion.ModuleKit
{
    /// <summary>
    /// Advance through or truncate text without splitting a character in half.
    ///
    /// A .NET string is UTF-16 code units, so anything outside the Basic Multilingual Plane — an emoji, and
    /// plenty of CJK — occupies TWO of them (a surrogate pair). Cutting a string at an arbitrary index can
    /// land between the pair and produce a lone surrogate, which renders as a replacement box and can break
    /// a downstream JSON/XML writer. A module that types text out progressively, or clips a model's reply to
    /// a length cap, wants these instead of raw index arithmetic.
    ///
    /// Copied out of the app's own RuntimeGeometry, which one module had duplicated and another was
    /// source-linking an entire file to reach. Since RA-253 (2026-10-01) the host compiles THIS file by
    /// source link too, beside AtomicFile and CrossSessionLock (F358), so the twin that drifted is gone.
    /// </summary>
    public static class UnicodeTextProgress
    {
        /// <summary>The next index at or after <paramref name="currentLength"/> that does not split a
        /// surrogate pair — i.e. advance by one whole character, not one code unit.</summary>
        public static int NextCodePointBoundary(string text, int currentLength)
        {
            text = text ?? "";
            if (currentLength < 0) currentLength = 0;
            if (currentLength >= text.Length) return text.Length;
            if (char.IsHighSurrogate(text[currentLength]) &&
                currentLength + 1 < text.Length &&
                char.IsLowSurrogate(text[currentLength + 1]))
                return currentLength + 2;
            return currentLength + 1;
        }

        /// <summary>Clip to at most <paramref name="maximumCodeUnits"/> UTF-16 code units, backing off by one
        /// when that boundary would sever a surrogate pair.</summary>
        public static string TruncateAtCodePointBoundary(string text, int maximumCodeUnits)
        {
            text = text ?? "";
            if (maximumCodeUnits <= 0) return "";
            if (text.Length <= maximumCodeUnits) return text;

            // length > 0 (the cap returned above, F236) and length < text.Length (the early return above, RA-254),
            // so text[length - 1] and text[length] are both in range without a further guard.
            int length = maximumCodeUnits;
            if (char.IsHighSurrogate(text[length - 1]) &&
                char.IsLowSurrogate(text[length]))
                length--;
            return text.Substring(0, length);
        }
    }
}
