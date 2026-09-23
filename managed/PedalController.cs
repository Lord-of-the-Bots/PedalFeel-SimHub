using System;
using System.Diagnostics;

namespace PedalFeel.SimHub
{
    internal sealed class PedalController : IDisposable
    {
        private readonly object sync = new object();
        private readonly Func<IEffectEngine> engineFactory;
        private readonly Func<long> clock;
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private IEffectEngine? engine;
        private PedalFeelSettings settings = new PedalFeelSettings();
        private bool compatibleGame, gameRunning, paused, disposed, failed;
        private string state = L10n.T("Работают стандартные эффекты SimHub.");
        private NativeOutput? lastFrame;
        private long testDeadline;
        private int testChannel, testHz, testIntensity;
        private EffectPreviewKind? previewEffect;
        private long previewStart, previewDeadline;

        internal PedalController(Func<IEffectEngine>? engineFactory = null, Func<long>? clock = null)
        {
            this.engineFactory = engineFactory ?? (() => new NativeEngine());
            this.clock = clock ?? (() => watch.ElapsedMilliseconds);
        }
        public bool Active { get { lock (sync) return !disposed && settings.Enabled && !failed &&
            ((compatibleGame && gameRunning) || testDeadline > clock() || previewDeadline > clock()); } }
        internal bool PreviewActive { get { lock (sync) return previewEffect.HasValue && previewDeadline > clock(); } }
        public string Status { get { lock (sync) return !disposed && settings.Enabled && !failed && !Active ? WaitingStatus() : state; } }
        public PanelStatus Presentation()
        {
            lock (sync) {
                if (disposed) return PanelStatus.Create("Устройство остановлено", L10n.T("Включите устройство в SimHub, чтобы продолжить."), StatusTone.Neutral);
                if (!settings.Enabled) return PanelStatus.Create("Педалями управляет SimHub", L10n.T("Автоматическое включение PedalFeel выключено."), StatusTone.Neutral);
                if (failed) return PanelStatus.Create("Не удалось включить PedalFeel", L10n.T("Управление осталось у SimHub. Откройте подробности состояния, чтобы узнать причину."), StatusTone.Error);
                if (PreviewActive) return PanelStatus.Create(L10n.F("Пример: {0}", EffectPreview.Name(previewEffect!.Value)),
                    L10n.T("Отдельный эффект с текущими настройками профиля и моторов. Остановится через 2 секунды."), StatusTone.Active);
                if (testDeadline > clock()) return PanelStatus.Create(testChannel == settings.BrakeChannel ? L10n.T("Проверяем тормоз") : L10n.T("Проверяем газ"),
                    L10n.F("{0} Гц · {1}% · остановится через 0,5 секунды.", testHz, testIntensity), StatusTone.Active);
                if (gameRunning && !compatibleGame) return PanelStatus.Create("В этой игре работает SimHub", L10n.T("PedalFeel включится автоматически, когда запустите iRacing."), StatusTone.Neutral);
                if (!gameRunning) return PanelStatus.Create("Ждём запуск iRacing", L10n.T("Автоматическое включение готово. Сейчас педалями управляет SimHub."), StatusTone.Ready);
                if (paused) return PanelStatus.Create("Пауза или повтор — вибрация выключена", L10n.T("PedalFeel продолжает управлять педалями и возобновит эффекты после возвращения к езде."), StatusTone.Neutral);
                if (!lastFrame.HasValue || lastFrame.Value.Connected == 0)
                    return PanelStatus.Create("Ждём данные iRacing", L10n.T("Симулятор запущен. Эффекты начнутся после загрузки сессии и выезда на трассу."), StatusTone.Ready);
                if (lastFrame.Value.Stale != 0) return PanelStatus.Create("Данные игры перестали обновляться", L10n.T("Вибрация остановлена. Если игра закрыта, SimHub вернёт управление в течение нескольких секунд."), StatusTone.Warning);
                if (lastFrame.Value.DrivingActive == 0) return PanelStatus.Create("Готово — можно выезжать", L10n.T("PedalFeel управляет педалями. Эффекты появятся во время езды."), StatusTone.Ready);
                var active = PanelStatus.Create("PedalFeel работает", L10n.T("Используются настройки выбранной машины. Силу ощущений можно менять ниже."), StatusTone.Active);
                var frame = lastFrame.Value;
                active.OutputSummary = L10n.F("Тормоз: сигнал {0}% → мотор {1}% · {2} Гц\nГаз: сигнал {3}% → мотор {4}% · {5} Гц",
                    SignalPercent(frame.BrakeRaw), frame.BrakeIntensity, frame.BrakeHz,
                    SignalPercent(frame.ThrottleRaw), frame.ThrottleIntensity, frame.ThrottleHz);
                return active;
            }
        }
        private static int SignalPercent(double value) => double.IsNaN(value) || double.IsInfinity(value) ? 0
            : (int)Math.Round(Math.Max(0, Math.Min(1, value)) * 100, MidpointRounding.AwayFromZero);
        public void Configure(PedalFeelSettings updated, bool resetTelemetry = false)
        {
            lock (sync) {
                if (disposed) return;
                settings = updated.Clone(); settings.Normalize();
                lastFrame = null;
                testDeadline = 0;
                previewDeadline = 0; previewEffect = null;
                failed = false;
                if (!settings.Enabled) {
                    state = L10n.T("Работают стандартные эффекты SimHub.");
                    engine?.Dispose(); engine = null; return;
                }
                if (settings.BrakeChannel == settings.ThrottleChannel) {
                    Fail(L10n.T("Выберите разные каналы тормоза и газа.")); return;
                }
                try {
                    // iRacing's ABS/brake demand, wheel scales and RPM/speed learning belong to
                    // the previous car. A car switch starts a fresh reader as well as fresh renderers.
                    if (resetTelemetry) { engine?.Dispose(); engine = null; }
                    if (engine == null) engine = engineFactory();
                    engine.Configure(settings);
                    state = compatibleGame && gameRunning ? L10n.T("PedalFeel включён. Ожидание телеметрии iRacing.") : WaitingStatus();
                } catch (Exception error) { Fail(error.Message); }
            }
        }
        public void SetContext(string? game, bool running, bool pausedOrReplay)
        {
            lock (sync) {
                if (disposed) return;
                bool supported = string.Equals(game?.Trim(), "iracing", StringComparison.OrdinalIgnoreCase);
                bool starting = supported && running && !(compatibleGame && gameRunning);
                bool changed = compatibleGame != supported || gameRunning != running || paused != pausedOrReplay;
                if (changed) { testDeadline = 0; previewDeadline = 0; previewEffect = null; }
                compatibleGame = supported; gameRunning = running; paused = pausedOrReplay;
                // The same car can be driven after restarting the simulator. Its saved tuning
                // remains, but learned telemetry must start afresh for the new session.
                if (starting && settings.Enabled && !failed) Configure(settings, resetTelemetry: true);
                else if (changed && settings.Enabled && !failed)
                    state = supported && running ? L10n.T("PedalFeel включён. Ожидание телеметрии iRacing.") : WaitingStatus();
            }
        }
        private string WaitingStatus() => gameRunning && !compatibleGame
            ? L10n.T("Для этой игры работают стандартные эффекты SimHub. PedalFeel автоматически включится в iRacing.")
            : L10n.T("Автоматический режим PedalFeel: ожидание запуска iRacing. Работают стандартные эффекты SimHub.");

