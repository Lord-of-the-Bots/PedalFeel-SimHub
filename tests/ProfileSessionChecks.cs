using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using GameReaderCommon;
using PedalFeel.SimHub;
using SimHub.Plugins.Devices;

internal static class ProfileSessionChecks
{
    public static void Run(Action<bool, string> check)
    {
        CheckSessionTransitions(check);
        CheckLocalizedSnapshots(check);
        CheckOfflinePreviewStop(check);
    }

    private static void CheckSessionTransitions(Action<bool, string> check)
    {
        using (var f = new Fixture()) {
            f.Update(Data("IRacing", true, "session-car", "Session car"));
            string assigned = f.Profiles.CreateProfile("Assigned session profile", "formula", true);
            var assignedSettings = f.Profiles.Current();
            assignedSettings.Strength = .37; assignedSettings.EngineTexture = .19;
            f.Profiles.CommitProfile(assigned, assignedSettings);
            f.Controller.Configure(f.Profiles.Current());
            f.DisplayCurrent();
            check(f.State().CanAssign && f.State().AssignedId == assigned,
                "the real extension enables assignment only after current iRacing car telemetry arrives");

            f.Select("author-subtle");
            f.Update(Data("IRacing", true, "session-car", "Session car"));
            check(f.Profiles.CurrentProfileId == "author-subtle" && f.State().AssignedId == assigned &&
                Near(f.LastEngine.Configured!.Strength, CarPresets.CreateBasis("author-subtle").Strength),
                "same-session telemetry preserves a temporary manual profile while retaining the car assignment");

            var dropped = Data("IRacing", true);
            dropped.NewData = null!;
            f.Update(dropped);
            check(f.Profiles.CurrentKey == "iracing|id:session-car" && f.Profiles.CurrentProfileId == "author-subtle" &&
                !f.State().CanAssign && Rejects(() => f.Call("AssignProfile")),
                "a dropped identity frame keeps current tuning but disables assignment to a stale car identity");
            f.Update(Data("IRacing", true, "session-car", "Session car"));
            check(f.Profiles.CurrentProfileId == "author-subtle" && f.State().CanAssign && f.State().AssignedId == assigned,
                "returning identity within the same session restores assignment controls without undoing the temporary profile");

            f.Update(Data("IRacing", false, "session-car", "Session car"));
            check(!f.Controller.Active && !f.State().CanAssign && f.Profiles.CurrentProfileId == "author-subtle",
                "closing iRacing disables assignment and preserves the visible temporary profile until the next car is known");
            f.Update(Data("IRacing", true));
            check(f.Profiles.CurrentProfileId == "author-subtle" && !f.State().CanAssign,
                "a new session with no car identity defers profile restoration instead of assigning to the previous car");
            f.Update(Data("IRacing", true, "session-car", "Session car"));
            f.DisplayCurrent();
            check(f.Profiles.CurrentProfileId == assigned && f.State().AssignedId == assigned && f.State().CanAssign &&
                Near(f.LastEngine.Configured!.Strength, .37) && Near(f.LastEngine.Configured.EngineTexture, .19),
                "restarting iRacing in the same car reloads its assigned profile and configures the engine with its saved tuning");

            f.Update(Data("AssettoCorsa", true, "session-car", "Session car"));
            check(!f.Controller.Active && !f.State().CanAssign && Rejects(() => f.Call("AssignProfile")) &&
                Rejects(() => f.Call("ClearAssignment")) && f.Profiles.PanelState().AssignedId == assigned,
                "another game cannot assign or clear an iRacing profile using the last known car");
            int profileCount = f.Profiles.PanelState().Profiles.Count;
            f.Update(Data("IRacing", true));
            check(!f.State().CanAssign && Rejects(() => f.Call("CreateProfile", "Invalid assignment", "formula", true)) &&
                f.Profiles.PanelState().Profiles.Count == profileCount,
                "unknown current car rejects create-and-assign without leaving a new profile behind");

            f.Update(Data("IRacing", true, "session-car", "Session car", replay: true));
            f.DisplayCurrent();
            check(!f.State().CanAssign && Rejects(() => f.Call("AssignProfile")),
                "replay telemetry does not authorize assigning the displayed car");
            f.Update(Data("IRacing", true, "session-car", "Session car", spectating: true));
            check(!f.State().CanAssign && Rejects(() => f.Call("AssignProfile")),
                "spectating another car does not authorize an assignment");
            f.Update(Data("IRacing", true, "session-car", "Session car"));
            check(f.State().CanAssign && f.State().AssignedId == assigned,
                "normal driving restores assignment controls after replay or spectating");
        }
    }

