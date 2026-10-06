using System.Globalization;
using System.Threading;
using System.Windows;

namespace SquadDns.Infrastructure;

public static class LocalizationManager
{
    private const string FrSource = "Localization/Strings.fr.xaml";
    private const string EnSource = "Localization/Strings.en.xaml";

    public static string Language { get; private set; } = "fr";

    public static event Action? Changed;

    public static void Apply(string language)
    {
        var normalized = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "fr";
        if (normalized == Language && FindCurrent(normalized) is not null)
        {
            return;
        }

        Replace(normalized == "en" ? EnSource : FrSource, "Strings.");
        Language = normalized;

        var culture = new CultureInfo(normalized);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        Changed?.Invoke();
    }

    // Application.Current devient null apres Shutdown() : T() est appele par le gestionnaire
    // d'erreur de derniere chance, qui ne doit jamais se casser lui-meme sur cette lecture.
    public static string T(string key) => Application.Current?.TryFindResource(key) as string ?? key;

    public static string F(string key, params object[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentUICulture, T(key), args);
        }
        catch (FormatException)
        {
            return T(key);
        }
    }

    private static ResourceDictionary? FindCurrent(string language) =>
        FindContaining(language == "en" ? EnSource : FrSource);

    private static ResourceDictionary? FindContaining(string fragment)
    {
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
        {
            if (dictionary.Source?.OriginalString.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true)
            {
                return dictionary;
            }
        }

        return null;
    }

    private static void Replace(string source, string fragment)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var old = FindContaining(fragment);
        var index = old is null ? dictionaries.Count : dictionaries.IndexOf(old);
        var replacement = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };

        if (old is not null)
        {
            dictionaries[index] = replacement;
        }
        else
        {
            dictionaries.Insert(index, replacement);
        }
    }
}

public static class ThemeManager
{
    private const string DarkSource = "Themes/Neon.Dark.xaml";
    private const string LightSource = "Themes/Neon.Light.xaml";

    public static string Theme { get; private set; } = "dark";

    public static bool IsDark => Theme != "light";

    public static event Action? Changed;

    public static void Apply(string theme)
    {
        var normalized = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        var source = normalized == "light" ? LightSource : DarkSource;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var old = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Neon.", StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };

        if (old is null)
        {
            dictionaries.Insert(0, replacement);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(old)] = replacement;
        }

        Theme = normalized;
        Changed?.Invoke();
    }
}
