using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackupMirror.Tray;

/// <summary>Minimal client for the Backup Mirror service API (loopback only).</summary>
internal sealed class ServiceClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public ServiceClient()
    {
        _http.DefaultRequestHeaders.Add("X-BackupMirror", "1");
    }

    public string BaseUrl { get; private set; } = DiscoverUrl();

    /// <summary>The service writes endpoint.json into its data folder when it starts.</summary>
    private static string DiscoverUrl()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("BACKUPMIRROR_DATA"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMirror"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupMirror")
        };

        foreach (var dir in candidates)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var path = Path.Combine(dir, "endpoint.json");
                if (!File.Exists(path)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } url)
                    return url.TrimEnd('/');
            }
            catch
            {
                // try the next location
            }
        }

        return "http://127.0.0.1:8737";
    }

    public async Task<Status?> GetStatusAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<Status>(BaseUrl + "/api/status", Json);
        }
        catch
        {
            BaseUrl = DiscoverUrl(); // the port may have changed after a reconfigure
            return null;
        }
    }

    public async Task<string?> PostAsync(string path, object? body = null)
    {
        try
        {
            using var res = await _http.PostAsync(BaseUrl + path, JsonContent.Create(body ?? new { }, options: Json));
            if (res.IsSuccessStatusCode) return null;
            var text = await res.Content.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("error", out var e)) return e.GetString();
            }
            catch
            {
                // not json
            }

            return $"{(int)res.StatusCode} {res.ReasonPhrase}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public void Dispose() => _http.Dispose();

    internal sealed record Status(bool Armed, string Version, string Machine, List<JobInfo> Jobs);

    internal sealed record JobInfo(Guid Id, string Name, bool Enabled, string Status, string? Message, int PendingDeleteCount);
}
