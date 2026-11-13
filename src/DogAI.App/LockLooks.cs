using System.Windows;
using System.Windows.Media;
using DogAI.Core.Appearance;

namespace DogAI.App;

/// <summary>
/// The lock screen's colours, fonts and corners for each look. The lock screen is a plain WPF window (nothing else may run
/// before the unlock, the web view included), so the values are copied from the tokens at the top of
/// <c>DogAI.UI/wwwroot/css/app.css</c>. Keep the two in step when a look's colours change.
/// </summary>
public sealed record LockPalette(
    string Bg, string Surface, string Surface2, string Line, string Text, string Muted, string Accent, string Ink, string Bad,
    double Radius, double ButtonRadius, string UiFont, string HeadFont, double FontSize, bool AccentWordmark)
{
    private const string Segoe = "Segoe UI Variable Text, Segoe UI";
    private const string SegoeDisplay = "Segoe UI Variable Display, Segoe UI";
    private const string Serif = "Georgia, Cambria, Times New Roman";

    public static LockPalette For(string? skin, bool dark) => (Skins.Normalize(skin ?? Skins.Default), dark) switch
    {
        (Skins.Fireside, true) => new("#1c1713", "#241e19", "#30281f", "#3d3229", "#ede4d8", "#a69886", "#e08a5c", "#1c1713", "#ef9a8c", 10, 999, Segoe, Serif, 14, true),
        (Skins.Fireside, false) => new("#f5efe4", "#fbf7ef", "#ece3d3", "#dccfba", "#2b241d", "#75675a", "#a8481f", "#ffffff", "#a8321f", 10, 999, Segoe, Serif, 14, true),
        // Playhouse: the accent is General's blue, the colour of its home screen.
        (Skins.Playhouse, true) => new("#16132b", "#201c3b", "#2c2752", "#3a3468", "#f4f1ff", "#aaa4d1", "#6cb2ff", "#12102a", "#ff8f9c", 22, 999, SegoeDisplay, SegoeDisplay, 15, true),
        (Skins.Playhouse, false) => new("#f2efff", "#ffffff", "#ece8ff", "#d9d2f6", "#211c3c", "#655f88", "#1c69c9", "#ffffff", "#b3264a", 22, 999, SegoeDisplay, SegoeDisplay, 15, true),
        (_, true) => new("#16181c", "#1c1f24", "#262a31", "#2f343c", "#e6e8eb", "#8d95a2", "#f2a93b", "#1a1206", "#f28b82", 6, 6, Segoe, Segoe, 13.5, false),
        _ => new("#f3f4f6", "#ffffff", "#e9ebef", "#d7dbe1", "#1b1e23", "#5d6673", "#a35f00", "#ffffff", "#b3261e", 6, 6, Segoe, Segoe, 13.5, false),
    };

    /// <summary>Puts the palette into a window's resources, where the lock screen's styles read it.</summary>
    public void ApplyTo(FrameworkElement target)
    {
        var r = target.Resources;
        r["Bg"] = Brush(Bg);
        r["Surface"] = Brush(Surface);
        r["Surface2"] = Brush(Surface2);
        r["Line"] = Brush(Line);
        r["Text"] = Brush(Text);
        r["Muted"] = Brush(Muted);
        r["Accent"] = Brush(Accent);
        r["Ink"] = Brush(Ink);
        r["Bad"] = Brush(Bad);
        r["Radius"] = new CornerRadius(Radius);
        r["BoxRadius"] = new CornerRadius(Math.Min(Radius, 10));
        r["ButtonRadius"] = new CornerRadius(Math.Min(ButtonRadius, 20));
        r["UiFont"] = new FontFamily(UiFont);
        r["HeadFont"] = new FontFamily(HeadFont);
    }

    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
