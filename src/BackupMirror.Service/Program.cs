using System.Text.Json;
using System.Text.Json.Serialization;
using BackupMirror.Core.Security;
using BackupMirror.Core.Storage;
using BackupMirror.Core.Time;
using BackupMirror.Service.Logging;
using BackupMirror.Service.Runtime;
using BackupMirror.Service.Web;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;

var asWindowsService = OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService();
var asSystemd = OperatingSystem.IsLinux() && SystemdHelpers.IsSystemdService();
var dataArg = Array.IndexOf(args, "--data");
var dataRoot = dataArg >= 0 && dataArg + 1 < args.Length
    ? Path.GetFullPath(args[dataArg + 1])
    : DataPaths.ResolveDataRoot(preferProgramDataForService: asWindowsService || asSystemd);
DataPaths.EnsureLayout(dataRoot);
SecretProtector.Initialize(dataRoot);

if (args.Contains("--access-key"))
{
    // Prints (creating if needed) the key for signing in from another machine.
    var keyPath = Path.Combine(dataRoot, "access.key");
    _ = new WebSecurity(Microsoft.Extensions.Options.Options.Create(new ServiceOptions { DataRoot = dataRoot }));
    Console.WriteLine(File.ReadAllText(keyPath).Trim());
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Machine-specific overrides survive upgrades: <data>/settings.json (e.g. "BackupMirror": { "Urls": "http://0.0.0.0:8737" }).
builder.Configuration.AddJsonFile(Path.Combine(dataRoot, "settings.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("BACKUPMIRROR_");

builder.Host.UseWindowsService(o => o.ServiceName = "BackupMirror");
builder.Host.UseSystemd();

var section = builder.Configuration.GetSection("BackupMirror");
builder.Services.Configure<ServiceOptions>(section);
builder.Services.PostConfigure<ServiceOptions>(o => o.DataRoot = dataRoot);
var urls = section["Urls"] ?? "http://127.0.0.1:8737";
builder.WebHost.UseUrls(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

var fileLog = new RollingFileLoggerProvider(Path.Combine(dataRoot, "logs", "service.log"));
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddProvider(fileLog);
#pragma warning disable CA1416 // guarded by OperatingSystem.IsWindows()
if (OperatingSystem.IsWindows() && asWindowsService) builder.Logging.AddEventLog(s => s.SourceName = "BackupMirror");
#pragma warning restore CA1416

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
};
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

builder.Services.AddSingleton(fileLog);
builder.Services.AddSingleton(new JsonJobStore(dataRoot));
builder.Services.AddSingleton(new RuntimeStore(dataRoot));
builder.Services.AddSingleton(new EventHub(json));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<JobManager>();
builder.Services.AddSingleton<ScheduleWatcher>();
builder.Services.AddSingleton<RealtimeWatchManager>();
builder.Services.AddSingleton<WebSecurity>();
builder.Services.AddHostedService<MirrorHostedService>();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

await app.Services.GetRequiredService<JobManager>().InitializeAsync(CancellationToken.None);

var security = app.Services.GetRequiredService<WebSecurity>();
app.Use((ctx, next) => security.InvokeAsync(ctx, _ => next()));
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache"
});
ApiEndpoints.Map(app);

app.Lifetime.ApplicationStarted.Register(() =>
{
    // Lets the Windows tray app (and scripts) find the dashboard.
    try
    {
        var first = urls.Split(';')[0].Trim().Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "127.0.0.1").Replace("*", "127.0.0.1").Replace("+", "127.0.0.1");
        File.WriteAllText(Path.Combine(dataRoot, "endpoint.json"), JsonSerializer.Serialize(new { url = first, pid = Environment.ProcessId }));
    }
    catch (Exception ex)
    {
        log.LogWarning("Could not write endpoint.json: {Message}", ex.Message);
    }

    log.LogInformation("Backup Mirror {Version} on {Os}. Dashboard: {Urls}  Data: {Root}",
        ApiEndpoints.Version, Environment.OSVersion, urls, dataRoot);
    if (security.RemoteEnabled)
        log.LogInformation("Remote access is on. Sign in from other machines with the key in {Path}",
            Path.Combine(dataRoot, "access.key"));
});

try
{
    await app.RunAsync();
}
catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
{
    log.LogCritical("Cannot listen on {Urls}: {Message}. Is another copy running? Change BackupMirror:Urls in {Settings}.",
        urls, ex.Message, Path.Combine(dataRoot, "settings.json"));
    Environment.ExitCode = 2;
}
