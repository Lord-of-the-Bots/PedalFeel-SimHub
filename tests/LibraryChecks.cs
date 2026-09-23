using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PedalFeel.SimHub;

internal static class LibraryChecks
{
    public static void Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "PedalFeelLibrary-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, "profiles.json");
        string a = "", copy = "";
        using (var profiles = new ProfileRepository(file)) {
            var initial = profiles.PanelState();
            check(initial.Profiles.Count == 4 && initial.Bases.Count == 5 && initial.SelectedId == "author-balanced" &&
                initial.AssignedId == "" && !initial.CanAssign, "a fresh library offers author bases and Formula without a guessed assignment");
            check(Rejects(() => profiles.AssignCurrentProfile()) && Rejects(() => profiles.ClearCurrentAssignment()),
                "assignment commands require a real current iRacing car");
            check(Rejects(() => profiles.CreateProfile("No car", "formula", true)) && profiles.PanelState().Profiles.Count == 4,
                "failed create-and-assign does not leave a half-created profile");
            check(Rejects(() => profiles.CreateProfile("  ", "formula", false)) &&
                Rejects(() => profiles.CreateProfile(new string('x', 81), "formula", false)) &&
                Rejects(() => profiles.CreateProfile("two\nlines", "formula", false)), "profile names reject blank, oversized and multiline values");
            check(Rejects(() => profiles.SelectProfile("missing")) && Rejects(() => profiles.CreateProfile("Unknown basis", "missing", false)),
                "unknown library identifiers never silently select or create a different profile");

            profiles.SelectCar("IRacing", "car-a", "First car");
            a = profiles.CreateProfile("  My GT3  ", "author-balanced", true);
            var settings = profiles.Current(); settings.Strength = .31; settings.EffectsGain = 1.6;
            settings.BrakeMaximum[0] = 47; settings.BrakeMinimum[0] = 4; settings.Enabled = true;
            profiles.CommitProfile(a, settings);
            check(profiles.PanelState().AssignedId == a && profiles.PanelState().Profiles.Single(p => p.Key == a).Value == "My GT3",
                "creating a profile trims its name and assigns only when explicitly requested");
            check(Rejects(() => profiles.CreateProfile("my gt3", "formula", false)), "case-insensitive duplicate names are refused");
            profiles.SelectCar("IRacing", "car-b", "Second car");
            check(profiles.CurrentProfileId == "author-balanced" && profiles.PanelState().AssignedId == "" &&
                profiles.Current().Strength == .70 && profiles.Current().EffectsGain == CarPresets.CreateBasis("author-balanced").EffectsGain,
                "an unassigned car loads the author baseline rather than the previous car's profile or gain");
            check(profiles.Current().Enabled && profiles.Current().BrakeMaximum[0] == 47 && profiles.Current().BrakeMinimum[0] == 4,
                "mode and frequency calibration remain shared between independently tuned profiles");
            profiles.SelectProfile(a);
            check(profiles.PanelState().AssignedId == "" && profiles.Current().EffectsGain == 1.6,
                "choosing a profile applies its gain without implicitly assigning the current car");
            check(!profiles.SelectCar("IRacing", "car-b", "Second car") && profiles.CurrentProfileId == a,
                "repeated telemetry for the same car cannot undo a manual profile selection");
            check(!profiles.SelectCar("IRacing", null, null) && profiles.CurrentProfileId == a && profiles.CurrentKey == "iracing|id:car-b",
                "a temporary iRacing frame without car identity retains the current selection and known car");
            profiles.AssignCurrentProfile();
            settings = profiles.Current(); settings.EffectsGain = 2.8; profiles.CommitProfile(a, settings);
            profiles.SelectCar("IRacing", "car-a", "First car");
            check(profiles.CurrentProfileId == a && profiles.Current().EffectsGain == 2.8,
                "two explicitly assigned cars share the same named profile and subsequent edits");
            profiles.SelectProfile("author-subtle"); profiles.Select(ProfileRepository.DefaultKey, null);
            profiles.SelectCar("IRacing", "car-a", "First car");
            check(profiles.CurrentProfileId == a, "an explicit session boundary reloads the same car's assigned profile");
            copy = profiles.CreateProfile("Independent copy", "current", false);
            settings = profiles.Current(); settings.EffectsGain = .9; settings.LimiterStrength = .12;
            profiles.CommitProfile(copy, settings);
            profiles.SelectProfile(a);
            check(profiles.Current().EffectsGain == 2.8 && profiles.Current().LimiterStrength == 1,
                "copying a selected profile creates an independent deep copy");
            var stale = profiles.Current(); stale.Strength = .43;
            profiles.SelectProfile(copy); profiles.CommitProfile(a, stale);
            check(profiles.CurrentProfileId == copy && profiles.Current().EffectsGain == .9 && profiles.Current().Strength == .31,
                "a delayed commit targets its captured profile ID without overwriting the new selection");
            profiles.SelectCar("IRacing", "car-c", "Third car");
            check(profiles.CurrentProfileId == "author-balanced", "changing to another unassigned car discards only the temporary selection");
            profiles.SelectProfile(copy); profiles.AssignCurrentProfile();
            settings = profiles.Current(); settings.BrakeMaximum[0] = 58; profiles.CommitProfile(copy, settings);
            profiles.SelectCar("IRacing", "car-a", "First car");
            check(profiles.Current().EffectsGain == 2.8 && profiles.Current().BrakeMaximum[0] == 58 && profiles.Current().Strength == .43,
                "hardware edits propagate globally while profile gain and effect tuning remain independent");
            profiles.Save();
        }
        using (var profiles = new ProfileRepository(file)) {
            profiles.SelectCar("IRacing", "car-a", "First car");
            check(profiles.CurrentProfileId == a && profiles.Current().EffectsGain == 2.8 && profiles.Current().Strength == .43,
                "named profile edits and car assignments persist after restart");
            profiles.SelectCar("IRacing", "car-c", "Third car");
            check(profiles.CurrentProfileId == copy && profiles.Current().EffectsGain == .9 && profiles.Current().LimiterStrength == .12,
                "another car reloads its distinct persisted profile and gain");
            profiles.ClearCurrentAssignment();
            check(profiles.PanelState().AssignedId == "" && profiles.CurrentProfileId == "author-balanced",
                "clearing an assignment immediately restores the unassigned author baseline");
            profiles.SelectCar("IRacing", "car-a", "First car"); profiles.SelectCar("IRacing", "car-c", "Third car");
            check(profiles.CurrentProfileId == "author-balanced", "a removed assignment stays removed across car changes");
        }
        Aliases(Path.Combine(directory, "aliases.json"), check);
        Migration(Path.Combine(directory, "legacy.json"), check);
    }
    private static void Aliases(string file, Action<bool, string> check)
    {
        using (var profiles = new ProfileRepository(file)) {
            profiles.SelectCar("IRacing", null, "New model");
            string assigned = profiles.CreateProfile("Model profile", "formula", true);
            profiles.SelectProfile("author-subtle");
            profiles.SelectCar("IRacing", "stable-id", "New model");
            check(profiles.PanelState().AssignedId == assigned && profiles.CurrentProfileId == "author-subtle",
                "a model gaining a stable ID retains its assignment and current temporary selection");
            profiles.SelectCar("IRacing", "another", "Other"); profiles.SelectCar("IRacing", null, "New model");
            check(profiles.CurrentKey == "iracing|id:stable-id" && profiles.CurrentProfileId == assigned,
                "model-only telemetry resolves its saved stable ID assignment after a car change");
            profiles.ClearCurrentAssignment();
            profiles.SelectCar("IRacing", "stable-id", "New model");
            profiles.SelectCar("IRacing", null, "New model");
            check(profiles.PanelState().AssignedId == "" && profiles.CurrentProfileId == "author-balanced",
                "clearing the stable assignment cannot resurrect its old provisional model assignment");
            profiles.SelectCar("AssettoCorsa", "stable-id", "New model");
            check(!profiles.PanelState().CanAssign && profiles.CurrentKey == ProfileRepository.DefaultKey,
                "leaving iRacing removes the current-car assignment target");
        }
    }
    private static void Migration(string file, Action<bool, string> check)
    {
        var legacy = new JObject {
            ["SchemaVersion"] = 1, ["TuningRevision"] = 1,
            ["Device"] = new JObject { ["EffectsGain"] = 1.7, ["Enabled"] = true,
                ["BrakeChannel"] = 0, ["ThrottleChannel"] = 2, ["BrakeMaximum"] = new JArray(51, 52, 53, 54) },
            ["Cars"] = new JObject {
                ["iracing|id:a"] = new JObject { ["Strength"] = .31, ["ShiftKick"] = .65, ["LimiterStrength"] = 0, ["DownshiftKick"] = .12, ["EffectsGain"] = 4 },
                ["iracing|id:b"] = new JObject { ["Strength"] = .74, ["ShiftKick"] = .18, ["LimiterStrength"] = .22, ["DownshiftKick"] = .34, ["EffectsGain"] = .1 }
            },
            ["CarNames"] = new JObject { ["iracing|id:a"] = "Same car name", ["iracing|id:b"] = "Same car name" },
            ["ModelAliases"] = new JObject { ["iracing|model:Alias A"] = "iracing|id:a" }
        };
        legacy["Cars"]!["iracing|model:Alias A"] = legacy["Cars"]!["iracing|id:a"]!.DeepClone();
        legacy["CarNames"]!["iracing|model:Alias A"] = "Provisional saved car";
        File.WriteAllText(file, legacy.ToString());
        string a;
        using (var profiles = new ProfileRepository(file)) {
            check(profiles.Current().EffectsGain == 1.7 && profiles.CurrentProfileId != "author-balanced",
                "an old no-car global gain is retained in its own imported baseline profile");
            profiles.SelectCar("IRacing", "new-car", "New car after upgrade");
            check(profiles.CurrentProfileId == "author-balanced" && profiles.Current().EffectsGain == 2.1,
                "a newly discovered unassigned car uses the new author baseline instead of an old device-wide gain");
            profiles.SelectCar("IRacing", "a", "Same car name"); a = profiles.CurrentProfileId;
            var first = profiles.Current();
            check(first.Strength == .31 && first.ShiftKick == .65 && first.LimiterStrength == 0 && first.DownshiftKick == .12 && first.EffectsGain == 1.7,
                "0.3.4 migration preserves each saved effect and imports the effective global gain instead of stale per-car copies");
            check(first.Enabled && first.BrakeChannel == 0 && first.ThrottleChannel == 2 && first.BrakeMaximum[2] == 53,
                "library migration retains shared hardware calibration, channels and mode");
            profiles.SelectCar("IRacing", "b", "Same car name");
            check(profiles.CurrentProfileId != a && profiles.Current().Strength == .74 && profiles.Current().EffectsGain == 1.7,
                "legacy cars with duplicate display names become separate named profiles without merging tuning");
            check(profiles.PanelState().Profiles.Select(p => p.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == profiles.PanelState().Profiles.Count,
                "migration gives duplicate legacy names distinct readable suffixes");
            var second = profiles.Current(); second.EffectsGain = 2.6; profiles.CommitProfile(profiles.CurrentProfileId, second);
            profiles.SelectCar("IRacing", null, "Alias A");
            check(profiles.CurrentProfileId == a && profiles.Current().EffectsGain == 1.7,
                "migrated aliases retain their assignments and later gains are profile-specific");
            profiles.ClearCurrentAssignment(); profiles.SelectCar("IRacing", "a", "Alias A");
            check(profiles.PanelState().AssignedId == "",
                "a legacy provisional model assignment cannot resurrect a cleared stable assignment after import");
            profiles.SelectProfile(a); profiles.AssignCurrentProfile();
        }
        check((int)JObject.Parse(File.ReadAllText(file))["SchemaVersion"]! == 2, "library import is persisted as schema two");
        using (var profiles = new ProfileRepository(file)) {
            profiles.SelectCar("IRacing", "a", "Same car name");
            check(profiles.CurrentProfileId == a && profiles.Current().ShiftKick == .65 && profiles.Current().EffectsGain == 1.7,
                "reopening a migrated library does not repeat import or reset saved tuning");
        }
        var ordinary = (JObject)legacy.DeepClone(); ordinary["Device"]!["EffectsGain"] = 2.1;
        string ordinaryFile = file + ".ordinary.json"; File.WriteAllText(ordinaryFile, ordinary.ToString());
        using (var profiles = new ProfileRepository(ordinaryFile))
            check(profiles.CurrentProfileId == "author-balanced" && profiles.PanelState().Profiles.Count == 7,
                "migration avoids an unnecessary extra baseline when the old global gain already equals new x1");
    }
    private static bool Rejects(Action action)
    {
        try { action(); return false; } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; }
    }
}
