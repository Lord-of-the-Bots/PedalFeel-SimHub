using System;
using System.Reflection;
using PedalFeel.SimHub;
using SimHub.Plugins.Devices;

internal static class StatusChecks
{
    public static void Run(Action<bool, string> check)
    {
        ControllerTransitions(check);
        FaultPriority(check);
        ReadOnlyDeviceAttention(check);
    }

    private static void ControllerTransitions(Action<bool, string> check)
    {
        long now = 1000;
        var engine = new StatusEngine();
        using (var controller = new PedalController(() => engine, () => now))
        {
            controller.Configure(new PedalFeelSettings { Enabled = true });
            controller.SetContext("IRacing", false, false);
            check(controller.Presentation().Tone == StatusTone.Ready && !controller.Active,
                "presentation distinguishes armed waiting from active pedal ownership");
            controller.SetContext("AssettoCorsa", true, false);
            check(controller.Presentation().Tone == StatusTone.Neutral && !controller.Active,
                "another running game has a stock-mode presentation instead of iRacing readiness");

            controller.SetContext("AssettoCorsa", false, false);
            controller.RequestTest(1, 25, 12);
            check(controller.Presentation().Tone == StatusTone.Active && controller.Active,
                "offline manual test has an active presentation without requiring native telemetry");
            now += 500;
            check(controller.Presentation().Tone == StatusTone.Ready && !controller.Active && engine.Ticks == 0,
                "manual-test presentation expires from the clock even without a rendering callback");

            controller.SetContext("IRacing", true, false);
            check(controller.Presentation().Tone == StatusTone.Ready,
                "a new session cannot display an old active telemetry frame before its first sample");
            engine.Frame = new NativeOutput { Connected = 0 };
            controller.Produce();
            check(controller.Presentation().Tone == StatusTone.Ready,
                "missing iRacing telemetry is waiting rather than an active-effect presentation");
            engine.Frame = LiveFrame();
            controller.Produce();
            check(controller.Presentation().Tone == StatusTone.Active,
                "fresh driving telemetry produces the active controller presentation");
            check(controller.Presentation().OutputSummary == "Тормоз: сигнал 57% → мотор 20% · 25 Гц\nГаз: сигнал 29% → мотор 10% · 35 Гц",
                "visible output distinguishes effect signal from calibrated motor command for both pedals");
            int ticks = engine.Ticks, configurations = engine.Configurations;
            for (int i = 0; i < 5; i++) controller.Presentation();
            check(engine.Ticks == ticks && engine.Configurations == configurations,
                "repeated presentation reads neither sample nor reconfigure the native engine");

            controller.SetContext("IRacing", true, true);
            var paused = controller.Produce();
            check(controller.Presentation().Tone == StatusTone.Neutral && controller.Active && paused!.BrakeIntensity == 0,
                "pause presentation remains quiet while retaining ownership over stock effects");
            check(controller.Presentation().OutputSummary == "", "pause hides the previous moving-car motor values");
            controller.SetContext("IRacing", true, false);
            engine.Frame = new NativeOutput { Connected = 1, Stale = 1 };
            var stale = controller.Produce();
            check(controller.Presentation().Tone == StatusTone.Warning && stale!.BrakeIntensity == 0,
                "stale telemetry presents a warning alongside silent output");
            check(controller.Presentation().OutputSummary == "", "stale telemetry is not presented as a current motor command");
            engine.Frame = new NativeOutput { Connected = 1, DrivingActive = 0 };
            var parked = controller.Produce();
            check(controller.Presentation().Tone == StatusTone.Ready && parked!.BrakeIntensity == 0,
                "an inactive driving session is ready rather than reported as vibrating");
            engine.Frame = LiveFrame(); controller.Produce();
            controller.Configure(new PedalFeelSettings { Enabled = true, Strength = .4 });
            check(controller.Presentation().Tone == StatusTone.Ready,
                "reconfiguration clears the previous frame from the user-facing activity summary");
            controller.SetContext("IRacing", false, false);
            check(controller.Presentation().Tone == StatusTone.Ready && !controller.Active,
                "simulator exit returns the summary to armed waiting without disabling the saved mode");
            controller.Configure(new PedalFeelSettings { Enabled = false });
            check(controller.Presentation().Tone == StatusTone.Neutral && !controller.Active,
                "explicitly disabled automatic mode presents stock ownership rather than readiness");
        }
    }

