using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using BA63Driver;
using BA63Driver.Interfaces;
using GameReaderCommon.Enums;
using PedalFeel.SimHub;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device.HapticDevices;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsWithFrequency;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;
using SimHub.Plugins.DataPlugins.ShakeItV3.Outputs;
using SimHub.Plugins.DataPlugins.ShakeItV3.Outputs.Audio.Renderers;
using SimHub.Plugins.DataPlugins.ShakeItV3.Settings;
using SimHub.Plugins.Devices;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

public static class AdapterChecks
{
    public static void Run(Action<bool, string> check)
    {
        CheckProfileInitialization(check);
        // This fixture supplies the actual host generic device/settings types but replaces transport
        // creation with an in-memory IUSBGenericManagerSerial. It never constructs a real USB manager.
        var profile = new AdapterProfile();
        DeviceInstance device = MakeDevice(profile.Settings);
        bool enabled = false;
        MotorCommand? next = new MotorCommand { BrakeChannel = 1, ThrottleChannel = 2, BrakeFrequency = 37, BrakeIntensity = 80, ThrottleFrequency = 22, ThrottleIntensity = 30 };
        bool fail = false;
        using (var adapter = new SimHubHapticsAdapter(device, () => fail ? throw new InvalidOperationException("fixture") : next, () => enabled, false))
        {
            bool ready = adapter.Refresh();
            if (!ready) Console.WriteLine(typeof(SimHubHapticsAdapter).GetField("_lastError", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(adapter));
            check(ready, "adapter finds the existing P-HPR device without opening transport: " + adapter.Status);
            check(profile.Provider.Manager.Writes == 0, "Refresh performs no hardware output");
            check(!ReferenceEquals(profile.Output.ShakeItChannelsInfoProvider, profile.Provider), "provider is wrapped");
            var stock = new Dictionary<int, ChannelValue> { [1] = new ChannelValue { Frequency = 16, Gain = .2 } };
            var baseline = new AdapterProfile();
            baseline.Provider.UpdateOutput(stock);
            double stockGain = baseline.Provider.Manager.Last!.States[1].Gain;
            profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
            check(profile.Provider.Manager.Last!.States[1].Frequency == 16 && Near(profile.Provider.Manager.Last.States[1].Gain, stockGain), "disabled mode forwards stock values with stock filtering intact");

            // Stock hardware filters must not alter native commands.
            enabled = true;
            profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
            var native = profile.Provider.Manager.Last!;
            check(native.States[1].Frequency == 37 && Near(native.States[1].Gain, .8) && native.States[2].Frequency == 22 && Near(native.States[2].Gain, .3), "native replaces stock through the same manager");
            check(native.States[0].Gain == 0 && adapter.IsActive, "unused physical channel is silent");
            next = new MotorCommand { BrakeChannel = 2, ThrottleChannel = 0, BrakeFrequency = 90, BrakeIntensity = 200, ThrottleFrequency = 0, ThrottleIntensity = 80 };
            Tick(adapter);
            profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
            check(profile.Provider.Manager.Last!.States[2].Frequency == 50 && profile.Provider.Manager.Last.States[2].Gain == 1 && profile.Provider.Manager.Last.States[0].Gain == 0, "mapped commands clamp safely; zero-Hz command stays silent");

            next = null;
            Tick(adapter);
            profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
            check(!adapter.IsActive && profile.Provider.Manager.Last!.States[1].Frequency == 16, "null native result falls back to stock");
            fail = true;
            profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
            check(!adapter.IsActive && adapter.Status.Contains("fixture") && profile.Provider.Manager.Last!.States[1].Frequency == 16, "native exception restores stock and records cause");
            fail = false;
            next = new MotorCommand();
            profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
            check(profile.Provider.Manager.Last!.States[1].Frequency == 16, "fault stays on stock until an explicit disable/re-enable");
            enabled = false; adapter.Refresh(); enabled = true; adapter.Refresh();
            profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
            check(adapter.IsActive && profile.Provider.Manager.Last!.States.All(x => x.Gain == 0), "retry and zero command produce silence rather than stock effects");

            var replacement = new AdapterProfile();
            SetHostedSettings(device, replacement.Settings);
            check(adapter.Refresh() && ReferenceEquals(profile.Output.ShakeItChannelsInfoProvider, profile.Provider), "profile recreation restores stale provider and wraps the current one");
            replacement.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
            check(replacement.Provider.Manager.Writes == 1, "new profile uses its own existing transport");
            adapter.Dispose();
            check(ReferenceEquals(replacement.Output.ShakeItChannelsInfoProvider, replacement.Provider), "Dispose restores original provider");
            check(replacement.Provider.Manager.Last!.States.All(x => x.Gain == 0), "Dispose silences active output without disposing stock manager");
            check(replacement.Provider.Manager.DisposeCount == 0, "adapter does not dispose a transport still owned by SimHub");
        }

        // An unrelated replacement must survive teardown.
        var ownership = new AdapterProfile();
        using (var adapter = new SimHubHapticsAdapter(MakeDevice(ownership.Settings), () => new MotorCommand(), () => false, false))
        {
            adapter.Refresh();
            var foreign = new AdapterFakeProvider();
            ownership.Output.ShakeItChannelsInfoProvider = foreign;
            adapter.Dispose();
            check(ReferenceEquals(ownership.Output.ShakeItChannelsInfoProvider, foreign), "teardown restores only a provider still owned by this adapter");
        }
        CheckAutomaticOwnership(check);
        CheckPreviewOwnership(check);
        CheckTimer(check);
        CheckUi(check);
    }

    private static void CheckAutomaticOwnership(Action<bool, string> check)
    {
        var profile = new AdapterProfile();
        var engine = new OwnershipFakeEngine();
        long now = 1000;
        using (var controller = new PedalController(() => engine, () => now))
        {
            controller.Configure(new PedalFeelSettings { Enabled = true });
            controller.SetContext("IRacing", false, false);
            using (var adapter = new SimHubHapticsAdapter(MakeDevice(profile.Settings), controller.Produce,
                () => controller.Active, false, controller.CancelTest))
            {
                var stock = new Dictionary<int, ChannelValue> { [1] = new ChannelValue { Frequency = 16, Gain = .2 } };
                adapter.Refresh();
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
                check(!controller.Active && !adapter.IsActive && engine.Ticks == 0 &&
                    profile.Provider.Manager.Last!.States[1].Frequency == 16,
                    "armed PedalFeel leaves selected-but-not-running iRacing on stock output");

                int beforeReady = profile.Provider.Manager.Writes;
                adapter.EnsureTestReady();
                check(!controller.Active && !adapter.IsActive && profile.Provider.Manager.Writes == beforeReady,
                    "manual readiness accepts an armed offline controller without artificial ownership or output");
                controller.RequestTest(1, 25, 40);
                adapter.WakeOutput();
                check(controller.Active && adapter.IsActive && profile.Provider.Manager.Last!.States[1].Frequency == 25 &&
                    Near(profile.Provider.Manager.Last.States[1].Gain, .4) && engine.Ticks == 0,
                    "offline manual test temporarily acquires the existing stock transport");
                int testWrites = profile.Provider.Manager.Writes;
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(new Dictionary<int, ChannelValue>());
                check(profile.Provider.Manager.Writes == testWrites && Near(profile.Provider.Manager.Last!.States[1].Gain, .4),
                    "positive and empty stock callbacks cannot replace a temporary manual test");

                // Register a stock root as the UI scan does, then exercise the queued automatic
                // restoration path when the test expires; property/binding details are tested below.
                var stockControl = new SimHub.Plugins.DataPlugins.ShakeItV3.UI.AdapterFixtureStockControl();
                typeof(SimHubHapticsAdapter).GetMethod("DisableStockControls", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(adapter, new object[] { stockControl, new HashSet<DependencyObject>() });
                DrainDispatcher();
                check(!stockControl.IsEnabled, "stock settings remain locked during temporary manual ownership");
                now = 1499;
                Tick(adapter);
                check(adapter.IsActive && Near(profile.Provider.Manager.Last!.States[1].Gain, .4),
                    "temporary test remains active until its 500 ms deadline");
                int beforeExpiry = profile.Provider.Manager.Writes;
                now = 1500;
                Tick(adapter);
                check(!controller.Active && !adapter.IsActive && profile.Provider.Manager.Writes == beforeExpiry + 1 &&
                    profile.Provider.Manager.Last!.States.All(s => s.Gain == 0),
                    "expiry sends exactly one stopping frame before relinquishing temporary ownership");
                DrainDispatcher();
                check(stockControl.IsEnabled,
                    "temporary ownership expiry restores locked stock settings through the queued UI path");
                Tick(adapter);
                check(profile.Provider.Manager.Writes == beforeExpiry + 1,
                    "expired offline test does not keep writing zero over stock effects");
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
                check(profile.Provider.Manager.Last!.States[1].Frequency == 16 &&
                    profile.Provider.Manager.Last.States[1].Gain > 0 && engine.Ticks == 0,
                    "stock resumes after the manual stopping frame without polling inactive native telemetry");

                // The host can observe the expired deadline before the timer does. Its callback
                // must also put a zero frame ahead of the first resumed stock frame.
                now = 2000;
                adapter.EnsureTestReady(); controller.RequestTest(1, 25, 40); adapter.WakeOutput();
                var silentWrites = new List<bool>();
                profile.Provider.Manager.ObserveWrite = states => silentWrites.Add(states.States.All(s => s.Gain == 0));
                now = 2500;
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
                profile.Provider.Manager.ObserveWrite = null;
                check(!adapter.IsActive && silentWrites.Count == 2 && silentWrites[0] && !silentWrites[1],
                    "host-first expiry writes silence before forwarding the resumed stock frame");

                controller.SetContext("IRacing", true, false);
                adapter.Refresh();
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
                check(controller.Active && adapter.IsActive && engine.Ticks > 0 &&
                    profile.Provider.Manager.Last!.States[1].Frequency == 37 && Near(profile.Provider.Manager.Last.States[1].Gain, .67),
                    "running iRacing automatically acquires output using the saved armed setting");
                int nativeWrites = profile.Provider.Manager.Writes;
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
                check(profile.Provider.Manager.Writes == nativeWrites,
                    "stock callbacks remain suppressed during automatic game ownership");

                controller.SetContext("IRacing", false, false);
                adapter.Refresh();
                check(!controller.Active && !adapter.IsActive && profile.Provider.Manager.Last!.States.All(s => s.Gain == 0),
                    "game exit sends the stopping frame and releases automatic ownership");
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
                check(profile.Provider.Manager.Last!.States[1].Frequency == 16 && profile.Provider.Manager.Last.States[1].Gain > 0,
                    "game exit immediately permits normal stock effect callbacks");
                controller.SetContext("IRacing", true, false);
                adapter.Refresh();
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
                check(controller.Active && adapter.IsActive && profile.Provider.Manager.Last!.States[1].Frequency == 37,
                    "a later iRacing launch automatically reacquires output without another Enable or Configure");
            }
        }
    }

    private static void CheckPreviewOwnership(Action<bool, string> check)
    {
        var profile = new AdapterProfile();
        var engine = new OwnershipFakeEngine();
        long now = 1000;
        using (var controller = new PedalController(() => engine, () => now))
        {
            // Non-default physical channels and the ordinary 35% calibration ceiling
            // catch accidental remapping or a second calibration of preview commands.
            controller.Configure(new PedalFeelSettings { Enabled = true, BrakeChannel = 2, ThrottleChannel = 0 });
            controller.SetContext("IRacing", false, false);
            using (var adapter = new SimHubHapticsAdapter(MakeDevice(profile.Settings), controller.Produce,
                () => controller.Active, false, controller.CancelTest))
            {
                var stock = new Dictionary<int, ChannelValue> { [1] = new ChannelValue { Frequency = 16, Gain = .2 } };
                adapter.Refresh();
                adapter.EnsureTestReady();
                controller.RequestPreview(EffectPreviewKind.Engine);
                adapter.WakeOutput();
                check(controller.Active && controller.PreviewActive && adapter.IsActive && engine.Ticks == 0 &&
                    engine.PreviewCalls == 1 && engine.LastPreview == EffectPreviewKind.Engine && Near(engine.LastPreviewSeconds, 0),
                    "offline effect preview acquires output without game telemetry or a stock callback");
                check(profile.Provider.Manager.Last!.States[0].Frequency == 44 &&
                    Near(profile.Provider.Manager.Last.States[0].Gain, .58) &&
                    profile.Provider.Manager.Last.States[1].Gain == 0 && profile.Provider.Manager.Last.States[2].Gain == 0,
                    "preview uses the configured throttle channel and passes the physical command without recalibration");
                int previewWrites = profile.Provider.Manager.Writes;
                int previewCalls = engine.PreviewCalls;
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(new Dictionary<int, ChannelValue>());
                check(profile.Provider.Manager.Writes == previewWrites && engine.PreviewCalls == previewCalls &&
                    Near(profile.Provider.Manager.Last!.States[0].Gain, .58),
                    "positive and empty stock callbacks cannot overwrite or advance an active effect preview");

                now = 2999;
                Tick(adapter);
                check(controller.PreviewActive && adapter.IsActive && Near(engine.LastPreviewSeconds, 1.999),
                    "preview remains active and receives elapsed time until the final millisecond");
                int beforeExpiry = profile.Provider.Manager.Writes;
                previewCalls = engine.PreviewCalls;
                now = 3000;
                Tick(adapter);
                check(!controller.Active && !controller.PreviewActive && !adapter.IsActive &&
                    profile.Provider.Manager.Writes == beforeExpiry + 1 && profile.Provider.Manager.Last!.States.All(s => s.Gain == 0) &&
                    engine.PreviewCalls == previewCalls && engine.Ticks == 0,
                    "two-second preview deadline writes one stop frame before releasing offline ownership");
                Tick(adapter);
                check(profile.Provider.Manager.Writes == beforeExpiry + 1,
                    "expired preview does not continue sending zero over stock output");
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
                check(profile.Provider.Manager.Last!.States[1].Frequency == 16 && profile.Provider.Manager.Last.States[1].Gain > 0,
                    "stock output resumes after the preview stopping frame");

                now = 4000;
                adapter.EnsureTestReady(); controller.RequestPreview(EffectPreviewKind.Engine); adapter.WakeOutput();
                var writesAtHostExpiry = new List<bool>();
                profile.Provider.Manager.ObserveWrite = states => writesAtHostExpiry.Add(states.States.All(s => s.Gain == 0));
                now = 6000;
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
                profile.Provider.Manager.ObserveWrite = null;
                check(!controller.PreviewActive && !adapter.IsActive && writesAtHostExpiry.Count == 2 &&
                    writesAtHostExpiry[0] && !writesAtHostExpiry[1],
                    "host-first preview expiry puts silence before the first resumed stock frame");

                now = 7000;
                adapter.EnsureTestReady(); controller.RequestPreview(EffectPreviewKind.Engine); adapter.WakeOutput();
                previewCalls = engine.PreviewCalls;
                now = 7100;
                controller.RequestTest(2, 35, 46); adapter.WakeOutput();
                check(!controller.PreviewActive && controller.Active && adapter.IsActive && engine.PreviewCalls == previewCalls &&
                    profile.Provider.Manager.Last!.States[2].Frequency == 35 && Near(profile.Provider.Manager.Last.States[2].Gain, .46) &&
                    profile.Provider.Manager.Last.States[0].Gain == 0,
                    "a 500 ms motor test replaces an effect preview immediately with its exact requested power");
                now = 7600;
                Tick(adapter);
                now = 7800;
                Tick(adapter);
                check(!controller.Active && !controller.PreviewActive && !adapter.IsActive && engine.PreviewCalls == previewCalls &&
                    profile.Provider.Manager.Last!.States.All(s => s.Gain == 0),
                    "replaced effect preview cannot resume when the shorter motor test expires");

                now = 8000;
                adapter.EnsureTestReady(); controller.RequestTest(0, 25, 40); adapter.WakeOutput();
                now = 8100;
                controller.RequestPreview(EffectPreviewKind.Surface); adapter.WakeOutput();
                check(controller.PreviewActive && engine.LastPreview == EffectPreviewKind.Surface && Near(engine.LastPreviewSeconds, 0) &&
                    profile.Provider.Manager.Last!.States[2].Frequency == 31 && Near(profile.Provider.Manager.Last.States[2].Gain, .42) &&
                    profile.Provider.Manager.Last.States[0].Frequency == 44 && Near(profile.Provider.Manager.Last.States[0].Gain, .58),
                    "a two-pedal effect preview replaces a motor test and keeps both configured channel mappings");
                now = 8500;
                Tick(adapter);
                check(controller.PreviewActive && adapter.IsActive && Near(engine.LastPreviewSeconds, .4),
                    "the replaced motor test deadline cannot stop the new two-second preview");
                now = 10100;
                Tick(adapter);
                check(!controller.Active && !adapter.IsActive && profile.Provider.Manager.Last!.States.All(s => s.Gain == 0),
                    "replacement preview stops at its own deadline without reviving the old motor test");

                now = 11000;
                adapter.EnsureTestReady(); controller.RequestPreview(EffectPreviewKind.Engine); adapter.WakeOutput();
                previewCalls = engine.PreviewCalls;
                int positiveWrites = profile.Provider.Manager.PositiveWrites;
                profile.Provider.Manager.ConnectOnDisplay = true;
                profile.Provider.Manager.Connected = false;
                now = 11010;
                Tick(adapter);
                check(profile.Provider.Manager.Connected && !controller.Active && !controller.PreviewActive && !adapter.IsActive &&
                    profile.Provider.Manager.Last!.States.All(s => s.Gain == 0) && profile.Provider.Manager.PositiveWrites == positiveWrites,
                    "brief USB disconnect cancels the effect preview and reconnects only with silence");
                int afterReconnect = profile.Provider.Manager.Writes;
                now = 11500;
                Tick(adapter);
                check(engine.PreviewCalls == previewCalls && profile.Provider.Manager.Writes == afterReconnect && engine.Ticks == 0,
                    "a reconnected transport cannot replay the remainder of a cancelled effect preview");
                profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(stock);
                check(profile.Provider.Manager.Last!.States[1].Frequency == 16 && profile.Provider.Manager.Last.States[1].Gain > 0,
                    "stock output is available immediately after preview cancellation on disconnect");
            }
        }
    }

    private sealed class OwnershipFakeEngine : IEffectEngine, IEffectPreviewEngine
    {
        public int Ticks { get; private set; }
        public int PreviewCalls { get; private set; }
        public EffectPreviewKind LastPreview { get; private set; }
        public double LastPreviewSeconds { get; private set; }
        public void Configure(PedalFeelSettings settings) { }
        public NativeOutput Tick()
        {
            Ticks++;
            return new NativeOutput { Connected = 1, DrivingActive = 1, NewSample = 1,
                BrakeHz = 37, BrakeIntensity = 67, ThrottleHz = 22, ThrottleIntensity = 23 };
        }
        public NativeOutput Preview(EffectPreviewKind effect, double elapsedSeconds)
        {
            PreviewCalls++; LastPreview = effect; LastPreviewSeconds = elapsedSeconds;
            bool brake = effect <= EffectPreviewKind.Downshift || effect >= EffectPreviewKind.Surface;
            bool throttle = effect >= EffectPreviewKind.Traction;
            // Native previews deliberately do not claim a live telemetry connection.
            return new NativeOutput { Connected = 0, DrivingActive = 1, NewSample = 1,
                BrakeHz = brake ? 31 : 0, BrakeIntensity = brake ? 42 : 0,
                ThrottleHz = throttle ? 44 : 0, ThrottleIntensity = throttle ? 58 : 0 };
        }
        public void Dispose() { }
    }

    private static void CheckProfileInitialization(Action<bool, string> check)
    {
        var setup = new AdapterProfile();
        var profile = (ShakeItProfile)FormatterServices.GetUninitializedObject(typeof(ShakeItProfile));
        FieldInfo effects = FindField(typeof(ShakeItProfile), "<EffectsContainers>k__BackingField");
        effects.SetValue(profile, Activator.CreateInstance(effects.FieldType));
        // Execute the actual host initializer: it creates a separate output with no provider.
        // The current global output already has the in-memory provider, so no USB is instantiated.
        typeof(ShakeItSettings).GetMethod("InitProfile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(setup.Settings, new object[] { profile });
        FindField(setup.Settings.GetType(), "_CurrentProfile").SetValue(setup.Settings, profile);
        check(profile.OutputManager is MotorsOutputManagerBase && ((MotorsOutputManagerBase)profile.OutputManager).ShakeItChannelsInfoProvider == null,
            "real SimHub InitProfile creates an unused profile output with a null provider");
        var device = MakeDevice(setup.Settings);
        using (var adapter = new SimHubHapticsAdapter(device, () => new MotorCommand(), () => true, false))
        {
            bool ready = adapter.Refresh();
            Console.WriteLine("Profile initialization adapter status: " + adapter.Status);
            check(ready, "unused profile output must not reject the initialized global P-HPR provider");
            check(setup.Provider.Manager.Writes == 0, "profile discovery never starts USB output");

            var profileOutput = (MotorsOutputManagerBase)profile.OutputManager;
            var profileProvider = new AdapterFakeProvider();
            profileProvider.SetSettings(setup.Settings);
            profileOutput.ShakeItChannelsInfoProvider = profileProvider;
            FindField(typeof(ShakeItProfile), "<IncludeOutputSettingsInProfile>k__BackingField").SetValue(profile, true);
            FindField(typeof(ShakeItSettings), "oldManager").SetValue(setup.Settings, profileOutput);
            setup.Output.ShakeItChannelsInfoProvider = null!;
            check(adapter.Refresh() && setup.Output.ShakeItChannelsInfoProvider == null && !ReferenceEquals(profileOutput.ShakeItChannelsInfoProvider, profileProvider),
                "initialized active profile remains usable with an unused null global output");

            setup.Output.ShakeItChannelsInfoProvider = new AdapterUnknownProvider();
            check(adapter.Refresh(), "an unused incompatible provider cannot veto the active P-HPR output");
            profileOutput.ShakeItChannelsInfoProvider = null!;
            check(!adapter.Refresh() && !(bool)typeof(SimHubHapticsAdapter).GetField("_faulted", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(adapter)!,
                "a temporarily null active provider waits without latching a compatibility fault");
            profileOutput.ShakeItChannelsInfoProvider = profileProvider;
            check(adapter.Refresh(), "active provider initialization automatically recovers without a mode toggle");

            profileOutput.ShakeItChannelsInfoProvider = new AdapterUnknownProvider();
            check(!adapter.Refresh() && adapter.Status.Contains(nameof(AdapterUnknownProvider)),
                "an incompatible active provider still fails to stock with its actual type in the diagnostic");
            string diagnostic = (string)typeof(SimHubHapticsAdapter).GetField("_lastProviderDiagnostic", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(adapter)!;
            check(diagnostic.Contains("role=active") && diagnostic.Contains(typeof(AdapterUnknownProvider).FullName!) && diagnostic.Contains(profileOutput.GetType().FullName!),
                "provider diagnostics include the actual provider type, output type and active role");
            profileOutput.ShakeItChannelsInfoProvider = profileProvider;
            check(!adapter.Refresh(), "a real incompatible-provider fault stays latched until mode reset");
            check(profileProvider.Manager.Writes == 0 && setup.Provider.Manager.Writes == 0,
                "all initialization and compatibility regressions leave transport untouched");
        }
    }

    private static void Tick(SimHubHapticsAdapter adapter) => typeof(SimHubHapticsAdapter)
        .GetMethod("Pump", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(adapter, null);

    private static void CheckTimer(Action<bool, string> check)
    {
        var cold = new AdapterProfile();
        cold.Provider.Manager.Connected = false;
        cold.Provider.Manager.ConnectOnDisplay = true;
        using (var adapter = new SimHubHapticsAdapter(MakeDevice(cold.Settings), () => new MotorCommand(), () => true, false))
        {
            adapter.EnsureTestReady();
            check(cold.Provider.Manager.Connected && cold.Provider.Manager.Writes == 1 && cold.Provider.Manager.Last!.States.All(s => s.Gain == 0),
                "manual readiness primes a cold stock transport with silence before starting the test clock");
        }
        var reconnect = new AdapterProfile();
        reconnect.Provider.Manager.Connected = false;
        reconnect.Provider.Manager.ConnectOnDisplay = true;
        using (var adapter = new SimHubHapticsAdapter(MakeDevice(reconnect.Settings), () => new MotorCommand { BrakeIntensity = 60 }, () => true, false))
        {
            adapter.Refresh();
            reconnect.Output.ShakeItChannelsInfoProvider.UpdateOutput(new Dictionary<int, ChannelValue> { [1] = new ChannelValue { Frequency = 35, Gain = 1 } });
            check(reconnect.Provider.Manager.Connected && reconnect.Provider.Manager.Last!.States.All(s => s.Gain == 0) && reconnect.Provider.Manager.Writes == 1,
                "a stock callback that silently reconnects transport cannot leak stock effects into PedalFeel mode");
        }
        var profile = new AdapterProfile();
        var device = MakeDevice(profile.Settings);
        int enabled = 1;
        var clock = Stopwatch.StartNew();
        long deadline = 500;
        int cancellations = 0;
        var pulse = new MotorCommand { BrakeFrequency = 35, BrakeIntensity = 60 };
        using (var adapter = new SimHubHapticsAdapter(device,
            () => clock.ElapsedMilliseconds < Interlocked.Read(ref deadline) ? pulse : new MotorCommand(),
            () => Volatile.Read(ref enabled) == 1,
            () => { Interlocked.Exchange(ref deadline, 0); Interlocked.Increment(ref cancellations); }))
        {
            adapter.EnsureTestReady();
            adapter.WakeOutput();
            check(SpinWait.SpinUntil(() => profile.Provider.Manager.Last?.States[1].Gain > 0, 700),
                "real timer/manual test starts without telemetry or a stock UpdateOutput callback");
            check(SpinWait.SpinUntil(() => clock.ElapsedMilliseconds >= 500 && profile.Provider.Manager.Last?.States[1].Gain == 0, 1600),
                "real timer sends the stopping frame after 500 ms without host callbacks");
            check(clock.ElapsedMilliseconds < 1200, "manual test stop is bounded without telemetry");

            Interlocked.Exchange(ref deadline, long.MaxValue);
            adapter.WakeOutput();
            // Host's empty/zero frame must never overwrite PedalFeel while its pump owns the output.
            profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(new Dictionary<int, ChannelValue>());
            check(profile.Provider.Manager.Last!.States[1].Gain > 0, "empty stock frame cannot silence the independently pumped test");
            int positiveWrites;
            int cancellationsBefore = Volatile.Read(ref cancellations);
            lock (typeof(SimHubHapticsAdapter).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(adapter)!)
            {
                positiveWrites = profile.Provider.Manager.PositiveWrites;
                profile.Provider.Manager.ConnectOnDisplay = true;
                profile.Provider.Manager.Connected = false;
            }
            check(SpinWait.SpinUntil(() => Volatile.Read(ref cancellations) > cancellationsBefore && profile.Provider.Manager.Connected, 500),
                "brief disconnect cancels the pending test even if the stock transport reconnects immediately");
            Thread.Sleep(70);
            check(profile.Provider.Manager.PositiveWrites == positiveWrites && profile.Provider.Manager.Last!.States.All(s => s.Gain == 0),
                "reconnected controller never resumes the remainder of a cancelled manual test");
            Interlocked.Exchange(ref deadline, long.MaxValue); adapter.WakeOutput();
            Volatile.Write(ref enabled, 0);
            adapter.Refresh();
            int stoppedWrites = profile.Provider.Manager.Writes;
            Thread.Sleep(70);
            check(profile.Provider.Manager.Last!.States.All(s => s.Gain == 0) && profile.Provider.Manager.Writes == stoppedWrites,
                "mode off sends zero and stops timer output");

            Volatile.Write(ref enabled, 1);
            Interlocked.Exchange(ref deadline, long.MaxValue);
            adapter.EnsureTestReady(); adapter.WakeOutput();
            device.Enabled = false;
            check(SpinWait.SpinUntil(() => profile.Provider.Manager.Last!.States.All(s => s.Gain == 0), 500),
                "device main switch stops independent output without waiting for the host callback");
            stoppedWrites = profile.Provider.Manager.Writes;
            Thread.Sleep(70);
            check(profile.Provider.Manager.Writes == stoppedWrites, "disabled device transport is never reopened by the pump");
            bool refused = false;
            try { adapter.EnsureTestReady(); } catch (InvalidOperationException) { refused = true; }
            check(refused, "manual test clearly refuses a disabled device");

            device.Enabled = true;
            Interlocked.Exchange(ref deadline, long.MaxValue);
            adapter.EnsureTestReady(); adapter.WakeOutput();
            profile.Output.ShakeItChannelsInfoProvider.Stop();
            stoppedWrites = profile.Provider.Manager.Writes;
            Thread.Sleep(70);
            check(profile.Provider.Manager.Writes == stoppedWrites && !adapter.IsActive,
                "stock Stop latches teardown and the independent timer cannot reopen transport");
            profile.Output.ShakeItChannelsInfoProvider.UpdateOutput(new Dictionary<int, ChannelValue>());
            adapter.WakeOutput();
            adapter.Dispose();
            stoppedWrites = profile.Provider.Manager.Writes;
            Thread.Sleep(70);
            check(profile.Provider.Manager.Last!.States.All(s => s.Gain == 0) && profile.Provider.Manager.Writes == stoppedWrites,
                "Dispose sends zero and queued timer callbacks never write again");
        }
    }

    private static void CheckUi(Action<bool, string> check)
    {
        var profile = new AdapterProfile();
        object? callbackGate = null;
        int callbackCount = 0;
        bool callbackHeldGate = true;
        using (var adapter = new SimHubHapticsAdapter(MakeDevice(profile.Settings), () => null, () => false, false, null,
            () => { callbackHeldGate = Monitor.IsEntered(callbackGate!); callbackCount++; }))
        {
            callbackGate = typeof(SimHubHapticsAdapter).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(adapter)!;
            var root = new Grid();
            var stock = new SimHub.Plugins.DataPlugins.ShakeItV3.UI.AdapterFixtureStockControl();
            var initiallyDisabled = new SimHub.Plugins.DataPlugins.ShakeItV3.UI.AdapterFixtureStockControl { IsEnabled = false, ToolTip = "original local hint" };
            var custom = new Button { IsEnabled = true };
            var binding = new Binding("Value") { Source = new AdapterBooleanValue { Value = true } };
            var tooltipBinding = new Binding("Caption") { Source = new AdapterBooleanValue { Caption = "original bound hint" } };
            var showDisabledBinding = new Binding("Value") { Source = new AdapterBooleanValue { Value = false } };
            BindingOperations.SetBinding(stock, UIElement.IsEnabledProperty, binding);
            BindingOperations.SetBinding(stock, FrameworkElement.ToolTipProperty, tooltipBinding);
            BindingOperations.SetBinding(stock, ToolTipService.ShowOnDisabledProperty, showDisabledBinding);
            root.Children.Add(stock); root.Children.Add(initiallyDisabled); root.Children.Add(custom);
            var disable = typeof(SimHubHapticsAdapter).GetMethod("DisableStockControls", BindingFlags.NonPublic | BindingFlags.Instance)!;
            disable.Invoke(adapter, new object[] { root, new HashSet<DependencyObject>() });
            var savedStates = (System.Collections.IDictionary)typeof(SimHubHapticsAdapter).GetField("_uiStates", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(adapter)!;
            object firstState = savedStates[stock]!;
            disable.Invoke(adapter, new object[] { root, new HashSet<DependencyObject>() });
            check(savedStates.Count == 2 && ReferenceEquals(savedStates[stock], firstState), "repeated scans attach exactly one lock overlay per stock settings root");
            check(!stock.IsEnabled && !initiallyDisabled.IsEnabled && custom.IsEnabled, "only stock ShakeIt controls become disabled");
            check(((string)stock.ToolTip).Contains("PedalFeel") && ToolTipService.GetShowOnDisabled(stock), "disabled stock controls explain how to return to SimHub");
            typeof(SimHubHapticsAdapter).GetMethod("RestoreUi", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(adapter, null);
            check(stock.IsEnabled && ReferenceEquals(BindingOperations.GetBindingBase(stock, UIElement.IsEnabledProperty), binding), "stock IsEnabled binding restored");
            check(!initiallyDisabled.IsEnabled && custom.IsEnabled, "previous disabled state and PedalFeel controls preserved");
            check((string)stock.ToolTip == "original bound hint" && ReferenceEquals(BindingOperations.GetBindingBase(stock, FrameworkElement.ToolTipProperty), tooltipBinding), "original tooltip value and binding restored");
            check(!ToolTipService.GetShowOnDisabled(stock) && ReferenceEquals(BindingOperations.GetBindingBase(stock, ToolTipService.ShowOnDisabledProperty), showDisabledBinding), "original ShowOnDisabled value and binding restored");
            check((string)initiallyDisabled.ToolTip == "original local hint" && initiallyDisabled.ReadLocalValue(ToolTipService.ShowOnDisabledProperty) == DependencyProperty.UnsetValue, "local tooltip and unset ShowOnDisabled restored");
            check(savedStates.Count == 0, "unlock releases all overlay handles with the saved control state");

            var returnToStock = typeof(SimHubHapticsAdapter).GetMethod("RequestReturnToStock", BindingFlags.NonPublic | BindingFlags.Instance)!;
            lock (callbackGate)
            {
                returnToStock.Invoke(adapter, null);
                check(callbackCount == 0, "return-to-stock action is queued instead of calling the extension while holding the output gate");
            }
            DrainDispatcher();
            check(callbackCount == 1 && !callbackHeldGate, "return-to-stock callback runs once outside the output gate");
            returnToStock.Invoke(adapter, null);
            adapter.Dispose();
            DrainDispatcher();
            check(callbackCount == 1, "a queued return-to-stock callback cannot invoke an already disposed extension");
        }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.00001;
    private static FieldInfo FindField(Type type, string name)
    {
        for (Type? t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null) return f;
        }
        throw new MissingFieldException(type.FullName, name);
    }
    private static DeviceInstance MakeDevice(AdapterFakeSettings settings)
    {
        Type open = typeof(DeviceInstance).Assembly.GetType("SimHub.Plugins.DataPlugins.ShakeItV3.Device.ShakeItV3DeviceInstance`2", true)!;
        Type closed = open.MakeGenericType(typeof(AdapterFakeOutput), typeof(AdapterFakeSettings));
        var device = (DeviceInstance)Activator.CreateInstance(closed, true)!;
        device.DeviceDescriptor = new DeviceDescriptor { DeviceTypeID = SimHubHapticsAdapter.SupportedDeviceTypeId };
        device.Enabled = true;
        SetHostedSettings(device, settings);
        return device;
    }
    private static void SetHostedSettings(DeviceInstance device, AdapterFakeSettings settings)
    {
        FieldInfo hostedField = FindField(device.GetType(), "shakeITV3PluginBase");
        object hosted = FormatterServices.GetUninitializedObject(hostedField.FieldType);
        FindField(hosted.GetType(), "settings").SetValue(hosted, settings);
        hostedField.SetValue(device, hosted);
    }
}

public sealed class AdapterProfile
{
    public AdapterFakeProvider Provider { get; } = new AdapterFakeProvider();
    public AdapterFakeOutput Output { get; } = new AdapterFakeOutput();
    public AdapterFakeSettings Settings { get; } = (AdapterFakeSettings)FormatterServices.GetUninitializedObject(typeof(AdapterFakeSettings));
    public AdapterProfile()
    {
        // Normal settings construction assumes a running SimHub PluginManager. Populate just the
        // state consumed by the adapter/provider so the test stays isolated from host initialization.
        typeof(ShakeItSettings).GetField("<SettingsStore>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Settings, new AbstractSettingsStore());
        for (Type? t = typeof(AdapterFakeSettings); t != null; t = t.BaseType)
        {
            var profileField = t.GetField("_CurrentProfile", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (profileField != null) profileField.SetValue(Settings, FormatterServices.GetUninitializedObject(typeof(ShakeItProfile)));
        }
        Settings.OutputManager = Output;
        typeof(ShakeItSettings).GetField("oldManager", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Settings, Output);
        Output.ShakeItChannelsInfoProvider = Provider;
        Provider.SetSettings(Settings);
    }
}
public sealed class AdapterFakeSettings : ShakeitSettingsMotorsWithFrequencyOutputManagerBase<AdapterFakeOutput, AdapterFakeProvider> { }
public sealed class AdapterFakeOutput : MotorsWithFrequencyOutputManagerBase
{
    protected override void UpdateOutput(bool isGameRunning, double gain, IList<EffectOutput> effects, List<IAudioRenderer> renderers) { }
}
public sealed class AdapterFakeProvider : MotorsWithFrequencyChannelsSettingsProvider<SimagicHapticSettings>
{
    public AdapterFakeManager Manager { get; } = new AdapterFakeManager();
    protected override IUSBGenericManagerSerial<MotorStates> CreateManager() => Manager;
}
public sealed class AdapterUnknownProvider : IShakeItChannelsInfoProvider
{
    public string DefaultSettingsKey => "fixture-unsupported";
    public bool IsConnected => false;
    public List<ChannelInformation> GetChannels(MotorsWithFrequencyOutputManagerBase outputManager) => new List<ChannelInformation>();
    public ChannelActivation CreateDefaultActivationFor(FFBPlacement placement, MotorsWithFrequencyOutputManagerBase outputManager) => new ChannelActivation();
    public void LoadDefaultPlatformSettings(EffectsContainerBase effects, ShakeItProfile profile) { }
    public void UpdateOutput(Dictionary<int, ChannelValue> values) => throw new InvalidOperationException("Unsupported fixture must never output");
    public void Stop() { }
    public FrequencyRange HardwareFrequencyRange() => new FrequencyRange(10, 50, false);
    public void SetSettings(ShakeItSettings settings) { }
    public IEnumerable<DeviceSettingControl> GetSettingsControls() => Array.Empty<DeviceSettingControl>();
}
public sealed class AdapterFakeManager : IUSBGenericManagerSerial<MotorStates>
{
    private MotorStates? last;
    private int writes;
    private int positiveWrites;
    private int connected = 1;
    public MotorStates? Last => Volatile.Read(ref last);
    public int Writes => Volatile.Read(ref writes);
    public int PositiveWrites => Volatile.Read(ref positiveWrites);
    public bool Connected { get => Volatile.Read(ref connected) == 1; set => Volatile.Write(ref connected, value ? 1 : 0); }
    public bool ConnectOnDisplay { get; set; }
    public Action<MotorStates>? ObserveWrite { get; set; }
    public int DisposeCount { get; private set; }
    public void Display(MotorStates value)
    {
        if (ConnectOnDisplay) Connected = true;
        Volatile.Write(ref last, value); Interlocked.Increment(ref writes);
        if (value.States.Any(s => s.Gain > 0)) Interlocked.Increment(ref positiveWrites);
        ObserveWrite?.Invoke(value);
    }
    public void DisplayExtra() { }
    public void Close() { }
    public bool IsConnected() => Connected;
    public IUSBDriver GetCurrentDriver() => null!;
    public IEnumerable<string> GetSerialNumbers() => Array.Empty<string>();
    public void SetSerialNumber(string value) { }
    public void Dispose() { DisposeCount++; }
}
public sealed class AdapterBooleanValue { public bool Value { get; set; } public string Caption { get; set; } = ""; }
namespace SimHub.Plugins.DataPlugins.ShakeItV3.UI
{
    public sealed class AdapterFixtureStockControl : UserControl { }
}
