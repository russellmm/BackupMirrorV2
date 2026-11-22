using System.Net;
using System.Security.Cryptography;
using System.Text;
using BackupMirror.Service.Runtime;
using Microsoft.Extensions.Options;

namespace BackupMirror.Service.Web;

/// <summary>
/// Protects the dashboard:
///  * Host-header allow-list (blocks DNS-rebinding attacks from web pages).
///  * State-changing API calls need the X-BackupMirror header, which browsers won't send cross-site
///    without a CORS preflight we never approve (CSRF protection).
///  * Clients on another machine must sign in with the access key from the data folder.
///    Clients on this machine (loopback) are trusted.
/// </summary>
public sealed class WebSecurity
{
    public const string CsrfHeader = "X-BackupMirror";
    public const string CookieName = "bm_auth";

    private readonly HashSet<string> _allowedHosts;
    private readonly byte[] _keyHash;

    public WebSecurity(IOptions<ServiceOptions> options)
    {
        var o = options.Value;
        AccessKey = LoadOrCreateKey(Path.Combine(o.DataRoot, "access.key"));
        _keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(AccessKey));

        var machine = Environment.MachineName;
        _allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "localhost", machine, machine + ".local", machine + ".lan"
        };
        try { _allowedHosts.Add(Dns.GetHostEntry(machine).HostName); } catch { /* offline */ }
        foreach (var h in o.ExtraAllowedHosts) _allowedHosts.Add(h.Trim());
        RemoteEnabled = !o.Urls.Split(';').All(u => u.Contains("127.0.0.1") || u.Contains("localhost") || u.Contains("[::1]"));
    }

    public string AccessKey { get; }
    public bool RemoteEnabled { get; }

    public static bool IsLoopback(HttpContext ctx)
        => ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    public bool IsAuthenticated(HttpContext ctx)
    {
        if (IsLoopback(ctx)) return true;
        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value)) return false;
        return KeyMatches(value);
    }

    public bool KeyMatches(string candidate)
        => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Trim())), _keyHash);

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var host = ctx.Request.Host.Host.Trim('[', ']');
        if (!IPAddress.TryParse(host, out _) && !_allowedHosts.Contains(host))
        {
            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            await ctx.Response.WriteAsync("Unknown host name. Add it to BackupMirror:ExtraAllowedHosts in settings.json.");
            return;
        }

        var path = ctx.Request.Path.Value ?? "/";
        var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        var method = ctx.Request.Method;
        var mutating = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));

        if (isApi && mutating && !ctx.Request.Headers.ContainsKey(CsrfHeader))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new ApiError("Missing request header."));
            return;
        }

        var open = path is "/login.html" or "/login.js" or "/theme.js" or "/api/login" or "/app.css" or "/favicon.svg"
                   || path.StartsWith("/fonts/", StringComparison.OrdinalIgnoreCase);
        if (!open && !IsAuthenticated(ctx))
        {
            if (isApi)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new ApiError("Sign in required."));
            }
            else
            {
                ctx.Response.Redirect("/login.html");
            }

            return;
        }

        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'";
        await next(ctx);
    }

    private static string LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 16) return existing;
        }

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(path, options))
        using (var w = new StreamWriter(fs))
            w.WriteLine(key);
        return key;
    }
}