    private static void FaultPriority(Action<bool, string> check)
    {
        using (var controller = new PedalController(() => throw new InvalidOperationException("status fixture native load failure")))
        {
            controller.Configure(new PedalFeelSettings { Enabled = true });
            controller.SetContext("IRacing", false, false);
            check(controller.Presentation().Tone == StatusTone.Error && !controller.Active,
                "native initialization failure is not hidden by the ordinary waiting-for-game summary");
            controller.SetContext("AssettoCorsa", true, false);
            check(controller.Presentation().Tone == StatusTone.Error,
                "switching to another simulator cannot mask an unresolved native initialization fault");

            var profile = new AdapterProfile();
            var device = MakeDevice(profile);
            device.Enabled = false;
            using (var adapter = new SimHubHapticsAdapter(device, () => null, () => false, false))
            {
                var extension = new PedalFeelExtension();
                SetExtensionField(extension, "controller", controller);
                SetExtensionField(extension, "adapter", adapter);
                var summary = (PanelStatus)typeof(PedalFeelExtension)
                    .GetMethod("PresentationStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(extension, null)!;
                check(summary.Tone == StatusTone.Error && !string.IsNullOrWhiteSpace(summary.Diagnostic) && profile.Provider.Manager.Writes == 0,
                    "combined presentation preserves a controller fault over a less severe device warning without output");
            }
            controller.Configure(new PedalFeelSettings { Enabled = false });
            check(controller.Presentation().Tone == StatusTone.Neutral,
                "disabling the failed feature intentionally returns the summary to standard effects");
        }
    }

    private static void ReadOnlyDeviceAttention(Action<bool, string> check)
    {
        var profile = new AdapterProfile();
        var device = MakeDevice(profile);
        using (var adapter = new SimHubHapticsAdapter(device, () => new MotorCommand(), () => true, false))
        {
            check(adapter.Refresh(), "status fixture discovers the host route without initializing USB");
            FieldInfo manager = FindField(profile.Provider.GetType(), "manager");
            object? before = manager.GetValue(profile.Provider);
            PanelStatus? attention = null;
            for (int i = 0; i < 5; i++) attention = adapter.AttentionStatus(true);
            check(attention != null && attention.Tone != StatusTone.Active && !adapter.IsActive,
                "an unstarted or disconnected transport cannot let a controller summary claim active hardware");
            check(ReferenceEquals(before, manager.GetValue(profile.Provider)) && profile.Provider.Manager.Writes == 0,
                "device status reads never create a USB manager or send a priming frame");
            device.Enabled = false;
            check(adapter.AttentionStatus(false)?.Tone == StatusTone.Warning && profile.Provider.Manager.Writes == 0,
                "disabled device remains visible while idle without acquiring output for a status check");
        }
    }

    private static DeviceInstance MakeDevice(AdapterProfile profile) => (DeviceInstance)typeof(AdapterChecks)
        .GetMethod("MakeDevice", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { profile.Settings })!;

    private static void SetExtensionField(PedalFeelExtension extension, string name, object value) => typeof(PedalFeelExtension)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(extension, value);

    private static FieldInfo FindField(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        throw new MissingFieldException(type.FullName, name);
    }

    private static NativeOutput LiveFrame() => new NativeOutput { Connected = 1, DrivingActive = 1,
        NewSample = 1, BrakeHz = 25, BrakeIntensity = 20, ThrottleHz = 35, ThrottleIntensity = 10,
        BrakeRaw = .57, ThrottleRaw = .29 };

    private sealed class StatusEngine : IEffectEngine
    {
        public NativeOutput Frame;
        public int Ticks, Configurations;
        public void Configure(PedalFeelSettings settings) { Configurations++; }
        public NativeOutput Tick() { Ticks++; return Frame; }
        public void Dispose() { }
    }
}
