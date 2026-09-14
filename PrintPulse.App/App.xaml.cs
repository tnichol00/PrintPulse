using System.IO;
using System.Windows;
using System.Drawing;
using Microsoft.Toolkit.Uwp.Notifications;
using Forms = System.Windows.Forms;

namespace PrintPulse;
public partial class App : System.Windows.Application
{
    private Mutex? mutex;
    private EventWaitHandle? openEvent;
    private EventWaitHandle? settingsEvent;
    private CancellationTokenSource lifetime = new();
    private Forms.NotifyIcon? tray;
    private Icon? icon;
    private FlyoutWindow? flyout;
    private SettingsWindow? settings;
    private MonitorViewModel? model;
    private bool diagnostics;
    private void Trace(string stage)
    {
        if (!diagnostics) return;
        try { var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintPulse"); Directory.CreateDirectory(folder); File.AppendAllText(Path.Combine(folder, "lifecycle.log"), DateTimeOffset.Now.ToString("O") + " " + stage + Environment.NewLine); } catch { }
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        diagnostics = e.Args.Contains("--diagnostics"); Trace("startup");
        mutex = new Mutex(true, @"Local\PrintPulse.Instance", out var first);
        if (!first)
        {
            if (!e.Args.Contains("--startup")) { try { using var existing = EventWaitHandle.OpenExisting(e.Args.Contains("--settings") ? @"Local\PrintPulse.Settings" : @"Local\PrintPulse.Open"); existing.Set(); } catch (WaitHandleCannotBeOpenedException) { } }
            Shutdown(); return;
        }
        openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PrintPulse.Open");
        settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PrintPulse.Settings");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintPulse");
        model = new MonitorViewModel(directory, Dispatcher);
        Trace("model-created");
        flyout = new FlyoutWindow { DataContext = model, OpenSettings = OpenSettings, RefreshRequested = () => model.Refresh() };
        Trace("flyout-created");
        icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open PrintPulse", null, (_, _) => flyout.Open());
        menu.Items.Add("Refresh", null, (_, _) => model.Refresh());
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        var startup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = PrintPulse.Startup.Enabled };
        startup.Click += (_, _) => { try { PrintPulse.Startup.Set(!PrintPulse.Startup.Enabled); startup.Checked = PrintPulse.Startup.Enabled; } catch { OpenSettings(); } };
        menu.Items.Add(startup);
        menu.Opening += (_, _) => startup.Checked = PrintPulse.Startup.Enabled;
        menu.Items.Add("Sign out", null, (_, _) => { try { model.SignOut(); } catch { OpenSettings(); } });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        tray = new Forms.NotifyIcon { Icon = icon, Text = "PrintPulse · Bambu printer monitor", ContextMenuStrip = menu, Visible = true };
        Trace("tray-created");
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) flyout.Toggle(); };
        tray.BalloonTipClicked += (_, _) => flyout.Open();
        model.Notification += (title, body) =>
        {
            try { new ToastContentBuilder().AddText(title).AddText(body).Show(); }
            catch { tray.ShowBalloonTip(5000, title, body, Forms.ToolTipIcon.Info); }
        };
        ToastNotificationManagerCompat.OnActivated += _ => Dispatcher.BeginInvoke(() => flyout.Open());
        Trace("notifications-ready");
        model.Resume();
        Trace("session-resumed");
        _ = Task.Run(() => { while (!lifetime.IsCancellationRequested) { var index = WaitHandle.WaitAny([openEvent, settingsEvent, lifetime.Token.WaitHandle]); if (index == 0) Dispatcher.BeginInvoke(() => flyout.Open()); else if (index == 1) Dispatcher.BeginInvoke(OpenSettings); else break; } });
        if (e.Args.Contains("--settings")) OpenSettings();
        else if (e.Args.Contains("--open")) flyout.Open();
        Trace("startup-complete");
    }
    private void OpenSettings()
    {
        if (settings == null) { settings = new SettingsWindow(model!) { OpenPanel = () => flyout!.Open() }; settings.Closed += (_, _) => { settings = null; Trace("settings-closed"); }; }
        settings.Show(); settings.Activate();
        Trace("settings-opened");
    }
    protected override void OnExit(ExitEventArgs e)
    {
        lifetime.Cancel(); model?.Dispose(); tray?.Dispose(); icon?.Dispose();
        // Event handle remains valid until the waiting thread observes cancellation; process teardown closes it.
        mutex?.Dispose(); base.OnExit(e);
    }
}

