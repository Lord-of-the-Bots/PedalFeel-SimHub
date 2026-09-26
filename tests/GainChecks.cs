using System;
using Newtonsoft.Json;
using PedalFeel.SimHub;

internal static class GainChecks
{
    public static void Run(Action<bool, string> check)
    {
        var legacy = JsonConvert.DeserializeObject<PedalFeelSettings>("{\"Strength\":0.41}")!;
        check(Near(legacy.EffectsGain, PedalFeelSettings.BaseEffectsGain) && Near(legacy.Strength, .41),
            "legacy standalone settings receive a finite baseline without replacing existing tuning");

        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var value = new PedalFeelSettings { EffectsGain = invalid };
            value.Normalize();
            check(Near(value.EffectsGain, PedalFeelSettings.BaseEffectsGain), "non-finite gain falls back to the new baseline: " + invalid);
        }
        foreach (var sample in new[] { new[] { -1.0, 0.0 }, new[] { 9.0, 4.2 }, new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 }, new[] { 2.7, 2.7 }, new[] { 3.0, 3.0 }, new[] { 4.2, 4.2 } })
        {
            var value = new PedalFeelSettings { EffectsGain = sample[0] };
            value.Normalize();
            check(Near(value.EffectsGain, sample[1]), "gain sanitization preserves valid values and clamps bounds: " + sample[0]);
        }
        var native = NativeConfig.From(new PedalFeelSettings { EffectsGain = 1.7 });
        check(native.Version == 4 && native.Size == 200 && Near(native.EffectsGain, 1.7),
            "managed settings pass the chosen gain through the versioned native configuration");

        // Named-profile gain migration and isolation are exercised by LibraryChecks.

        var engine = new ManualOnlyEngine();
        using (var controller = new PedalController(() => engine, () => 0))
        {
            controller.SetContext("IRacing", false, false);
            foreach (double gain in new[] { 2.1, 4.2 })
            {
                var settings = new PedalFeelSettings {
                    Enabled = true, EffectsGain = gain, BrakeChannel = 0, ThrottleChannel = 2,
                    BrakeMinimum = new[] { 90.0, 90, 90, 90 }, BrakeMaximum = new[] { 100.0, 100, 100, 100 },
                    ThrottleMinimum = new[] { 90.0, 90, 90, 90 }, ThrottleMaximum = new[] { 100.0, 100, 100, 100 }
                };
                controller.Configure(settings);
                controller.RequestTest(0, 25, 50);
                var brake = controller.Produce();
                check(brake != null && brake.BrakeIntensity == 50 && brake.BrakeFrequency == 25 && brake.ThrottleIntensity == 0,
                    "manual brake test stays at exactly 50% regardless of game gain and calibration: " + gain);
                controller.RequestTest(2, 35, 50);
                var throttle = controller.Produce();
                check(throttle != null && throttle.ThrottleIntensity == 50 && throttle.ThrottleFrequency == 35 && throttle.BrakeIntensity == 0,
                    "manual throttle test stays at exactly 50% regardless of game gain and calibration: " + gain);
            }
            check(engine.TickCalls == 0, "manual calibration tests bypass the effect-rendering engine entirely");
        }
    }

    private static bool Near(double left, double right) => Math.Abs(left - right) < .000001;
    private sealed class ManualOnlyEngine : IEffectEngine
    {
        public int TickCalls;
        public void Configure(PedalFeelSettings settings) { }
        public NativeOutput Tick() { TickCalls++; throw new InvalidOperationException("Manual tests must not render game effects."); }
        public void Dispose() { }
    }
}
