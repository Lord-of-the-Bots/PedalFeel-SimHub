using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PedalFeel.SimHub
{
    /// <summary>
    /// Shows the same explanation over each stock settings pane. The owner disables and
    /// restores the underlying control; this helper never changes its content or bindings.
    /// </summary>
    internal sealed class StockControlLockAdorner : IDisposable
    {
        private readonly FrameworkElement _root;
        private Grid _notice;
        private readonly Action _returnToStock;
        private string _culture;
        private AdornerLayer? _layer;
        private LockAdorner? _adorner;
        private Grid? _fallbackParent;
        private Canvas? _fallbackHost;
        private bool _disposed;

        private StockControlLockAdorner(FrameworkElement root, Action returnToStock)
        {
            _root = root;
            _returnToStock = returnToStock;
            _culture = L10n.CultureName;
            _notice = CreateNotice(returnToStock);
            _root.Loaded += RootLoaded;
            _root.Unloaded += RootUnloaded;
            _root.IsVisibleChanged += RootVisibilityChanged;
            _root.LayoutUpdated += RootLayoutUpdated;
            Refresh();
        }

        public static IDisposable Attach(FrameworkElement root, Action returnToStock)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (returnToStock == null) throw new ArgumentNullException(nameof(returnToStock));
            root.Dispatcher.VerifyAccess();
            return new StockControlLockAdorner(root, returnToStock);
        }

        // Useful to verify the optional no-AdornerLayer path without a SimHub device.
        internal bool IsAttached => _adorner != null || _fallbackHost != null;
        internal bool UsesGridFallback => _fallbackHost != null;

        private Grid CreateNotice(Action returnToStock)
        {
            var overlay = new Grid { IsEnabled = true, ClipToBounds = true };
            Resource(overlay, TextElement.ForegroundProperty, "TextBrush", Brushes.White);
            var shade = new Border { Opacity = .91 };
            Resource(shade, Border.BackgroundProperty, "WindowBackgroundBrush", new SolidColorBrush(Color.FromRgb(35, 35, 35)));
            overlay.Children.Add(shade);

            var message = new StackPanel();
            message.Children.Add(new TextBlock
            {
                Text = L10n.T("Педалями управляет PedalFeel"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 9)
            });
            message.Children.Add(new TextBlock
            {
                Text = L10n.T("Настраивайте ощущения на вкладке PedalFeel. После закрытия iRacing эти настройки снова станут доступны; возврат может занять до 4 секунд. При проверке без игры блокировка длится только 0,5 секунды."),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 13)
            });
            var button = new Button
            {
                Content = L10n.T("Вернуть управление SimHub"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 7, 12, 7)
            };
            var failure = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
                Visibility = Visibility.Collapsed
            };
            button.Click += (_, __) =>
            {
                if (_disposed) return;
                try { returnToStock(); }
                catch (Exception error)
                {
                    failure.Text = L10n.F("Не удалось вернуть стандартные эффекты: {0}", error.Message);
                    failure.Visibility = Visibility.Visible;
                }
            };
            message.Children.Add(button);
            message.Children.Add(new TextBlock
            {
                Text = L10n.T("Кнопка также выключит автоматическое включение PedalFeel."),
                FontSize = 12,
                Opacity = .8,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 9, 0, 0)
            });
            message.Children.Add(failure);
            var card = new Border
            {
                Child = message,
                Padding = new Thickness(20),
                Margin = new Thickness(20, 24, 20, 20),
                MaxWidth = 510,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3)
            };
            Resource(card, Border.BackgroundProperty, "WindowBackgroundBrush", new SolidColorBrush(Color.FromRgb(35, 35, 35)));
            Resource(card, Border.BorderBrushProperty, "GrayBrush6", Brushes.Gray);
            overlay.Children.Add(card);
            return overlay;
        }

        private static void Resource(FrameworkElement element, DependencyProperty property, string key, Brush fallback)
        {
            if (Application.Current?.TryFindResource(key) is Brush) element.SetResourceReference(property, key);
            else element.SetValue(property, fallback);
        }

        private void RootLoaded(object sender, RoutedEventArgs args) => Refresh();
        private void RootUnloaded(object sender, RoutedEventArgs args) => DetachVisual();
        private void RootVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Refresh();
        private void RootLayoutUpdated(object? sender, EventArgs args) => Refresh();

        private void Refresh()
        {
            if (_disposed) return;
            string culture = L10n.CultureName;
            if (_culture != culture)
            {
                DetachVisual();
                _culture = culture;
                _notice = CreateNotice(_returnToStock);
            }
            if (!_root.IsVisible)
            {
                if (_adorner != null) _adorner.Visibility = Visibility.Collapsed;
                if (_fallbackHost != null) _fallbackHost.Visibility = Visibility.Collapsed;
                return;
            }

            var layer = AdornerLayer.GetAdornerLayer(_root);
            if (layer != null)
            {
                if (_adorner == null || !ReferenceEquals(_layer, layer))
                {
                    DetachVisual();
                    _layer = layer;
                    _adorner = new LockAdorner(_root, _notice);
                    layer.Add(_adorner);
                }
                _adorner.Visibility = Visibility.Visible;
                _adorner.SetVisibleBounds(VisibleBounds(_root, _root));
                return;
            }

            // A few host templates have no AdornerDecorator. Add a non-layout-affecting
            // sibling to an enabled Grid instead of wrapping/replacing the user's content.
            var parent = FindEnabledGrid(_root);
            if (parent == null) { DetachVisual(); return; }
            if (_fallbackHost == null || !ReferenceEquals(_fallbackParent, parent))
            {
                DetachVisual();
                _fallbackParent = parent;
                _fallbackHost = new Canvas { ClipToBounds = true, IsEnabled = true };
                Grid.SetRowSpan(_fallbackHost, Math.Max(1, parent.RowDefinitions.Count));
                Grid.SetColumnSpan(_fallbackHost, Math.Max(1, parent.ColumnDefinitions.Count));
                Panel.SetZIndex(_fallbackHost, int.MaxValue);
                _fallbackHost.Children.Add(_notice);
                parent.Children.Add(_fallbackHost);
            }
            _fallbackHost.Visibility = Visibility.Visible;
            Rect bounds = VisibleBounds(_root, _fallbackHost);
            _notice.Visibility = bounds.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            if (bounds.IsEmpty) return;
            SetIfChanged(_notice, FrameworkElement.WidthProperty, bounds.Width);
            SetIfChanged(_notice, FrameworkElement.HeightProperty, bounds.Height);
            SetIfChanged(_notice, Canvas.LeftProperty, bounds.Left);
            SetIfChanged(_notice, Canvas.TopProperty, bounds.Top);
        }

        private static void SetIfChanged(DependencyObject target, DependencyProperty property, double value)
        {
            var previous = (double)target.GetValue(property);
            if (double.IsNaN(previous) || Math.Abs(previous - value) > .1) target.SetValue(property, value);
        }

        private static Grid? FindEnabledGrid(DependencyObject root)
        {
            for (DependencyObject? parent = VisualTreeHelper.GetParent(root); parent != null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is Grid grid && grid.IsEnabled) return grid;
            return null;
        }

        // Constrain both implementations to the pane's visible viewport. A tall scrolled
        // page must never cover tab headers or controls outside that page.
        private static Rect VisibleBounds(FrameworkElement root, Visual relativeTo)
        {
            try
            {
                Rect result = root.TransformToVisual(relativeTo).TransformBounds(new Rect(root.RenderSize));
                for (DependencyObject? parent = VisualTreeHelper.GetParent(root); parent != null; parent = VisualTreeHelper.GetParent(parent))
                {
                    if (parent is UIElement element)
                    {
                        if (!element.IsVisible) return Rect.Empty;
                        Rect limit = element.TransformToVisual(relativeTo).TransformBounds(new Rect(element.RenderSize));
                        result.Intersect(limit);
                        if (result.IsEmpty) return Rect.Empty;
                    }
                }
                return result.Width > 0 && result.Height > 0 ? result : Rect.Empty;
            }
            catch (InvalidOperationException) { return Rect.Empty; }
        }

        private void DetachVisual()
        {
            if (_adorner != null)
            {
                _layer?.Remove(_adorner);
                _adorner.ReleaseNotice();
                _adorner = null;
                _layer = null;
            }
            if (_fallbackHost != null)
            {
                _fallbackHost.Children.Remove(_notice);
                _fallbackParent?.Children.Remove(_fallbackHost);
                _fallbackHost = null;
                _fallbackParent = null;
            }
            _notice.ClearValue(FrameworkElement.WidthProperty);
            _notice.ClearValue(FrameworkElement.HeightProperty);
            _notice.ClearValue(Canvas.LeftProperty);
            _notice.ClearValue(Canvas.TopProperty);
            _notice.Visibility = Visibility.Visible;
        }

        public void Dispose()
        {
            if (!_root.Dispatcher.CheckAccess())
            {
                if (!_root.Dispatcher.HasShutdownStarted) _root.Dispatcher.BeginInvoke(new Action(Dispose));
                return;
            }
            if (_disposed) return;
            _disposed = true;
            _root.Loaded -= RootLoaded;
            _root.Unloaded -= RootUnloaded;
            _root.IsVisibleChanged -= RootVisibilityChanged;
            _root.LayoutUpdated -= RootLayoutUpdated;
            DetachVisual();
        }

        private sealed class LockAdorner : Adorner
        {
            private Grid? _notice;
            private Rect _visibleBounds = Rect.Empty;
            public LockAdorner(FrameworkElement adorned, Grid notice) : base(adorned)
            {
                _notice = notice;
                IsEnabled = true;
                IsClipEnabled = true;
                AddVisualChild(notice);
                AddLogicalChild(notice);
            }
            public void SetVisibleBounds(Rect bounds)
            {
                if (_visibleBounds == bounds) return;
                _visibleBounds = bounds;
                InvalidateMeasure();
                InvalidateArrange();
            }
            protected override int VisualChildrenCount => _notice == null ? 0 : 1;
            protected override Visual GetVisualChild(int index)
            {
                if (index != 0 || _notice == null) throw new ArgumentOutOfRangeException(nameof(index));
                return _notice;
            }
            protected override Size MeasureOverride(Size constraint)
            {
                _notice?.Measure(_visibleBounds.IsEmpty ? new Size() : _visibleBounds.Size);
                return AdornedElement.RenderSize;
            }
            protected override Size ArrangeOverride(Size finalSize)
            {
                if (_notice != null)
                {
                    _notice.Visibility = _visibleBounds.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
                    _notice.Arrange(_visibleBounds.IsEmpty ? new Rect() : _visibleBounds);
                }
                return finalSize;
            }
            public void ReleaseNotice()
            {
                if (_notice == null) return;
                RemoveLogicalChild(_notice);
                RemoveVisualChild(_notice);
                _notice = null;
            }
        }
    }
}
