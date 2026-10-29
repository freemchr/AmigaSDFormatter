using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using AmigaSdCreator.Core.Diagnostics;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Desktop.Converters;

// Ported from the WPF app's Converters.cs. Avalonia uses IsVisible (bool) instead of Visibility.

/// <summary>True when the bound value is truthy (non-null, non-empty, true, non-zero). "Invert" as parameter flips it. Bind to IsVisible.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool truthy = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            long l => l != 0,
            System.Collections.ICollection c => c.Count > 0,
            Enum e => System.Convert.ToInt64(e) != 0,
            _ => true,
        };
        if (parameter as string == "Invert") truthy = !truthy;
        return truthy;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when an integer equals the parameter (used for wizard steps).</summary>
public sealed class StepVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int step) return false;
        var p = parameter as string ?? "";
        // "InvertN": visible on every step before the working step except N (used for the Next button).
        if (p.StartsWith("Invert", StringComparison.Ordinal) && int.TryParse(p[6..], out int except))
            return step != except && step < except + 1;
        return int.TryParse(p, out int want) && step == want;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BytesConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long l ? Format.Bytes(l) : value is int i ? Format.Bytes(i) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps severities / statuses / verdicts to theme brushes.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            FindingSeverity.Ok or VerificationStatus.Verified or CompatibilityVerdict.Compatible => "OkBrush",
            FindingSeverity.Info or MessageSeverity.Info or VerificationStatus.Partial or CompatibilityVerdict.NotAssessed => "InfoBrush",
            FindingSeverity.Warning or MessageSeverity.Warning or VerificationStatus.Experimental
                or CompatibilityVerdict.LikelyCompatible or CompatibilityVerdict.NeedsAmigaFormat or CompatibilityVerdict.Unknown => "WarnBrush",
            FindingSeverity.Problem or MessageSeverity.Error or CompatibilityVerdict.Incompatible => "ErrorBrush",
            VerificationStatus.Protected => "BlueBrush",
            true => "OkBrush",
            false => "ErrorBrush",
            _ => "SubtleTextBrush",
        };
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var brush) && brush is IBrush b ? b : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SeverityGlyphConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FindingSeverity.Ok or true => "✔",
        FindingSeverity.Info or MessageSeverity.Info => "ℹ",
        FindingSeverity.Warning or MessageSeverity.Warning => "⚠",
        FindingSeverity.Problem or MessageSeverity.Error or false => "✖",
        _ => "•",
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class VerdictTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CompatibilityVerdict v ? ReportExporter.VerdictText(v) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class UseTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IntendedUse u ? AmigaSdCreator.Ui.ViewModels.HardwareSelection.UseText(u) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is VerificationStatus s ? AmigaSdCreator.Ui.ViewModels.MainViewModel.StatusText(s) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class HasItemsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is System.Collections.ICollection { Count: > 0 };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
