using System.Reflection;
using System.Runtime.InteropServices;
using BackupMirror.Core.Network;
using BackupMirror.Core.Security;
using BackupMirror.Service.Logging;
using BackupMirror.Service.Runtime;
using Microsoft.Extensions.Options;

namespace BackupMirror.Service.Web;

public static class ApiEndpoints
{
    public static readonly string Version =
        typeof(ApiEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "2.0.0";

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", (HttpContext ctx, JobManager jobs, WebSecurity sec, IOptions<ServiceOptions> o) =>
            new StatusView(jobs.Armed, Version, Environment.MachineName, OsName(), o.Value.DataRoot, jobs.StartedUtc,
                !WebSecurity.IsLoopback(ctx), sec.RemoteEnabled,
                jobs.Jobs.OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase).Select(jobs.BuildView).ToList()));

        api.MapPost("/login", (HttpContext ctx, LoginInput input, WebSecurity sec) =>
        {
            if (!sec.KeyMatches(input.Key ?? "")) return Results.Json(new ApiError("That access key is not right."), statusCode: 401);
            ctx.Response.Cookies.Append(WebSecurity.CookieName, input.Key!.Trim(), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = ctx.Request.IsHttps,
                MaxAge = TimeSpan.FromDays(30)
            });
            return Results.Ok();
        });

        api.MapPost("/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(WebSecurity.CookieName);
            return Results.Ok();
        });

        api.MapPost("/arm", async (ArmInput input, JobManager jobs, CancellationToken ct) =>
        {
            await jobs.SetArmedAsync(input.Armed, ct);
            return Results.Ok(new { armed = jobs.Armed });
        });

        // ---- jobs
        api.MapGet("/jobs", (JobManager jobs) => jobs.Jobs.Select(jobs.BuildView));

        api.MapGet("/jobs/{id:guid}", (Guid id, JobManager jobs) =>
            jobs.Find(id) is { } j ? Results.Ok(jobs.BuildView(j)) : NotFound());

        api.MapPost("/jobs", (JobInput input, JobManager jobs, CancellationToken ct) => Guard(async () =>
        {
            var view = await jobs.SaveJobAsync(null, input, ct);
            return Results.Created($"/api/jobs/{view.Id}", view);
        }));

        api.MapPut("/jobs/{id:guid}", (Guid id, JobInput input, JobManager jobs, CancellationToken ct) =>
            Guard(async () => Results.Ok(await jobs.SaveJobAsync(id, input, ct))));

        api.MapDelete("/jobs/{id:guid}", (Guid id, JobManager jobs, CancellationToken ct) => Guard(async () =>
        {
            await jobs.DeleteJobAsync(id, ct);
            return Results.NoContent();
        }));

        api.MapPost("/jobs/{id:guid}/enabled", (Guid id, EnableInput input, JobManager jobs, CancellationToken ct) => Guard(async () =>
        {
            await jobs.SetEnabledAsync(id, input.Enabled, ct);
            return Results.Ok(jobs.BuildView(jobs.Find(id)!));
        }));

        api.MapPost("/jobs/{id:guid}/sync", (Guid id, JobManager jobs) =>
            jobs.RequestRun(id, "manual") is { } err ? Results.Json(new ApiError(err), statusCode: 409) : Results.Accepted());

        api.MapPost("/jobs/{id:guid}/cancel", (Guid id, JobManager jobs) =>
            jobs.Cancel(id) ? Results.Accepted() : Results.Json(new ApiError("Job is not running."), statusCode: 409));

        api.MapGet("/jobs/{id:guid}/pending-deletes", (Guid id, JobManager jobs) =>
        {
            var rt = jobs.Runtime(id);
            return Results.Ok(new { count = rt.PendingDeleteCount, files = rt.PendingDeletes });
        });

        api.MapPost("/jobs/{id:guid}/confirm-deletes", (Guid id, JobManager jobs) =>
            jobs.RequestRun(id, "confirm", confirmDeletes: true) is { } err
                ? Results.Json(new ApiError(err), statusCode: 409)
                : Results.Accepted());

        api.MapPost("/jobs/{id:guid}/skip-deletes", (Guid id, JobManager jobs) =>
        {
            jobs.SkipPendingDeletes(id);
            return Results.Ok();
        });

        api.MapPost("/jobs/{id:guid}/compare", (Guid id, CompareInput? input, JobManager jobs, CancellationToken ct) =>
            Guard(async () => Results.Ok(await jobs.CompareAsync(id, input ?? new CompareInput(), ct))));

        api.MapPost("/compare/{id:guid}/apply", (Guid id, ApplyInput? input, JobManager jobs) =>
            jobs.ApplyCompare(id, input?.Skip ?? new List<int>()) is { } err
                ? Results.Json(new ApiError(err), statusCode: 409)
                : Results.Accepted());

        api.MapPost("/sync-all", (JobManager jobs) =>
        {
            var started = 0;
            foreach (var j in jobs.Jobs.Where(jobs.CanAutoRun))
                if (jobs.RequestRun(j.Id, "manual") is null) started++;
            return Results.Ok(new { started });
        });

        api.MapGet("/history", (Guid? jobId, int? limit, JobManager jobs) =>
            jobs.History(jobId, Math.Clamp(limit ?? 100, 1, 500)));

        // ---- live events
        api.MapGet("/events", async (HttpContext ctx, EventHub hub, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var (id, reader) = hub.Subscribe();
            try
            {
                await ctx.Response.WriteAsync("retry: 3000\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                while (!ct.IsCancellationRequested)
                {
                    using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        if (await reader.WaitToReadAsync(heartbeat.Token))
                        {
                            while (reader.TryRead(out var frame))
                                await ctx.Response.WriteAsync(frame, ct);
                        }
                        else break;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", ct);
                    }

                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // client went away
            }
            finally
            {
                hub.Unsubscribe(id);
            }
        });

        // ---- file system helpers for the folder picker
        api.MapGet("/fs/roots", (JobManager jobs) =>
            FileBrowser.Roots(jobs.Jobs.SelectMany(j => new[] { j.SourcePath, j.DestinationPath })));
        api.MapGet("/fs/list", (string path) => ListWithTimeout(path));

        // POST so a share password never ends up in a URL or log.
        api.MapPost("/fs/list", async (ListInput input, JobManager jobs) =>
        {
            var path = input.Path?.Trim() ?? "";
            if (NetworkLocations.ToUncIfMapped(path) is { } unc) path = unc;
            if (UncShareConnector.IsUncPath(path))
            {
                var (user, password) = ShareCredentials(input.UserName, input.Password, input.JobId, jobs);
                if (user is not null)
                {
                    var err = await Task.Run(() => UncShareConnector.EnsureConnected(path, user, password))
                        .WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                    if (err is not null) return new DirListing(path, null, Array.Empty<DirEntry>(), err);
                }
            }

            var listing = await ListWithTimeout(path).ConfigureAwait(false);
            if (listing.Error is { } e && UncShareConnector.IsUncPath(path) && e.Contains("denied", StringComparison.OrdinalIgnoreCase))
                listing = listing with
                {
                    Error = "Access denied. The service signs in as this computer, not as you. Enter the share's user name and " +
                            "password under “Network share sign-in” in the mirror settings, then browse again."
                };
            return listing;
        });

        api.MapPost("/fs/check", (PathCheckInput input, JobManager jobs) =>
        {
            var destination = string.Equals(input.Kind, "destination", StringComparison.OrdinalIgnoreCase);
            if (destination && UncShareConnector.IsUncPath(input.Path) && !string.IsNullOrWhiteSpace(input.UserName))
            {
                var password = input.Password;
                if (password is null && input.JobId is { } jid && jobs.Find(jid)?.DestinationPasswordProtected is { } saved)
                {
                    try { password = SecretProtector.Unprotect(saved); }
                    catch { /* fall through to a connect error */ }
                }

                if (UncShareConnector.EnsureConnected(input.Path, input.UserName, password) is { } err)
                    return new PathCheckResult(false, false, null, err, null, null);
            }

            if (NetworkLocations.ToUncIfMapped(input.Path) is { } mapped)
                return new PathCheckResult(false, false, null,
                    $"{input.Path.Trim()[..2]} is a mapped drive in your Windows sign-in; the service can't see drive letters. Use the network path instead.",
                    null, null) { Suggestion = mapped };

            return FileBrowser.Check(input.Path, destination);
        });

        api.MapPost("/fs/mkdir", (MkdirInput input) =>
        {
            try
            {
                var info = Directory.CreateDirectory(input.Path);
                return Results.Ok(new DirEntry(info.Name, info.FullName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.Json(new ApiError(ex.Message), statusCode: 400);
            }
        });

        // ---- diagnostics
        api.MapGet("/logs", (int? lines, RollingFileLoggerProvider log) => log.Tail(Math.Clamp(lines ?? 300, 10, 5000)));
    }

    /// <summary>A share on an offline server can block for a long time; give up after 15 s.</summary>
    private static async Task<DirListing> ListWithTimeout(string path)
    {
        try
        {
            return await Task.Run(() => FileBrowser.List(path)).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new DirListing(path, null, Array.Empty<DirEntry>(), "No response from the server (15 s). Is it switched on and reachable?");
        }
    }

    private static (string? User, string? Password) ShareCredentials(string? user, string? password, Guid? jobId, JobManager jobs)
    {
        var job = jobId is { } id ? jobs.Find(id) : null;
        user = string.IsNullOrWhiteSpace(user) ? job?.DestinationUserName : user.Trim();
        if (user is null) return (null, null);
        if (string.IsNullOrEmpty(password) && job?.DestinationPasswordProtected is { } saved)
        {
            try { password = SecretProtector.Unprotect(saved); }
            catch { /* connect will report the failure */ }
        }

        return (user, password);
    }

    private static IResult NotFound() => Results.Json(new ApiError("Job not found."), statusCode: 404);

    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Json(new ApiError(ex.Message), statusCode: 404);
        }
        catch (ArgumentException ex)
        {
            // Drop the " (Parameter 'job')" suffix .NET appends; users see this text.
            var msg = ex.ParamName is { } p ? ex.Message.Replace($" (Parameter '{p}')", "") : ex.Message;
            return Results.Json(new ApiError(msg), statusCode: 400);
        }
    }

    private static string OsName()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var pretty = File.ReadLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME="));
                if (pretty is not null) return pretty["PRETTY_NAME=".Length..].Trim('"');
            }
            catch
            {
                // fall through
            }
        }

        return RuntimeInformation.OSDescription;
    }
}
