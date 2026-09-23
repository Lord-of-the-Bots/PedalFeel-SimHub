using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PedalFeel.SimHub;

internal static class LocalizationHostChecks
{
    public static void Run(string host, Action<bool, string> check)
    {
        // Exercise the actual host localizer and packaged resx files in this test
        // process. Do not start SimHub, change its saved language, or touch hardware.
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "WoteverLocalization")
            ?? Assembly.LoadFrom(Path.Combine(host, "WoteverLocalization.dll"));
        var type = assembly.GetType("WoteverLocalization.LocalizationProvider", true)!;
        var singleton = type.GetField("instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(string) }, null)!;
        var selectLanguage = type.GetMethod("SetLanguage", new[] { typeof(string) })!;
        var currentLanguage = type.GetProperty("CurrentLanguage")!;
        var languageCode = currentLanguage.PropertyType.GetProperty("Code")!;
        object? originalSingleton = singleton.GetValue(null);
        string? originalOverride = L10n.OverrideCulture;
        CultureInfo originalCulture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            string languageFolder = Path.Combine(host, "Languages");
            check(File.Exists(Path.Combine(languageFolder, "SimHub.resx")), "actual host language resources are available");
            object localizer = constructor.Invoke(new object[] { languageFolder });
            singleton.SetValue(null, localizer);
            L10n.OverrideCulture = null;

            Thread.CurrentThread.CurrentUICulture = new CultureInfo("ru-RU");
            selectLanguage.Invoke(localizer, new object[] { "de-DE" });
            check((string)languageCode.GetValue(currentLanguage.GetValue(localizer, null), null)! == "de-DE",
                "actual SimHub localizer selects German from its packaged resources");
            check(L10n.CultureName == "de-DE" && L10n.T("Тормоз") == "Bremse",
                "the selected SimHub language overrides a different Windows UI culture");

            // Reuse the already initialized L10n reader: it must not cache the old
            // selected language when SimHub switches language at runtime.
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("de-DE");
            selectLanguage.Invoke(localizer, new object[] { "fr-FR" });
            check(L10n.CultureName == "fr-FR" && L10n.T("Тормоз") == "Frein",
                "runtime SimHub language changes are reflected without overriding the plugin culture");

            foreach (string culture in new[] { "en", "de-DE", "fr-FR", "it", "ko-KR", "ru-RU", "zh-Hans-CN" })
            {
                // SimHub derives Language.Code from CultureInfo.Name, not the
                // resx suffix. .NET Framework normalizes zh-Hans-CN to zh-CN.
                // SetLanguage requires that actual code, while our resource file
                // deliberately retains its explicit script/region identifier.
                string hostCode = new CultureInfo(culture).Name;
                selectLanguage.Invoke(localizer, new object[] { hostCode });
                check((string)languageCode.GetValue(currentLanguage.GetValue(localizer, null), null)! == hostCode &&
                    L10n.CultureName == culture,
                    "actual SimHub language code maps correctly: " + hostCode + " -> " + culture);
            }
            check(L10n.T("Тормоз") == "刹车",
                "the actual SimHub Chinese culture alias loads the complete Simplified Chinese catalog");

            selectLanguage.Invoke(localizer, new object[] { "not-a-shipped-language" });
            check(L10n.CultureName == "en", "SimHub's own unknown-language fallback is followed instead of the OS language");
            check(Thread.CurrentThread.CurrentUICulture.Name == "de-DE",
                "reading the host language does not change the thread UI culture");
        }
        finally
        {
            singleton.SetValue(null, originalSingleton);
            L10n.OverrideCulture = originalOverride;
            Thread.CurrentThread.CurrentUICulture = originalCulture;
        }
    }
}
