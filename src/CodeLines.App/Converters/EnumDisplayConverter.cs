using System.Globalization;
using System.Windows.Data;
using CodeLines.Core.Models;

namespace CodeLines.App.Converters;

public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        CountingScope.SourceOnly => "Source Code Only",
        CountingScope.SourceAndConfiguration => "Source Code + Configuration",
        CountingScope.AllText => "All Text Files",
        FileCategory.SourceCode => "Source Code",
        FileCategory.Configuration => "Configuration",
        FileCategory.DocumentationText => "Documentation / Text",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
