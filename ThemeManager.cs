using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace AudioWin
{
    public class AccentPreset
    {
        public string Name { get; set; }
        public string Primary { get; set; }
        public string Secondary { get; set; }

        public AccentPreset(string name, string primary, string secondary)
        {
            Name = name; Primary = primary; Secondary = secondary;
        }
    }

    /// <summary>
    /// Owns the colour palette. Everything visual in the app resolves its colours
    /// through DynamicResource lookups of the "C.*" keys, so writing new values
    /// into the dictionary that declares them re-skins the entire UI live.
    ///
    /// The old implementation replaced both the Color AND the Brush objects on
    /// Application.Current.Resources, which meant any code that had cached a
    /// brush kept rendering the previous theme. Here we only ever touch the
    /// Colors, and let WPF push the change through the brushes.
    /// </summary>
    public static class ThemeManager
    {
        public static readonly AccentPreset[] Accents =
        {
            new AccentPreset("Violet",  "#7C5CFF", "#B65CFF"),
            new AccentPreset("Ocean",   "#2E9BFF", "#22D3EE"),
            new AccentPreset("Ember",   "#FF7A45", "#FF4D8D"),
            new AccentPreset("Mint",    "#22C88A", "#7CE38B"),
            new AccentPreset("Rose",    "#FF5C8A", "#FF9A6C"),
            new AccentPreset("Gold",    "#F5A524", "#FFD466"),
        };

        private static ResourceDictionary paletteDictionary;

        /// <summary>Currently applied theme name, "Dark" or "Light".</summary>
        public static string CurrentTheme { get; private set; } = "Dark";

        /// <summary>Currently applied accent preset name.</summary>
        public static string CurrentAccent { get; private set; } = "Violet";

        /// <summary>Raised after a successful Apply so views can repaint owner-drawn bits.</summary>
        public static event Action ThemeChanged;

        public static void Apply(string theme = "Dark", string accentName = "White")
        {
            var dict = ResolvePaletteDictionary();
            if (dict == null) return;

            CurrentTheme = "Dark";
            CurrentAccent = "White";

            var map = DarkPalette();

            foreach (var kv in map)
                dict[kv.Key] = kv.Value;

            try { ThemeChanged?.Invoke(); } catch { }
        }

        private static Dictionary<string, Color> DarkPalette() => new Dictionary<string, Color>
        {
            ["C.Base"]        = Parse("#000000"),
            ["C.Surface"]     = Parse("#000000"),
            ["C.SurfaceAlt"]  = Parse("#050505"),
            ["C.Elevated"]    = Parse("#0A0A0A"),
            ["C.Overlay"]     = Parse("#121212"),
            ["C.Stroke"]      = Parse("#1E1E1E"),
            ["C.StrokeSoft"]  = Parse("#121212"),

            ["C.Text"]        = Parse("#FFFFFF"),
            ["C.TextMuted"]   = Parse("#A0A0A0"),
            ["C.TextDim"]     = Parse("#606060"),

            ["C.Accent"]      = Parse("#FFFFFF"),
            ["C.AccentAlt"]   = Parse("#FFFFFF"),
            ["C.AccentSoft"]  = Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF),
            ["C.OnAccent"]    = Parse("#000000"),

            ["C.Danger"]      = Parse("#F04A5C"),
            ["C.Success"]     = Parse("#3DD68C"),
            ["C.Warning"]     = Parse("#F5A524"),

            ["C.Hover"]       = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF),
            ["C.Pressed"]     = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
            ["C.Selected"]    = Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF),
            ["C.Scrim"]       = Color.FromArgb(0xD9, 0x00, 0x00, 0x00),
            ["C.Shadow"]      = Parse("#000000"),
        };

        private static Dictionary<string, Color> LightPalette() => DarkPalette();

        /// <summary>
        /// Walks the merged dictionary tree to find the dictionary that actually
        /// declares the palette. Writing into that dictionary (rather than into
        /// Application.Resources, which would merely shadow it) is what makes the
        /// DynamicResource subscribers re-evaluate.
        /// </summary>
        private static ResourceDictionary ResolvePaletteDictionary()
        {
            if (paletteDictionary != null) return paletteDictionary;
            var app = Application.Current?.Resources;
            if (app == null) return null;
            paletteDictionary = Find(app, new HashSet<ResourceDictionary>());
            return paletteDictionary;
        }

        private static ResourceDictionary Find(ResourceDictionary dict, HashSet<ResourceDictionary> seen)
        {
            if (dict == null || !seen.Add(dict)) return null;
            if (dict.Contains("C.Base") && dict.Contains("C.Accent")) return dict;
            foreach (var child in dict.MergedDictionaries)
            {
                var hit = Find(child, seen);
                if (hit != null) return hit;
            }
            return null;
        }

        public static Color Parse(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return Colors.Magenta; }
        }

        private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        /// <summary>Picks black or white text for best contrast against a background.</summary>
        private static Color BestTextOn(Color bg)
        {
            // Relative luminance, sRGB coefficients.
            double L = (0.2126 * Channel(bg.R) + 0.7152 * Channel(bg.G) + 0.0722 * Channel(bg.B));
            return L > 0.55 ? Color.FromRgb(0x10, 0x10, 0x16) : Colors.White;
        }

        private static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        /// <summary>Convenience lookup for code that needs a live theme brush.</summary>
        public static System.Windows.Media.Brush Brush(string key)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is System.Windows.Media.Brush b) return b;
            }
            catch { }
            return Brushes.Gray;
        }

        public static Color ColorOf(string key)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is Color c) return c;
            }
            catch { }
            return Colors.Gray;
        }
    }
}
