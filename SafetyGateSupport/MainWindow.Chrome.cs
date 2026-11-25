using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SafetyGateSupport;

// The title bar is drawn by the app (WindowChrome). A maximized window is placed by Windows
// slightly beyond the screen edges, so the content is inset by exactly that overhang: the
// caption buttons then sit in the true screen corner and the ✕ is hit at the very top-right pixel.
public partial class MainWindow
{
    private const double TitleBarHeight = 36;

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this);
    }
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeCaptionButton.Content = maximized ? "" : "";
        MaximizeCaptionButton.ToolTip = maximized ? "Restore Down" : "Maximize";
        FitToScreen();
        // Windows finishes placing the window after StateChanged; measure again once it has.
        Dispatcher.InvokeAsync(FitToScreen, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void Window_PlacementChanged(object? sender, EventArgs e) { if (WindowState == WindowState.Maximized) FitToScreen(); }
    private void Window_DpiChanged(object sender, DpiChangedEventArgs e) => Dispatcher.InvokeAsync(FitToScreen, System.Windows.Threading.DispatcherPriority.Loaded);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Window_StateChanged(this, EventArgs.Empty);
    }

    /// <summary>The part of the maximized window that lies outside the monitor's work area, in WPF units.</summary>
    internal Thickness MaximizedOverhang()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (WindowState != WindowState.Maximized || handle == IntPtr.Zero || !GetWindowRect(handle, out var window)) return new Thickness(0);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, MonitorDefaultToNearest), ref info)) return new Thickness(0);
        var toUnits = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var work = info.Work;
        return new Thickness(Math.Max(0, work.Left - window.Left) * toUnits.M11, Math.Max(0, work.Top - window.Top) * toUnits.M22,
            Math.Max(0, window.Right - work.Right) * toUnits.M11, Math.Max(0, window.Bottom - work.Bottom) * toUnits.M22);
    }

    private void FitToScreen()
    {
        if (_isClosed) return;
        var overhang = MaximizedOverhang();
        if (WindowRoot.Margin != overhang) WindowRoot.Margin = overhang;
        Chrome.CaptionHeight = TitleBarHeight + overhang.Top;
    }

    private const uint MonitorDefaultToNearest = 2;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
