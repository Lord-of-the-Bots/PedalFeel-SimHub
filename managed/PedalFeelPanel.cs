using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace PedalFeel.SimHub
{
    public sealed class PedalFeelPanel : UserControl
    {
        private PedalFeelSettings _settings;
        private readonly Action _changed, _stop;
        private readonly Func<string> _status;
        private readonly Action<int, int, int> _test;
        private readonly Func<string>? _currentCar, _profileDescription;
        private readonly Action? _resetProfile;
        private readonly List<KeyValuePair<string, string>> _presets;
        private readonly Action<string>? _applyPreset;
        private readonly Func<PanelStatus>? _presentationStatus;
        private readonly Action<EffectPreviewKind>? _previewEffect;
        private readonly Action? _stopPreview, _assignProfile, _clearAssignment;
        private readonly Func<ProfilePanelState>? _profileState;
        private readonly Action<string>? _selectProfile;
        private readonly Action<string, string, bool>? _createProfile;
        private readonly List<KeyValuePair<EffectPreviewKind, Button>> _previewButtons = new List<KeyValuePair<EffectPreviewKind, Button>>();
        private readonly Dictionary<EffectPreviewKind, TextBlock> _previewMessages = new Dictionary<EffectPreviewKind, TextBlock>();
        private readonly DispatcherTimer _timer;
        private CheckBox _autoEnable = null!;
        private TabControl _sections = null!;
        private ScrollViewer _feelingScroll = null!, _pedalsScroll = null!;
        private Expander _presetsExpander = null!, _diagnosticsExpander = null!;
        private FrameworkElement _brakeCurve = null!, _throttleCurve = null!;
        private TextBlock _outputSummary = null!;
        private TextBlock _statusTitle = null!, _statusDetail = null!, _statusDiagnostic = null!, _statusMarker = null!;
        private TextBlock _carText = null!, _carHint = null!, _profileSummary = null!, _profileDescriptionText = null!;
        private TextBlock _feedback = null!, _testError = null!, _testHint = null!;
        private ComboBox _testPedal = null!, _presetChoice = null!;
        private ComboBox? _profileChoice, _profileBasis;
        private TextBox? _profileName;
        private CheckBox? _createAssigned;
        private Expander? _createProfileExpander;
        private Button? _assignProfileButton, _clearAssignmentButton;
        private TextBlock? _assignmentInfo;
        private bool _createProfileOpen, _assignNewProfile;
        private string _newProfileName = "", _newProfileBasis = "author-balanced", _previewErrorMessage = "";
        private EffectPreviewKind? _previewErrorKind;
        private bool _updating, _presetsOpen, _diagnosticsOpen;
        private int _generation, _sectionIndex, _testPedalIndex;
        private double _feelingOffset, _pedalsOffset;
        private string _presetId = "auto", _feedbackMessage = "", _testErrorMessage = "", _lastCulture = "";
        private DateTime _feedbackUntil;

        public PedalFeelPanel(PedalFeelSettings settings, Action changed, Func<string> status,
            Action<int, int, int> test, Action stop, Func<string>? currentCar = null,
            Action? resetProfile = null, Func<string>? profileDescription = null,
            IEnumerable<KeyValuePair<string, string>>? presets = null, Action<string>? applyPreset = null,
            Func<PanelStatus>? presentationStatus = null,
            Action<EffectPreviewKind>? previewEffect = null, Action? stopPreview = null,
            Func<ProfilePanelState>? profileState = null, Action<string>? selectProfile = null,
            Action<string, string, bool>? createProfile = null, Action? assignProfile = null, Action? clearAssignment = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _changed = changed ?? throw new ArgumentNullException(nameof(changed));
            _status = status ?? (() => "");
            _test = test ?? throw new ArgumentNullException(nameof(test));
            _stop = stop ?? throw new ArgumentNullException(nameof(stop));
            _currentCar = currentCar; _resetProfile = resetProfile; _profileDescription = profileDescription;
            _presets = presets == null ? new List<KeyValuePair<string, string>>() : new List<KeyValuePair<string, string>>(presets);
            _applyPreset = applyPreset; _presentationStatus = presentationStatus;
            _previewEffect = previewEffect; _stopPreview = stopPreview; _profileState = profileState;
            _selectProfile = selectProfile; _createProfile = createProfile; _assignProfile = assignProfile; _clearAssignment = clearAssignment;
            Build();
            _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, __) => RefreshState();
            Loaded += (_, __) => { RefreshState(); _timer.Start(); };
            Unloaded += (_, __) => { _timer.Stop(); CancelPreview(); };
        }

        public void Rebind(PedalFeelSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => Rebind(settings))); return; }
            CaptureViewState();
            ++_generation; // Detached controls must not change the next car's settings.
            _settings = settings;
            Build();
        }
        private void CaptureViewState()
        {
            if (_sections == null) return;
            _sectionIndex = Math.Max(0, _sections.SelectedIndex);
            _feelingOffset = _feelingScroll.VerticalOffset; _pedalsOffset = _pedalsScroll.VerticalOffset;
            _presetsOpen = _presetsExpander?.IsExpanded == true;
            _diagnosticsOpen = _diagnosticsExpander.IsExpanded;
            _testPedalIndex = Math.Max(0, _testPedal.SelectedIndex);
            if (_presetChoice?.SelectedValue is string id) _presetId = id;
            if (_createProfileExpander != null) _createProfileOpen = _createProfileExpander.IsExpanded;
            if (_profileName != null) _newProfileName = _profileName.Text;
            if (_profileBasis?.SelectedValue is string basis) _newProfileBasis = basis;
            if (_createAssigned != null) _assignNewProfile = _createAssigned.IsChecked == true;
        }
        private void Build()
        {
            _updating = true; ++_generation; _lastCulture = L10n.CultureName; _settings.Normalize();
            _previewButtons.Clear(); _previewMessages.Clear();
            var root = new Grid { Margin = new Thickness(14), MaxWidth = 920 };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
            root.Children.Add(BuildHeader());
            _sections = BuildNavigation();
            _feelingScroll = PageScroll(BuildFeelings(), "FeelingScroll");
            _pedalsScroll = PageScroll(BuildPedals(), "PedalsScroll");
            _sections.Items.Add(Named(new TabItem { Header = NavigationHeader("Ощущения в игре", _profileState == null ? "Эффекты и настройки текущей машины" : "Эффекты и профили"), Content = _feelingScroll }, "FeelingsTab"));
            _sections.Items.Add(Named(new TabItem { Header = NavigationHeader("Настройка педалей", "Калибровка, проверка и каналы"), Content = _pedalsScroll }, "PedalsTab"));
            _sections.SelectedIndex = Math.Min(1, _sectionIndex); Grid.SetRow(_sections, 1); root.Children.Add(_sections);
            Content = root; _updating = false; RefreshState();
            int generation = _generation; double feelingOffset = _feelingOffset, pedalsOffset = _pedalsOffset;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                if (generation != _generation) return;
                _feelingScroll.ScrollToVerticalOffset(feelingOffset); _pedalsScroll.ScrollToVerticalOffset(pedalsOffset);
            }));
        }
        private TabControl BuildNavigation()
        {
            var tabs = Named(new TabControl { Margin = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent }, "MainSections");
            tabs.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            var host = new FrameworkElementFactory(typeof(DockPanel));
            var headers = new FrameworkElementFactory(typeof(ItemsPresenter)); headers.SetValue(DockPanel.DockProperty, Dock.Top); host.AppendChild(headers);
            var content = new FrameworkElementFactory(typeof(ContentPresenter), "PART_SelectedContentHost"); content.SetValue(ContentPresenter.ContentSourceProperty, "SelectedContent"); host.AppendChild(content);
            tabs.Template = new ControlTemplate(typeof(TabControl)) { VisualTree = host };
            var columns = new FrameworkElementFactory(typeof(UniformGrid)); columns.SetValue(UniformGrid.ColumnsProperty, 2); tabs.ItemsPanel = new ItemsPanelTemplate(columns);
            var border = new FrameworkElementFactory(typeof(Border), "NavigationBorder"); border.SetValue(Border.PaddingProperty, new Thickness(14, 12, 14, 12));
            border.SetValue(Border.MarginProperty, new Thickness(2, 0, 2, 0)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1)); border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            Brush line = (TryFindResource("TextBrush") as Brush ?? SystemColors.ControlTextBrush).CloneCurrentValue(); line.Opacity = .35; border.SetValue(Border.BorderBrushProperty, line);
            var label = new FrameworkElementFactory(typeof(ContentPresenter)); label.SetValue(ContentPresenter.ContentSourceProperty, "Header"); border.AppendChild(label);
            var template = new ControlTemplate(typeof(TabItem)) { VisualTree = border };
            Brush selected = (TryFindResource("AccentColorBrush") as Brush ?? Brushes.SteelBlue).CloneCurrentValue(); selected.Opacity = .25;
            var active = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            active.Setters.Add(new Setter(Border.BackgroundProperty, selected, "NavigationBorder"));
            active.Setters.Add(new Setter(Border.BorderBrushProperty, TryFindResource("AccentColorBrush") as Brush ?? Brushes.SteelBlue, "NavigationBorder")); template.Triggers.Add(active);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true }; focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "NavigationBorder")); template.Triggers.Add(focus);
            var style = new Style(typeof(TabItem)); style.Setters.Add(new Setter(Control.TemplateProperty, template)); style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrush"))); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); tabs.ItemContainerStyle = style;
            return tabs;
        }
        private static FrameworkElement NavigationHeader(string title, string description)
        {
            var panel = new StackPanel(); panel.Children.Add(Named(Text(title, 17, FontWeights.SemiBold), title == "Настройка педалей" ? "NavigationPedalsTitle" : "NavigationFeelingsTitle")); panel.Children.Add(Note(description, 4)); return panel;
        }
        private FrameworkElement BuildHeader()
        {
            var body = new StackPanel();
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.Children.Add(Text("PedalFeel", 20, FontWeights.SemiBold));
            _autoEnable = Named(new CheckBox {
                Content = Text("Автоматически включать PedalFeel в iRacing", 13), IsChecked = _settings.Enabled,
                Margin = new Thickness(22, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right
            }, "AutoEnable");
            int generation = _generation;
            _autoEnable.Click += (_, __) => { if (_updating || generation != _generation) return; _settings.Enabled = _autoEnable.IsChecked == true; Apply(); };
            Put(heading, _autoEnable, 1); body.Children.Add(heading);
            body.Children.Add(Note("Запустили iRacing → PedalFeel. Закрыли iRacing → SimHub.", 5));
            body.Children.Add(Note("Нужен запущенный симулятор: одного выбора игры в SimHub недостаточно. Возврат после закрытия — до 4 с."));
            var statusRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); statusRow.ColumnDefinitions.Add(new ColumnDefinition());
            _statusMarker = Text("●", 13, FontWeights.SemiBold);
            _statusTitle = Named(Text("", 14, FontWeights.SemiBold), "StatusTitle");
            Put(statusRow, _statusMarker, 0); Put(statusRow, _statusTitle, 1);
            if (_previewEffect != null) {
                statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var stopPreview = Named(Button("Остановить пример"), "StopPreview"); stopPreview.Margin = new Thickness(10, 0, 0, 0);
                stopPreview.ToolTip = L10n.T("Останавливает только пример; эффекты игры продолжают работать.");
                stopPreview.Click += (_, __) => { if (generation == _generation) { CancelPreview(); RefreshState(); } };
                Put(statusRow, stopPreview, 2);
            }
            body.Children.Add(statusRow);
            _statusDetail = Named(Note("", 3), "StatusDetail"); _statusDetail.Margin = new Thickness(20, 3, 0, 3); body.Children.Add(_statusDetail);
            var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var stop = Named(Button("Вернуть управление SimHub"), "Stop"); stop.Click += (_, __) => RunAction(_stop); actions.Children.Add(stop);
            var stopHint = Note("Отключит автоматический режим."); stopHint.VerticalAlignment = VerticalAlignment.Center; stopHint.Margin = new Thickness(12, 4, 0, 4); actions.Children.Add(stopHint); body.Children.Add(actions);
            _feedback = Named(Note("", 3), "ActionError"); body.Children.Add(_feedback);
            _statusDiagnostic = Named(Note(""), "StatusDiagnostic");
            _diagnosticsExpander = Named(new Expander { Header = L10n.T("Подробности состояния"), IsExpanded = _diagnosticsOpen,
                Content = new ScrollViewer { Content = _statusDiagnostic, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Margin = new Thickness(0, 4, 0, 0) }, "DiagnosticsExpander");
            body.Children.Add(_diagnosticsExpander); return Card(body, 0);
        }
        private StackPanel BuildFeelings()
        {
            var page = new StackPanel(); var profile = new StackPanel();
            _carText = Named(Text("", 16, FontWeights.SemiBold), "CurrentCar"); _carText.Margin = new Thickness(0, 0, 0, 4);
            _carHint = Named(Note(""), "CarHint"); _profileSummary = Named(Note(""), "ProfileSummary");
            profile.Children.Add(_carText); profile.Children.Add(_carHint); profile.Children.Add(_profileSummary);
            profile.Children.Add(_profileState == null ? (UIElement)BuildPresetControls() : BuildNamedProfileControls()); page.Children.Add(Card(profile));
            _outputSummary = Named(Text("", 13, FontWeights.SemiBold), "OutputSummary");
            _outputSummary.Margin = new Thickness(0, 8, 0, 0); profile.Children.Add(_outputSummary);
            profile.Children.Add(SliderRow("EffectsGain", "Общая сила эффектов", () => _settings.EffectsGain / PedalFeelSettings.BaseEffectsGain, v => _settings.EffectsGain = v * PedalFeelSettings.BaseEffectsGain,
                "Для выбранного профиля. Усиливает игровые эффекты; мощность проверки не меняется.", 0, 2, false, .1, v => "×" + v.ToString("0.0", UiCulture), "Диапазон: ×0–×2."));
            if (_previewEffect != null) profile.Children.Add(BuildPreviewNotice());
            var brake = CardBody("Тормоз");
            brake.Children.Add(SliderRow("Strength", "Сила тормозных эффектов", () => _settings.Strength, v => _settings.Strength = v, "Общая сила сигналов тормоза, включая ABS и блокировку колёс.", previews: new[] { EffectPreviewKind.BrakeLoading, EffectPreviewKind.Locking }));
            brake.Children.Add(SliderRow("GripThreshold", "Порог предупреждения", () => _settings.GripThreshold, v => _settings.GripThreshold = v, "Меньше — раньше, больше — позже. Предел сцепления оценивается по телеметрии.", .75, 1.05, false));
            brake.Children.Add(SliderRow("Texture", "Предупреждение о пределе", () => _settings.Texture, v => _settings.Texture = v, "Сила предупреждения о расчётном пределе сцепления передних шин.", previews: new[] { EffectPreviewKind.Threshold }));
            brake.Children.Add(SliderRow("AbsPunch", "Импульсы ABS", () => _settings.AbsPunch, v => _settings.AbsPunch = v, "Сила пульсации, когда iRacing сообщает о работе ABS.", previews: new[] { EffectPreviewKind.Abs }));
            brake.Children.Add(SliderRow("DownshiftKick", "Толчок при понижении", () => _settings.DownshiftKick, v => _settings.DownshiftKick = v, "Короткий толчок на тормозе при переключении на пониженную передачу. 0% — выключено.", previews: new[] { EffectPreviewKind.Downshift })); page.Children.Add(Card(brake));
            var throttle = CardBody("Газ");
            throttle.Children.Add(SliderRow("TractionStrength", "Потеря сцепления сзади", () => _settings.TractionStrength, v => _settings.TractionStrength = v, "Расчётное предупреждение о потере сцепления задней оси; это не прямой сигнал TC.", previews: new[] { EffectPreviewKind.Traction }));
            throttle.Children.Add(SliderRow("EngineTexture", "Вибрация двигателя", () => _settings.EngineTexture, v => _settings.EngineTexture = v, "Обычная вибрация двигателя меняется с оборотами при нажатии газа. Отсечка настраивается отдельно.", previews: new[] { EffectPreviewKind.Engine }));
            throttle.Children.Add(SliderRow("LimiterStrength", "Отсечка", () => _settings.LimiterStrength, v => _settings.LimiterStrength = v, "Импульсы при срабатывании ограничителя оборотов. 0% — выключено.", previews: new[] { EffectPreviewKind.Limiter }));
            throttle.Children.Add(SliderRow("IdleTexture", "Холостой ход", () => _settings.IdleTexture, v => _settings.IdleTexture = v, "Мягкая вибрация работающего двигателя при отпущенном газе.", previews: new[] { EffectPreviewKind.Idle }));
            throttle.Children.Add(SliderRow("ShiftKick", "Толчок при повышении", () => _settings.ShiftKick, v => _settings.ShiftKick = v, "Короткий толчок при переключении на повышенную передачу.", previews: new[] { EffectPreviewKind.Upshift })); page.Children.Add(Card(throttle));
            var surface = CardBody("Дорога");
            surface.Children.Add(SliderRow("SurfaceStrength", "Неровности и поребрики", () => _settings.SurfaceStrength, v => _settings.SurfaceStrength = v, "Ощущения от работы подвески. Сигналы сцепления и ABS имеют приоритет над дорожным фоном.", previews: new[] { EffectPreviewKind.Surface, EffectPreviewKind.Rumble }));
            page.Children.Add(Card(surface)); return page;
        }
        private FrameworkElement BuildNamedProfileControls()
        {
            var body = new StackPanel(); int generation = _generation;
            _profileChoice = Named(new ComboBox { DisplayMemberPath = "Value", SelectedValuePath = "Key", MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch }, "ProfileChoice");
            _profileChoice.SelectionChanged += (_, __) => {
                if (_updating || generation != _generation || _selectProfile == null) return;
                if (_profileChoice.SelectedValue is string id) RunAction(() => _selectProfile(id));
            };
            var selected = Row("Текущий профиль"); Put(selected, _profileChoice, 1); body.Children.Add(selected);
            body.Children.Add(Note("Профиль применяется сразу. Назначение машине сохраняется отдельно."));
            _profileDescriptionText = Named(Note(""), "ProfileDescription"); body.Children.Add(_profileDescriptionText);
            body.Children.Add(Note("Изменения сохраняются автоматически в выбранный профиль и действуют для всех назначенных ему машин."));
            _assignmentInfo = Named(Note(""), "AssignmentStatus"); body.Children.Add(_assignmentInfo);
            var actions = new WrapPanel();
            _assignProfileButton = Named(Button("Назначить текущей машине"), "AssignProfile"); _assignProfileButton.Margin = new Thickness(0, 4, 10, 4);
            _clearAssignmentButton = Named(Button("Отменить назначение"), "ClearAssignment");
            _assignProfileButton.Click += (_, __) => { if (generation == _generation && _assignProfile != null) RunAction(_assignProfile); };
            _clearAssignmentButton.Click += (_, __) => { if (generation == _generation && _clearAssignment != null) RunAction(_clearAssignment); };
            actions.Children.Add(_assignProfileButton); actions.Children.Add(_clearAssignmentButton); body.Children.Add(actions);
            if (_resetProfile != null) {
                var reset = Named(Button("Вернуть настройки основы"), "ResetProfile");
                reset.Margin = new Thickness(0, 4, 0, 4); reset.HorizontalAlignment = HorizontalAlignment.Left;
                reset.ToolTip = L10n.T("Изменения сохраняются автоматически в выбранный профиль и действуют для всех назначенных ему машин.");
                reset.Click += (_, __) => { if (generation == _generation) RunAction(_resetProfile); };
                body.Children.Add(reset);
            }

            var create = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            _profileName = Named(new TextBox { Text = _newProfileName, MaxLength = 80, MinWidth = 150 }, "NewProfileName");
            _profileName.TextChanged += (_, __) => { if (!_updating && generation == _generation) _newProfileName = _profileName.Text; };
            var nameRow = Row("Название профиля"); Put(nameRow, _profileName, 1); create.Children.Add(nameRow);
            _profileBasis = Named(new ComboBox { DisplayMemberPath = "Value", SelectedValuePath = "Key", MinWidth = 150 }, "NewProfileBasis");
            _profileBasis.SelectionChanged += (_, __) => { if (!_updating && generation == _generation && _profileBasis.SelectedValue is string id) _newProfileBasis = id; };
            var basisRow = Row("Основа"); Put(basisRow, _profileBasis, 1); create.Children.Add(basisRow);
            _createAssigned = Named(new CheckBox { Content = Text("Сразу назначить текущей машине", 13), IsChecked = _assignNewProfile, Margin = new Thickness(0, 7, 0, 7) }, "AssignNewProfile");
            _createAssigned.Click += (_, __) => { if (generation == _generation) _assignNewProfile = _createAssigned.IsChecked == true; };
            create.Children.Add(_createAssigned);
            var createButton = Named(Button("Создать профиль"), "CreateProfile"); createButton.HorizontalAlignment = HorizontalAlignment.Left;
            createButton.Click += (_, __) => {
                if (generation != _generation || _createProfile == null) return;
                string name = _profileName.Text.Trim();
                if (string.IsNullOrWhiteSpace(name)) { Feedback(L10n.T("Введите название профиля.")); return; }
                if (!(_profileBasis.SelectedValue is string basis)) return;
                RunAction(() => _createProfile(name, basis, _createAssigned.IsChecked == true));
            };
            create.Children.Add(createButton);
            _createProfileExpander = Named(new Expander { Header = L10n.T("Создать свой профиль"), Content = create, IsExpanded = _createProfileOpen, Margin = new Thickness(0, 6, 0, 0) }, "CreateProfileExpander");
            body.Children.Add(_createProfileExpander); return body;
        }
        private void RefreshNamedProfiles(ProfilePanelState state)
        {
            if (_profileChoice == null || _profileBasis == null || _assignmentInfo == null) return;
            _updating = true;
            try {
                SetOptions(_profileChoice, state.Profiles, state.SelectedId);
                var bases = new List<KeyValuePair<string, string>>();
                foreach (var item in state.Bases) bases.Add(new KeyValuePair<string, string>(item.Key, L10n.T(item.Value)));
                SetOptions(_profileBasis, bases, _newProfileBasis);
                _profileChoice.IsEnabled = _selectProfile != null;
                _assignProfileButton!.IsEnabled = state.CanAssign && _assignProfile != null && !string.IsNullOrEmpty(state.SelectedId) && state.SelectedId != state.AssignedId;
                _clearAssignmentButton!.IsEnabled = state.CanAssign && _clearAssignment != null && !string.IsNullOrEmpty(state.AssignedId);
                _createAssigned!.IsEnabled = state.CanAssign;
                if (!state.CanAssign) { _createAssigned.IsChecked = false; _assignNewProfile = false; }
                if (!state.CanAssign) _assignmentInfo.Text = L10n.T("Загрузите машину в iRacing, чтобы назначить профиль.");
                else if (string.IsNullOrEmpty(state.AssignedId)) _assignmentInfo.Text = L10n.T("Для этой машины профиль не назначен.");
                else {
                    string name = state.AssignedId;
                    foreach (var item in state.Profiles) if (item.Key == state.AssignedId) { name = item.Value; break; }
                    _assignmentInfo.Text = L10n.F("Для этой машины назначен профиль «{0}».", name);
                }
                _profileDescriptionText.Text = state.Description ?? "";
            }
            finally { _updating = false; }
        }
        private static void SetOptions(ComboBox combo, List<KeyValuePair<string, string>> options, string selected)
        {
            var old = combo.ItemsSource as List<KeyValuePair<string, string>>;
            bool same = old != null && old.Count == options.Count;
            if (same) for (int index = 0; index < options.Count; index++) if (!old![index].Equals(options[index])) { same = false; break; }
            if (!same) combo.ItemsSource = new List<KeyValuePair<string, string>>(options);
            if (!Equals(combo.SelectedValue, selected)) combo.SelectedValue = selected;
        }
        private FrameworkElement BuildPreviewNotice()
        {
            var body = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            body.Children.Add(Note("Кнопки «Попробовать» воспроизводят отдельный эффект до 2 секунд с настройками профиля и калибровкой моторов. Это имитация, а не запись конкретной машины."));
            return body;
        }
        private void CancelPreview()
        {
            try { _stopPreview?.Invoke(); }
            catch (Exception error) { Feedback(L10n.F("Не удалось остановить пример: {0}", error.Message)); }
        }
        private string? PreviewUnavailable(EffectPreviewKind effect)
        {
            if (!_settings.Enabled) return L10n.T("Включите «Автоматически включать PedalFeel в iRacing» вверху вкладки.");
            if (!(EffectPreview.UsesBrake(effect) && _settings.BrakeEnabled) && !(EffectPreview.UsesThrottle(effect) && _settings.ThrottleEnabled))
                return L10n.T("Включите выбранную педаль в разделе каналов.");
            return EffectPreview.HasStrength(effect, _settings) ? null : L10n.T("Эффект выключен: увеличьте его силу или общую силу профиля.");
        }
        private FrameworkElement PreviewButtons(EffectPreviewKind[] effects)
        {
            var body = new StackPanel(); var row = new WrapPanel(); body.Children.Add(row); int generation = _generation;
            foreach (var effect in effects) {
                string title = effects.Length == 1 ? L10n.T("Попробовать") : L10n.F("Попробовать: {0}", EffectPreview.Name(effect));
                var button = Named(Button(""), "Preview" + effect); button.Content = "▶ " + title;
                button.FontSize = 11; button.Padding = new Thickness(9, 4, 9, 4); button.Margin = new Thickness(0, 1, 8, 3);
                ToolTipService.SetShowOnDisabled(button, true);
                button.Click += (_, __) => {
                    if (generation != _generation || _previewEffect == null) return;
                    _previewErrorKind = effect; _previewErrorMessage = PreviewUnavailable(effect) ?? "";
                    if (_previewErrorMessage.Length == 0) try { _previewEffect(effect); }
                        catch (Exception error) { _previewErrorMessage = L10n.F("Не удалось воспроизвести пример: {0}", error.Message); }
                    RefreshState();
                };
                row.Children.Add(button); _previewButtons.Add(new KeyValuePair<EffectPreviewKind, Button>(effect, button));
                var message = Named(Note(""), "PreviewMessage" + effect); body.Children.Add(message); _previewMessages[effect] = message;
            }
            return body;
        }
        private void RefreshPreviewButtons()
        {
            foreach (var item in _previewButtons) {
                string? reason = PreviewUnavailable(item.Key); item.Value.IsEnabled = reason == null;
                item.Value.ToolTip = reason ?? L10n.T("Воспроизвести пример эффекта до 2 секунд");
                var message = _previewMessages[item.Key];
                message.Text = _previewErrorKind == item.Key && _previewErrorMessage.Length > 0 ? _previewErrorMessage
                    : _settings.Enabled && !EffectPreview.HasStrength(item.Key, _settings) ? L10n.T("Эффект выключен: увеличьте его силу или общую силу профиля.") : "";
                message.Visibility = message.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        private Expander BuildPresetControls()
        {
            var content = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            _profileDescriptionText = Named(Note(""), "ProfileDescription"); content.Children.Add(_profileDescriptionText);
            content.Children.Add(Note("Это предварительные настройки по типу машины. После заезда их можно подстроить; изменения сохраняются для этой машины."));
            var actions = new WrapPanel(); var options = new List<KeyValuePair<string, string>>();
            foreach (var p in _presets) options.Add(new KeyValuePair<string, string>(p.Key, L10n.T(p.Value)));
            _presetChoice = Named(new ComboBox { ItemsSource = options, DisplayMemberPath = "Value", SelectedValuePath = "Key", MinWidth = 215, MaxWidth = 410, HorizontalAlignment = HorizontalAlignment.Left }, "PresetChoice");
            if (_presets.Count > 0 && _applyPreset != null)
            {
                int selected = _presets.FindIndex(p => p.Key == _presetId); if (selected < 0) selected = _presets.FindIndex(p => p.Key == "auto"); _presetChoice.SelectedIndex = Math.Max(0, selected);
                var row = Row("Стартовый набор"); Put(row, _presetChoice, 1); content.Children.Add(row);
                content.Children.Add(Note("Набор заменит эффекты текущей машины и включит обе педали. Общая калибровка моторов сохранится."));
                var apply = Named(Button("Применить к этой машине"), "ApplyPreset"); apply.Margin = new Thickness(0, 4, 10, 4);
                var action = _applyPreset; int generation = _generation;
                apply.Click += (_, __) => { if (generation != _generation) return; var id = _presetChoice.SelectedValue as string;
                    if (string.IsNullOrWhiteSpace(id)) { Feedback(L10n.T("Выберите стартовый набор.")); return; } _presetId = id!; RunAction(() => action(id!)); };
                actions.Children.Add(apply);
            }
            if (_resetProfile != null) { var reset = Named(Button("Вернуть стартовые настройки машины"), "ResetPreset"); reset.Click += (_, __) => RunAction(_resetProfile); actions.Children.Add(reset); }
            if (actions.Children.Count > 0) content.Children.Add(actions);
            _presetsExpander = Named(new Expander { Header = L10n.T("Стартовый набор и сброс"), Content = content, IsExpanded = _presetsOpen, Margin = new Thickness(0, 5, 0, 0) }, "PresetsExpander"); return _presetsExpander;
        }
        private StackPanel BuildPedals()
        {
            var page = new StackPanel(); page.Children.Add(Card(BuildCalibrationControls()));
            var channels = CardBody("Какие педали использовать");
            channels.Children.Add(Note(_profileState == null ? "Каналы общие для блока. Включение эффектов каждой педали сохраняется для текущей машины."
                : "Каналы общие для блока. Включение эффектов каждой педали сохраняется в выбранном профиле."));
            channels.Children.Add(ChannelRow("Тормоз", true)); channels.Children.Add(ChannelRow("Газ", false));
            channels.Children.Add(Note("У тормоза и газа должны быть разные каналы.")); page.Children.Add(Card(channels)); return page;
        }
        private StackPanel BuildCalibrationControls()
        {
            var body = CardBody("Мощность по частотам");
            var selector = new WrapPanel { Margin = new Thickness(0, 3, 0, 10) };
            var pedalLabel = Text("Педаль", 13); pedalLabel.Margin = new Thickness(0, 6, 0, 0); selector.Children.Add(pedalLabel);
            _testPedal = Named(new ComboBox { Width = 160, Margin = new Thickness(12, 0, 0, 0) }, "TestPedal");
            _testPedal.Items.Add(L10n.T("Тормоз")); _testPedal.Items.Add(L10n.T("Газ")); _testPedal.SelectedIndex = Math.Min(1, _testPedalIndex); selector.Children.Add(_testPedal); body.Children.Add(selector);
            _brakeCurve = Curve("Brake", () => _settings.BrakeMinimum, () => _settings.BrakeMaximum);
            _throttleCurve = Curve("Throttle", () => _settings.ThrottleMinimum, () => _settings.ThrottleMaximum);
            body.Children.Add(_brakeCurve); body.Children.Add(_throttleCurve);
            _testError = Named(Text("", 13, FontWeights.SemiBold), "TestError"); _testError.Margin = new Thickness(0, 5, 0, 3); body.Children.Add(_testError);
            _testHint = Named(Note("", 3), "TestAvailability"); body.Children.Add(_testHint);
            int generation = _generation;
            _testPedal.SelectionChanged += (_, __) => { if (generation != _generation) return; RefreshSelectedPedal(); RefreshTestAvailability(); };
            RefreshSelectedPedal(); return body;
        }
        private void RefreshSelectedPedal()
        {
            bool brake = _testPedal.SelectedIndex == 0;
            _brakeCurve.Visibility = brake ? Visibility.Visible : Visibility.Collapsed;
            _throttleCurve.Visibility = brake ? Visibility.Collapsed : Visibility.Visible;
        }
        private void RunTest(bool brake, int point, double power)
        {
            TestFeedback(""); string? reason = TestUnavailableReason(brake, power);
            if (reason != null) { TestFeedback(reason); return; }
            int[] frequencies = { 16, 25, 35, 50 };
            try { _test(brake ? _settings.BrakeChannel : _settings.ThrottleChannel, frequencies[point], (int)Math.Round(power)); }
            catch (Exception error) { TestFeedback(L10n.F("Последняя проверка не выполнена: {0}", error.Message)); }
            finally { RefreshState(); }
        }
        private string? TestUnavailableReason(bool brake, double power)
        {
            if (!_settings.Enabled) return L10n.T("Для проверки включите автоматический режим вверху. Запускать iRacing не нужно.");
            if (!(brake ? _settings.BrakeEnabled : _settings.ThrottleEnabled))
                return L10n.F(_profileState == null ? "Включите эффекты педали «{0}» для этой машины в блоке ниже."
                    : "Включите эффекты педали «{0}» для этого профиля в блоке ниже.", L10n.T(brake ? "Тормоз" : "Газ"));
            if (power < 1) return L10n.T("При мощности 0% мотор не вибрирует. Укажите мощность выше 0%.");
            return null;
        }
        private void RefreshTestAvailability()
        {
            if (_testPedal == null || _testHint == null) return;
            string? reason = TestUnavailableReason(_testPedal.SelectedIndex == 0, 1);
            _testHint.Text = reason ?? ""; _testHint.Visibility = reason == null ? Visibility.Collapsed : Visibility.Visible;
        }
        private FrameworkElement ChannelRow(string label, bool brake)
        {
            var row = new Grid { Margin = new Thickness(0, 9, 0, 3) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            var enabled = Named(new CheckBox { Content = new TextBlock { Text = L10n.F(_profileState == null ? "{0} · эффекты для этой машины" : "{0} · эффекты профиля", L10n.T(label)), TextWrapping = TextWrapping.Wrap },
                IsChecked = brake ? _settings.BrakeEnabled : _settings.ThrottleEnabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }, brake ? "BrakeEnabled" : "ThrottleEnabled");
            var channel = Named(new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch }, brake ? "BrakeChannel" : "ThrottleChannel");
            foreach (string item in new[] { "Сцепление (0)", "Тормоз (1)", "Газ (2)" }) channel.Items.Add(L10n.T(item));
            channel.SelectedIndex = brake ? _settings.BrakeChannel : _settings.ThrottleChannel; int generation = _generation;
            enabled.Click += (_, __) => { if (_updating || generation != _generation) return; if (brake) _settings.BrakeEnabled = enabled.IsChecked == true; else _settings.ThrottleEnabled = enabled.IsChecked == true; Apply(); };
            channel.SelectionChanged += (_, __) => {
                if (_updating || generation != _generation || channel.SelectedIndex < 0) return;
                int selected = channel.SelectedIndex, other = brake ? _settings.ThrottleChannel : _settings.BrakeChannel;
                if (selected == other) { _updating = true; channel.SelectedIndex = brake ? _settings.BrakeChannel : _settings.ThrottleChannel; _updating = false;
                    Feedback(L10n.T("Этот канал уже назначен другой педали. Выберите свободный канал.")); return; }
                if (brake) _settings.BrakeChannel = selected; else _settings.ThrottleChannel = selected; Apply();
            };
            Put(row, enabled, 0); Put(row, channel, 1); return row;
        }
        private FrameworkElement SliderRow(string name, string label, Func<double> get, Action<double> set,
            string description, double minimum = 0, double maximum = 1, bool percentage = true, double step = .01, Func<double, string>? formatValue = null, string? noteSuffix = null, EffectPreviewKind[]? previews = null)
        {
            var block = new StackPanel { Margin = new Thickness(0, 3, 0, 8) }; var row = Row(label); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            var slider = Named(new Slider { Minimum = minimum, Maximum = maximum, Value = get(), SmallChange = step, LargeChange = step * 5, TickFrequency = step,
                IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = L10n.T(description) }, name);
            Func<double, string> format = formatValue ?? (v => percentage ? (v * 100).ToString("0", UiCulture) + "%" : v.ToString("0.00", UiCulture));
            var valueText = Text(format(slider.Value), 13, FontWeights.SemiBold); valueText.HorizontalAlignment = HorizontalAlignment.Right; valueText.VerticalAlignment = VerticalAlignment.Center;
            if (name == "EffectsGain") Named(valueText, "EffectsGainValue");
            int generation = _generation; slider.ValueChanged += (_, __) => { if (_updating || generation != _generation) return; valueText.Text = format(slider.Value); set(slider.Value); Apply(); };
            Put(row, slider, 1); Put(row, valueText, 2); block.Children.Add(row);
            var note = Note(description); if (noteSuffix != null) note.Text += " " + L10n.T(noteSuffix); block.Children.Add(note);
            if (_previewEffect != null && previews != null) block.Children.Add(PreviewButtons(previews));
            return block;
        }
        private FrameworkElement Curve(string prefix, Func<double[]> minimum, Func<double[]> maximum)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Put(grid, Note("Частота"), 0); Put(grid, Note("Минимум, %"), 1); Put(grid, Note("Максимум, %"), 2);
            int[] frequencies = { 16, 25, 35, 50 };
            for (int index = 0; index < frequencies.Length; ++index)
            {
                int point = index, generation = _generation; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = Text("", 12); label.Text = L10n.F("{0} Гц", frequencies[point]); label.VerticalAlignment = VerticalAlignment.Center; Put(grid, label, 0, point + 1);
                FrameworkElement cell(bool isMinimum) {
                    string name = prefix + (isMinimum ? "Minimum" : "Maximum") + point;
                    var row = new Grid { Margin = new Thickness(0, 5, 10, 5) };
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var slider = Named(new Slider { Minimum = 0, Maximum = 100, Value = isMinimum ? minimum()[point] : maximum()[point],
                        SmallChange = 1, LargeChange = 5, TickFrequency = 1, IsSnapToTickEnabled = true, MinWidth = 55,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, name);
                    var value = Named(Text(FormatPower(slider.Value) + "%", 12), name + "Value"); value.VerticalAlignment = VerticalAlignment.Center; value.HorizontalAlignment = HorizontalAlignment.Right; value.Margin = new Thickness(0, 0, 7, 0);
                    slider.ValueChanged += (_, __) => {
                        if (_updating || generation != _generation) return;
                        double requested = slider.Value, clamped = isMinimum ? Math.Min(requested, maximum()[point]) : Math.Max(requested, minimum()[point]);
                        _updating = true; slider.Value = clamped; _updating = false;
                        value.Text = FormatPower(clamped) + "%";
                        double previous = isMinimum ? minimum()[point] : maximum()[point];
                        if (Math.Abs(previous - clamped) < .00001) return;
                        if (isMinimum) minimum()[point] = clamped; else maximum()[point] = clamped;
                        Apply();
                    };
                    var test = Named(Button("500 мс"), prefix + (isMinimum ? "MinimumTest" : "MaximumTest") + point);
                    test.Content = "▶ " + L10n.T("500 мс");
                    test.FontSize = 11; test.Padding = new Thickness(8, 4, 8, 4); test.Margin = new Thickness(0); test.ToolTip = L10n.T("Проверить это значение на 500 мс");
                    test.Click += (_, __) => { if (generation == _generation) RunTest(prefix == "Brake", point, isMinimum ? minimum()[point] : maximum()[point]); };
                    Put(row, slider, 0); Put(row, value, 1); Put(row, test, 2); return row;
                }
                Put(grid, cell(true), 1, point + 1); Put(grid, cell(false), 2, point + 1);
            }
            return grid;
        }
        private void Apply() { _settings.Normalize(); RunAction(_changed); }
        private void RunAction(Action action)
        {
            try { action(); } catch (Exception error) { Feedback(L10n.F("Не удалось применить действие: {0}", error.Message)); } RefreshState();
        }
        private void RefreshState()
        {
            if (_autoEnable == null) return;
            if (!_updating && _lastCulture != L10n.CultureName) { CaptureViewState(); Build(); return; }
            _updating = true; _autoEnable.IsChecked = _settings.Enabled; _updating = false;
            try
            {
                PanelStatus? status = _presentationStatus?.Invoke(); string fallback = status == null ? (_status() ?? "") : "";
                _outputSummary.Text = status?.OutputSummary ?? ""; _outputSummary.Visibility = string.IsNullOrWhiteSpace(_outputSummary.Text) ? Visibility.Collapsed : Visibility.Visible;
                _statusTitle.Text = status?.Title ?? FirstLine(fallback); _statusDetail.Text = status?.Detail ?? ""; _statusDiagnostic.Text = status?.Diagnostic ?? fallback;
                StatusTone tone = status?.Tone ?? StatusTone.Neutral; _statusMarker.Text = tone == StatusTone.Error || tone == StatusTone.Warning ? "!" : "●"; _statusMarker.Foreground = StatusBrush(tone);
            }
            catch (Exception error)
            {
                _outputSummary.Text = ""; _outputSummary.Visibility = Visibility.Collapsed;
                _statusTitle.Text = L10n.T("Не удалось прочитать состояние");
                _statusDetail.Text = L10n.T("Откройте подробности состояния. При необходимости верните управление SimHub.");
                _statusDiagnostic.Text = error.Message; _statusMarker.Text = "!"; _statusMarker.Foreground = StatusBrush(StatusTone.Error);
            }
            _statusDetail.Visibility = string.IsNullOrWhiteSpace(_statusDetail.Text) ? Visibility.Collapsed : Visibility.Visible;
            try
            {
                ProfilePanelState? profile = _profileState?.Invoke();
                string? car = profile?.CurrentCar ?? _currentCar?.Invoke(); bool known = !string.IsNullOrWhiteSpace(car);
                _carText.Text = known ? L10n.F("Машина: {0}", car!) : L10n.T("Машина ещё не выбрана");
                _carHint.Text = profile != null ? "" : known ? L10n.T("Изменения сохраняются автоматически для этой машины.")
                    : L10n.T("Загрузите машину в iRacing, чтобы настроить её ощущения. Пока редактируются настройки без привязки к машине; новым машинам назначаются свои стартовые наборы.");
                if (profile != null) RefreshNamedProfiles(profile);
            }
            catch (Exception error) {
                _carText.Text = L10n.T("Машина ещё не выбрана"); _carHint.Text = L10n.T("Ожидание данных о машине из iRacing.");
                if (_profileState != null) _profileDescriptionText.Text = L10n.F("Не удалось прочитать профили: {0}", error.Message);
            }
            _carHint.Visibility = string.IsNullOrWhiteSpace(_carHint.Text) ? Visibility.Collapsed : Visibility.Visible;
            if (_profileState == null) {
                try { _profileDescriptionText.Text = _profileDescription?.Invoke() ?? ""; }
                catch { _profileDescriptionText.Text = L10n.T("Не удалось получить описание стартового набора."); }
            }
            _profileSummary.Text = _profileState == null ? FirstLine(_profileDescriptionText.Text) : "";
            _profileSummary.Visibility = string.IsNullOrWhiteSpace(_profileSummary.Text) ? Visibility.Collapsed : Visibility.Visible;
            _profileDescriptionText.Visibility = string.IsNullOrWhiteSpace(_profileDescriptionText.Text) ? Visibility.Collapsed : Visibility.Visible;
            _feedback.Text = _feedbackMessage; _feedback.Visibility = string.IsNullOrEmpty(_feedbackMessage) || DateTime.UtcNow >= _feedbackUntil ? Visibility.Collapsed : Visibility.Visible;
            _testError.Text = _testErrorMessage; _testError.Visibility = string.IsNullOrEmpty(_testErrorMessage) ? Visibility.Collapsed : Visibility.Visible;
            RefreshTestAvailability(); RefreshPreviewButtons();
        }
        private void Feedback(string message)
        {
            _feedbackMessage = message; _feedbackUntil = DateTime.UtcNow.AddSeconds(15);
            if (_feedback != null) { _feedback.Text = message; _feedback.Visibility = Visibility.Visible; }
        }
        private void TestFeedback(string message)
        {
            _testErrorMessage = message;
            if (_testError != null) { _testError.Text = message; _testError.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible; }
        }
        private Brush StatusBrush(StatusTone tone)
        {
            if (tone == StatusTone.Active) return TryFindResource("AccentColorBrush") as Brush ?? Brushes.SteelBlue;
            if (tone == StatusTone.Error) return new SolidColorBrush(Color.FromRgb(202, 121, 121));
            if (tone == StatusTone.Warning) return new SolidColorBrush(Color.FromRgb(185, 158, 100));
            return TryFindResource("TextBrush") as Brush ?? SystemColors.ControlTextBrush;
        }
        private Border Card(UIElement content, double bottom = 12)
        {
            Brush line = (TryFindResource("TextBrush") as Brush ?? SystemColors.ControlTextBrush).CloneCurrentValue(); line.Opacity = .18;
            return new Border { Child = content, Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, bottom), BorderThickness = new Thickness(1),
                BorderBrush = line, CornerRadius = new CornerRadius(3), Background = Brushes.Transparent };
        }
        private static StackPanel CardBody(string title)
        {
            var body = new StackPanel(); var heading = Text(title, 16, FontWeights.SemiBold); heading.Margin = new Thickness(0, 0, 0, 8); body.Children.Add(heading); return body;
        }
        private static ScrollViewer PageScroll(UIElement content, string name) => Named(new ScrollViewer {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 12, 8, 0), Background = Brushes.Transparent }, name);
        private static CultureInfo UiCulture
        {
            get { try { return CultureInfo.GetCultureInfo(L10n.CultureName); } catch (CultureNotFoundException) { return CultureInfo.CurrentCulture; } }
        }
        private static string FormatPower(double value) => value.ToString("0.#", UiCulture);
        private static string FirstLine(string text)
        {
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) if (!string.IsNullOrWhiteSpace(line)) return line.Trim(); return "";
        }
        // All static UI keys pass through these helpers; provider strings are assigned directly.
        private static TextBlock Note(string text, double top = 2)
        {
            var block = Text(text, 12); block.Opacity = .78; block.Margin = new Thickness(0, top, 0, 4); return block;
        }
        private static TextBlock Text(string text, double size, FontWeight? weight = null)
        {
            var block = new TextBlock { Text = L10n.T(text), FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); return block;
        }
        private static Grid Row(string label)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(202) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var text = Text(label, 13); text.VerticalAlignment = VerticalAlignment.Center; text.Margin = new Thickness(0, 0, 10, 0); Put(row, text, 0); return row;
        }
        private static Button Button(string title) => new Button { Content = L10n.T(title), Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 4, 0, 4) };
        private static T Named<T>(T element, string name) where T : FrameworkElement { element.Name = name; AutomationProperties.SetAutomationId(element, name); return element; }
        private static void Put(Grid grid, UIElement element, int column, int row = 0) { Grid.SetColumn(element, column); Grid.SetRow(element, row); grid.Children.Add(element); }
    }
}
