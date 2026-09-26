using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PedalFeel.SimHub;

internal static class PanelScreenshots
{
    public static void Run(string host)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string uri in new[] { "MahApps.Metro;component/Styles/Controls.xaml", "MahApps.Metro;component/Styles/Fonts.xaml", "MahApps.Metro;component/Styles/Colors.xaml", "MahApps.Metro;component/Styles/Accents/Blue.xaml", "MahApps.Metro;component/Styles/Accents/BaseDark.xaml", "SimHub.Plugins;component/Styles/SimHubStyles.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/" + uri) });
        string directory = Path.GetFullPath("build/ui-0.5.0"); Directory.CreateDirectory(directory);
        foreach (string language in new[] { "en", "ru-RU" }) {
            L10n.OverrideCulture = language;
            using (var repository = new ProfileRepository(Path.Combine(directory, "fixture-" + language + ".json"))) {
                var settings = CarPresets.CreateBasis(CarPresets.Standard); settings.Enabled = true;
                var panel = new PedalFeelPanel(settings, () => {}, () => "", (_, __, ___) => {}, () => {},
                    resetProfile: () => {}, presentationStatus: () => new PanelStatus {
                        Title = L10n.T("Машина ещё не выбрана"), OutputSummary = L10n.T("Тормоз") + ": 0%     |     " + L10n.T("Газ") + ": 0%"
                    }, previewEffect: _ => {}, profileState: repository.PanelState, selectProfile: _ => {}, createProfile: (_, __, ___) => {}, deleteProfile: () => 0);
                var outer = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                var root = new Border { Background = (Brush)app.FindResource("WindowBackgroundBrush"), Child = outer };
                using (var source = new HwndSource(new HwndSourceParameters("PedalFeel render") { Width = 1100, Height = 950, PositionX = -10000, PositionY = -10000, WindowStyle = unchecked((int)0x80000000) })) {
                    source.RootVisual = root; Layout(root); Pump(); panel.InvalidateMeasure(); Layout(root); Pump();
                    Render(root, Path.Combine(directory, language + "-top.png"));
                    var scroll = Tree(panel).OfType<ScrollViewer>().Single(x => x.Name == "FeelingScroll");
                    if (scroll.ViewportHeight <= 0 || scroll.ScrollableHeight <= 0 || outer.ScrollableHeight > 1)
                        throw new Exception("Extension failed to establish an inner viewport");
                    double indicatorTop = Tree(panel).OfType<TextBlock>().Single(x => x.Name == "OutputSummary").TranslatePoint(new Point(), root).Y;
                    scroll.ScrollToEnd(); Layout(root); Pump();
                    double after = Tree(panel).OfType<TextBlock>().Single(x => x.Name == "OutputSummary").TranslatePoint(new Point(), root).Y;
                    if (Math.Abs(after - indicatorTop) > .1) throw new Exception("Output indicator moved with page scroll");
                    Render(root, Path.Combine(directory, language + "-bottom.png"));
                    scroll.ScrollToVerticalOffset(330); Layout(root); Pump();
                    Render(root, Path.Combine(directory, language + "-brake.png"));
                    scroll.ScrollToTop();
                    Tree(panel).OfType<Expander>().Single(x => x.Name == "CreateProfileExpander").IsExpanded = true;
                    Layout(root); Pump(); Render(root, Path.Combine(directory, language + "-create.png"));
                    Tree(panel).OfType<TabControl>().Single(x => x.Name == "MainSections").SelectedIndex = 1;
                    Layout(root); Pump(); Render(root, Path.Combine(directory, language + "-calibration.png"));
                    source.RootVisual = null;
                }
            }
        }
        app.Shutdown(); Console.WriteLine("Rendered both languages; outer viewport and fixed load indicator verified.");
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root) {
        yield return root;
        if (root is Visual) for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Layout(FrameworkElement root) { root.Measure(new Size(1100, 950)); root.Arrange(new Rect(0, 0, 1100, 950)); root.UpdateLayout(); }
    private static void Pump() { var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) }; timer.Tick += (_, __) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    private static void Render(FrameworkElement root, string path) { var bitmap = new RenderTargetBitmap(1100,950,96,96,PixelFormats.Pbgra32); bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(path)) encoder.Save(stream); }
}
