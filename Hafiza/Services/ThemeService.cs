using System;
using System.Windows;
using System.Windows.Media;

namespace Hafiza.Services;

public static class ThemeService
{
    public const string Dark = "dark";
    public const string Light = "light";

    public static string CurrentTheme { get; set; } = Dark;

    public static bool IsDark => !string.Equals(CurrentTheme, Light, StringComparison.OrdinalIgnoreCase);

    public static void ApplyTheme(string? theme, ResourceDictionary? resources = null)
    {
        CurrentTheme = string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
        var res = resources ?? Application.Current?.Resources;
        if (res is null) return;

        if (IsDark)
        {
            SetDarkTheme(res);
        }
        else
        {
            SetLightTheme(res);
        }
    }

    private static void SetDarkTheme(ResourceDictionary res)
    {
        var canvas = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(Color.FromRgb(0x10, 0x1C, 0x22), 0),
                new(Color.FromRgb(0x0C, 0x14, 0x1A), 0.55),
                new(Color.FromRgb(0x11, 0x28, 0x2B), 1)
            }
        };
        canvas.Freeze();
        res["CanvasBrush"] = canvas;

        res["PanelBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x23, 0x26)));
        res["CardBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)));
        res["CardBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x32, 0x36, 0x38)));
        res["CardHoverBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)));
        res["PrimaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xF6)));
        res["SecondaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xAA, 0xB2, 0xB0)));
        res["AccentBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0xDF, 0xC9)));
        res["DangerBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x7B, 0x7B)));

        res["SurfaceBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x36, 0x50, 0x58)));
        res["SectionBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x1D, 0x23)));
        res["SectionBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x23, 0x3D, 0x44)));
        res["SearchBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x2B, 0x33)));
        res["SearchBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x35, 0x54, 0x5D)));
        res["SearchHintBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x79, 0x81, 0x7F)));
        res["FolderCardBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x18, 0x2C, 0x32)));
        res["FolderCardBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x57, 0x5C)));
        res["FolderAreaBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x24)));
        res["FolderAreaBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x38)));
        res["EmptyStateBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x1F, 0x25)));
        res["EmptyStateBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x3E, 0x45)));
        res["DragHandleBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x28)));
        res["DragDotBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xA2)));
        res["SettingsBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x17, 0x19, 0x1B)));
        res["BadgeBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x24, 0x3C, 0x3A)));
        res["IconBoxBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x12, 0x3D, 0x40)));
        res["IconBoxBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0xDF, 0xC9)));

        res["IconButtonForegroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xAA, 0xB2, 0xB0)));
        res["IconButtonHoverBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0x38, 0x3B)));
        res["IconButtonHoverForegroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
        res["IconButtonSelectedBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0x46, 0x3F)));

        res["PillBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x24, 0x28, 0x2A)));
        res["PillForegroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xAA, 0xB2, 0xB0)));
        res["PillHoverBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0x3A, 0x3C)));
        res["PillHoverForegroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
        res["PillSelectedBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x18, 0x4B, 0x40)));
        res["PillSelectedForegroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0xDF, 0xC9)));

        res["CheckBoxBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x25, 0x27)));
        res["CheckBoxBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x87, 0x91, 0x8E)));
        res["CheckBoxMarkBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x20, 0x1C)));

        res["PinIconBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xA4, 0xAE, 0xAB)));
        res["DeleteIconBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xB4, 0xFF)));
        res["CopyIconBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x9D, 0xED, 0xE3)));
        res["CopyIconSecondaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x72, 0xCF, 0xC4)));
        res["ThemeIconBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3B)));

        res[SystemColors.MenuBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x28)));
        res[SystemColors.MenuTextBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0xDD, 0xE5, 0xE2)));
        res[SystemColors.HighlightBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0x28, 0x52, 0x49)));
        res[SystemColors.HighlightTextBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xFF, 0xFC)));
        res[SystemColors.ControlBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x28)));
        res[SystemColors.ControlTextBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0xDD, 0xE5, 0xE2)));
    }

    private static void SetLightTheme(ResourceDictionary res)
    {
        var canvas = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(Color.FromRgb(0xF5, 0xF8, 0xF9), 0),
                new(Color.FromRgb(0xEC, 0xF2, 0xF3), 0.55),
                new(Color.FromRgb(0xE0, 0xEA, 0xED), 1)
            }
        };
        canvas.Freeze();
        res["CanvasBrush"] = canvas;

        res["PanelBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xFB, 0xFC, 0xFC)));
        res["CardBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
        res["CardBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xD0, 0xDE, 0xE0)));
        res["CardHoverBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF6, 0xF7)));
        res["PrimaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x1A, 0x1C)));
        res["SecondaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x45, 0x54, 0x58)));
        res["AccentBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x0C, 0x93, 0x84)));
        res["DangerBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)));

        res["SurfaceBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xC2, 0xD2, 0xD4)));
        res["SectionBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xEE, 0xF0)));
        res["SectionBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xCD, 0xDB, 0xDD)));
        res["SearchBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
        res["SearchBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xB8, 0xC8, 0xCB)));
        res["SearchHintBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x60, 0x70, 0x73)));
        res["FolderCardBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xF1, 0xF3)));
        res["FolderCardBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xB6, 0xC9, 0xCD)));
        res["FolderAreaBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xE4, 0xEE, 0xF0)));
        res["FolderAreaBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xC0, 0xD0, 0xD3)));
        res["EmptyStateBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xEB, 0xF2, 0xF4)));
        res["EmptyStateBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0xD4, 0xD6)));
        res["DragHandleBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xDD, 0xE7, 0xE9)));
        res["DragDotBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x48, 0x5A, 0x5D)));
        res["SettingsBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF9, 0xFA)));
        res["BadgeBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xCE, 0xEA, 0xE5)));
        res["IconBoxBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xD2, 0xEF, 0xEB)));
        res["IconBoxBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x0C, 0x93, 0x84)));

        res["IconButtonForegroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x31, 0x34)));
        res["IconButtonHoverBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xD6, 0xE3, 0xE5)));
        res["IconButtonHoverForegroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x13, 0x15)));
        res["IconButtonSelectedBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0xEA, 0xE4)));

        res["PillBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xDF, 0xEB, 0xED)));
        res["PillForegroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x32, 0x35)));
        res["PillHoverBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xD0, 0xDE, 0xE0)));
        res["PillHoverForegroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x12, 0x14)));
        res["PillSelectedBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x0C, 0x93, 0x84)));
        res["PillSelectedForegroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));

        res["CheckBoxBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
        res["CheckBoxBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x76, 0x89, 0x8C)));
        res["CheckBoxMarkBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));

        res["PinIconBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x45, 0x58, 0x5C)));
        res["DeleteIconBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)));
        res["CopyIconBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x09, 0x80, 0x72)));
        res["CopyIconSecondaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x0C, 0x98, 0x88)));
        res["ThemeIconBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x26, 0x29)));

        res[SystemColors.MenuBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFA)));
        res[SystemColors.MenuTextBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x1A, 0x1C)));
        res[SystemColors.HighlightBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0xD0, 0xEA, 0xE5)));
        res[SystemColors.HighlightTextBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0x08, 0x75, 0x69)));
        res[SystemColors.ControlBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0xF3, 0xF4)));
        res[SystemColors.ControlTextBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x1A, 0x1C)));
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
