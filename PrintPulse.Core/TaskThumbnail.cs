using System.Text.Json;

namespace PrintPulse.Core;
public static class TaskThumbnail
{
    public static string? FindCover(IEnumerable<JsonElement> tasks, string printerId, string taskId)
    {
        if (string.IsNullOrWhiteSpace(printerId) || string.IsNullOrWhiteSpace(taskId) || taskId == "0") return null;
        foreach (var task in tasks)
        {
            var identity = Printer.Text(task, "id") ?? Printer.Text(task, "taskId");
            if (Printer.Text(task, "deviceId") == printerId && identity == taskId)
                return Printer.Text(task, "cover");
        }
        return null;
    }
}
