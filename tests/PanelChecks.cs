using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PedalFeel.SimHub;

/// <summary>Exercises the real settings controls with an in-memory engine; never opens a device.</summary>
public static class PanelChecks
{
    public static void Run(Action<bool, string> check)
    {
        var settings = new PedalFeelSettings();
        PedalFeelSettings current = settings, saved = settings.Clone();
        string car = "Ferrari 296 GT3";
        int changes = 0, stops = 0, tests = 0, resets = 0, presetApplications = 0;
        int statusReads = 0;
        string appliedCar = "", appliedPreset = "";
        string outputSummary = "Output fixture: brake is quiet";
        bool rejectTest = false;
        long now = 0;
        int actualChannel = -1, actualHz = -1, actualPower = -1;
        using (var controller = new PedalController(() => new PanelFakeEngine(), () => now))
        {
            controller.Configure(current);
            controller.SetContext("IRacing", false, false);
            var panel = new PedalFeelPanel(current,
                () => { changes++; saved = current.Clone(); controller.Configure(current); },
                () => controller.Status,
                (channel, hz, power) => {
                    tests++; actualChannel = channel; actualHz = hz; actualPower = power;
                    if (rejectTest) throw new InvalidOperationException("fixture transport failure");
                    controller.RequestTest(channel, hz, power);
                },
                () => { stops++; current.Enabled = false; controller.Configure(current); },
                () => car, () => resets++, () => "fixture preset description",
                new[] { new KeyValuePair<string, string>("auto", "Automatic"), new KeyValuePair<string, string>("gt3", "GT3"), new KeyValuePair<string, string>("formula", "Formula") },
                id => { presetApplications++; appliedCar = car; appliedPreset = id; },
                presentationStatus: () => {
                    statusReads++;
                    return new PanelStatus { Title = "Waiting fixture", Detail = "Status detail fixture", Diagnostic = controller.Status,
                        OutputSummary = outputSummary, Tone = StatusTone.Ready };
                });

            using (var source = new HwndSource(new HwndSourceParameters("PedalFeel invisible interaction checks")
            {
                Width = 1000, Height = 760, PositionX = -10000, PositionY = -10000,
                WindowStyle = unchecked((int)0x80000000) // No WS_VISIBLE: no user-facing window.
            }))
            {
                var host = new Border { Child = panel };
                source.RootVisual = host;
                Layout(host); Pump(25);
                check(changes == 0 && tests == 0 && presetApplications == 0, "opening settings does not save, apply a preset or request vibration");

                Click(Find<ToggleButton>(panel, "AutoEnable"));
                check(settings.Enabled && saved.Enabled && !controller.Active, "automatic enable persists while waiting for iRacing without starting output");
                Click(Find<Button>(panel, "Stop"));
                check(stops == 1 && !settings.Enabled, "stop control returns through the explicit stop callback");
                Click(Find<ToggleButton>(panel, "AutoEnable"));

                var effectsGain = Find<Slider>(panel, "ThrottleStrength");
                int beforeGain = changes;
                check(Near(effectsGain.Value, 1) && Near(settings.EffectsGain, 2.1),
                    "the new 1x baseline retains the former 0.7x strength internally");
                effectsGain.Value = .5;
                check(Near(effectsGain.Minimum, 0) && Near(effectsGain.Maximum, 1) && Near(effectsGain.TickFrequency, .01) &&
                    Near(settings.ThrottleStrength, .5) && Near(saved.ThrottleStrength, .5) && changes == beforeGain + 1 && tests == 0 && !controller.Active,
                    "displayed 0.5x saves internal gain 1.05 immediately without starting vibration");

                var limiter = Find<Slider>(panel, "LimiterStrength");
                var downshift = Find<Slider>(panel, "DownshiftKick");
                check(Near(limiter.Value, .2) && Near(downshift.Value, .2) &&
                    Near(limiter.Minimum, 0) && Near(limiter.Maximum, 1) &&
                    Near(downshift.Minimum, 0) && Near(downshift.Maximum, 1) &&
                    Near(Find<Slider>(panel, "ShiftKick").Maximum, 1),
                    "limiter and downshift start at 20 percent while all shift controls retain personal adjustment up to 100 percent");

                var sliderValues = new Dictionary<string, double> {
                    ["Strength"] = .31, ["GripThreshold"] = 1.03, ["Texture"] = .42,
                    ["AbsPunch"] = .53, ["TractionStrength"] = .64, ["EngineTexture"] = .26,
                    ["IdleTexture"] = .37, ["ShiftKick"] = .48, ["SurfaceStrength"] = .59,
                    ["LimiterStrength"] = .67, ["DownshiftKick"] = .56
                };
                int beforeSliders = changes;
                foreach (var pair in sliderValues) Find<Slider>(panel, pair.Key).Value = pair.Value;
                check(changes >= beforeSliders + sliderValues.Count && sliderValues.All(pair =>
                    Near((double)typeof(PedalFeelSettings).GetProperty(pair.Key)!.GetValue(saved)!, pair.Value)),
                    "all eleven effect controls save their independent tuning values");
                limiter.Value = 0; downshift.Value = 0;
                check(Near(saved.LimiterStrength, 0) && Near(saved.DownshiftKick, 0) && Near(saved.EngineTexture, .26) &&
                    Near(saved.ShiftKick, .48) && tests == 0 && !controller.Active,
                    "limiter and downshift can be disabled independently without altering engine or upshift effects or starting a motor");
                limiter.Value = .67; downshift.Value = .56;
                var threshold = Find<Slider>(panel, "GripThreshold");
                check(Near(threshold.Minimum, .75) && Near(threshold.Maximum, 1.05), "grip threshold preserves its physical .75–1.05 range");
                threshold.Value = .75; check(Near(saved.GripThreshold, .75), "minimum grip threshold survives persistence");
                threshold.Value = 1.05; check(Near(saved.GripThreshold, 1.05), "maximum grip threshold survives persistence");

                var navigation = Find<TabControl>(panel, "MainSections"); navigation.SelectedIndex = 1;
                Layout(host);
                var brakeChannel = Find<ComboBox>(panel, "BrakeChannel");
                var throttleChannel = Find<ComboBox>(panel, "ThrottleChannel");
                int beforeDuplicate = changes;
                brakeChannel.SelectedIndex = settings.ThrottleChannel;
                check(settings.BrakeChannel == 1 && brakeChannel.SelectedIndex == 1 && changes == beforeDuplicate,
                    "duplicate pedal channels are refused without saving or silently reassigning a pedal");
                brakeChannel.SelectedIndex = 0; throttleChannel.SelectedIndex = 1;
                check(saved.BrakeChannel == 0 && saved.ThrottleChannel == 1, "distinct pedal channels persist independently");

                var testPedal = Find<ComboBox>(panel, "TestPedal");
                Click(Find<ToggleButton>(panel, "BrakeEnabled"));
                check(!saved.BrakeEnabled && saved.ThrottleEnabled, "disabling brake leaves throttle enabled");
                Click(Find<ToggleButton>(panel, "ThrottleEnabled"));
                check(!saved.BrakeEnabled && !saved.ThrottleEnabled, "each pedal can be disabled and persisted independently");
                var disabledPedalTest = Find<Button>(panel, "BrakeMaximumTest0");
                if (disabledPedalTest.IsEnabled) Click(disabledPedalTest);
                check(tests == 0 && !controller.Active, "a disabled pedal cannot start a row test");
                Click(Find<ToggleButton>(panel, "BrakeEnabled")); Click(Find<ToggleButton>(panel, "ThrottleEnabled"));

                int beforeCalibration = changes;
                for (int pedal = 0; pedal < 2; pedal++)
                {
                    testPedal.SelectedIndex = pedal;
                    string prefix = pedal == 0 ? "Brake" : "Throttle";
                    for (int point = 0; point < 4; point++)
                    {
                        var low = Find<Slider>(panel, prefix + "Minimum" + point);
                        var high = Find<Slider>(panel, prefix + "Maximum" + point);
                        double lowPower = 10 + pedal * 10 + point, highPower = 50 + pedal * 10 + point;
                        var expected = current.Clone();
                        (pedal == 0 ? expected.BrakeMinimum : expected.ThrottleMinimum)[point] = lowPower;
                        low.Value = lowPower;
                        check(SameCalibration(saved, expected) && tests == 0 && !controller.Active,
                            "minimum slider immediately saves only its own point without output: " + prefix + point);
                        (pedal == 0 ? expected.BrakeMaximum : expected.ThrottleMaximum)[point] = highPower;
                        high.Value = highPower;
                        check(SameCalibration(saved, expected) && tests == 0 && !controller.Active,
                            "maximum slider immediately saves only its own point without output: " + prefix + point);
                        check(Near(low.Minimum, 0) && Near(low.Maximum, 100) && Near(high.Minimum, 0) && Near(high.Maximum, 100),
                            "calibration sliders retain the full 0–100 percent scale: " + prefix + point);

                        low.Value = highPower + 5;
                        check(Near(low.Value, highPower) && Near(high.Value, highPower) &&
                            Near((pedal == 0 ? saved.BrakeMinimum : saved.ThrottleMinimum)[point], highPower) &&
                            Near((pedal == 0 ? saved.BrakeMaximum : saved.ThrottleMaximum)[point], highPower),
                            "minimum clamps at the maximum without moving the other control: " + prefix + point);
                        low.Value = lowPower;
                        high.Value = lowPower - 5;
                        check(Near(high.Value, lowPower) && Near(low.Value, lowPower) &&
                            Near((pedal == 0 ? saved.BrakeMaximum : saved.ThrottleMaximum)[point], lowPower) &&
                            Near((pedal == 0 ? saved.BrakeMinimum : saved.ThrottleMinimum)[point], lowPower),
                            "maximum clamps at the minimum without moving the other control: " + prefix + point);
                        high.Value = highPower;
                    }
                }
                check(changes > beforeCalibration && tests == 0 && !controller.Active,
                    "editing and constraining all sixteen calibration values never starts a motor");

                testPedal.SelectedIndex = 0;
                Find<Slider>(panel, "BrakeMinimum0").Value = 0;
                Click(Find<Button>(panel, "BrakeMinimumTest0"));
                check(tests == 0 && !string.IsNullOrWhiteSpace(Find<TextBlock>(panel, "TestError").Text),
                    "zero-power row test gives local feedback without requesting a motor pulse");
                Find<Slider>(panel, "BrakeMinimum0").Value = 10;

                int[] frequencies = { 16, 25, 35, 50 };
                for (int pedal = 0; pedal < 2; pedal++)
                {
                    int selectionChanges = changes, selectionTests = tests;
                    testPedal.SelectedIndex = pedal;
                    check(changes == selectionChanges && tests == selectionTests && !controller.Active,
                        "changing the visible pedal table does not save or start a test: " + pedal);
                    string prefix = pedal == 0 ? "Brake" : "Throttle";
                    for (int point = 0; point < 4; point++)
                    {
                        foreach (string endpoint in new[] { "Minimum", "Maximum" })
                        {
                            string label = prefix + endpoint + point;
                            int power = (int)Find<Slider>(panel, label).Value;
                            int expectedChannel = pedal == 0 ? current.BrakeChannel : current.ThrottleChannel;
                            int previousTests = tests, previousChanges = changes;
                            long started = now += 1000;
                            Click(Find<Button>(panel, prefix + endpoint + "Test" + point));
                            check(tests == previousTests + 1 && changes == previousChanges &&
                                actualChannel == expectedChannel && actualHz == frequencies[point] && actualPower == power,
                                "row click sends the exact saved power, channel and frequency without another save: " + label);
                            now = started + 499;
                            var frame = controller.Produce();
                            check(frame != null &&
                                (pedal == 0 ? frame.BrakeIntensity : frame.ThrottleIntensity) == power &&
                                (pedal == 0 ? frame.BrakeFrequency : frame.ThrottleFrequency) == frequencies[point] &&
                                (pedal == 0 ? frame.ThrottleIntensity : frame.BrakeIntensity) == 0,
                                "the selected row drives only its pedal through 499 ms without telemetry: " + label);
                            now = started + 500;
                            check(!controller.Active && controller.Produce() == null,
                                "the row test releases output at exactly 500 ms: " + label);
                        }
                    }
                }

                rejectTest = true;
                Click(Find<Button>(panel, "ThrottleMaximumTest3"));
                int beforeRefreshChanges = changes, beforeRefreshTests = tests, beforeStatusReads = statusReads;
                outputSummary = "Output fixture: throttle test stopped";
                Pump(600); // Let the real status timer refresh; it must not erase the test error.
                check(statusReads > beforeStatusReads && changes == beforeRefreshChanges && tests == beforeRefreshTests,
                    "structured status refresh reads presentation without saving settings or requesting output");
                check(Find<TextBlock>(panel, "OutputSummary").Text == outputSummary &&
                    Find<TextBlock>(panel, "OutputSummary").Visibility == Visibility.Visible,
                    "the live output summary refreshes visibly without requiring diagnostic details");
                check(Find<TextBlock>(panel, "TestError").Visibility == Visibility.Visible &&
                    Find<TextBlock>(panel, "TestError").Text.Contains("fixture transport failure"),
                    "last row-test failure remains visible after a live status refresh");

                Find<Expander>(panel, "PresetsExpander").IsExpanded = true;
                Find<Expander>(panel, "DiagnosticsExpander").IsExpanded = true;
                Find<ComboBox>(panel, "PresetChoice").SelectedValue = "gt3";
                check(presetApplications == 0, "selecting a starting preset does not apply it without a click");

                int beforeRebind = changes, testsBeforeRebind = tests;
                var staleSlider = Find<Slider>(panel, "BrakeMinimum0");
                var staleGain = Find<Slider>(panel, "ThrottleStrength");
                var staleLimiter = Find<Slider>(panel, "LimiterStrength");
                var staleDownshift = Find<Slider>(panel, "DownshiftKick");
                var staleRowTest = Find<Button>(panel, "BrakeMinimumTest0");
                car = "BMW M4 GT3 EVO";
                current = settings.Clone(); current.Strength = .83; current.BrakeMinimum[0] = 14; current.ThrottleStrength = .8;
                current.LimiterStrength = .13; current.DownshiftKick = .17;
                controller.Configure(current); panel.Rebind(current); Layout(host); Pump(25);
                staleSlider.Value = 16; staleGain.Value = 1.9; staleLimiter.Value = .81; staleDownshift.Value = .82; Click(staleRowTest);
                check(Near(current.BrakeMinimum[0], 14) && Near(settings.BrakeMinimum[0], 10) &&
                    Near(current.ThrottleStrength, .8) && Near(settings.ThrottleStrength, .5) &&
                    Near(current.LimiterStrength, .13) && Near(current.DownshiftKick, .17) &&
                    Near(settings.LimiterStrength, .67) && Near(settings.DownshiftKick, .56) &&
                    changes == beforeRebind && tests == testsBeforeRebind,
                    "events from old calibration/gain sliders and row buttons cannot modify or actuate rebound settings");
                check(Near(Find<Slider>(panel, "BrakeMinimum0").Value, 14) &&
                    Near(Find<Slider>(panel, "ThrottleMaximum3").Value, 63) && Near(Find<Slider>(panel, "ThrottleStrength").Value, .8) &&
                    changes == beforeRebind && presetApplications == 0,
                    "rebind shows the current saved calibration immediately without pending drafts or implicit saves");
                check(Find<TabControl>(panel, "MainSections").SelectedIndex == 1 &&
                    Find<ComboBox>(panel, "TestPedal").SelectedIndex == 1 &&
                    Find<Expander>(panel, "PresetsExpander").IsExpanded &&
                    Find<Expander>(panel, "DiagnosticsExpander").IsExpanded,
                    "car changes preserve navigation, selected pedal and open disclosures");
                check(Find<TextBlock>(panel, "TestError").Text.Contains("fixture transport failure"),
                    "car rebind preserves the last explicit row-test error");
                Find<Slider>(panel, "Strength").Value = .84;
                Find<Slider>(panel, "BrakeMinimum0").Value = 15;
                Find<Slider>(panel, "LimiterStrength").Value = .46;
                Find<Slider>(panel, "DownshiftKick").Value = .47;
                check(Near(current.Strength, .84) && Near(saved.BrakeMinimum[0], 15) &&
                    Near(saved.LimiterStrength, .46) && Near(saved.DownshiftKick, .47) &&
                    Near(settings.Strength, .31) && Near(settings.BrakeMinimum[0], 10) &&
                    Near(settings.LimiterStrength, .67) && Near(settings.DownshiftKick, .56),
                    "post-rebind effect and calibration edits persist to the new settings without mutating the old object");
                Find<ComboBox>(panel, "PresetChoice").SelectedValue = "formula";
                Click(Find<Button>(panel, "ApplyPreset"));
                check(presetApplications == 1 && appliedPreset == "formula" && appliedCar == car,
                    "only a new apply click sends the chosen preset for the currently displayed car");
                Click(Find<Button>(panel, "ResetPreset"));
                check(resets == 1, "reset action remains wired to the profile reset callback");

                int changesBeforeLanguage = changes, testsBeforeLanguage = tests;
                var calibrationBeforeLanguage = current.Clone();
                var staleLanguageSlider = Find<Slider>(panel, "BrakeMinimum0");
                var staleLanguageGain = Find<Slider>(panel, "ThrottleStrength");
                var staleLanguageLimiter = Find<Slider>(panel, "LimiterStrength");
                var staleLanguageDownshift = Find<Slider>(panel, "DownshiftKick");
                var staleLanguageRow = Find<Button>(panel, "BrakeMinimumTest0");
                L10n.OverrideCulture = "de-DE"; Pump(650); Layout(host);
                check(((TextBlock)Find<CheckBox>(panel, "AutoEnable").Content).Text == L10n.T("Автоматически включать PedalFeel в iRacing") &&
                    Find<TextBlock>(panel, "NavigationPedalsTitle").Text == L10n.T("Настройка педалей"),
                    "language changes refresh visible labels without recreating the device");
                check(Find<TabControl>(panel, "MainSections").SelectedIndex == 1 &&
                    Find<ComboBox>(panel, "TestPedal").SelectedIndex == 1 &&
                    Near(Find<Slider>(panel, "BrakeMinimum0").Value, 15) &&
                    Near(Find<Slider>(panel, "ThrottleStrength").Value, .8) &&
                    Near(Find<Slider>(panel, "LimiterStrength").Value, .46) && Near(Find<Slider>(panel, "DownshiftKick").Value, .47) &&
                    SameCalibration(current, calibrationBeforeLanguage) &&
                    changes == changesBeforeLanguage && tests == testsBeforeLanguage,
                    "language changes retain navigation, selected pedal and saved calibration without output or extra saves");
                staleLanguageSlider.Value = 17; staleLanguageGain.Value = 1.5; staleLanguageLimiter.Value = .82; staleLanguageDownshift.Value = .83; Click(staleLanguageRow);
                check(changes == changesBeforeLanguage && tests == testsBeforeLanguage && Near(current.BrakeMinimum[0], 15) && Near(current.ThrottleStrength, .8) &&
                    Near(current.LimiterStrength, .46) && Near(current.DownshiftKick, .47),
                    "events from controls replaced by a language change cannot alter calibration or request output");
                L10n.OverrideCulture = "ru-RU"; Pump(650);
                panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                source.RootVisual = null;
            }
        }
        ProfilePreviewPanelChecks.Run(check);
    }

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement
    {
        return Tree(root, new HashSet<DependencyObject>()).OfType<T>().SingleOrDefault(e =>
            e.Name == id || AutomationProperties.GetAutomationId(e) == id)
            ?? throw new InvalidOperationException("Panel control not found: " + id);
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject item, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(item)) yield break;
        yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
            foreach (var node in Tree(child, seen)) yield return node;
        if (item is Visual)
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
                foreach (var node in Tree(VisualTreeHelper.GetChild(item, i), seen)) yield return node;
    }
    private static void Click(ButtonBase button)
    {
        if (!button.IsEnabled) throw new InvalidOperationException("Cannot click disabled control: " + button.Name);
        // Invoke the actual WPF virtual click handler, including ToggleButton's state transition.
        button.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
    }
    private static bool SameCalibration(PedalFeelSettings a, PedalFeelSettings b) =>
        a.BrakeMinimum.SequenceEqual(b.BrakeMinimum) && a.BrakeMaximum.SequenceEqual(b.BrakeMaximum) &&
        a.ThrottleMinimum.SequenceEqual(b.ThrottleMinimum) && a.ThrottleMaximum.SequenceEqual(b.ThrottleMaximum);
    private static bool Near(double a, double b) => Math.Abs(a - b) < .000001;
    private static void Layout(FrameworkElement root) { root.Measure(new Size(1000, 760)); root.Arrange(new Rect(0, 0, 1000, 760)); root.UpdateLayout(); }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, __) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private sealed class PanelFakeEngine : IEffectEngine
    {
        public void Configure(PedalFeelSettings settings) { }
        public NativeOutput Tick() => default;
        public void Dispose() { }
    }
}
