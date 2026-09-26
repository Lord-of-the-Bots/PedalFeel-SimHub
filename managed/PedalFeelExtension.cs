using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices;
using SimHub.Plugins.Devices.DeviceExtensions;

namespace PedalFeel.SimHub
{
    public sealed class PedalFeelExtensionFilter : IDeviceExtensionFilter
    {
        public const string SimagicHapticsId = "A974E20A-A714-467D-B55A-7DEE2BA3896C";
        public IEnumerable<Type> GetExtensionsTypes(DeviceInstance device)
        {
            if (string.Equals(device?.DeviceDescriptor?.DeviceTypeID, SimagicHapticsId, StringComparison.OrdinalIgnoreCase))
                yield return typeof(PedalFeelExtension);
        }
    }

    public sealed class PedalFeelExtension : DeviceExtension
    {
        private readonly object sync = new object();
        private ProfileRepository? profiles;
        private PedalController? controller;
        private SimHubHapticsAdapter? adapter;
        private PedalFeelPanel? panel;
        private PedalFeelSettings panelSettings = new PedalFeelSettings();
        private string panelKey = ProfileRepository.DefaultKey, panelName = L10n.T("Базовый профиль");
        private string panelProfileId = "";
        private ProfilePanelState panelProfileState = new ProfilePanelState();
        private bool currentCarAvailable;
        private bool iracingWasRunning, restoreAssignment;
        private bool ended;
        private string error = "";
        private int frames;
        public override string ExtentionTabTitle => "PedalFeel";

