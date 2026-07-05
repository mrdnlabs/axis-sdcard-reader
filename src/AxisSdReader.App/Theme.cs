using System.Windows;
using System.Windows.Media;

namespace AxisSdReader.App;

/// <summary>
/// The design's token-based theming system: two themes (Dark default, Light) with a fixed Trust Blue
/// accent. Tokens are published as brushes/colors in application resources under "T.*" keys; all UI
/// consumes them via DynamicResource. Everything except the video surface re-themes. Custom-drawn
/// controls listen to <see cref="Changed"/>.
/// </summary>
public static class Theme
{
    public static event Action? Changed;

    public static bool IsDark { get; private set; } = true;

    private sealed record AccentDef(string A, string Hover, string InkDark, string InkLight);

    // Fixed accent (Trust Blue).
    private static readonly AccentDef TheAccent = new("#3b82f6", "#2f6fed", "#8bb4ff", "#1a4fc0");

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var r = System.Windows.Application.Current.Resources;

        // --- neutrals: token → (dark, light) ---------------------------------
        Set(r, "Bg", dark ? "#0e1116" : "#eef0f3");
        Set(r, "Body", dark ? "#070a0d" : "#e7e9ed");
        Set(r, "Panel", dark ? "#161b22" : "#ffffff");
        Set(r, "Panel2", dark ? "#12161c" : "#f7f8fa");
        Set(r, "Panel3", dark ? "#10141a" : "#fbfbfc");
        Set(r, "Subtle", dark ? "#1b212a" : "#f4f6f9");
        Set(r, "Subtle2", dark ? "#0f141a" : "#f0f2f5");
        Set(r, "Chip", dark ? "#232a34" : "#eef1f5");
        Set(r, "Hover", dark ? "#222a33" : "#eef1f6");
        Set(r, "Border", dark ? "#232932" : "#e2e5e9");
        Set(r, "Border2", dark ? "#2a313b" : "#e2e7ef");
        Set(r, "Border3", dark ? "#1c222a" : "#eaecef");
        Set(r, "Hair", dark ? "#262d37" : "#e6e9ee");
        Set(r, "TlBorder", dark ? "#242b34" : "#e4e7ec");
        Set(r, "TlLine", dark ? "#1c232c" : "#e0e4ea");
        Set(r, "Text", dark ? "#eaedf2" : "#1f2328");
        Set(r, "Text2", dark ? "#c4cbd4" : "#3a4048");
        Set(r, "Text3", dark ? "#98a2ad" : "#5b6470");
        Set(r, "Muted", dark ? "#7c8590" : "#8a929c");
        Set(r, "Muted2", dark ? "#8b95a0" : "#96a0ab");
        Set(r, "Muted3", dark ? "#828c98" : "#9aa3ae");
        Set(r, "Faint", dark ? "#5f6a77" : "#a2abb5");
        Set(r, "Disabled", dark ? "#3f4854" : "#b3bcc7");
        Set(r, "Playhead", dark ? "#eef2f7" : "#12203a");
        Set(r, "PlayheadHalo", dark ? "#80000000" : "#99FFFFFF");
        Set(r, "Statusbar", dark ? "#0b0e13" : "#f2f3f6");
        Set(r, "Video", dark ? "#05070a" : "#0d0f12");
        Set(r, "VideoBorder", dark ? "#222932" : "#00000000");
        Set(r, "Scrim", dark ? "#9E020408" : "#571C2128");
        Set(r, "Trust", dark ? "#2fbf7a" : "#1a8a5a");
        Set(r, "TrustText", dark ? "#63d39b" : "#136c46");
        Set(r, "TrustText2", dark ? "#4bb98a" : "#2f8a63");
        Set(r, "TrustBg", dark ? "#241a8a5a" : "#eaf6ef");
        Set(r, "TrustBorder", dark ? "#522fbf7a" : "#bfe4cf");

        // --- accent -----------------------------------------------------------
        var a = TheAccent;
        var accentColor = (Color)ColorConverter.ConvertFromString(a.A);
        Set(r, "Accent", a.A);
        Set(r, "AccentH", a.Hover);
        Set(r, "AccentInk", dark ? a.InkDark : a.InkLight);
        SetAlpha(r, "AccentTint", accentColor, dark ? 0.16 : 0.10);
        SetAlpha(r, "AccentTint2", accentColor, dark ? 0.12 : 0.07);
        SetAlpha(r, "AccentHalo", accentColor, dark ? 0.30 : 0.22);
        r["T.AccentColor"] = accentColor; // for drop shadows

        // Export-range/mark color: a fixed yellow that never collides with the blue accent.
        var sel = "#f7c948";
        var selColor = (Color)ColorConverter.ConvertFromString(sel);
        Set(r, "Sel", sel);
        SetAlpha(r, "SelFill", selColor, 0.30);

        Changed?.Invoke();
    }

    private static void Set(ResourceDictionary r, string token, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        r["T." + token] = brush;
    }

    private static void SetAlpha(ResourceDictionary r, string token, Color baseColor, double alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), baseColor.R, baseColor.G, baseColor.B));
        brush.Freeze();
        r["T." + token] = brush;
    }

    /// <summary>Convenience for custom-drawn controls: current brush for a token.</summary>
    public static Brush Brush(string token) =>
        System.Windows.Application.Current.TryFindResource("T." + token) as Brush ?? Brushes.Magenta;
}
