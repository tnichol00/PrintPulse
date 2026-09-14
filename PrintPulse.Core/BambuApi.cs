using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PrintPulse.Core;

public sealed class CloudException(string message, bool expired = false) : Exception(message) { public bool Expired { get; } = expired; }
public sealed record LoginResult(Session? Session, string? Challenge, string? TfaKey);
public sealed class BambuApi : IDisposable
{
    private readonly HttpClient http;
    private readonly CookieContainer cookies = new();
    public BambuApi(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { CookieContainer = cookies, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PrintPulse/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
    public static string Root(bool china) => china ? "https://api.bambulab.cn" : "https://api.bambulab.com";
    private async Task<JsonElement> Request(string url, object? body, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, url);
        if (body != null) request.Content = JsonContent.Create(body);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new CloudException("Session expired. Sign in again in Settings.", true);
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new CloudException("Bambu blocked this sign-in method. Try email-code sign-in; a browser verification may be required.");
        if ((int)response.StatusCode == 429) throw new CloudException("Bambu is limiting requests. Wait a few minutes before trying again.");
        if (!response.IsSuccessStatusCode) throw new CloudException(body == null ? "Bambu Cloud is unavailable. PrintPulse will retry." : "Sign-in was rejected. Check the details or request a new code.");
        try { using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); return doc.RootElement.Clone(); }
        catch (JsonException) { throw new CloudException("Bambu returned an unexpected response. Try again later."); }
    }
    public async Task<LoginResult> Login(string email, string value, bool code, bool china, CancellationToken ct)
    {
        object body = code ? new { account = email, code = value } : new { account = email, password = value, apiError = "" };
        var result = await Request(Root(china) + "/v1/user-service/user/login", body, null, ct);
        if (Printer.Text(result, "accessToken") is { Length: > 0 } token)
            return new(await MakeSession(token, email, china, ct), null, null);
        return Printer.Text(result, "loginType") switch
        {
            "verifyCode" => new(null, "email", null),
            "tfa" => new(null, "tfa", Printer.Text(result, "tfaKey")),
            _ => throw new CloudException("Sign-in was not completed. Check your code or try email-code sign-in.")
        };
    }
    public async Task SendCode(string email, bool china, CancellationToken ct) => _ = await Request(Root(china) + "/v1/user-service/user/sendemail/code", new { email, type = "codeLogin" }, null, ct);
    public async Task<Session> VerifyTfa(string key, string code, string email, bool china, CancellationToken ct)
    {
        var root = china ? "https://bambulab.cn" : "https://bambulab.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, root + "/api/sign-in/tfa") { Content = JsonContent.Create(new { tfaKey = key, tfaCode = code }) };
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new CloudException("Bambu requires browser verification for authenticator sign-in. Try email-code sign-in.");
        if (!response.IsSuccessStatusCode) throw new CloudException("Authenticator code was rejected. Try the current code or email-code sign-in.");
        // The TFA endpoint establishes a cookie session; its body need not be JSON.
        var token = cookies.GetCookies(new Uri(root))["token"]?.Value;
        if (string.IsNullOrWhiteSpace(token)) throw new CloudException("Authenticator sign-in did not return a session. Try email-code sign-in.");
        return await MakeSession(token, email, china, ct);
    }
    private async Task<Session> MakeSession(string token, string email, bool china, CancellationToken ct)
    {
        string? username = null;
        try
        {
            var part = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(part.PadRight((part.Length + 3) / 4 * 4, '=')));
            username = Printer.Text(doc.RootElement, "username");
        } catch (Exception e) when (e is FormatException or JsonException or IndexOutOfRangeException) { }
        if (string.IsNullOrWhiteSpace(username))
        {
            var result = await Request(Root(china) + "/v1/design-user-service/my/preference", null, token, ct);
            username = Printer.Text(result, "uid") is {} uid ? "u_" + uid : null;
        }
        if (string.IsNullOrWhiteSpace(username)) throw new CloudException("Bambu did not provide an account identifier. Please sign in again.");
        return new Session(token, username, email, china);
    }
    public async Task<JsonElement[]> Discover(Session session, CancellationToken ct)
    {
        var result = await Request(Root(session.China) + "/v1/iot-service/api/user/bind", null, session.Token, ct);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array) throw new CloudException("Bambu did not return a printer list. PrintPulse will retry.");
        return devices.EnumerateArray().Select(d => d.Clone()).ToArray();
    }
    public async Task<JsonElement[]> Tasks(Session session, CancellationToken ct)
    {
        var result = await Request(Root(session.China) + "/v1/user-service/my/tasks?limit=100", null, session.Token, ct);
        if (result.ValueKind != JsonValueKind.Object) throw new CloudException("Bambu task metadata is temporarily unavailable.");
        return result.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array ? hits.EnumerateArray().Select(d => d.Clone()).ToArray() : [];
    }
    public void Dispose() => http.Dispose();
}