        private void EnsureProfiles()
        {
            if (profiles != null) return;
            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            string file = Path.Combine(root, "PluginsData", "PedalFeel", LinkedDevice.InstanceId.ToString("D") + ".json");
            profiles = new ProfileRepository(file);
        }
        public override void Init(PluginManager pluginManager)
        {
            lock (sync) {
                ended = false;
                iracingWasRunning = false; restoreAssignment = false; currentCarAvailable = false;
                error = "";
                try {
                    EnsureProfiles();
                    adapter?.Dispose(); adapter = null;
                    controller?.Dispose();
                    controller = new PedalController();
                    controller.Configure(profiles!.Current());
                    if (pluginManager.LastData != null) UpdateContext(pluginManager.LastData);
                    adapter = new SimHubHapticsAdapter(LinkedDevice, () => controller?.Produce(), () => controller?.Active ?? false,
                        () => controller?.CancelTest(), Stop);
                    Info("Initialized " + Assembly.GetExecutingAssembly().GetName().Version);
                } catch (Exception exception) { error = exception.Message; Log(exception); }
            }
        }
        public override void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            lock (sync) {
                if (ended) return;
                try {
                    bool wasActive = controller?.Active ?? false;
                    UpdateContext(data);
                    // Release/take over on the host's running-state transition immediately;
                    // unchanged frames only need the periodic route/profile discovery scan.
                    if (wasActive != (controller?.Active ?? false) || frames++ % 30 == 0) adapter?.Refresh();
                } catch (Exception exception) { error = exception.Message; Log(exception); }
            }
        }
        private void UpdateContext(GameData data)
        {
            if (profiles == null || controller == null) return;
            bool iracingRunning = data.GameRunning && string.Equals(data.GameName?.Trim(), "iracing", StringComparison.OrdinalIgnoreCase);
            if (iracingRunning && !iracingWasRunning) restoreAssignment = true;
            iracingWasRunning = iracingRunning;
            controller.SetContext(data.GameName, data.GameRunning,
                data.GamePaused || data.GameReplay || (data.NewData?.Spectating ?? false));
            string key = ProfileRepository.CarKey(data.GameName, data.NewData?.CarId, data.NewData?.CarModel);
            bool available = data.GameRunning && !data.GameReplay && !(data.NewData?.Spectating ?? false) && key != ProfileRepository.DefaultKey;
            currentCarAvailable = available;
            // Transient empty frames while loading the same sim must not overwrite the current car profile.
            if (key == ProfileRepository.DefaultKey) return;
            // A new session in the same car must load its assignment, even if the
            // user tried a different profile temporarily during the last session.
            if (restoreAssignment && iracingRunning) {
                profiles.Select(ProfileRepository.DefaultKey, null);
                restoreAssignment = false;
            }
            if (profiles.SelectCar(data.GameName, data.NewData?.CarId, data.NewData?.CarModel)) {
                controller.Configure(profiles.Current(), resetTelemetry: true);
                RebindPanel();
            }
        }
        public override Control CreateSettingControl()
        {
            lock (sync) {
                EnsureProfiles();
                if (panel == null) {
                    panelSettings = profiles!.Current(); panelKey = profiles.CurrentKey; panelName = profiles.CurrentName;
                    CaptureProfileState();
                    panel = new PedalFeelPanel(panelSettings, Changed, Status,
                        RequestTest, Stop,
                        () => panelKey == ProfileRepository.DefaultKey ? "" : panelName, Reset,
                        () => profiles?.Describe(panelKey, panelName) ?? "", CarPresets.Options, ApplyPreset, PresentationStatus,
                        previewEffect: RequestPreview, stopPreview: StopPreview,
                        profileState: GetProfileState, selectProfile: SelectProfile,
                        createProfile: CreateProfile, assignProfile: AssignProfile, clearAssignment: ClearAssignment, deleteProfile: DeleteProfile);
                }
                return panel;
            }
        }
        private void RequestTest(int channel, int hz, int intensity)
        {
            lock (sync) {
                if (ended || controller == null || adapter == null)
                    throw new InvalidOperationException(L10n.T("Устройство ещё не готово. Включите его в разделе Devices."));
                try {
                    // Armed automatic mode can be waiting for iRacing. A manual test temporarily
                    // owns output even then, after validating its settings and the stock transport.
                    controller.ValidateTest(channel, hz, intensity);
                    adapter.EnsureTestReady();
                    controller.RequestTest(channel, hz, intensity);
                    adapter.WakeOutput();
                    Info("Manual test submitted: channel=" + channel + ", frequency=" + hz + " Hz, power=" + intensity + "%, duration=500 ms. " + adapter.Status);
                } catch (Exception exception) {
                    controller.CancelTest();
                    adapter.Refresh();
                    Log(exception);
                    throw;
                }
            }
        }
        private void Changed()
        {
            lock (sync) {
                if (ended || profiles == null) return;
                // The edit belongs to the profile displayed by this panel, even if a car
                // change has queued (but not yet executed) a UI rebind.
                bool modeChanged = profiles.Current().Enabled != panelSettings.Enabled;
                profiles.CommitProfile(panelProfileId, panelSettings);
                controller?.Configure(profiles.Current());
                if (modeChanged) adapter?.Refresh();
            }
        }
        private void RequestPreview(EffectPreviewKind effect)
        {
            lock (sync) {
                if (ended || controller == null || adapter == null)
                    throw new InvalidOperationException(L10n.T("Устройство ещё не готово. Включите его в разделе Devices."));
                EnsureDisplayedProfileCurrent();
                try {
                    controller.ValidatePreview(effect);
                    adapter.EnsureTestReady();
                    controller.RequestPreview(effect);
                    adapter.WakeOutput();
                } catch (Exception exception) {
                    controller.CancelPreview(); adapter.Refresh(); Log(exception); throw;
                }
            }
        }
        private void StopPreview()
        {
            lock (sync) {
                if (ended) return;
                // Refresh releases an offline preview with silence. WakeOutput requires
                // ownership and would reject the already-cancelled offline preview.
                controller?.CancelPreview(); adapter?.Refresh();
            }
        }
        private void EnsureDisplayedProfileCurrent(bool requireCar = false)
        {
            if (ended || profiles == null || profiles.CurrentKey != panelKey || profiles.CurrentProfileId != panelProfileId)
                throw new InvalidOperationException(L10n.T("Машина или профиль изменились. Дождитесь обновления вкладки и повторите действие."));
            if (requireCar && !currentCarAvailable)
                throw new InvalidOperationException(L10n.T("Для назначения профиля загрузите текущую машину в iRacing."));
        }
        private void SelectProfile(string id)
        {
            lock (sync) {
                EnsureDisplayedProfileCurrent(); profiles!.SelectProfile(id);
                controller?.Configure(profiles.Current()); adapter?.Refresh(); RebindPanel();
            }
        }
        private void CreateProfile(string name, string basisId, bool assignCurrent)
        {
            lock (sync) {
                EnsureDisplayedProfileCurrent(assignCurrent);
                profiles!.CreateProfile(name, basisId, assignCurrent);
                controller?.Configure(profiles.Current()); adapter?.Refresh(); RebindPanel();
            }
        }
        private int DeleteProfile()
        {
            lock (sync) {
                EnsureDisplayedProfileCurrent();
                int affected = profiles!.DeleteProfile(panelProfileId);
                controller?.Configure(profiles.Current()); adapter?.Refresh(); RebindPanel();
                return affected;
            }
        }
        private void AssignProfile()
        {
            lock (sync) {
                EnsureDisplayedProfileCurrent(true); profiles!.AssignCurrentProfile(); RebindPanel();
            }
        }
        private void ClearAssignment()
        {
            lock (sync) {
                EnsureDisplayedProfileCurrent(true); profiles!.ClearCurrentAssignment();
                controller?.Configure(profiles.Current()); adapter?.Refresh(); RebindPanel();
            }
        }
        private void CaptureProfileState()
        {
            panelProfileId = profiles!.CurrentProfileId;
            panelProfileState = profiles.PanelState();
            panelProfileState.CanAssign = panelProfileState.CanAssign && currentCarAvailable;
        }
        private ProfilePanelState GetProfileState()
        {
            lock (sync) {
                // Recompute translated seed names and edit status, but never pair
                // a newly arrived car/profile with controls still showing the old one.
                if (!ended && profiles != null && profiles.CurrentKey == panelKey && profiles.CurrentProfileId == panelProfileId)
                    CaptureProfileState();
                return panelProfileState;
            }
        }
        private void Stop()
        {
            lock (sync) {
                if (profiles == null) return;
                var settings = profiles.Current(); settings.Enabled = false;
                profiles.Commit(settings); controller?.Configure(settings); adapter?.Refresh(); RebindPanel();
            }
        }
        private void Reset()
        {
            lock (sync) {
                EnsureDisplayedProfileCurrent(); profiles!.ResetCurrent();
                controller?.Configure(profiles.Current()); adapter?.Refresh(); RebindPanel();
            }
        }
        private void ApplyPreset(string presetId)
        {
            lock (sync) {
                if (ended || profiles == null) return;
                // The button belongs to the car currently displayed, even if a telemetry frame
                // switched the driving car before the queued UI rebind has run.
                profiles.ApplyPreset(panelKey, panelName, presetId);
                controller?.Configure(profiles.Current()); RebindPanel();
            }
        }
        private void RebindPanel()
        {
            if (panel == null) return;
            var currentPanel = panel;
            currentPanel.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() => {
                lock (sync) {
                    if (ended || profiles == null) return;
                    panelSettings = profiles.Current(); panelKey = profiles.CurrentKey; panelName = profiles.CurrentName;
                    CaptureProfileState();
                    currentPanel.Rebind(panelSettings);
                }
            }));
        }
        private string Status()
        {
            // Called by the UI, never from the hardware callback.
            string result = controller?.Status ?? L10n.T("Ожидание инициализации устройства.");
            if (!string.IsNullOrEmpty(error)) result += "\n" + error;
            if (adapter != null && !string.IsNullOrEmpty(adapter.Status)) result += "\n" + adapter.Status;
            if (profiles != null && !string.IsNullOrEmpty(profiles.SaveError)) result += "\n" + profiles.SaveError;
            return result;
        }
        private PanelStatus PresentationStatus()
        {
            lock (sync) {
                var result = controller?.Presentation() ?? PanelStatus.Create("Подготавливаем устройство", L10n.T("Подождите, пока SimHub загрузит настройки педалей."), StatusTone.Ready);
                // Query controller and adapter sequentially: never hold the controller lock while
                // acquiring the output gate, whose callbacks may themselves read the controller.
                bool requested = controller?.Active ?? false;
                var deviceAttention = adapter?.AttentionStatus(requested);
                if (deviceAttention != null && result.Tone != StatusTone.Error) result = deviceAttention;
                if (!string.IsNullOrEmpty(error)) result = PanelStatus.Create("Нужна проверка PedalFeel", L10n.T("Во время работы возникла ошибка. Откройте подробности состояния, чтобы узнать причину."), StatusTone.Error);
                if (profiles != null && !string.IsNullOrEmpty(profiles.SaveError)) {
                    if (result.Tone != StatusTone.Error) {
                        result.Title = L10n.T("Проверьте сохранение настроек");
                        result.Tone = StatusTone.Warning;
                    }
                    result.Detail += "\n" + profiles.SaveError;
                }
                result.Diagnostic = Status();
                return result;
            }
        }
        public override void LoadDefaultSettings() { /* Per-car settings are independent of stock device profiles. */ }
        public override JToken GetSettings() => new JObject { ["SchemaVersion"] = 1 };
        public override void SetSettings(JToken settings, bool isDefault) { /* Local device catalog is authoritative. */ }
        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions() { yield break; }
        public override void End(PluginManager pluginManager)
        {
            lock (sync) {
                ended = true;
                try { adapter?.Dispose(); } catch (Exception exception) { Log(exception); }
                adapter = null;
                controller?.Dispose(); controller = null;
                profiles?.Dispose(); profiles = null;
                panel = null;
            }
        }
        private static void Log(Exception error)
        {
            try { global::SimHub.Logging.Current.Error("PedalFeel: " + error.Message, error); } catch { }
        }
        private static void Info(string message)
        {
            try { global::SimHub.Logging.Current.Info("PedalFeel: " + message); } catch { }
        }
    }
}
