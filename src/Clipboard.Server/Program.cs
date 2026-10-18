using System.Threading.RateLimiting;
using Clipboard.Server;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var maxUpload = builder.Configuration.GetValue("MaxUploadBytes", 1_073_741_824L);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = maxUpload + (1L << 20);
});

builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    forwarded.ForwardLimit = 1;
});

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("create", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            }));
});

var options = new AppOptions
{
    DataDir = builder.Configuration["DataDir"] ?? "data",
    MaxUploadBytes = builder.Configuration.GetValue("MaxUploadBytes", 1_073_741_824L),
    MaxTotalBytes = builder.Configuration.GetValue("MaxTotalBytes", 10_737_418_240L),
    AbandonedAfterDays = builder.Configuration.GetValue("AbandonedAfterDays", 30)
};
if (!Path.IsPathRooted(options.DataDir))
    options.DataDir = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, options.DataDir));
if (options.MaxUploadBytes < 1 || options.MaxTotalBytes < options.MaxUploadBytes || options.AbandonedAfterDays < 1)
    throw new InvalidOperationException("Clipboard limits are invalid.");

Directory.CreateDirectory(options.DataDir);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => new Database(Path.Combine(options.DataDir, "clipboard.db")));
builder.Services.AddSingleton(_ => new EncryptedBlobStore(options.DataDir));
builder.Services.AddHostedService<AbandonedCleanupService>();

var app = builder.Build();
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Request failed ({Type}).", ex.GetType().Name);
        if (!context.Response.HasStarted)
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    }
});
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Content-Security-Policy"] = "default-src 'self'; base-uri 'none'; frame-ancestors 'none'";
        headers.CacheControl = "no-store";
        return Task.CompletedTask;
    });
    await next();
});
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
ClipboardEndpoints.Map(app);
app.Run();
