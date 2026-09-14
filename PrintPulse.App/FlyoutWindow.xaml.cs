using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Shell;
using Forms = System.Windows.Forms;

namespace PrintPulse;
public partial class FlyoutWindow : Window
{
    public Action? OpenSettings { get; set; }
    public Action? RefreshRequested { get; set; }
    private DateTime lastDismiss;
    public FlyoutWindow()
    {
        InitializeComponent();
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(1), CornerRadius = new CornerRadius(10) });
        SourceInitialized += (_, _) => { var value = 2; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref value, 4); };
        Deactivated += (_, _) => { lastDismiss = DateTime.UtcNow; Hide(); };
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }
    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        // Deactivation precedes the tray MouseClick callback when the visible panel is clicked away.
        if (DateTime.UtcNow - lastDismiss < TimeSpan.FromMilliseconds(220)) return;
        Open();
    }
    public void Open()
    {
        var point = Forms.Cursor.Position;
        var screen = Forms.Screen.FromPoint(point);
        var handle = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(handle, IntPtr.Zero, screen.WorkingArea.Right - 20, screen.WorkingArea.Bottom - 20, 0, 0, 0x0001 | 0x0004 | 0x0010);
        var dpi = GetDpiForWindow(handle) / 96d;
        var work = screen.WorkingArea;
        Width = Math.Min(510, work.Width / dpi - 20);
        var count = (DataContext as MonitorViewModel)?.VisiblePrinters.Count ?? 0;
        Height = Math.Min(Math.Max(300, 104 + count * 143), work.Height / dpi - 24);
        Show(); UpdateLayout();
        var w = (int)Math.Ceiling(ActualWidth * dpi); var h = (int)Math.Ceiling(ActualHeight * dpi);
        var x = work.Right - w - (int)(12 * dpi); var y = work.Bottom - h - (int)(12 * dpi);
        if (work.Left > screen.Bounds.Left) x = work.Left + (int)(12 * dpi);
        if (work.Top > screen.Bounds.Top) y = work.Top + (int)(12 * dpi);
        SetWindowPos(handle, new IntPtr(-1), Math.Max(work.Left, x), Math.Max(work.Top, y), w, h, 0x0040);
        Activate();
    }
    private void SettingsClick(object sender, RoutedEventArgs e) => OpenSettings?.Invoke();
    private void RefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
