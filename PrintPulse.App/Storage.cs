using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using PrintPulse.Core;

namespace PrintPulse;
public sealed class CredentialStore(string directory)
{
    public string FilePath => Path.Combine(directory, "session.bin");
    public void Save(Session session)
    {
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session);
        try { File.WriteAllBytes(FilePath + ".tmp", ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); File.Move(FilePath + ".tmp", FilePath, true); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public Session? Load()
    {
        if (!File.Exists(FilePath)) return null;
        byte[]? bytes = null;
        try { bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser); return JsonSerializer.Deserialize<Session>(bytes); }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException) { return null; }
        finally { if (bytes != null) CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Clear() { File.Delete(FilePath); File.Delete(FilePath + ".tmp"); }
}
public sealed class SettingsStore(string directory)
{
    public Preferences Load() { try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(directory, "settings.json"))) ?? new(); } catch { return new(); } }
    public void Save(Preferences preferences)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(preferences)); File.Move(path + ".tmp", path, true);
    }
}
public static class Startup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled { get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue("PrintPulse") != null; } }
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue("PrintPulse", $"\"{Environment.ProcessPath}\" --startup");
        else key.DeleteValue("PrintPulse", false);
    }
}
