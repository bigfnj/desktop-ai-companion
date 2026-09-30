using System;
using System.Text;

namespace DesktopAICompanion.Tools
{
    /// <summary>
    /// Convert the current animation XML to a DOT file.
    /// </summary>
    /// <remarks>
    /// A DOT file can be opened with Graphviz (or http://www.webgraphviz.com/) to generate a graphically view of the XML.
    /// Reached from the SHIFT-start debug window's "Convert to DOT" only. Builds into a StringBuilder, writes no
    /// Console line, and escapes every name it puts inside a DOT string (F325): the old export concatenated
    /// strings quadratically, formatted a Console line per sequence edge that nothing in a windowed process
    /// reads, and emitted animation names verbatim, so a quote in a pet's name broke the label and the graph.
    /// </remarks>
	class XmlToDot
	{
        /// <summary>
        /// Convert the Xml to a DOT and returns the result as string.
        /// </summary>
        /// <param name="model">The root node of xml file.</param>
        /// <returns>The DOT text; a one-line comment when the model has no animations.</returns>
		static public string ProcessXml(XmlData.RootNode model)
		{
			if (model == null || model.Animations == null || model.Animations.Animation == null)
				return "# No animations found.\r\n";
			string title = model.Header != null ? model.Header.Title : null;
			return ProcessAnimations(title, model.Animations.Animation);
		}

		static private string ProcessAnimations(string animationTitle, XmlData.AnimationNode[] animations)
		{
			var dot = new StringBuilder();
			dot.Append("# Convert ").Append(animationTitle).Append(" to Graphviz dot format by DesktopAICompanion Xml2Gv ")
			   .Append(DateTime.Now).Append("\r\n");
			dot.Append("# Copy the text and insert it into https://dreampuf.github.io/GraphvizOnline/ or http://webgraphviz.com/ to generate an image\r\n");
			dot.Append("# This functionality was added after this isse: https://github.com/Adrianotiger/desktopPet/issues/6 \r\n");
			dot.Append("digraph PetGraph {\r\n");
			dot.Append(" rankdir = LR;\r\n");

			foreach (var anim in animations)
			{
				if (anim == null) continue;
				if (anim.Sequence != null && anim.Sequence.Next != null)
				{
					dot.Append("# ").Append(anim.Id).Append(" Sequence ").Append(anim.Sequence.Next.Length).Append("\r\n");
					ProcessNext(dot, Next.Sequence, TotalProbability(anim.Sequence.Next), anim, anim.Sequence.Next);
				}

				if (anim.Border != null && anim.Border.Next != null)
				{
					dot.Append("# ").Append(anim.Id).Append(" Border ").Append(anim.Border.Next.Length).Append("\r\n");
					ProcessNext(dot, Next.Border, TotalProbability(anim.Border.Next), anim, anim.Border.Next);
				}

				if (anim.Gravity != null && anim.Gravity.Next != null)
				{
					dot.Append("# ").Append(anim.Id).Append(" Gravity ").Append(anim.Gravity.Next.Length).Append("\r\n");
					ProcessNext(dot, Next.Gravity, TotalProbability(anim.Gravity.Next), anim, anim.Gravity.Next);
				}

				dot.Append("  anim_").Append(anim.Id).Append(" [ label=\"").Append(EscapeLabel(anim.Name))
				   .Append(" (").Append(anim.Id).Append(")\" ]\r\n");
			}
			dot.Append("}\n");
			return dot.ToString();
		}

		/// <summary>
		/// The text of a DOT double-quoted string: backslash and quote escaped, a line break written as \n,
		/// a carriage return dropped (F325). Everything an animation name or an "only" flag contributes to a
		/// label goes through here.
		/// </summary>
		internal static string EscapeLabel(string text)
		{
			if (string.IsNullOrEmpty(text)) return "";
			var escaped = new StringBuilder(text.Length + 8);
			foreach (char c in text)
			{
				switch (c)
				{
					case '\\': escaped.Append("\\\\"); break;
					case '"': escaped.Append("\\\""); break;
					case '\r': break;
					case '\n': escaped.Append("\\n"); break;
					default: escaped.Append(c); break;
				}
			}
			return escaped.ToString();
		}

		private static int TotalProbability(XmlData.NextNode[] nexts)
		{
			int total = 0;
			foreach (var next in nexts)
				if (next != null) total += next.Probability;
			return total;
		}

		private enum Next { Sequence, Border, Gravity };

		private const string sequenceColor = "black";
		private const string borderColor = "#5555DDFF";
		private const string gravityColor = "#55DD55FF";

		static private void ProcessNext(StringBuilder dot, Next type, int totalProbability, XmlData.AnimationNode anim, XmlData.NextNode[] nexts)
		{
			var edgeColor = sequenceColor;
			var typeMarker = "S";
			if (type == Next.Border) { edgeColor = borderColor; typeMarker = "B"; }
			if (type == Next.Gravity) { edgeColor = gravityColor; typeMarker = "G"; }

			foreach (var next in nexts)
			{
				if (next == null) continue;
				double relative = totalProbability > 0 ? (double)next.Probability / totalProbability : 0.0;
				string color = type == Next.Sequence ? ProbabilityToGray(relative) : edgeColor;
				string relative2Decimal = relative.ToString("00%");
				string probability = relative2Decimal == "100%" ? "" : "(" + next.Probability + ")";
				dot.Append("  anim_").Append(anim.Id).Append(" -> anim_").Append(next.Value)
				   .Append(" [ label=\"").Append(relative2Decimal).Append(probability).Append(' ')
				   .Append(EscapeLabel(next.OnlyFlag)).Append(' ').Append(typeMarker)
				   .Append("\" color=\"").Append(color).Append("\" fontcolor=\"").Append(color)
				   .Append("\" penwidth=\"1\" ]\r\n");
			}
		}

		static private string ProbabilityToGray(double relativeProbability)
		{
			// A relative probability of 0..1 maps linearly onto grey70 (light) .. grey0 (black):
			// p1 spans 30..100, and p2 mirrors it back into the grey scale.
			var p1 = Math.Floor((0.3 + (relativeProbability / (1 / 0.7))) * 100);
			var p2 = 70 - (p1 - 30);
			return "grey" + p2;
		}
	}
}
