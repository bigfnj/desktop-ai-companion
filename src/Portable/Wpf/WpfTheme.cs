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

            if (!dark) return;   // light mode: keep the stock WPF look

            window.Background = Bg;
            window.Foreground = Text;
            ResourceDictionary res = window.Resources;
            Implicit(res, typeof(TextBlock), new Setter(TextBlock.ForegroundProperty, Text));
            Implicit(res, typeof(Label), new Setter(Control.ForegroundProperty, Text));
            Implicit(res, typeof(Button),
                new Setter(Control.BackgroundProperty, Surface),
                new Setter(Control.ForegroundProperty, Text),
                new Setter(Control.BorderBrushProperty, Border));
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
            // Grouped list cards (fortune packs) use Expander section headers; without this its header
            // text/chevron keep the stock near-black foreground and vanish against the dark card.
            Implicit(res, typeof(Expander), new Setter(Control.ForegroundProperty, Text));
            Implicit(res, typeof(Separator), new Setter(Control.BackgroundProperty, Border));
            res[typeof(ScrollBar)] = BuildScrollBarStyle();   // WPF scrollbars are light by default
            window.Resources.MergedDictionaries.Add(BuildComboResources());   // dark ComboBox + readable popup
        }

        // A dark ComboBox: the stock template's dropdown popup uses SystemColors (a light popup with faint
        // text regardless of the Background/Foreground set on the control), so we supply a full template —
        // a dark closed box + a dark popup — plus a ComboBoxItem style with readable text and a hover/select
        // highlight. Parsed as a ResourceDictionary so the ComboBox + ComboBoxItem styles register as implicit
        // (keyed by type) and apply to every combo in the window, including the items in the popup.
        private static ResourceDictionary BuildComboResources()
        {
            const string xaml = @"
<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <SolidColorBrush x:Key=""dpSurface"" Color=""#FF2D2D30""/>
  <SolidColorBrush x:Key=""dpText"" Color=""#FFF0F0F0""/>
  <SolidColorBrush x:Key=""dpBorder"" Color=""#FF46464A""/>
  <SolidColorBrush x:Key=""dpHighlight"" Color=""#FF3D5A80""/>
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

        // A minimal dark vertical scrollbar (thin dark track + rounded grey thumb). The window's
        // ScrollViewers disable the horizontal bar, so a vertical-only template is sufficient.
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
}
