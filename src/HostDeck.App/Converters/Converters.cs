using System.Globalization;
using System.Windows;
using System.Windows.Data;
using HostDeck.Core.Model;
using HostDeck.Presentation.ViewModels;

namespace HostDeck.App.Converters;

/// <summary>
/// Protokoll (bzw. Tree-Eintrag) → Fluent-Icon. Als MultiBinding (Item, IsExpanded, Protocol) verwenden,
/// damit sich das Icon beim Auf-/Zuklappen bzw. nach Verschieben aktualisiert.
/// </summary>
public sealed class ProtocolIconConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
        => Convert(values.Length > 0 ? values[0] : null, targetType, parameter, culture);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TreeItemViewModel { IsFolder: true, IsExpanded: true } => Icons.FolderOpen,
        TreeItemViewModel { IsFolder: true } => Icons.Folder,
        TreeItemViewModel item => Icons.ForProtocol(item.Protocol),
        ProtocolKind p => Icons.ForProtocol(p),
        _ => Icons.Server,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is Visibility.Visible) ^ Invert;
}

/// <summary>Vergleicht ein Enum mit dem ConverterParameter (für Layout-Toggle-Buttons).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null && value.ToString() == parameter.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is null) ^ (parameter as string == "invert") ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long b when b < 1024 => $"{b} B",
        long b when b < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        long b when b < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} MB",
        long b => $"{b / 1024.0 / 1024 / 1024:0.##} GB",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
