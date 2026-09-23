using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PedalFeel.SimHub;

internal static class PresetChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(CarPresets.Cars.Length >= 190, "Embedded roster includes current and legacy car variants");
        foreach (var car in CarPresets.Cars) {
            if (CarPresets.Resolve("default", car.Name).Preset.Id != car.Family)
                throw new InvalidOperationException("Catalog name mapping failed: " + car.Name);
            foreach (var path in car.Paths)
                if (CarPresets.Resolve("iracing|id:" + path, "").Preset.Id != car.Family)
                    throw new InvalidOperationException("Catalog path mapping failed: " + path);
        }
        check(true, "Every catalog name and documented path selects its intended starting family");
        check(CarPresets.Resolve("iracing|id:123456", "Honda Civic Type R TCR").Preset.Id == "touring_fwd", "Numeric car IDs use the reported model name without guessing ID mappings");
        check(CarPresets.Resolve("default", "Porsche 911 GT3 Cup (992.1)").Preset.Id == "cup", "Porsche Cup cannot be confused with GT3 R");
        check(CarPresets.Resolve("iracing|id:stockcarbrasil\\cruze", "Stock Car Pro Series Chevrolet Cruze").Preset.Id == "road_rwd", "Stock Car Brasil Cruze uses its rear-drive race platform, not the road car drivetrain");
        check(CarPresets.Resolve("iracing|id:\\cars\\ferrari296gt3", "").Preset.Id == "gt3", "Documented paths normalize the cars prefix and separators");
        check(CarPresets.Resolve("default", "Some new GT3 2027").Confidence == 1, "Future named classes are marked as inferred, not exact catalog matches");
        check(CarPresets.Resolve("iracing|id:unknown", "Unknown car").Preset.Id == "fallback", "Unrecognized cars always have a conservative universal starting point");
        check(CarPresets.Get("gt3").Create().Strength == .70 && CarPresets.Get("gt3").Create().GripThreshold == .91, "GT3 Balanced grip settings remain unchanged by the softer shift tuning");
        check(CarPresets.All.All(p => p.Create().ShiftKick >= 0 && p.Create().ShiftKick <= .20), "Every built-in upshift default is at most 20 percent");
        check(CarPresets.Get("oval_open").Create().ShiftKick == .18 && CarPresets.Get("dirt_oval").Create().ShiftKick == .15, "Existing upshift defaults below 20 percent are preserved");
        check(CarPresets.All.Where(p => p.Id != "electric").All(p => p.Create().LimiterStrength == .20 && p.Create().DownshiftKick == .20), "Combustion presets start limiter and downshift controls at 20 percent");
        check(new[] { "touring_fwd", "road_awd", "dirt_oval", "rallycross", "offroad", "electric", "fallback" }.All(id => CarPresets.Get(id).Create().TractionStrength == 0), "Unsupported driven-wheel contexts do not start with rear-traction amplitude");
        var electric = CarPresets.Get("electric").Create();
        check(electric.EngineTexture == 0 && electric.IdleTexture == 0 && electric.ShiftKick == 0 && electric.LimiterStrength == 0 && electric.DownshiftKick == 0, "Electric preset removes combustion engine, idle, both shifts and limiter amplitudes");

        var balanced = CarPresets.CreateBasis("author-balanced");
        var subtle = CarPresets.CreateBasis("author-subtle");
        var aggressive = CarPresets.CreateBasis("author-aggressive");
        check(balanced.GripThreshold == .91 && balanced.Strength == .70 && balanced.Texture == .62 &&
            balanced.AbsPunch == .72 && balanced.TractionStrength == .65 && balanced.EngineTexture == .48 &&
            balanced.IdleTexture == .28 && balanced.ShiftKick == .20,
            "Author Balanced retains upstream tuning with the requested 20 percent upshift cap");
        check(subtle.GripThreshold == .93 && subtle.Strength == .52 && subtle.Texture == .45 &&
            subtle.AbsPunch == .58 && subtle.TractionStrength == .48 && subtle.EngineTexture == .30 &&
            subtle.IdleTexture == .16 && subtle.ShiftKick == .20,
            "Author Subtle retains upstream tuning with the requested 20 percent upshift cap");
        check(aggressive.GripThreshold == .87 && aggressive.Strength == .88 && aggressive.Texture == .84 &&
            aggressive.AbsPunch == .90 && aggressive.TractionStrength == .82 && aggressive.EngineTexture == .68 &&
            aggressive.IdleTexture == .40 && aggressive.ShiftKick == .20,
            "Author Agggressive retains upstream tuning with the requested 20 percent upshift cap");
        check(new[] { balanced, subtle, aggressive }.All(s => s.LimiterStrength == 1 && s.DownshiftKick == 1 && s.SurfaceStrength == .28),
            "Author bases retain original fixed limiter/downshift amplitudes and surface default");
        check(new[] { balanced, subtle, aggressive }.All(s => s.EffectsGain == 2.1),
            "All new author profiles use the requested common x1 baseline of absolute gain 2.1");
        var formula = CarPresets.CreateBasis("formula");
        check(formula.EffectsGain == 2.1 && formula.ShiftKick == .20 && formula.DownshiftKick == .20 && formula.LimiterStrength == .20,
            "Formula basis retains the last softer 0.3.4 gain and transient settings");
        check(CarPresets.Bases.All(p => p.Key != "gt4" && p.Key != "auto"),
            "Primary profile bases do not claim an author GT4 or guess a car-family assignment");
    }
}
