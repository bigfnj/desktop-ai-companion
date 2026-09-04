using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopAICompanion.Modules;

namespace DesktopAICompanion.ModuleKit
{
    /// <summary>
    /// Reusable speech-bubble style controls for a module's options pane, plus the reader that turns the saved
    /// values into a <see cref="SpeechStyle"/> to pass to IHost.Say/SayAll. Every speaking module gets the SAME
    /// font / size / weight / colour controls in a couple of lines, each stored in that module's OWN settings,
    /// so speech styling is module-owned yet consistent across modules. The host renders whatever style it is
    /// handed; nothing here is central. "Default" choices map to unset SpeechStyle fields, so the bubble keeps
    /// its own look for those.
    /// </summary>
    public static class SpeechStyleSettings
    {
        // Namespaced setting ids so they cannot collide with a module's own keys.
        public const string FontKey = "speechFont";
        public const string SizeKey = "speechSize";
        public const string BoldKey = "speechBold";
        public const string ItalicKey = "speechItalic";
        public const string UnderlineKey = "speechUnderline";
        public const string ColorKey = "speechColor";

        private const string DefaultChoice = "Default";
        private const int DefaultSize = 9;
        private const int MinSize = 6;
        private const int MaxSize = 24;

        // A short, safe menu of families present on essentially every Windows box (plus "Default" = the
        // bubble's own font). A dropdown beats a free-text box: a typo would silently fall back, and 300
        // installed families is not a menu. Arbitrary font entry can come later if anyone asks.
        private static readonly string[] Fonts =
            { DefaultChoice, "Segoe UI", "Arial", "Calibri", "Verdana", "Tahoma", "Georgia",
              "Times New Roman", "Consolas", "Comic Sans MS" };

        private static readonly string[] Colors =
            { DefaultChoice, "Black", "Blue", "Green", "Red", "Orange", "Purple", "Teal", "Gray" };

        private static readonly Dictionary<string, string> ColorHex =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Black", "#000000" }, { "Blue", "#1E66F5" }, { "Green", "#2E8B57" },
                { "Red", "#CC3333" }, { "Orange", "#E08000" }, { "Purple", "#8A2BE2" },
                { "Teal", "#008080" }, { "Gray", "#606060" },
            };

        /// <summary>The style controls, to splice into an OptionsPane.Schema (put them in your own group).</summary>
        public static SettingField[] Fields(string group) { return Fields(group, ""); }

        /// <summary>As <see cref="Fields(string)"/>, but every field id is prefixed so several INDEPENDENT style
        /// sets can share one pane (e.g. one per calendar feed). Pass the SAME prefix to
        /// <see cref="AddLoadValues(IDictionary{string,string},IModuleSettings,string)"/>,
        /// <see cref="Save(IModuleSettings,IReadOnlyDictionary{string,string},string)"/> and
        /// <see cref="ToStyle(IModuleSettings,string)"/>; the field id doubles as the settings key.</summary>
        public static SettingField[] Fields(string group, string idPrefix)
        {
            string p = idPrefix ?? "";
            return new[]
            {
                new SettingField { Id = p + FontKey, Label = "Speech font", Kind = SettingKind.Enum, Options = Fonts, Group = group },
                new SettingField { Id = p + SizeKey, Label = "Speech size (pt)", Kind = SettingKind.Int, Min = MinSize, Max = MaxSize, Group = group },
                new SettingField { Id = p + BoldKey, Label = "Bold", Kind = SettingKind.Bool, Group = group },
                new SettingField { Id = p + ItalicKey, Label = "Italic", Kind = SettingKind.Bool, Group = group },
                new SettingField { Id = p + UnderlineKey, Label = "Underline", Kind = SettingKind.Bool, Group = group },
                new SettingField { Id = p + ColorKey, Label = "Text colour", Kind = SettingKind.Enum, Options = Colors, Group = group },
            };
        }

        /// <summary>Add the saved values of the style fields to a pane Load() dictionary.</summary>
        public static void AddLoadValues(IDictionary<string, string> values, IModuleSettings settings) { AddLoadValues(values, settings, ""); }

