using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace PrintPulse;
public sealed class ThumbnailCache(string directory) : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    public async Task<string?> Get(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !AllowedHost(uri.Host)) return null;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".png");
        if (File.Exists(path)) return path;
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 5_000_000) return null;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(); var chunk = new byte[8192]; int n;
            while ((n = await source.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + n > 5_000_000) return null; buffer.Write(chunk, 0, n); }
            buffer.Position = 0;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 192; bitmap.StreamSource = buffer; bitmap.EndInit(); bitmap.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path + ".tmp")) encoder.Save(output);
            File.Move(path + ".tmp", path, true);
            foreach (var old in new DirectoryInfo(directory).GetFiles("*.png").OrderByDescending(x => x.LastWriteTimeUtc).Skip(200)) old.Delete();
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or NotSupportedException or System.IO.FileFormatException or ArgumentException) { return null; }
    }
    public static bool AllowedHost(string host) =>
        // Exact asset bucket observed in authenticated Bambu task metadata. Do not allow arbitrary S3 hosts.
        host.Equals("or-cloud-model-prod.s3.dualstack.us-west-2.amazonaws.com", StringComparison.OrdinalIgnoreCase) ||
        new[] { "bambulab.com", "bambulab.cn", "bblmw.com", "makerworld.com" }.Any(x => host.Equals(x, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + x, StringComparison.OrdinalIgnoreCase));
    public void Clear() { if (Directory.Exists(directory)) foreach (var path in Directory.EnumerateFiles(directory)) File.Delete(path); }
    public void Dispose() => http.Dispose();
}
