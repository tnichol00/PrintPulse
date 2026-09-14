using System.ComponentModel;
using System.Text.Json;

namespace PrintPulse.Core;

public enum PrintState { Printing, Paused, Preparing, Idle, Completed, Error, Offline, Unknown }
public sealed class Printer : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "Printer";
    public string Model { get; set; } = "Bambu Lab";
    public string Job { get; private set; } = "";
    public string TaskId { get; private set; } = "";
    public int? Progress { get; private set; }
    public int? Layer { get; private set; }
    public int? TotalLayers { get; private set; }
    public int? RemainingMinutes { get; private set; }
    public DateTimeOffset? EstimatedFinish { get; private set; }
    public DateTimeOffset? LastReport { get; private set; }
    public PrintState State { get; private set; } = PrintState.Unknown;
    private PrintState lastOnline = PrintState.Unknown;
    public string? Thumbnail { get; private set; }
    public bool Active => State is PrintState.Printing or PrintState.Paused or PrintState.Preparing;
    public string StateLabel => State == PrintState.Error ? "Failed / Error" : State.ToString();
    public string JobLabel => string.IsNullOrWhiteSpace(Job) ? State == PrintState.Idle ? "Ready for your next print" : "No job information" : Job;
    public string Percentage => State == PrintState.Idle ? "—" : Progress is {} p ? $"{p}%" : "—";
    public int BarValue => State == PrintState.Idle ? 0 : Progress ?? 0;
    public string LayerLabel => Layer is {} l && TotalLayers is > 0 ? $"Layer {l} / {TotalLayers}" : "Layer —";
    public string TimeLabel => RemainingMinutes is {} m && Active ? m >= 60 ? $"{m / 60}h {m % 60:00}m left" : $"{m}m left" : "Time —";
    public string EtaLabel => EstimatedFinish is {} t && State == PrintState.Printing ? $"ETA {t.LocalDateTime:t}" : "ETA —";
    public string Accent => State switch { PrintState.Error => "#C42B1C", PrintState.Paused => "#9D5D00", PrintState.Offline or PrintState.Idle or PrintState.Unknown => "#6B7280", _ => "#00A82D" };
    public string Freshness => LastReport is {} t ? $"Last update {t.LocalDateTime:G}" : "Waiting for printer report";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void SetThumbnail(string? path) { Thumbnail = path; Changed(); }
    public void SetOnline(bool online)
    {
        if (!online && State != PrintState.Offline) { lastOnline = State; State = PrintState.Offline; }
        else if (online && State == PrintState.Offline) State = lastOnline;
        Changed();
    }
    public void Apply(JsonElement p, DateTimeOffset now)
    {
        if (p.ValueKind != JsonValueKind.Object) return;
        var task = Text(p, "subtask_id");
        var newTask = !string.IsNullOrEmpty(task) && task != "0" && task != TaskId;
        var job = Text(p, "subtask_name");
        if (string.IsNullOrWhiteSpace(job)) job = newTask || string.IsNullOrEmpty(Job) ? Text(p, "gcode_file") : null;
        if (newTask || (string.IsNullOrEmpty(TaskId) && !string.IsNullOrEmpty(Job) && !string.IsNullOrEmpty(job) && job != Job))
        {
            Progress = Layer = TotalLayers = RemainingMinutes = null; EstimatedFinish = null; Thumbnail = null;
            if (newTask) Job = "";
        }
        if (task != null) TaskId = task;
        if (job != null) Job = job;
        if (Number(p, "mc_percent") is {} progress) Progress = Math.Clamp(progress, 0, 100);
        if (Number(p, "layer_num") is {} layer) Layer = Math.Max(0, layer);
        if (Number(p, "total_layer_num") is {} total) TotalLayers = Math.Max(0, total);
        if (Number(p, "mc_remaining_time") is {} minutes)
        {
            RemainingMinutes = Math.Clamp(minutes, 0, 525600);
            EstimatedFinish = now.AddMinutes(RemainingMinutes.Value);
        }
        if (Text(p, "gcode_state") is {} state) State = ParseState(state);
        else if (State == PrintState.Offline) State = lastOnline;
        if (State == PrintState.Completed) { Progress = 100; RemainingMinutes = 0; }
        if (State == PrintState.Idle) { Progress = Layer = TotalLayers = RemainingMinutes = null; EstimatedFinish = null; Job = ""; Thumbnail = null; TaskId = ""; }
        lastOnline = State;
        LastReport = now;
        Changed();
    }
    public static PrintState ParseState(string s) => s.ToUpperInvariant() switch
    {
        "RUNNING" or "PRINTING" => PrintState.Printing, "PAUSE" or "PAUSED" => PrintState.Paused,
        "PREPARE" or "PREPARING" or "SLICING" or "INIT" => PrintState.Preparing,
        "FINISH" or "SUCCESS" or "COMPLETED" => PrintState.Completed, "FAILED" or "ERROR" => PrintState.Error,
        "IDLE" => PrintState.Idle, "OFFLINE" => PrintState.Offline, _ => PrintState.Unknown
    };
    public static string? Text(JsonElement p, string key) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
    public static int? Number(JsonElement p, string key) => int.TryParse(Text(p, key), out var v) ? v : null;
}

public sealed record Session(string Token, string Username, string Email, bool China);
public sealed class Preferences
{
    public HashSet<string> HiddenPrinters { get; set; } = [];
    public bool NotifyStarted { get; set; } = true;
    public bool NotifyCompleted { get; set; } = true;
    public bool NotifyFailed { get; set; } = true;
    public bool NotifyDisconnected { get; set; } = true;
}
public static class Events
{
    public static string? Transition(PrintState before, PrintState after, Preferences prefs) => (before, after) switch
    {
        (PrintState.Idle or PrintState.Completed or PrintState.Error, PrintState.Printing or PrintState.Preparing) when prefs.NotifyStarted => "Print started",
        (PrintState.Printing or PrintState.Paused or PrintState.Preparing, PrintState.Completed) when prefs.NotifyCompleted => "Print completed",
        (PrintState.Printing or PrintState.Paused or PrintState.Preparing, PrintState.Error) when prefs.NotifyFailed => "Print failed",
        (PrintState.Printing or PrintState.Paused or PrintState.Preparing, PrintState.Offline) when prefs.NotifyDisconnected => "Printer disconnected",
        _ => null
    };
}
