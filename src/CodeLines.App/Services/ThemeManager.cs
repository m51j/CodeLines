using System.Windows;
using Microsoft.Win32;

namespace CodeLines.App.Services;

public static class ThemeManager
{
    private const string ThemeMarker = "Theme.xaml";

    public static void Apply(string requestedTheme)
    {
        var application = Application.Current;
        application.ThemeMode = requestedTheme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System
        };

        var palette = requestedTheme == "System" ? ResolveSystemPalette() : requestedTheme;
        var dictionaries = application.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(IsThemeDictionary);
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/CodeLines.App;component/Themes/{palette}Theme.xaml", UriKind.Absolute)
        };

        if (current is not null) dictionaries.Remove(current);
        dictionaries.Insert(0, replacement);
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary) =>
        dictionary.Source?.OriginalString.EndsWith(ThemeMarker, StringComparison.OrdinalIgnoreCase) == true;

    private static string ResolveSystemPalette()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? "Dark" : "Light";
        }
        catch
        {
            return "Light";
        }
    }
}
