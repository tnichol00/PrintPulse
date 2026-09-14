using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PrintPulse.Core;

namespace PrintPulse.Tests;
public static class Program
{
    static int passed, failed;
    static string output = "";
    static readonly List<string> results = [];
    static JsonElement J(string s) { using var d = JsonDocument.Parse(s); return d.RootElement.Clone(); }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Test(string name, Action test)
    {
        try { test(); results.Add("PASS " + name); passed++; }
        catch (Exception e) { results.Add("FAIL " + name + " · " + e.GetType().Name + ": " + e.Message); failed++; }
    }
    [STAThread] public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--live-readonly") return LiveReadOnly(args.Skip(1).FirstOrDefault());
        output = Path.GetFullPath(args.FirstOrDefault() ?? "test-evidence"); Directory.CreateDirectory(output);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PrintPulse;component/Theme.xaml") });
        Test("malformed JSON values cannot crash the UI reducer", () => { var p = new Printer(); foreach (var value in new[] { "null", "[]", "42", "\"text\"" }) p.Apply(J(value), DateTimeOffset.Now); Check(p.LastReport == null, "Malformed report accepted"); });
        Test("filename delta does not overwrite named job or progress", () => { var p = new Printer(); p.Apply(J("{\"subtask_id\":\"42\",\"subtask_name\":\"Named model\",\"gcode_state\":\"RUNNING\",\"mc_percent\":65,\"layer_num\":50}"), DateTimeOffset.Now); p.Apply(J("{\"gcode_file\":\"Metadata/plate_1.gcode\"}"), DateTimeOffset.Now); Check(p.Job == "Named model" && p.Progress == 65 && p.Layer == 50, "Filename delta reset job"); });
        Test("new task may use a filename fallback", () => { var p = new Printer(); p.Apply(J("{\"subtask_id\":\"1\",\"subtask_name\":\"Old job\"}"), DateTimeOffset.Now); p.Apply(J("{\"subtask_id\":\"2\",\"gcode_file\":\"new.gcode\"}"), DateTimeOffset.Now); Check(p.Job == "new.gcode", "New filename missing"); });
        Test("partial reports preserve progress/layers/job/ETA", () =>
        {
            var p = new Printer(); var now = DateTimeOffset.Now;
            p.Apply(J("{\"subtask_id\":\"1\",\"subtask_name\":\"case\",\"gcode_state\":\"RUNNING\",\"mc_percent\":89,\"layer_num\":1189,\"total_layer_num\":1399,\"mc_remaining_time\":84}"), now);
            p.Apply(J("{\"gcode_state\":\"PAUSE\"}"), now.AddMinutes(2));
            Check(p.Progress == 89 && p.Layer == 1189 && p.Job == "case" && p.EstimatedFinish == now.AddMinutes(84), "Partial update discarded data");
            Check(p.State == PrintState.Paused && p.EtaLabel == "ETA —", "Pause ETA/state incorrect");
        });
        Test("new job clears old metadata and thumbnail", () => { var p = new Printer(); p.Apply(J("{\"subtask_id\":\"1\",\"mc_percent\":89,\"layer_num\":100}"), DateTimeOffset.Now); p.SetThumbnail("old"); p.Apply(J("{\"subtask_id\":\"2\",\"gcode_state\":\"PREPARE\"}"), DateTimeOffset.Now); Check(p.Progress == null && p.Layer == null && p.Thumbnail == null, "Old job metadata remains"); });
        Test("preview selection matches both printer and current print", () =>
        {
            var tasks = new[] { J("{\"deviceId\":\"A\",\"id\":\"old\",\"cover\":\"old-image\"}"), J("{\"deviceId\":\"B\",\"id\":\"current\",\"cover\":\"other-printer-image\"}"), J("{\"deviceId\":\"A\",\"id\":\"current\",\"cover\":\"correct-image\"}") };
            Check(TaskThumbnail.FindCover(tasks, "A", "current") == "correct-image", "Wrong printer/job preview selected");
            Check(TaskThumbnail.FindCover(tasks, "B", "current") == "other-printer-image", "Other printer not independent");
            Check(TaskThumbnail.FindCover(tasks, "A", "new-print") == null && TaskThumbnail.FindCover(tasks, "A", "0") == null, "Old preview substituted for unknown print");
            Check(TaskThumbnail.FindCover([J("{\"deviceId\":\"A\",\"id\":\"old\",\"taskId\":\"current\",\"cover\":\"wrong\"}")], "A", "current") == null, "Alternate task field overrode primary identity");
        });
        Test("offline retains last known progress and restores state", () => { var p = new Printer(); p.Apply(J("{\"gcode_state\":\"RUNNING\",\"mc_percent\":46}"), DateTimeOffset.Now); p.SetOnline(false); Check(p.Progress == 46 && p.State == PrintState.Offline, "Offline reset"); p.SetOnline(true); Check(p.State == PrintState.Printing, "Online restore"); });
        Test("completed and idle semantics", () => { var p = new Printer(); p.Apply(J("{\"gcode_state\":\"FINISH\"}"), DateTimeOffset.Now); Check(p.Progress == 100, "Completion not 100"); p.Apply(J("{\"gcode_state\":\"IDLE\"}"), DateTimeOffset.Now); Check(p.BarValue == 0 && p.Progress == null, "Idle progress remains"); });
        Test("missing, string and excessive numeric fields", () => { var p = new Printer(); p.Apply(J("{\"mc_percent\":\"101\",\"mc_remaining_time\":-1,\"layer_num\":null}"), DateTimeOffset.Now); Check(p.Progress == 100 && p.RemainingMinutes == 0 && p.Layer == null, "Malformed numeric handling"); });
        Test("all required printer state mappings", () => { Check(Printer.ParseState("PAUSE") == PrintState.Paused && Printer.ParseState("PREPARE") == PrintState.Preparing && Printer.ParseState("FAILED") == PrintState.Error && Printer.ParseState("OFFLINE") == PrintState.Offline && Printer.ParseState("unexpected") == PrintState.Unknown, "State mapping mismatch"); });
        Test("event notifications exclude progress, initial state and pause resume", () => { var prefs = new Preferences(); Check(Events.Transition(PrintState.Printing, PrintState.Printing, prefs) == null && Events.Transition(PrintState.Unknown, PrintState.Printing, prefs) == null && Events.Transition(PrintState.Paused, PrintState.Printing, prefs) == null, "Notification spam"); Check(Events.Transition(PrintState.Idle, PrintState.Printing, prefs) == "Print started" && Events.Transition(PrintState.Printing, PrintState.Completed, prefs) == "Print completed" && Events.Transition(PrintState.Paused, PrintState.Error, prefs) == "Print failed" && Events.Transition(PrintState.Printing, PrintState.Offline, prefs) == "Printer disconnected", "Required event missing"); prefs.NotifyFailed = false; Check(Events.Transition(PrintState.Printing, PrintState.Error, prefs) == null, "Disabled event delivered"); });
        Test("DPAPI persistence is encrypted and sign-out removes authentication", () => { var store = new CredentialStore(Path.Combine(output, "isolated-secrets")); var fixture = new Session("TEST-SECRET-DO-NOT-USE", "u_fixture", "fixture@example.invalid", false); store.Save(fixture); Check(!Encoding.UTF8.GetString(File.ReadAllBytes(store.FilePath)).Contains(fixture.Token), "Secret plaintext"); Check(new CredentialStore(Path.GetDirectoryName(store.FilePath)!).Load() == fixture, "Session restart failed"); store.Clear(); Check(!File.Exists(store.FilePath) && store.Load() == null, "Credential cleanup failed"); });
        Test("corrupt credentials do not crash startup", () => { var store = new CredentialStore(Path.Combine(output, "isolated-secrets")); File.WriteAllText(store.FilePath, "corrupt"); Check(store.Load() == null, "Corrupt session accepted"); store.Clear(); });
        Test("thumbnail host allowlist excludes local or lookalike hosts", () => { Check(ThumbnailCache.AllowedHost("cdn.bblmw.com") && !ThumbnailCache.AllowedHost("bblmw.com.evil.invalid") && !ThumbnailCache.AllowedHost("127.0.0.1"), "Unsafe thumbnail hostname"); });
        Test("Bambu task asset bucket allowed without opening arbitrary S3 hosts", () => { Check(ThumbnailCache.AllowedHost("or-cloud-model-prod.s3.dualstack.us-west-2.amazonaws.com") && !ThumbnailCache.AllowedHost("unrelated.s3.dualstack.us-west-2.amazonaws.com") && !ThumbnailCache.AllowedHost("or-cloud-model-prod.s3.dualstack.us-west-2.amazonaws.com.evil.invalid"), "S3 bucket validation too broad or missing"); });
        Test("opaque token uses preference uid and real discovery endpoint", () =>
        {
            var requests = new List<string>();
            using var api = new BambuApi(new FakeHandler(async (r, ct) => { requests.Add(r.RequestUri!.AbsolutePath); if (r.Method == HttpMethod.Post) { var body = await r.Content!.ReadAsStringAsync(ct); Check(body.Contains("password") && !body.Contains("code"), "Password and code mixed"); return Response("{\"accessToken\":\"opaque-fixture\"}"); } if (r.RequestUri!.AbsolutePath.EndsWith("preference")) return Response("{\"uid\":123}"); Check(r.Headers.Authorization?.Parameter == "opaque-fixture", "Authorization absent"); return Response("{\"devices\":[{\"dev_id\":\"1\"},{\"dev_id\":\"2\"},{\"dev_id\":\"3\"}]}"); }));
            var login = api.Login("fixture@example.invalid", "fixture", false, false, default).GetAwaiter().GetResult(); Check(login.Session?.Username == "u_123", "Opaque identity failed"); Check(api.Discover(login.Session!, default).GetAwaiter().GetResult().Length == 3, "Not all discovered"); Check(requests.Contains("/v1/iot-service/api/user/bind"), "Wrong discovery path");
        });
        Test("email and TFA challenge paths are recognized", () => { foreach (var kind in new[] { "verifyCode", "tfa" }) { using var api = new BambuApi(new FakeHandler((r, ct) => Task.FromResult(Response("{\"loginType\":\"" + kind + "\",\"tfaKey\":\"fixture-key\"}")))); var login = api.Login("a@b.invalid", "fixture", false, false, default).GetAwaiter().GetResult(); Check(login.Session == null && login.Challenge == (kind == "tfa" ? "tfa" : "email"), "Challenge lost"); } });
        Test("expired authentication returns reauthentication state", () => { using var api = new BambuApi(new FakeHandler((r, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))); try { api.Discover(new Session("fixture", "u_fixture", "a@b.invalid", false), default).GetAwaiter().GetResult(); throw new Exception("401 accepted"); } catch (CloudException ex) { Check(ex.Expired && !ex.Message.Contains("fixture"), "Unsafe auth failure"); } });
        Test("malformed REST roots produce recoverable cloud failures", () => { foreach (var payload in new[] { "null", "[]", "42", "\"text\"" }) { using var api = new BambuApi(new FakeHandler((r, ct) => Task.FromResult(Response(payload)))); foreach (var taskCall in new[] { false, true }) { try { var session = new Session("fixture", "u_fixture", "a@b.invalid", false); (taskCall ? api.Tasks(session, default) : api.Discover(session, default)).GetAwaiter().GetResult(); throw new Exception("Invalid shape accepted"); } catch (CloudException ex) { Check(!ex.Expired, "Bad shape marked session expired"); } } } });
        Test("network cancellation interrupts pending requests", () => { using var api = new BambuApi(new FakeHandler(async (r, ct) => { await Task.Delay(10000, ct); return Response("{}"); })); using var cts = new CancellationTokenSource(50); var sw = System.Diagnostics.Stopwatch.StartNew(); try { api.Discover(new Session("fixture", "u_fixture", "a@b.invalid", false), cts.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { Check(sw.ElapsedMilliseconds < 1500, "Cancellation too slow"); } });
        using var model = new MonitorViewModel(Path.Combine(output, "isolated-model"), Dispatcher.CurrentDispatcher);
        Test("multi-printer independent updates, idle/offline visibility and hidden settings", () => { Seed(model, 3); model.ApplyReport("2", J("{\"mc_percent\":51}")); Check(model.Printers[0].Progress == 89 && model.Printers[1].Progress == 51 && model.Printers[2].Progress == 72, "Updates leak across printers"); model.Preferences.HiddenPrinters.Add("2"); model.SavePreferences(); Check(model.VisiblePrinters.Count == 2 && model.Printers.Count == 3, "Hidden printer removed incorrectly"); model.Preferences.HiddenPrinters.Clear(); model.SavePreferences(); Check(model.VisiblePrinters.Count == 3, "Show did not restore"); });
        Test("sign-out invalidates in-flight login before persistence", () => { var epoch = model.Generation; model.SignOut(); model.Accept(new Session("fixture", "u_fixture", "a@b.invalid", false), epoch); Check(model.Session == null && model.VisiblePrinters.Count == 0 && !File.Exists(Path.Combine(output, "isolated-model", "session.bin")), "Stale login resurrected session"); });
        Test("expired completed monitor permits Refresh, SignOut and Dispose", () =>
        {
            using var expired = new MonitorViewModel(Path.Combine(output, "expired-model"), Dispatcher.CurrentDispatcher, () => new BambuApi(new FakeHandler((r, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))));
            expired.Accept(new Session("fixture", "u_fixture", "a@b.invalid", false), expired.Generation); Pump(200);
            Check(expired.ConnectionText.Contains("Sign in again"), "Expiration not surfaced"); expired.Refresh(); Pump(100); expired.SignOut(); expired.SignOut();
            Check(expired.Session == null && !File.Exists(Path.Combine(output, "expired-model", "session.bin")), "Sign-out after expired run failed");
        });
        Test("UI dispatcher remains responsive while network stalls; sign-out cancels it", () =>
        {
            using var stalled = new MonitorViewModel(Path.Combine(output, "stalled-model"), Dispatcher.CurrentDispatcher, () => new BambuApi(new FakeHandler(async (r, ct) => { await Task.Delay(30000, ct); return Response("{}"); })));
            stalled.Accept(new Session("fixture", "u_fixture", "a@b.invalid", false), stalled.Generation);
            int ticks = 0; var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Normal, (_, _) => ticks++, Dispatcher.CurrentDispatcher);
            Pump(300); timer.Stop(); Check(ticks >= 8, "Network stalled dispatcher"); stalled.SignOut(); Check(stalled.Session == null, "Sign-out failed during network stall");
        });
        Test("network failure automatically retries with bounded backoff", () =>
        {
            int requests = 0; using var api = new BambuApi(new FakeHandler((r, ct) => { Interlocked.Increment(ref requests); throw new HttpRequestException("fixture outage"); }));
            var cloud = new CloudMonitor(api); using var cancel = new CancellationTokenSource(); var task = cloud.Run(new Session("fixture", "u_fixture", "a@b.invalid", false), cancel.Token);
            Pump(5400); cancel.Cancel(); task.GetAwaiter().GetResult(); Check(requests == 2, "Expected one retry within bounded initial backoff");
        });
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2d }) Test($"WPF layout/render at {scale * 100:0}% target density", () => { Seed(model, 3); Render(model, 535, scale, "three-printers"); });
        Test("one printer render", () => { Seed(model, 1); Render(model, 300, 1, "one-printer"); });
        Test("many printers scroll beneath a fixed header", () => { Seed(model, 12); var view = Render(model, 600, 1, "many-printers"); Check(((ScrollViewer)view.FindName("PrinterScroll")).ScrollableHeight > 500 && ((ItemsControl)view.FindName("PrinterItems")).Items.Count == 12, "Overflow does not scroll"); });
        Test("idle, offline, error and long names render", () => { Seed(model, 5); model.Printers[0].Name = new string('N', 150); model.ApplyReport("1", J("{\"subtask_name\":\"A very long printer job name that needs ellipsis and must not overlap any other card fields\"}")); model.ApplyReport("2", J("{\"gcode_state\":\"IDLE\"}")); model.Printers[2].SetOnline(false); model.ApplyReport("4", J("{\"gcode_state\":\"FAILED\"}")); Render(model, 700, 1, "edge-states"); });
        results.Add($"TOTAL {passed} passed, {failed} failed");
        results.Add("These are isolated fixture and WPF rendering tests. Live Bambu, real DPI switching, multi-monitor, tray clicks and notification delivery require separate runtime evidence.");
        File.WriteAllLines(Path.Combine(output, "test-results.txt"), results); foreach (var result in results) Console.WriteLine(result);
        return failed == 0 ? 0 : 1;
    }
    static void Seed(MonitorViewModel model, int count)
    {
        model.ApplyDevices(Enumerable.Range(1, count).Select(i => J(JsonSerializer.Serialize(new { dev_id = i.ToString(), name = i == 1 ? "Bambu Lab A1" : i == 2 ? "Bambu Lab P1S" : i == 3 ? "Bambu Lab X1 Carbon" : $"Workshop {i}", online = true }))).ToArray());
        for (int i = 1; i <= count; i++) model.ApplyReport(i.ToString(), J(JsonSerializer.Serialize(new { subtask_id = i.ToString(), subtask_name = i == 1 ? "NAS_PerfectVersion_Outer Casing" : i == 2 ? "Tool Drawer Insert" : "Camera Mount Bracket", gcode_state = i == 3 ? "PAUSE" : "RUNNING", mc_percent = i == 1 ? 89 : i == 2 ? 46 : 72, layer_num = i == 1 ? 1189 : i == 2 ? 402 : 612, total_layer_num = i == 1 ? 1399 : i == 2 ? 876 : 845, mc_remaining_time = i == 1 ? 84 : i == 2 ? 185 : 58 })));
    }
    static FlyoutWindow Render(MonitorViewModel model, double height, double scale, string name, bool live = false)
    {
        var view = new FlyoutWindow { DataContext = live ? model : new { model.VisiblePrinters, ConnectionText = "Test fixtures · Layout validation" }, Width = 510, Height = height };
        var root = (FrameworkElement)view.Content; root.Width = 510; root.Height = height;
        root.Measure(new System.Windows.Size(510, height)); root.Arrange(new Rect(0, 0, 510, height)); root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Check(root.ActualWidth == 510 && ((ItemsControl)view.FindName("PrinterItems")).Items.Count == model.VisiblePrinters.Count, "Binding/layout incorrect");
        var image = new RenderTargetBitmap((int)(510 * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); image.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(output, $"{name}-{scale * 100:0}.png")); png.Save(file);
        return view;
    }
    static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    static int LiveReadOnly(string? destination)
    {
        output = Path.GetFullPath(destination ?? "live-evidence"); Directory.CreateDirectory(output);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PrintPulse;component/Theme.xaml") });
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintPulse");
        using var model = new MonitorViewModel(directory, Dispatcher.CurrentDispatcher);
        // Explicit diagnostic invocation only. No credentials, account email, serial IDs or tokens are emitted.
        model.Resume();
        if (model.Session == null) { Console.WriteLine("LIVE CHECK: no saved session is available. Sign in through the app."); return 2; }
        var reportTimes = new HashSet<DateTimeOffset>();
        for (int i = 0; i < 15; i++) { Pump(5000); foreach (var printer in model.Printers) if (printer.LastReport is {} time) reportTimes.Add(time); }
        Render(model, Math.Min(800, Math.Max(300, 104 + model.VisiblePrinters.Count * 143)), 1.5, "live-printers", true);
        var lines = new List<string> { "PrintPulse explicit read-only live validation", "Captured " + DateTimeOffset.Now.ToString("O"), "Connection: " + model.ConnectionText, "Discovered printers: " + model.Printers.Count, "Distinct observed report timestamps: " + reportTimes.Count };
        foreach (var printer in model.Printers)
        { lines.Add($"Printer: {printer.Name}; model: {printer.Model}; state: {printer.StateLabel}; progress: {printer.Percentage}; {printer.LayerLabel}; {printer.TimeLabel}; {printer.EtaLabel}; report received: {printer.LastReport != null}; thumbnail: {printer.Thumbnail != null}"); }
        try
        {
            using var api = new BambuApi(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var tasks = api.Tasks(model.Session, timeout.Token).GetAwaiter().GetResult();
            foreach (var printer in model.Printers)
            {
                var matches = tasks.Where(t => Printer.Text(t, "deviceId") == printer.Id && (Printer.Text(t, "id") == printer.TaskId || Printer.Text(t, "taskId") == printer.TaskId)).ToArray();
                lines.Add($"Thumbnail availability for {printer.Name}: matching current cloud tasks={matches.Length}; cover field supplied={matches.Any(t => !string.IsNullOrWhiteSpace(Printer.Text(t, "cover")))}");
            }
        }
        catch (Exception e) when (e is CloudException or HttpRequestException or OperationCanceledException) { lines.Add("Thumbnail metadata availability could not be verified in this run."); }
        lines.Add("Live data accessed using the saved session; no printer-control actions. Raster evidence is the production view rendered with actual data, not a physical tray screenshot.");
        File.WriteAllLines(Path.Combine(output, "live-results.txt"), lines); foreach (var line in lines) Console.WriteLine(line);
        return model.Connected && model.Printers.Count > 0 && model.Printers.Any(p => p.LastReport != null && p.State != PrintState.Unknown) ? 0 : 2;
    }
    static void Pump(int milliseconds) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
}


