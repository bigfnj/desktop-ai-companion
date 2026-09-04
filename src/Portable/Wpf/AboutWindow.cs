using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace DesktopAICompanion.Wpf
{
    /// <summary>
    /// Programmatic (no-XAML) WPF About window — the themed replacement for the retired WinForms
    /// <c>AboutBox</c>, and now also the home of the former Help dialog (a single window, not two). Built the
    /// same way as <see cref="OptionsWindow"/> (DockPanel/StackPanel/TextBlock, explicit sizing,
    /// <see cref="WpfTheme.Apply"/> for the dark title bar + implicit control styles), shown modally from the
    /// WinForms UI thread. Top-to-bottom: the AI-Edition modernization blurb + project link; a "Using
    /// DesktopAICompanion" usage/help section with the project-doc links (allowlisted); the Original/Legacy upstream
    /// credits; and — at the very bottom — the active pet's author/title/version/info. The pet's <c>info</c>
    /// supports the same markup the old AboutBox did (<c>[br]</c> → line break, <c>[link:https://…]</c> →
    /// clickable link) parsed into WPF inlines routed through <see cref="WebLinks.TryOpen"/>. Read-only + a
    /// plain Close button (the old "Cancel = SyncSheeps()" behaviour is intentionally dropped).
    /// </summary>
    internal sealed class AboutWindow : Window
    {
        // Match the AboutBox's info cap so a pathological pet payload can't build an unbounded inline run.
        private const int InfoMaxChars = 8192;

        private readonly Brush _linkBrush = WpfHyperlinks.LinkBrush();

        public AboutWindow(string author, string title, string version, string info)
        {
            Title = "About Desktop AI Companion — Version " +
                System.Windows.Forms.Application.ProductVersion;
            Width = 560;
            Height = 640;
            MinWidth = 420;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WpfTheme.Apply(this);   // light/dark/system per preference; installs implicit styles + dark title bar

            var root = new DockPanel { Margin = new Thickness(10), LastChildFill = true };

            // Bottom bar: a single Close button on the right (mirrors OptionsWindow's chrome).
            var bottomBar = new DockPanel { LastChildFill = false };
            var close = new Button { Content = "_Close", Width = 84, Height = 26, Margin = new Thickness(4), IsCancel = true };
            close.Click += (s, e) => Close();
            DockPanel.SetDock(close, Dock.Right);
            bottomBar.Children.Add(close);
            DockPanel.SetDock(bottomBar, Dock.Bottom);
            root.Children.Add(bottomBar);

            var stack = new StackPanel { Margin = new Thickness(2, 2, 2, 8) };

            // --- What this is + who modernized it (top) ---
            stack.Children.Add(new TextBlock
            {
                Text = "AI Edition concept & build by BigFN'j",
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });
            stack.Children.Add(Line("Modernized to .NET 10 as a lean plugin host: a native WPF settings shell, isolated capability modules installed from an in-app catalog, and System.Text.Json throughout (no third-party JSON)."));
            stack.Children.Add(new TextBlock { Height = 6 });
            stack.Children.Add(Line("Desktop AI Companion is a Windows desktop companion. A sprite lives on your screen and, offline by default, speaks fortunes matched to whatever you are doing (via a local embedding model). An optional AI brain can narrate your screen through a local or OpenAI-compatible LLM; nothing leaves your machine unless you enable and configure a cloud provider."));
            stack.Children.Add(LinkLine("Project: ", "https://github.com/bigfnj/desktop-ai-companion", false));

            // --- Using the pet (folded in from the former Help dialog) ---
            stack.Children.Add(Header("Using DesktopAICompanion"));
            stack.Children.Add(Line("• Drag the companion to reposition it; right-click it to poke it."));
            stack.Children.Add(Line("• Right-click the tray icon for actions, Options, and Exit."));
            stack.Children.Add(Line("• Fortunes and smart matching run locally. The optional AI brain stays off until you configure and enable it."));
            stack.Children.Add(Line("• Review the Privacy notice before sending screen context to a provider."));
            stack.Children.Add(Line("• Portable ZIP copies keep data beside DesktopAICompanion.exe; MSI installs keep it under %LOCALAPPDATA%\\DesktopAICompanion."));
            stack.Children.Add(new TextBlock { Height = 4 });
            stack.Children.Add(Line("Documentation (opens in your browser on click):"));
            stack.Children.Add(LinkLine("• Privacy: ", "https://github.com/bigfnj/desktop-ai-companion/blob/master/PRIVACY.md", true));
            stack.Children.Add(LinkLine("• Support: ", "https://github.com/bigfnj/desktop-ai-companion/blob/master/SUPPORT.md", true));
            stack.Children.Add(LinkLine("• Security: ", "https://github.com/bigfnj/desktop-ai-companion/blob/master/SECURITY.md", true));
            stack.Children.Add(LinkLine("• Companion authoring: ", "https://github.com/bigfnj/desktop-ai-companion/blob/master/grimoire/03-pet-xml-format.md", true));
            stack.Children.Add(LinkLine("• Fortune packs: ", "https://github.com/bigfnj/desktop-ai-companion/blob/master/packs/README.md", true));
            stack.Children.Add(LinkLine("• Release status: ", "https://github.com/bigfnj/desktop-ai-companion/blob/master/docs/RELEASE-CHECKLIST.md", true));

            // --- Original / legacy credits (moved down, below our modernization notes) ---
            stack.Children.Add(Header("Original / Legacy"));
            stack.Children.Add(Line("Original concept and artwork by Tatsutoshi Nomura."));
            stack.Children.Add(Line("C# application code by Adriano Petrucci."));
            stack.Children.Add(Line("System-tray implementation by Sergi Fumanya Grunwaldt."));
            stack.Children.Add(LinkLine("Audio via NAudio (open source): ", "https://github.com/naudio/NAudio", false));
            stack.Children.Add(LinkLine("Upstream project (eSheep): ", "https://esheep.petrucci.ch", false));

            // --- The current pet (VERY bottom, per request) ---
            stack.Children.Add(Header("Information about the current companion"));
            stack.Children.Add(BuildPetInfoCard(author, title, version, info));

            root.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack,
            });

            Content = root;
        }

        private static TextBlock Line(string text)
        {
            return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
        }

        // A bold section heading with a little breathing room above it.
        private static TextBlock Header(string text)
        {
            return new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 4) };
        }

        // A line of prose ending in a single clickable link.
        private TextBlock LinkLine(string prefix, string url, bool projectDoc)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            if (!string.IsNullOrEmpty(prefix)) tb.Inlines.Add(new Run(prefix));
            tb.Inlines.Add(WpfHyperlinks.Link(url, url, projectDoc, _linkBrush));
            return tb;
        }

        // The active pet's author/title/version + its info text (with [br]/[link:] markup parsed to inlines),
        // wrapped in the same bordered card chrome OptionsWindow uses (theme-neutral in light + dark).
        private Border BuildPetInfoCard(string author, string title, string version, string info)
        {
            var inner = new StackPanel();
            inner.Children.Add(LabeledRow("Author:", author));
            inner.Children.Add(LabeledRow("Title:", title));
            inner.Children.Add(LabeledRow("Version:", version));
            inner.Children.Add(new TextBlock { Text = "Info:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 2) });

            var infoBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
            AppendInfoInlines(infoBlock, info);
            inner.Children.Add(infoBlock);

            return new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(8),
                Child = inner,
            };
        }

        private static DockPanel LabeledRow(string label, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), LastChildFill = true };
            var l = new TextBlock { Text = label, FontWeight = FontWeights.Bold, Width = 60, VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(l, Dock.Left);
            row.Children.Add(l);
            row.Children.Add(new TextBlock { Text = value ?? "", TextWrapping = TextWrapping.Wrap });
            return row;
        }

        /// <summary>
        /// Parse the pet's <c>info</c> into WPF inlines, honouring the AboutBox markup: <c>[br]</c> becomes a
        /// line break and <c>[link:https://…]</c> becomes a clickable hyperlink (the link text is the URL).
        /// Truncated at a code-point boundary to the same 8192-char cap the old AboutBox used.
        /// </summary>
        private void AppendInfoInlines(TextBlock target, string info)
        {
            info = UnicodeTextProgress.TruncateAtCodePointBoundary(info ?? "", InfoMaxChars);

            var pending = new StringBuilder();
            int i = 0;
            while (i < info.Length)
            {
                if (MatchAt(info, i, "[br]"))
                {
                    FlushRun(target, pending);
                    target.Inlines.Add(new LineBreak());
                    i += 4;
                    continue;
                }
                if (MatchAt(info, i, "[link:"))
                {
                    int close = info.IndexOf(']', i + 6);
                    if (close >= 0)
                    {
                        string url = info.Substring(i + 6, close - (i + 6));
                        FlushRun(target, pending);
                        target.Inlines.Add(WpfHyperlinks.Link(url, url, false, _linkBrush));
                        i = close + 1;
                        continue;
                    }
                }
                pending.Append(info[i]);
                i++;
            }
            FlushRun(target, pending);
        }

        private static void FlushRun(TextBlock target, StringBuilder pending)
        {
            if (pending.Length == 0) return;
            target.Inlines.Add(new Run(pending.ToString()));
            pending.Clear();
        }

        private static bool MatchAt(string text, int index, string token)
        {
            return index + token.Length <= text.Length &&
                string.Compare(text, index, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }
    }

    /// <summary>
    /// Shared hyperlink factory for the WPF About/Help windows: a theme-aware link brush and an inline
    /// <see cref="Hyperlink"/> whose navigation is routed through the security-reviewed <see cref="WebLinks"/>
    /// (arbitrary HTTPS, or the project-doc allowlist). A URL that isn't a well-formed absolute URI renders as
    /// plain text (no live link) so a malformed pet payload can't throw while the window builds.
    /// </summary>
    internal static class WpfHyperlinks
    {
        public static Brush LinkBrush()
        {
            string mode = "system";
            try { if (Program.MyData != null) mode = Program.MyData.GetThemeMode(); } catch { }
            bool dark = WpfTheme.EffectiveDark(mode);
            // Readable link colour on each background: a bright blue on the dark surface, a deeper blue on light.
            Color c = dark ? Color.FromRgb(0x6C, 0xB6, 0xFF) : Color.FromRgb(0x0A, 0x4A, 0xA6);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Inline Link(string text, string url, bool projectDoc, Brush brush)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                return new Run(text);   // not a live link, but still shown verbatim

            var link = new Hyperlink(new Run(text)) { NavigateUri = uri };
            if (brush != null) link.Foreground = brush;
            string captured = url;
            link.RequestNavigate += delegate(object sender, RequestNavigateEventArgs e)
            {
                if (projectDoc) WebLinks.TryOpenProjectDoc(captured);
                else WebLinks.TryOpen(captured);
                e.Handled = true;
            };
            return link;
        }
    }
}
