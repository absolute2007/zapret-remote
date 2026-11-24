using System.Drawing;

namespace ZapretRemote.UI;

/// <summary>
/// Utilitarian dark slate palette for the desktop window and tray menu.
/// </summary>
public static class Theme
{
    public static readonly Color Background = Color.FromArgb(18, 21, 26);
    public static readonly Color Surface = Color.FromArgb(26, 30, 38);
    public static readonly Color SurfaceBorder = Color.FromArgb(46, 53, 66);
    public static readonly Color InputBackground = Color.FromArgb(34, 39, 49);

    public static readonly Color TextPrimary = Color.FromArgb(236, 240, 246);
    public static readonly Color TextSecondary = Color.FromArgb(148, 159, 178);

    public static readonly Color AccentGreen = Color.FromArgb(35, 134, 54);
    public static readonly Color AccentGreenHover = Color.FromArgb(46, 160, 67);
    public static readonly Color AccentRed = Color.FromArgb(184, 56, 59);
    public static readonly Color AccentRedHover = Color.FromArgb(207, 70, 73);
    public static readonly Color AccentAmber = Color.FromArgb(210, 153, 34);
    public static readonly Color AccentBlue = Color.FromArgb(56, 139, 253);

    public static readonly Color ButtonNeutral = Color.FromArgb(43, 49, 60);
    public static readonly Color ButtonNeutralHover = Color.FromArgb(56, 64, 78);

    public static readonly Font TitleFont = new("Segoe UI", 13f, FontStyle.Bold);
    public static readonly Font SectionFont = new("Segoe UI", 9.5f, FontStyle.Bold);
    public static readonly Font BodyFont = new("Segoe UI", 9f, FontStyle.Regular);
    public static readonly Font SmallFont = new("Segoe UI", 8.25f, FontStyle.Regular);
    public static readonly Font MonoFont = new("Consolas", 8.5f, FontStyle.Regular);
    public static readonly Font HeroButtonFont = new("Segoe UI", 10.5f, FontStyle.Bold);
}
