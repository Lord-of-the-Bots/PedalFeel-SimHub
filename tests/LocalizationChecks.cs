using System;
using System.Linq;
using System.Text.RegularExpressions;
using PedalFeel.SimHub;

internal static class LocalizationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var baseline = L10n.Catalog("en");
        check(baseline.Count >= 230, "complete English fallback catalogue is embedded in the shipped assembly");
        foreach (string culture in L10n.SupportedCultures)
        {
            var catalog = L10n.Catalog(culture);
            check(catalog.Count == baseline.Count && baseline.Keys.All(catalog.ContainsKey) && catalog.Values.All(v => !string.IsNullOrWhiteSpace(v)),
                "all user-facing localization keys are present for " + culture);
            foreach (var entry in catalog)
            {
                string Placeholders(string value) => string.Join(",", Regex.Matches(value, @"\{\d+\}").Cast<Match>().Select(m => m.Value).OrderBy(v => v));
                if (Placeholders(entry.Key) != Placeholders(entry.Value) || entry.Key.Count(c => c == '\n') != entry.Value.Count(c => c == '\n'))
                    throw new InvalidOperationException("Broken localized format for " + culture + ": " + entry.Key);
                // Exercise the real formatting path, including strings with several arguments.
                string.Format(entry.Value, 1, 2, 3, 4, 5, 6);
            }
            check(true, "localized formatting arguments and line breaks are valid for " + culture);
        }
        string? previous = L10n.OverrideCulture;
        try
        {
            L10n.OverrideCulture = "es-ES";
            check(L10n.CultureName == "en" && L10n.T("Педали и проверка") == baseline["Педали и проверка"],
                "additional unsupported host languages fall back to English");
        }
        finally { L10n.OverrideCulture = previous; }
    }
}
