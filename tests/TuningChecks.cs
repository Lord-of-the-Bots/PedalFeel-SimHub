using System;
using System.IO;
using Newtonsoft.Json.Linq;
using PedalFeel.SimHub;

internal static class TuningChecks
{
    public static void Run(Action<bool, string> check)
    {
        var defaults = new PedalFeelSettings();
        check(Near(defaults.EffectsGain, 3 * .7) && Near(defaults.ShiftKick, .20) &&
            Near(defaults.LimiterStrength, .20) && Near(defaults.DownshiftKick, .20),
            "new x1 baseline is the previous x0.7 and all transient defaults are 20 percent");
        foreach (var pair in new[] { new[] { -1.0, 0.0 }, new[] { 0.0, 0.0 }, new[] { .63, .63 },
            new[] { 1.0, 1.0 }, new[] { 5.0, 1.0 }, new[] { double.NaN, .20 },
            new[] { double.PositiveInfinity, .20 }, new[] { double.NegativeInfinity, .20 } }) {
            var settings = new PedalFeelSettings { LimiterStrength = pair[0], DownshiftKick = pair[0] };
            settings.Normalize();
            check(Near(settings.LimiterStrength, pair[1]) && Near(settings.DownshiftKick, pair[1]),
                "limiter and downshift normalization retain explicit mute and valid strengths: " + pair[0]);
        }
        var native = NativeConfig.From(new PedalFeelSettings { LimiterStrength = .73, DownshiftKick = .34 });
        check(Near(native.LimiterStrength, .73) && Near(native.DownshiftKick, .34),
            "independent limiter and downshift strengths reach their native ABI fields");
        native = NativeConfig.From(new PedalFeelSettings { LimiterStrength = 0, DownshiftKick = 0 });
        check(native.LimiterStrength == 0 && native.DownshiftKick == 0,
            "explicit zero strengths reach native configuration without a fallback value");

        string directory = Path.Combine(Path.GetTempPath(), "PedalFeelTuningChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "old-profiles.json");
        var catalog = new JObject {
            ["SchemaVersion"] = 1,
            ["Device"] = new JObject { ["EffectsGain"] = 3, ["Enabled"] = true,
                ["BrakeMaximum"] = new JArray(46, 55, 62, 73) },
            ["Cars"] = new JObject(), ["CarNames"] = new JObject(), ["Origins"] = new JObject()
        };
        AddOldCar(catalog, "stock", "gt3", .45, false);
        AddOldCar(catalog, "edited-other", "gt3", .45, true);
        catalog["Cars"]!["edited-other"]!["Strength"] = .33;
        AddOldCar(catalog, "custom-shift", "gt3", .67, true);
        AddOldCar(catalog, "lower-oval", "oval_open", .18, false);
        AddOldCar(catalog, "lower-dirt", "dirt_oval", .15, false);
        AddOldCar(catalog, "legacy-no-origin", null, .45, false);
        AddOldCar(catalog, "unknown-origin", "custom-future", .42, false);
        AddOldCar(catalog, "electric", "electric", 0, false);
        AddOldCar(catalog, "custom-electric", "electric", 0, true);
        catalog["Cars"]!["custom-electric"]!["Strength"] = .36;
        catalog["Cars"]!["custom-electric"]!["SurfaceStrength"] = .53;
        AddOldCar(catalog, "explicit-electric", "electric", 0, false);
        catalog["Cars"]!["explicit-electric"]!["LimiterStrength"] = .81;
        catalog["Cars"]!["explicit-electric"]!["DownshiftKick"] = .72;
        File.WriteAllText(file, catalog.ToString());
        using (var profiles = new ProfileRepository(file)) {
            profiles.Select("stock", "Stock GT3");
            var stock = profiles.Current();
            check(Near(stock.EffectsGain, 2.1) && Near(stock.ShiftKick, .20) &&
                Near(stock.LimiterStrength, .20) && Near(stock.DownshiftKick, .20) &&
                stock.Enabled && Near(stock.BrakeMaximum[2], 62),
                "0.3.3 stock gain and shift migrate while device choices and frequency limits survive");
            check(!profiles.Describe("stock", "Stock GT3").Contains("вашими изменениями"),
                "automatic stock migration does not falsely mark a profile as personally edited");
            profiles.Select("edited-other", "Customized grip");
            check(Near(profiles.Current().Strength, .33) && Near(profiles.Current().ShiftKick, .20),
                "an untouched stock upshift migrates independently of other personal car tuning");
            profiles.Select("custom-shift", "Customized shift");
            check(Near(profiles.Current().ShiftKick, .67), "a personally chosen upshift is never capped by a default update");
            profiles.Select("lower-oval", "Oval");
            check(Near(profiles.Current().ShiftKick, .18), "migration retains the softer 18 percent stock upshift");
            profiles.Select("lower-dirt", "Dirt");
            check(Near(profiles.Current().ShiftKick, .15), "migration retains the softer 15 percent stock upshift");
            profiles.Select("legacy-no-origin", "Legacy GT3");
            check(Near(profiles.Current().ShiftKick, .45), "migration does not guess whether an originless legacy value was a default");
            profiles.Select("unknown-origin", "Unknown family");
            check(Near(profiles.Current().ShiftKick, .42), "migration preserves tuning whose preset origin is unknown");
            profiles.Select("electric", "Electric");
            check(profiles.Current().LimiterStrength == 0 && profiles.Current().DownshiftKick == 0,
                "an untouched identified electric preset migrates new combustion-related layers to zero");
            profiles.Select("custom-electric", "Customized electric");
            check(profiles.Current().LimiterStrength == 0 && profiles.Current().DownshiftKick == 0 &&
                Near(profiles.Current().Strength, .36) && Near(profiles.Current().SurfaceStrength, .53) &&
                profiles.Describe("custom-electric", "Customized electric").Contains("вашими изменениями"),
                "customized electric tuning is retained while absent combustion-related controls start disabled");
            profiles.Select("explicit-electric", "Explicit electric");
            check(Near(profiles.Current().LimiterStrength, .81) && Near(profiles.Current().DownshiftKick, .72),
                "explicitly saved new effect strengths are not overwritten by electric migration");
        }
        check((int)JObject.Parse(File.ReadAllText(file))["TuningRevision"]! == 1,
            "opening and closing old settings persists the one-time tuning revision even without a user edit");
        using (var profiles = new ProfileRepository(file)) {
            profiles.Select("stock", "Stock GT3");
            var settings = profiles.Current(); settings.EffectsGain = 3; settings.ShiftKick = .45;
            profiles.Commit(settings);
        }
        using (var profiles = new ProfileRepository(file)) {
            profiles.Select("stock", "Stock GT3");
            check(Near(profiles.Current().EffectsGain, 3) && Near(profiles.Current().ShiftKick, .45),
                "new-scale explicit gain 3 and upshift 45 percent survive restart without a second migration");
        }
        foreach (var pair in new[] { new[] { 2.1, 2.1 }, new[] { 1.5, 1.5 }, new[] { 6.0, 4.2 }, new[] { 0.0, 0.0 } }) {
            string customFile = Path.Combine(directory, "gain-" + Guid.NewGuid().ToString("N") + ".json");
            var custom = (JObject)catalog.DeepClone(); custom["Device"]!["EffectsGain"] = pair[0];
            File.WriteAllText(customFile, custom.ToString());
            using (var profiles = new ProfileRepository(customFile))
                check(Near(profiles.Current().EffectsGain, 2.1),
                    "migration preserves custom absolute gain within the new supported range: " + pair[0]);
        }
        CarTuning(directory, check);
    }

    private static void CarTuning(string directory, Action<bool, string> check)
    {
        string file = Path.Combine(directory, "new-effects.json");
        using (var profiles = new ProfileRepository(file)) {
            profiles.SelectCar("IRacing", "ferrari296gt3", "Ferrari 296 GT3");
            profiles.CreateProfile("Ferrari", "formula", true);
            var first = profiles.Current(); first.LimiterStrength = 0; first.DownshiftKick = .61;
            profiles.Commit(first);
            check(profiles.Describe(profiles.CurrentKey, profiles.CurrentName).Contains("вашими изменениями"),
                "limiter/downshift edits count as personal car tuning");
            profiles.SelectCar("IRacing", "bmwm4gt3", "BMW M4 GT3");
            check(Near(profiles.Current().LimiterStrength, .25) && Near(profiles.Current().DownshiftKick, .80),
                "an unassigned car loads independent author limiter and downshift defaults");
            profiles.SelectCar("IRacing", "ferrari296gt3", "Ferrari 296 GT3");
            check(profiles.Current().LimiterStrength == 0 && Near(profiles.Current().DownshiftKick, .61),
                "car selection restores its explicit limiter mute and downshift strength");
        }
        using (var profiles = new ProfileRepository(file)) {
            profiles.SelectCar("IRacing", "ferrari296gt3", "Ferrari 296 GT3");
            check(profiles.Current().LimiterStrength == 0 && Near(profiles.Current().DownshiftKick, .61),
                "new effect tuning persists after restart");
            var reset = profiles.ResetCurrent();
            check(Near(reset.LimiterStrength, .20) && Near(reset.DownshiftKick, .20) && Near(reset.ShiftKick, .20),
                "reset restores all three current preset transient defaults");
            var electric = profiles.ApplyPreset(profiles.CurrentKey, profiles.CurrentName, "electric");
            check(electric.LimiterStrength == 0 && electric.DownshiftKick == 0 && electric.ShiftKick == 0,
                "explicitly applying the electric preset disables all three transient defaults");
        }
    }
    private static void AddOldCar(JObject catalog, string key, string? family, double shift, bool edited)
    {
        catalog["Cars"]![key] = new JObject { ["ShiftKick"] = shift };
        catalog["CarNames"]![key] = key;
        if (family != null) catalog["Origins"]![key] = new JObject {
            ["PresetId"] = family, ["Revision"] = 1, ["Automatic"] = true,
            ["UserEdited"] = edited, ["Confidence"] = 2, ["Basis"] = "Test preset"
        };
    }
    private static bool Near(double left, double right) => Math.Abs(left - right) < .000001;
}
