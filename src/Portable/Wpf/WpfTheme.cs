using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // TextBoxBase.CaretBrushProperty
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;                 // XamlReader (dark scrollbar template)
using System.Windows.Media;

namespace DesktopAICompanion.Wpf
{
    /// <summary>
    /// A light/dark theme for the WPF settings window (S5b), following the user's preference
    /// ("system" / "light" / "dark"). System mode reads the OS setting via <see cref="WindowTheme.IsDark"/>
    /// (the same registry check the WinForms tray dialogs use). Dark mode paints the window and installs
    /// implicit control styles so the chrome (nav, buttons, inputs) follows; light mode keeps the stock WPF
    /// look (lower risk than fighting the default light templates). The immersive dark title bar is applied
    /// once the window has a handle. Applied when the window opens; a preference change takes effect on the
    /// next open.
    /// </summary>
    internal static class WpfTheme
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1 = 19;

        // Dark palette (mirrors WindowTheme's WinForms colours so the two UIs match).
        private static readonly Brush Bg = Freeze(Color.FromRgb(0x20, 0x20, 0x20));
        private static readonly Brush Surface = Freeze(Color.FromRgb(0x2D, 0x2D, 0x30));   // inputs / lists / buttons
        private static readonly Brush Text = Freeze(Color.FromRgb(0xF0, 0xF0, 0xF0));
        private static readonly Brush Border = Freeze(Color.FromRgb(0x46, 0x46, 0x4A));

        private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>
        /// The window background a dark pane is drawn on. Exposed for --wpf-options-selftest, which renders a
        /// pane under <see cref="AddDarkResources"/> to measure what a greyed row LOOKS like (host 1.4.0).
        /// </summary>
        internal static Brush DarkBackground { get { return Bg; } }

        /// <summary>
        /// Resource key for how far a greyed settings row is dimmed (host 1.4.0, lane feature/settings-primitives).
        ///
        /// EnabledWhen greys a row by setting IsEnabled, and IsEnabled alone did not LOOK like anything in the
        /// dark theme: the implicit TextBlock style sets a fixed foreground with no disabled state, the dark
        /// ComboBox template has no IsEnabled trigger, and a ✓/✗ Info line carries its own colour, so a greyed
        /// row kept a full-contrast label beside a live-looking dropdown. The PaneView dims the row it greys by
        /// referencing this key, once, at the row (or the card body), so every control kind inside dims by the
        /// same amount, colours included. The amount is the THEME's call, which is why it is a resource rather
        /// than a number in the renderer: the stock light controls already grey themselves, so light dims less.
        ///
        /// The rejected alternative was a disabled trigger on every implicit style (TextBlock, ComboBox,
        /// CheckBox, RadioButton). It dims elements, not rows, so a radio option's own TextBlock and its
        /// RadioButton would each dim and multiply, and a locally coloured ✓/✗ line would not dim at all.
        /// </summary>
        internal const string DisabledOpacityKey = "dpDisabledOpacity";

        /// <summary>Dark: about what the approved mockup drew (0.42). Light: less, because Aero2's disabled
        /// editors already go grey on their own and a row dimmed on top of that went faint.</summary>
        internal const double DarkDisabledOpacity = 0.45;
        internal const double LightDisabledOpacity = 0.6;

