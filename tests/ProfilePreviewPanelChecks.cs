using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PedalFeel.SimHub;

internal static class ProfilePreviewPanelChecks
{
    public static void Run(Action<bool, string> check)
    {
        var current = new PedalFeelSettings { Enabled = true, DownshiftKick = .2, LimiterStrength = .2 };
        var state = new ProfilePanelState {
            SelectedId = "balanced", AssignedId = "balanced", CurrentCar = "Ferrari 296 GT3", CanAssign = true,
            Description = "Fixture profile description",
            Profiles = new List<KeyValuePair<string, string>> { Pair("balanced", "Balanced fixture"), Pair("formula", "Formula fixture"), Pair("custom", "Газ") },
            Bases = new List<KeyValuePair<string, string>> { Pair("author-balanced", "Авторский GT3 Balanced"), Pair("formula", "Формулы · настройки 0.3.4"), Pair("current", "Копия выбранного профиля") }
        };
        int saves = 0, selects = 0, assignments = 0, clears = 0, creates = 0, previews = 0, stops = 0, liveStops = 0, resets = 0;
        string createdName = "", createdBasis = ""; bool createdAssigned = false, rejectPreview = false;
        EffectPreviewKind lastPreview = default;
        PedalFeelPanel panel = null!;
        panel = new PedalFeelPanel(current, () => saves++, () => "fixture", (_, __, ___) => { }, () => liveStops++,
            resetProfile: () => resets++,
            previewEffect: effect => { previews++; lastPreview = effect; if (rejectPreview) throw new InvalidOperationException("fixture preview failure"); },
            stopPreview: () => stops++, profileState: () => state,
            selectProfile: id => { selects++; state.SelectedId = id; current = current.Clone(); panel.Rebind(current); },
            createProfile: (name, basis, assign) => {
                creates++; createdName = name; createdBasis = basis; createdAssigned = assign;
                string id = creates == 1 ? "created" : "created" + creates;
                state.Profiles.Add(Pair(id, name)); state.SelectedId = id;
                if (assign) state.AssignedId = id;
                current = current.Clone(); panel.Rebind(current);
            },
            assignProfile: () => { assignments++; state.AssignedId = state.SelectedId; },
            clearAssignment: () => { clears++; state.AssignedId = ""; });
        using (var source = new HwndSource(new HwndSourceParameters("PedalFeel profile and preview checks") {
            Width = 1000, Height = 800, PositionX = -10000, PositionY = -10000, WindowStyle = unchecked((int)0x80000000)
        }))
        {
            var host = new Border { Child = panel }; source.RootVisual = host; Layout(host); Pump(25);
            check(saves == 0 && selects == 0 && creates == 0 && assignments == 0 && previews == 0,
                "opening named-profile UI does not apply, assign, save, create or preview anything");
            check(!Tree(panel).OfType<FrameworkElement>().Any(e => e.Name == "PresetChoice") &&
                (string)Find<ComboBox>(panel, "NewProfileBasis").SelectedValue == "author-balanced",
                "named profiles replace inferred family presets and new profiles default to the exact author basis");
            Find<ComboBox>(panel, "ProfileChoice").SelectedValue = "formula";
            check(selects == 1 && state.SelectedId == "formula" && state.AssignedId == "balanced" && assignments == 0 && saves == 0 && previews == 0,
                "selecting a profile applies it immediately without assigning it to the current car");
            Click(Find<Button>(panel, "AssignProfile"));
            check(assignments == 1 && state.AssignedId == "formula", "car assignment requires its own explicit action");
            Click(Find<Button>(panel, "ClearAssignment"));
            check(clears == 1 && state.AssignedId == "" && state.SelectedId == "formula",
                "clearing a car assignment leaves the selected editing profile intact");
            Click(Find<Button>(panel, "ResetProfile"));
            check(resets == 1 && state.SelectedId == "formula" && state.AssignedId == "" && previews == 0,
                "restoring a named profile's basis is explicit and does not assign it or preview an effect");

            foreach (EffectPreviewKind effect in Enum.GetValues(typeof(EffectPreviewKind)))
            {
                int previous = previews;
                Click(Find<Button>(panel, "Preview" + effect));
                check(previews == previous + 1 && lastPreview == effect && saves == 0,
                    "the effect's explicit preview button forwards only its own kind: " + effect);
            }
            int beforePassive = previews;
            Find<Slider>(panel, "LimiterStrength").Value = 0;
            var zeroPreview = Find<Button>(panel, "PreviewLimiter");
            check(!zeroPreview.IsEnabled && Find<Button>(panel, "PreviewEngine").IsEnabled &&
                !string.IsNullOrWhiteSpace(Find<TextBlock>(panel, "PreviewMessageLimiter").Text),
                "a zero-strength effect is explained locally and cannot be previewed while other effects remain available");
            ForceClick(zeroPreview);
            check(previews == beforePassive, "a queued click on a zero-strength preview cannot reach the motor callback");
            Find<Slider>(panel, "LimiterStrength").Value = .2;
            Find<Slider>(panel, "Strength").Value = 0;
            check(!Find<Button>(panel, "PreviewThreshold").IsEnabled && !Find<Button>(panel, "PreviewAbs").IsEnabled &&
                !Find<Button>(panel, "PreviewDownshift").IsEnabled && Find<Button>(panel, "PreviewEngine").IsEnabled,
                "brake preview availability respects the common brake strength independently of throttle effects");
            Find<Slider>(panel, "Strength").Value = .7;
            Find<Slider>(panel, "EffectsGain").Value = 0;
            check(Enum.GetValues(typeof(EffectPreviewKind)).Cast<EffectPreviewKind>().All(e => !Find<Button>(panel, "Preview" + e).IsEnabled),
                "zero overall profile strength disables every effect preview");
            Find<Slider>(panel, "EffectsGain").Value = 1;
            Click(Find<CheckBox>(panel, "BrakeEnabled"));
            check(!Find<Button>(panel, "PreviewAbs").IsEnabled && Find<Button>(panel, "PreviewEngine").IsEnabled,
                "pedal disable blocks only previews that need that pedal");
            Click(Find<CheckBox>(panel, "BrakeEnabled"));
            Click(Find<CheckBox>(panel, "AutoEnable"));
            check(Enum.GetValues(typeof(EffectPreviewKind)).Cast<EffectPreviewKind>().All(e => !Find<Button>(panel, "Preview" + e).IsEnabled),
                "automatic mode off disables all previews");
            ForceClick(Find<Button>(panel, "PreviewEngine"));
            Click(Find<CheckBox>(panel, "AutoEnable"));
            check(previews == beforePassive, "editing settings and enabling controls never starts an effect preview");

            rejectPreview = true; Click(Find<Button>(panel, "PreviewUpshift")); Pump(550);
            check(Find<TextBlock>(panel, "PreviewMessageUpshift").Text.Contains("fixture preview failure") &&
                Find<TextBlock>(panel, "PreviewMessageUpshift").Visibility == Visibility.Visible,
                "preview transport errors remain beside the affected effect after status refresh");
            Click(Find<Button>(panel, "StopPreview"));
            check(stops == 1 && liveStops == 0 && current.Enabled, "stop preview cancels only the example and leaves live automatic mode enabled");

            Find<Expander>(panel, "CreateProfileExpander").IsExpanded = true;
            Click(Find<Button>(panel, "CreateProfile"));
            check(creates == 0 && !string.IsNullOrWhiteSpace(Find<TextBlock>(panel, "ActionError").Text),
                "an empty profile name is explained without creating a profile");
            Find<TextBox>(panel, "NewProfileName").Text = "  Мой профиль  ";
            Find<ComboBox>(panel, "NewProfileBasis").SelectedValue = "current";
            Click(Find<CheckBox>(panel, "AssignNewProfile"));
            int beforeCreatePreviews = previews;
            check(creates == 0 && assignments == 1, "editing a profile name, basis and assignment choice does not create or assign anything");
            Click(Find<Button>(panel, "CreateProfile"));
            check(creates == 1 && createdName == "Мой профиль" && createdBasis == "current" && createdAssigned &&
                state.SelectedId == "created" && state.AssignedId == "created" && previews == beforeCreatePreviews,
                "create sends the trimmed name, stable basis and explicit assignment choice without previewing effects");

            Find<TextBox>(panel, "NewProfileName").Text = "Черновик";
            var oldName = Find<TextBox>(panel, "NewProfileName");
            var oldChoice = Find<ComboBox>(panel, "ProfileChoice");
            var oldAssign = Find<Button>(panel, "AssignProfile");
            var oldCreate = Find<Button>(panel, "CreateProfile");
            var oldPreview = Find<Button>(panel, "PreviewEngine");
            var oldStop = Find<Button>(panel, "StopPreview");
            var oldReset = Find<Button>(panel, "ResetProfile");
            int beforeRebindSaves = saves, beforeRebindPreviews = previews, beforeRebindSelects = selects, beforeRebindStops = stops;
            state.CurrentCar = "BMW M4 GT3"; state.AssignedId = "balanced"; current = current.Clone(); panel.Rebind(current); Layout(host);
            oldName.Text = "Discarded old edit"; oldChoice.SelectedValue = "balanced";
            ForceClick(oldAssign); ForceClick(oldCreate); ForceClick(oldPreview); ForceClick(oldStop); ForceClick(oldReset);
            check(saves == beforeRebindSaves && previews == beforeRebindPreviews && selects == beforeRebindSelects &&
                assignments == 1 && creates == 1 && resets == 1 && stops == beforeRebindStops && Find<TextBox>(panel, "NewProfileName").Text == "Черновик",
                "old profile and preview controls cannot act on a different car after rebind");
            check(Find<Expander>(panel, "CreateProfileExpander").IsExpanded &&
                (string)Find<ComboBox>(panel, "NewProfileBasis").SelectedValue == "current" && Find<CheckBox>(panel, "AssignNewProfile").IsChecked == true,
                "profile creation drafts and disclosure survive rebind without creating or assigning");

            oldPreview = Find<Button>(panel, "PreviewEngine"); oldCreate = Find<Button>(panel, "CreateProfile");
            string? previousCulture = L10n.OverrideCulture;
            try {
                L10n.OverrideCulture = "de-DE"; Pump(600); Layout(host);
                check(Find<TextBox>(panel, "NewProfileName").Text == "Черновик" &&
                    (string)Find<ComboBox>(panel, "NewProfileBasis").SelectedValue == "current" &&
                    ((IEnumerable<KeyValuePair<string, string>>)Find<ComboBox>(panel, "ProfileChoice").ItemsSource).Single(p => p.Key == "custom").Value == "Газ",
                    "language changes preserve creation drafts, stable IDs and literal user profile names");
                ForceClick(oldPreview); ForceClick(oldCreate);
                check(previews == beforeRebindPreviews && creates == 1 && saves == beforeRebindSaves,
                    "controls replaced by a language change cannot create profiles or start examples");
            }
            finally { L10n.OverrideCulture = previousCulture; }
            state.CanAssign = false; state.CurrentCar = ""; state.AssignedId = ""; Pump(600);
            check(!Find<CheckBox>(panel, "AssignNewProfile").IsEnabled && Find<CheckBox>(panel, "AssignNewProfile").IsChecked == false,
                "losing the current car clears the unavailable new-profile assignment choice");
            Find<TextBox>(panel, "NewProfileName").Text = "Offline profile";
            Click(Find<Button>(panel, "CreateProfile"));
            check(creates == 2 && !createdAssigned && state.AssignedId == "",
                "a profile can still be created without assignment after the current car disappears");
            int beforeUnload = stops;
            panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            check(stops == beforeUnload + 1 && liveStops == 0 && current.Enabled,
                "closing the pane cancels its preview without stopping live PedalFeel");
            source.RootVisual = null;
        }
    }
    private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);
    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement => Tree(root).OfType<T>().Single(e => e.Name == name);
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>(); var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.Count > 0) {
            var next = pending.Pop(); if (!seen.Add(next)) continue; yield return next;
            foreach (var child in LogicalTreeHelper.GetChildren(next).OfType<DependencyObject>()) pending.Push(child);
            if (next is Visual) for (int index = 0; index < VisualTreeHelper.GetChildrenCount(next); index++) pending.Push(VisualTreeHelper.GetChild(next, index));
        }
    }
    private static void Click(ButtonBase button)
    {
        if (!button.IsEnabled) throw new InvalidOperationException("Cannot click disabled control: " + button.Name);
        button.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
    }
    private static void ForceClick(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Layout(FrameworkElement root) { root.Measure(new Size(1000, 800)); root.Arrange(new Rect(0, 0, 1000, 800)); root.UpdateLayout(); }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, __) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
