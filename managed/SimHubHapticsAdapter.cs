using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using BA63Driver;
using GameReaderCommon.Enums;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device.HapticDevices;
using SimHub.Plugins.DataPlugins.ShakeItV3.Device.MotorsWithFrequency;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;
using SimHub.Plugins.DataPlugins.ShakeItV3.Settings;
using SimHub.Plugins.Devices;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace PedalFeel.SimHub
{
    /// <summary>
    /// Routes the existing P-HPR writer through PedalFeel. It never opens another HID handle,
    /// changes device Enabled/Suspended, or rewrites the user's ShakeIt profiles.
    /// Internal members below were checked against SimHub 9.12.8; unsupported layouts fail to stock.
    /// </summary>
    public sealed class SimHubHapticsAdapter : IDisposable
    {
        public const string SupportedDeviceTypeId = "A974E20A-A714-467D-B55A-7DEE2BA3896C";
        private static string StockControlsHint => L10n.T("Вибрацией управляет PedalFeel. Верните управление SimHub на вкладке PedalFeel, чтобы открыть эти настройки.");
        private readonly DeviceInstance _device;
        private readonly Func<MotorCommand?> _produce;
        private readonly Func<bool> _enabled;
        private readonly Action? _cancelPendingTest;
        private readonly Action? _returnToStock;
        private readonly object _gate = new object();
        private readonly Dictionary<MotorsOutputManagerBase, ProviderBridge> _routes = new Dictionary<MotorsOutputManagerBase, ProviderBridge>();
        private readonly Dictionary<Control, UiState> _uiStates = new Dictionary<Control, UiState>();
        private readonly Timer? _timer;
        private ProviderBridge? _current, _writing;
        private bool _waitingForConnection;
        private DateTime _nextRouteScan;
        private volatile bool _disposed, _faulted, _active, _supported;
        private volatile string _status = L10n.T("Ожидание штатного устройства SIMAGIC");
        private string? _lastError;
        private string? _lastProviderDiagnostic;
        private DateTime _nextUiScan;
        private bool _lastUiDisable;
        private bool _uiQueued;
        private DependencyObject? _lastUiRoot;

        public SimHubHapticsAdapter(DeviceInstance device, Func<MotorCommand?> produce, Func<bool> enabled, Action? cancelPendingTest = null, Action? returnToStock = null)
            : this(device, produce, enabled, true, cancelPendingTest, returnToStock) { }

        internal SimHubHapticsAdapter(DeviceInstance device, Func<MotorCommand?> produce, Func<bool> enabled, bool startTimer, Action? cancelPendingTest = null, Action? returnToStock = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _produce = produce ?? throw new ArgumentNullException(nameof(produce));
            _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
            _cancelPendingTest = cancelPendingTest;
            _returnToStock = returnToStock;
            if (startTimer) _timer = new Timer(TimerTick, null, 16, 16);
        }

        public string Status => _status;
        public bool IsActive => _active;

        internal PanelStatus? AttentionStatus(bool ownershipRequested)
        {
            lock (_gate)
            {
                if (_disposed) return PanelStatus.Create("Устройство остановлено", L10n.T("Включите устройство в SimHub, чтобы продолжить."), StatusTone.Neutral);
                if (_faulted) return PanelStatus.Create("Не удалось передать педали PedalFeel", L10n.T("Штатное управление сохранено. Причина указана в подробностях состояния."), StatusTone.Error);
                if (!DeviceCanRun(_device)) return PanelStatus.Create("Устройство выключено в SimHub", L10n.T("Включите переключатель рядом с названием устройства. После этого можно запускать игру или проверку."), StatusTone.Warning);
                if (!ownershipRequested) return null;
                if (!_supported || _current == null || !_current.Attached || !_current.IsInstalled) return PanelStatus.Create("Подготавливаем устройство", L10n.T("Ждём, когда SimHub подготовит вывод педалей. Если ожидание затянулось, откройте подробности состояния."), StatusTone.Ready);
                if (_waitingForConnection || (_active && !_current.IsConnected)) return PanelStatus.Create("Блок педалей не подключён", L10n.T("Проверьте питание и USB. Для настройки каналов откройте «Настройка педалей»."), StatusTone.Warning);
                if (_current.Stopped || !DeviceCanRun(_current.Device)) return PanelStatus.Create("Вывод педалей приостановлен", L10n.T("Проверьте, включено ли устройство в SimHub. Причина может быть указана в подробностях состояния."), StatusTone.Warning);
                if (!_active) return PanelStatus.Create("Подключаем PedalFeel", L10n.T("Игра или ручная проверка запущена. Ждём готовности вывода педалей."), StatusTone.Ready);
                return null;
            }
        }

        /// <summary>Call from extension DataUpdate; also safe to call after a mode change.</summary>
        public bool Refresh()
        {
            lock (_gate)
            {
                if (_disposed) return false;
                _nextRouteScan = DateTime.UtcNow.AddMilliseconds(250);
                bool requested = _enabled();
                if (!requested) { ReleaseOutput(); _faulted = false; }
                try
                {
                    if (!string.Equals(_device.DeviceDescriptor?.DeviceTypeID, SupportedDeviceTypeId, StringComparison.OrdinalIgnoreCase))
                        throw new NotSupportedException(L10n.T("Это расширение предназначено для штатного устройства SIMAGIC P-HPR."));

                    var live = new HashSet<MotorsOutputManagerBase>();
                    ProviderBridge? current = null;
                    string waitingStatus = L10n.T("Ожидание загрузки штатной хаптики SimHub");
                    foreach (DeviceInstance instance in _device.GetInstances())
                    {
                        Type type = instance.GetType();
                        if (!type.IsGenericType || type.GetGenericTypeDefinition().FullName !=
                            "SimHub.Plugins.DataPlugins.ShakeItV3.Device.ShakeItV3DeviceInstance`2") continue;
                        object? hosted = type.GetMethod("GetDevice", BindingFlags.Instance | BindingFlags.Public)?.Invoke(instance, null);
                        if (hosted == null) continue;
                        object? settings = Member(hosted, "Settings");
                        // CurrentProfile / CurrentOutputManager getters can create profiles, stop the
                        // previous transport and initialize another manager. Observe their cached state
                        // instead; the stock host remains responsible for its lifecycle transitions.
                        object? currentProfile = settings == null ? null : Field(settings.GetType(), "_CurrentProfile")?.GetValue(settings);
                        var effective = (settings == null ? null : Field(settings.GetType(), "oldManager")?.GetValue(settings)) as MotorsOutputManagerBase;
                        if (effective == null)
                        {
                            bool fromProfile = Member(currentProfile, "IncludeOutputSettingsInProfile") as bool? == true;
                            effective = Member(fromProfile ? currentProfile : settings, "OutputManager") as MotorsOutputManagerBase;
                        }
                        // InitProfile constructs an additional output whose provider is normally null
                        // when output settings are global. Only inspect the manager selected by the
                        // host; unused profile/global outputs must not veto the working transport.
                        if (effective == null) continue;
                        var provider = effective.ShakeItChannelsInfoProvider;
                        if (provider is ProviderBridge existing && ReferenceEquals(existing.Owner, this))
                        {
                            live.Add(effective);
                            current = existing;
                            continue;
                        }
                        ReportProvider(effective, provider, "active");
                        if (provider == null)
                        {
                            waitingStatus = L10n.T("Ожидание инициализации штатного вывода SIMAGIC (провайдер ещё не создан)");
                            continue; // A transient initialization state, not a latched compatibility fault.
                        }
                        var stock = provider as MotorsWithFrequencyChannelsSettingsProvider<SimagicHapticSettings>;
                        if (stock == null)
                            throw new NotSupportedException(L10n.F("Неподдерживаемый активный провайдер {0} в {1}; штатный вывод сохранён. Подробности в журнале SimHub.", provider.GetType().Name, effective.GetType().Name));
                        var bridge = new ProviderBridge(this, stock, effective, instance);
                        if (_routes.TryGetValue(effective, out ProviderBridge retired)) retired.Attached = false;
                        _routes[effective] = bridge;
                        effective.ShakeItChannelsInfoProvider = bridge;
                        live.Add(effective);
                        current = bridge;
                    }
                    if (!ReferenceEquals(_current, current)) ReleaseOutput();
                    foreach (var stale in _routes.Keys.Where(k => !live.Contains(k)).ToArray())
                    {
                        RestoreRoute(stale, _routes[stale]);
                        _routes.Remove(stale);
                    }
                    _current = current;
                    _supported = current != null;
                    if (!_supported) { _active = false; if (!_faulted) _status = waitingStatus; }
                    else if (!requested) _status = L10n.T("Штатная хаптика SimHub");
                    else if (!_faulted && !_active) _status = L10n.T("PedalFeel готов; ожидание вывода");
                }
                catch (Exception ex)
                {
                    Fault(ex);
                    RestoreAllRoutes();
                    _supported = false;
                }
                QueueUiUpdate();
                return _supported && !_faulted;
            }
        }

        private void ReportProvider(MotorsOutputManagerBase output, IShakeItChannelsInfoProvider? provider, string role)
        {
            string diagnostic = "PedalFeel provider: role=" + role + "; output=" + output.GetType().FullName +
                "; provider=" + (provider?.GetType().FullName ?? "<null>");
            if (diagnostic == _lastProviderDiagnostic) return;
            _lastProviderDiagnostic = diagnostic;
            try { global::SimHub.Logging.Current.Info(diagnostic); } catch { }
        }

        /// <summary>Validate the current device and its existing transport before starting a short test.</summary>
        public void EnsureTestReady()
        {
            lock (_gate)
            {
                Refresh();
                // The automatic mode can be armed while the simulator is closed. The extension
                // validates the explicit test request before this preflight; ownership starts only
                // after its 500 ms test deadline has been set, and WakeOutput still requires it.
                RequireReady(requireOwnership: false);
                try
                {
                    var manager = _current!.EnsureTransport();
                    // USBGenericManager creates/reopens the driver only from Display(), not from
                    // IsConnected(). Prime it with silence before starting the test's 500 ms clock.
                    if (!manager.IsConnected())
                    {
                        CancelPendingTest();
                        _current.Write(new MotorStates());
                    }
                    if (!manager.IsConnected())
                        throw new InvalidOperationException(L10n.T("Контроллер SIMAGIC не подключён. Проверьте питание, USB и выбранный серийный номер."));
                }
                catch (Exception ex)
                {
                    Log(ex);
                    Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    throw new InvalidOperationException("SIMAGIC: " + cause.Message, cause);
                }
            }
        }

        /// <summary>Send the first test frame now; the independent timer also sends its stopping frame.</summary>
        public void WakeOutput()
        {
            lock (_gate)
            {
                RequireReady();
                Pump();
                if (!_active) throw new InvalidOperationException(_status);
                if (_writing == null || !_writing.IsConnected)
                {
                    ReleaseOutput();
                    throw new InvalidOperationException(L10n.T("Контроллер SIMAGIC отключился; тест остановлен."));
                }
            }
        }

        private void RequireReady(bool requireOwnership = true)
        {
            if (_disposed) throw new InvalidOperationException(L10n.T("Расширение PedalFeel остановлено."));
            if (_faulted) throw new InvalidOperationException(_status);
            if (requireOwnership && !_enabled()) throw new InvalidOperationException(L10n.T("PedalFeel сейчас не управляет выводом. Запустите iRacing или ручную проверку."));
            if (!DeviceCanRun(_device)) throw new InvalidOperationException(L10n.T("Устройство SIMAGIC выключено или приостановлено в SimHub."));
            if (_current == null || !_current.Attached || !_current.IsInstalled)
                throw new InvalidOperationException(L10n.T("Штатный вывод SIMAGIC ещё не готов. Дождитесь подключения устройства."));
            if (!DeviceCanRun(_current.Device) || _current.Stopped)
                throw new InvalidOperationException(L10n.T("Штатная хаптика устройства остановлена или выключена в SimHub."));
        }

        private static bool DeviceCanRun(DeviceInstance device) => device.ShouldBeRunning() && !device.Suspended;

        private void TimerTick(object? state)
        {
            // Drop overlapping timer callbacks instead of building a queue behind a slow USB write.
            if (!Monitor.TryEnter(_gate)) return;
            try
            {
                if (_disposed) return;
                if (DateTime.UtcNow >= _nextRouteScan) Refresh();
                Pump();
            }
            catch (Exception ex) { Fault(ex); QueueUiUpdate(); }
            finally { Monitor.Exit(_gate); }
        }

        private void Pump()
        {
            _waitingForConnection = false;
            var bridge = _current;
            if (_disposed || _faulted || !_enabled() || !DeviceCanRun(_device) || bridge == null ||
                !bridge.Attached || !bridge.IsInstalled || bridge.Stopped || !DeviceCanRun(bridge.Device))
            {
                ReleaseOutput();
                QueueUiUpdate();
                return;
            }
            try
            {
                if (!bridge.EnsureTransport().IsConnected())
                {
                    ReleaseOutput();
                    CancelPendingTest();
                    // Poll/reconnect through the stock manager using silence; never replay the
                    // remainder of a manual test after a brief USB disconnect.
                    bridge.Write(new MotorStates());
                    _waitingForConnection = true;
                    _status = L10n.T("PedalFeel: ожидание подключения контроллера");
                    QueueUiUpdate();
                    return;
                }
                MotorCommand? command = _produce();
                if (command == null)
                {
                    ReleaseOutput();
                    _status = L10n.T("PedalFeel недоступен; работает штатная хаптика");
                }
                else
                {
                    if (!ReferenceEquals(_writing, bridge)) ReleaseOutput();
                    _writing = bridge;
                    bridge.Write(BuildMotorStates(command));
                    _active = true;
                    _status = bridge.IsConnected ? L10n.T("PedalFeel управляет педалями") : L10n.T("PedalFeel: ожидание подключения контроллера");
                }
            }
            catch (Exception ex) { Fault(ex); }
            QueueUiUpdate();
        }

        private void ReleaseOutput()
        {
            if (_writing != null) CancelPendingTest();
            _writing?.SilenceExisting();
            _writing = null;
            _active = false;
            _waitingForConnection = false;
        }

        private void CancelPendingTest()
        {
            try { _cancelPendingTest?.Invoke(); } catch (Exception ex) { Log(ex); }
        }

        // Pure conversion seam: validation and clamping can be tested without a device or SimHub loop.
        internal static MotorStates BuildMotorStates(MotorCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (command.BrakeChannel < 0 || command.BrakeChannel > 2 || command.ThrottleChannel < 0 || command.ThrottleChannel > 2 || command.BrakeChannel == command.ThrottleChannel)
                throw new ArgumentException(L10n.T("Каналы тормоза и газа должны различаться и находиться в диапазоне 0–2."));
            var states = new MotorStates();
            for (int i = 0; i < 3; i++) states.States[i] = new MotorState { Frequency = 10, Gain = 0 };
            states.States[command.BrakeChannel] = new MotorState { Frequency = Clamp(command.BrakeFrequency, 10, 50), Gain = command.BrakeFrequency > 0 ? Clamp(command.BrakeIntensity, 0, 100) / 100.0 : 0 };
            states.States[command.ThrottleChannel] = new MotorState { Frequency = Clamp(command.ThrottleFrequency, 10, 50), Gain = command.ThrottleFrequency > 0 ? Clamp(command.ThrottleIntensity, 0, 100) / 100.0 : 0 };
            return states;
        }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

        private void Update(ProviderBridge bridge, Dictionary<int, ChannelValue> stockValues)
        {
            lock (_gate)
            {
                if (_disposed || !bridge.Attached || !bridge.IsInstalled || !DeviceCanRun(_device) || !DeviceCanRun(bridge.Device))
                {
                    if (ReferenceEquals(_writing, bridge)) ReleaseOutput();
                    QueueUiUpdate();
                    return;
                }
                bridge.Stopped = false;
                if (!_disposed && !_faulted && bridge.Attached && bridge.IsInstalled && _enabled() &&
                    DeviceCanRun(_device) && DeviceCanRun(bridge.Device))
                {
                    // A stock callback proves which profile manager is actually in use, including
                    // the narrow interval before Refresh observes the host's updated cache.
                    if (!ReferenceEquals(_current, bridge)) { ReleaseOutput(); _current = bridge; }
                    if (!_active) Pump();
                    if (_active || _waitingForConnection) return;
                }
                ReleaseOutput();
                bridge.Original.UpdateOutput(stockValues);
                QueueUiUpdate();
            }
        }

        private void Fault(Exception ex)
        {
            ReleaseOutput();
            bool shouldLog = !_faulted || _lastError != ex.ToString();
            _lastError = ex.ToString();
            _faulted = true;
            _active = false;
            Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            _status = L10n.F("Штатная хаптика восстановлена: {0}", cause.Message);
            if (shouldLog) Log(ex);
        }

        private static void Log(Exception ex)
        {
            try { global::SimHub.Logging.Current.Error("PedalFeel output: " + ex.Message, ex); } catch { }
        }

        private static void RestoreRoute(MotorsOutputManagerBase manager, ProviderBridge bridge)
        {
            bridge.Attached = false;
            if (ReferenceEquals(manager.ShakeItChannelsInfoProvider, bridge)) manager.ShakeItChannelsInfoProvider = bridge.Original;
        }

        private void RestoreAllRoutes()
        {
            foreach (var route in _routes) RestoreRoute(route.Key, route.Value);
            _routes.Clear();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _timer?.Dispose();
                ReleaseOutput();
                _current = null;
                RestoreAllRoutes();
                QueueUiUpdate(true);
            }
        }

        private void QueueUiUpdate(bool force = false)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            bool disable = !_disposed && _supported && !_faulted && _active && _enabled();
            if (!force && disable == _lastUiDisable && DateTime.UtcNow < _nextUiScan) return;
            _lastUiDisable = disable;
            _nextUiScan = DateTime.UtcNow.AddMilliseconds(300);
            if (_uiQueued) return;
            _uiQueued = true;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                lock (_gate)
                {
                    _uiQueued = false;
                    bool block = !_disposed && _supported && !_faulted && _active && _enabled();
                    if (!block) { RestoreUi(); return; }
                    try
                    {
                        // Read the cached page only: SettingsControl/GetSettingsControls would create unrelated controls.
                        var root = Field(_device.GetType(), "controlInstance")?.GetValue(_device) as DependencyObject;
                        if (!ReferenceEquals(_lastUiRoot, root)) { RestoreUi(); _lastUiRoot = root; }
                        if (root != null) DisableStockControls(root, new HashSet<DependencyObject>());
                    }
                    catch (Exception ex)
                    {
                        Fault(ex);
                        RestoreAllRoutes();
                        RestoreUi();
                    }
                }
            }));
        }

        private void DisableStockControls(DependencyObject node, HashSet<DependencyObject> visited)
        {
            if (!visited.Add(node)) return;
            var control = node as Control;
            string typeName = node.GetType().FullName ?? "";
            if (control != null && typeName.StartsWith("SimHub.Plugins.DataPlugins.ShakeItV3.", StringComparison.Ordinal))
            {
                if (!_uiStates.ContainsKey(control)) _uiStates.Add(control, new UiState(control, RequestReturnToStock));
                control.SetCurrentValue(UIElement.IsEnabledProperty, false);
                control.SetCurrentValue(FrameworkElement.ToolTipProperty, StockControlsHint);
                control.SetCurrentValue(ToolTipService.ShowOnDisabledProperty, true);
                return;
            }
            if (node is Visual || node is Visual3D)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) DisableStockControls(VisualTreeHelper.GetChild(node, i), visited);
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject dependency) DisableStockControls(dependency, visited);
        }

        private void RequestReturnToStock()
        {
            // Never invoke the extension callback while holding _gate: extension.Stop takes its
            // own lifecycle lock and calls Refresh, while DataUpdate uses the opposite lock order.
            var dispatcher = Application.Current?.Dispatcher;
            if (_returnToStock == null || dispatcher == null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (_disposed) return;
                try { _returnToStock(); } catch (Exception ex) { Log(ex); }
            }));
        }

        private void RestoreUi()
        {
            foreach (var saved in _uiStates) saved.Value.Restore(saved.Key);
            _uiStates.Clear();
        }

        private sealed class UiState
        {
            private readonly SavedProperty _enabled;
            private readonly SavedProperty _toolTip;
            private readonly SavedProperty _showOnDisabled;
            private readonly IDisposable _overlay;
            public UiState(Control control, Action returnToStock)
            {
                _enabled = new SavedProperty(control, UIElement.IsEnabledProperty);
                _toolTip = new SavedProperty(control, FrameworkElement.ToolTipProperty);
                _showOnDisabled = new SavedProperty(control, ToolTipService.ShowOnDisabledProperty);
                _overlay = StockControlLockAdorner.Attach(control, returnToStock);
            }
            public void Restore(Control control)
            {
                _overlay.Dispose();
                _enabled.Restore(control);
                _toolTip.Restore(control);
                _showOnDisabled.Restore(control);
            }
        }

        private sealed class SavedProperty
        {
            private readonly DependencyProperty _property;
            private readonly object _local;
            private readonly BindingBase? _binding;
            public SavedProperty(Control control, DependencyProperty property)
            {
                _property = property;
                _local = control.ReadLocalValue(property);
                _binding = BindingOperations.GetBindingBase(control, property);
            }
            public void Restore(Control control)
            {
                if (_binding != null) BindingOperations.SetBinding(control, _property, _binding);
                else if (_local == DependencyProperty.UnsetValue) control.ClearValue(_property);
                else control.SetValue(_property, _local);
            }
        }

        private static FieldInfo? Field(Type? type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            return null;
        }

        private static MethodInfo? Method(Type? type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (method != null) return method;
            }
            return null;
        }

        private static object? Member(object? target, string name)
        {
            if (target == null) return null;
            for (Type? type = target.GetType(); type != null; type = type.BaseType)
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (property != null) return property.GetValue(target, null);
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target);
            }
            return null;
        }

        private sealed class ProviderBridge : IShakeItChannelsInfoProvider
        {
            public readonly SimHubHapticsAdapter Owner;
            public readonly MotorsWithFrequencyChannelsSettingsProvider<SimagicHapticSettings> Original;
            public readonly DeviceInstance Device;
            private readonly MotorsOutputManagerBase _output;
            public bool Attached = true;
            public bool Stopped;
            public bool IsInstalled => ReferenceEquals(_output.ShakeItChannelsInfoProvider, this);
            private readonly FieldInfo _managerField;
            private readonly MethodInfo _ensureManager, _updateConnection;
            public ProviderBridge(SimHubHapticsAdapter owner, MotorsWithFrequencyChannelsSettingsProvider<SimagicHapticSettings> original, MotorsOutputManagerBase output, DeviceInstance device)
            {
                Owner = owner;
                Original = original;
                _output = output;
                Device = device;
                Type type = original.GetType();
                _managerField = Field(type, "manager") ?? throw new NotSupportedException(L10n.T("SimHub: поле транспорта изменено"));
                _ensureManager = Method(type, "EnsureManager") ?? throw new NotSupportedException(L10n.T("SimHub: инициализация транспорта изменена"));
                _updateConnection = Method(type, "UpdateIsConnected") ?? throw new NotSupportedException(L10n.T("SimHub: состояние транспорта изменено"));
                if (!typeof(IUSBGenericManagerSerial<MotorStates>).IsAssignableFrom(_managerField.FieldType))
                    throw new NotSupportedException(L10n.T("SimHub: неподдерживаемый тип транспорта"));
            }
            public IUSBGenericManagerSerial<MotorStates> EnsureTransport()
            {
                _ensureManager.Invoke(Original, null);
                var manager = _managerField.GetValue(Original) as IUSBGenericManagerSerial<MotorStates>;
                if (manager == null) throw new InvalidOperationException(L10n.T("SimHub не создал транспорт контроллера"));
                manager.SetSerialNumber(Original.DisplaySerial && Original.UseRequestedSerial ? Original.RequestedSerial : null);
                _updateConnection.Invoke(Original, null);
                return manager;
            }
            public void Write(MotorStates states)
            {
                var manager = EnsureTransport();
                manager.Display(states);
                _updateConnection.Invoke(Original, null);
            }
            public void SilenceExisting()
            {
                try
                {
                    var manager = _managerField.GetValue(Original) as IUSBGenericManagerSerial<MotorStates>;
                    if (manager != null && manager.IsConnected()) manager.Display(new MotorStates());
                }
                catch { /* Device teardown may already have disposed the original transport. */ }
            }
            public string DefaultSettingsKey => Original.DefaultSettingsKey;
            public bool IsConnected => Original.IsConnected;
            public List<ChannelInformation> GetChannels(MotorsWithFrequencyOutputManagerBase outputManager) => Original.GetChannels(outputManager);
            public ChannelActivation CreateDefaultActivationFor(FFBPlacement placement, MotorsWithFrequencyOutputManagerBase outputManager) => Original.CreateDefaultActivationFor(placement, outputManager);
            public void LoadDefaultPlatformSettings(EffectsContainerBase effects, ShakeItProfile profile) => Original.LoadDefaultPlatformSettings(effects, profile);
            public void UpdateOutput(Dictionary<int, ChannelValue> values) => Owner.Update(this, values);
            public void Stop()
            {
                lock (Owner._gate)
                {
                    Stopped = true;
                    Owner.CancelPendingTest();
                    if (ReferenceEquals(Owner._writing, this)) Owner.ReleaseOutput();
                    Original.Stop();
                    Owner.QueueUiUpdate();
                }
            }
            public FrequencyRange HardwareFrequencyRange() => Original.HardwareFrequencyRange();
            public void SetSettings(ShakeItSettings settings) => Original.SetSettings(settings);
            public IEnumerable<DeviceSettingControl> GetSettingsControls() => Original.GetSettingsControls();
        }
    }
}