        /// <summary>Whether the effective theme is dark for the given preference ("system" consults the OS).</summary>
        public static bool EffectiveDark(string mode)
        {
            if (string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase)) return false;
            try { return WindowTheme.IsDark(); } catch { return false; }
        }

        public static void Apply(Window window)
        {
            if (window == null) return;
            string mode = "system";
            try { if (Program.MyData != null) mode = Program.MyData.GetThemeMode(); } catch { }
            bool dark = EffectiveDark(mode);

            // The immersive title bar needs a window handle; set it once the source is initialized.
            window.SourceInitialized += delegate
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(window).Handle;
                    int v = dark ? 1 : 0;
                    if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)) != 0)
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1, ref v, sizeof(int));
                }
                catch { }
            };

            if (!dark)
            {
                AddLightResources(window.Resources);
                return;
            }

            window.Background = Bg;
            window.Foreground = Text;
            AddDarkResources(window.Resources);
        }

        /// <summary>Light mode keeps the stock WPF look; the one thing it installs is how far a greyed row dims
        /// (host 1.4.0). Split out like <see cref="AddDarkResources"/>, for the same self-test.</summary>
        internal static void AddLightResources(ResourceDictionary res)
        {
            if (res == null) return;
            res[DisabledOpacityKey] = LightDisabledOpacity;
        }

        /// <summary>
        /// Every implicit style and resource the dark theme installs, into any dictionary. Split out of
        /// <see cref="Apply"/> (host 1.4.0) so --wpf-options-selftest can draw a pane exactly as the dark window
        /// does and measure the pixels: a check on IsEnabled had passed while a greyed dropdown looked live.
        /// </summary>
        internal static void AddDarkResources(ResourceDictionary res)
        {
            if (res == null) return;
            res[DisabledOpacityKey] = DarkDisabledOpacity;
            Implicit(res, typeof(TextBlock), new Setter(TextBlock.ForegroundProperty, Text));
            // No Label style: the shell and the schema-rendered module panes build TextBlocks, and PetStudio
            // themes its own window, so a Label style here styled nothing in any window (F374).
            // Button is NOT styled here any more: its implicit style moved into BuildComboResources' XAML with
            // a full dark template (host 1.4.0), and a style left in this dictionary would win over it, since a
            // dictionary's own entries are found before its merged ones.
            Implicit(res, typeof(TextBox),
                new Setter(Control.BackgroundProperty, Surface),
                new Setter(Control.ForegroundProperty, Text),
                new Setter(Control.BorderBrushProperty, Border),
                new Setter(TextBoxBase.CaretBrushProperty, Text));
            Implicit(res, typeof(PasswordBox),
                new Setter(Control.BackgroundProperty, Surface),
                new Setter(Control.ForegroundProperty, Text),
                new Setter(Control.BorderBrushProperty, Border));
            // ComboBox gets a full dark template (below): the stock template's dropdown popup ignores
            // Background/Foreground set on the ComboBox, so its items render on a light popup — unreadable.
            Implicit(res, typeof(ListBox),
                new Setter(Control.BackgroundProperty, Surface),
                new Setter(Control.ForegroundProperty, Text),
                new Setter(Control.BorderBrushProperty, Border));
            Implicit(res, typeof(CheckBox), new Setter(Control.ForegroundProperty, Text));
            // RadioButton needs exactly what CheckBox needs, and did not get it until the
            // options pane grew its first radio group: the stock foreground is near-black,
            // so every option rendered as black text on a dark card and the one control
            // whose entire job is to be read was the one that could not be. Nothing was
            // wrong with the radio; the theme simply had no opinion about it.
            Implicit(res, typeof(RadioButton), new Setter(Control.ForegroundProperty, Text));
            // Grouped list cards (fortune packs) use Expander section headers; without this its header
            // text/chevron keep the stock near-black foreground and vanish against the dark card.
            Implicit(res, typeof(Expander), new Setter(Control.ForegroundProperty, Text));
            Implicit(res, typeof(Separator), new Setter(Control.BackgroundProperty, Border));
            res[typeof(ScrollBar)] = BuildScrollBarStyle();   // WPF scrollbars are light by default
            res.MergedDictionaries.Add(BuildComboResources());   // dark ComboBox + readable popup, dark Button
        }

        // A dark ComboBox: the stock template's dropdown popup uses SystemColors (a light popup with faint
        // text regardless of the Background/Foreground set on the control), so we supply a full template —
        // a dark closed box + a dark popup — plus a ComboBoxItem style with readable text and a hover/select
        // highlight. Parsed as a ResourceDictionary so the ComboBox + ComboBoxItem styles register as implicit
        // (keyed by type) and apply to every combo in the window, including the items in the popup. The dark
        // Button lives here too (host 1.4.0): it needs a full template for the same reason the combo does.
        private static ResourceDictionary BuildComboResources()
        {
            const string xaml = @"
<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <SolidColorBrush x:Key=""dpSurface"" Color=""#FF2D2D30""/>
  <SolidColorBrush x:Key=""dpText"" Color=""#FFF0F0F0""/>
  <SolidColorBrush x:Key=""dpBorder"" Color=""#FF46464A""/>
  <SolidColorBrush x:Key=""dpHighlight"" Color=""#FF3D5A80""/>
  <SolidColorBrush x:Key=""dpHover"" Color=""#FF3E3E42""/>
  <SolidColorBrush x:Key=""dpHoverBorder"" Color=""#FF5A5A5E""/>
  <SolidColorBrush x:Key=""dpDisabledText"" Color=""#FF777777""/>
  <SolidColorBrush x:Key=""dpDisabledBorder"" Color=""#FF3A3A3D""/>
  <!-- A dark Button, host 1.4.0. The stock Aero2 template hard-codes its state colours inside the template,
       so the Background set on a dark button lasted only until a trigger fired: a DISABLED button turned the
       light grey F4F4F4 with grey text, a pale box on the dark card (the Apply button whenever nothing is
       unsaved, every action button while it runs, and every button in a card greyed as a whole), and a HOVERED
       one turned light blue under light text. The disabled values are the approved mockup's (surface, 777
       text, 3A3A3D border). RecognizesAccessKey keeps the mnemonics in _Apply and _Close. -->
  <Style TargetType=""{x:Type Button}"">
    <Setter Property=""Foreground"" Value=""{StaticResource dpText}""/>
    <Setter Property=""Background"" Value=""{StaticResource dpSurface}""/>
    <Setter Property=""BorderBrush"" Value=""{StaticResource dpBorder}""/>
    <Setter Property=""BorderThickness"" Value=""1""/>
    <Setter Property=""Padding"" Value=""1""/>
    <Setter Property=""HorizontalContentAlignment"" Value=""Center""/>
    <Setter Property=""VerticalContentAlignment"" Value=""Center""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""{x:Type Button}"">
          <Border x:Name=""bd"" Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}""
              BorderThickness=""{TemplateBinding BorderThickness}"" SnapsToDevicePixels=""True"">
            <ContentPresenter x:Name=""cp"" Margin=""{TemplateBinding Padding}"" Focusable=""False"" RecognizesAccessKey=""True""
                HorizontalAlignment=""{TemplateBinding HorizontalContentAlignment}""
                VerticalAlignment=""{TemplateBinding VerticalContentAlignment}"" SnapsToDevicePixels=""True""/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""bd"" Property=""Background"" Value=""{StaticResource dpHover}""/>
              <Setter TargetName=""bd"" Property=""BorderBrush"" Value=""{StaticResource dpHoverBorder}""/>
            </Trigger>
            <Trigger Property=""IsPressed"" Value=""True"">
              <Setter TargetName=""bd"" Property=""Background"" Value=""{StaticResource dpHighlight}""/>
            </Trigger>
            <Trigger Property=""IsEnabled"" Value=""False"">
              <Setter TargetName=""bd"" Property=""Background"" Value=""{StaticResource dpSurface}""/>
              <Setter TargetName=""bd"" Property=""BorderBrush"" Value=""{StaticResource dpDisabledBorder}""/>
              <Setter TargetName=""cp"" Property=""TextElement.Foreground"" Value=""{StaticResource dpDisabledText}""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType=""{x:Type ComboBoxItem}"">
    <Setter Property=""Foreground"" Value=""{StaticResource dpText}""/>
    <Setter Property=""Background"" Value=""Transparent""/>
    <Setter Property=""Padding"" Value=""6,3""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""{x:Type ComboBoxItem}"">
          <Border x:Name=""bd"" Background=""{TemplateBinding Background}"" Padding=""{TemplateBinding Padding}"" SnapsToDevicePixels=""True"">
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsHighlighted"" Value=""True"">
              <Setter TargetName=""bd"" Property=""Background"" Value=""{StaticResource dpHighlight}""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType=""{x:Type ComboBox}"">
    <Setter Property=""Foreground"" Value=""{StaticResource dpText}""/>
    <Setter Property=""Background"" Value=""{StaticResource dpSurface}""/>
    <Setter Property=""BorderBrush"" Value=""{StaticResource dpBorder}""/>
    <Setter Property=""SnapsToDevicePixels"" Value=""True""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""{x:Type ComboBox}"">
          <Grid>
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width=""*""/>
              <ColumnDefinition Width=""18""/>
            </Grid.ColumnDefinitions>
            <ToggleButton Grid.ColumnSpan=""2"" Focusable=""False"" ClickMode=""Press""
                IsChecked=""{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"">
              <ToggleButton.Template>
                <ControlTemplate TargetType=""{x:Type ToggleButton}"">
                  <Border Background=""{StaticResource dpSurface}"" BorderBrush=""{StaticResource dpBorder}"" BorderThickness=""1"" CornerRadius=""2""/>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter Grid.Column=""0"" Margin=""6,0,0,0"" VerticalAlignment=""Center"" HorizontalAlignment=""Left""
                Content=""{TemplateBinding SelectionBoxItem}""
                ContentTemplate=""{TemplateBinding SelectionBoxItemTemplate}""
                IsHitTestVisible=""False""/>
            <Path Grid.Column=""1"" HorizontalAlignment=""Center"" VerticalAlignment=""Center""
                Data=""M0,0 L4,4 L8,0 Z"" Fill=""{StaticResource dpText}""/>
            <Popup x:Name=""PART_Popup"" Placement=""Bottom"" AllowsTransparency=""True"" Focusable=""False""
                IsOpen=""{TemplateBinding IsDropDownOpen}"" PopupAnimation=""Slide"">
              <Border Background=""{StaticResource dpSurface}"" BorderBrush=""{StaticResource dpBorder}"" BorderThickness=""1""
                  MinWidth=""{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}""
                  MaxHeight=""{TemplateBinding MaxDropDownHeight}"">
                <ScrollViewer SnapsToDevicePixels=""True""><ItemsPresenter/></ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
            return (ResourceDictionary)XamlReader.Parse(xaml);
        }

        // A minimal dark vertical scrollbar (thin dark track + rounded grey thumb). Vertical-only is
        // sufficient because every ScrollViewer in the window disables its horizontal bar: the ones the
        // panes construct do so explicitly, and the nav ListBox -- the one ScrollViewer this code does not
        // build, which kept WPF's horizontal Auto -- has it disabled in OptionsWindow (F377). A horizontal
        // bar given this template renders as a squashed vertical track, which is how PetStudio's copy of
        // it broke that window's horizontal bars before it was removed there.
        private static Style BuildScrollBarStyle()
        {
            const string xaml =
                "<ControlTemplate TargetType=\"{x:Type ScrollBar}\" " +
                "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
                "<Grid Background=\"#FF202020\">" +
                "<Track Name=\"PART_Track\" Orientation=\"Vertical\" IsDirectionReversed=\"True\">" +
                "<Track.Thumb><Thumb><Thumb.Template>" +
                "<ControlTemplate TargetType=\"{x:Type Thumb}\">" +
                "<Border Background=\"#FF5A5A5E\" CornerRadius=\"4\" Margin=\"2,1,2,1\"/>" +
                "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
                "</Track></Grid></ControlTemplate>";
            var style = new Style(typeof(ScrollBar));
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 12.0));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Bg));
            style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(xaml)));
            return style;
        }

        private static void Implicit(ResourceDictionary res, Type target, params Setter[] setters)
        {
            var style = new Style(target);
            foreach (Setter s in setters) style.Setters.Add(s);
            res[target] = style;   // no x:Key => implicit style for every instance of the type in this window
        }
    }

    /// <summary>Text helpers the panes share. <see cref="Short"/> trims an exception message to a status line;
    /// the Companions and Modules panes each carried an identical copy (F337).</summary>
    internal static class PaneText
    {
        internal static string Short(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            message = message.Trim();
            return message.Length > 200 ? message.Substring(0, 200) + "…" : message;
        }
    }
}
