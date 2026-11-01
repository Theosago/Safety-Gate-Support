using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace SafetyGateSupport;

/// <summary>Click-to-view drawing surface. Wheel zooms at the cursor; drag pans; double-click fits.</summary>
public sealed class DrawingZoomWindow : Window
{
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private double _zoom = 1; private Point _dragStart; private double _left, _top; private bool _dragging;
    public double Zoom => _zoom;
    public DrawingZoomWindow(string title, Image preview)
    {
        Title = title; WindowState = WindowState.Maximized; Background = Brushes.White; MinWidth = 700; MinHeight = 500;
        var panel = new DockPanel(); var bar = new DockPanel { Margin = new Thickness(14, 8, 14, 8) };
        var close = new Button { Content = "Close · Esc", HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(close, Dock.Right); bar.Children.Add(close);
        bar.Children.Add(new TextBlock { Text = "Mouse wheel to zoom • drag to pan • double-click to fit", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(bar, Dock.Top); panel.Children.Add(bar); _scroll.Content = _image; panel.Children.Add(_scroll); Content = panel;
        close.Click += (_, _) => Close(); PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        _image.SetBinding(Image.SourceProperty, new Binding(nameof(Image.Source)) { Source = preview, NotifyOnTargetUpdated = true });
        _image.TargetUpdated += (_, _) => { if (IsLoaded) Fit(); };
        Loaded += (_, _) => Fit(); _scroll.PreviewMouseWheel += Wheel;
        _scroll.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Fit(); e.Handled = true; return; }
            _dragStart = e.GetPosition(_scroll); _left = _scroll.HorizontalOffset; _top = _scroll.VerticalOffset;
            _dragging = _scroll.CaptureMouse(); _scroll.Cursor = Cursors.SizeAll; e.Handled = true;
        };
        _scroll.PreviewMouseMove += (_, e) => { if (_dragging && e.LeftButton == MouseButtonState.Pressed) { Point p = e.GetPosition(_scroll); _scroll.ScrollToHorizontalOffset(_left - (p.X - _dragStart.X)); _scroll.ScrollToVerticalOffset(_top - (p.Y - _dragStart.Y)); } };
        _scroll.PreviewMouseLeftButtonUp += (_, e) => { _dragging = false; _scroll.ReleaseMouseCapture(); _scroll.Cursor = Cursors.Arrow; e.Handled = true; };
    }
    public void Fit()
    {
        if (_image.Source is not BitmapSource bitmap) return;
        _scroll.UpdateLayout();
        _zoom = Math.Max(0.01, Math.Min(Math.Max(1, _scroll.ViewportWidth - 24) / bitmap.PixelWidth, Math.Max(1, _scroll.ViewportHeight - 24) / bitmap.PixelHeight));
        ResizeImage(bitmap); _scroll.ScrollToHorizontalOffset(0); _scroll.ScrollToVerticalOffset(0);
    }
    public void ZoomAt(double factor, Point cursor)
    {
        if (_image.Source is not BitmapSource bitmap) return;
        double oldWidth = bitmap.PixelWidth * _zoom, oldHeight = bitmap.PixelHeight * _zoom;
        double x = (_scroll.HorizontalOffset + cursor.X - Math.Max(0, (_scroll.ViewportWidth - oldWidth) / 2)) / _zoom;
        double y = (_scroll.VerticalOffset + cursor.Y - Math.Max(0, (_scroll.ViewportHeight - oldHeight) / 2)) / _zoom;
        _zoom = Math.Clamp(_zoom * factor, 0.02, 12); ResizeImage(bitmap); _scroll.UpdateLayout();
        _scroll.ScrollToHorizontalOffset(x * _zoom - cursor.X + Math.Max(0, (_scroll.ViewportWidth - _image.Width) / 2));
        _scroll.ScrollToVerticalOffset(y * _zoom - cursor.Y + Math.Max(0, (_scroll.ViewportHeight - _image.Height) / 2));
    }
    private void ResizeImage(BitmapSource bitmap) { _image.Width = bitmap.PixelWidth * _zoom; _image.Height = bitmap.PixelHeight * _zoom; }
    private void Wheel(object sender, MouseWheelEventArgs e) { ZoomAt(Math.Pow(1.2, e.Delta / 120d), e.GetPosition(_scroll)); e.Handled = true; }
}