    private static void CheckLocalizedSnapshots(Action<bool, string> check)
    {
        string? priorCulture = L10n.OverrideCulture;
        try {
            L10n.OverrideCulture = "ru-RU";
            using (var f = new Fixture()) {
                f.DisplayCurrent();
                var russian = f.State();
                string russianName = russian.Profiles.Single(p => p.Key == CarPresets.AuthorBalanced).Value;
                double displayedStrength = f.DisplayedSettings.Strength;
                L10n.OverrideCulture = "en";
                var english = f.State();
                check(english.SelectedId == russian.SelectedId &&
                    english.Profiles.Single(p => p.Key == CarPresets.AuthorBalanced).Value == L10n.T(CarPresets.BasisLabel(CarPresets.AuthorBalanced)) &&
                    english.Profiles.Single(p => p.Key == CarPresets.AuthorBalanced).Value != russianName &&
                    Near(f.DisplayedSettings.Strength, displayedStrength),
                    "GetProfileState refreshes translated seed names after locale changes without replacing displayed tuning");

                // Simulate repository state changing before the queued WPF rebind executes.
                f.Profiles.SelectProfile("formula");
                L10n.OverrideCulture = "de-DE";
                var staleProfile = f.State();
                check(ReferenceEquals(staleProfile, english) && staleProfile.SelectedId == CarPresets.AuthorBalanced &&
                    Near(f.DisplayedSettings.Strength, CarPresets.CreateBasis(CarPresets.AuthorBalanced).Strength) &&
                    !Near(f.DisplayedSettings.Strength, f.Profiles.Current().Strength),
                    "a locale refresh cannot pair a newly selected profile name with controls still showing the previous profile");
                f.DisplayCurrent();
                var rebound = f.State();
                check(rebound.SelectedId == "formula" &&
                    rebound.Profiles.Single(p => p.Key == "formula").Value == L10n.T(CarPresets.BasisLabel("formula")) &&
                    Near(f.DisplayedSettings.Strength, f.Profiles.Current().Strength),
                    "after the panel rebind, translated names and tuning come from the same selected profile");

                f.Profiles.SelectProfile(CarPresets.AuthorBalanced);
                f.DisplayCurrent();
                var beforeCarChange = f.State();
                f.Profiles.SelectCar("IRacing", "new-locale-car", "New locale car");
                check(f.Profiles.CurrentProfileId == beforeCarChange.SelectedId && ReferenceEquals(f.State(), beforeCarChange) &&
                    f.State().CurrentCar == "",
                    "a new car with the same default profile still waits for the queued panel rebind before changing its snapshot");
            }
        } finally { L10n.OverrideCulture = priorCulture; }
    }

    private static void CheckOfflinePreviewStop(Action<bool, string> check)
    {
        using (var f = new Fixture()) {
            f.Update(Data("IRacing", false));
            var transport = new AdapterProfile();
            var makeDevice = typeof(AdapterChecks).GetMethod("MakeDevice", BindingFlags.Static | BindingFlags.NonPublic)!;
            var device = (DeviceInstance)makeDevice.Invoke(null, new object[] { transport.Settings })!;
            using (var adapter = new SimHubHapticsAdapter(device, f.Controller.Produce, () => f.Controller.Active,
                false, f.Controller.CancelTest)) {
                Set(f.Extension, "adapter", adapter);
                adapter.EnsureTestReady();
                f.Controller.RequestPreview(EffectPreviewKind.Engine);
                adapter.WakeOutput();
                check(adapter.IsActive && f.Controller.PreviewActive && transport.Provider.Manager.Last!.States.Any(s => s.Gain > 0),
                    "offline stop fixture begins with an actual adapter-owned effect preview");
                f.Call("StopPreview");
                check(!f.Controller.Active && !f.Controller.PreviewActive && !adapter.IsActive &&
                    transport.Provider.Manager.Last!.States.All(s => s.Gain == 0),
                    "the extension StopPreview cancels offline playback and sends silence without attempting to reacquire ownership");
                int writes = transport.Provider.Manager.Writes;
                f.Call("StopPreview");
                check(transport.Provider.Manager.Writes == writes && !adapter.IsActive,
                    "stopping an already stopped offline preview is harmless and does not reopen output");
            }
        }
    }

