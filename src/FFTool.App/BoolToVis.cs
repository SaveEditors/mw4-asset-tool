using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FFTool.App;

/// <summary>bool → Visibility. <see cref="Instance"/> maps true→Visible;
/// <see cref="Inverse"/> maps true→Collapsed.</summary>
public sealed class BoolToVis : IValueConverter
{
    public static readonly BoolToVis Instance = new(false);
    public static readonly BoolToVis Inverse = new(true);

    private readonly bool _invert;
    private BoolToVis(bool invert) => _invert = invert;

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool b = value is true;
        if (_invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}
