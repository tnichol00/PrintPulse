using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Threading;
using PrintPulse.Core;

namespace PrintPulse;
public sealed class MonitorViewModel : INotifyPropertyChanged, IDisposable
{
    public ObservableCollection<Printer> Printers { get; } = [];
    public ObservableCollection<Printer> VisiblePrinters { get; } = [];
    public Preferences Preferences { get; }
    public Session? Session { get; private set; }
    public string Account => Session?.Email ?? "Signed out";
    public string ConnectionText { get; private set; } = "Sign in to connect your printers";
    public bool Connected { get; private set; }
    public int Generation { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string, string>? Notification;
    private readonly Dispatcher dispatcher;
    private readonly CredentialStore credentials;
    private readonly SettingsStore settings;
    private readonly ThumbnailCache thumbnails;
    private CancellationTokenSource? connection;
    private Task? run;
    private readonly DispatcherTimer staleTimer;
    private DateTimeOffset connectedSince;
    private readonly Func<BambuApi> apiFactory;
    public MonitorViewModel(string directory, Dispatcher dispatcher, Func<BambuApi>? apiFactory = null)
    {
        this.dispatcher = dispatcher;
        this.apiFactory = apiFactory ?? (() => new BambuApi());
        credentials = new(directory); settings = new(directory); thumbnails = new(System.IO.Path.Combine(directory, "thumbnails"));
        Preferences = settings.Load();
        staleTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => CheckStale(), dispatcher);
    }
    public void Resume() { Session = credentials.Load(); Changed(); if (Session != null) Refresh(); }
    public void Accept(Session session, int generation)
    {
        if (generation != Generation) return;
        credentials.Save(session); Session = session; Changed(); Refresh();
    }
    public void Refresh()
    {
        StopConnection();
        Generation++;
        if (Session == null) return;
        var epoch = Generation; var session = Session;
        var cts = new CancellationTokenSource(); connection = cts;
        run = Task.Run(async () =>
        {
            using (var api = apiFactory())
            {
                var monitor = new CloudMonitor(api);
                void OnUI(Action action) { if (!cts.IsCancellationRequested) dispatcher.BeginInvoke(() => { if (Generation == epoch && !cts.IsCancellationRequested) action(); }); }
                monitor.Devices += devices => OnUI(() => ApplyDevices(devices));
                monitor.Report += (id, report) => OnUI(() => ApplyReport(id, report));
                monitor.Connection += (text, connected) => OnUI(() => { if (connected && !Connected) connectedSince = DateTimeOffset.UtcNow; Connected = connected; ConnectionText = text; Changed(); });
                monitor.TaskMetadata += tasks => OnUI(() => { _ = ApplyThumbnails(tasks, epoch, cts.Token); });
                try { await monitor.Run(session, cts.Token); }
                catch (OperationCanceledException) { }
                catch { OnUI(() => { Connected = false; ConnectionText = "Connection interrupted. Use Refresh to retry."; Changed(); }); }
            }
        });
    }
    public void ApplyDevices(JsonElement[] devices)
    {
        var ids = new HashSet<string>();
        foreach (var device in devices)
        {
            var id = Printer.Text(device, "dev_id"); if (string.IsNullOrWhiteSpace(id)) continue;
            ids.Add(id);
            var printer = Printers.FirstOrDefault(x => x.Id == id);
            var existing = printer != null;
            if (printer == null) { printer = new Printer { Id = id }; Printers.Add(printer); }
            printer.Name = Printer.Text(device, "name") ?? Printer.Text(device, "dev_product_name") ?? "Bambu printer";
            printer.Model = Printer.Text(device, "dev_product_name") ?? Printer.Text(device, "dev_model_name") ?? "Bambu Lab";
            var before = printer.State;
            if (device.TryGetProperty("online", out var online) && online.ValueKind is JsonValueKind.True or JsonValueKind.False) printer.SetOnline(online.GetBoolean());
            if (existing) Notify(printer, before);
            printer.Changed();
        }
        foreach (var removed in Printers.Where(x => !ids.Contains(x.Id)).ToArray()) Printers.Remove(removed);
        Refilter();
    }
    public void ApplyReport(string id, JsonElement report)
    {
        var printer = Printers.FirstOrDefault(x => x.Id == id); if (printer == null) return;
        var before = printer.State; printer.Apply(report, DateTimeOffset.Now); Notify(printer, before);
    }
    private void Notify(Printer printer, PrintState before)
    {
        var title = Events.Transition(before, printer.State, Preferences);
        if (title != null) Notification?.Invoke(title, printer.Name + " · " + printer.JobLabel);
    }
    private void CheckStale()
    {
        if (!Connected || DateTimeOffset.UtcNow - connectedSince < TimeSpan.FromMinutes(3)) return;
        foreach (var printer in Printers.Where(p => p.Active && p.LastReport is {} last && DateTimeOffset.UtcNow - last > TimeSpan.FromMinutes(3)))
        { var before = printer.State; printer.SetOnline(false); Notify(printer, before); }
    }
    private async Task ApplyThumbnails(JsonElement[] tasks, int epoch, CancellationToken ct)
    {
        try
        {
            foreach (var printer in Printers.ToArray())
            {
                var taskId = printer.TaskId;
                if (string.IsNullOrEmpty(taskId) || taskId == "0" || printer.Thumbnail != null) continue;
                var cover = TaskThumbnail.FindCover(tasks, printer.Id, taskId);
                if (cover == null) continue;
                var path = await Task.Run(() => thumbnails.Get(cover, ct), ct);
                if (Generation != epoch || ct.IsCancellationRequested) return;
                if (printer.TaskId == taskId) printer.SetThumbnail(path);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* Thumbnail failure leaves the built-in placeholder. */ }
    }
    public void SavePreferences() { settings.Save(Preferences); Refilter(); }
    public void Refilter()
    {
        var wanted = Printers.Where(p => !Preferences.HiddenPrinters.Contains(p.Id)).ToArray();
        foreach (var old in VisiblePrinters.Where(p => !wanted.Contains(p)).ToArray()) VisiblePrinters.Remove(old);
        foreach (var printer in wanted) if (!VisiblePrinters.Contains(printer)) VisiblePrinters.Add(printer);
    }
    public void SignOut()
    {
        Generation++; StopConnection();
        credentials.Clear(); Session = null; Connected = false; Printers.Clear(); VisiblePrinters.Clear();
        ConnectionText = "Signed out · Sign in through Settings"; Changed();
        try { thumbnails.Clear(); } catch (System.IO.IOException) { }
    }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    private void StopConnection()
    {
        var old = connection; connection = null;
        if (old == null) return;
        old.Cancel();
        if (run is {} pending) _ = pending.ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
        else old.Dispose();
    }
    public void Dispose() { Generation++; StopConnection(); staleTimer.Stop(); thumbnails.Dispose(); }
}
