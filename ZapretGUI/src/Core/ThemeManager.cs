using System.IO;
using System.Windows;
using System.Windows.Media;

namespace ZapretGUI.Core;

/// <summary>Переключение тем Windows 11 (Light/Dark/System) + акцентный цвет.</summary>
public static class ThemeManager
{
    private static ResourceDictionary? _current;

    public static void Apply(string theme, string accentHex)
    {
        var dict = new ResourceDictionary();
        bool dark = IsDark(theme);

        // Базовые кисти Win11 Mica/Control
        dict["WindowBackgroundBrush"]   = Frozen(dark ? "#202020" : "#F3F3F3");
        dict["LayerBrush"]              = Frozen(dark ? "#2B2B2B" : "#FBFBFB");
        dict["CardBrush"]               = Frozen(dark ? "#333333" : "#FFFFFF");
        dict["CardBorderBrush"]         = Frozen(dark ? "#3D3D3D" : "#E5E5E5");
        dict["SubtleBorderBrush"]       = Frozen(dark ? "#454545" : "#D6D6D6");
        dict["TextPrimaryBrush"]        = Frozen(dark ? "#FFFFFF" : "#1A1A1A");
        dict["TextSecondaryBrush"]      = Frozen(dark ? "#A0A0A0" : "#616161");
        dict["TextDisabledBrush"]       = Frozen(dark ? "#5F5F5F" : "#A5A5A5");
        dict["ControlFillBrush"]        = Frozen(dark ? "#373737" : "#EEEBE9");
        dict["ControlHoverBrush"]       = Frozen(dark ? "#454545" : "#F6F6F6");
        dict["ControlPressedBrush"]     = Frozen(dark ? "#2A2A2A" : "#DFDFDF");
        dict["ControlBorderBrush"]      = Frozen(dark ? "#494949" : "#868686");
        dict["AccentBrush"]             = Frozen(NormalizeHex(accentHex));
        dict["AccentHoverBrush"]        = Frozen(Lighten(NormalizeHex(accentHex), dark ? 0.25 : -0.08));
        dict["AccentPressedBrush"]      = Frozen(Lighten(NormalizeHex(accentHex), dark ? 0.10 : -0.18));
        dict["AccentTextBrush"]         = Frozen(dark ? "#1A1A1A" : "#FFFFFF");
        dict["AccentFadeBrush"]         = Frozen(WithAlpha(NormalizeHex(accentHex), (byte)(dark ? 0x3A : 0x22)));
        dict["SuccessBrush"]            = Frozen(dark ? "#6CCB5F" : "#0F7B0F");
        dict["WarningBrush"]            = Frozen(dark ? "#FDD735" : "#9D5D00");
        dict["ErrorBrush"]              = Frozen(dark ? "#FF6B6B" : "#C42B1C");
        dict["NavSelectedBrush"]        = Frozen(dark ? "#3D3D3D" : "#EAEAEA");
        dict["ScrollBrush"]             = Frozen(dark ? "#4D4D4D" : "#C4C4C4");
        dict["ToggleOffBrush"]          = Frozen(dark ? "#4D4D4D" : "#878787");
        dict["ToggleOffBorderBrush"]    = Frozen(dark ? "#9A9A9A" : "#3D3D3D");
        dict["BadgeBlockedBrush"]       = Frozen(dark ? "#5A1E1E" : "#FBE0DC");
        dict["BadgeOkBrush"]            = Frozen(dark ? "#1E4620" : "#DEF4E0");
        dict["BadgeWarnBrush"]          = Frozen(dark ? "#4D4010" : "#FFF4CE");
        dict["LogBgBrush"]              = Frozen(dark ? "#191919" : "#FAFAFA");

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current != null) merged.Remove(_current);
        _current = dict;
        merged.Add(dict);
    }

    public static bool IsDark(string theme)
    {
        if (string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)) return false;
        return GetSystemAppModeIsDark();
    }

    /// <summary>Реестр Personalize\AppsUseLightTheme (0 = тёмная тема ОС).</summary>
    private static bool GetSystemAppModeIsDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            if (v is int i) return i == 0;
        }
        catch { }
        return false;
    }

    private static System.Windows.Media.Color Parse(string hex)
    {
        hex = NormalizeHex(hex);
        byte a = 0xFF, r, g, b;
        var s = hex.TrimStart('#');
        if (s.Length == 8) { a = Convert.ToByte(s.Substring(0, 2), 16); s = s.Substring(2); }
        r = Convert.ToByte(s.Substring(0, 2), 16);
        g = Convert.ToByte(s.Substring(2, 2), 16);
        b = Convert.ToByte(s.Substring(4, 2), 16);
        return System.Windows.Media.Color.FromArgb(a, r, g, b);
    }

    private static string NormalizeHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "#0067C0";
        if (!hex.StartsWith("#")) hex = "#" + hex;
        if (hex.Length == 4) hex = "#" + hex[1] + hex[1] + hex[2] + hex[2] + hex[3] + hex[3];
        return hex.Length >= 7 ? hex.Substring(0, 7) : "#0067C0";
    }

    /// <summary>Публичная нормализация HEX-акцента (используется ViewModel для превью цвета).</summary>
    public static string NormalizeAccent(string hex) => NormalizeHex(hex);

    private static string Lighten(string hex, double delta)
    {
        var c = Parse(hex);
        byte F(double v) => (byte)Math.Max(0, Math.Min(255, v));
        if (delta >= 0)
            return $"#{F(c.R + (255 - c.R) * delta):X2}{F(c.G + (255 - c.G) * delta):X2}{F(c.B + (255 - c.B) * delta):X2}";
        var k = 1 + delta;
        return $"#{F(c.R * k):X2}{F(c.G * k):X2}{F(c.B * k):X2}";
    }

    private static string WithAlpha(string hex, byte a)
    {
        var c = Parse(hex);
        return $"#{a:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var br = new SolidColorBrush(Parse(hex));
        br.Freeze();
        return br;
    }
}
