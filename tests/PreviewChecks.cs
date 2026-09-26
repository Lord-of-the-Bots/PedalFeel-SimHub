using System;
using PedalFeel.SimHub;

internal static class PreviewChecks
{
    public static void Run(Action<bool, string> check)
    {
        long now = 0;
        var fake = new PreviewEngine();
        var settings = new PedalFeelSettings { Enabled = true, BrakeChannel = 0, ThrottleChannel = 2 };
        using (var controller = new PedalController(() => fake, () => now)) {
            controller.Configure(settings);
            controller.SetContext("IRacing", false, false);
            check(!controller.Active, "preview does not take ownership merely by opening/configuring settings");
            controller.RequestPreview(EffectPreviewKind.Abs);
            check(controller.Active && controller.PreviewActive, "explicit preview temporarily takes ownership without a running game");
            now = 500;
            var command = controller.Produce();
            check(command != null && command.BrakeChannel == 0 && command.ThrottleChannel == 2 && command.BrakeIntensity == 17 && command.ThrottleIntensity == 0 && fake.Elapsed == .5,
                "effect preview uses the real elapsed time and current physical channel mapping");
            check(fake.Ticks == 0 && fake.Previews == 1, "preview uses its isolated rendering path, never live telemetry polling");
            check(controller.Presentation().Title.Contains("ABS"), "active preview identifies the effect in the status");
            controller.RequestPreview(EffectPreviewKind.Engine);
            command = controller.Produce();
            check(fake.Effect == EffectPreviewKind.Engine && fake.Elapsed == 0 && command!.BrakeIntensity == 0 && command.ThrottleIntensity == 23,
                "another preview replaces the first and starts its own timeline");
            now = 2499; controller.Produce();
            check(controller.Active, "preview remains active just before its fixed two-second limit");
            now = 2500;
            check(!controller.Active && controller.Produce() == null, "offline preview releases ownership exactly at its time limit");
            controller.RequestPreview(EffectPreviewKind.Engine); controller.CancelPreview();
            check(!controller.Active && !controller.PreviewActive, "stop preview releases temporary ownership without disabling automatic mode");

            controller.RequestPreview(EffectPreviewKind.Abs); controller.RequestTest(0, 25, 50);
            command = controller.Produce();
            check(!controller.PreviewActive && command!.BrakeIntensity == 50 && command.BrakeFrequency == 25,
                "500ms frequency test replaces preview and keeps direct power semantics");
            controller.RequestPreview(EffectPreviewKind.Abs); controller.CancelTest();
            check(!controller.Active, "device cancellation callback stops pending effect preview as well as frequency tests");
            controller.RequestPreview(EffectPreviewKind.Abs); controller.Configure(settings);
            check(!controller.PreviewActive && !controller.Active, "editing or switching profile cancels the previous preview");
            controller.RequestPreview(EffectPreviewKind.Abs); controller.SetContext("IRacing", true, false);
            check(!controller.PreviewActive && controller.Active, "game start cancels preview and returns to live processing");
            controller.RequestPreview(EffectPreviewKind.Limiter);
            now += 2000;
            int ticks = fake.Ticks;
            command = controller.Produce();
            check(command != null && command.BrakeIntensity == 0 && command.ThrottleIntensity == 0 && fake.Ticks == ticks,
                "preview deadline emits a quiet frame before a live session resumes");
            controller.Produce();
            check(fake.Ticks == ticks + 1, "live telemetry resumes after the quiet transition frame");
            controller.RequestPreview(EffectPreviewKind.Abs); controller.SetContext("IRacing", true, true);
            check(!controller.PreviewActive, "pause/replay transition cancels active preview");
            controller.SetContext("OtherGame", true, false);
            check(Throws(() => controller.RequestPreview(EffectPreviewKind.Abs)), "preview cannot intercept another running game");
            controller.SetContext("IRacing", false, false);
            settings.BrakeEnabled = false; controller.Configure(settings);
            check(Throws(() => controller.RequestPreview(EffectPreviewKind.Abs)), "brake preview refuses a disabled brake pedal");
            controller.RequestPreview(EffectPreviewKind.Surface);
            check(controller.PreviewActive, "road preview remains available for the enabled throttle alone");
            settings.BrakeEnabled = true; settings.AbsPunch = 0; controller.Configure(settings);
            check(Throws(() => controller.RequestPreview(EffectPreviewKind.Abs)), "zero effect strength is never replaced by a test amplitude");
            settings.AbsPunch = .7; settings.Strength = 0; controller.Configure(settings);
            check(Throws(() => controller.RequestPreview(EffectPreviewKind.Abs)), "ABS preview respects the overall brake strength control");
            settings.Strength = .7; settings.EffectsGain = 0; controller.Configure(settings);
            check(Throws(() => controller.RequestPreview(EffectPreviewKind.Engine)), "preview respects a muted profile gain");
            settings.EffectsGain = 2.1; settings.Enabled = false; controller.Configure(settings);
            check(Throws(() => controller.RequestPreview(EffectPreviewKind.Engine)), "automatic mode must be armed before preview can take ownership");
            settings.Enabled = true; controller.Configure(settings);
            check(Throws(() => controller.RequestPreview((EffectPreviewKind)99)), "unknown preview selection is rejected before taking ownership");
            fake.ThrowPreview = true; controller.RequestPreview(EffectPreviewKind.Abs);
            check(controller.Produce() == null && !controller.Active, "preview failure returns output ownership to stock rather than retaining the motor command");
        }
        Native(check);
    }