        // Validate before checking the transport, without taking ownership or starting the timer.
        public void ValidateTest(int channel, int hz, int intensity)
        {
            lock (sync) {
                if (disposed) throw new InvalidOperationException(L10n.T("Устройство остановлено. Включите его в SimHub."));
                if (!settings.Enabled) throw new InvalidOperationException(L10n.T("Включите «Автоматически включать PedalFeel в iRacing» вверху вкладки."));
                if (failed || engine == null) throw new InvalidOperationException(state);
                if (gameRunning && !compatibleGame) throw new InvalidOperationException(L10n.T("Сейчас запущена другая игра. Закройте её перед проверкой PedalFeel; запускать iRacing для теста не требуется."));
                if (channel < 0 || channel > 2 || (channel != settings.BrakeChannel && channel != settings.ThrottleChannel))
                    throw new ArgumentException(L10n.T("Выбранный канал не назначен педали."));
                if (channel == settings.BrakeChannel ? !settings.BrakeEnabled : !settings.ThrottleEnabled)
                    throw new InvalidOperationException(L10n.T("Включите выбранную педаль в разделе каналов."));
                if (hz != 16 && hz != 25 && hz != 35 && hz != 50)
                    throw new ArgumentException(L10n.T("Выберите частоту 16, 25, 35 или 50 Гц."));
                if (intensity < 1 || intensity > 100)
                    throw new ArgumentException(L10n.T("Для проверки задайте мощность от 1 до 100%."));
            }
        }
        public void RequestTest(int channel, int hz, int intensity)
        {
            lock (sync) {
                ValidateTest(channel, hz, intensity);
                previewDeadline = 0; previewEffect = null;
                testChannel = channel; testHz = hz; testIntensity = intensity;
                testDeadline = clock() + 500;
                state = L10n.F("Тест {0} Гц · {1}% · 0,5 с", testHz, testIntensity);
            }
        }
        public void CancelTest()
        {
            lock (sync) { testDeadline = 0; previewDeadline = 0; previewEffect = null; }
        }
        public void ValidatePreview(EffectPreviewKind effect)
        {
            lock (sync) {
                if (!EffectPreview.IsValid(effect)) throw new ArgumentException(L10n.T("Неизвестный эффект для проверки."));
                if (disposed) throw new InvalidOperationException(L10n.T("Устройство остановлено. Включите его в SimHub."));
                if (!settings.Enabled) throw new InvalidOperationException(L10n.T("Включите «Автоматически включать PedalFeel в iRacing» вверху вкладки."));
                if (failed || engine == null) throw new InvalidOperationException(state);
                if (!(engine is IEffectPreviewEngine)) throw new InvalidOperationException(L10n.T("Обновите движок PedalFeel для проверки отдельных эффектов."));
                if (gameRunning && !compatibleGame) throw new InvalidOperationException(L10n.T("Сейчас запущена другая игра. Закройте её перед проверкой PedalFeel; запускать iRacing для теста не требуется."));
                if (!(EffectPreview.UsesBrake(effect) && settings.BrakeEnabled) && !(EffectPreview.UsesThrottle(effect) && settings.ThrottleEnabled))
                    throw new InvalidOperationException(L10n.T("Включите выбранную педаль в разделе каналов."));
                if (!EffectPreview.HasStrength(effect, settings))
                    throw new InvalidOperationException(L10n.T("Эффект выключен: увеличьте его силу или общую силу профиля."));
            }
        }
        public void RequestPreview(EffectPreviewKind effect)
        {
            lock (sync) {
                ValidatePreview(effect);
                testDeadline = 0; lastFrame = null;
                previewEffect = effect; previewStart = clock();
                previewDeadline = previewStart + EffectPreview.DurationMilliseconds;
                state = L10n.F("Пример: {0}", EffectPreview.Name(effect));
            }
        }
        public void CancelPreview()
        {
            lock (sync) { previewDeadline = 0; previewEffect = null; }
        }
        public MotorCommand? Produce()
        {
            lock (sync) {
                if (!Active || engine == null) return null;
                var command = Zero();
                try {
                    if (previewEffect.HasValue) {
                        if (previewDeadline > clock()) {
                            var frame = ((IEffectPreviewEngine)engine).Preview(previewEffect.Value, Math.Max(0, clock() - previewStart) / 1000.0);
                            command.BrakeFrequency = frame.BrakeHz; command.BrakeIntensity = frame.BrakeIntensity;
                            command.ThrottleFrequency = frame.ThrottleHz; command.ThrottleIntensity = frame.ThrottleIntensity;
                            state = L10n.F("Пример: {0}", EffectPreview.Name(previewEffect.Value));
                            return command;
                        }
                        // Give the transport an explicit quiet frame before resuming a live session.
                        previewDeadline = 0; previewEffect = null;
                        return command;
                    }
                    if (testDeadline > clock()) {
                        if (testChannel == settings.BrakeChannel) { command.BrakeFrequency = testHz; command.BrakeIntensity = testIntensity; }
                        else { command.ThrottleFrequency = testHz; command.ThrottleIntensity = testIntensity; }
                        state = L10n.F("Тест {0} Гц · {1}% · 0,5 с", testHz, testIntensity);
                        return command;
                    }
                    var output = engine.Tick();
                    lastFrame = output;
                    if (paused) { state = L10n.T("PedalFeel: пауза или повтор — вибрация остановлена."); return command; }
                    if (output.Connected == 0) { state = L10n.T("PedalFeel включён. Ожидание телеметрии iRacing."); return command; }
                    if (output.Stale != 0) { state = L10n.T("PedalFeel: телеметрия перестала обновляться — вибрация остановлена."); return command; }
                    if (output.DrivingActive == 0) { state = L10n.T("PedalFeel: ожидание выезда на трассу."); return command; }
                    command.BrakeFrequency = output.BrakeHz; command.BrakeIntensity = output.BrakeIntensity;
                    command.ThrottleFrequency = output.ThrottleHz; command.ThrottleIntensity = output.ThrottleIntensity;
                    state = L10n.F("Тормоз: {0} · {1}% / {2} Гц\nГаз: {3} · {4}% / {5} Гц",
                        Mode(output.BrakeMode), output.BrakeIntensity, output.BrakeHz,
                        Mode(output.ThrottleMode), output.ThrottleIntensity, output.ThrottleHz);
                    return command;
                } catch (Exception error) { Fail(error.Message); return null; }
            }
        }
        private MotorCommand Zero() => new MotorCommand { BrakeChannel = settings.BrakeChannel, ThrottleChannel = settings.ThrottleChannel };
        private static string Mode(int value)
        {
            string[] names = { L10n.T("тишина"), L10n.T("холостой ход"), L10n.T("нагрузка"), L10n.T("порог сцепления"), L10n.T("блокировка"), "ABS", L10n.T("потеря сцепления"), L10n.T("неровности"), L10n.T("поребрик"), L10n.T("переключение"), L10n.T("отсечка") };
            return L10n.T(value >= 0 && value < names.Length ? names[value] : L10n.T("эффект"));
        }
        private void Fail(string message)
        {
            failed = true; testDeadline = 0; previewDeadline = 0; previewEffect = null;
            state = L10n.F("PedalFeel недоступен: {0} Стандартное управление остаётся активным.", message);
            try { engine?.Dispose(); } catch { }
            engine = null;
        }
        public void Dispose()
        {
            lock (sync) { disposed = true; testDeadline = 0; previewDeadline = 0; previewEffect = null; engine?.Dispose(); engine = null; }
        }
    }
}
