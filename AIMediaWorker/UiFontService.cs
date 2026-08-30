using AIMediaWorker.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;

namespace AIMediaWorker;

public static class UiFontService
{
    private const string ThemeFontResourceKey = "ContentControlThemeFontFamily";
    private static readonly ConditionalWeakTable<DependencyObject, object> AppliedElements = new();
    private static readonly object AppliedMarker = new();
    private static FontFamily _currentFontFamily = new(GeneralSettings.DefaultUiFontFamily);

    public static FontFamily CurrentFontFamily => _currentFontFamily;

    public static void Apply(string? fontFamily, DependencyObject? visualRoot = null)
    {
        var normalized = string.IsNullOrWhiteSpace(fontFamily)
            ? GeneralSettings.DefaultUiFontFamily
            : fontFamily.Trim();
        var family = new FontFamily(normalized);
        _currentFontFamily = family;
        Application.Current.Resources[ThemeFontResourceKey] = family;
        if (visualRoot is not null) ApplyToVisualTree(visualRoot, family);
    }

    private static void ApplyToVisualTree(DependencyObject element, FontFamily family)
    {
        // IconElement implementations render private text nodes with a symbol font.
        // Applying the UI font to those nodes turns glyph code points into missing
        // characters, and only affects icons that happened to be realized at the
        // time of the update.
        if (element is IconElement) return;

        if (element is Control control && CanSetFont(element, Control.FontFamilyProperty))
        {
            control.FontFamily = family;
            MarkApplied(element);
        }
        else if (element is TextBlock text && CanSetFont(element, TextBlock.FontFamilyProperty))
        {
            text.FontFamily = family;
            MarkApplied(element);
        }

        var childCount = VisualTreeHelper.GetChildrenCount(element);
        for (var index = 0; index < childCount; index++) ApplyToVisualTree(VisualTreeHelper.GetChild(element, index), family);
    }

    private static bool CanSetFont(DependencyObject element, DependencyProperty property) =>
        element.ReadLocalValue(property) == DependencyProperty.UnsetValue || AppliedElements.TryGetValue(element, out _);

    private static void MarkApplied(DependencyObject element)
    {
        if (!AppliedElements.TryGetValue(element, out _)) AppliedElements.Add(element, AppliedMarker);
    }
}
