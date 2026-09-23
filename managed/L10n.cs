using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace PedalFeel.SimHub
{
    /// <summary>Follows SimHub's own language selection, independent of Windows locale.</summary>
    public static class L10n
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Dictionary<string, string>> Catalogs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        internal static readonly string[] SupportedCultures = { "en", "de-DE", "fr-FR", "it", "ko-KR", "ru-RU", "zh-Hans-CN" };
        internal static string? OverrideCulture { get; set; }
        private static PropertyInfo? providerInstance;
        private static PropertyInfo? currentLanguage;
        private static PropertyInfo? languageCode;
        private static bool providerFound;

        public static string CultureName
        {
            get
            {
                if (OverrideCulture != null) return NormalizeCulture(OverrideCulture);
                try
                {
                    lock (Gate)
                    {
                        if (!providerFound)
                        {
                            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "WoteverLocalization");
                            var type = assembly?.GetType("WoteverLocalization.LocalizationProvider");
                            providerInstance = type?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            currentLanguage = type?.GetProperty("CurrentLanguage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            languageCode = currentLanguage?.PropertyType.GetProperty("Code", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            providerFound = providerInstance != null && currentLanguage != null && languageCode != null;
                        }
                        if (providerFound)
                        {
                            var provider = providerInstance!.GetValue(null, null);
                            var language = provider == null ? null : currentLanguage!.GetValue(provider, null);
                            var code = language == null ? null : languageCode!.GetValue(language, null) as string;
                            if (!string.IsNullOrWhiteSpace(code)) return NormalizeCulture(code!);
                        }
                    }
                }
                catch { /* Standalone UI checks or host startup: use the UI culture until ready. */ }
                return NormalizeCulture(CultureInfo.CurrentUICulture.Name);
            }
        }

        internal static string NormalizeCulture(string? name)
        {
            string value = (name ?? "").ToLowerInvariant();
            if (value.StartsWith("ru")) return "ru-RU";
            if (value.StartsWith("de")) return "de-DE";
            if (value.StartsWith("fr")) return "fr-FR";
            if (value.StartsWith("it")) return "it";
            if (value.StartsWith("ko")) return "ko-KR";
            if (value.StartsWith("zh")) return "zh-Hans-CN";
            return "en";
        }

        public static string T(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string culture = CultureName;
            if (culture == "ru-RU") return text;
            if (Catalog(culture).TryGetValue(text, out string value)) return value;
            return culture != "en" && Catalog("en").TryGetValue(text, out value) ? value : text;
        }

        public static string F(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(format), args);

        internal static Dictionary<string, string> Catalog(string culture)
        {
            lock (Gate)
            {
                if (Catalogs.TryGetValue(culture, out var existing)) return existing;
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PedalFeel.Locales." + culture + ".json"))
                {
                    if (stream != null)
                        using (var reader = new StreamReader(stream))
                            result = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd()) ?? result;
                }
                Catalogs[culture] = result;
                return result;
            }
        }
    }
}