    private static void Native(Action<bool, string> check)
    {
        using (var engine = new NativeEngine()) {
            var settings = new PedalFeelSettings { BrakeEngineTexture = .25, BrakeIdleTexture = .25 }; engine.Configure(settings);
            foreach (EffectPreviewKind effect in Enum.GetValues(typeof(EffectPreviewKind))) {
                bool felt = false;
                for (int i = 0; i < 120; ++i) {
                    var frame = engine.Preview(effect, i / 60.0);
                    felt |= frame.BrakeIntensity > 0 || frame.ThrottleIntensity > 0;
                    if (frame.BrakeIntensity < 0 || frame.BrakeIntensity > 35 || frame.ThrottleIntensity < 0 || frame.ThrottleIntensity > 35)
                        throw new InvalidOperationException("Native preview exceeded calibrated ceiling: " + effect);
                    if (!EffectPreview.UsesBrake(effect) && frame.BrakeIntensity != 0 || !EffectPreview.UsesThrottle(effect) && frame.ThrottleIntensity != 0)
                        throw new InvalidOperationException("Native preview activated an unrelated pedal: " + effect);
                }
                check(felt, "real native preview produces the requested effect within current calibrated limits: " + effect);
                var stopped = engine.Preview(effect, 2);
                check(stopped.BrakeIntensity == 0 && stopped.ThrottleIntensity == 0 && stopped.DrivingActive == 0,
                    "real native preview stops at two seconds: " + effect);
            }
            var first = engine.Preview(EffectPreviewKind.Limiter, .8);
            engine.Preview(EffectPreviewKind.Abs, .7);
            var again = engine.Preview(EffectPreviewKind.Limiter, .8);
            check(first.BrakeRaw == again.BrakeRaw && first.ThrottleRaw == again.ThrottleRaw && first.ThrottleIntensity == again.ThrottleIntensity,
                "preview is deterministic and independent of other preview calls");
            settings.EffectsGain = 0; engine.Configure(settings);
            var muted = engine.Preview(EffectPreviewKind.Limiter, .8);
            check(muted.BrakeIntensity == 0 && muted.ThrottleIntensity == 0, "native preview uses profile gain including exact mute");
            check(Throws(() => engine.Preview(EffectPreviewKind.Abs, double.NaN)), "native preview rejects non-finite time through PInvoke");
        }
    }
    private static bool Throws(Action action) { try { action(); return false; } catch (Exception) { return true; } }
    private sealed class PreviewEngine : IEffectEngine, IEffectPreviewEngine
    {
        public int Ticks, Previews;
        public double Elapsed;
        public EffectPreviewKind Effect;
        public bool ThrowPreview;
        public void Configure(PedalFeelSettings settings) { }
        public NativeOutput Tick() { ++Ticks; return new NativeOutput(); }
        public NativeOutput Preview(EffectPreviewKind effect, double elapsedSeconds)
        {
            if (ThrowPreview) throw new InvalidOperationException("fixture preview failure");
            ++Previews; Effect = effect; Elapsed = elapsedSeconds;
            return new NativeOutput { BrakeHz = EffectPreview.UsesBrake(effect) ? 25 : 0,
                BrakeIntensity = EffectPreview.UsesBrake(effect) ? 17 : 0,
                ThrottleHz = EffectPreview.UsesThrottle(effect) ? 35 : 0,
                ThrottleIntensity = EffectPreview.UsesThrottle(effect) ? 23 : 0 };
        }
        public void Dispose() { }
    }
}
