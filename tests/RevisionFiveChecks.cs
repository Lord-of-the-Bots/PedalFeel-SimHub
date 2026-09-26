using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PedalFeel.SimHub;

internal static class RevisionFiveChecks
{
    public static void Run(Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "PedalFeel05-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "profiles.json");
        using (var r = new ProfileRepository(path)) {
            check(r.PanelState().Profiles.Count == 2 && r.CurrentProfileId == CarPresets.Standard, "only Standard and Original GT3 are seeded");
            var s = r.Current();
            check(s.Strength == .60 && s.GripThreshold == 1 && s.Texture == 1 && s.AbsPunch == 1 &&
                s.DownshiftKick == .80 && s.ShiftKick == .80 && s.TractionStrength == .40 &&
                s.EngineTexture == .25 && s.IdleTexture == .25 && s.LimiterStrength == .25 && s.SurfaceStrength == .30 && s.EffectsGain == 2.1,
                "Standard matches every supplied screenshot value and the fixed x1 baseline");
            r.SelectCar("IRacing", "a", "Car A");
            string id = r.CreateProfile("Custom", "current", true);
            s = r.Current(); s.ThrottleStrength = .47; s.BrakeEngineTexture = .31; s.BrakeIdleTexture = .22;
            r.Commit(s);
            r.SelectCar("IRacing", "b", "Car B"); r.SelectProfile(id); r.AssignCurrentProfile();
            check(!r.SelectCar("IRacing", "b", ""), "missing model name cannot request a panel rebuild for the same stable car");
            check(r.Current().ThrottleStrength == .47 && r.Current().BrakeEngineTexture == .31 && r.Current().BrakeIdleTexture == .22,
                "separate pedal and engine gains are copied and shared through profile assignments");
            check(r.DeleteProfile(id) == 2 && r.CurrentProfileId == CarPresets.Standard, "deletion switches current profile and both assigned cars to Standard");
            r.SelectCar("IRacing", "a", "Car A");
            check(r.CurrentProfileId == CarPresets.Standard && r.PanelState().AssignedId == CarPresets.Standard,
                "a deleted profile never leaves a dangling assignment");
            r.Save();
        }
        using (var r = new ProfileRepository(path)) {
            r.SelectCar("IRacing", "b", "Car B");
            check(r.CurrentProfileId == CarPresets.Standard && r.PanelState().Profiles.Count == 2, "deletion and fallback persist across restart");
        }
        var old = JObject.Parse(File.ReadAllText(path)); old["SchemaVersion"] = 2;
        old["Profiles"]!["formula"] = JObject.FromObject(new { Name = "Formula", BasisId = "formula", LocalizedName = true, Settings = new PedalFeelSettings { Strength = .44, EffectsGain = 4.2 } });
        old["Profiles"]!["personal"] = JObject.FromObject(new { Name = "My tuning", BasisId = "formula", Settings = new PedalFeelSettings { Strength = .38, BrakeMaximum = new double[] { 51, 52, 53, 54 } } });
        old["CarAssignments"]!["iracing|id:a"] = "formula"; old["CarAssignments"]!["iracing|id:b"] = "personal";
        old["SelectedProfileId"] = "formula"; File.WriteAllText(path, old.ToString());
        using (var r = new ProfileRepository(path)) {
            check(r.CurrentProfileId == CarPresets.Standard && r.PanelState().Profiles.Count == 3,
                "upgrade retires old built-in variants but retains user-created profiles");
            r.SelectCar("IRacing", "b", "Car B");
            check(r.CurrentProfileId == "personal" && r.Current().Strength == .38 && r.Current().EffectsGain == 2.1,
                "upgrade preserves personal effects and assignments while fixing the common baseline");
        }
        check(File.Exists(path + ".before-0.5.0") && JObject.Parse(File.ReadAllText(path + ".before-0.5.0"))["Profiles"]!["formula"] != null,
            "migration keeps a dedicated snapshot of retired tuning");
        using (var engine = new NativeEngine()) {
            var s = new PedalFeelSettings { BrakeEngineTexture = .5, BrakeIdleTexture = .5, ThrottleStrength = 0 };
            engine.Configure(s);
            var idle = engine.Preview(EffectPreviewKind.BrakeIdle, .6);
            var drive = engine.Preview(EffectPreviewKind.BrakeEngine, .8);
            check(idle.BrakeIntensity > 0 && drive.BrakeIntensity > 0 && idle.ThrottleIntensity == 0 && drive.ThrottleIntensity == 0,
                "brake engine and idle have independent native output with throttle fully muted");
            s.Strength = 0; engine.Configure(s);
            check(engine.Preview(EffectPreviewKind.BrakeEngine, .8).BrakeIntensity == 0 && engine.Preview(EffectPreviewKind.BrakeIdle, .6).BrakeIntensity == 0,
                "brake master mutes both new engine layers including previews");
        }
    }
}