        /// <summary>Prefixed form of <see cref="AddLoadValues(IDictionary{string,string},IModuleSettings)"/>.</summary>
        public static void AddLoadValues(IDictionary<string, string> values, IModuleSettings settings, string keyPrefix)
        {
            if (values == null || settings == null) return;
            string p = keyPrefix ?? "";
            values[p + FontKey] = settings.Get(p + FontKey, DefaultChoice);
            values[p + SizeKey] = ClampSize(settings.GetInt(p + SizeKey, DefaultSize)).ToString(CultureInfo.InvariantCulture);
            values[p + BoldKey] = settings.GetBool(p + BoldKey, false) ? "true" : "false";
            values[p + ItalicKey] = settings.GetBool(p + ItalicKey, false) ? "true" : "false";
            values[p + UnderlineKey] = settings.GetBool(p + UnderlineKey, false) ? "true" : "false";
            values[p + ColorKey] = settings.Get(p + ColorKey, DefaultChoice);
        }

        /// <summary>Persist the style fields from a pane Save() values dictionary into the module settings.
        /// The caller still owns calling settings.Save().</summary>
        public static void Save(IModuleSettings settings, IReadOnlyDictionary<string, string> values) { Save(settings, values, ""); }

        /// <summary>Prefixed form of <see cref="Save(IModuleSettings,IReadOnlyDictionary{string,string})"/>.</summary>
        public static void Save(IModuleSettings settings, IReadOnlyDictionary<string, string> values, string keyPrefix)
        {
            if (settings == null || values == null) return;
            string p = keyPrefix ?? "";
            string v;
            if (values.TryGetValue(p + FontKey, out v) && !string.IsNullOrWhiteSpace(v)) settings.Set(p + FontKey, v.Trim());
            if (values.TryGetValue(p + SizeKey, out v))
            {
                int n;
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    settings.Set(p + SizeKey, ClampSize(n).ToString(CultureInfo.InvariantCulture));
            }
            SaveBool(settings, values, p + BoldKey);
            SaveBool(settings, values, p + ItalicKey);
            SaveBool(settings, values, p + UnderlineKey);
            if (values.TryGetValue(p + ColorKey, out v) && !string.IsNullOrWhiteSpace(v)) settings.Set(p + ColorKey, v.Trim());
        }

        /// <summary>Build the SpeechStyle to hand to IHost.Say/SayAll from the saved settings.</summary>
        public static SpeechStyle ToStyle(IModuleSettings settings) { return ToStyle(settings, ""); }

        /// <summary>Prefixed form of <see cref="ToStyle(IModuleSettings)"/>.</summary>
        public static SpeechStyle ToStyle(IModuleSettings settings, string keyPrefix)
        {
            if (settings == null) return null;
            string p = keyPrefix ?? "";
            string font = settings.Get(p + FontKey, DefaultChoice);
            string colorName = settings.Get(p + ColorKey, DefaultChoice);
            string colorHex = null;
            if (!IsDefault(colorName))
            {
                string hex;
                if (ColorHex.TryGetValue(colorName.Trim(), out hex)) colorHex = hex;
            }
            return new SpeechStyle
            {
                FontFamily = IsDefault(font) ? null : font.Trim(),
                FontSize = ClampSize(settings.GetInt(p + SizeKey, DefaultSize)),
                Bold = settings.GetBool(p + BoldKey, false),
                Italic = settings.GetBool(p + ItalicKey, false),
                Underline = settings.GetBool(p + UnderlineKey, false),
                TextColor = colorHex,
            };
        }

        private static void SaveBool(IModuleSettings s, IReadOnlyDictionary<string, string> values, string key)
        {
            string v; bool b;
            if (values.TryGetValue(key, out v) && bool.TryParse(v, out b)) s.Set(key, b ? "true" : "false");
        }

        private static bool IsDefault(string choice)
        {
            return string.IsNullOrWhiteSpace(choice) ||
                string.Equals(choice.Trim(), DefaultChoice, StringComparison.OrdinalIgnoreCase);
        }

        private static int ClampSize(int n) { return n < MinSize ? MinSize : (n > MaxSize ? MaxSize : n); }
    }
}