    private static GameData Data(string game, bool running, string? id = null, string? model = null,
        bool replay = false, bool spectating = false)
    {
        // Only these host telemetry properties are consumed by UpdateContext; do not
        // initialize a game reader or any global SimHub process state for the fixture.
        var status = (SessionStatusData)FormatterServices.GetUninitializedObject(typeof(SessionStatusData));
        SetHostProperty(status, nameof(StatusDataBase.CarId), id ?? "");
        SetHostProperty(status, nameof(StatusDataBase.CarModel), model ?? "");
        SetHostProperty(status, nameof(StatusDataBase.Spectating), spectating);
        var data = new GameData { GameName = game, GameReplay = replay, NewData = status };
        SetHostProperty(data, nameof(GameData.GameRunning), running);
        return data;
    }
    private sealed class SessionStatusData : StatusDataBase
    {
        public override object GetRawDataObject() => this;
    }
    private sealed class SessionEngine : IEffectEngine, IEffectPreviewEngine
    {
        public PedalFeelSettings? Configured { get; private set; }
        public void Configure(PedalFeelSettings settings) => Configured = settings.Clone();
        public NativeOutput Tick() => new NativeOutput();
        public NativeOutput Preview(EffectPreviewKind effect, double elapsedSeconds) =>
            new NativeOutput { NewSample = 1, DrivingActive = 1, ThrottleHz = 37, ThrottleIntensity = 41 };
        public void Dispose() { }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly ProfileRepository Profiles = new ProfileRepository(Path.Combine(Path.GetTempPath(),
            "PedalFeelSession-" + Guid.NewGuid().ToString("N"), "profiles.json"));
        private readonly List<SessionEngine> engines = new List<SessionEngine>();
        public readonly PedalController Controller;
        public readonly PedalFeelExtension Extension = new PedalFeelExtension();
        public SessionEngine LastEngine => engines.Last();
        public PedalFeelSettings DisplayedSettings => (PedalFeelSettings)Get(Extension, "panelSettings")!;
        public Fixture()
        {
            Controller = new PedalController(() => { var engine = new SessionEngine(); engines.Add(engine); return engine; }, () => 1000);
            var settings = Profiles.Current(); settings.Enabled = true; Profiles.Commit(settings);
            Controller.Configure(Profiles.Current());
            Set(Extension, "profiles", Profiles); Set(Extension, "controller", Controller);
        }
        public void Update(GameData data) => Call("UpdateContext", data);
        public void DisplayCurrent()
        {
            Set(Extension, "panelSettings", Profiles.Current());
            Set(Extension, "panelKey", Profiles.CurrentKey); Set(Extension, "panelName", Profiles.CurrentName);
            Call("CaptureProfileState");
        }
        public void Select(string id)
        {
            DisplayCurrent(); Call("SelectProfile", id); DisplayCurrent();
        }
        public ProfilePanelState State() => (ProfilePanelState)Call("GetProfileState")!;
        public object? Call(string method, params object[] args)
        {
            try { return typeof(PedalFeelExtension).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Extension, args); }
            catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException!; }
        }
        public void Dispose() { Controller.Dispose(); Profiles.Dispose(); }
    }
    private static bool Near(double a, double b) => Math.Abs(a - b) < .000001;
    private static object? Get(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static void SetHostProperty(object instance, string name, object value) => instance.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(instance, value, null);
    private static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }
}
