using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using PedalFeel.SimHub;

internal static class Program
{
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("SIMHUB_INSTALL_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SimHub");
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            string file = Path.Combine(host, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        };
        try { Run(host); Console.WriteLine("PASS: " + checks + " managed checks"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string host)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        L10n.OverrideCulture = "ru-RU";
        Check(!Environment.Is64BitProcess, "x86 host architecture");
        Check(Marshal.SizeOf(typeof(NativeConfig)) == 176 && Marshal.SizeOf(typeof(NativeOutput)) == 80, "ABI v3 structure sizes");
        Check(Marshal.OffsetOf(typeof(NativeConfig), "BrakeMinimum").ToInt32() == 88, "ABI inline calibration offset");
        Check(Marshal.OffsetOf(typeof(NativeConfig), "EffectsGain").ToInt32() == 152, "ABI v3 appended effect gain offset");
        Check(Marshal.OffsetOf(typeof(NativeConfig), "LimiterStrength").ToInt32() == 160, "ABI v3 appended limiter strength offset");
        Check(Marshal.OffsetOf(typeof(NativeConfig), "DownshiftKick").ToInt32() == 168, "ABI v3 appended downshift kick offset");
        using (var engine = new NativeEngine()) {
            engine.Configure(new PedalFeelSettings());
            var frame = engine.Tick();
            Check(frame.Version == 3 && frame.Size == 80, "Actual native cdecl/PInvoke ABI v3");
            Check(frame.BrakeIntensity >= 0 && frame.BrakeIntensity <= 100, "Native output range");
            if (frame.Connected == 0) Check(frame.BrakeIntensity == 0 && frame.ThrottleIntensity == 0, "No telemetry means native silence");
        }
        Profiles(); Controllers(); AutomaticOwnership(); PresetChecks.Run(Check); GainChecks.Run(Check); TuningChecks.Run(Check); LibraryChecks.Run(Check); PreviewChecks.Run(Check); ProfileSessionChecks.Run(Check);
        var adapterChecks = typeof(Program).Assembly.GetType("AdapterChecks");
        if (adapterChecks != null) adapterChecks.GetMethod("Run")!.Invoke(null, new object[] { (Action<bool,string>)Check });
        StatusChecks.Run(Check);
        PanelChecks.Run(Check);
        LocalizationChecks.Run(Check);
        LocalizationHostChecks.Run(host, Check);
        Check(PedalFeelExtensionFilter.SimagicHapticsId == SimHubHapticsAdapter.SupportedDeviceTypeId, "Device filter and adapter identities match");
        app.Shutdown();
    }
    private static void Profiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "PedalFeelTests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "profiles.json");
        using (var profiles = new ProfileRepository(path)) {
            profiles.CreateProfile("Garage tuning", "author-balanced", false);
            var settings = profiles.Current(); settings.Strength = .31; settings.Enabled = true; settings.BrakeMaximum[0] = 27;
            profiles.Commit(settings);
            string a = ProfileRepository.CarKey("IRacing", "ferrari296gt3", "Ferrari 296 GT3");
            string b = ProfileRepository.CarKey("IRacing", "bmwm4gt3", "BMW M4 GT3");
            Check(profiles.Select(a, "Ferrari 296 GT3"), "New car selects a profile");
            Check(profiles.Current().Strength == .70, "Unassigned car receives the author Balanced base");
            profiles.CreateProfile("Ferrari tuning", "author-balanced", true);
            settings = profiles.Current(); settings.Strength = .8; profiles.Commit(settings);
            profiles.Select(b, "BMW M4 GT3");
            Check(profiles.Current().Strength == .70, "Different car has independent initial tuning");
            profiles.CreateProfile("BMW tuning", "author-balanced", true);
            settings = profiles.Current(); settings.BrakeMaximum[0] = 42; profiles.Commit(settings);
            profiles.Select(a, "Ferrari 296 GT3");
            Check(profiles.Current().Strength == .8 && profiles.Current().BrakeMaximum[0] == 42, "Car tuning independent, calibration shared");
            Check(profiles.Current().Enabled, "Mode is per-device and survives car changes");
            string displayedProfile = profiles.CurrentProfileId;
            settings = profiles.Current(); settings.Strength = .6;
            profiles.Select(b, "BMW M4 GT3");
            profiles.CommitProfile(displayedProfile, settings);
            Check(profiles.Current().Strength == .70, "A delayed UI edit cannot overwrite the new car");
            profiles.Save();
        }
        using (var restored = new ProfileRepository(path)) {
            restored.Select("iracing|id:ferrari296gt3", "Ferrari");
            Check(restored.Current().Strength == .6, "Car tuning persists after reload");
            var reset = restored.ResetCurrent();
            Check(reset.Strength == .70 && reset.BrakeMaximum[0] == 42 && reset.Enabled, "Reset only current car effects; device calibration/mode retained");
            restored.Save();
        }
        File.WriteAllText(path, "{broken");
        using (var restored = new ProfileRepository(path)) {
            restored.Select("iracing|id:ferrari296gt3", "Ferrari");
            Check(restored.Current().Strength == .6, "Corrupt settings recover from last good backup");
        }
        Check(Directory.GetFiles(root, "*.recovery-*").Length > 0, "Unreadable settings preserved outside rotating backup");
        using (var aliases = new ProfileRepository(Path.Combine(root, "aliases.json"))) {
            aliases.SelectCar("IRacing", null, "Porsche 911 GT3 R");
            aliases.CreateProfile("Porsche tuning", "author-balanced", true);
            var tuned = aliases.Current(); tuned.Strength = .44; aliases.Commit(tuned);
            aliases.SelectCar("IRacing", "porsche992rgt3", "Porsche 911 GT3 R");
            Check(aliases.Current().Strength == .44, "Provisional model tuning migrates when stable car ID arrives");
            string stableKey = aliases.CurrentKey;
            aliases.SelectCar("IRacing", null, "Porsche 911 GT3 R");
            Check(aliases.CurrentKey == stableKey && aliases.Current().Strength == .44, "Temporarily missing car ID keeps current tuning");
        }
        // The temp files contain only this test's settings, useful for inspection if a check fails.
        Check(ProfileRepository.CarKey("AssettoCorsa", "1", "Car") == ProfileRepository.DefaultKey, "Other games do not collide with iRacing car keys");
    }
    private static void Controllers()
    {
        long now = 1000;
        var fake = new FakeEngine();
        using (var controller = new PedalController(() => fake, () => now)) {
            controller.Configure(new PedalFeelSettings { Enabled = true });
            controller.SetContext("IRacing", true, false);
            var output = controller.Produce();
            Check(controller.Active && output!.BrakeIntensity == 20, "Active mode uses engine output");
            controller.RequestTest(1, 25, 12);
            Check(controller.Produce()!.BrakeIntensity == 12, "Explicit test routes to brake");
            now += 500;
            Check(controller.Produce()!.BrakeIntensity == 20, "Test automatically ends at 500ms");
            fake.Frame.Connected = 0;
            controller.RequestTest(2, 50, 17);
            Check(controller.Produce()!.ThrottleIntensity == 17, "Manual test does not require iRacing telemetry");
            now += 500;
            Check(controller.Produce()!.ThrottleIntensity == 0, "Idle manual test emits zero when 500ms expires");
            controller.RequestTest(1, 16, 11);
            controller.CancelTest();
            Check(controller.Produce()!.BrakeIntensity == 0, "Cancelled test cannot fire after a delayed connection");
            Check(RefusesTest(controller, 1, 25, 0), "Zero-power test gives an explicit explanation");
            fake.Frame.Connected = 1;
            fake.Frame.Stale = 1;
            Check(controller.Produce()!.BrakeIntensity == 0, "Stale frame cannot drive motors");
            fake.Frame.Stale = 0;
            controller.SetContext("IRacing", true, true);
            Check(controller.Produce()!.ThrottleIntensity == 0, "Replay/pause emits zero");
            controller.SetContext("AssettoCorsa", true, false);
            Check(!controller.Active && controller.Produce() == null, "Unsupported game returns ownership to stock");
            Check(RefusesTest(controller), "Manual test explains unsupported selected game");
            controller.SetContext("IRacing", true, false);
            Check(controller.Active, "iRacing automatically reactivates chosen mode");
            controller.Configure(new PedalFeelSettings { Enabled = false });
            Check(!controller.Active && controller.Produce() == null, "Stock mode bypasses native engine");
            Check(RefusesTest(controller), "Manual test cannot silently succeed in stock mode");
            controller.Configure(new PedalFeelSettings { Enabled = true, BrakeEnabled = false });
            Check(RefusesTest(controller), "Disabled pedal rejects a manual test");
        }
        using (var broken = new PedalController(() => throw new DllNotFoundException("test"))) {
            broken.Configure(new PedalFeelSettings { Enabled = true });
            Check(!broken.Active && broken.Produce() == null, "Native load failure leaves stock in control");
            Check(RefusesTest(broken), "Manual test exposes native load failure");
        }
        using (var duplicate = new PedalController(() => new FakeEngine())) {
            duplicate.Configure(new PedalFeelSettings { Enabled = true, BrakeChannel = 1, ThrottleChannel = 1 });
            Check(!duplicate.Active, "Invalid duplicate mapping rejected");
        }
        int readers = 0;
        using (var perCar = new PedalController(() => { readers++; return new FakeEngine(); })) {
            var settings = new PedalFeelSettings { Enabled = true };
            perCar.Configure(settings);
            perCar.Configure(settings);
            Check(readers == 1, "Effect tuning retains the car's telemetry reader");
            perCar.Configure(settings, resetTelemetry: true);
            Check(readers == 2, "A car change resets learned ABS, wheel and drivetrain telemetry state");
        }
    }
    private static void AutomaticOwnership()
    {
        long now = 1000;
        int readers = 0;
        using (var controller = new PedalController(() => { readers++; return new FakeEngine(); }, () => now)) {
            var settings = new PedalFeelSettings { Enabled = true };
            controller.Configure(settings);
            Check(!controller.Active && controller.Produce() == null, "Armed mode with no game context leaves stock output available");
            controller.SetContext("IRacing", false, false);
            settings.Strength = .4; controller.Configure(settings);
            Check(!controller.Active && controller.Status.Contains("ожидание запуска"), "Selecting iRacing or editing effects cannot acquire output before the simulator runs");
            controller.ValidateTest(1, 25, 12);
            Check(!controller.Active, "Test validation alone cannot acquire ownership");
            controller.RequestTest(1, 25, 12);
            Check(controller.Active && controller.Produce()!.BrakeIntensity == 12, "Offline test temporarily acquires output without running iRacing");
            now += 499;
            Check(controller.Active, "Offline test retains ownership until its deadline");
            now++;
            Check(!controller.Active && controller.Produce() == null && controller.Status.Contains("стандартные"), "Offline test deadline automatically returns ownership and status to stock");
            controller.SetContext("IRacing", true, false);
            Check(controller.Active && controller.Produce()!.BrakeIntensity == 20 && readers == 2, "Actual simulator start acquires output and resets session telemetry learning");
            controller.SetContext("IRacing", true, true);
            Check(controller.Active && controller.Produce()!.BrakeIntensity == 0, "Pause retains ownership with silence instead of leaking stock effects");
            controller.SetContext("IRacing", true, false);
            controller.RequestTest(1, 16, 11);
            controller.SetContext("IRacing", false, false);
            Check(!controller.Active && controller.Produce() == null, "Simulator exit cancels an in-flight test and returns stock ownership");
            controller.SetContext("IRacing", true, false);
            Check(controller.Active && controller.Produce()!.BrakeIntensity == 20 && readers == 3, "Next simulator launch reacquires automatically without resuming the old test");
            controller.SetContext(null, true, false);
            Check(!controller.Active, "A missing game name cannot retain iRacing ownership");
            controller.SetContext("AssettoCorsa", true, false);
            Check(!controller.Active && RefusesTest(controller), "A different running game retains stock and refuses a conflicting test");
            controller.SetContext("AssettoCorsa", false, false);
            controller.RequestTest(2, 35, 10);
            Check(controller.Active && controller.Produce()!.ThrottleIntensity == 10, "Offline calibration does not require changing SimHub's selected game");
            controller.CancelTest();
            Check(!controller.Active, "Cancelling an offline test immediately releases ownership");
            settings.Enabled = false; controller.Configure(settings);
            controller.SetContext("IRacing", true, false);
            Check(!controller.Active && RefusesTest(controller), "Choosing standard effects disarms future automatic takeovers");
            settings.Enabled = true; controller.Configure(settings);
            Check(controller.Active, "Arming while iRacing is already running takes over immediately");
        }
    }
    private static bool RefusesTest(PedalController controller, int channel = 1, int hz = 25, int power = 12)
    {
        try { controller.RequestTest(channel, hz, power); return false; }
        catch (InvalidOperationException error) { return !string.IsNullOrWhiteSpace(error.Message); }
        catch (ArgumentException error) { return !string.IsNullOrWhiteSpace(error.Message); }
    }
    internal static void Check(bool pass, string label)
    {
        if (!pass) throw new InvalidOperationException("FAIL: " + label);
        checks++; Console.WriteLine("OK " + label);
    }
    private sealed class FakeEngine : IEffectEngine
    {
        public NativeOutput Frame = new NativeOutput { Connected = 1, DrivingActive = 1, BrakeHz = 25, BrakeIntensity = 20, ThrottleHz = 35, ThrottleIntensity = 10 };
        public void Configure(PedalFeelSettings settings) { }
        public NativeOutput Tick() => Frame;
        public void Dispose() { }
    }
}
