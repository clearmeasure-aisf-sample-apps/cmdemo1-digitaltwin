using System.Text.Json.Serialization;
using DigitalTwin.Api.Auth;
using DigitalTwin.Api.Endpoints;
using DigitalTwin.Api.Mcp;
using DigitalTwin.Api.Realtime;
using DigitalTwin.Application.Abstractions;
using DigitalTwin.Infrastructure;
using DigitalTwin.Infrastructure.Options;
using DigitalTwin.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSignalR()
    .AddJsonProtocol(opts =>
    {
        opts.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddSingleton<RealtimeEventPublisher>();
builder.Services.AddSingleton<IRealtimeEventPublisher>(sp => sp.GetRequiredService<RealtimeEventPublisher>());

builder.Services.AddDigitalTwinInfrastructure(builder.Configuration);

builder.Services.AddMcpServer(opts =>
{
    opts.ServerInfo = new()
    {
        Name = "cmdemo1-digitaltwin",
        Version = "1.0.0"
    };
})
.WithHttpTransport(httpOpts => httpOpts.Stateless = true)
.WithTools<TwinMcpTools>();

var app = builder.Build();

// Ensure persistence database/tables exist across restarts (with startup retry for newly provisioned Azure SQL)
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TwinDbContext>>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    for (var attempt = 1; attempt <= 6; attempt++)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            break;
        }
        catch (Exception ex) when (attempt < 6)
        {
            startupLogger.LogDatabaseRetry(attempt, ex);
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Protect /mcp with X-Twin-Key
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
    {
        var opts = context.RequestServices.GetRequiredService<IOptions<TwinOptions>>().Value;
        if (!TwinKeyAuth.IsAuthorized(context, opts, required: true))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized: valid X-Twin-Key header required for MCP." });
            return;
        }
    }
    await next();
});

app.MapHub<TwinHub>("/hubs/twin");
app.MapWebhookEndpoints();
app.MapTwinEndpoints();
app.MapMcp("/mcp");

app.Run();

public partial class Program;

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Database EnsureCreated attempt {Attempt} failed; retrying in 5s")]
    public static partial void LogDatabaseRetry(this ILogger logger, int attempt, Exception ex);
}
