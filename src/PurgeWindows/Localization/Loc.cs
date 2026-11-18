using System.Globalization;
using System.Resources;

namespace PurgeWindows.Localization;

/// <summary>
/// Every user-facing string goes through here. The English text is the source
/// of truth; translations live in Strings.resx / Strings.&lt;culture&gt;.resx.
/// Unknown keys and missing translations fall back to the English source text.
/// </summary>
public static class Loc
{
    static readonly ResourceManager Rm = new("PurgeWindows.Localization.Strings", typeof(Loc).Assembly);

    /// <summary>The language the app speaks: PURGEWINDOWS_LANG if set, otherwise Windows'.</summary>
    public static string CurrentLanguage =>
        Environment.GetEnvironmentVariable("PURGEWINDOWS_LANG") is { Length: > 0 } forced
            ? forced
            : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary>True when a translation catalogue for <paramref name="language"/> is available.</summary>
    public static bool HasLanguage(string language) =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, $"{language}", "PurgeWindows.resources.dll"));

    /// <summary>The translation for <paramref name="key"/>, or the English text itself.</summary>
    public static string Get(string key)
    {
        try
        {
            var culture = new CultureInfo(CurrentLanguage);
            return Rm.GetString(key, culture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
        catch (CultureNotFoundException)
        {
            return key;
        }
    }

    /// <summary>Formats a translated template with <paramref name="args"/>.</summary>
    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try { return string.Format(template, args); }
        catch (FormatException) { return template; }
    }

    /// <summary>
    /// Russian needs three plural forms (1 элемент, 2 элемента, 5 элементов),
    /// English two. The catalogue holds "One", "Few" and "Many"; English uses One/Many.
    /// </summary>
    public static string Plural(long count, string oneKey, string fewKey, string manyKey)
    {
        var language = CurrentLanguage;
        if (language == "ru")
        {
            var n = Math.Abs(count) % 100;
            if (n is >= 11 and <= 14) return Get(manyKey);
            return (n % 10) switch
            {
                1 => Get(oneKey),
                2 or 3 or 4 => Get(fewKey),
                _ => Get(manyKey),
            };
        }
        return count == 1 ? Get(oneKey) : Get(manyKey);
    }

    public static string Items(long count) =>
        Plural(count, "ItemsCountOne", "ItemsCountFew", "ItemsCountMany");

    public static string Files(long count) =>
        Plural(count, "FilesCountOne", "FilesCountFew", "FilesCountMany");
}
