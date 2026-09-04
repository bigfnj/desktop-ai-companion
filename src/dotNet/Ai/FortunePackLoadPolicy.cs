namespace DesktopAICompanion.Ai
{
    /// <summary>
    /// Resource bounds shared by trusted-catalog admission and pack downloads. These limits describe
    /// what the runtime can accept from the fortunes directory.
    ///
    /// This is the only surviving piece of the former base fortune engine: the engine itself
    /// (FortuneProvider / FortuneFileImporter and their self-tests) moved to the Fortunes module,
    /// but <see cref="DesktopAICompanion.RemoteCatalog"/> still enforces these same per-file/entry bounds
    /// when validating and downloading catalog packs, so the policy stays in the base.
    /// </summary>
    internal static class FortunePackLoadPolicy
    {
        // 512 matches RemoteCatalogClient's per-kind catalog entry cap: the catalog itself refuses to list
        // more packs than this, so the loader must accept at least as many or installing everything the
        // catalog offers silently drops the overflow. (At 128 it did exactly that -- the full 152-pack
        // catalog lost its last 24 files alphabetically, so e.g. tv-simpsons never loaded.) The real memory
        // bounds are the byte/entry caps below, which are unchanged.
        public const int MaximumFiles = 512;
        public const int MaximumFileBytes = 4 * 1024 * 1024;
        public const int MaximumTotalBytes = 16 * 1024 * 1024;
        public const int MaximumEntries = 100000;

        public static bool TryValidatePackMetadata(
            int bytes,
            int entries,
            out string error)
        {
            if (bytes < 1 || bytes > MaximumFileBytes)
            {
                error = "pack byte count is outside the runtime per-file limit";
                return false;
            }
            if (entries < 1 || entries > MaximumEntries)
            {
                error = "pack row count is outside the runtime entry limit";
                return false;
            }
            error = null;
            return true;
        }
    }
}
