using System.Collections.Generic;
using System.Text;

namespace DesktopAICompanion.Tools.ShimejiConvert.Emit
{
    /// <summary>One thing the conversion could not carry faithfully.</summary>
    public sealed class ResidueItem
    {
        public string Name;    // the Shimeji action / behaviour name
        public string Kind;    // "dropped" (Group3) or "degraded" (Group2)
        public string Detail;  // why, in plain language
    }

    /// <summary>
    /// The honest account of what an import loses, shown to the user before install and written beside the
    /// pet as residue.txt. This is a first-class deliverable, not a debug aid: a converted pet is a lossy
    /// artefact and the report is where that loss is stated rather than hidden.
    /// </summary>
    public sealed class ResidueReport
    {
        public readonly List<ResidueItem> Dropped = new List<ResidueItem>();   // Group3
        public readonly List<ResidueItem> Degraded = new List<ResidueItem>();  // Group2
        public readonly List<string> Notes = new List<string>();               // format-wide losses

        public int Total { get { return Dropped.Count + Degraded.Count; } }

        public string ToText(string skinName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Shimeji import: what was lost");
            sb.AppendLine("skin: " + (skinName ?? "(unnamed)"));
            sb.AppendLine();

            if (Notes.Count > 0)
            {
                sb.AppendLine("Format limits (affect every import):");
                foreach (string n in Notes) sb.AppendLine("  - " + n);
                sb.AppendLine();
            }

            if (Dropped.Count > 0)
            {
                sb.AppendLine("Dropped entirely (" + Dropped.Count + "):");
                foreach (ResidueItem i in Dropped) sb.AppendLine("  - " + i.Name + ": " + i.Detail);
                sb.AppendLine();
            }

            if (Degraded.Count > 0)
            {
                sb.AppendLine("Kept but simplified (" + Degraded.Count + "):");
                foreach (ResidueItem i in Degraded) sb.AppendLine("  - " + i.Name + ": " + i.Detail);
                sb.AppendLine();
            }

            if (Total == 0 && Notes.Count == 0)
                sb.AppendLine("Nothing was dropped or degraded.");

            return sb.ToString().TrimEnd();
        }
    }
}
