using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace PedalFeel.SimHub
{
    internal sealed class CarCatalogEntry
    {
        public string Name { get; set; } = "";
        public string Family { get; set; } = "fallback";
        public string[] Aliases { get; set; } = Array.Empty<string>();
        public string[] Paths { get; set; } = Array.Empty<string>();
        public string[] SourceUrls { get; set; } = Array.Empty<string>();
    }

    internal sealed class CarPreset
    {
        public string Id { get; }
        public string Label { get; }
        public string Note { get; }
        private readonly double[] values;
        public CarPreset(string id, string label, string note, params double[] values)
        {
            if (values.Length != 11) throw new ArgumentException("Eleven tuning values are required.");
            Id = id; Label = label; Note = note; this.values = values;
        }
        public PedalFeelSettings Create() => new PedalFeelSettings {
            GripThreshold = values[0], Strength = values[1], Texture = values[2], AbsPunch = values[3],
            TractionStrength = values[4], EngineTexture = values[5], ShiftKick = values[6],
            IdleTexture = values[7], SurfaceStrength = values[8], LimiterStrength = values[9], DownshiftKick = values[10]
        };
    }

    internal sealed class CarPresetMatch
    {
        public CarPreset Preset { get; }
        public string Basis { get; }
        public int Confidence { get; }
        public CarPresetMatch(CarPreset preset, string basis, int confidence)
        { Preset = preset; Basis = basis; Confidence = confidence; }
    }

    // These are explicitly provisional gain choices, not new physics models or measured car calibration.
    // Neither the upstream telemetry estimator nor the hardware calibration is changed by a preset.
    internal static class CarPresets
    {
        public const int Revision = 2;
        public const string Standard = "standard";
        public const string AuthorBalanced = "author-balanced";
        public static readonly KeyValuePair<string, string>[] Bases = {
            new KeyValuePair<string, string>(Standard, "Стандартный"),
            new KeyValuePair<string, string>(AuthorBalanced, "Оригинальный GT3"),
            new KeyValuePair<string, string>("current", "Копия выбранного профиля")
        };
        public static string BasisLabel(string id) => Bases.FirstOrDefault(p => p.Key == id).Value ?? id;
        public static PedalFeelSettings CreateBasis(string id)
        {
            // Based on upstream win32_main.cpp::applyProfile. The user-requested
            // common gain and 20% upshift cap apply to all new named profiles.
            PedalFeelSettings settings;
            switch (id) {
                case Standard: return new PedalFeelSettings { GripThreshold = 1, Strength = .60, Texture = 1, AbsPunch = 1,
                    DownshiftKick = .80, TractionStrength = .40, EngineTexture = .25, LimiterStrength = .25,
                    IdleTexture = .25, ShiftKick = .80, SurfaceStrength = .30 };
                case AuthorBalanced:
                    settings = new PedalFeelSettings { GripThreshold = .91, Strength = .70, Texture = .62,
                        AbsPunch = .72, TractionStrength = .65, EngineTexture = .48, IdleTexture = .28, ShiftKick = .45 };
                    break;
                case "author-subtle":
                    settings = new PedalFeelSettings { GripThreshold = .93, Strength = .52, Texture = .45,
                        AbsPunch = .58, TractionStrength = .48, EngineTexture = .30, IdleTexture = .16, ShiftKick = .25 };
                    break;
                case "author-aggressive":
                    settings = new PedalFeelSettings { GripThreshold = .87, Strength = .88, Texture = .84,
                        AbsPunch = .90, TractionStrength = .82, EngineTexture = .68, IdleTexture = .40, ShiftKick = .65 };
                    break;
                case "formula": return Get("formula").Create();
                default: throw new ArgumentException(L10n.F("Неизвестный стартовый набор: {0}", id));
            }
            settings.EffectsGain = PedalFeelSettings.BaseEffectsGain;
            settings.ShiftKick = Math.Min(settings.ShiftKick, .20);
            settings.SurfaceStrength = .28;
            settings.LimiterStrength = 1;
            settings.DownshiftKick = 1;
            return settings;
        }
        public static readonly CarPreset[] All = {
            new CarPreset("gt3", "GT3 · Balanced", "На основе PedalFeel GT3 Balanced; переключения передач и отсечка настроены на 20%.", .91, .70, .62, .72, .65, .48, .20, .28, .28, .20, .20),
            new CarPreset("gt4", "GT4", "Более спокойный фон двигателя и расчётных сигналов сцепления.", .91, .62, .50, .68, .48, .38, .20, .22, .24, .20, .20),
            new CarPreset("gte", "GTE / GT1 / GT2", "Умеренные расчётные сигналы; ABS воспроизводится только при наличии сигнала игры.", .93, .60, .42, .65, .45, .38, .20, .20, .22, .20, .20),
            new CarPreset("cup", "Кубковые спорткары", "Стартовый набор для Porsche Cup и Ferrari Challenge; это разные машины, ABS берётся только из телеметрии.", .92, .62, .45, .65, .42, .42, .20, .22, .22, .20, .20),
            new CarPreset("touring_fwd", "Передний привод / TCR", "Амплитуда расчётной потери заднего сцепления выключена. Модель оригинала не оценивает пробуксовку переднего привода.", .93, .55, .32, .65, 0, .34, .20, .18, .20, .20, .20),
            new CarPreset("prototype", "Прототипы", "Расчётный порог ослаблен: исходная модель GT3 не учитывает аэродинамику каждого прототипа.", .97, .55, .32, .65, .35, .30, .20, .14, .18, .20, .20),
            new CarPreset("formula", "Формулы", "Сдержанные расчётные сигналы; порог GT3 не является измерением предела сцепления формулы.", .99, .52, .28, .65, .30, .28, .20, .12, .18, .20, .20),
            new CarPreset("road_rwd", "Дорожные / туринговые · задний привод", "Умеренная сила и фон для дорожных машин, лёгких спорткаров и Supercars.", .90, .55, .42, .65, .40, .38, .20, .20, .22, .20, .20),
            new CarPreset("road_awd", "Дорожные · полный привод", "Амплитуда задней потери сцепления выключена: оценка оригинала рассчитана на задний привод.", .95, .50, .25, .65, 0, .32, .20, .16, .20, .20, .20),
            new CarPreset("oval_stock", "Овалы · кузовные машины", "Ослаблены расчётные сигналы и дорожный фон для длительной езды в повороте.", .97, .50, .30, .65, .25, .38, .20, .20, .14, .20, .20),
            new CarPreset("oval_open", "Овалы · открытые колёса / Modified", "Сдержанный старт для Sprint, Silver Crown и Modified; модель GT3 не адаптирована к овалу.", .99, .45, .24, .65, .20, .32, .18, .14, .12, .20, .20),
            new CarPreset("dirt_oval", "Грунтовые овалы", "Амплитуда задней потери сцепления выключена, расчётный порог и дорожный фон ослаблены: скольжение здесь часто штатное.", 1.02, .35, .10, .65, 0, .30, .15, .18, .10, .20, .20),
            new CarPreset("rallycross", "Ралли-кросс / Cross Car", "Предварительный набор с ослабленным порогом и выключенной амплитудой задней потери сцепления.", 1.00, .42, .15, .65, 0, .32, .20, .18, .12, .20, .20),
            new CarPreset("offroad", "Внедорожные грузовики", "Дорожный фон и расчётный порог ослаблены; амплитуда задней потери сцепления выключена.", 1.00, .38, .12, .65, 0, .30, .20, .18, .10, .20, .20),
            new CarPreset("electric", "Электромобили", "Вибрация двигателя, холостого хода, переключения передач и отсечка выключены. Исходная модель GT3 не адаптирована к электроприводу.", .97, .45, .20, .65, 0, 0, 0, 0, .16, 0, 0),
            new CarPreset("fallback", "Универсальный · мягкий старт", "Машина не распознана. Расчётные сигналы ослаблены, амплитуда задней потери сцепления выключена; набор можно выбрать вручную.", .98, .42, .20, .65, 0, .25, .20, .12, .16, .20, .20)
        };
        private static readonly Dictionary<string, CarPreset> ById = All.ToDictionary(p => p.Id, StringComparer.Ordinal);
        // Only used to migrate an unchanged field from a documented old preset. Unknown
        // origins and personal shift choices must not be guessed from a car's name.
        private static readonly Dictionary<string, double> RevisionOneShift = new Dictionary<string, double>(StringComparer.Ordinal) {
            ["gt3"] = .45, ["gt4"] = .35, ["gte"] = .38, ["cup"] = .42,
            ["touring_fwd"] = .30, ["prototype"] = .30, ["formula"] = .30,
            ["road_rwd"] = .30, ["road_awd"] = .28, ["oval_stock"] = .24,
            ["oval_open"] = .18, ["dirt_oval"] = .15, ["rallycross"] = .28,
            ["offroad"] = .25, ["electric"] = 0, ["fallback"] = .20
        };
        internal static bool TryGetRevisionOneShift(string id, out double value) => RevisionOneShift.TryGetValue(id, out value);
        public static readonly CarCatalogEntry[] Cars = ReadCatalog();
        private static readonly Dictionary<string, CarCatalogEntry?> Lookup = BuildLookup();

        public static IEnumerable<KeyValuePair<string, string>> Options
        {
            get {
                yield return new KeyValuePair<string, string>("auto", "Автоматически по машине");
                foreach (var preset in All) yield return new KeyValuePair<string, string>(preset.Id, preset.Label);
            }
        }
        public static CarPreset Get(string id) => ById.TryGetValue(id, out var value)
            ? value : throw new ArgumentException(L10n.F("Неизвестный стартовый набор: {0}", id));

        public static CarPresetMatch Resolve(string key, string? model)
        {
            string identity = key;
            int marker = identity.IndexOf("|id:", StringComparison.Ordinal);
            if (marker >= 0) identity = identity.Substring(marker + 4);
            marker = identity.IndexOf("|model:", StringComparison.Ordinal);
            if (marker >= 0) identity = identity.Substring(marker + 7);
            foreach (string? candidate in new[] { identity, model }) {
                string normalized = Normalize(candidate);
                if (normalized.Length > 0 && Lookup.TryGetValue(normalized, out var car) && car != null)
                    return new CarPresetMatch(Get(car.Family), "Каталог iRacing: " + car.Name, 2);
            }
            // A small, unambiguous class fallback covers future named cars; no guessed numeric IDs.
            string name = Normalize(model);
            string? family = null;
            if (name.Contains("nascar") || name.StartsWith("arca")) family = "oval_stock";
            else if (name.Contains("dirt")) family = "dirt_oval";
            else if (name.Contains("tcr")) family = "touring_fwd";
            else if (name.Contains("porsche") && name.Contains("cup")) family = "cup";
            else if (name.Contains("gt4")) family = "gt4";
            else if (name.Contains("gt3")) family = "gt3";
            else if (name.Contains("gte") || name.Contains("gt1") || name.Contains("gt2")) family = "gte";
            else if (name.Contains("gtp") || name.Contains("lmp")) family = "prototype";
            else if (name.Contains("formula")) family = "formula";
            return family == null
                ? new CarPresetMatch(Get("fallback"), "Универсальный набор: точного совпадения в каталоге нет", 0)
                : new CarPresetMatch(Get(family), "Тип определён по названию; точного совпадения в каталоге нет", 1);
        }

        internal static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string text = value!.Trim().Replace('/', '\\');
            if (text.StartsWith("\\cars\\", StringComparison.OrdinalIgnoreCase)) text = text.Substring(6);
            else if (text.StartsWith("cars\\", StringComparison.OrdinalIgnoreCase)) text = text.Substring(5);
            text = text.Normalize(NormalizationForm.FormD);
            var result = new StringBuilder();
            foreach (char c in text)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
                    result.Append(char.ToLowerInvariant(c));
            return result.ToString();
        }
        private static CarCatalogEntry[] ReadCatalog()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PedalFeel.CarCatalog.json")
                ?? throw new InvalidDataException(L10n.T("Не найден каталог машин PedalFeel.")))
            using (var reader = new StreamReader(stream)) {
                var entries = JsonConvert.DeserializeObject<CarCatalogEntry[]>(reader.ReadToEnd())
                    ?? throw new InvalidDataException(L10n.T("Пустой каталог машин PedalFeel."));
                foreach (var car in entries)
                    if (string.IsNullOrWhiteSpace(car.Name) || !ById.ContainsKey(car.Family))
                        throw new InvalidDataException(L10n.F("Ошибка каталога машин PedalFeel: {0}", car.Name));
                return entries;
            }
        }
        private static Dictionary<string, CarCatalogEntry?> BuildLookup()
        {
            var result = new Dictionary<string, CarCatalogEntry?>(StringComparer.Ordinal);
            foreach (var car in Cars)
                foreach (string alias in new[] { car.Name }.Concat(car.Aliases ?? Array.Empty<string>()).Concat(car.Paths ?? Array.Empty<string>())) {
                    string key = Normalize(alias);
                    if (key.Length == 0) continue;
                    if (!result.TryGetValue(key, out var previous)) result.Add(key, car);
                    else if (previous == null || previous.Family != car.Family) result[key] = null;
                }
            return result;
        }
    }
}
